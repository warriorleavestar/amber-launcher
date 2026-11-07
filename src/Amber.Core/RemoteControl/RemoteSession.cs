using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.RemoteControl;

/// <summary>一次远程控制是怎么结束的。</summary>
public enum RemoteEnd
{
    /// <summary>自己这边停的（按钮、热键、关窗口）。</summary>
    Local,
    /// <summary>对方停的。</summary>
    Peer,
    /// <summary>连接断了（掉线、被踢、房间结束）。</summary>
    Disconnected,
    /// <summary>对方发了不合规的数据。</summary>
    ProtocolError,
}

/// <summary>远程控制连接一端的公共部分：一把写锁、只触发一次的结束事件、读循环。</summary>
public abstract class RemoteEndpoint : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _ended;

    protected RemoteEndpoint(Stream stream, string peerName, string peerFingerprint)
    {
        Stream = stream;
        PeerName = peerName;
        PeerFingerprint = peerFingerprint;
    }

    protected Stream Stream { get; }

    /// <summary>对方的名字和身份指纹（握手认出来的，不是对方自己报的）。</summary>
    public string PeerName { get; }
    public string PeerFingerprint { get; }

    protected CancellationToken Lifetime => _cts.Token;

    public bool HasEnded => Volatile.Read(ref _ended) != 0;

    /// <summary>结束了（只触发一次，可能在任何线程上）。</summary>
    public event Action<RemoteEnd>? Ended;

    /// <summary>这次会话结束时一起结束（比如成员钥匙作废）。</summary>
    public void EndWith(CancellationToken token)
    {
        if (token.IsCancellationRequested) Abort(RemoteEnd.Disconnected);
        else token.Register(() => Abort(RemoteEnd.Disconnected));
    }

    protected async Task<bool> SendAsync(byte[] frame, CancellationToken ct = default)
    {
        if (HasEnded) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            await _writeLock.WaitAsync(linked.Token);
            try
            {
                await Stream.WriteAsync(frame, linked.Token);
                await Stream.FlushAsync(linked.Token);
            }
            finally
            {
                _writeLock.Release();
            }
            return true;
        }
        catch (Exception e) when (IsConnectionError(e))
        {
            Abort(RemoteEnd.Disconnected);
            return false;
        }
    }

    /// <summary>测试用：绕过检查直接发一帧（验证对方会丢弃不该收的输入）。</summary>
    internal Task<bool> SendRawForTest(byte[] frame) => SendAsync(frame);

    /// <summary>发“结束”再关。</summary>
    public async Task StopAsync(RemoteStopReason reason = RemoteStopReason.Normal)
    {
        if (HasEnded) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await SendAsync(RemoteWire.Stop(reason), timeout.Token);
        Abort(RemoteEnd.Local);
    }

    /// <summary>立即断开（不通知对方）。</summary>
    public void Abort(RemoteEnd end)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        OnEnding();
        if (Stream is SecureStream secure) secure.Abort();
        else Stream.Dispose();
        Ended?.Invoke(end);
    }

    /// <summary>结束前的收尾（被控方松开按住的键）。</summary>
    protected virtual void OnEnding() { }

    /// <summary>读循环：逐帧交给 Handle，返回 false 表示协议错误。</summary>
    protected async Task ReadLoopAsync()
    {
        try
        {
            while (!HasEnded)
            {
                var frame = await RemoteWire.ReadAsync(Stream, Lifetime);
                if (frame is not { } f)
                {
                    Abort(RemoteEnd.Disconnected);
                    return;
                }
                if (f.Type == RemoteFrameType.Stop)
                {
                    Abort(RemoteEnd.Peer);
                    return;
                }
                if (!await HandleAsync(f))
                {
                    Abort(RemoteEnd.ProtocolError);
                    return;
                }
            }
        }
        catch (InvalidDataException)
        {
            Abort(RemoteEnd.ProtocolError);
        }
        catch (Exception e) when (IsConnectionError(e))
        {
            Abort(RemoteEnd.Disconnected);
        }
    }

    protected abstract Task<bool> HandleAsync(RemoteFrame frame);

    internal static bool IsConnectionError(Exception e) =>
        e is IOException or OperationCanceledException or ObjectDisposedException or SocketException or CryptographicException or InvalidOperationException;

    public virtual async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 被控方：同意之后才建。先读控制方的 Hello，再发 Accept（是否允许控制、画面尺寸），然后按节奏截屏、只发变化的块。
/// 输入包只在“允许控制”时注入；只看、收回控制之后、已经结束时收到的一律丢弃（控制方那边本来也不发，这里是第二道保险）。
/// 收回控制和结束时松开所有按住的键和鼠标键。
/// </summary>
public sealed class RemoteSharer : RemoteEndpoint
{
    private readonly IScreenSource _screen;
    private readonly IInputSink? _input;
    private readonly ITileCodec _codec;
    private readonly HashSet<(ushort Scan, bool Extended)> _keysDown = [];
    private readonly HashSet<RemoteMouseButton> _buttonsDown = [];
    private readonly Lock _inputGate = new();
    private readonly SemaphoreSlim _ackSignal = new(0);
    private volatile bool _controlAllowed;
    private long _sent;
    private long _acked;
    private long _rttTicks;
    private (ScreenBounds Bounds, int Width, int Height) _geometry;
    private long _injected;
    private long _dropped;
    private long _tilesSent;
    private (int X, int Y) _lastMouse;

    public RemoteSharer(Stream stream, IScreenSource screen, IInputSink? input, ITileCodec codec, bool allowControl, string peerName, string peerFingerprint)
        : base(stream, peerName, peerFingerprint)
    {
        _screen = screen;
        _input = input;
        _codec = codec;
        _controlAllowed = allowControl && input is not null;
    }

    /// <summary>现在允许对方控制。</summary>
    public bool ControlAllowed => _controlAllowed;

    /// <summary>能不能（重新）允许控制：同意时选了“只看”就没有注入，不能改成允许。</summary>
    public bool CanGrantControl => _input is not null && !HasEnded;

    /// <summary>注入了多少个输入包、丢弃了多少个（测试和诊断用）。</summary>
    public long InputsInjected => Interlocked.Read(ref _injected);
    public long InputsDropped => Interlocked.Read(ref _dropped);
    public long TilesSent => Interlocked.Read(ref _tilesSent);

    /// <summary>最近一次测到的往返时间。</summary>
    public TimeSpan RoundTrip => TimeSpan.FromTicks(Interlocked.Read(ref _rttTicks));

    /// <summary>基础截屏间隔（约 10 帧/秒）。往返慢时自动放慢。</summary>
    internal TimeSpan BaseInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>最多这么多批画面没确认就先不发（慢网络自然降帧率）。</summary>
    internal int MaxInFlight { get; init; } = 2;

    internal static TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>控制方的 Hello 已经被读过了（房主这边先读 Hello 再弹同意窗）。</summary>
    public bool HelloAlreadyRead { get; init; }

    /// <summary>读控制方的 Hello（版本对得上返回 true）。</summary>
    public static async Task<bool> ReadHelloAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        var first = await RemoteWire.ReadAsync(stream, timeout.Token);
        return first is { Type: RemoteFrameType.Hello } f && f.Payload is [RemoteWire.Version];
    }

    public event Action<bool>? ControlChanged;

    /// <summary>跑整个会话（直到结束）。</summary>
    public async Task RunAsync()
    {
        try
        {
            if (!HelloAlreadyRead && !await ReadHelloAsync(Stream, Lifetime))
            {
                Abort(RemoteEnd.ProtocolError);
                return;
            }
            var raw = new ScreenFrame();
            if (!_screen.TryCapture(raw))
            {
                await SendAsync(RemoteWire.Deny(RemoteDenyReason.Unsupported));
                Abort(RemoteEnd.Local);
                return;
            }
            var scale = RemoteGeometry.ScaleFor(raw.Width, raw.Height);
            var frame = scale == 1 ? raw : new ScreenFrame();
            if (scale != 1) RemoteGeometry.Downscale(raw, frame, scale);
            _geometry = (_screen.Bounds, frame.Width, frame.Height);
            if (!await SendAsync(RemoteWire.Accept(new RemoteAccept(_controlAllowed, frame.Width, frame.Height, _codec.Id)))) return;
            var reader = ReadLoopAsync();
            var pinger = PingLoopAsync();
            await CaptureLoopAsync(raw, frame);
            await reader;
            await pinger;
        }
        catch (InvalidDataException)
        {
            Abort(RemoteEnd.ProtocolError);
        }
        catch (Exception e) when (IsConnectionError(e))
        {
            Abort(RemoteEnd.Disconnected);
        }
    }

    /// <summary>允许 / 收回控制（被控方自己点）。收回时松开所有按住的键。只看的会话不能改成允许（没有输入通道）。</summary>
    public async Task SetControlAsync(bool allow)
    {
        if (allow && _input is null) return;
        if (_controlAllowed == allow) return;
        _controlAllowed = allow;
        if (!allow) ReleaseAll();
        await SendAsync(allow ? RemoteWire.Encode(RemoteFrameType.GrantControl, []) : RemoteWire.Encode(RemoteFrameType.RevokeControl, []));
        ControlChanged?.Invoke(allow);
    }

    protected override void OnEnding()
    {
        _controlAllowed = false;
        ReleaseAll();
        try { _ackSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private void ReleaseAll()
    {
        if (_input is null) return;
        lock (_inputGate)
        {
            foreach (var (scan, ext) in _keysDown) Safe(() => _input.Key(scan, true, ext));
            _keysDown.Clear();
            var (x, y) = _lastMouse;
            foreach (var b in _buttonsDown) Safe(() => _input.Mouse(RemoteMouseAction.Up, b, x, y, 0));
            _buttonsDown.Clear();
        }
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private (TimeSpan Interval, int Quality) Pace()
    {
        var rtt = RoundTrip;
        return rtt < TimeSpan.FromMilliseconds(150) ? (BaseInterval, 70)
            : rtt < TimeSpan.FromMilliseconds(400) ? (BaseInterval * 2, 55)
            : (BaseInterval * 4, 40);
    }

    private async Task CaptureLoopAsync(ScreenFrame raw, ScreenFrame frame)
    {
        var differ = new TileDiffer();
        (int X, int Y)? lastCursor = null;
        var first = true;
        var clock = Stopwatch.StartNew();
        while (!HasEnded)
        {
            var (interval, quality) = Pace();
            if (!first)
            {
                var wait = interval - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, Lifetime);
                // 等对方确认，在途的批数不超过上限。
                while (!HasEnded && Interlocked.Read(ref _sent) - Interlocked.Read(ref _acked) >= MaxInFlight)
                    await _ackSignal.WaitAsync(TimeSpan.FromSeconds(1), Lifetime);
                if (HasEnded) return;
                clock.Restart();
                if (!await Task.Run(() => _screen.TryCapture(raw), Lifetime))
                {
                    await Task.Delay(500, Lifetime);
                    continue;
                }
                var scale = RemoteGeometry.ScaleFor(raw.Width, raw.Height);
                if (scale == 1 && !ReferenceEquals(frame, raw)) frame.CopyFrom(raw);
                else if (scale != 1) RemoteGeometry.Downscale(raw, frame, scale);
                _geometry = (_screen.Bounds, frame.Width, frame.Height);
            }
            first = false;
            var (bounds, fw, fh) = _geometry;
            var target = ReferenceEquals(frame, raw) ? raw : frame;
            if (_screen.CursorPosition() is { } c)
            {
                var pos = RemoteGeometry.DesktopToFrame(bounds, fw, fh, c.X, c.Y);
                if (pos != lastCursor)
                {
                    lastCursor = pos;
                    await SendAsync(RemoteWire.Cursor(pos.X, pos.Y, true));
                }
            }
            var changed = differ.Diff(target);
            if (changed.Count == 0) continue;
            await SendTilesAsync(target, changed, quality);
        }
    }

    /// <summary>编码变了的块，凑成不超过约 1 MiB 的批发出去。</summary>
    private async Task SendTilesAsync(ScreenFrame frame, List<(int X, int Y, int Width, int Height)> changed, int quality)
    {
        var batch = new List<RemoteTile>();
        var size = 0;
        foreach (var (x, y, w, h) in changed)
        {
            var data = _codec.Encode(TileCopy.Extract(frame, x, y, w, h), w, h, w * 4, quality);
            if (data.Length > RemoteWire.MaxTileData) data = new RawTileCodec().Encode(TileCopy.Extract(frame, x, y, w, h), w, h, w * 4, 0);
            batch.Add(new RemoteTile(x, y, w, h, data));
            size += data.Length + 12;
            if (size >= 1024 * 1024 || batch.Count == ushort.MaxValue)
            {
                if (!await FlushBatchAsync(frame, batch)) return;
                batch = [];
                size = 0;
            }
        }
        if (batch.Count > 0) await FlushBatchAsync(frame, batch);
    }

    private async Task<bool> FlushBatchAsync(ScreenFrame frame, List<RemoteTile> tiles)
    {
        var seq = (uint)Interlocked.Increment(ref _sent);
        Interlocked.Add(ref _tilesSent, tiles.Count);
        return await SendAsync(RemoteWire.Tiles(new RemoteTileBatch(seq, frame.Width, frame.Height, tiles)));
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!HasEnded)
            {
                await SendAsync(RemoteWire.Ping(Stopwatch.GetTimestamp()));
                await Task.Delay(TimeSpan.FromSeconds(1), Lifetime);
            }
        }
        catch (OperationCanceledException) { }
    }

    protected override Task<bool> HandleAsync(RemoteFrame frame)
    {
        switch (frame.Type)
        {
            case RemoteFrameType.Pong:
                var elapsed = Stopwatch.GetElapsedTime(RemoteWire.ParseStamp(frame.Payload));
                if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(1)) Interlocked.Exchange(ref _rttTicks, elapsed.Ticks);
                return Task.FromResult(true);
            case RemoteFrameType.Ping:
                return SendAsync(RemoteWire.Pong(frame.Payload));
            case RemoteFrameType.FrameAck:
                var seq = RemoteWire.ParseFrameAck(frame.Payload);
                if (seq > Interlocked.Read(ref _sent)) return Task.FromResult(false);
                if (seq > Interlocked.Read(ref _acked)) Interlocked.Exchange(ref _acked, seq);
                try { _ackSignal.Release(); } catch (SemaphoreFullException) { }
                return Task.FromResult(true);
            case RemoteFrameType.MouseInput:
                OnMouse(RemoteWire.ParseMouse(frame.Payload));
                return Task.FromResult(true);
            case RemoteFrameType.KeyInput:
                OnKey(RemoteWire.ParseKey(frame.Payload));
                return Task.FromResult(true);
            default:
                return Task.FromResult(false);
        }
    }

    private void OnMouse(RemoteMouse m)
    {
        lock (_inputGate)
        {
            if (!_controlAllowed || HasEnded || _input is null)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            var (bounds, fw, fh) = _geometry;
            var (x, y) = RemoteGeometry.FrameToDesktop(bounds, fw, fh, m.X, m.Y);
            _lastMouse = (x, y);
            if (m.Action == RemoteMouseAction.Down && m.Button != RemoteMouseButton.None) _buttonsDown.Add(m.Button);
            if (m.Action == RemoteMouseAction.Up) _buttonsDown.Remove(m.Button);
            var delta = Math.Clamp(m.Delta, -1200, 1200);
            Safe(() => _input.Mouse(m.Action, m.Button, x, y, delta));
            Interlocked.Increment(ref _injected);
        }
    }

    private void OnKey(RemoteKey k)
    {
        lock (_inputGate)
        {
            if (!_controlAllowed || HasEnded || _input is null)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            if (k.Up) _keysDown.Remove((k.ScanCode, k.Extended));
            else _keysDown.Add((k.ScanCode, k.Extended));
            Safe(() => _input.Key(k.ScanCode, k.Up, k.Extended));
            Interlocked.Increment(ref _injected);
        }
    }
}

/// <summary>
/// 控制方：发 Hello，等被控方同意（Accept）或拒绝（Deny）。同意后收画面块、解码进自己的画面缓冲，每批回确认。
/// 只有被控方允许控制时才发输入包；没允许时 SendMouse/SendKey 什么都不发。
/// </summary>
public sealed class RemoteViewer : RemoteEndpoint
{
    private readonly ITileCodec _codec;
    private volatile bool _control;

    public RemoteViewer(Stream stream, ITileCodec codec, string peerName, string peerFingerprint) : base(stream, peerName, peerFingerprint) => _codec = codec;

    /// <summary>画面缓冲（BGRA）。读写都要锁 FrameLock。</summary>
    public ScreenFrame Frame { get; } = new();
    public Lock FrameLock { get; } = new();

    /// <summary>被控方现在允许控制。</summary>
    public bool ControlGranted => _control;

    public (int X, int Y, bool Visible) Cursor { get; private set; }

    /// <summary>被控方拒绝时的原因。</summary>
    public RemoteDenyReason? DenyReason { get; private set; }

    public long BatchesReceived { get; private set; }

    /// <summary>画面更新了（在读线程上触发）。</summary>
    public event Action? FrameChanged;
    public event Action<bool>? ControlChanged;
    public event Action? CursorChanged;

    internal static TimeSpan AcceptTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>发 Hello，等对方决定。同意返回 Accept 的内容并开始收画面；拒绝或断开返回 null（DenyReason 说明原因）。</summary>
    public async Task<RemoteAccept?> StartAsync(CancellationToken ct = default)
    {
        if (!await SendAsync(RemoteWire.Hello(), ct)) return null;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
            wait.CancelAfter(AcceptTimeout);
            var first = await RemoteWire.ReadAsync(Stream, wait.Token);
            switch (first)
            {
                case { Type: RemoteFrameType.Accept } a:
                    var accept = RemoteWire.ParseAccept(a.Payload);
                    if (accept.Codec != _codec.Id)
                    {
                        await StopAsync(RemoteStopReason.ProtocolError);
                        return null;
                    }
                    lock (FrameLock) Frame.Resize(accept.Width, accept.Height);
                    _control = accept.Control;
                    _ = ReadLoopAsync();
                    return accept;
                case { Type: RemoteFrameType.Deny } d:
                    DenyReason = RemoteWire.ParseDeny(d.Payload);
                    Abort(RemoteEnd.Peer);
                    return null;
                case null:
                    Abort(RemoteEnd.Disconnected);
                    return null;
                default:
                    Abort(RemoteEnd.ProtocolError);
                    return null;
            }
        }
        catch (InvalidDataException)
        {
            Abort(RemoteEnd.ProtocolError);
            return null;
        }
        catch (Exception e) when (IsConnectionError(e))
        {
            DenyReason ??= ct.IsCancellationRequested ? null : RemoteDenyReason.TimedOut;
            Abort(RemoteEnd.Disconnected);
            return null;
        }
    }

    public Task<bool> SendMouseAsync(RemoteMouse mouse) => _control && !HasEnded ? SendAsync(RemoteWire.Mouse(mouse)) : Task.FromResult(false);

    public Task<bool> SendKeyAsync(RemoteKey key) => _control && !HasEnded ? SendAsync(RemoteWire.Key(key)) : Task.FromResult(false);

    protected override async Task<bool> HandleAsync(RemoteFrame frame)
    {
        switch (frame.Type)
        {
            case RemoteFrameType.Tiles:
                var batch = RemoteWire.ParseTiles(frame.Payload);
                lock (FrameLock)
                {
                    if (Frame.Width != batch.FrameWidth || Frame.Height != batch.FrameHeight) Frame.Resize(batch.FrameWidth, batch.FrameHeight);
                    foreach (var t in batch.Tiles)
                    {
                        var dest = Frame.Pixels.AsSpan(t.Y * Frame.Stride + t.X * 4);
                        if (!_codec.Decode(t.Data, dest, t.Width, t.Height, Frame.Stride)
                            && !new RawTileCodec().Decode(t.Data, dest, t.Width, t.Height, Frame.Stride)) return false;
                    }
                }
                BatchesReceived++;
                FrameChanged?.Invoke();
                return await SendAsync(RemoteWire.FrameAck(batch.Sequence));
            case RemoteFrameType.Cursor:
                Cursor = RemoteWire.ParseCursor(frame.Payload);
                CursorChanged?.Invoke();
                return true;
            case RemoteFrameType.Ping:
                return await SendAsync(RemoteWire.Pong(frame.Payload));
            case RemoteFrameType.Pong:
                return true;
            case RemoteFrameType.GrantControl:
                _control = true;
                ControlChanged?.Invoke(true);
                return true;
            case RemoteFrameType.RevokeControl:
                _control = false;
                ControlChanged?.Invoke(false);
                return true;
            default:
                return false;
        }
    }
}
