using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 直连码不带密钥（JAY2D- / JAY2R-），桥钥匙来自邀请里的大厅密钥，批准前直连桥只能到大厅，
/// 批准后证明成员身份才接到房间；踢出断开桥；换网络时桥不动；旧版直连码不认。
/// </summary>
public sealed class DirectInviteTests
{
    private static readonly DirectOptions Options = new([], false, [], false, false, false, true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    private static readonly IReadOnlyList<Candidate> SomeAddresses =
    [
        new(PathKind.IPv6, IPEndPoint.Parse("[2408:8000::1]:40000")),
        new(PathKind.Punch, IPEndPoint.Parse("203.0.113.5:51234")),
        new(PathKind.Lan, IPEndPoint.Parse("192.168.1.5:40000")),
    ];

    private static string BodyOf(string code)
    {
        var b64 = code[6..].Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private static string Encode(string prefix, JsonObject json) =>
        prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- 码 ----

    [Fact]
    public void InviteAndReplyRoundTrip()
    {
        var invite = new DirectCode(false, "abcdef0123456789", NatKind.Cone, SomeAddresses, DirectTestKeys.Invite);
        var text = invite.Encode();
        Assert.StartsWith("JAY2D-", text);
        Assert.True(DirectCode.TryParse("朋友发来：" + text + " 快来", out var parsed, out _));
        Assert.False(parsed.IsReply);
        Assert.Equal(invite.Candidates, parsed.Candidates);
        Assert.Equal(DirectTestKeys.Invite.ToString(), parsed.Invite!.ToString());
        Assert.True(parsed.DirectlyReachable);

        var reply = new DirectCode(true, "abcdef0123456789", NatKind.Symmetric, [new Candidate(PathKind.Punch, IPEndPoint.Parse("198.51.100.7:40001"))]);
        var replyText = reply.Encode();
        Assert.StartsWith("JAY2R-", replyText);
        Assert.True(DirectCode.TryParse(replyText, out var r, out _));
        Assert.True(r.IsReply);
        Assert.Null(r.Invite);
        Assert.False(r.DirectlyReachable);
        // 打码显示只露出前缀和最后 4 位。
        Assert.Equal("JAY2D-", invite.Masked[..6]);
        Assert.DoesNotContain(text[6..^4], invite.Masked);
    }

    /// <summary>直连码里没有房间的网络名、密钥和成员钥匙；回复码里只有会话、NAT 和地址。</summary>
    [Fact]
    public void CodesCarryNoRoomSecretsOrMemberKeys()
    {
        using var host = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default);
        var members = new MemberTable();
        var room = credentials.ToHostRoom(members, "Host");
        var guest = members.Issue(RandomNumberGenerator.GetBytes(32), "Steve");
        var book = new InviteBook { KeepLobbyKey = true };
        var pending = book.Create(1, TimeSpan.FromMinutes(10), false, credentials.Tunnel);
        var inviteText = new DirectCode(false, "abcdef0123456789", NatKind.Cone, SomeAddresses, pending.Invite).Encode();
        var replyText = new DirectCode(true, "abcdef0123456789", NatKind.Cone, SomeAddresses).Encode();
        foreach (var body in new[] { BodyOf(inviteText), BodyOf(replyText), inviteText, replyText })
        {
            foreach (var secret in room.Secrets) Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(credentials.Secret, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(credentials.Network, body, StringComparison.OrdinalIgnoreCase);
            foreach (var key in new[] { room.Member!, guest.Key })
            {
                Assert.DoesNotContain(key.PskBase64, body);
                Assert.DoesNotContain(Convert.ToHexString(key.CopyPsk()), body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(key.IdHex, body, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.Equal(["c", "n", "s", "v"], JsonNode.Parse(BodyOf(replyText))!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["c", "i", "n", "s", "v"], JsonNode.Parse(BodyOf(inviteText))!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("AMBDIRECT1-eyJhIjoxfQ")]
    [InlineData("来直连：ambdirect1-eyJyIjoiQU1CLUs3UTJNOVhELTRIWlA4UlcyTjZUQy0yNTU2NSJ9")]
    [InlineData("AMBREPLY1-eyJhIjoxfQ")]
    public void OldDirectCodesAreRejectedWithTheUpdateHint(string text)
    {
        Assert.False(DirectCode.TryParse(text, out _, out var error));
        Assert.Contains("旧版本", error);
        Assert.True(DirectCode.LooksLikeOldCode(text));
        Assert.Equal(PastedCodeKind.OldDirect, PastedCode.Detect(text));
    }

    [Fact]
    public void BadCodesAreRejected()
    {
        var good = JsonNode.Parse(BodyOf(new DirectCode(false, "abcdef0123456789", NatKind.Cone, SomeAddresses, DirectTestKeys.Invite).Encode()))!.AsObject();
        var newer = good.DeepClone().AsObject();
        newer["v"] = 2;
        Assert.False(DirectCode.TryParse(Encode("JAY2D-", newer), out _, out var error));
        Assert.Contains("第 2 版", error);
        Assert.False(DirectCode.TryParse("JAY2D-!!!!", out _, out error));
        Assert.Contains("损坏", error);
        Assert.False(DirectCode.TryParse("hello", out _, out error));
        Assert.Contains("没认出", error);
        // 邀请码少了邀请、回复码带了邀请、多了别的字段（比如塞进来的密钥）：都不认。
        var noInvite = good.DeepClone().AsObject();
        noInvite.Remove("i");
        Assert.False(DirectCode.TryParse(Encode("JAY2D-", noInvite), out _, out _));
        Assert.False(DirectCode.TryParse(Encode("JAY2R-", good.DeepClone().AsObject()), out _, out _));
        var extra = good.DeepClone().AsObject();
        extra["r"] = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565";
        Assert.False(DirectCode.TryParse(Encode("JAY2D-", extra), out _, out _));
        var badSession = good.DeepClone().AsObject();
        badSession["s"] = "short";
        Assert.False(DirectCode.TryParse(Encode("JAY2D-", badSession), out _, out _));
    }

    [Theory]
    [InlineData("JAY2D-eyJ2IjoxfQ", PastedCodeKind.DirectInvite)]
    [InlineData("来直连：jay2d-eyJ2IjoxfQ 快来", PastedCodeKind.DirectInvite)]
    [InlineData("JAY2R-eyJ2IjoxfQ", PastedCodeKind.DirectReply)]
    public void PastedCodeRecognizesDirectCodes(string text, PastedCodeKind kind) => Assert.Equal(kind, PastedCode.Detect(text));

    [Fact]
    public void DiagnosticsRedactDirectCodes()
    {
        var invite = new DirectCode(false, "abcdef0123456789", NatKind.Cone, SomeAddresses, DirectTestKeys.Invite).Encode();
        var reply = new DirectCode(true, "abcdef0123456789", NatKind.Cone, SomeAddresses).Encode();
        var text = NetDiagnostics.Redact($"invite {invite} reply {reply.ToLowerInvariant()}", [], includePublicIps: true);
        Assert.DoesNotContain(invite[6..], text);
        Assert.DoesNotContain(reply[6..].ToLowerInvariant(), text);
        Assert.Contains("JAY2D-<直连码已隐藏>", text);
    }

    // ---- 桥钥匙 ----

    /// <summary>桥钥匙 = HKDF-SHA256(L, salt "jay-direct-v1", info "bridge", 32)：和房间密钥无关，两个房间同一个 L 就是同一把。</summary>
    [Fact]
    public void BridgeKeyComesFromTheLobbyKeyNotTheRoomSecret()
    {
        var expected = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, DirectTestKeys.LobbyKey, expected, "jay-direct-v1"u8, "bridge"u8);
        Assert.Equal(expected, DirectKeys.Bridge(DirectTestKeys.LobbyKey));
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
        var old = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(room.Secret), 32, Encoding.UTF8.GetBytes(room.NetworkName), "amber-direct-v1"u8.ToArray());
        Assert.NotEqual(old, DirectKeys.Bridge(DirectTestKeys.LobbyKey));
        Assert.NotEqual(DirectKeys.Bridge(DirectTestKeys.LobbyKey), DirectKeys.Bridge(new byte[16]));
    }

    // ---- 批准前后的直连桥 ----

    private sealed class DirectRoom : IAsyncDisposable
    {
        public readonly X25519KeyPair HostKey = X25519KeyPair.Generate();
        public readonly RoomCredentials Credentials;
        public readonly MemberTable Members = new();
        public readonly RoomCode HostRoom;
        public readonly DirectSession Host;
        public readonly UdpClient Lobby;

        public DirectRoom(Func<bool>? acceptNewPeers = null)
        {
            Credentials = RoomCredentials.Create(HostKey.PublicKey, 25565, TunnelSettings.Default);
            HostRoom = Credentials.ToHostRoom(Members, "Host");
            Host = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-direct-" + Guid.NewGuid().ToString("N")), HostRoom, true, Options, new DiagnosticLog())
            {
                BridgeKey = DirectTestKeys.Bridge,
                StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
                SpeedTestPortForTest = 0,
                AcceptNewPeers = acceptNewPeers,
            };
            // 代替大厅的 EasyTier：在大厅端口上收（房间的那个由各个测试在房间端口上开）。
            Lobby = new UdpClient(new IPEndPoint(IPAddress.Loopback, Host.LobbyUdpPort!.Value));
        }

        public Task<DirectCode> PrepareAsync() => Host.PrepareAsync();

        public RoomCode Admit(byte[] guestPub, string name) =>
            (Credentials with { Member = Members.Issue(guestPub, name).Key }).ToRoom();

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Lobby.Dispose();
            HostKey.Dispose();
        }
    }

    private static DirectSession Guest() => new(null!, Path.Combine(Path.GetTempPath(), "amber-direct-" + Guid.NewGuid().ToString("N")), null, false, Options, new DiagnosticLog())
    {
        BridgeKey = DirectTestKeys.Bridge,
        StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
        SpeedTestPortForTest = 0,
    };

    /// <summary>从加入方本机“EasyTier”往直连桥发一个包，看它到了房主那边的哪个实例。</summary>
    private static async Task<string?> ArrivesAtAsync(DirectSession guest, UdpClient lobby, UdpClient? room, string text)
    {
        using var et = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var data = Encoding.UTF8.GetBytes(text);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var fromLobby = lobby.ReceiveAsync(wait.Token).AsTask();
        var fromRoom = room?.ReceiveAsync(wait.Token).AsTask() ?? Task.Delay(Timeout.Infinite, wait.Token).ContinueWith(_ => default(UdpReceiveResult));
        for (var i = 0; i < 10 && !fromLobby.IsCompleted && !fromRoom.IsCompleted; i++)
        {
            await et.SendAsync(data, new IPEndPoint(IPAddress.Loopback, guest.Bridge!.LocalPort));
            await Task.WhenAny(fromLobby, fromRoom, Task.Delay(150));
        }
        if (fromLobby.IsCompletedSuccessfully && Encoding.UTF8.GetString(fromLobby.Result.Buffer) == text) return "lobby";
        if (fromRoom.IsCompletedSuccessfully && Encoding.UTF8.GetString(fromRoom.Result.Buffer) == text) return "room";
        return null;
    }

    /// <summary>批准之前，加入方经直连桥的数据只到大厅；证明了成员身份之后才到房间。</summary>
    [Fact]
    public async Task BridgeReachesOnlyTheLobbyUntilTheGuestProvesMembership()
    {
        await using var host = new DirectRoom();
        var invite = (await host.PrepareAsync()) with { IsReply = false, Invite = DirectTestKeys.Invite };
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        await guest.PrepareAsync();
        string? beforeApproval = null;
        var joined = await guest.JoinAsync(invite, async (peers, _) =>
        {
            Assert.Equal([$"udp://127.0.0.1:{guest.Bridge!.LocalPort}"], peers);
            beforeApproval = await ArrivesAtAsync(guest, host.Lobby, room, "knock");
            Assert.False(host.Host.Bridge.Peers.Single().Admitted);
            return host.Admit(RandomNumberGenerator.GetBytes(32), "Steve");
        });
        Assert.True(joined);
        Assert.Equal("lobby", beforeApproval);
        var peer = host.Host.Bridge.Peers.Single();
        Assert.True(peer.Admitted);
        Assert.Same(host.Members.FindGuest(peer.Member!.PublicKey), peer.Member);
        Assert.Equal("room", await ArrivesAtAsync(guest, host.Lobby, room, "play"));
        Assert.True(guest.HasRoom && guest.Room.FromInvite);
    }

    /// <summary>成员钥匙不在房主的成员表里（没批准、伪造的）：房主不认，桥还是只能到大厅。</summary>
    [Fact]
    public async Task UnknownMemberKeyIsRefusedAndTheBridgeStaysOnTheLobby()
    {
        await using var host = new DirectRoom();
        var invite = (await host.PrepareAsync()) with { IsReply = false, Invite = DirectTestKeys.Invite };
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        await guest.PrepareAsync();
        var forged = (host.Credentials with { Member = MemberKey.Create() }).ToRoom();
        await Assert.ThrowsAsync<DirectFailedException>(() => guest.JoinAsync(invite, (_, _) => Task.FromResult<RoomCode?>(forged)));
        Assert.False(host.Host.Bridge.Peers.Single().Admitted);
        Assert.Equal("lobby", await ArrivesAtAsync(guest, host.Lobby, room, "sneak"));
    }

    /// <summary>挑战每条桥一个：别人的证明（换了挑战）不对。</summary>
    [Fact]
    public void ProofIsBoundToTheChallengeAndSession()
    {
        var key = MemberKey.Create();
        var a = DirectKeys.AdmitProof(key, "abcdef0123456789", new byte[16]);
        Assert.Equal(a, DirectKeys.AdmitProof(key, "abcdef0123456789", new byte[16]));
        Assert.NotEqual(a, DirectKeys.AdmitProof(key, "abcdef0123456789", Enumerable.Repeat((byte)1, 16).ToArray()));
        Assert.NotEqual(a, DirectKeys.AdmitProof(key, "0123456789abcdef", new byte[16]));
        Assert.NotEqual(a, DirectKeys.AdmitProof(MemberKey.Create(), "abcdef0123456789", new byte[16]));
    }

    /// <summary>被拒（敲门没通过）：不进房间，也不去证明成员身份。</summary>
    [Fact]
    public async Task DeniedKnockLeavesWithoutARoom()
    {
        await using var host = new DirectRoom();
        var invite = (await host.PrepareAsync()) with { IsReply = false, Invite = DirectTestKeys.Invite };
        await using var guest = Guest();
        await guest.PrepareAsync();
        Assert.False(await guest.JoinAsync(invite, (_, _) => Task.FromResult<RoomCode?>(null)));
        Assert.False(guest.HasRoom);
        Assert.False(host.Host.Bridge!.Peers.Single().Admitted);
    }

    /// <summary>踢出：这个成员的直连桥马上断开，之后他的探测不再接。</summary>
    [Fact]
    public async Task KickDropsTheMembersBridgePeer()
    {
        await using var host = new DirectRoom();
        var invite = (await host.PrepareAsync()) with { IsReply = false, Invite = DirectTestKeys.Invite };
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        await guest.PrepareAsync();
        Assert.True(await guest.JoinAsync(invite, (_, _) => Task.FromResult<RoomCode?>(host.Admit(RandomNumberGenerator.GetBytes(32), "Steve"))));
        var peer = host.Host.Bridge.Peers.Single();
        host.Members.Kick(peer.Member!, null);
        Assert.Empty(host.Host.Bridge.Peers);
        // 加入方的心跳停了会补发探测：踢出的地址不再接。
        await Task.Delay(DirectBridge.ReprobeAfter + TimeSpan.FromSeconds(1.5));
        Assert.Empty(host.Host.Bridge.Peers);
        Assert.Null(await ArrivesAtAsync(guest, host.Lobby, room, "after-kick"));
    }

    /// <summary>
    /// 没有待用邀请时房主的直连桥不接新人：桥钥匙（由 L 派生）整个房间都不换，拿着旧码的人连大厅都到不了。
    /// 有了新邀请又能接。
    /// </summary>
    [Fact]
    public async Task ClosedRoomRefusesNewBridgePeers()
    {
        var open = false;
        await using var host = new DirectRoom(() => Volatile.Read(ref open));
        var code = await host.PrepareAsync();
        await using var guest = Guest();
        await guest.PrepareAsync();
        Assert.Null(await guest.Bridge!.PunchAsync(code.Candidates, code.Nat, TimeSpan.FromSeconds(3)));
        Assert.Empty(host.Host.Bridge!.Peers);
        Volatile.Write(ref open, true);
        Assert.NotNull(await guest.Bridge.PunchAsync(code.Candidates, code.Nat, TimeSpan.FromSeconds(5)));
        Assert.Single(host.Host.Bridge.Peers);
    }

    /// <summary>邀请用完了（不再接新人），已经证明过身份的直连成员照常：还连着房间。</summary>
    [Fact]
    public async Task AdmittedMembersKeepWorkingAfterInvitesRunOut()
    {
        var open = true;
        await using var host = new DirectRoom(() => Volatile.Read(ref open));
        var invite = (await host.PrepareAsync()) with { IsReply = false, Invite = DirectTestKeys.Invite };
        using var room = new UdpClient(new IPEndPoint(IPAddress.Loopback, host.Host.Bridge!.LocalPort));
        await using var guest = Guest();
        await guest.PrepareAsync();
        Assert.True(await guest.JoinAsync(invite, (_, _) => Task.FromResult<RoomCode?>(host.Admit(RandomNumberGenerator.GetBytes(32), "Steve"))));
        Volatile.Write(ref open, false);
        Assert.True(host.Host.Bridge.Peers.Single().Admitted);
        Assert.Equal("room", await ArrivesAtAsync(guest, host.Lobby, room, "still-here"));
    }

    /// <summary>踢人换网络：留下的直连成员换到新房间，直连桥不动（还是接在房间上）。</summary>
    [Fact]
    public async Task RekeyKeepsTheBridgeAndSwitchesTheRoom()
    {
        await using var host = new DirectRoom();
        var invite = (await host.PrepareAsync()) with { IsReply = false, Invite = DirectTestKeys.Invite };
        await using var guest = Guest();
        await guest.PrepareAsync();
        Assert.True(await guest.JoinAsync(invite, (_, _) => Task.FromResult<RoomCode?>(host.Admit(RandomNumberGenerator.GetBytes(32), "Steve"))));
        var next = guest.Room.Rekeyed(host.Credentials.Rotate().ToRekey());
        await guest.SwitchRoomAsync(next);
        Assert.Equal(next.Secret, guest.EasyTier!.Options.Room.Secret);
        Assert.Equal(2, guest.Room.Epoch);
        var hostNext = host.HostRoom.Rekeyed(new RoomRekey(next.NetworkName, next.Secret, 2));
        await host.Host.SwitchRoomAsync(hostNext);
        Assert.Equal(hostNext.Secret, host.Host.EasyTier!.Options.Room.Secret);
        Assert.True(host.Host.Bridge!.Peers.Single().Admitted);
        Assert.Single(guest.Bridge!.Peers);
    }

    // ---- 直连邀请里的 JAY2 邀请照样按普通邀请的规矩：令牌、一次、过期、批准 ----

    private sealed class Knocking : IAsyncDisposable
    {
        public DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        public readonly InviteBook Book;
        public readonly X25519KeyPair Host = X25519KeyPair.Generate();
        public readonly MemberTable Members = new();
        public readonly KnockServer Server;
        public readonly RoomCredentials Credentials;

        public Knocking(Func<KnockRequest, CancellationToken, Task<bool>>? approve = null)
        {
            Book = new InviteBook(() => Now) { KeepLobbyKey = true };
            Credentials = RoomCredentials.Create(Host.PublicKey, 25565, TunnelSettings.Default);
            Server = new KnockServer(Book, Host, () => Credentials, _ => false, approve ?? ((_, _) => Task.FromResult(true)), IPAddress.Loopback, 0,
                limits: new KnockLimits { HandshakeTimeout = TimeSpan.FromSeconds(3), DecisionTimeout = TimeSpan.FromMilliseconds(400) }, members: Members);
        }

        /// <summary>按直连邀请码里带着的邀请敲门（码先编码再解析，和真的一样）。</summary>
        public async Task<KnockOutcome> KnockAsync(DirectCode code, X25519KeyPair who)
        {
            Assert.True(DirectCode.TryParse(code.Encode(), out var parsed, out _));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Server.Port);
            return await KnockClient.KnockAsync(client.GetStream(), parsed.Invite!, who, "Steve", "0.6.0", decisionTimeout: TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Host.Dispose();
        }
    }

    private static DirectCode CodeFor(PendingInvite invite) => new(false, "abcdef0123456789", NatKind.Cone, SomeAddresses, invite.Invite);

    private static async Task<bool> RejectedAsync(Func<Task<KnockOutcome>> knock)
    {
        try { return !(await knock()).Approved; }
        catch (IOException) { return true; }
    }

    [Fact]
    public async Task DirectInviteIsSingleUse()
    {
        await using var k = new Knocking();
        var code = CodeFor(k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default));
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        Assert.True((await k.KnockAsync(code, a)).Approved);
        Assert.True(await RejectedAsync(() => k.KnockAsync(code, b)));
        Assert.Null(k.Members.FindGuest(b.PublicKey));
    }

    [Fact]
    public async Task ExpiredDirectInviteFails()
    {
        await using var k = new Knocking();
        var code = CodeFor(k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default));
        k.Now += TimeSpan.FromMinutes(11);
        using var a = X25519KeyPair.Generate();
        Assert.True(await RejectedAsync(() => k.KnockAsync(code, a)));
        Assert.Empty(k.Members.All);
    }

    [Fact]
    public async Task WrongTokenFails()
    {
        await using var k = new Knocking();
        var pending = k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default);
        var token = (byte[])pending.Invite.Token.Clone();
        token[0] ^= 1;
        var forged = CodeFor(pending) with { Invite = pending.Invite with { Token = token } };
        using var a = X25519KeyPair.Generate();
        Assert.True(await RejectedAsync(() => k.KnockAsync(forged, a)));
        Assert.Empty(k.Members.All);
        Assert.Equal(1, pending.UsesLeft);
    }

    [Fact]
    public async Task DirectInviteNeedsTheHostsApproval()
    {
        await using var k = new Knocking((_, _) => Task.FromResult(false));
        var code = CodeFor(k.Book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default));
        using var a = X25519KeyPair.Generate();
        var outcome = await k.KnockAsync(code, a);
        Assert.False(outcome.Approved);
        Assert.Null(outcome.Credentials);
        Assert.Empty(k.Members.All);
    }

    [Fact]
    public async Task BannedGuestCannotUseADirectInvite()
    {
        await using var k = new Knocking();
        using var a = X25519KeyPair.Generate();
        k.Members.Ban(a.PublicKey, "Steve");
        var code = CodeFor(k.Book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default));
        Assert.True(await RejectedAsync(() => k.KnockAsync(code, a)));
        Assert.Empty(k.Members.All);
    }

    /// <summary>直连房间的邀请簿：邀请都用完后大厅密钥不换（桥钥匙由它派生）。</summary>
    [Fact]
    public void DirectBookKeepsItsLobbyKey()
    {
        var book = new InviteBook { KeepLobbyKey = true };
        var key = book.LobbyKeyCopy();
        var invite = book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        Assert.Equal(key, invite.Invite.LobbyKey);
        book.Revoke(invite);
        Assert.Equal(key, book.LobbyKeyCopy());
        Assert.Equal(key, book.Create(1, null, false, TunnelSettings.Default).Invite.LobbyKey);
        // 普通房间的邀请簿照旧：用完就换。
        var normal = new InviteBook();
        var first = normal.Create(1, null, false, TunnelSettings.Default);
        normal.Revoke(first);
        Assert.NotEqual(first.Invite.LobbyKey, normal.Create(1, null, false, TunnelSettings.Default).Invite.LobbyKey);
    }

    /// <summary>直连房主的大厅 EasyTier 只在本机回环上监听（给直连桥用）；普通大厅不监听。</summary>
    [Fact]
    public void DirectLobbyListensOnLoopbackOnly()
    {
        var direct = LobbyArgs.Build(new LobbyOptions("jay-l-x", "s", true, [], 15888, null, 40123));
        Assert.Contains("udp://127.0.0.1:40123", direct);
        Assert.DoesNotContain("--no-listener", direct);
        var normal = LobbyArgs.Build(new LobbyOptions("jay-l-x", "s", true, [], 15888));
        Assert.Contains("--no-listener", normal);
        Assert.DoesNotContain("--listeners", normal);
    }
}
