using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Jay 服务器网关：邀请码格式、入服、进服、用户名核对、设备上限、扫描器拿不到任何回应。</summary>
public sealed class GateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-gate-test-" + Guid.NewGuid().ToString("N"));
    private GateStore _store = null!;
    private GateServer _server = null!;
    private X25519KeyPair _serverKey = null!;
    private TcpListener _backend = null!;
    private readonly List<string> _log = [];

    public async Task InitializeAsync()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _store = GateStore.Initialize(Path.Combine(_root, "gate"), "127.0.0.1", 1, 0);
        var config = _store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        config.BackendPort = ((IPEndPoint)_backend.LocalEndpoint).Port;
        _serverKey = _store.LoadServerKey();
        _server = new GateServer(_store, config, _serverKey, line => { lock (_log) _log.Add(line); });
        _server.Start();
        config.PublicPort = _server.Port;
        _store.SaveConfig(config);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _backend.Stop();
        _serverKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MemberIdentity Identity(string name) => MemberIdentity.LoadOrCreate(Path.Combine(_root, "client-" + name));

    private GateInvite Invite(string name, int uses = 1) => _store.CreateInvite(name, TimeSpan.FromHours(1), uses, _serverKey.PublicKey);

    [Fact]
    public void InviteRoundTripsAndToleratesCopyPaste()
    {
        var invite = Invite("Steve");
        var code = invite.Encode();
        Assert.StartsWith(GateInvite.Prefix, code);
        var parsed = GateInvite.TryParse($"来玩吧：\n{code.ToLowerInvariant()}  （24 小时内有效）", out var error);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(invite.Port, parsed.Port);
        Assert.Equal(invite.Token, parsed.Token);
        Assert.Equal(invite.ServerKeyHash, parsed.ServerKeyHash);
        Assert.Equal("Steve", parsed.Username);
        Assert.DoesNotContain(Convert.ToHexString(invite.Token), invite.ToString());
    }

    [Fact]
    public void TamperedInviteIsRejected()
    {
        var code = Invite("Steve").Encode();
        for (var i = GateInvite.Prefix.Length; i < code.Length; i += 7)
        {
            var c = code[i] == 'Z' ? 'Y' : 'Z';
            var tampered = code[..i] + c + code[(i + 1)..];
            Assert.Null(GateInvite.TryParse(tampered, out var error));
            Assert.NotNull(error);
        }
        Assert.Null(GateInvite.TryParse(code[..^3], out _));
        Assert.Null(GateInvite.TryParse("JAY2-ABC", out _));
    }

    [Fact]
    public async Task EnrollThenJoinForwardsLoginToBackend()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        Assert.Equal("Steve", membership.Username);
        Assert.Equal(_serverKey.PublicKey, membership.ServerPublicKey);
        Assert.Equal(_store.AccessKey(), membership.AccessKey);

        await using var local = new TunnelClient(0, GateClient.Opener(membership, identity));
        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, local.Port);
        var hello = LoginPackets("steve");
        await game.GetStream().WriteAsync(hello);

        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var received = new byte[hello.Length];
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(hello, received);

        await server.GetStream().WriteAsync("pong"u8.ToArray());
        var reply = new byte[4];
        await game.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pong"u8.ToArray(), reply);
        Assert.NotNull(_store.Read().Member("Steve")!.Devices.Single().LastSeen);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("remove")]
    public async Task RevokedDeviceIsDroppedWhileOnline(string how)
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var local = new TunnelClient(0, GateClient.Opener(membership, identity));
        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, local.Port);
        await game.GetStream().WriteAsync(LoginPackets("Steve"));
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await server.GetStream().WriteAsync("pong"u8.ToArray());
        var reply = new byte[4];
        await game.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        switch (how)
        {
            case "revoke": Assert.Equal(1, _store.RevokeDevices("Steve", null)); break;
            case "disable": Assert.True(_store.SetDisabled("Steve", true)); break;
            default: Assert.True(_store.RemoveMember("Steve")); break;
        }

        // 网关每秒看一次名单：在线的连接要被断开（读到结尾或连接被重置），不用等玩家重连。
        var dropped = false;
        try { dropped = await game.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) == 0; }
        catch (IOException) { dropped = true; }
        Assert.True(dropped);
        lock (_log) Assert.Contains(_log, l => l.Contains("Steve"));
    }

    [Fact]
    public async Task OtherMembersStayOnlineWhenOneIsRevoked()
    {
        using var a = Identity("a");
        using var b = Identity("b");
        var steve = await GateClient.EnrollAsync(Invite("Steve"), a);
        var alex = await GateClient.EnrollAsync(Invite("Alex"), b);
        await using var tunnel = new TunnelClient(0, GateClient.Opener(alex, b));
        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, tunnel.Port);
        await game.GetStream().WriteAsync(LoginPackets("Alex"));
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _store.RevokeDevices("Steve", null);
        await Task.Delay(2500);
        await server.GetStream().WriteAsync("pong"u8.ToArray());
        var reply = new byte[4];
        await game.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pong"u8.ToArray(), reply);
        Assert.Equal("Steve", steve.Username);
    }

    [Fact]
    public async Task InviteIsSingleUseAndGateStaysSilent()
    {
        using var identity = Identity("a");
        var invite = Invite("Steve");
        await GateClient.EnrollAsync(invite, identity);
        using var other = Identity("b");
        var e = await Assert.ThrowsAsync<IOException>(() => GateClient.EnrollAsync(invite, other));
        Assert.Equal(Amber.Core.Localization.L.T("core.gate.protocol.silent"), e.Message);
    }

    [Fact]
    public async Task WrongUsernameGetsDisconnectAndNeverReachesBackend()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var secure = await JoinRaw(membership, identity);
        await secure.WriteAsync(LoginPackets("Notch"));
        await secure.FlushAsync();
        var buffer = new byte[512];
        var n = await secure.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Notch", Encoding.UTF8.GetString(buffer, 0, n));
        var pending = _backend.AcceptTcpClientAsync();
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task StolenAccessKeyOnAnotherDeviceIsRefused()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        using var thief = Identity("thief");
        var e = await Assert.ThrowsAsync<GateRejectedException>(() => JoinRaw(membership, thief));
        Assert.Equal(GateStatus.NotMember, e.Status);
    }

    [Fact]
    public async Task ImpostorServerIsDetectedBeforeDeviceKeyIsSent()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        using var fake = X25519KeyPair.Generate();
        var pinnedWrong = membership with { ServerPublicKey = fake.PublicKey };
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => JoinRaw(pinnedWrong, identity));
    }

    [Fact]
    public async Task DeviceLimitPerUsername()
    {
        var invite = Invite("Steve", uses: 3);
        using var a = Identity("a");
        using var b = Identity("b");
        using var c = Identity("c");
        await GateClient.EnrollAsync(invite, a);
        await GateClient.EnrollAsync(invite, b);
        var e = await Assert.ThrowsAsync<GateRejectedException>(() => GateClient.EnrollAsync(invite, c));
        Assert.Equal(GateStatus.DeviceLimit, e.Status);
        Assert.Equal(2, _store.Read().Member("steve")!.Devices.Count);
    }

    [Fact]
    public async Task ScannerGetsNoBytes()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        var stream = tcp.GetStream();
        // 像 MC 状态查询那样的探测，以及一条长度合法但内容随机的“握手”。
        await stream.WriteAsync((byte[])[0x00, 0x30, .. RandomNumberGenerator.GetBytes(0x30)]);
        var buffer = new byte[64];
        int n;
        try { n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { n = 0; }
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task StatusPingIsForwardedWithoutUsername()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var secure = await JoinRaw(membership, identity);
        var handshake = Handshake(1);
        await secure.WriteAsync(handshake);
        await secure.FlushAsync();
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var received = new byte[handshake.Length];
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(handshake, received);
    }

    [Fact]
    public async Task LegacyPingIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => McLogin.ReadAsync(new MemoryStream([0xFE, 0x01]), CancellationToken.None));
    }

    private async Task<SecureStream> JoinRaw(GateMembership membership, MemberIdentity identity)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, membership.Port);
        return await GateProtocol.JoinAsync(tcp.GetStream(), membership, identity.KeyPair);
    }

    private static byte[] LoginPackets(string name) => [.. Handshake(2), .. Packet([0x00, .. String(name), .. new byte[16]])];

    private static byte[] Handshake(int next) =>
        Packet([0x00, 0xFF, 0x05, .. String("mc.example.com"), 0x63, 0xDD, (byte)next]);

    private static byte[] String(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, .. bytes];
    }

    private static byte[] Packet(byte[] body) => [(byte)body.Length, .. body];
}

/// <summary>JAYS1- 贴错输入框时能认出来，不和 JAY2 系列的前缀混淆。</summary>
public sealed class GatePastedCodeTests
{
    [Theory]
    [InlineData("JAYS1-04632E9J5RRKCE1E64Q34C", PastedCodeKind.JayServer)]
    [InlineData("来玩：jays1-04632e9j5rrkce", PastedCodeKind.JayServer)]
    [InlineData("JAY2-ABCDEF", PastedCodeKind.Invite)]
    [InlineData("JAY2D-ABCDEF", PastedCodeKind.DirectInvite)]
    public void Detects(string text, PastedCodeKind kind) => Assert.Equal(kind, PastedCode.Detect(text));
}
