using Amber.Core.Localization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amber.Core.Content;
using Amber.Core.Game;

namespace Amber.Core.Launch;

public sealed record JunkGroup(string Name, string Description, IReadOnlyList<string> Paths, long Bytes);

/// <summary>清理游戏垃圾：只动日志、崩溃报告、natives 和下载残留，不碰存档、模组和配置。</summary>
public static class Toolbox
{
    public static List<JunkGroup> ScanJunk(GameDirectory dir)
    {
        var logs = new List<string>();
        var crashes = new List<string>();
        var natives = new List<string>();
        var parts = new List<string>();

        foreach (var root in WorkDirs(dir))
        {
            AddFiles(logs, Path.Combine(root, "logs"), "*.log.gz");
            AddFiles(logs, Path.Combine(root, "logs"), "debug*.log*");
            AddFiles(crashes, Path.Combine(root, "crash-reports"), "*.txt");
            if (Directory.Exists(root)) crashes.AddRange(Directory.EnumerateFiles(root, "hs_err_pid*.log"));
        }
        foreach (var id in dir.ListInstalled())
        {
            var native = dir.NativesDir(id);
            if (Directory.Exists(native)) natives.Add(native);
        }
        foreach (var folder in new[] { dir.LibrariesDir, dir.VersionsDir, Path.Combine(dir.AssetsDir, "objects") })
        {
            if (Directory.Exists(folder)) parts.AddRange(Directory.EnumerateFiles(folder, "*.part", SearchOption.AllDirectories));
        }

        return
        [
            new JunkGroup(L.T("core.launch.cleanup.oldlogs"), L.T("core.launch.cleanup.oldlogsdescription"), logs, SizeOf(logs)),
            new JunkGroup(L.T("core.launch.cleanup.crashes"), L.T("core.launch.cleanup.crashesdescription"), crashes, SizeOf(crashes)),
            new JunkGroup("natives", L.T("core.launch.cleanup.nativesdescription"), natives, SizeOf(natives)),
            new JunkGroup(L.T("core.launch.cleanup.partialdownloads"), L.T("core.launch.cleanup.partialdownloadsdescription"), parts, SizeOf(parts)),
        ];
    }

    /// <summary>返回实际清掉的字节数。被占用的文件跳过。</summary>
    public static long Clean(IEnumerable<JunkGroup> groups)
    {
        long freed = 0;
        foreach (var path in groups.SelectMany(g => g.Paths))
        {
            try
            {
                var size = SizeOf([path]);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                freed += size;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 正在使用 */ }
        }
        return freed;
    }

    private static IEnumerable<string> WorkDirs(GameDirectory dir)
    {
        yield return dir.Root;
        foreach (var id in dir.ListInstalled()) yield return dir.VersionDir(id);
    }

    private static void AddFiles(List<string> list, string folder, string pattern)
    {
        if (Directory.Exists(folder)) list.AddRange(Directory.EnumerateFiles(folder, pattern));
    }

    public static long SizeOf(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) total += new FileInfo(path).Length;
                else if (Directory.Exists(path))
                    total += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>生成启动脚本，方便不开启动器直接跑。令牌会写进脚本，所以默认换成占位符。</summary>
    public static string WriteLaunchScript(string path, string javaPath, string workDir, IReadOnlyList<string> args, string accessToken)
    {
        var builder = new StringBuilder();
        if (OperatingSystem.IsWindows())
        {
            builder.AppendLine("@echo off").AppendLine("chcp 65001 >nul");
            builder.AppendLine($"cd /d \"{workDir}\"");
            builder.Append($"\"{javaPath}\"");
        }
        else
        {
            builder.AppendLine("#!/bin/sh");
            builder.AppendLine($"cd \"{workDir}\"");
            builder.Append($"\"{javaPath}\"");
        }
        foreach (var arg in args)
        {
            var value = accessToken.Length > 0 ? arg.Replace(accessToken, "REPLACE_WITH_TOKEN") : arg;
            builder.Append(' ').Append(value.Contains(' ') ? $"\"{value}\"" : value);
        }
        builder.AppendLine();
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>版本文件的大小统计，用于实例页显示。</summary>
    public static long InstanceSize(GameInstance instance) => SizeOf([instance.WorkDir]);
}

public sealed record UpdateInfo(string Version, string? Url, string? Notes);

/// <summary>
/// 更新检查：只在用户配置了更新地址时才联网，只提示，不下载也不替换。
/// 地址可以指向 GitHub Releases API，或任何返回 {tag_name, html_url, body} 的 JSON。
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    public static string CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>没有配置地址、或已是最新时返回 null。</summary>
    public async Task<UpdateInfo?> CheckAsync(string? feedUrl, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return null;
        using var doc = JsonDocument.Parse(await http.GetStringAsync(feedUrl, ct));
        var root = doc.RootElement;
        var tag = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : root.TryGetProperty("version", out var v) ? v.GetString() : null)
                  ?? throw new InvalidDataException(L.T("core.launch.update.missingversion"));
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : root.TryGetProperty("url", out var u2) ? u2.GetString() : null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return IsNewer(tag, currentVersion) ? new UpdateInfo(tag.TrimStart('v'), url, notes) : null;
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Normalize(candidate), out var a) && Version.TryParse(Normalize(current), out var b) && a > b;

    private static string Normalize(string v)
    {
        v = v.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return v.Contains('.') ? v : v + ".0";
    }
}
