using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Noise_XXpsk3_25519_ChaChaPoly_SHA256：对照 cacophony 的官方测试向量（握手三条消息、握手哈希、之后的传输消息），
/// 以及 psk 不对、消息被改、对方给全零公钥时握手失败。
/// </summary>
public sealed class NoiseXxTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    // cacophony vectors/cacophony.txt 里 protocol_name = Noise_XXpsk3_25519_ChaChaPoly_SHA256 的那一条。
    private static readonly byte[] Prologue = H("4a6f686e2047616c74");
    private static readonly byte[] Psk = H("54686973206973206d7920417573747269616e20706572737065637469766521");
    private const string InitStatic = "e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1";
    private const string InitEphemeral = "893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a";
    private const string RespStatic = "4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893";
    private const string RespEphemeral = "bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b";
    private const string HandshakeHash = "a477edf6a131bbdb54707f6ea30eab6cd935d9b560f0e5fd1f053a95a99669fb";

    private static readonly (string Payload, string Ciphertext)[] Messages =
    [
        ("4c756477696720766f6e204d69736573", "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c7944c9f5ff0e8079630cb7e270c20bbf480821b77a384a645c71a2fd9b3db1c16a5f"),
        ("4d757272617920526f746862617264", "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f144808843b123def17f71e6ae8e57e0e1dec5949c5f7415c6f33517398747d821a06dc23ad430aa1fd7381d46195c378a819fd574425462cbb2d4ca339e738a0b7001dc91423fbf55a99af0c6f1df21012ceb2f"),
        ("462e20412e20486179656b", "52187316111b118d4c060364f7b975dc0809b2590779aff2d63113c564f11744493384db7bf32d5ae6686df6ab06d508d2e07caaf1d6afc010b978735fc78900e71ae1d314130d042e729a"),
        ("4361726c204d656e676572", "eaedc672d4c21e0e2955758756fb98f194c4e90d5deb5b6cf30b27"),
        ("4a65616e2d426170746973746520536179", "522d543c5fe799d09a3d9da7ff54d0dc03c8af1dc7751d2ff708339d2290943e98"),
        ("457567656e2042f6686d20766f6e2042617765726b", "7d4e2c3873eef6a213b04e72f9df60a91666072d3544c5d96c34a09e2329b5030bee796741"),
    ];

    // Noise_XXpsk0：cacophony 和 snow 里都没有官方向量。这一组用 python noiseprotocol 生成（先确认它能逐字节重现上面官方的 XXpsk3 向量），
    // 密钥、prologue、psk、payload 都和上面那组相同。
    private const string HandshakeHashPsk0 = "0793e6b2d2fed61d803e0d9de8a4b961436e896194246a784d30d1bb1fd42e54";
    private static readonly string[] CiphertextsPsk0 =
    [
        "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c794444e557af4bb1c735a4948bdd9aedbb4b3db9a8aed79bc6517b05eca117a755c3",
        "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f14480884309813c929a2ee793a6909502f23492a90b088e4acc71ae6ca49464e67d97fdf404b94bc46aad47491994c66e9a8832f1701cdc1f47fc2ba565695638f005ca2e7b9a569a4c241a57879a0779d8bdbd",
        "591674f2df5cc5e0a9b3ddea86bafb1219b36345c12aa4bd0526a9f727fe2e2a14efe7e0a7b7710d042006afd97e7cf58a3efe9041dba7cf07099b7c2345135e8d51254f6f742730eb7755",
        "65bc9bce988c8c9becae660cd4fdd43f1c0dfaa40a2ac2322a09ae",
        "4cc7db3b57b01e80a8f684fa6eb8d043ae32f38aa598cf8bbe474aa9ffc4dfce9b",
        "2ebc31fbd26efafc391423fd69aa09159c5c15d9327ebae0e4afaffac55baa42fd9b742f91",
    ];

    [Fact]
    public void MatchesCacophonyVector() => RunVector(NoisePattern.XXpsk3, HandshakeHash, [.. Messages.Select(m => m.Ciphertext)]);

    [Fact]
    public void MatchesXxPsk0Vector() => RunVector(NoisePattern.XXpsk0, HandshakeHashPsk0, CiphertextsPsk0);

    private static void RunVector(NoisePattern pattern, string handshakeHash, string[] ciphertexts)
    {
        using var initStatic = X25519KeyPair.FromPrivateKey(H(InitStatic));
        using var respStatic = X25519KeyPair.FromPrivateKey(H(RespStatic));
        using var init = new NoiseHandshake(pattern, true, Prologue, Psk, initStatic) { FixedEphemeral = X25519KeyPair.FromPrivateKey(H(InitEphemeral)) };
        using var resp = new NoiseHandshake(pattern, false, Prologue, Psk, respStatic) { FixedEphemeral = X25519KeyPair.FromPrivateKey(H(RespEphemeral)) };

        // 握手：-> e   <- e, ee, s, es   -> s, se, psk，每条都带 payload。
        for (var i = 0; i < 3; i++)
        {
            var (writer, reader) = i % 2 == 0 ? (init, resp) : (resp, init);
            var message = writer.WriteMessage(H(Messages[i].Payload));
            Assert.Equal(ciphertexts[i], Convert.ToHexStringLower(message));
            Assert.Equal(Messages[i].Payload, Convert.ToHexStringLower(reader.ReadMessage(message)));
        }
        Assert.True(init.IsComplete);
        Assert.True(resp.IsComplete);
        Assert.Equal(handshakeHash, Convert.ToHexStringLower(init.HandshakeHash));
        Assert.Equal(handshakeHash, Convert.ToHexStringLower(resp.HandshakeHash));
        // 双方都拿到了对方的长期公钥。
        Assert.Equal(respStatic.PublicKey, init.RemoteStaticPublicKey);
        Assert.Equal(initStatic.PublicKey, resp.RemoteStaticPublicKey);

        // 之后的传输消息：响应方先发（向量里第 4 条），再交替。
        var (iSend, iRecv) = init.Split();
        var (rSend, rRecv) = resp.Split();
        using (iSend) using (iRecv) using (rSend) using (rRecv)
        {
            for (var i = 3; i < Messages.Length; i++)
            {
                var (send, recv) = i % 2 == 0 ? (iSend, rRecv) : (rSend, iRecv);
                var c = send.EncryptWithAd([], H(Messages[i].Payload));
                Assert.Equal(ciphertexts[i], Convert.ToHexStringLower(c));
                Assert.Equal(Messages[i].Payload, Convert.ToHexStringLower(recv.DecryptWithAd([], c)));
            }
        }
    }

    private static (NoiseHandshake Init, NoiseHandshake Resp) Pair(X25519KeyPair a, X25519KeyPair b, byte[]? respPsk = null, NoisePattern pattern = NoisePattern.XXpsk3) =>
        (new NoiseHandshake(pattern, true, Prologue, Psk, a), new NoiseHandshake(pattern, false, Prologue, respPsk ?? Psk, b));

    /// <summary>XXpsk0：psk 不对时响应方读第一条消息就失败，什么都没回；响应方也拿不到发起方的长期公钥。</summary>
    [Fact]
    public void XxPsk0WrongPskFailsAtFirstMessage()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var other = (byte[])Psk.Clone();
        other[^1] ^= 0x80;
        var (init, resp) = Pair(a, b, other, NoisePattern.XXpsk0);
        using (init) using (resp)
        {
            Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage([])));
            Assert.Null(resp.RemoteStaticPublicKey);
            Assert.False(resp.IsComplete);
        }
    }

    [Fact]
    public void XxPsk0RandomKeysComplete()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var (init, resp) = Pair(a, b, pattern: NoisePattern.XXpsk0);
        using (init) using (resp)
        {
            resp.ReadMessage(init.WriteMessage([]));
            init.ReadMessage(resp.WriteMessage("{}"u8));
            resp.ReadMessage(init.WriteMessage("hi"u8));
            Assert.True(init.IsComplete && resp.IsComplete);
            Assert.Equal(init.HandshakeHash, resp.HandshakeHash);
            Assert.Equal(b.PublicKey, init.RemoteStaticPublicKey);
            Assert.Equal(a.PublicKey, resp.RemoteStaticPublicKey);
        }
    }

    /// <summary>随机密钥也能走完，双方的握手哈希一致。</summary>
    [Fact]
    public void RandomKeysComplete()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var (init, resp) = Pair(a, b);
        using (init) using (resp)
        {
            resp.ReadMessage(init.WriteMessage([]));
            init.ReadMessage(resp.WriteMessage([]));
            resp.ReadMessage(init.WriteMessage("hi"u8));
            Assert.Equal(init.HandshakeHash, resp.HandshakeHash);
            Assert.Equal(b.PublicKey, init.RemoteStaticPublicKey);
            Assert.Equal(a.PublicKey, resp.RemoteStaticPublicKey);
        }
    }

    /// <summary>psk 不对：第三条消息（psk 在这里混入）认证失败。</summary>
    [Fact]
    public void WrongPskFails()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var other = (byte[])Psk.Clone();
        other[0] ^= 1;
        var (init, resp) = Pair(a, b, other);
        using (init) using (resp)
        {
            resp.ReadMessage(init.WriteMessage([]));
            init.ReadMessage(resp.WriteMessage([]));
            Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage([])));
            Assert.False(resp.IsComplete);
        }
    }

    /// <summary>任何一条握手消息被改了一个比特：对方读的时候认证失败。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 40)]
    [InlineData(1, 0)]
    [InlineData(1, 40)]
    [InlineData(1, 90)]
    [InlineData(2, 5)]
    [InlineData(2, 60)]
    public void TamperedMessageFails(int index, int position)
    {
        foreach (var pattern in new[] { NoisePattern.XXpsk3, NoisePattern.XXpsk0 }) TamperedMessageFailsFor(pattern, index, position);
    }

    private static void TamperedMessageFailsFor(NoisePattern pattern, int index, int position)
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var (init, resp) = Pair(a, b, pattern: pattern);
        using (init) using (resp)
        {
            for (var i = 0; i <= index; i++)
            {
                var (writer, reader) = i % 2 == 0 ? (init, resp) : (resp, init);
                var message = writer.WriteMessage("payload-payload"u8);
                if (i == index)
                {
                    message[position % message.Length] ^= 0x01;
                    Assert.ThrowsAny<CryptographicException>(() => reader.ReadMessage(message));
                }
                else reader.ReadMessage(message);
            }
        }
    }

    /// <summary>对方的临时公钥或长期公钥是全零：拒绝（不会得到全零的共享密钥还继续）。</summary>
    [Fact]
    public void AllZeroRemoteKeysRejected()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();

        // 全零的临时公钥（第一条消息被换成 32 个零 + 原来的 payload 位置）：响应方回第二条时 ee 失败。
        var (init, resp) = Pair(a, b);
        using (init) using (resp)
        {
            var first = init.WriteMessage([]);
            Array.Clear(first, 0, X25519KeyPair.Length);
            Assert.ThrowsAny<CryptographicException>(() =>
            {
                resp.ReadMessage(first);
                resp.WriteMessage([]);
            });
        }

        // 全零的长期公钥：用一个“长期密钥”公钥为全零的伪造响应方做不出来，所以直接检查读取端：
        // 发起方读到的 s 解密后是全零时拒绝。这里用同一个握手状态构造合法加密的全零 s。
        using var zeroLike = new ZeroStaticResponder();
        var (init2, _) = Pair(a, b);
        using (init2)
        {
            var m1 = init2.WriteMessage([]);
            var m2 = zeroLike.Respond(m1);
            Assert.ThrowsAny<CryptographicException>(() => init2.ReadMessage(m2));
            Assert.Null(init2.RemoteStaticPublicKey);
        }
    }

    /// <summary>测试用的响应方：按 XXpsk3 的第二条消息格式，但 s 填 32 个零（真实实现做不出来，只能手工拼）。</summary>
    private sealed class ZeroStaticResponder : IDisposable
    {
        private readonly SymmetricState _ss = new(NoiseHandshake.ProtocolName(NoisePattern.XXpsk3));
        private readonly X25519KeyPair _e = X25519KeyPair.Generate();

        public byte[] Respond(byte[] first)
        {
            _ss.MixHash(Prologue);
            var re = first[..X25519KeyPair.Length];
            _ss.MixHash(re);
            _ss.MixKey(re);
            _ss.DecryptAndHash(first[X25519KeyPair.Length..]);
            var message = new List<byte>(_e.PublicKey);
            _ss.MixHash(_e.PublicKey);
            _ss.MixKey(_e.PublicKey);
            _ss.MixKey(_e.Dh(re));
            message.AddRange(_ss.EncryptAndHash(new byte[X25519KeyPair.Length]));
            // es 本该是 DH(s, re)，这里 s 不存在：随便混一个值，发起方在这之前（读 s 时）就应该拒绝。
            _ss.MixKey(new byte[32]);
            message.AddRange(_ss.EncryptAndHash([]));
            return [.. message];
        }

        public void Dispose()
        {
            _ss.Dispose();
            _e.Dispose();
        }
    }

    /// <summary>XXpsk3 必须给长期密钥；NN 模式不能给。</summary>
    [Fact]
    public void StaticKeyRequiredOnlyForXx()
    {
        using var a = X25519KeyPair.Generate();
        Assert.Throws<ArgumentException>(() => new NoiseHandshake(NoisePattern.XXpsk3, true, Prologue, Psk));
        Assert.Throws<ArgumentException>(() => new NoiseHandshake(NoisePattern.XXpsk0, true, Prologue, Psk));
        Assert.Throws<ArgumentException>(() => new NoiseHandshake(NoisePattern.NNpsk0, true, Prologue, Psk, a));
    }

    /// <summary>私钥导出再导入得到同一个公钥。</summary>
    [Fact]
    public void PrivateKeyRoundTrips()
    {
        using var a = X25519KeyPair.Generate();
        var raw = a.ExportPrivateKey();
        using var b = X25519KeyPair.FromPrivateKey(raw);
        CryptographicOperations.ZeroMemory(raw);
        Assert.Equal(a.PublicKey, b.PublicKey);
    }
}
