using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// 握手之后的记录层。每条记录：[2 字节长度][密文]，密文 = ChaCha20-Poly1305(明文)。
/// 明文：[1 字节标志][2 字节数据长度][数据][填充的零]。标志：bit0 掩护记录（丢弃）、bit1 这条之后发送方换钥。
/// 换钥用 Noise 的 Rekey()，标志在加密的明文里，别人改不了。
/// 伪装模式下长度字段再用 AES-256-CTR 密钥流异或掉，看起来和 Minecraft 开了加密后的数据流一样是一串随机字节。
/// </summary>
public sealed class SecureStream : Stream, IAuthenticatedStream
{
    public const int MaxCiphertext = ushort.MaxValue;
    public const int MaxPlaintext = MaxCiphertext - CipherState.TagLength;
    private const int HeaderLength = 3;
    public const int MaxData = MaxPlaintext - HeaderLength;

    private const byte FlagCover = 1;
    private const byte FlagRekey = 2;
    /// <summary>正常结束：发送方不会再发数据。没收到它就断开的连接算被截断（可能是攻击者掐断），按出错处理。</summary>
    private const byte FlagClose = 4;

    private readonly Stream _inner;
    private readonly CipherState _send;
    private readonly CipherState _receive;
    private readonly TransportProfile _profile;
    private readonly LengthMask? _sendMask;
    private readonly LengthMask? _receiveMask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _cover;
    private readonly Task _rekeyTimer;
    private long _lastReceiveRekeyTicks = Stopwatch.GetTimestamp();
    /// <summary>本机没在读的时间（游戏卡住、本地写不出去）：这段时间对方的换钥记录在缓冲区里等着，不算对方的错。</summary>
    private long _pausedSinceRekeyTicks;
    private long _readReturnedTicks;
    private bool _closeSent;
    private int _disposed;

    /// <summary>对方发来了加密认证过的结束记录（不是被半路掐断）。</summary>
    public bool ClosedCleanly { get; private set; }
    private byte[] _readBuffer = [];
    private int _readOffset;
    private long _sentSinceRekey;
    private long _lastRekeyTicks = Stopwatch.GetTimestamp();
    private long _lastWriteTicks = Stopwatch.GetTimestamp();

    /// <summary>统计：发出去的记录数、换钥次数、掩护记录数、填充字节数（测试和调试报告用）。</summary>
    public long RecordsSent { get; private set; }
    public long Rekeys { get; private set; }
    public long CoverRecordsSent { get; private set; }
    public long PaddingBytesSent { get; private set; }
    public long BytesOnWire { get; private set; }

    public SecureStream(Stream inner, CipherState send, CipherState receive, TransportProfile profile, LengthMask? sendMask = null, LengthMask? receiveMask = null)
    {
        _inner = inner;
        _send = send;
        _receive = receive;
        _profile = profile;
        _sendMask = sendMask;
        _receiveMask = receiveMask;
        if (profile.CoverTrafficIdle is { } idle) _cover = CoverLoopAsync(idle, _cts.Token);
        _rekeyTimer = RekeyLoopAsync(_cts.Token);
    }

    /// <summary>收方向的密钥最多用这么久：对方按时间换钥，空闲时也会发空记录换钥，超过就说明对方不守规矩或者被重放。</summary>
    private TimeSpan MaxReceiveKeyAge => _profile.RekeyEvery * 2 + (_profile.RekeyEvery < TimeSpan.FromSeconds(30) ? _profile.RekeyEvery : TimeSpan.FromSeconds(30));

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_readReturnedTicks != 0) _pausedSinceRekeyTicks += Stopwatch.GetTimestamp() - _readReturnedTicks;
        try
        {
            return await ReadCoreAsync(buffer, ct);
        }
        finally
        {
            _readReturnedTicks = Stopwatch.GetTimestamp();
        }
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (_readOffset >= _readBuffer.Length)
        {
            var record = await ReadRecordAsync(ct);
            if (record is null) return 0;
            _readBuffer = record;
            _readOffset = 0;
        }
        var n = Math.Min(buffer.Length, _readBuffer.Length - _readOffset);
        _readBuffer.AsSpan(_readOffset, n).CopyTo(buffer.Span);
        _readOffset += n;
        return n;
    }

    /// <summary>读一条记录的数据部分；连接正常结束返回 null；掩护记录跳过。认证失败抛 CryptographicException。</summary>
    private async Task<byte[]?> ReadRecordAsync(CancellationToken ct)
    {
        while (true)
        {
            if (ClosedCleanly) return null;
            var header = new byte[2];
            if (!await ReadExactAsync(_inner, header, ct)) throw new EndOfStreamException(L.T("core.mp.transport.stream.abruptdisconnect"));
            _receiveMask?.Apply(header);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (length < CipherState.TagLength + HeaderLength) throw new CryptographicException(L.T("core.mp.transport.stream.recordshort"));
            var cipher = new byte[length];
            if (!await ReadExactAsync(_inner, cipher, ct)) throw new EndOfStreamException(L.T("core.mp.transport.stream.recordtruncated"));
            var plain = _receive.DecryptWithAd([], cipher);
            var flags = plain[0];
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(1));
            if (HeaderLength + dataLength > plain.Length) throw new CryptographicException(L.T("core.mp.transport.stream.recordlength"));
            if ((flags & FlagRekey) != 0)
            {
                _receive.Rekey();
                _lastReceiveRekeyTicks = Stopwatch.GetTimestamp();
                _pausedSinceRekeyTicks = 0;
            }
            else
            {
                // 距上次换钥的时间，扣掉本机没在读的时间。
                var paused = TimeSpan.FromSeconds(_pausedSinceRekeyTicks / (double)Stopwatch.Frequency);
                if (Stopwatch.GetElapsedTime(_lastReceiveRekeyTicks) - paused > MaxReceiveKeyAge) throw new CryptographicException(L.T("core.mp.transport.stream.rekeyoverdue"));
            }
            if ((flags & FlagClose) != 0)
            {
                ClosedCleanly = true;
                return null;
            }
            if ((flags & FlagCover) != 0) continue;
            return plain.AsSpan(HeaderLength, dataLength).ToArray();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closeSent, this);
            for (var offset = 0; offset < buffer.Length || buffer.Length == 0 && offset == 0; offset += MaxData)
            {
                var chunk = buffer.Slice(offset, Math.Min(MaxData, buffer.Length - offset));
                await WriteRecordAsync(chunk.Span.ToArray(), 0, ct);
                if (buffer.Length == 0) break;
            }
            await _inner.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteRecordAsync(byte[] data, byte flags, CancellationToken ct)
    {
        var plainLength = PaddedLength(HeaderLength + data.Length);
        var plain = new byte[plainLength];
        if (NeedsRekey()) flags |= FlagRekey;
        plain[0] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(1), (ushort)data.Length);
        data.CopyTo(plain, HeaderLength);
        var cipher = _send.EncryptWithAd([], plain);
        var record = new byte[2 + cipher.Length];
        BinaryPrimitives.WriteUInt16BigEndian(record, (ushort)cipher.Length);
        _sendMask?.Apply(record.AsSpan(0, 2));
        cipher.CopyTo(record, 2);
        await _inner.WriteAsync(record, ct);
        RecordsSent++;
        BytesOnWire += record.Length;
        PaddingBytesSent += plainLength - HeaderLength - data.Length;
        if ((flags & FlagCover) != 0) CoverRecordsSent++;
        _lastWriteTicks = Stopwatch.GetTimestamp();
        if ((flags & FlagRekey) != 0)
        {
            _send.Rekey();
            Rekeys++;
            _sentSinceRekey = 0;
            _lastRekeyTicks = Stopwatch.GetTimestamp();
        }
        else _sentSinceRekey++;
    }

    private bool NeedsRekey() =>
        _sentSinceRekey + 1 >= _profile.RekeyEveryRecords || Stopwatch.GetElapsedTime(_lastRekeyTicks) >= _profile.RekeyEvery;

    internal int PaddedLength(int length)
    {
        if (_profile.PadBuckets is { } buckets)
            return buckets.FirstOrDefault(b => b >= length, MaxPlaintext);
        var pad = _profile.PadTo;
        return pad <= 1 ? length : Math.Min(MaxPlaintext, (length + pad - 1) / pad * pad);
    }

    /// <summary>空闲时按随机间隔发掩护记录（最小档位、内容是零），让流量的时间和多少不直接对应游戏里的动作。</summary>
    private async Task CoverLoopAsync(TimeSpan idle, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wait = idle * (0.5 + RandomNumberGenerator.GetInt32(1000) / 1000.0);
                await Task.Delay(wait, ct);
                if (Stopwatch.GetElapsedTime(_lastWriteTicks) < wait) continue;
                if (!await _writeLock.WaitAsync(0, ct)) continue;
                try
                {
                    if (_closeSent) return;
                    await WriteRecordAsync([], FlagCover, ct);
                    await _inner.FlushAsync(ct);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    /// <summary>按时间换钥不能只靠“有数据要发”：一个方向空闲时由这里发一条空记录来换钥。</summary>
    private async Task RekeyLoopAsync(CancellationToken ct)
    {
        var step = TimeSpan.FromTicks(Math.Max(_profile.RekeyEvery.Ticks / 8, TimeSpan.FromMilliseconds(50).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(step, ct);
                if (Stopwatch.GetElapsedTime(_lastRekeyTicks) < _profile.RekeyEvery) continue;
                await _writeLock.WaitAsync(ct);
                try
                {
                    if (_closeSent) return;
                    if (Stopwatch.GetElapsedTime(_lastRekeyTicks) >= _profile.RekeyEvery)
                    {
                        await WriteRecordAsync([], FlagCover, ct);
                        await _inner.FlushAsync(ct);
                    }
                }
                finally
                {
                    _writeLock.Release();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
    }

    /// <summary>发结束记录（只发一次）。之后不能再写。</summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_closeSent) return;
            _closeSent = true;
            await WriteRecordAsync([], FlagClose, ct);
            await _inner.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException(L.T("core.mp.transport.stream.interrupted"));
            }
            read += n;
        }
        return true;
    }

    private Task? _shutdown;
    private bool _aborted;

    /// <summary>出错时用：不发结束记录就关（对方会当成连接被截断）。</summary>
    public void Abort()
    {
        _aborted = true;
        _ = ShutdownAsync();
    }

    /// <summary>正常关闭：先发结束记录（最多等 1 秒）再关连接、清密钥。等它做完再关底下的 TCP，对方才一定收得到结束记录。</summary>
    public override ValueTask DisposeAsync() => new(ShutdownAsync());

    /// <summary>同步的 Dispose 不阻塞调用的线程（可能是界面线程）：在后台做同样的收尾。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = ShutdownAsync();
        base.Dispose(disposing);
    }

    private Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _shutdown = Task.Run(ShutdownCoreAsync);
        return _shutdown ?? Task.CompletedTask;
    }

    private async Task ShutdownCoreAsync()
    {
        if (!_aborted)
        {
            try { await CloseAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception e) when (e is TimeoutException or ObjectDisposedException or IOException or OperationCanceledException or System.Net.Sockets.SocketException or CryptographicException) { }
        }
        _cts.Cancel();
        try { await Task.WhenAll(new[] { _cover, _rekeyTimer }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        // 先关底下的连接：卡在写上的操作会因此出错并放开写锁。
        _inner.Dispose();
        // 一定在拿到写锁之后才清发送密钥，保证没有写在用它。
        await _writeLock.WaitAsync();
        try
        {
            _send.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }
        _receive.Dispose();
        _sendMask?.Dispose();
        _receiveMask?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>长度字段的遮盖：AES-256-CTR 密钥流（计数器从 0 开始，每 2 字节用掉密钥流的 2 字节）。两个方向用不同的密钥。</summary>
public sealed class LengthMask : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _block = new byte[16];
    private int _used = 16;

    public LengthMask(byte[] key)
    {
        _aes = Aes.Create();
        _aes.Key = key;
    }

    public void Apply(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (_used == 16)
            {
                _aes.EncryptEcb(_counter, _block, PaddingMode.None);
                for (var j = 15; j >= 0 && ++_counter[j] == 0; j--) { }
                _used = 0;
            }
            data[i] ^= _block[_used++];
        }
    }

    public void Dispose() => _aes.Dispose();
}
