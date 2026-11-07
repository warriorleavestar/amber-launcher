using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

public class TransportPrimitiveTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void X25519_Rfc7748Vector()
    {
        // RFC 7748 第 6.1 节。
        using var alice = X25519KeyPair.Import(H("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"), H("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));
        using var bob = X25519KeyPair.Import(H("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"), H("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"));
        var expected = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
        Assert.Equal(expected, Convert.ToHexStringLower(alice.Dh(bob.PublicKey)));
        Assert.Equal(expected, Convert.ToHexStringLower(bob.Dh(alice.PublicKey)));
        // 小阶点（全零公钥）要被拒绝。
        Assert.ThrowsAny<CryptographicException>(() => alice.Dh(new byte[32]));
    }

    [Fact]
    public void ChaCha20Poly1305_Rfc8439Vector()
    {
        // RFC 8439 第 2.8.2 节。
        var key = H("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var nonce = H("070000004041424344454647");
        var ad = H("50515253c0c1c2c3c4c5c6c7");
        var plain = System.Text.Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aead = new ChaCha20Poly1305(key);
        aead.Encrypt(nonce, plain, cipher, tag, ad);
        Assert.Equal("1ae10b594f09e26a7e902ecbd0600691", Convert.ToHexStringLower(tag));
        Assert.StartsWith("d31a8d34648e60db7b86afbc53ef7ec2", Convert.ToHexStringLower(cipher));
    }

    [Fact]
    public void NoiseHkdf_MatchesRfc5869()
    {
        // Noise 的 HKDF 就是 RFC 5869（salt = 链密钥，info 为空）；和系统的 HKDF 对一下。
        var ck = RandomNumberGenerator.GetBytes(32);
        var ikm = RandomNumberGenerator.GetBytes(32);
        var (o1, o2, o3) = SymmetricState.Hkdf(ck, ikm, 3);
        var reference = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 96, ck, []);
        Assert.Equal(reference, (byte[])[.. o1, .. o2, .. o3!]);
    }

    [Fact]
    public void CipherState_NonceAndRekey()
    {
        var a = new CipherState();
        var b = new CipherState();
        var key = RandomNumberGenerator.GetBytes(32);
        a.InitializeKey(key);
        b.InitializeKey((byte[])key.Clone());
        var c1 = a.EncryptWithAd([], "hello"u8);
        Assert.Equal("hello"u8.ToArray(), b.DecryptWithAd([], c1));
        // 同一明文两次加密结果不同（nonce 前进）。
        Assert.NotEqual(c1, a.EncryptWithAd([], "hello"u8));
        Assert.Equal(2UL, a.Nonce);
    }

    [Fact]
    public void CipherState_RejectsTamperAndReplay()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var a = new CipherState();
        var b = new CipherState();
        a.InitializeKey(key);
        b.InitializeKey((byte[])key.Clone());
        var c = a.EncryptWithAd([], "data"u8);
        var tampered = (byte[])c.Clone();
        tampered[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => b.DecryptWithAd([], tampered));
        Assert.Equal("data"u8.ToArray(), b.DecryptWithAd([], c));
        // 重放同一条：接收方 nonce 已经前进，解不开。
        Assert.ThrowsAny<CryptographicException>(() => b.DecryptWithAd([], c));
        // 换钥后两边同步换才解得开。
        a.Rekey();
        var after = a.EncryptWithAd([], "z"u8);
        var noRekey = new CipherState();
        noRekey.InitializeKey((byte[])key.Clone());
        noRekey.SetNonce(1);
        Assert.ThrowsAny<CryptographicException>(() => noRekey.DecryptWithAd([], after));
        b.Rekey();
        Assert.Equal("z"u8.ToArray(), b.DecryptWithAd([], after));
    }
}

public class NoiseHandshakeTests
{
    private static readonly byte[] Psk = RandomNumberGenerator.GetBytes(32);

    private static (CipherState, CipherState, CipherState, CipherState, byte[], byte[]) Run(NoisePattern pattern, byte[] psk1, byte[] psk2, string prologue1 = "p", string prologue2 = "p")
    {
        using var i = new NoiseHandshake(pattern, true, System.Text.Encoding.ASCII.GetBytes(prologue1), psk1);
        using var r = new NoiseHandshake(pattern, false, System.Text.Encoding.ASCII.GetBytes(prologue2), psk2);
        var m1 = i.WriteMessage("req"u8);
        Assert.Equal("req"u8.ToArray(), r.ReadMessage(m1));
        var m2 = r.WriteMessage("ok"u8);
        Assert.Equal("ok"u8.ToArray(), i.ReadMessage(m2));
        Assert.Equal(i.HandshakeHash, r.HandshakeHash);
        var (iSend, iReceive) = i.Split();
        var (rSend, rReceive) = r.Split();
        return (iSend, iReceive, rSend, rReceive, m1, m2);
    }

    [Theory]
    [InlineData(NoisePattern.NNpsk0)]
    [InlineData(NoisePattern.NNhfspsk0)]
    public void HandshakeAndTransport(NoisePattern pattern)
    {
        if (pattern == NoisePattern.NNhfspsk0 && !NoiseHandshake.HybridSupported) return;
        var (iSend, iReceive, rSend, rReceive, m1, m2) = Run(pattern, Psk, (byte[])Psk.Clone());
        Assert.Equal("a"u8.ToArray(), rReceive.DecryptWithAd([], iSend.EncryptWithAd([], "a"u8)));
        Assert.Equal("b"u8.ToArray(), iReceive.DecryptWithAd([], rSend.EncryptWithAd([], "b"u8)));
        // 握手消息大小：NN 是 32 字节临时公钥 + 加密的 payload；混合模式再加 ML-KEM 公钥 / 密文。
        if (pattern == NoisePattern.NNpsk0) Assert.Equal((32 + 3 + 16, 32 + 2 + 16), (m1.Length, m2.Length));
        else Assert.Equal((32 + 1184 + 16 + 3 + 16, 32 + 1088 + 16 + 2 + 16), (m1.Length, m2.Length));
    }

    [Theory]
    [InlineData(NoisePattern.NNpsk0)]
    [InlineData(NoisePattern.NNhfspsk0)]
    public void WrongPskOrPrologueFails(NoisePattern pattern)
    {
        if (pattern == NoisePattern.NNhfspsk0 && !NoiseHandshake.HybridSupported) return;
        // 不知道房间码（psk 不同）的人在第一条消息就失败。
        Assert.ThrowsAny<CryptographicException>(() => Run(pattern, Psk, RandomNumberGenerator.GetBytes(32)));
        // 两边模式不一样（prologue 不同）也失败，不会悄悄用弱的那种。
        Assert.ThrowsAny<CryptographicException>(() => Run(pattern, Psk, (byte[])Psk.Clone(), "mode1", "mode2"));
    }

    [Fact]
    public void TamperedHandshakeFails()
    {
        using var i = new NoiseHandshake(NoisePattern.NNpsk0, true, [], Psk);
        using var r = new NoiseHandshake(NoisePattern.NNpsk0, false, [], (byte[])Psk.Clone());
        var m1 = i.WriteMessage("req"u8);
        m1[5] ^= 0x40;   // 改临时公钥
        Assert.ThrowsAny<CryptographicException>(() => r.ReadMessage(m1));
    }

    [Fact]
    public void EphemeralKeysGiveForwardSecrecy()
    {
        // 同一个 psk 两次握手得到不同的会话密钥。
        var (a, _, _, _, _, _) = Run(NoisePattern.NNpsk0, Psk, (byte[])Psk.Clone());
        var (b, _, _, _, _, _) = Run(NoisePattern.NNpsk0, Psk, (byte[])Psk.Clone());
        Assert.NotEqual(a.EncryptWithAd([], new byte[16]), b.EncryptWithAd([], new byte[16]));
    }
}

public class TunnelTests
{
    private static readonly RoomCode Room = RoomCode.Create(25565);

    /// <summary>本机回环上建一对 TCP 连接。</summary>
    private static async Task<(NetworkStream Client, NetworkStream Server, TcpClient A, TcpClient B)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var b = await accept;
        listener.Stop();
        return (a.GetStream(), b.GetStream(), a, b);
    }

    public static TheoryData<TransportMode, bool> Modes()
    {
        var data = new TheoryData<TransportMode, bool>();
        foreach (var mode in new[] { TransportMode.Minecraft, TransportMode.High, TransportMode.Extreme })
            foreach (var disguise in new[] { false, true }) data.Add(mode, disguise);
        return data;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RoundTrip_AllModes(TransportMode mode, bool disguise)
    {
        if (mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported) return;
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, Room, mode, disguise);
            await using var client = await TunnelHandshake.ConnectAsync(c, Room, mode, disguise, TunnelService.Chat);
            var accepted = await accept;
            Assert.NotNull(accepted);
            Assert.Equal(TunnelService.Chat, accepted.Value.Service);
            await using var server = accepted.Value.Stream;

            // 大小不一的数据（包括超过一条记录上限的）两个方向都原样到达。
            foreach (var size in new[] { 1, 100, 5000, SecureStream.MaxData, SecureStream.MaxData + 1, 200_000 })
            {
                var data = RandomNumberGenerator.GetBytes(size);
                var got = new byte[size];
                // 写和读同时进行：数据比 TCP 缓冲区大时先写完再读会卡住。
                var write = client.WriteAsync(data).AsTask();
                Assert.True(await SecureStream.ReadExactAsync(server, got, default));
                await write;
                Assert.Equal(data, got);
                write = server.WriteAsync(data).AsTask();
                Assert.True(await SecureStream.ReadExactAsync(client, got, default));
                await write;
                Assert.Equal(data, got);
            }
        }
    }

    [Fact]
    public async Task WrongRoomCodeIsRejected()
    {
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, Room, TransportMode.High, false);
            var connect = TunnelHandshake.ConnectAsync(c, RoomCode.Create(25565), TransportMode.High, false, TunnelService.Game);
            await Assert.ThrowsAnyAsync<CryptographicException>(async () => await accept);
            b.Close();
            await Assert.ThrowsAnyAsync<Exception>(async () => await connect);
        }
    }

    [Fact]
    public async Task ModeMismatchIsRejected()
    {
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, Room, TransportMode.High, false);
            var connect = TunnelHandshake.ConnectAsync(c, Room, TransportMode.Minecraft, false, TunnelService.Game);
            await Assert.ThrowsAnyAsync<CryptographicException>(async () => await accept);
            b.Close();
            await Assert.ThrowsAnyAsync<Exception>(async () => await connect);
        }
    }

    [Fact]
    public async Task TamperedRecordBreaksTheConnection()
    {
        // 中间人在两条 TCP 之间转发，改掉客户端第一条数据记录密文里的一个字节：服务端必须报认证失败，而不是收到改过的数据。
        var (c, mitmIn, a, b) = await PairAsync();
        var (mitmOut, s, a2, b2) = await PairAsync();
        using (a) using (b) using (a2) using (b2)
        {
            // 握手消息 1 的帧 = 2 字节长度 + 32 字节临时公钥 + 加密的 1 字节 payload（17）= 51；再往后是记录头 2 字节 + 密文。
            const int tamperAt = 51 + 2 + 5;
            var up = Task.Run(async () =>
            {
                var buf = new byte[1];
                var count = 0;
                try
                {
                    while (await mitmIn.ReadAsync(buf) == 1)
                    {
                        if (++count == tamperAt) buf[0] ^= 0x01;
                        await mitmOut.WriteAsync(buf);
                    }
                }
                catch (IOException) { }
            });
            var down = Task.Run(async () => { try { await mitmOut.CopyToAsync(mitmIn); } catch (IOException) { } catch (ObjectDisposedException) { } });

            var accept = TunnelHandshake.AcceptAsync(s, Room, TransportMode.High, false);
            await using var client = await TunnelHandshake.ConnectAsync(c, Room, TransportMode.High, false, TunnelService.Game);
            var accepted = await accept;
            Assert.NotNull(accepted);
            await using var server = accepted.Value.Stream;
            await client.WriteAsync("secret game data"u8.ToArray());
            await Assert.ThrowsAnyAsync<CryptographicException>(async () => await server.ReadAsync(new byte[64]));
        }
    }

    [Fact]
    public async Task RekeyPaddingAndCover()
    {
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            using var i = new NoiseHandshake(NoisePattern.NNpsk0, true, [], RandomNumberGenerator.GetBytes(32));
            var psk = (byte[])typeof(NoiseHandshake).GetField("_psk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(i)!;
            using var r = new NoiseHandshake(NoisePattern.NNpsk0, false, [], (byte[])psk.Clone());
            r.ReadMessage(i.WriteMessage([]));
            i.ReadMessage(r.WriteMessage([]));
            var (iSend, iReceive) = i.Split();
            var (rSend, rReceive) = r.Split();
            // 每 3 条记录换钥、填充到档位、空闲 30 毫秒发掩护记录。
            var profile = new TransportProfile(TransportMode.Extreme, NoisePattern.NNpsk0, 3, TimeSpan.FromHours(1), 1, [256, 1024, SecureStream.MaxPlaintext], TimeSpan.FromMilliseconds(30));
            await using var client = new SecureStream(c, iSend, iReceive, profile);
            await using var server = new SecureStream(s, rSend, rReceive, profile with { CoverTrafficIdle = null });

            for (var n = 0; n < 10; n++)
            {
                var data = RandomNumberGenerator.GetBytes(10 + n * 100);
                await client.WriteAsync(data);
                var got = new byte[data.Length];
                Assert.True(await SecureStream.ReadExactAsync(server, got, default));
                Assert.Equal(data, got);
            }
            Assert.True(client.Rekeys >= 3);
            // 10 字节的数据也填充到 256 档。
            Assert.Equal(256, client.PaddedLength(3 + 10));
            Assert.Equal(1024, client.PaddedLength(3 + 300));
            // 掩护记录靠计时器发，CI 机器忙时可能晚到；等到发出为止（最多 10 秒），不靠固定延时。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (client.CoverRecordsSent == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(client.CoverRecordsSent > 0, "空闲时应该发掩护记录");
            // 掩护记录在接收方被丢弃，之后的真实数据照样对得上。
            await client.WriteAsync("after cover"u8.ToArray());
            var tail = new byte[11];
            Assert.True(await SecureStream.ReadExactAsync(server, tail, default));
            Assert.Equal("after cover"u8.ToArray(), tail);
        }
    }

    [Fact]
    public async Task Disguise_AnswersServerListPing()
    {
        // 伪装模式下，有人（比如服务器列表）来查询状态，回答得像个普通服务器。
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, Room, TransportMode.High, true);
            var hs = new McDisguise.PacketWriter().VarInt(0).VarInt(767).String("example.org").UShort(25565).VarInt(1).ToPacket();
            var req = new McDisguise.PacketWriter().VarInt(0).ToPacket();
            var ping = new McDisguise.PacketWriter().VarInt(1).Long(42).ToPacket();
            await c.WriteAsync((byte[])[.. hs, .. req, .. ping]);
            var status = new McDisguise.PacketReader(await McDisguise.ReadPacketAsync(c, default));
            Assert.Equal(0, status.VarInt());
            Assert.Contains("\"protocol\":767", status.String(32767));
            var pong = new McDisguise.PacketReader(await McDisguise.ReadPacketAsync(c, default));
            Assert.Equal((1, 42L), (pong.VarInt(), pong.Long()));
            Assert.Null(await accept);
        }
    }

    [Fact]
    public async Task Disguise_LooksLikeMinecraftLogin()
    {
        // 抓客户端发出的前几个字节：应该是合法的 MC Handshake（下一状态 = 登录）和 Login Start。
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var connect = TunnelHandshake.ConnectAsync(c, Room, TransportMode.High, true, TunnelService.Game);
            var handshake = new McDisguise.PacketReader(await McDisguise.ReadPacketAsync(s, default));
            Assert.Equal(0, handshake.VarInt());
            Assert.Equal(767, handshake.VarInt());
            handshake.String(255);
            handshake.UShort();
            Assert.Equal(2, handshake.VarInt());
            var login = new McDisguise.PacketReader(await McDisguise.ReadPacketAsync(s, default));
            Assert.Equal(0, login.VarInt());
            Assert.StartsWith("Player", login.String(16));
            b.Close();
            await Assert.ThrowsAnyAsync<Exception>(async () => await connect);
        }
    }
}

public class ForwardingTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.233.144.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    public void OnlyPublicDestinationsAreForwarded(string address, bool allowed) =>
        Assert.Equal(allowed, Socks5.IsPublic(System.Net.IPAddress.Parse(address)));

    [Fact]
    public async Task RelayRewritesHandshakeAndKeepsForgeMarker()
    {
        // 假的 MC 服务器：读第一个包，把里面的地址发回来。
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
        var seen = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using var c = await server.AcceptTcpClientAsync();
            var p = new McDisguise.PacketReader(await McDisguise.ReadPacketAsync(c.GetStream(), default));
            p.VarInt();
            p.VarInt();
            var address = p.String(255);
            var port = p.UShort();
            seen.SetResult($"{address}|{port}");
            await c.GetStream().WriteAsync("ok"u8.ToArray());
        });

        // 出口这边的 SOCKS5（测试里允许连回环），经一对内存里的 TCP 连接接到加入方的转发。
        var exit = new TcpListener(IPAddress.Loopback, 0);
        exit.Start();
        var exitPort = ((IPEndPoint)exit.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await exit.AcceptTcpClientAsync();
            await Socks5.ServeAsync(c.GetStream(), default, allow: _ => true);
        });
        Func<CancellationToken, Task<Stream>> open = async ct =>
        {
            var t = new TcpClient();
            await t.ConnectAsync(IPAddress.Loopback, exitPort, ct);
            return t.GetStream();
        };
        await using var relay = new TunnelClient(0, open, prepare: McHandshakeRewriter.ViaSocks("127.0.0.1", serverPort));

        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, relay.Port);
        var hs = new McDisguise.PacketWriter().VarInt(0).VarInt(767).String("127.0.0.1\0FML3\0").UShort((ushort)relay.Port).VarInt(2).ToPacket();
        await game.GetStream().WriteAsync(hs);
        Assert.Equal($"127.0.0.1\0FML3\0|{serverPort}", await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var reply = new byte[2];
        Assert.True(await SecureStream.ReadExactAsync(game.GetStream(), reply, default));
        Assert.Equal("ok"u8.ToArray(), reply);
        server.Stop();
        exit.Stop();
    }

    [Fact]
    public async Task SocksRefusesPrivateTargetsWithReason()
    {
        var exit = new TcpListener(IPAddress.Loopback, 0);
        exit.Start();
        var port = ((IPEndPoint)exit.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await exit.AcceptTcpClientAsync();
            await Socks5.ServeAsync(c.GetStream(), default);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client.GetStream(), "192.168.0.1", 80, default));
        Assert.Contains("局域网", e.Message);
        exit.Stop();
    }

    [Fact]
    public void RoomCodeCarriesTransport()
    {
        var room = RoomCode.Create(25565) with { Tunnel = new TunnelSettings(TransportMode.Extreme, true) };
        Assert.StartsWith("AMB-T3D-", room.ToString());
        Assert.EndsWith("-25565", room.ToString());
        Assert.True(RoomCode.TryParse("朋友发来的：" + room + " 快来", out var parsed));
        Assert.Equal(room, parsed);
        // 通用模式的房间码和以前完全一样，老版本也能用。
        var plain = RoomCode.Create(25565);
        Assert.Matches("^AMB-[0-9A-Z]{8}-[0-9A-Z]{12}-25565$", plain.ToString());
        Assert.True(RoomCode.TryParse(plain.ToString(), out var p2));
        Assert.Equal(TransportMode.General, p2.Tunnel.Mode);
    }
}

public class ExitPolicyTests
{
    private static ExitPolicy Policy() => new(
        [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("2400:1a00:b1:4e::25"), IPAddress.Parse("2400:1a00:b1:4e:a1b2:c3d4:e5f6:7788"), IPAddress.Parse("10.233.144.7")],
        [(IPAddress.Parse("192.168.1.0"), 24), (IPAddress.Parse("2400:1a00:b1:4e::"), 64), (IPAddress.Parse("10.233.144.0"), 24)]);

    [Theory]
    // 本机自己的公网 IPv6（固定的和临时的）、同一网段里的别的设备（路由器 ::1、家里别的电脑）
    [InlineData("2400:1a00:b1:4e::25")]
    [InlineData("2400:1a00:b1:4e:a1b2:c3d4:e5f6:7788")]
    [InlineData("2400:1a00:b1:4e::1")]
    [InlineData("2400:1a00:b1:4e:1111:2222:3333:4444")]
    // 私有、回环、链路本地、CGNAT、组播、广播、ULA、EasyTier 虚拟网段
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.100.1.1")]
    [InlineData("10.233.144.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    // 嵌着私有 IPv4 的 IPv6 写法
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::192.168.1.1")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::192.168.1.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b:1::c0a8:101")]
    [InlineData("2002:c0a8:0101::1")]
    [InlineData("2002:7f00:0001::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3f57:fefe")]   // Teredo，客户端 IPv4 取反后是 192.168.1.1
    // 文档、基准测试网段
    [InlineData("2001:db8::1")]
    [InlineData("198.18.0.1")]
    public void DeniesOwnHostAndLocalNetworks(string address) => Assert.False(Policy().Allows(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:0808:0808::1")]
    public void AllowsOtherPublicAddresses(string address) => Assert.True(Policy().Allows(IPAddress.Parse(address)));

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void GameOnlyExitRefusesIPv6(string address) => Assert.False(Policy().Allows(IPAddress.Parse(address)));

    [Fact]
    public void DeniesRouterPublicIpOnceKnown()
    {
        var policy = Policy();
        var wan = IPAddress.Parse("203.1.2.3");
        Assert.True(policy.Allows(wan));
        policy.AddPublicAddress(wan);
        Assert.False(policy.Allows(wan));
        Assert.False(policy.Allows(IPAddress.Parse("::ffff:203.1.2.3")));
        Assert.False(policy.Allows(IPAddress.Parse("2002:cb01:0203::1")));
    }

    [Fact]
    public void RealMachineAddressesAreDenied()
    {
        // 这台电脑真实网卡上的每个地址都必须被拒绝。
        using var policy = new ExitPolicy();
        policy.SetReady(true);
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                Assert.False(policy.Allows(u.Address), u.Address.ToString());
    }

    [Fact]
    public async Task SocksDeniesHostnameThatResolvesToLocal()
    {
        // 主机名在出口解析：localhost 解析出的回环地址被拒绝，不会因为是名字就放行。
        var exit = new TcpListener(IPAddress.Loopback, 0);
        exit.Start();
        var port = ((IPEndPoint)exit.LocalEndpoint).Port;
        using var policy = Policy();
        _ = Task.Run(async () =>
        {
            using var c = await exit.AcceptTcpClientAsync();
            await Socks5.ServeAsync(c.GetStream(), default, allow: policy.Allows);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client.GetStream(), "localhost", 80, default));
        exit.Stop();
    }
}

public class ExitPortTests
{
    [Theory]
    [InlineData(25, false)]
    [InlineData(465, false)]
    [InlineData(587, false)]
    [InlineData(0, false)]
    [InlineData(443, true)]
    [InlineData(80, true)]
    [InlineData(25565, true)]
    [InlineData(19132, true)]
    public void SmtpPortsAreDenied(int port, bool allowed) => Assert.Equal(allowed, ExitPolicy.AllowsPort(port));

    [Fact]
    public async Task SocksRefusesSmtpEvenToPublicHost()
    {
        var exit = new TcpListener(IPAddress.Loopback, 0);
        exit.Start();
        var port = ((IPEndPoint)exit.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await exit.AcceptTcpClientAsync();
            await Socks5.ServeAsync(c.GetStream(), default, allow: _ => true);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client.GetStream(), "8.8.8.8", 25, default));
        Assert.Contains("发邮件", e.Message);
        exit.Stop();
    }
}
