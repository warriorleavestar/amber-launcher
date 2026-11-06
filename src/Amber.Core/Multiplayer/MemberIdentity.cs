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
    /// <summary>重置前的旧密钥：正在进行的握手可能还拿着它，留到 Dispose 才清零。</summary>
    private readonly List<X25519KeyPair> _retired = [];

    private MemberIdentity(string directory, X25519KeyPair key, string? warning)
    {
        _directory = directory;
        _key = key;
        Warning = warning;
    }

    /// <summary>当前的密钥对。<see cref="Reset"/> 之后换成新的；旧的在这个对象 Dispose 前一直可用（正在进行的握手不会突然失效）。</summary>
    public X25519KeyPair KeyPair { get { lock (_gate) return _key; } }

    public byte[] PublicKey => (byte[])KeyPair.PublicKey.Clone();

    /// <summary>给人看的指纹，比如 K7Q2-M9XD-4HZP-8RW2。</summary>
    public string Fingerprint => FingerprintOf(KeyPair.PublicKey);

    /// <summary>读取时出了问题（文件坏了、换了系统用户）而换了新身份时的说明；正常时为 null。</summary>
    public string? Warning { get; private set; }

    private string KeyPath => Path.Combine(_directory, FileName);

    /// <summary>这次运行用的是只在内存里的临时身份（身份文件读不了，比如被杀毒软件锁住）：不保存，下次启动再读文件。</summary>
    public bool IsTemporary { get; private set; }

    /// <summary>
    /// 读取身份；没有就新建并保存。文件坏了或解不开（内容不对、换了系统用户）：原文件留一份 .broken，换一把新的，Warning 说明原因。
    /// 文件读不了（被占用、没权限）时绝不覆盖它：这次用一把不保存的临时身份，Warning 说明。
    /// </summary>
    public static MemberIdentity LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            byte[] stored;
            try
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stored = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new MemberIdentity(directory, X25519KeyPair.Generate(), L.F("core.mp.identity.unreadable", e.Message)) { IsTemporary = true };
            }
            try
            {
                var raw = Unprotect(directory, stored);
                try
                {
                    return new MemberIdentity(directory, X25519KeyPair.FromPrivateKey(raw), null);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
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

    /// <summary>
    /// 换一个新身份（用户点“重置身份”）。之前认识你的房主会把你当成新成员。
    /// 界面只在没有开房间、没有加入房间时允许；即使这时还有握手在用旧密钥，旧密钥也留到 Dispose 才清零。
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            var old = _key;
            _key = X25519KeyPair.Generate();
            Warning = null;
            Save();
            IsTemporary = false;
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
            // 每次用新的临时文件名：上次崩溃留下的 .tmp 不会挡路，也不会写进别人已经打开的文件。
            var temp = KeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
        if (!File.Exists(path))
        {
            if (!create) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            var created = RandomNumberGenerator.GetBytes(32);
            try
            {
                // CreateNew：另一个 Jay 进程同时在建时，只有一个能建成，输的一方读对方建好的。
                WritePrivate(path, created);
                return created;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(created);
            }
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
        return key;
    }

    /// <summary>新建文件（已存在就失败）；非 Windows 上一创建就是 0600（不先以默认权限创建再改）。</summary>
    private static void WritePrivate(string path, byte[] data)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
