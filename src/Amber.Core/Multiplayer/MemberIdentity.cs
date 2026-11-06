using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 这台电脑上 Amber 的联机身份：一把长期的 X25519 密钥（每次安装一把），房主靠它认出“还是那个人”。
/// 保存在数据目录的 identity.key，和账号一样加密：Windows 用 DPAPI（当前用户），macOS/Linux 用仅当前用户可读（0600）的本机密钥文件 + AES-GCM。
/// 私钥绝不写进日志；ToString 只给指纹。文件坏了不会崩：换一把新的并给出提示（之前认识你的房主会把你当新成员）。
/// </summary>
public sealed class MemberIdentity : IDisposable
{
    public const string FileName = "identity.key";
    private const string WrapKeyFileName = "identity.wrap";
    private static readonly byte[] Entropy = "Amber.Launcher.Identity"u8.ToArray();
    private static readonly byte[] MagicDpapi = "AMBID1"u8.ToArray();
    private static readonly byte[] MagicAesGcm = "AMBID2"u8.ToArray();
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly string _directory;
    private readonly Lock _gate = new();
    private X25519KeyPair _key;
    // 重置后换下来的旧密钥：可能还有握手在用，等这个对象 Dispose 时再清零。
    private readonly List<X25519KeyPair> _retired = [];

    private MemberIdentity(string directory, X25519KeyPair key, string? warning, bool persisted = true)
    {
        _directory = directory;
        _key = key;
        Warning = warning;
        IsPersisted = persisted;
    }

    /// <summary>false：这次读身份文件时遇到读写错误（比如被杀毒软件暂时锁住），用的是只在内存里的临时身份，没有覆盖原文件。</summary>
    public bool IsPersisted { get; private set; }

    /// <summary>当前的密钥对。<see cref="Reset"/> 之后换成新的，旧的被清零，别长期持有。</summary>
    public X25519KeyPair KeyPair { get { lock (_gate) return _key; } }

    public byte[] PublicKey => (byte[])KeyPair.PublicKey.Clone();

    /// <summary>给人看的指纹，比如 K7Q2-M9XD-4HZP-8RW2。</summary>
    public string Fingerprint => FingerprintOf(KeyPair.PublicKey);

    /// <summary>读取时出了问题（文件坏了、换了系统用户）而换了新身份时的说明；正常时为 null。</summary>
    public string? Warning { get; private set; }

    private string KeyPath => Path.Combine(_directory, FileName);

    /// <summary>
    /// 读取身份；没有就新建并保存。文件内容坏了或解不开（密码学或格式错误）：原文件留一份 .broken，换一把新的，Warning 说明原因。
    /// 读写出错（文件被占用、权限一时不对）：不动原文件，这次用内存里的临时身份，Warning 说明，下次启动再读。
    /// </summary>
    public static MemberIdentity LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            try
            {
                var raw = Unprotect(directory, File.ReadAllBytes(path));
                try
                {
                    return new MemberIdentity(directory, X25519KeyPair.FromPrivateKey(raw), null);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new MemberIdentity(directory, X25519KeyPair.Generate(), L.T("core.mp.identity.unreadable"), persisted: false);
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                try { File.Copy(path, path + ".broken", true); }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
                var fresh = new MemberIdentity(directory, X25519KeyPair.Generate(), L.T("core.mp.identity.corrupt"));
                fresh.Save();
                return fresh;
            }
        }
        var created = new MemberIdentity(directory, X25519KeyPair.Generate(), null);
        created.Save();
        return created;
    }

    /// <summary>换一个新身份（用户点“重置身份”）。之前认识你的房主会把你当成新成员。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            var fresh = X25519KeyPair.Generate();
            var old = _key;
            _key = fresh;
            try
            {
                Save();
            }
            catch
            {
                _key = old;
                fresh.Dispose();
                throw;
            }
            Warning = null;
            IsPersisted = true;
            _retired.Add(old);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_directory);
        var raw = KeyPair.ExportPrivateKey();
        try
        {
            var data = Protect(_directory, raw);
            var temp = KeyPath + ".tmp";
            WritePrivate(temp, data);
            File.Move(temp, KeyPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>公钥的指纹：SHA-256 的前 10 字节，Crockford Base32（16 个字符），每 4 个一组。</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> publicKey)
    {
        var digest = SHA256.HashData(publicKey);
        var text = new StringBuilder(19);
        // 80 位正好 16 个 5 位。
        var bits = 0;
        var buffer = 0;
        var written = 0;
        for (var i = 0; i < 10; i++)
        {
            buffer = (buffer << 8) | digest[i];
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (written > 0 && written % 4 == 0) text.Append('-');
                text.Append(Crockford[(buffer >> bits) & 31]);
                written++;
            }
        }
        return text.ToString();
    }

    public override string ToString() => Fingerprint;

    public void Dispose()
    {
        lock (_gate)
        {
            _key.Dispose();
            foreach (var old in _retired) old.Dispose();
            _retired.Clear();
        }
    }

    private static byte[] Protect(string directory, byte[] raw)
    {
        if (OperatingSystem.IsWindows())
            return [.. MagicDpapi, .. ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser)];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[raw.Length];
        var tag = new byte[16];
        var key = WrapKey(directory, create: true);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, raw, cipher, tag, Entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return [.. MagicAesGcm, .. nonce, .. tag, .. cipher];
    }

    private static byte[] Unprotect(string directory, byte[] stored)
    {
        if (stored.AsSpan().StartsWith(MagicDpapi))
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException(L.T("core.mp.identity.otherplatform"));
            var raw = ProtectedData.Unprotect(stored[MagicDpapi.Length..], Entropy, DataProtectionScope.CurrentUser);
            if (raw.Length != X25519KeyPair.Length) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            return raw;
        }
        if (stored.AsSpan().StartsWith(MagicAesGcm))
        {
            var body = stored.AsSpan(MagicAesGcm.Length);
            if (body.Length != 12 + 16 + X25519KeyPair.Length) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            var raw = new byte[X25519KeyPair.Length];
            var key = WrapKey(directory, create: false);
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(body[..12], body[28..], body[12..28], raw, Entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
            return raw;
        }
        throw new CryptographicException(L.T("core.mp.identity.corrupt"));
    }

    /// <summary>macOS/Linux：包身份用的本机密钥，0600，和身份文件放一起。</summary>
    private static byte[] WrapKey(string directory, bool create)
    {
        var path = Path.Combine(directory, WrapKeyFileName);
        // 长度不对的包裹密钥（比如第一次写入时断电留下的空文件）不可能加密过任何身份：
        // 需要新建时挪成 .broken 再重建，否则损坏分支里重新保存还会读到它，每次启动都失败。
        if (create && File.Exists(path) && new FileInfo(path).Length != 32)
            File.Move(path, path + ".broken", true);
        if (!File.Exists(path))
        {
            if (!create) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            var created = RandomNumberGenerator.GetBytes(32);
            try
            {
                // CreateNew：两个 Jay 同时第一次启动时，只有一个能写成，另一个读已经写好的那把，不会互相覆盖。
                WritePrivate(path, created, FileMode.CreateNew);
                return created;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(created);
            }
        }
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    /// <summary>写文件；非 Windows 上一创建就是 0600（不先以默认权限创建再改）。</summary>
    private static void WritePrivate(string path, byte[] data, FileMode mode = FileMode.Create)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        // 已经存在的文件不会按 UnixCreateMode 改权限，写完再收紧一次。
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
