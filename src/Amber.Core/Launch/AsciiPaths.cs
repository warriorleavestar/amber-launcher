using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Launch;

/// <summary>
/// Windows 上 Java 启动器按“非 Unicode 程序的语言”（ANSI 代码页）读自己的路径和命令行参数。
/// 系统语言设成英文等时，路径里的中文就变成问号，Java 报 “could not find java.dll”，游戏也找不到库。
/// 这时给目录建一个纯英文名字的目录联接（junction，不需要管理员权限），启动时改用联接路径。
/// </summary>
public static class AsciiPaths
{
    static AsciiPaths() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>当前 ANSI 代码页能不能原样表示这段文字。非 Windows 或者代码页是 UTF-8 时总是能。</summary>
    public static bool Representable(string text) => Representable(text, OperatingSystem.IsWindows() ? (int)GetACP() : 65001);

    internal static bool Representable(string text, int codePage)
    {
        if (codePage == 65001 || text.All(c => c < 128)) return true;
        try
        {
            var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            encoding.GetBytes(text);
            return true;
        }
        catch (Exception e) when (e is EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>需要的话返回一个纯英文的联接路径指向 directory；不需要或者建不了时返回 directory 本身。</summary>
    public static string AliasIfNeeded(string directory, Action<string>? log = null)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!OperatingSystem.IsWindows() || Representable(full)) return full;
        var name = "a" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..10];
        foreach (var parent in LinkParents(full))
        {
            var link = Path.Combine(parent, name);
            try
            {
                Directory.CreateDirectory(parent);
                var info = new DirectoryInfo(link);
                if (info.Exists)
                {
                    if (info.LinkTarget is { } target && Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar).Equals(full, StringComparison.OrdinalIgnoreCase))
                        return link;
                    continue;
                }
                var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, full }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.WaitForExit(10000);
                if (Directory.Exists(link))
                {
                    log?.Invoke($"路径里有当前系统语言表示不了的字符，改用联接 {link} → {full}");
                    return link;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
        log?.Invoke("路径里有当前系统语言表示不了的字符，而且建不了英文名的联接，游戏可能启动不了");
        return full;
    }

    /// <summary>
    /// 1.6 以前的 Minecraft 和老 FML 不认 --gameDir，固定用 %APPDATA%\.minecraft 做“家目录”（日志、lib、coremods 都写在那）。
    /// 给这个实例建 本地数据\Amber\legacyhome\哈希\.minecraft → 实例目录的联接，启动时只给这个游戏进程把 APPDATA 指过去，
    /// 用户真正的 .minecraft 就不会被写。返回要设成 APPDATA 的目录；建不了时返回 null。
    /// </summary>
    public static string? LegacyHome(string workDir, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var full = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar);
        var name = "h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..10];
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amber", "legacyhome", name);
        if (!Representable(parent)) parent = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "AmberLegacyHome", name);
        var link = Path.Combine(parent, ".minecraft");
        try
        {
            Directory.CreateDirectory(parent);
            var info = new DirectoryInfo(link);
            if (info.Exists && info.LinkTarget is { } target && Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar).Equals(full, StringComparison.OrdinalIgnoreCase))
                return parent;
            if (info.Exists)
            {
                // 旧联接指向别处（实例搬家了）：只删联接本身，不碰里面的东西；是真文件夹就不动。
                if (info.LinkTarget is null) return null;
                info.Delete();
            }
            if (CreateJunction(link, full)) return parent;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke("[Amber] 建不了旧版本用的家目录联接：" + e.Message);
        }
        return null;
    }

    private static bool CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
        return Directory.Exists(link);
    }

    /// <summary>放联接的地方：先 Amber 的本地数据目录，再公共目录，最后目标所在盘的根目录。要求本身是纯英文。</summary>
    private static IEnumerable<string> LinkParents(string target)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amber", "links"),
            Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "AmberLinks"),
            Path.Combine(Path.GetPathRoot(target) ?? @"C:\", "AmberLinks"),
        };
        return candidates.Where(c => c.All(ch => ch < 128));
    }

    /// <summary>把 path 里 from 开头的部分换成 to。</summary>
    public static string Remap(string path, string from, string to)
    {
        var prefix = from.TrimEnd(Path.DirectorySeparatorChar);
        if (from == to || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path;
        var rest = path[prefix.Length..];
        return rest.Length == 0 || rest[0] == Path.DirectorySeparatorChar ? to + rest : path;
    }
}
