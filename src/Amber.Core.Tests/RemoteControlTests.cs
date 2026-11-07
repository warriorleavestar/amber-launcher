using System.Collections.Concurrent;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.RemoteControl;

namespace Amber.Core.Tests;

/// <summary>
/// 远程控制：线上格式、块比对、坐标换算；以及经真的成员钥匙通道（回环 TCP）跑的完整会话：
/// 同意之前不截屏不发画面、只看时输入被丢弃、收回控制后输入被丢弃、踢人后立刻结束、房主请求成员、AMB- 房间不支持。
/// </summary>
public sealed class RemoteControlTests
{
    // ---- 假的截屏和注入 ----

    private sealed class FakeScreen(int width = 200, int height = 130) : IScreenSource
    {
        public int Captures;
        public volatile byte Shade = 10;
        public ScreenBounds Bounds { get; } = new(-100, 50, width, height);

        public bool TryCapture(ScreenFrame frame)
        {
            Interlocked.Increment(ref Captures);
            frame.Resize(Bounds.Width, Bounds.Height);
            for (var y = 0; y < Bounds.Height; y++)
                for (var x = 0; x < Bounds.Width; x++)
                {
                    var o = (y * Bounds.Width + x) * 4;
                    frame.Pixels[o] = (byte)x;
                    frame.Pixels[o + 1] = (byte)y;
                    // 左上角那块的颜色可以变（测只发变化块）。
                    frame.Pixels[o + 2] = x < 64 && y < 64 ? Shade : (byte)7;
                    frame.Pixels[o + 3] = 255;
                }
            return true;
        }

        public (int X, int Y)? CursorPosition() => (Bounds.X + 20, Bounds.Y + 30);
        public void Dispose() { }
    }

    private sealed class FakeInput : IInputSink
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public void Mouse(RemoteMouseAction action, RemoteMouseButton button, int x, int y, int delta) => Calls.Enqueue($"mouse {action} {button} {x},{y} {delta}");
        public void Key(ushort scanCode, bool up, bool extended) => Calls.Enqueue($"key {scanCode} {up} {extended}");
        public void Dispose() { }
    }

    private sealed class FixedPolicy(RemoteConsent consent) : IRemoteConsentPolicy
    {
        public int Asked;
        public RemoteConsentRequest? Last;
        public Task<RemoteConsent> DecideAsync(RemoteConsentRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            Last = request;
            return Task.FromResult(consent);
        }
    }

    private static (RoomCode Host, MemberTable Table, RoomCode Guest, MemberEntry Entry) Rooms(TransportMode mode = TransportMode.High)
    {
        var host = X25519KeyPair.Generate();
        var guest = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey.ToArray(), 25565, mode == TransportMode.General ? TunnelSettings.Default : new TunnelSettings(mode, false));
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var entry = table.Issue(guest.PublicKey, "Alice");
        var sent = RoomCredentials.FromJson((credentials with { Member = entry.Key }).ToJson());
        return (hostRoom, table, sent.ToRoom(), entry);
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    // ---- 线上格式 ----

    [Fact]
    public async Task Frames_RoundTrip()
    {
        var ms = new MemoryStream();
        var tiles = new RemoteTileBatch(7, 300, 200, [new RemoteTile(0, 0, 64, 64, [1, 2, 3]), new RemoteTile(256, 192, 44, 8, [])]);
        foreach (var f in new[]
        {
            RemoteWire.Hello(), RemoteWire.Accept(new RemoteAccept(true, 1920, 1080, 2)), RemoteWire.Deny(RemoteDenyReason.Busy), RemoteWire.Tiles(tiles),
            RemoteWire.Cursor(-5, 9, true), RemoteWire.Mouse(new RemoteMouse(RemoteMouseAction.Wheel, RemoteMouseButton.None, 10, 20, -120)),
            RemoteWire.Key(new RemoteKey(0x1D, true, true)), RemoteWire.Ping(123456789), RemoteWire.FrameAck(42), RemoteWire.Stop(RemoteStopReason.StoppedByUser),
        }) ms.Write(f);
        ms.Position = 0;
        async Task<RemoteFrame> Next() => (await RemoteWire.ReadAsync(ms, default))!.Value;

        var hello = await Next();
        Assert.Equal(new byte[] { RemoteWire.Version }, hello.Payload);
        Assert.Equal(new RemoteAccept(true, 1920, 1080, 2), RemoteWire.ParseAccept((await Next()).Payload));
        Assert.Equal(RemoteDenyReason.Busy, RemoteWire.ParseDeny((await Next()).Payload));
        var back = RemoteWire.ParseTiles((await Next()).Payload);
        Assert.Equal(7u, back.Sequence);
        Assert.Equal((300, 200), (back.FrameWidth, back.FrameHeight));
        Assert.Equal([1, 2, 3], back.Tiles[0].Data);
        Assert.Equal((256, 192, 44, 8), (back.Tiles[1].X, back.Tiles[1].Y, back.Tiles[1].Width, back.Tiles[1].Height));
        Assert.Equal((-5, 9, true), RemoteWire.ParseCursor((await Next()).Payload));
        Assert.Equal(new RemoteMouse(RemoteMouseAction.Wheel, RemoteMouseButton.None, 10, 20, -120), RemoteWire.ParseMouse((await Next()).Payload));
        Assert.Equal(new RemoteKey(0x1D, true, true), RemoteWire.ParseKey((await Next()).Payload));
        Assert.Equal(123456789, RemoteWire.ParseStamp((await Next()).Payload));
        Assert.Equal(42u, RemoteWire.ParseFrameAck((await Next()).Payload));
        Assert.Equal(RemoteFrameType.Stop, (await Next()).Type);
        Assert.Null(await RemoteWire.ReadAsync(ms, default));
    }

    [Fact]
    public void Tiles_OutsideFrameOrTooLarge_AreRejected()
    {
        byte[] Payload(RemoteTileBatch b) => RemoteWire.Tiles(b)[5..];
        Assert.Throws<InvalidDataException>(() => RemoteWire.ParseTiles(Payload(new(1, 100, 100, [new RemoteTile(64, 64, 64, 64, [])]))));
        Assert.Throws<InvalidDataException>(() => RemoteWire.ParseTiles(Payload(new(1, 200, 200, [new RemoteTile(0, 0, 65, 10, [])]))));
        Assert.Throws<InvalidDataException>(() => RemoteWire.ParseAccept([1, 0x20, 0, 0, 10, 1]));
        Assert.Throws<InvalidDataException>(() => RemoteWire.ParseKey([0, 0, 0]));
    }

    [Fact]
    public async Task Frame_WithHugeLength_IsRejected()
    {
        var ms = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF, 4]);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteWire.ReadAsync(ms, default));
    }

    [Fact]
    public void HelloPayload_RoleAndSession()
    {
        var sid = RemoteWire.NewSessionId();
        Assert.True(RemoteWire.TryParseHelloPayload(RemoteWire.HelloPayload(RemoteRole.Controlled, sid), out var role, out var back));
        Assert.Equal(RemoteRole.Controlled, role);
        Assert.Equal(sid, back);
        Assert.True(RemoteWire.TryParseHelloPayload(RemoteWire.HelloPayload(RemoteRole.Controller, []), out role, out _));
        Assert.Equal(RemoteRole.Controller, role);
        // 被控方必须带会话号；控制方不能带。
        var bad = RemoteWire.HelloPayload(RemoteRole.Controlled, sid);
        bad[1] = (byte)RemoteRole.Controller;
        Assert.False(RemoteWire.TryParseHelloPayload(bad, out _, out _));
        Assert.False(RemoteWire.TryParseHelloPayload([9, 1], out _, out _));
    }

    // ---- 块比对和坐标 ----

    [Fact]
    public void Differ_SendsAllFirst_ThenOnlyChangedTiles()
    {
        var screen = new FakeScreen();
        var frame = new ScreenFrame();
        var differ = new TileDiffer();
        screen.TryCapture(frame);
        // 200×130 → 4×3 块（边上的不满 64）。
        var first = differ.Diff(frame);
        Assert.Equal(12, first.Count);
        Assert.Contains((192, 128, 8, 2), first);
        screen.TryCapture(frame);
        Assert.Empty(differ.Diff(frame));
        screen.Shade = 99;
        screen.TryCapture(frame);
        Assert.Equal([(0, 0, 64, 64)], differ.Diff(frame));
    }

    [Fact]
    public void Geometry_MapsBetweenFrameDesktopAndView()
    {
        var bounds = new ScreenBounds(-1920, 0, 3840, 1080);
        // 画面是缩小一半的。
        Assert.Equal((-1920 + 1, 1), RemoteGeometry.FrameToDesktop(bounds, 1920, 540, 0, 0));
        Assert.Equal((1919, 1079), RemoteGeometry.FrameToDesktop(bounds, 1920, 540, 5000, 5000));
        Assert.Equal((960, 270), RemoteGeometry.DesktopToFrame(bounds, 1920, 540, 0, 540));
        // 1000×500 的窗口显示 400×400 的画面：左右各 250 的黑边。
        Assert.Null(RemoteGeometry.ViewToFrame(1000, 500, 400, 400, 100, 250));
        Assert.Equal((0, 0), RemoteGeometry.ViewToFrame(1000, 500, 400, 400, 250, 0));
        Assert.Equal((200, 200), RemoteGeometry.ViewToFrame(1000, 500, 400, 400, 500, 250));
        Assert.Equal((0, 0), RemoteGeometry.ToAbsolute(bounds, -1920, 0));
        Assert.Equal((65535, 65535), RemoteGeometry.ToAbsolute(bounds, 1919, 1079));
        Assert.Equal(1, RemoteGeometry.ScaleFor(1920, 1080));
        Assert.Equal(2, RemoteGeometry.ScaleFor(3840, 2160));
        Assert.Equal(3, RemoteGeometry.ScaleFor(7680, 1080));
    }

    [Fact]
    public void Downscale_AveragesBlocks()
    {
        var src = new ScreenFrame(4, 2);
        for (var i = 0; i < 8; i++) src.Pixels[i * 4] = (byte)(i * 10);
        var dst = new ScreenFrame();
        RemoteGeometry.Downscale(src, dst, 2);
        Assert.Equal((2, 1), (dst.Width, dst.Height));
        Assert.Equal((0 + 10 + 40 + 50) / 4, dst.Pixels[0]);
    }

    [Fact]
    public void RawCodec_RoundTripsAndRejectsGarbage()
    {
        var codec = new RawTileCodec();
        var tile = Enumerable.Range(0, 10 * 3 * 4).Select(i => (byte)i).ToArray();
        var data = codec.Encode(tile, 10, 3, 40, 70);
        var back = new byte[tile.Length];
        Assert.True(codec.Decode(data, back, 10, 3, 40));
        Assert.Equal(tile, back);
        Assert.False(codec.Decode([1, 2, 3], back, 10, 3, 40));
    }

    [Fact]
    public async Task AskEveryTime_TimesOutAsDenied()
    {
        var policy = new AskEveryTimePolicy((_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new RemoteConsent(true, false)), TimeSpan.FromMilliseconds(100));
        var result = await policy.DecideAsync(new RemoteConsentRequest("x", "y"), default);
        Assert.False(result.Accepted);
    }

    // ---- 完整会话（成员钥匙通道，回环 TCP） ----

    private static RemoteHostOptions HostOptions(IRemoteConsentPolicy policy, FakeScreen screen, FakeInput input) =>
        new(policy, () => screen, () => input, new RawTileCodec());

    private static async Task<RemoteViewer> ConnectAsControllerAsync(RemoteHub hub, RoomCode guest) =>
        new(await RemoteConnector.OpenAsync("127.0.0.1", hub.ListenPort, guest, RemoteRole.Controller, null), new RawTileCodec(), "Host", "");

    [Theory]
    [InlineData(TransportMode.General)]
    [InlineData(TransportMode.High)]
    public async Task MemberControlsHost_SeesScreenAndMovesMouse(TransportMode mode)
    {
        var (hostRoom, _, guest, entry) = Rooms(mode);
        var screen = new FakeScreen();
        var input = new FakeInput();
        var policy = new FixedPolicy(new RemoteConsent(true, false));
        await using var hub = new RemoteHub(hostRoom, HostOptions(policy, screen, input), 0);
        RemoteSharer? sharer = null;
        hub.SharingStarted += (s, _) => sharer = s;

        await using var viewer = await ConnectAsControllerAsync(hub, guest);
        var accept = await viewer.StartAsync();
        Assert.NotNull(accept);
        Assert.True(accept.Value.Control);
        // 同意弹窗里的名字和指纹来自成员表（不是对方自己报的）。
        Assert.Equal(("Alice", entry.Fingerprint), (policy.Last!.Name, policy.Last.Fingerprint));
        await WaitUntil(() => viewer.BatchesReceived > 0);
        lock (viewer.FrameLock)
        {
            Assert.Equal((200, 130), (viewer.Frame.Width, viewer.Frame.Height));
            var o = (100 * 200 + 150) * 4;
            Assert.Equal(new byte[] { 150, 100, 7, 255 }, viewer.Frame.Pixels[o..(o + 4)]);
        }
        await WaitUntil(() => viewer.Cursor == (20, 30, true));

        // 只有变化的块再发。
        var before = sharer!.TilesSent;
        screen.Shade = 200;
        await WaitUntil(() => sharer.TilesSent > before);
        await Task.Delay(400);
        Assert.Equal(before + 1, sharer.TilesSent);
        await WaitUntil(() => { lock (viewer.FrameLock) return viewer.Frame.Pixels[2] == 200; });

        Assert.True(await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, 10, 20, 0)));
        await WaitUntil(() => input.Calls.Count > 0);
        // 画面像素 (10,20) → 桌面坐标（左上角 -100,50）。
        Assert.Equal("mouse Move None -90,70 0", input.Calls.First());
        Assert.True(await viewer.SendKeyAsync(new RemoteKey(0x1E, false, false)));
        await WaitUntil(() => input.Calls.Count > 1);

        // 结束时松开按住的键（A 还按着）。
        await viewer.StopAsync();
        await WaitUntil(() => sharer.HasEnded);
        await WaitUntil(() => input.Calls.Contains("key 30 True False"));
    }

    [Fact]
    public async Task Declined_NoScreenIsCaptured()
    {
        var (hostRoom, _, guest, _) = Rooms();
        var screen = new FakeScreen();
        var input = new FakeInput();
        await using var hub = new RemoteHub(hostRoom, HostOptions(new FixedPolicy(RemoteConsent.Denied), screen, input), 0);
        await using var viewer = await ConnectAsControllerAsync(hub, guest);
        Assert.Null(await viewer.StartAsync());
        Assert.Equal(RemoteDenyReason.Declined, viewer.DenyReason);
        Assert.Equal(0, screen.Captures);
        Assert.Equal(0, viewer.BatchesReceived);
    }

    [Fact]
    public async Task ViewOnly_InputIsDropped()
    {
        var (hostRoom, _, guest, _) = Rooms();
        var screen = new FakeScreen();
        var input = new FakeInput();
        await using var hub = new RemoteHub(hostRoom, HostOptions(new FixedPolicy(new RemoteConsent(true, true)), screen, input), 0);
        RemoteSharer? sharer = null;
        hub.SharingStarted += (s, _) => sharer = s;
        await using var viewer = await ConnectAsControllerAsync(hub, guest);
        var accept = await viewer.StartAsync();
        Assert.False(accept!.Value.Control);
        Assert.False(viewer.ControlGranted);
        // 控制方自己不发。
        Assert.False(await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 1, 1, 0)));
        // 硬塞过去也被被控方丢掉。
        await viewer.SendRawForTest(RemoteWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 1, 1, 0)));
        await viewer.SendRawForTest(RemoteWire.Key(new RemoteKey(0x1E, false, false)));
        await WaitUntil(() => sharer?.InputsDropped == 2);
        Assert.Empty(input.Calls);
        // 只看的会话不能改成允许控制。
        await sharer!.SetControlAsync(true);
        Assert.False(sharer.ControlAllowed);
    }

    [Fact]
    public async Task RevokedControl_InputIsDropped_ScreenContinues()
    {
        var (hostRoom, _, guest, _) = Rooms();
        var screen = new FakeScreen();
        var input = new FakeInput();
        await using var hub = new RemoteHub(hostRoom, HostOptions(new FixedPolicy(new RemoteConsent(true, false)), screen, input), 0);
        RemoteSharer? sharer = null;
        hub.SharingStarted += (s, _) => sharer = s;
        await using var viewer = await ConnectAsControllerAsync(hub, guest);
        Assert.NotNull(await viewer.StartAsync());
        Assert.True(await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 5, 5, 0)));
        await WaitUntil(() => sharer?.InputsInjected == 1);

        await sharer!.SetControlAsync(false);
        await WaitUntil(() => !viewer.ControlGranted);
        // 收回时松开了按住的左键。
        Assert.Contains(input.Calls, c => c.StartsWith("mouse Up Left", StringComparison.Ordinal));
        var calls = input.Calls.Count;
        Assert.False(await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, 9, 9, 0)));
        await viewer.SendRawForTest(RemoteWire.Mouse(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, 9, 9, 0)));
        await WaitUntil(() => sharer.InputsDropped == 1);
        Assert.Equal(calls, input.Calls.Count);
        // 画面照常。
        var batches = viewer.BatchesReceived;
        screen.Shade = 123;
        await WaitUntil(() => viewer.BatchesReceived > batches);
        Assert.False(viewer.HasEnded);
    }

    [Fact]
    public async Task Kicked_SessionEndsImmediately()
    {
        var (hostRoom, table, guest, entry) = Rooms();
        var screen = new FakeScreen();
        await using var hub = new RemoteHub(hostRoom, HostOptions(new FixedPolicy(new RemoteConsent(true, false)), screen, new FakeInput()), 0);
        RemoteSharer? sharer = null;
        hub.SharingStarted += (s, _) => sharer = s;
        await using var viewer = await ConnectAsControllerAsync(hub, guest);
        Assert.NotNull(await viewer.StartAsync());
        await WaitUntil(() => sharer is not null);
        table.Kick(entry, null);
        await WaitUntil(() => sharer!.HasEnded && viewer.HasEnded, 5);
        Assert.Null(hub.Sharing);
        // 被踢之后连都连不上。
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var again = await ConnectAsControllerAsync(hub, guest);
            if (await again.StartAsync() is null) throw new IOException("closed");
        });
    }

    [Fact]
    public async Task HostRequestsMember_MemberConnectsBackAsControlled()
    {
        var (hostRoom, _, guest, entry) = Rooms();
        var memberScreen = new FakeScreen(150, 90);
        var memberInput = new FakeInput();
        await using var hub = new RemoteHub(hostRoom, HostOptions(new FixedPolicy(RemoteConsent.Denied), new FakeScreen(), new FakeInput()), 0);
        RemoteSharer? memberSharer = null;
        var result = await hub.RequestAsync(entry, sid =>
        {
            // 成员那边：收到请求、同意后连回房主（这里直接连）。
            _ = Task.Run(async () =>
            {
                var stream = await RemoteConnector.OpenAsync("127.0.0.1", hub.ListenPort, guest, RemoteRole.Controlled, sid);
                memberSharer = new RemoteSharer(stream, memberScreen, memberInput, new RawTileCodec(), allowControl: true, "Host", "");
                await memberSharer.RunAsync();
            });
            return true;
        });
        Assert.Equal(RemoteRequestFailure.None, result.Failure);
        await using var viewer = result.Viewer!;
        Assert.True(viewer.ControlGranted);
        await WaitUntil(() => viewer.BatchesReceived > 0);
        Assert.Equal((150, 90), (viewer.Frame.Width, viewer.Frame.Height));
        Assert.True(await viewer.SendMouseAsync(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, 0, 0, 0)));
        await WaitUntil(() => !memberInput.Calls.IsEmpty);
    }

    [Fact]
    public async Task HostRequest_DeclinedOrWrongSession()
    {
        var (hostRoom, _, guest, entry) = Rooms();
        await using var hub = new RemoteHub(hostRoom, HostOptions(new FixedPolicy(RemoteConsent.Denied), new FakeScreen(), new FakeInput()), 0);
        var declined = await hub.RequestAsync(entry, sid =>
        {
            _ = Task.Run(() => hub.Decline(entry, sid));
            return true;
        });
        Assert.Equal(RemoteRequestFailure.Declined, declined.Failure);
        Assert.Equal(RemoteRequestFailure.Offline, (await hub.RequestAsync(entry, _ => false)).Failure);

        // 编造的会话号：房主不认，连接被关。
        var forged = await RemoteConnector.OpenAsync("127.0.0.1", hub.ListenPort, guest, RemoteRole.Controlled, Convert.ToHexStringLower(RemoteWire.NewSessionId()));
        var sharer = new RemoteSharer(forged, new FakeScreen(), null, new RawTileCodec(), false, "Host", "");
        await sharer.RunAsync();
        Assert.True(sharer.HasEnded);
        Assert.Equal(0, sharer.TilesSent);
    }

    [Fact]
    public async Task OldRoomCode_IsNotSupported()
    {
        var room = RoomCode.Create(25565);
        Assert.False(room.FromInvite);
        Assert.Throws<InvalidOperationException>(() => new RemoteHub(room, HostOptions(RemoteConsentFor(true), new FakeScreen(), new FakeInput()), 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteConnector.OpenAsync("127.0.0.1", 1, room, RemoteRole.Controller, null));
    }

    private static FixedPolicy RemoteConsentFor(bool accept) => new(new RemoteConsent(accept, false));

    [Fact]
    public async Task ChatChannel_CarriesRemoteRequestAndDecline()
    {
        var (hostRoom, _, guest, entry) = Rooms();
        await using var chatHub = new ChatHub(hostRoom, 0);
        await using var client = new ChatClient(guest, "Alice");
        var requested = new TaskCompletionSource<string>();
        client.RemoteRequested += sid => requested.TrySetResult(sid);
        var declined = new TaskCompletionSource<(MemberEntry, string)>();
        chatHub.RemoteDeclined += (who, sid) => declined.TrySetResult((who, sid));
        await client.ConnectAsync("127.0.0.1", chatHub.Port);
        await WaitUntil(() => chatHub.MemberCount == 1);

        var sid = Convert.ToHexStringLower(RemoteWire.NewSessionId());
        Assert.True(chatHub.SendRemoteRequest(entry, sid));
        Assert.Equal(sid, await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await client.DeclineRemoteAsync(sid);
        var (who, back) = await declined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(entry, who);
        Assert.Equal(sid, back);
    }
}
