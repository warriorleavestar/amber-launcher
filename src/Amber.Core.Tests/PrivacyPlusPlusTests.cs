using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>隐私++：节点码、节点心跳、熔断（断开全部连接、暂停进服、自动恢复）、吊销节点。</summary>
public sealed class PrivacyPlusPlusTests : IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-plusplus-test-" + Guid.NewGuid().ToString("N"));
    private GateStore _store = null!;
    private GateServer _server = null!;
    private X25519KeyPair _serverKey = null!;
    private TcpListener _backend = null!;
    private readonly List<(GateRelay Relay, X25519KeyPair Key, GateRelayCode Code, string Address)> _relays = [];

    public async Task InitializeAsync()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _store = GateStore.Initialize(Path.Combine(_root, "gate"), "127.0.0.1", 1, 0);
        var config = _store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        config.BackendPort = ((IPEndPoint)_backend.LocalEndpoint).Port;
        _serverKey = _store.LoadServerKey();
        _server = new GateServer(_store, config, _serverKey, _ => { })
        {
            HeartbeatTimeoutOverride = TimeSpan.FromMilliseconds(1200),
            RecoverDelay = TimeSpan.FromMilliseconds(300),
            CheckInterval = TimeSpan.FromMilliseconds(100),
        };
        _server.Start();
        config.PublicPort = _server.Port;
        _store.SaveConfig(config);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var r in _relays)
        {
            await r.Relay.DisposeAsync();
            r.Key.Dispose();
        }
        await _server.DisposeAsync();
        _backend.Stop();
        _serverKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>加一个带节点码、会发心跳的节点。</summary>
    private int AddRelay()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var address = new GateEndpoint("127.0.0.1", port);
        _store.AddRelay(address, null);
        var code = _store.IssueRelayCode(address.ToString(), _serverKey.PublicKey);
        var key = X25519KeyPair.Generate();
        var relay = new GateRelay("127.0.0.1", port, "127.0.0.1", _server.Port, code: code, key: key, heartbeatInterval: Interval);
        relay.Start();
        _relays.Add((relay, key, code, address.ToString()));
        return _relays.Count - 1;
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    private MemberIdentity Identity(string name) => MemberIdentity.LoadOrCreate(Path.Combine(_root, "client-" + name));

    private async Task<(GateMembership, MemberIdentity)> Member()
    {
        var identity = Identity("a");
        var membership = await GateClient.EnrollAsync(_store.CreateInvite("Steve", TimeSpan.FromHours(1), 1, _serverKey.PublicKey), identity);
        return (membership, identity);
    }

    /// <summary>进服并让 MC 服务端那头收到登录包。返回游戏这头的流和服务端那头的连接。</summary>
    private async Task<(Stream Game, TcpClient Server)> Play(GateMembership membership, MemberIdentity identity)
    {
        var stream = await GateClient.Opener(membership, identity)(CancellationToken.None);
        var hello = LoginPackets("Steve");
        await stream.WriteAsync(hello);
        await stream.FlushAsync();
        var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await server.GetStream().ReadExactlyAsync(new byte[hello.Length]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return (stream, server);
    }

    private static async Task AssertEnded(Stream stream)
    {
        var buffer = new byte[16];
        int n;
        try { n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException) { n = 0; }
        Assert.Equal(0, n);
    }

    [Fact]
    public void RelayCodeRoundTripsAndRejectsTamper()
    {
        var code = new GateRelayCode(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32));
        var text = code.Encode();
        var parsed = GateRelayCode.TryParse("节点码：\n" + text.ToLowerInvariant())!;
        Assert.Equal(code.ServerKeyHash, parsed.ServerKeyHash);
        Assert.Equal(code.Secret, parsed.Secret);
        Assert.DoesNotContain(text[GateRelayCode.Prefix.Length..], code.ToString());
        var tampered = text[..10] + (text[10] == 'Z' ? 'Y' : 'Z') + text[11..];
        Assert.Null(GateRelayCode.TryParse(tampered));
        Assert.Null(GateRelayCode.TryParse("JAYS1-ABC"));
    }

    [Fact]
    public void PlusPlusNeedsRelaysWithCodes()
    {
        _store.AddRelay(new GateEndpoint("127.0.0.1", 1), null);
        _store.AddRelay(new GateEndpoint("127.0.0.1", 2), null);
        Assert.Throws<ArgumentException>(() => _store.SetPrivacy(PrivacyLevel.PlusPlus));
        _store.IssueRelayCode("127.0.0.1:1", _serverKey.PublicKey);
        _store.IssueRelayCode("127.0.0.1:2", _serverKey.PublicKey);
        _store.SetPrivacy(PrivacyLevel.PlusPlus);
        Assert.Throws<ArgumentException>(() => _store.IssueRelayCode("127.0.0.1:3", _serverKey.PublicKey));
    }

    [Fact]
    public async Task HealthyRelaysLetPlayersInThroughTwoPaths()
    {
        AddRelay();
        AddRelay();
        _store.SetPrivacy(PrivacyLevel.PlusPlus);
        await Until(() => _relays.All(r => r.Relay.HeartbeatConnected), "heartbeats");
        await Until(() => !_server.BreakerOpen, "breaker closed");
        var (membership, identity) = await Member();
        Assert.Equal(PrivacyLevel.PlusPlus, membership.Network!.Level);
        var (game, server) = await Play(membership, identity);
        await using (game)
        using (server)
        {
            Assert.IsType<SplitStream>(game);
            await server.GetStream().WriteAsync("pong"u8.ToArray());
            var reply = new byte[4];
            await game.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("pong"u8.ToArray(), reply);
        }
        // 运行状态会写到 health.json（jay-server status 读它）。
        await Until(() => _store.ReadHealth() is { Relays.Count: 2 }, "health.json");
    }

    [Fact]
    public async Task LosingARelayTripsTheBreakerAndItRecovers()
    {
        AddRelay();
        AddRelay();
        _store.SetPrivacy(PrivacyLevel.PlusPlus);
        await Until(() => !_server.BreakerOpen && _relays.All(r => r.Relay.HeartbeatConnected), "ready");
        var (membership, identity) = await Member();
        var (game, server) = await Play(membership, identity);
        var changes = new List<bool>();
        _server.BreakerChanged += (open, _) => { lock (changes) changes.Add(open); };

        // 一个节点没了（连转发带心跳一起停）：熔断，在线的人被断开，新连接收到“熔断中”。
        var lost = _relays[1];
        await lost.Relay.DisposeAsync();
        await Until(() => _server.BreakerOpen, "breaker open");
        await AssertEnded(game);
        await game.DisposeAsync();
        server.Dispose();
        var e = await Assert.ThrowsAnyAsync<Exception>(() => GateClient.Opener(membership, identity)(CancellationToken.None));
        Assert.True(e is IOException or GateRejectedException, e.ToString());

        // 节点回来了（同一把节点密钥、同一个节点码）：连续健康一会儿后自动恢复。
        var back = new GateRelay("127.0.0.1", GateEndpoint.TryParse(lost.Address)!.Port, "127.0.0.1", _server.Port, code: lost.Code, key: lost.Key, heartbeatInterval: Interval);
        back.Start();
        _relays[1] = (back, lost.Key, lost.Code, lost.Address);
        await Until(() => !_server.BreakerOpen, "breaker closed");
        lock (changes) Assert.Equal([true, false], changes);
        var (again, server2) = await Play(membership, identity);
        await again.DisposeAsync();
        server2.Dispose();
    }

    [Fact]
    public async Task OneOfThreeRelaysDownDoesNotTripTheBreaker()
    {
        AddRelay();
        AddRelay();
        AddRelay();
        _store.SetPrivacy(PrivacyLevel.PlusPlus);
        await Until(() => !_server.BreakerOpen && _relays.All(r => r.Relay.HeartbeatConnected), "ready");
        await _relays[2].Relay.DisposeAsync();
        await Task.Delay(2000);
        Assert.False(_server.BreakerOpen);
    }

    [Fact]
    public async Task RevokingARelayDropsEveryoneAndRemovesIt()
    {
        AddRelay();
        AddRelay();
        AddRelay();
        _store.SetPrivacy(PrivacyLevel.PlusPlus);
        await Until(() => !_server.BreakerOpen && _relays.All(r => r.Relay.HeartbeatConnected), "ready");
        var (membership, identity) = await Member();
        var (game, server) = await Play(membership, identity);
        await Until(() => _server.ActiveSessions == 1, "session registered");

        Assert.True(_store.RevokeRelay(_relays[2].Address));
        await AssertEnded(game);
        await game.DisposeAsync();
        server.Dispose();
        // 被吊销的节点：心跳通道被断开，之后也连不上（它的 psk 已经作废）。
        await Until(() => !_relays[2].Relay.HeartbeatConnected, "revoked relay heartbeat dropped");
        await Task.Delay(500);
        Assert.False(_relays[2].Relay.HeartbeatConnected);
        Assert.DoesNotContain(_store.Network(_store.LoadConfig()).Relays, r => r.ToString() == _relays[2].Address);

        // 其余两个节点照常：重新进服（新握手、新密钥）。
        var open = GateClient.Opener(membership with { Network = membership.Network! with { Relays = [.. membership.Network.Relays.Where(r => r.ToString() != _relays[2].Address)] } }, identity);
        await using var again = await open(CancellationToken.None);
        Assert.IsType<SplitStream>(again);
    }

    [Fact]
    public async Task RelayWithAnotherKeyIsRefused()
    {
        var index = AddRelay();
        await Until(() => _relays[index].Relay.HeartbeatConnected, "first relay");
        using var otherKey = X25519KeyPair.Generate();
        await using var impostor = new GateRelay("127.0.0.1", 0, "127.0.0.1", _server.Port, code: _relays[index].Code, key: otherKey, heartbeatInterval: Interval);
        impostor.Start();
        await Task.Delay(1500);
        Assert.False(impostor.HeartbeatConnected);
        Assert.True(_relays[index].Relay.HeartbeatConnected);
    }

    [Fact]
    public async Task ReissuedCodeCutsTheOldRelayOff()
    {
        var index = AddRelay();
        await Until(() => _relays[index].Relay.HeartbeatConnected, "relay");
        _store.IssueRelayCode(_relays[index].Address, _serverKey.PublicKey);
        await Until(() => !_relays[index].Relay.HeartbeatConnected, "old code dropped");
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
