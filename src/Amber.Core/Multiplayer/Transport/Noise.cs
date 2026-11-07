using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Security;
using System.Text;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Noise 协议框架（https://noiseprotocol.org/noise.html ，第 34 版）的一个最小实现，只用成熟的密码原语：
/// X25519（BouncyCastle 的 RFC 7748 实现）、ChaCha20-Poly1305、SHA-256 / HMAC-SHA256。不自己发明任何原语或构造。
/// 支持四种握手：
///   Noise_NNpsk0_25519_ChaChaPoly_SHA256：-> psk, e   &lt;- e, ee
///   Noise_NNhfspsk0_25519+MLKEM768_ChaChaPoly_SHA256：-> psk, e, e1   &lt;- e, ee, ekem1（Noise HFS 扩展，KEM 用 ML-KEM-768，混合抗量子）
///   Noise_XXpsk3_25519_ChaChaPoly_SHA256：-> e   &lt;- e, ee, s, es   -> s, se, psk（规范 7.5、9 节；双方交换长期身份公钥）
///   Noise_XXpsk0_25519_ChaChaPoly_SHA256：-> psk, e   &lt;- e, ee, s, es   -> s, se（敲门和成员控制通道用：令牌不对第一条消息就失败）
/// psk 只让持有它的人完成握手（NN 系列来自房间密钥或房主下发的成员密钥，XX 系列来自邀请令牌）；每次连接的临时密钥带来前向保密。
/// </summary>
public sealed class CipherState : IDisposable
{
    public const int KeyLength = 32;
    public const int TagLength = 16;
    private byte[]? _k;
    private ChaCha20Poly1305? _aead;
    private ulong _n;

    public bool HasKey => _k is not null;
    private volatile bool _disposed;
    /// <summary>换钥和关闭互斥：关闭之后换钥不能再把密钥装回去。</summary>
    private readonly Lock _keyLock = new();
    public ulong Nonce => _n;

    /// <summary>接管 key 数组（之后由这个对象负责清零）；旧密钥先清零。</summary>
    public void InitializeKey(byte[]? key)
    {
        if (_k is not null && !ReferenceEquals(_k, key)) CryptographicOperations.ZeroMemory(_k);
        _k = key;
        _aead?.Dispose();
        _aead = key is null ? null : new ChaCha20Poly1305(key);
        _n = 0;
    }

    /// <summary>测试用：直接设 nonce（比如测接近上限的情况）。</summary>
    internal void SetNonce(ulong n) => _n = n;

    private static byte[] NonceBytes(ulong n)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), n);
        return nonce;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        // 先把密钥和 AEAD 读到局部变量：别的线程同时清掉它们时，这里要么用完整的旧密钥，要么报错，不会退回“不加密”。
        var aead = _aead;
        var hasKey = _k is not null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!hasKey) return plaintext.ToArray();
        ObjectDisposedException.ThrowIf(aead is null, this);
        // 2^64-1 留给 Rekey 用，不能拿来加密。
        if (_n == ulong.MaxValue) throw new CryptographicException(L.T("core.mp.noise.nonceexhausted"));
        var output = new byte[plaintext.Length + TagLength];
        aead.Encrypt(NonceBytes(_n), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
        _n++;
        return output;
    }

    /// <summary>认证失败抛 CryptographicException，nonce 不前进。</summary>
    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        var aead = _aead;
        var hasKey = _k is not null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!hasKey) return ciphertext.ToArray();
        ObjectDisposedException.ThrowIf(aead is null, this);
        if (_n == ulong.MaxValue) throw new CryptographicException(L.T("core.mp.noise.nonceexhausted"));
        if (ciphertext.Length < TagLength) throw new CryptographicException(L.T("core.mp.noise.ciphertextshort"));
        var plain = new byte[ciphertext.Length - TagLength];
        aead.Decrypt(NonceBytes(_n), ciphertext[..plain.Length], ciphertext[plain.Length..], plain, ad);
        _n++;
        return plain;
    }

    /// <summary>Noise 规范 11.3 的 Rekey：k = ENCRYPT(k, 2^64-1, 空, 32 个零) 的前 32 字节。nonce 不变。</summary>
    public void Rekey()
    {
        lock (_keyLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_k is null) return;
            var zeros = new byte[KeyLength];
            var output = new byte[KeyLength];
            var tag = new byte[TagLength];
            var aead = _aead;
            ObjectDisposedException.ThrowIf(aead is null, this);
            aead.Encrypt(NonceBytes(ulong.MaxValue), zeros, output, tag);
            CryptographicOperations.ZeroMemory(_k);
            var n = _n;
            InitializeKey(output);
            _n = n;
        }
    }

    /// <summary>关闭时把密钥清零。之后再加解密会抛异常（不会退回“没有密钥就原样”的状态）。</summary>
    public void Dispose()
    {
        lock (_keyLock)
        {
            _disposed = true;
            if (_k is not null) CryptographicOperations.ZeroMemory(_k);
            _k = null;
            _aead?.Dispose();
            _aead = null;
        }
    }
}

public sealed class SymmetricState : IDisposable
{
    private readonly CipherState _cipher = new();
    private byte[] _ck;
    private byte[] _h;

    public SymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        _h = name.Length <= 32 ? [.. name, .. new byte[32 - name.Length]] : SHA256.HashData(name);
        _ck = (byte[])_h.Clone();
    }

    public byte[] HandshakeHash => (byte[])_h.Clone();
    public bool HasKey => _cipher.HasKey;

    public void MixHash(ReadOnlySpan<byte> data)
    {
        var old = _h;
        _h = SHA256.HashData([.. _h, .. data]);
        CryptographicOperations.ZeroMemory(old);
    }

    /// <summary>握手结束后清掉链密钥、哈希和握手用的密钥。</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_ck);
        CryptographicOperations.ZeroMemory(_h);
        _cipher.Dispose();
    }

    public void MixKey(ReadOnlySpan<byte> ikm)
    {
        var (ck, k, _) = Hkdf(_ck, ikm, 2);
        CryptographicOperations.ZeroMemory(_ck);
        _ck = ck;
        _cipher.InitializeKey(k);
    }

    public void MixKeyAndHash(ReadOnlySpan<byte> ikm)
    {
        var (ck, h, k) = Hkdf(_ck, ikm, 3);
        CryptographicOperations.ZeroMemory(_ck);
        _ck = ck;
        MixHash(h);
        CryptographicOperations.ZeroMemory(h);
        _cipher.InitializeKey(k!);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var c = _cipher.EncryptWithAd(_h, plaintext);
        MixHash(c);
        return c;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var p = _cipher.DecryptWithAd(_h, ciphertext);
        MixHash(ciphertext);
        return p;
    }

    public (CipherState First, CipherState Second) Split()
    {
        var (k1, k2, _) = Hkdf(_ck, [], 2);
        var c1 = new CipherState();
        var c2 = new CipherState();
        c1.InitializeKey(k1);
        c2.InitializeKey(k2);
        return (c1, c2);
    }

    /// <summary>Noise 规范 4.3 的 HKDF（基于 HMAC-SHA256），输出 2 或 3 段 32 字节。</summary>
    internal static (byte[] Out1, byte[] Out2, byte[]? Out3) Hkdf(byte[] chainingKey, ReadOnlySpan<byte> ikm, int outputs)
    {
        var temp = HMACSHA256.HashData(chainingKey, ikm);
        var o1 = HMACSHA256.HashData(temp, (byte[])[0x01]);
        var o2 = HMACSHA256.HashData(temp, (byte[])[.. o1, 0x02]);
        var o3 = outputs == 3 ? HMACSHA256.HashData(temp, (byte[])[.. o2, 0x03]) : null;
        CryptographicOperations.ZeroMemory(temp);
        return (o1, o2, o3);
    }
}

/// <summary>X25519 密钥对，使用跨平台的 RFC 7748 实现。</summary>
public sealed class X25519KeyPair : IDisposable
{
    public const int Length = 32;
    private readonly byte[] _privateKey;
    private bool _disposed;

    public byte[] PublicKey { get; }

    private X25519KeyPair(byte[] privateKey)
    {
        _privateKey = privateKey;
        PublicKey = new byte[Length];
        X25519.GeneratePublicKey(_privateKey, PublicKey);
    }

    public static X25519KeyPair Generate()
    {
        var privateKey = new byte[Length];
        X25519.GeneratePrivateKey(new SecureRandom(), privateKey);
        return new X25519KeyPair(privateKey);
    }

    /// <summary>从保存的私钥恢复（会先按 RFC 7748 做 clamp）。传入的数组不会被保留。</summary>
    public static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != Length) throw new CryptographicException(L.T("core.mp.noise.publickeylength"));
        var d = privateKey.ToArray();
        d[0] &= 248;
        d[31] &= 127;
        d[31] |= 64;
        return new X25519KeyPair(d);
    }

    /// <summary>导出私钥（保存身份用）。调用方负责用完清零、绝不写进日志。</summary>
    public byte[] ExportPrivateKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_privateKey.Clone();
    }

    /// <summary>测试用：按 RFC 7748 的格式导入私钥（小端、会先做 clamp）和对应公钥。</summary>
    internal static X25519KeyPair Import(byte[] privateKey, byte[] publicKey)
    {
        var d = (byte[])privateKey.Clone();
        d[0] &= 248;
        d[31] &= 127;
        d[31] |= 64;
        var pair = new X25519KeyPair(d);
        if (!CryptographicOperations.FixedTimeEquals(pair.PublicKey, publicKey))
        {
            pair.Dispose();
            throw new CryptographicException(L.T("core.mp.noise.invalidpublickey"));
        }
        return pair;
    }

    public byte[] Dh(ReadOnlySpan<byte> remotePublic)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (remotePublic.Length != Length) throw new CryptographicException(L.T("core.mp.noise.publickeylength"));
        var shared = new byte[Length];
        try
        {
            if (!X25519.CalculateAgreement(_privateKey, remotePublic, shared))
                throw new CryptographicException(L.T("core.mp.noise.zerosharedkey"));
        }
        catch (ArgumentException e)
        {
            throw new CryptographicException(L.T("core.mp.noise.invalidpublickey"), e);
        }
        // 全零结果说明对方给了小阶点，直接拒绝（RFC 7748 第 6.1 节建议的检查）。
        if (shared.All(b => b == 0)) throw new CryptographicException(L.T("core.mp.noise.zerosharedkey"));
        return shared;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_privateKey);
        _disposed = true;
    }
}

public enum NoisePattern { NNpsk0, NNhfspsk0, XXpsk3, XXpsk0 }

/// <summary>NNpsk0 / NNhfspsk0 两条消息、XXpsk3 三条消息的握手。initiator 先发。</summary>
public sealed class NoiseHandshake : IDisposable
{
    public static string ProtocolName(NoisePattern pattern) => pattern switch
    {
        NoisePattern.NNpsk0 => "Noise_NNpsk0_25519_ChaChaPoly_SHA256",
        NoisePattern.XXpsk3 => "Noise_XXpsk3_25519_ChaChaPoly_SHA256",
        NoisePattern.XXpsk0 => "Noise_XXpsk0_25519_ChaChaPoly_SHA256",
        _ => "Noise_NNhfspsk0_25519+MLKEM768_ChaChaPoly_SHA256",
    };

    private readonly SymmetricState _ss;
    private readonly NoisePattern _pattern;
    private readonly bool _initiator;
    private readonly byte[] _psk;
    private X25519KeyPair? _e;
    private byte[]? _re;
#pragma warning disable SYSLIB5006 // ML-KEM 在 .NET 10 里还标着实验性，但用的是系统（Windows CNG）的实现
    private MLKem? _e1;
#pragma warning restore SYSLIB5006
    private byte[]? _re1;
    private int _step;

    // XXpsk3：自己的长期密钥（不归这个对象管，不在 Dispose 里清掉）和对方的长期公钥。
    private readonly X25519KeyPair? _s;
    private byte[]? _rs;

    public NoiseHandshake(NoisePattern pattern, bool initiator, ReadOnlySpan<byte> prologue, byte[] psk)
        : this(pattern, initiator, prologue, psk, null)
    {
    }

    /// <param name="localStatic">XXpsk3 必须给：本机的长期身份密钥（握手里加密发给对方）。别的模式必须为 null。</param>
    public NoiseHandshake(NoisePattern pattern, bool initiator, ReadOnlySpan<byte> prologue, byte[] psk, X25519KeyPair? localStatic)
    {
        if (psk.Length != 32) throw new ArgumentException(L.T("core.mp.noise.psklength"));
        if (IsXx(pattern) != (localStatic is not null)) throw new ArgumentException(L.T("core.mp.noise.staticrequired"));
        _pattern = pattern;
        _initiator = initiator;
        _psk = psk.ToArray();
        _s = localStatic;
        _ss = new SymmetricState(ProtocolName(pattern));
        _ss.MixHash(prologue);
    }

    /// <summary>XXpsk3：对方的长期公钥（读到对方的 s 之后才有；握手完成前不要拿来做身份判断）。</summary>
    public byte[]? RemoteStaticPublicKey => _rs is null ? null : (byte[])_rs.Clone();

    private int MessageCount => IsXx(_pattern) ? 3 : 2;

    private static bool IsXx(NoisePattern pattern) => pattern is NoisePattern.XXpsk3 or NoisePattern.XXpsk0;

    /// <summary>测试用：指定临时密钥（Noise 测试向量里是固定的）。</summary>
    internal X25519KeyPair? FixedEphemeral { get; init; }

    public byte[] HandshakeHash => _ss.HandshakeHash;
    public bool IsComplete => _step == MessageCount;

    public static bool HybridSupported
    {
        get
        {
#pragma warning disable SYSLIB5006
            return MLKem.IsSupported;
#pragma warning restore SYSLIB5006
        }
    }

    private void WriteE(List<byte> message)
    {
        _e = FixedEphemeral ?? X25519KeyPair.Generate();
        message.AddRange(_e.PublicKey);
        _ss.MixHash(_e.PublicKey);
        // psk 握手里 e 也要 MixKey（规范 9.2）。
        _ss.MixKey(_e.PublicKey);
    }

    private ReadOnlySpan<byte> ReadE(ReadOnlySpan<byte> message)
    {
        if (message.Length < X25519KeyPair.Length) throw new CryptographicException(L.T("core.mp.noise.handshakeshort"));
        _re = message[..X25519KeyPair.Length].ToArray();
        _ss.MixHash(_re);
        _ss.MixKey(_re);
        return message[X25519KeyPair.Length..];
    }

    /// <summary>写下一条握手消息（payload 会被加密）。</summary>
    public byte[] WriteMessage(ReadOnlySpan<byte> payload)
    {
        EnsureHybridSupported();
        try
        {
            return WriteMessageCore(payload);
        }
        catch (PlatformNotSupportedException e)
        {
            throw new CryptographicException(L.T("core.mp.noise.hybridunsupported"), e);
        }
    }

    /// <summary>读对方的握手消息，返回解密后的 payload。认证失败、本机不支持时抛 CryptographicException。</summary>
    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        EnsureHybridSupported();
        try
        {
            return ReadMessageCore(message);
        }
        catch (PlatformNotSupportedException e)
        {
            throw new CryptographicException(L.T("core.mp.noise.hybridunsupported"), e);
        }
    }

    private void EnsureHybridSupported()
    {
        if (_pattern == NoisePattern.NNhfspsk0 && !HybridSupported)
            throw new CryptographicException(L.T("core.mp.noise.hybridunsupported"));
    }

    /// <summary>XXpsk3 的 s：长期公钥加密发出（这时已经有密钥）。</summary>
    private void WriteS(List<byte> message) => message.AddRange(_ss.EncryptAndHash(_s!.PublicKey));

    private ReadOnlySpan<byte> ReadS(ReadOnlySpan<byte> message)
    {
        var length = X25519KeyPair.Length + CipherState.TagLength;
        if (message.Length < length) throw new CryptographicException(L.T("core.mp.noise.handshakeshort"));
        var rs = _ss.DecryptAndHash(message[..length]);
        // 全零公钥（以及其他小阶点，DH 时会得到全零）一律拒绝。
        if (rs.All(x => x == 0)) throw new CryptographicException(L.T("core.mp.noise.invalidpublickey"));
        _rs = rs;
        return message[length..];
    }

    /// <summary>XXpsk3：-> e   &lt;- e, ee, s, es   -> s, se, psk。每条消息都可以带 payload。</summary>
    private byte[] WriteXxCore(ReadOnlySpan<byte> payload)
    {
        var message = new List<byte>();
        if (_initiator && _step == 0)
        {
            // XXpsk0：psk 在第一条消息最前面混入，不知道令牌的人第一条消息就过不了，也拿不到任何一方的长期公钥。
            if (_pattern == NoisePattern.XXpsk0) _ss.MixKeyAndHash(_psk);
            WriteE(message);
        }
        else if (!_initiator && _step == 1)
        {
            WriteE(message);
            _ss.MixKey(_e!.Dh(_re!));
            WriteS(message);
            _ss.MixKey(_s!.Dh(_re!));
        }
        else if (_initiator && _step == 2)
        {
            WriteS(message);
            _ss.MixKey(_s!.Dh(_re!));
            if (_pattern == NoisePattern.XXpsk3) _ss.MixKeyAndHash(_psk);
        }
        else throw new InvalidOperationException(L.T("core.mp.noise.unexpectedwrite"));
        message.AddRange(_ss.EncryptAndHash(payload));
        _step++;
        return [.. message];
    }

    private byte[] ReadXxCore(ReadOnlySpan<byte> message)
    {
        if (!_initiator && _step == 0)
        {
            if (_pattern == NoisePattern.XXpsk0) _ss.MixKeyAndHash(_psk);
            message = ReadE(message);
        }
        else if (_initiator && _step == 1)
        {
            message = ReadE(message);
            _ss.MixKey(_e!.Dh(_re!));
            message = ReadS(message);
            _ss.MixKey(_e.Dh(_rs!));
        }
        else if (!_initiator && _step == 2)
        {
            message = ReadS(message);
            _ss.MixKey(_e!.Dh(_rs!));
            if (_pattern == NoisePattern.XXpsk3) _ss.MixKeyAndHash(_psk);
        }
        else throw new InvalidOperationException(L.T("core.mp.noise.unexpectedread"));
        var payload = _ss.DecryptAndHash(message);
        _step++;
        return payload;
    }

    private byte[] WriteMessageCore(ReadOnlySpan<byte> payload)
    {
        if (IsXx(_pattern)) return WriteXxCore(payload);
        var message = new List<byte>();
        if (_initiator && _step == 0)
        {
            _ss.MixKeyAndHash(_psk);
            WriteE(message);
            if (_pattern == NoisePattern.NNhfspsk0)
            {
#pragma warning disable SYSLIB5006
                _e1 = MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
                message.AddRange(_ss.EncryptAndHash(_e1.ExportEncapsulationKey()));
#pragma warning restore SYSLIB5006
            }
        }
        else if (!_initiator && _step == 1)
        {
            WriteE(message);
            _ss.MixKey(_e!.Dh(_re!));
            if (_pattern == NoisePattern.NNhfspsk0)
            {
#pragma warning disable SYSLIB5006
                using var remote = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, _re1!);
                remote.Encapsulate(out var ciphertext, out var shared);
#pragma warning restore SYSLIB5006
                message.AddRange(_ss.EncryptAndHash(ciphertext));
                _ss.MixKey(shared);
                CryptographicOperations.ZeroMemory(shared);
            }
        }
        else throw new InvalidOperationException(L.T("core.mp.noise.unexpectedwrite"));
        message.AddRange(_ss.EncryptAndHash(payload));
        _step++;
        return [.. message];
    }

    private byte[] ReadMessageCore(ReadOnlySpan<byte> message)
    {
        if (IsXx(_pattern)) return ReadXxCore(message);
        if (!_initiator && _step == 0)
        {
            _ss.MixKeyAndHash(_psk);
            message = ReadE(message);
            if (_pattern == NoisePattern.NNhfspsk0)
            {
#pragma warning disable SYSLIB5006
                var length = MLKemAlgorithm.MLKem768.EncapsulationKeySizeInBytes + CipherState.TagLength;
#pragma warning restore SYSLIB5006
                if (message.Length < length) throw new CryptographicException(L.T("core.mp.noise.handshakeshort"));
                _re1 = _ss.DecryptAndHash(message[..length]);
                message = message[length..];
            }
        }
        else if (_initiator && _step == 1)
        {
            message = ReadE(message);
            _ss.MixKey(_e!.Dh(_re!));
            if (_pattern == NoisePattern.NNhfspsk0)
            {
#pragma warning disable SYSLIB5006
                var length = MLKemAlgorithm.MLKem768.CiphertextSizeInBytes + CipherState.TagLength;
                if (message.Length < length) throw new CryptographicException(L.T("core.mp.noise.handshakeshort"));
                var ciphertext = _ss.DecryptAndHash(message[..length]);
                var shared = _e1!.Decapsulate(ciphertext);
#pragma warning restore SYSLIB5006
                _ss.MixKey(shared);
                CryptographicOperations.ZeroMemory(shared);
                message = message[length..];
            }
        }
        else throw new InvalidOperationException(L.T("core.mp.noise.unexpectedread"));
        var payload = _ss.DecryptAndHash(message);
        _step++;
        return payload;
    }

    /// <summary>握手完成后分出两个方向的加密状态：(自己发用的, 自己收用的)。</summary>
    public (CipherState Send, CipherState Receive) Split()
    {
        if (!IsComplete) throw new InvalidOperationException(L.T("core.mp.noise.incomplete"));
        var (c1, c2) = _ss.Split();
        return _initiator ? (c1, c2) : (c2, c1);
    }

    public void Dispose()
    {
        _e?.Dispose();
        _e1?.Dispose();
        _ss.Dispose();
        CryptographicOperations.ZeroMemory(_psk);
        if (_re is not null) CryptographicOperations.ZeroMemory(_re);
    }
}
