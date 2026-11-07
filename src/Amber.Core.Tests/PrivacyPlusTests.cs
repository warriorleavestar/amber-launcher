using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>隐私+：经两个节点的 2-of-2 拆分进服、节点列表下发、兼容旧版客户端、少一个节点就断。</summary>
public sealed class PrivacyPlusTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-plus-test-" + Guid.NewGuid().ToString("N"));
    private GateStore _store = null!;
    private GateServer _server = null!;
    private X25519KeyPair _serverKey = null!;
    private TcpListener _backend = null!;
    private GateRelay _relayA = null!;
    private GateRelay _relayB = null!;

    public async Task InitializeAsync()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _store = GateStore.Initialize(Path.Combine(_root, "gate"), "127.0.0.1", 1, 0);
        var config = _store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        config.BackendPort = ((IPEndPoint)_backend.LocalEndpoint).Port;
        _serverKey = _store.LoadServerKey();
        _server = new GateServer(_store, config, _serverKey, _ => { });
        _server.Start();
        config.PublicPort = _server.Port;
        _store.SaveConfig(config);
        _relayA = new GateRelay("127.0.0.1", 0, "127.0.0.1", _server.Port);
        _relayB = new GateRelay("127.0.0.1", 0, "127.0.0.1", _server.Port);
        _relayA.Start();
        _relayB.Start();
        _store.AddRelay(new GateEndpoint("127.0.0.1", _relayA.Port), null);
        _store.AddRelay(new GateEndpoint("127.0.0.1", _relayB.Port), null);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _relayA.DisposeAsync();
        await _relayB.DisposeAsync();
        await _server.DisposeAsync();
        _backend.Stop();
        _serverKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MemberIdentity Identity(string name) => MemberIdentity.LoadOrCreate(Path.Combine(_root, "client-" + name));

    private GateInvite Invite(string name) => _store.CreateInvite(name, TimeSpan.FromHours(1), 1, _serverKey.PublicKey);

    [Fact]
    public void NetworkEncodingRoundTrips()
    {
        var network = new GateNetwork(PrivacyLevel.Plus, null, [new("relay1.example.com", 47671), new("10.0.0.5", 5)]);
        var decoded = GateProtocol.DecodeNetwork(GateProtocol.EncodeNetwork(network))!;
        Assert.Equal(PrivacyLevel.Plus, decoded.Level);
        Assert.Null(decoded.Direct);
        Assert.Equal(network.Relays, decoded.Relays);
        var basic = GateProtocol.DecodeNetwork(GateProtocol.EncodeNetwork(new GateNetwork(PrivacyLevel.Basic, new("mc.example.com", 47670), [])))!;
        Assert.Equal(new GateEndpoint("mc.example.com", 47670), basic.Direct);
        Assert.Null(GateProtocol.DecodeNetwork([1, 0, 1, 3, (byte)'a']));
        Assert.Null(GateProtocol.DecodeNetwork([9, 0, 0]));
    }

    [Theory]
    [InlineData("example.com:47671", "example.com", 47671)]
    [InlineData(" 192.168.1.20:5 ", "192.168.1.20", 5)]
    [InlineData("[::1]:47671", "::1", 47671)]
    public void EndpointParses(string text, string host, int port) => Assert.Equal(new GateEndpoint(host, port), GateEndpoint.TryParse(text));

    [Theory]
    [InlineData("example.com")]
    [InlineData("example.com:0")]
    [InlineData("::1:47671")]
    [InlineData("bad host:1")]
    public void EndpointRejects(string text) => Assert.Null(GateEndpoint.TryParse(text));

    [Fact]
    public void PlusNeedsTwoRelaysAndInviteUsesARelay()
    {
        _store.RemoveRelay($"127.0.0.1:{_relayB.Port}");
        Assert.Throws<ArgumentException>(() => _store.SetPrivacy(PrivacyLevel.Plus));
        _store.AddRelay(new GateEndpoint("127.0.0.1", _relayB.Port), null);
        _store.SetPrivacy(PrivacyLevel.Plus);
        Assert.Throws<ArgumentException>(() => _store.RemoveRelay($"127.0.0.1:{_relayA.Port}"));
        var invite = Invite("Steve");
        Assert.Contains(invite.Port, new[] { _relayA.Port, _relayB.Port });
        Assert.Null(_store.Network(_store.LoadConfig()).Direct);
        Assert.Throws<ArgumentException>(() => _store.AddRelay(new GateEndpoint("x.example.com", 1), "not-an-ip"));
    }

    [Fact]
    public async Task PlusJoinGoesThroughTwoRelaysAndCarriesLargeData()
    {
        _store.SetPrivacy(PrivacyLevel.Plus);
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        Assert.Equal(PrivacyLevel.Plus, membership.Network!.Level);
        Assert.Null(membership.Network.Direct);
        Assert.Equal(2, membership.Network.Relays.Count);

        await using var stream = await GateClient.Opener(membership, identity)(CancellationToken.None);
        Assert.IsType<SplitStream>(stream);
        var hello = LoginPackets("Steve");
        await stream.WriteAsync(hello);
        await stream.FlushAsync();
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var received = new byte[hello.Length];
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(hello, received);

        // 大块数据：跨好几个拆分块，两个方向都要原样到。
        var up = RandomNumberGenerator.GetBytes(200_000);
        var down = RandomNumberGenerator.GetBytes(150_000);
        var write = stream.WriteAsync(up).AsTask();
        var gotUp = new byte[up.Length];
        await server.GetStream().ReadExactlyAsync(gotUp).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        await write;
        Assert.Equal(up, gotUp);
        await server.GetStream().WriteAsync(down);
        var gotDown = new byte[down.Length];
        await stream.ReadExactlyAsync(gotDown).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(down, gotDown);
    }

    [Fact]
    public async Task ClientUpgradesToSplitWhenServerSwitchesToPlus()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        Assert.Equal(PrivacyLevel.Basic, membership.Network!.Level);
        Assert.NotNull(membership.Network.Direct);
        _store.SetPrivacy(PrivacyLevel.Plus);

        var updates = new List<GateNetwork>();
        var open = GateClient.Opener(membership, identity, n => { lock (updates) updates.Add(n); });
        await using var stream = await open(CancellationToken.None);
        Assert.IsType<SplitStream>(stream);
        lock (updates) Assert.Contains(updates, n => n.Level == PrivacyLevel.Plus && n.Relays.Count == 2);
    }

    [Fact]
    public async Task LegacyClientInPlusModeIsToldToUpdate()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using (var legacy = await LegacyJoin(membership, identity)) { }
        _store.SetPrivacy(PrivacyLevel.Plus);
        var e = await Assert.ThrowsAsync<GateRejectedException>(() => LegacyJoin(membership, identity));
        Assert.Equal(GateStatus.SplitRequired, e.Status);
    }

    [Fact]
    public async Task SplitLegsFromUnknownSourcesAreRefused()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        _store.SetPrivacy(PrivacyLevel.Plus);
        // 节点登记的来源 IP 对不上：网关把经它们转来的连接当成外人。
        _store.AddRelay(new GateEndpoint("127.0.0.1", _relayA.Port), "10.9.9.9");
        _store.AddRelay(new GateEndpoint("127.0.0.1", _relayB.Port), "10.9.9.9");
        var network = new GateNetwork(PrivacyLevel.Plus, null, [new("127.0.0.1", _relayA.Port), new("127.0.0.1", _relayB.Port)]);
        var e = await Assert.ThrowsAsync<GateRejectedException>(() => GateClient.OpenSplitAsync(membership, network, identity, null, CancellationToken.None));
        Assert.Equal(GateStatus.SplitRequired, e.Status);
    }

    [Fact]
    public async Task SplitNeedsTwoReachableRelaysAndNeverFallsBackToDirect()
    {
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        _store.SetPrivacy(PrivacyLevel.Plus);
        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        var network = new GateNetwork(PrivacyLevel.Plus, new("127.0.0.1", _server.Port), [new("127.0.0.1", _relayA.Port), new("127.0.0.1", deadPort)]);
        await Assert.ThrowsAsync<IOException>(() => GateClient.OpenSplitAsync(membership, network, identity, null, CancellationToken.None));
        var pending = _backend.AcceptTcpClientAsync();
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task LosingOneRelayEndsTheConnection()
    {
        _store.SetPrivacy(PrivacyLevel.Plus);
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var stream = await GateClient.Opener(membership, identity)(CancellationToken.None);
        var hello = LoginPackets("Steve");
        await stream.WriteAsync(hello);
        await stream.FlushAsync();
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await server.GetStream().ReadExactlyAsync(new byte[hello.Length]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await _relayA.DisposeAsync();
        var buffer = new byte[16];
        int n;
        try { n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception e) when (e is IOException or CryptographicException) { n = 0; }
        Assert.Equal(0, n);
        Assert.False(((IAuthenticatedStream)stream).ClosedCleanly);
        // MC 服务端那头也断开了。
        try { n = await server.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { n = 0; }
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task WrongUsernameOverSplitGetsDisconnect()
    {
        _store.SetPrivacy(PrivacyLevel.Plus);
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        await using var stream = await GateClient.Opener(membership, identity)(CancellationToken.None);
        await stream.WriteAsync(LoginPackets("Notch"));
        await stream.FlushAsync();
        var buffer = new byte[512];
        var n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Notch", Encoding.UTF8.GetString(buffer, 0, n));
    }

    [Fact]
    public async Task LegsWithSameIndexAreNotPaired()
    {
        _store.SetPrivacy(PrivacyLevel.Plus);
        using var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(Invite("Steve"), identity);
        var pairId = RandomNumberGenerator.GetBytes(JoinRequest.PairIdLength);
        await using var a = await Leg(_relayA.Port, membership, identity, 0, pairId);
        await using var b = await Leg(_relayB.Port, membership, identity, 0, pairId);
        var buffer = new byte[16];
        foreach (var leg in new[] { a, b })
        {
            int n;
            try { n = await leg.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception e) when (e is IOException or CryptographicException) { n = 0; }
            Assert.Equal(0, n);
        }
    }

    [Fact]
    public void ServerListKeepsNetworkAndUpdatesIt()
    {
        var list = new JayServerList(Path.Combine(_root, "client-list"));
        var key = RandomNumberGenerator.GetBytes(32);
        var membership = new GateMembership("relay.example.com", 47671, RandomNumberGenerator.GetBytes(32), key, "Steve",
            new GateNetwork(PrivacyLevel.Plus, null, [new("relay.example.com", 47671), new("10.0.0.2", 47671)]));
        var entry = list.Add(membership);
        var back = list.ToMembership(list.Load().Single());
        Assert.Equal(PrivacyLevel.Plus, back.Network!.Level);
        Assert.Equal(membership.Network!.Relays, back.Network.Relays);
        list.UpdateNetwork(entry.Id, new GateNetwork(PrivacyLevel.Basic, new("mc.example.com", 47670), []));
        back = list.ToMembership(list.Load().Single());
        Assert.Equal(PrivacyLevel.Basic, back.Network!.Level);
        Assert.Equal(new GateEndpoint("mc.example.com", 47670), back.Network.Direct);
    }

    private async Task<SecureStream> LegacyJoin(GateMembership membership, MemberIdentity identity)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        return await GateProtocol.JoinAsync(tcp.GetStream(), membership, identity.KeyPair, null, null, CancellationToken.None);
    }

    private static async Task<SecureStream> Leg(int port, GateMembership membership, MemberIdentity identity, byte index, byte[] pairId)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        return await GateProtocol.JoinAsync(tcp.GetStream(), membership, identity.KeyPair, JoinRequest.Leg(index, pairId), null, CancellationToken.None);
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
