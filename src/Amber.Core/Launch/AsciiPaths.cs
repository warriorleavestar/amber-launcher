using Amber.Core.Localization;
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
        var full = FullDirectoryPath(directory);
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
                    if (info.LinkTarget is { } target && FullDirectoryPath(target).Equals(full, StringComparison.OrdinalIgnoreCase))
                        return link;
                    continue;
                }
                WindowsJunction.Create(link, full);
                log?.Invoke(L.F("core.launch.paths.junctionfallback", link, full));
                return link;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
        log?.Invoke(L.T("core.launch.paths.junctionunavailable"));
        return full;
    }

    /// <summary>
    /// 1.6 以前的 Minecraft 和老 FML 不认 --gameDir，固定用系统的 minecraft 目录做“家目录”（日志、lib、coremods 都写在那）：
    /// Windows 是 %APPDATA%\.minecraft，Linux 是 ~/.minecraft，macOS 是 ~/Library/Application Support/minecraft。
    /// 给这个实例建 本地数据/Amber/legacyhome/哈希/（对应的相对位置）→ 实例目录的链接，启动时只给这个游戏进程把家目录指过去
    /// （Windows 改 APPDATA 环境变量，Mac/Linux 加 -Duser.home），用户真正的 minecraft 目录就不会被写。
    /// 返回要当作家目录的目录；建不了时返回 null。
    /// </summary>
    public static string? LegacyHome(string workDir, Action<string>? log = null) =>
        LegacyHome(workDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amber", "legacyhome"), log);

    internal static string? LegacyHome(string workDir, string root, Action<string>? log)
    {
        var full = FullDirectoryPath(workDir);
        var name = "h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsLinux() ? full : full.ToLowerInvariant())))[..10];
        var parent = Path.Combine(root, name);
        if (OperatingSystem.IsWindows() && !Representable(parent)) parent = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "AmberLegacyHome", name);
        var link = Path.Combine(parent, LegacyMinecraftDirectory());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var info = new DirectoryInfo(link);
            if (info.Exists && info.LinkTarget is { } target && FullDirectoryPath(Path.GetFullPath(target, Path.GetDirectoryName(link)!)).Equals(full, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return parent;
            if (info.Exists || info.LinkTarget is not null)
            {
                // 只移除旧链接本身，不碰目标和普通目录（悬空的旧链接 Exists 为 false，但 LinkTarget 有值）。
                if (info.LinkTarget is null) return null;
                info.Delete();
            }
            if (OperatingSystem.IsWindows()) WindowsJunction.Create(link, full);
            else Directory.CreateSymbolicLink(link, full);
            return parent;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke(L.F("core.launch.paths.homejunctionfailed", e.Message));
        }
        return null;
    }

    /// <summary>老版本游戏在家目录下找的 minecraft 目录（相对家目录）。</summary>
    internal static string LegacyMinecraftDirectory() =>
        OperatingSystem.IsMacOS() ? Path.Combine("Library", "Application Support", "minecraft") : ".minecraft";

    private static string FullDirectoryPath(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        // 本机磁盘的扩展路径与普通路径使用同一个联接缓存键和比较形式；UNC 保持原样。
        return OperatingSystem.IsWindows() && full.StartsWith(@"\\?\", StringComparison.Ordinal) && full.Length >= 7 &&
            char.IsAsciiLetter(full[4]) && full[5] == ':' && full[6] == '\\' ? full[4..] : full;
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

    /// <summary>
    /// 参数里带的路径（整个参数是路径、-javaagent:路径[=选项]、-D名字=路径）表示不了时，把它所在的目录换成英文联接。
    /// 认不出路径或者路径不存在时原样返回。
    /// </summary>
    public static string MapArgument(string arg, Action<string>? log = null) =>
        MapArgument(arg, Representable, dir => AliasIfNeeded(dir, log));

    internal static string MapArgument(string arg, Func<string, bool> representable, Func<string, string> alias)
    {
        if (representable(arg)) return arg;
        var (prefix, path, suffix) = SplitPathArgument(arg);
        if (path is null) return arg;
        string dir, rest;
        if (File.Exists(path)) (dir, rest) = (Path.GetDirectoryName(path)!, Path.DirectorySeparatorChar + Path.GetFileName(path));
        else if (Directory.Exists(path)) (dir, rest) = (path, "");
        else return arg;
        var mapped = alias(Path.TrimEndingDirectorySeparator(dir));
        if (rest.Length > 0)
        {
            // 测试、远程配置和跨平台工具可能在 Unix 上处理 Windows 路径；
            // 别把 C:\alias\file 拼成 C:\alias/file。
            var separator = mapped.Contains('\\') && !mapped.Contains('/') ? '\\' : Path.DirectorySeparatorChar;
            rest = separator + rest.TrimStart('/', '\\');
        }
        return prefix + mapped + rest + suffix;
    }

    /// <summary>拆出参数里的路径部分：前缀、路径、后缀（-javaagent 的 =选项）。不像路径时 path 为 null。</summary>
    internal static (string Prefix, string? Path, string Suffix) SplitPathArgument(string arg)
    {
        if (arg.StartsWith("-javaagent:", StringComparison.Ordinal))
        {
            var body = arg["-javaagent:".Length..];
            // 选项从 .jar 后面的 = 开始；路径里本身可能有 =，所以按 .jar= 找。
            var jarEnd = body.IndexOf(".jar=", StringComparison.OrdinalIgnoreCase);
            return jarEnd >= 0 ? ("-javaagent:", body[..(jarEnd + 4)], body[(jarEnd + 4)..]) : ("-javaagent:", body, "");
        }
        if (arg.StartsWith("-D", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 2 && LooksLikePath(arg[(eq + 1)..]))
            return (arg[..(eq + 1)], arg[(eq + 1)..], "");
        return LooksLikePath(arg) ? ("", arg, "") : ("", null, "");
    }

    private static bool LooksLikePath(string text) => Path.IsPathFullyQualified(text);

    /// <summary>把 path 里 from 开头的部分换成 to。</summary>
    public static string Remap(string path, string from, string to)
    {
        var prefix = from.TrimEnd('/', '\\');
        // 两种分隔符都可作为路径边界，但不能把系统根目录裁成空串或 C:。
        var root = Path.GetPathRoot(from);
        if (root is not null && prefix.Length < root.Length) prefix = root;
        if (from == to || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path;
        var rest = path[prefix.Length..];
        if (Path.EndsInDirectorySeparator(prefix)) return rest.Length == 0 ? to : Path.Combine(to, rest.TrimStart('/', '\\'));
        return rest.Length == 0 || rest[0] is '/' or '\\' ? to + rest : path;
    }
}
