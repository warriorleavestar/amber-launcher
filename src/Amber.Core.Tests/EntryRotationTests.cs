using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>入口端口轮换（TOTP 原理）：端口计算、邀请码和地址簿里的种子、网关和节点按时间窗开关端口、客户端照着连。</summary>
public sealed class EntryRotationTests : IAsyncLifetime
{
    private static readonly GateRotation Rule = new(600, 41000, 59000);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-rotation-test-" + Guid.NewGuid().ToString("N"));
    private GateStore _store = null!;
    private GateServer _server = null!;
    private X25519KeyPair _serverKey = null!;
    private TcpListener _backend = null!;
    private readonly List<(GateRelay Relay, X25519KeyPair Key)> _relays = [];

    public async Task InitializeAsync()
    {
        _backend = new TcpListener(IPAddress.Loopback, 0);
        _backend.Start();
        _store = GateStore.Initialize(Path.Combine(_root, "gate"), "127.0.0.1", 1, 0);
        var config = _store.LoadConfig();
        config.ListenAddress = "127.0.0.1";
        config.BackendPort = ((IPEndPoint)_backend.LocalEndpoint).Port;
        _serverKey = _store.LoadServerKey();
        _server = new GateServer(_store, config, _serverKey, _ => { }) { CheckInterval = TimeSpan.FromMilliseconds(100) };
        _server.Start();
        config.PublicPort = _server.Port;
        config.ListenPort = _server.Port;
        _store.SaveConfig(config);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var (relay, key) in _relays)
        {
            await relay.DisposeAsync();
            key.Dispose();
        }
        await _server.DisposeAsync();
        _backend.Stop();
        _serverKey.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
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

    private static bool Listening(int port)
    {
        using var tcp = new TcpClient();
        try
        {
            tcp.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private GateRelay AddRelay()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var address = new GateEndpoint("127.0.0.1", port);
        _store.AddRelay(address, null);
        var code = _store.IssueRelayCode(address.ToString(), _serverKey.PublicKey);
        var key = X25519KeyPair.Generate();
        var relay = new GateRelay("127.0.0.1", port, "127.0.0.1", _server.Port, code: code, key: key, heartbeatInterval: TimeSpan.FromMilliseconds(200));
        relay.Start();
        _relays.Add((relay, key));
        return relay;
    }

    private async Task PlayAsync(GateMembership membership, MemberIdentity identity)
    {
        await using var stream = await GateClient.Opener(membership, identity)(CancellationToken.None);
        var hello = LoginPackets("Steve");
        await stream.WriteAsync(hello);
        await stream.FlushAsync();
        using var server = await _backend.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var received = new byte[hello.Length];
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(hello, received);
    }

    [Fact]
    public void PortsAreDeterministicInRangeAndChangeEveryWindow()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var ports = Enumerable.Range(0, 200).Select(w => Rule.PortAt(seed, w)).ToList();
        Assert.All(ports, p => Assert.InRange(p, Rule.MinPort, Rule.MaxPort));
        Assert.Equal(ports, Enumerable.Range(0, 200).Select(w => Rule.PortAt(seed, w)));
        Assert.True(ports.Distinct().Count() > 150);
        var other = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(ports, Enumerable.Range(0, 200).Select(w => Rule.PortAt(other, w)));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(Rule.PortAt(seed, now), Rule.CandidatePorts(seed, now)[0]);
        // 每个入口的种子各不相同，也推不出主种子。
        var master = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(GateRotation.EntrySeed(master, "direct"), GateRotation.EntrySeed(master, "relay.example.com:47671"));
        Assert.False(new GateRotation(10, 20000, 60000).IsValid);
        Assert.False(new GateRotation(600, 20000, 20050).IsValid);
    }

    [Fact]
    public void InviteCarriesRotationAndOldInvitesStillParse()
    {
        var rotation = new GateEntryRotation(Rule, RandomNumberGenerator.GetBytes(32));
        var invite = new GateInvite("mc.example.com", 47670, RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), "Steve", rotation);
        var parsed = GateInvite.TryParse(invite.Encode(), out var error)!;
        Assert.Null(error);
        Assert.Equal(Rule, parsed.Rotation!.Rotation);
        Assert.Equal(rotation.Seed, parsed.Rotation.Seed);
        var plain = GateInvite.TryParse((invite with { Rotation = null }).Encode(), out _)!;
        Assert.Null(plain.Rotation);
    }

    [Fact]
    public void AddressBookCarriesSeedsPerEntry()
    {
        var network = new GateNetwork(PrivacyLevel.Plus, null, [new("a.example.com", 1), new("b.example.com", 2)],
            new GateNetworkRotation(Rule, null, [RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)]));
        var decoded = GateProtocol.DecodeNetwork(GateProtocol.EncodeNetwork(network))!;
        Assert.Equal(Rule, decoded.Rotation!.Rule);
        Assert.Equal(network.Rotation!.RelaySeeds[1], decoded.RelayRotation(new("b.example.com", 2))!.Seed);
        Assert.Null(decoded.DirectRotation);
        var basic = new GateNetwork(PrivacyLevel.Basic, new("mc.example.com", 47670), [], new GateNetworkRotation(Rule, RandomNumberGenerator.GetBytes(32), []));
        Assert.Equal(basic.Rotation!.DirectSeed, GateProtocol.DecodeNetwork(GateProtocol.EncodeNetwork(basic))!.DirectRotation!.Seed);
    }

    [Fact]
    public async Task GateRotatesItsPortAndPlayersFollow()
    {
        var fixedPort = _server.Port;
        _store.EnableRotation(Rule, TimeSpan.Zero);
        await Until(() => !_server.FixedPortOpen && _server.RotatingPorts.Count > 0, "rotating");
        Assert.False(Listening(fixedPort));
        var seed = GateRotation.EntrySeed(_store.Rotation()!.Value.Master, GateRotation.DirectEntry);
        Assert.Contains(Rule.PortAt(seed, DateTimeOffset.UtcNow), _server.RotatingPorts);

        // 新邀请带着轮换信息：玩家照着算出端口入服、进服。
        using var identity = MemberIdentity.LoadOrCreate(Path.Combine(_root, "client"));
        var invite = _store.CreateInvite("Steve", TimeSpan.FromHours(1), 1, _serverKey.PublicKey);
        Assert.NotNull(invite.Rotation);
        var membership = await GateClient.EnrollAsync(invite, identity);
        Assert.NotNull(membership.Network!.DirectRotation);
        await PlayAsync(membership, identity);

        // 关掉轮换：回到固定端口。
        _store.DisableRotation();
        await Until(() => _server.FixedPortOpen && _server.RotatingPorts.Count == 0, "fixed again");
        Assert.True(Listening(fixedPort));
    }

    [Fact]
    public async Task GracePeriodKeepsTheFixedPortForExistingPlayers()
    {
        using var identity = MemberIdentity.LoadOrCreate(Path.Combine(_root, "client"));
        var old = await GateClient.EnrollAsync(_store.CreateInvite("Steve", TimeSpan.FromHours(1), 1, _serverKey.PublicKey), identity);
        Assert.Null(old.Network!.DirectRotation);
        _store.EnableRotation(Rule, TimeSpan.FromHours(1));
        await Until(() => _server.RotatingPorts.Count > 0, "rotating");
        Assert.True(_server.FixedPortOpen);
        // 老玩家经固定端口进一次服，拿到地址簿里的轮换种子。
        GateNetwork? learned = null;
        await using (var stream = await GateClient.Opener(old, identity, n => learned = n)(CancellationToken.None)) { }
        Assert.NotNull(learned?.DirectRotation);
    }

    [Fact]
    public async Task RelaysRotateThroughTheHeartbeatChannel()
    {
        var a = AddRelay();
        var b = AddRelay();
        _store.SetPrivacy(PrivacyLevel.Plus);
        await Until(() => a.HeartbeatConnected && b.HeartbeatConnected, "heartbeats");
        var fixedA = a.Port;
        _store.EnableRotation(Rule, TimeSpan.Zero);
        await Until(() => !a.FixedPortOpen && !b.FixedPortOpen && a.RotatingPorts.Count > 0 && b.RotatingPorts.Count > 0, "relays rotating");
        Assert.False(Listening(fixedA));
        // 网关自己在隐私+ 下留着固定端口给节点转发，不轮换。
        Assert.True(_server.FixedPortOpen);
        Assert.Empty(_server.RotatingPorts);

        using var identity = MemberIdentity.LoadOrCreate(Path.Combine(_root, "client"));
        var invite = _store.CreateInvite("Steve", TimeSpan.FromHours(1), 1, _serverKey.PublicKey);
        Assert.NotNull(invite.Rotation);
        var membership = await GateClient.EnrollAsync(invite, identity);
        Assert.NotNull(membership.Network!.Rotation);
        await PlayAsync(membership, identity);

        // 客户端保存的地址簿里也有轮换种子。
        var list = new JayServerList(Path.Combine(_root, "list"));
        list.Add(membership);
        var back = list.ToMembership(list.Load().Single());
        Assert.Equal(membership.Network.Rotation.RelaySeeds.Count, back.Network!.Rotation!.RelaySeeds.Count);
        await PlayAsync(back, identity);
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
