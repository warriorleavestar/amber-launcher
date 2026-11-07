using System.Security.Cryptography;

namespace Amber.Core.Auth;

/// <summary>macOS/Linux 上的本机包裹密钥：创建时就限制为当前用户读写，避免先落盘再 chmod 的窗口。</summary>
internal static class LocalSecretKey
{
    public static byte[] LoadOrCreate(string path, string description)
    {
        if (File.Exists(path)) return Read(path, description);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var created = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var stream = new FileStream(path, options);
            stream.Write(created);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path))
        {
            // 另一个 Jay 进程刚好先建好：必须用它的密钥，不能各写各的。
            return Read(path, description);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return created;
    }

    private static byte[] Read(string path, string description)
    {
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new CryptographicException($"Invalid {description} key");
        return key;
    }
}
