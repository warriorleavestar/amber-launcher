using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Noise_XXpsk0_25519_ChaChaPoly_SHA256（敲门和成员控制通道用）。
/// cacophony 和 snow 都没有这个组合的向量：下面这条用 Python noiseprotocol 生成，
/// 生成前先确认同一个库能逐字节复现 cacophony 官方的 XXpsk3 向量；密钥、prologue、psk、payload 和 XXpsk3 那条相同。
/// </summary>
public sealed class NoiseXxPsk0Tests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    private static readonly byte[] Prologue = H("4a6f686e2047616c74");
    private static readonly byte[] Psk = H("54686973206973206d7920417573747269616e20706572737065637469766521");
    private const string InitStatic = "e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1";
    private const string InitEphemeral = "893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a";
    private const string RespStatic = "4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893";
    private const string RespEphemeral = "bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b";
    private const string HandshakeHash = "0793e6b2d2fed61d803e0d9de8a4b961436e896194246a784d30d1bb1fd42e54";

    private static readonly (string Payload, string Ciphertext)[] Messages =
    [
        ("4c756477696720766f6e204d69736573", "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c794444e557af4bb1c735a4948bdd9aedbb4b3db9a8aed79bc6517b05eca117a755c3"),
        ("4d757272617920526f746862617264", "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f14480884309813c929a2ee793a6909502f23492a90b088e4acc71ae6ca49464e67d97fdf404b94bc46aad47491994c66e9a8832f1701cdc1f47fc2ba565695638f005ca2e7b9a569a4c241a57879a0779d8bdbd"),
        ("462e20412e20486179656b", "591674f2df5cc5e0a9b3ddea86bafb1219b36345c12aa4bd0526a9f727fe2e2a14efe7e0a7b7710d042006afd97e7cf58a3efe9041dba7cf07099b7c2345135e8d51254f6f742730eb7755"),
        ("4361726c204d656e676572", "65bc9bce988c8c9becae660cd4fdd43f1c0dfaa40a2ac2322a09ae"),
        ("4a65616e2d426170746973746520536179", "4cc7db3b57b01e80a8f684fa6eb8d043ae32f38aa598cf8bbe474aa9ffc4dfce9b"),
        ("457567656e2042f6686d20766f6e2042617765726b", "2ebc31fbd26efafc391423fd69aa09159c5c15d9327ebae0e4afaffac55baa42fd9b742f91"),
    ];

    [Fact]
    public void MatchesIndependentVector()
    {
        using var initStatic = X25519KeyPair.FromPrivateKey(H(InitStatic));
        using var respStatic = X25519KeyPair.FromPrivateKey(H(RespStatic));
        using var init = new NoiseHandshake(NoisePattern.XXpsk0, true, Prologue, Psk, initStatic) { FixedEphemeral = X25519KeyPair.FromPrivateKey(H(InitEphemeral)) };
        using var resp = new NoiseHandshake(NoisePattern.XXpsk0, false, Prologue, Psk, respStatic) { FixedEphemeral = X25519KeyPair.FromPrivateKey(H(RespEphemeral)) };
        for (var i = 0; i < 3; i++)
        {
            var (writer, reader) = i % 2 == 0 ? (init, resp) : (resp, init);
            var message = writer.WriteMessage(H(Messages[i].Payload));
            Assert.Equal(Messages[i].Ciphertext, Convert.ToHexStringLower(message));
            Assert.Equal(Messages[i].Payload, Convert.ToHexStringLower(reader.ReadMessage(message)));
        }
        Assert.True(init.IsComplete);
        Assert.True(resp.IsComplete);
        Assert.Equal(HandshakeHash, Convert.ToHexStringLower(init.HandshakeHash));
        Assert.Equal(HandshakeHash, Convert.ToHexStringLower(resp.HandshakeHash));
        Assert.Equal(respStatic.PublicKey, init.RemoteStaticPublicKey);
        Assert.Equal(initStatic.PublicKey, resp.RemoteStaticPublicKey);

        var (iSend, iRecv) = init.Split();
        var (rSend, rRecv) = resp.Split();
        using (iSend) using (iRecv) using (rSend) using (rRecv)
        {
            for (var i = 3; i < Messages.Length; i++)
            {
                var (send, recv) = i % 2 == 0 ? (iSend, rRecv) : (rSend, iRecv);
                var c = send.EncryptWithAd([], H(Messages[i].Payload));
                Assert.Equal(Messages[i].Ciphertext, Convert.ToHexStringLower(c));
                Assert.Equal(Messages[i].Payload, Convert.ToHexStringLower(recv.DecryptWithAd([], c)));
            }
        }
    }

    /// <summary>令牌（psk）不对：第一条消息就认证失败，响应方不回任何东西，也就不会把自己的长期公钥发出去。</summary>
    [Fact]
    public void WrongPskFailsAtFirstMessage()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        var other = (byte[])Psk.Clone();
        other[0] ^= 1;
        using var init = new NoiseHandshake(NoisePattern.XXpsk0, true, Prologue, Psk, a);
        using var resp = new NoiseHandshake(NoisePattern.XXpsk0, false, Prologue, other, b);
        Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage([])));
        Assert.Null(resp.RemoteStaticPublicKey);
    }

    /// <summary>XXpsk0 和 XXpsk3 互不兼容（协议名不同），不会被混用。</summary>
    [Fact]
    public void DoesNotInteroperateWithPsk3()
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        using var init = new NoiseHandshake(NoisePattern.XXpsk0, true, Prologue, Psk, a);
        using var resp = new NoiseHandshake(NoisePattern.XXpsk3, false, Prologue, Psk, b);
        Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage("x"u8)));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 40)]
    [InlineData(1, 0)]
    [InlineData(1, 50)]
    [InlineData(2, 10)]
    [InlineData(2, 60)]
    public void TamperedMessageFails(int index, int position)
    {
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        using var init = new NoiseHandshake(NoisePattern.XXpsk0, true, Prologue, Psk, a);
        using var resp = new NoiseHandshake(NoisePattern.XXpsk0, false, Prologue, Psk, b);
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
