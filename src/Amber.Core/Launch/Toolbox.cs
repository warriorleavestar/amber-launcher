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
        var script = BuildLaunchScript(javaPath, workDir, args, accessToken, OperatingSystem.IsWindows());
        File.WriteAllText(path, script, new UTF8Encoding(false));
        return path;
    }

    internal static string BuildLaunchScript(string javaPath, string workDir, IReadOnlyList<string> args, string accessToken, bool windows)
    {
        // NUL 无法传给操作系统。
        if (javaPath.Contains('\0') || workDir.Contains('\0') || args.Any(a => a.Contains('\0')))
            throw new ArgumentException(L.T("core.launch.script.invaliddata"));
        var values = args.Select(a => accessToken.Length > 0 ? a.Replace(accessToken, "REPLACE_WITH_TOKEN") : a).ToArray();
        var builder = new StringBuilder();
        if (windows)
        {
            // cmd 只执行固定引导代码；路径和参数作为 JSON 数据保存，不参与任何 shell 解析。
            // 不使用 Java 参数文件，以兼容 Java 8；大 classpath 也不会挤占 cmd 的命令长度。
            builder.AppendLine("@echo off").AppendLine("setlocal DisableDelayedExpansion");
            builder.Append("rem ").AppendLine(L.T("core.launch.script.editnote"));
            builder.AppendLine("set \"AMBER_LAUNCH_SCRIPT=%~f0\"");
            builder.Append("\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"")
                .Append(WindowsBootstrap.ReplaceLineEndings(" ")).AppendLine("\"");
            builder.AppendLine("exit /b %errorlevel%");
            builder.AppendLine("# AMBER_LAUNCH_DATA_V1");
            builder.AppendLine(JsonSerializer.Serialize(new[] { javaPath, workDir }.Concat(values).ToArray(), AmberJsonContext.Default.StringArray));
        }
        else
        {
            builder.AppendLine("#!/bin/sh");
            builder.Append("cd -- ").Append(ShellQuote(workDir)).AppendLine(" || exit 1");
            builder.Append("exec ").Append(ShellQuote(javaPath));
            foreach (var value in values) builder.Append(' ').Append(ShellQuote(value));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    // 固定的 PowerShell 程序：逐项按 Win32/CRT 规则引用参数；正文绝不拼接实例数据。
    private const string WindowsBootstrap = """
        $ErrorActionPreference='Stop';
        function Quote([string]$value){
            $result=New-Object Text.StringBuilder;
            [void]$result.Append([char]34);$slashes=0;
            foreach($c in $value.ToCharArray()){
                if($c -eq [char]92){$slashes++;continue};
                if($c -eq [char]34){[void]$result.Append([char]92,(2*$slashes+1))}else{[void]$result.Append([char]92,$slashes)};
                [void]$result.Append($c);$slashes=0;
            };
            [void]$result.Append([char]92,(2*$slashes));[void]$result.Append([char]34);return $result.ToString();
        };
        try{
            $lines=[IO.File]::ReadAllLines($env:AMBER_LAUNCH_SCRIPT);
            $i=[Array]::IndexOf($lines,'# AMBER_LAUNCH_DATA_V1');if($i -lt 0){throw 'Missing launch data'};
            $data=ConvertFrom-Json -InputObject $lines[$i+1];if($data.Count -lt 2){throw 'Invalid launch data'};
            foreach($item in $data){if($item -isnot [string] -or $item.Contains([char]0)){throw 'Invalid launch data'}};
            $quoted=New-Object 'Collections.Generic.List[string]';
            for($n=2;$n -lt $data.Count;$n++){$quoted.Add((Quote $data[$n]))};
            $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$data[0];$start.WorkingDirectory=$data[1];
            $start.Arguments=[string]::Join(' ',$quoted);$start.UseShellExecute=$false;
            $process=[Diagnostics.Process]::Start($start);$process.WaitForExit();exit $process.ExitCode;
        }catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}
        """;

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
        if (url is not null)
        {
            if (!TryGetSafeUpdateUrl(url, out var validated)) throw new InvalidDataException(L.T("tools.download.invalid_url"));
            url = validated!.AbsoluteUri;
        }
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return IsNewer(tag, currentVersion) ? new UpdateInfo(tag.TrimStart('v'), url, notes) : null;
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Normalize(candidate), out var a) && Version.TryParse(Normalize(current), out var b) && a > b;

    /// <summary>更新页只允许无凭据和控制字符的绝对 HTTP(S) 地址，不把本机路径或自定义协议交给系统。</summary>
    public static bool TryGetSafeUpdateUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
            parsed.UserInfo.Length > 0 || parsed.Host.Length == 0 || parsed.IsFile || parsed.IsUnc ||
            Uri.UnescapeDataString(value).Any(char.IsControl)) return false;
        uri = parsed;
        return true;
    }

    private static string Normalize(string v)
    {
        v = v.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return v.Contains('.') ? v : v + ".0";
    }
}
