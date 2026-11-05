using Amber.Core.Localization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Amber.Core.Diagnostics;

public enum CrashSeverity { Likely, Possible }

public sealed record CrashFinding(string Title, string Advice, CrashSeverity Severity = CrashSeverity.Likely, string? Evidence = null);

public sealed record CrashAnalysis(IReadOnlyList<CrashFinding> Findings, IReadOnlyList<string> SuspectMods, IReadOnlyList<string> SourceFiles)
{
    public string Summary => Findings.Count == 0
        ? L.T("core.crash.summary.unknown")
        : string.Join("\n", Findings.Select(f => L.F("core.crash.summary.finding", f.Title, f.Advice)));
}

/// <summary>
/// 按日志内容判断崩溃原因。规则自己写的，一条规则只看一种明确的特征；
/// 宁可说不知道，也不给没有依据的结论。
/// </summary>
public static partial class CrashAnalyzer
{
    private sealed record Rule(Regex Pattern, Func<Match, CrashFinding> Build);

    /// <param name="key">翻译键的前缀：标题是 key.title，建议是 key.advice。匹配到时才取文字，换语言后马上生效。</param>
    private static Rule R(string pattern, string key, CrashSeverity severity = CrashSeverity.Likely) =>
        new(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), m => new CrashFinding(L.T(key + ".title"), L.T(key + ".advice"), severity, m.Value));

    private static Rule R(string pattern, Func<Match, CrashFinding> build) =>
        new(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), build);

    private static readonly Rule[] Rules =
    [
        // 内存
        R(@"java\.lang\.OutOfMemoryError", "core.crash.memory.exhausted"),
        R(@"Could not reserve enough space for \d+\w* object heap", "core.crash.memory.heapreservation"),
        R(@"There is insufficient memory for the Java Runtime", "core.crash.memory.system"),

        // Java 版本
        R(@"has been compiled by a more recent version of the Java Runtime \(class file version (\d+)", m =>
            new CrashFinding(L.F("core.crash.java.tooold.title", int.Parse(m.Groups[1].Value) - 44), L.T("core.crash.java.tooold.advice"), CrashSeverity.Likely, m.Value)),
        R(@"AppClassLoader cannot be cast to (class )?java\.net\.URLClassLoader", "core.crash.java.eightrequired"),
        R(@"InaccessibleObjectException|Unable to make .{1,200} accessible", "core.crash.java.toonew"),
        R(@"Unsupported class file major version (\d+)", m =>
            new CrashFinding(L.F("core.crash.java.unsupportedclass.title", m.Groups[1].Value), L.T("core.crash.java.unsupportedclass.advice"), CrashSeverity.Likely, m.Value)),
        R(@"The downloaded file (\S+) has an invalid checksum|files\.minecraftforge\.net/fmllibs", m =>
            new CrashFinding(m.Groups[1].Success ? L.F("core.crash.forge.librarydownload.file", m.Groups[1].Value) : L.T("core.crash.forge.librarydownload.title"), L.T("core.crash.forge.librarydownload.advice"), CrashSeverity.Likely, m.Value)),
        R(@"could not find java\.dll|Could not find Java SE Runtime Environment", "core.crash.java.missingfiles"),
        R(@"Error occurred during initialization of VM\s+.{0,120}", "core.crash.java.vmstartup"),
        R(@"Unrecognized (VM|command-line) option", "core.crash.java.unsupportedoption"),

        // 模组依赖与冲突
        R(@"(Found|Duplicate) .{0,40}duplicate mods?|DuplicateModsFoundException|Mod .{1,80} is (present|loaded) .{0,40}multiple", "core.crash.mods.duplicate"),
        R(@"Incompatible mods? found|net\.fabricmc\.loader\.impl\.FormattedException", "core.crash.mods.incompatible"),
        R(@"requires (version )?.{1,80} of (mod )?'?([\w-]+)'?,? (but|which)", m =>
            new CrashFinding(L.F("core.crash.mods.dependencyversion.title", m.Groups[3].Value), L.T("core.crash.mods.dependencyversion.advice"), CrashSeverity.Likely, m.Value)),
        R(@"Missing or unsupported mandatory dependencies:?[\s\S]{0,300}", m =>
            new CrashFinding(L.T("core.crash.mods.mandatorydependencies.title"), L.T("core.crash.mods.mandatorydependencies.advice"), CrashSeverity.Likely, m.Value)),
        R(@"Mod (\S+) requires (\S+)", m =>
            new CrashFinding(L.F("core.crash.mods.requires.title", m.Groups[1].Value, m.Groups[2].Value), L.T("core.crash.mods.requires.advice"), CrashSeverity.Likely, m.Value)),
        R(@"(org\.sinytra\.connector|dev\.su5ed\.sinytra\.connector)[\s\S]{0,2000}?(Exception|Error)|Sinytra Connector.{0,60}(failed|error|incompatible)", m =>
            new CrashFinding(L.T("core.crash.mods.connector.title"), L.T("core.crash.mods.connector.advice"), CrashSeverity.Likely, m.Value.Length > 300 ? m.Value[..300] : m.Value)),
        R(@"(Invalid|Failed to apply) mixin|MixinApplyError|InvalidMixinException|Mixin apply for mod (\S+) failed|Mixin transformation of .{1,120} failed", m =>
            new CrashFinding(m.Groups[2].Success ? L.F("core.crash.mods.mixin.namedtitle", m.Groups[2].Value) : L.T("core.crash.mods.mixin.title"), L.T("core.crash.mods.mixin.advice"), CrashSeverity.Likely, m.Value)),
        // 崩溃报告的系统信息里同时写着 OptiFine 和 Forge 版本：OptiFine 只适配某几个 Forge 版本，换 Forge 后最常见的就是 NoSuchMethodError。
        R(@"(NoSuchMethodError|NoSuchFieldError)[\s\S]{0,60000}OptiFine Version: (OptiFine_\S+)[\s\S]{0,20000}Forge: net\.minecraftforge:(\S+)", m =>
            new CrashFinding(L.F("core.crash.mods.optifineforge.title", m.Groups[2].Value, m.Groups[3].Value), L.T("core.crash.mods.optifineforge.advice"), CrashSeverity.Likely, m.Groups[1].Value)),
        R(@"NoSuchMethodError|NoSuchFieldError", "core.crash.mods.versionmismatch", CrashSeverity.Possible),
        R(@"ClassNotFoundException: (\S+)|NoClassDefFoundError: (\S+)", m =>
            new CrashFinding(L.F("core.crash.mods.missingclass.title", (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)), L.T("core.crash.mods.missingclass.advice"), CrashSeverity.Possible, m.Value)),
        R(@"optifine[\s\S]{0,400}(ClassCastException|NoSuchMethodError|TransformerException)", "core.crash.mods.optifineconflict"),

        // 文件损坏
        R(@"ZipException|zip file (is )?(closed|empty|corrupt)|error in opening zip file|Invalid (or corrupt )?(jar|zip)|invalid (LOC|CEN) header", "core.crash.files.corrupt", CrashSeverity.Likely),
        R(@"Checksum|Hash mismatch|SHA-?1 mismatch", "core.crash.files.checksum", CrashSeverity.Possible),

        // 显卡与驱动
        R(@"Pixel format not accelerated|WGL_ARB_create_context|No OpenGL context|GLFW error 65542|Could not create context|WGL: The driver does not appear to support OpenGL", "core.crash.graphics.opengl"),
        R(@"Couldn't set pixel format|Display.create|Failed to create window|GLFW error 6500\d", "core.crash.graphics.window", CrashSeverity.Possible),
        R(@"EXCEPTION_ACCESS_VIOLATION[\s\S]{0,4000}(atio6axx|atioglxx|nvoglv|ig\d{1,2}icd|amdvlk|vulkan)", "core.crash.graphics.drivercrash"),
        R(@"Problematic frame:\s*#\s*C\s+\[(atio6axx|atioglxx|nvoglv\w*|ig\d{1,2}icd\w*)", "core.crash.graphics.driverframe"),
        R(@"Shader .{1,120}(compile|link).{0,20}(error|fail)|Failed to compile shader", "core.crash.graphics.shader", CrashSeverity.Possible),

        // 其他明确特征
        R(@"UnsatisfiedLinkError", "core.crash.native.load"),
        R(@"Manually triggered debug crash", "core.crash.debug.manual"),
        R(@"Exception ticking world|Exception in world tick|Ticking entity|Ticking block entity", "core.crash.world.ticking", CrashSeverity.Possible),
        R(@"Failed to load (config|configuration)|ParsingException.{0,80}\.(toml|json)|Failed loading config file", "core.crash.files.config"),
        R(@"session.?(server)?.{0,40}(Invalid|Failed to verify)|Bad login|Invalid session", "core.crash.auth.session", CrashSeverity.Possible),
        R(@"java\.net\.BindException|Address already in use", "core.crash.network.port", CrashSeverity.Possible),
    ];

    [GeneratedRegex(@"Suspected Mods?:\s*(.+)")]
    private static partial Regex SuspectedLine();

    [GeneratedRegex(@"^\s*Mod File:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex ModFileLine();

    [GeneratedRegex(@"Suspected Mods?:\s*\n((?:[ \t]+.+\n)+)")]
    private static partial Regex SuspectedBlock();

    [GeneratedRegex(@"[\s(]([A-Za-z][\w .-]{1,60}) \(([\w-]+)\)")]
    private static partial Regex ModNameAndId();

    /// <summary>分析一段文本（崩溃报告、日志拼起来都行）。</summary>
    public static CrashAnalysis Analyze(string text, IReadOnlyList<string>? sourceFiles = null)
    {
        var findings = new List<CrashFinding>();
        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(text);
            if (match.Success) findings.Add(rule.Build(match) with { Evidence = Clip(match.Value) });
        }
        // 同样的标题只留一条，确定的排前面。
        var unique = findings.GroupBy(f => f.Title).Select(g => g.First()).OrderBy(f => f.Severity).ToList();
        return new CrashAnalysis(unique, FindSuspects(text), sourceFiles ?? []);
    }

    private static List<string> FindSuspects(string text)
    {
        var mods = new List<string>();
        foreach (Match m in SuspectedLine().Matches(text))
        {
            var value = m.Groups[1].Value.Trim();
            if (!value.Equals("None", StringComparison.OrdinalIgnoreCase) && value.Length > 0) mods.Add(value);
        }
        foreach (Match m in SuspectedBlock().Matches(text))
        {
            foreach (var line in m.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)) mods.Add(line.Trim());
        }
        foreach (Match m in ModFileLine().Matches(text))
        {
            var value = m.Groups[1].Value.Trim();
            if (value.Length > 0 && !value.Contains("NO FILE", StringComparison.OrdinalIgnoreCase))
                mods.Add(Path.GetFileName(value));
        }
        return mods.Distinct().Take(10).ToList();
    }

    private static string Clip(string value)
    {
        value = value.Trim();
        return value.Length <= 400 ? value : value[..400] + "…";
    }

    /// <summary>
    /// 分析一次运行：取启动之后产生的崩溃报告和 JVM 错误文件，加上 latest.log 和内存里的输出。
    /// pid 给了时只认这个进程的 hs_err_pid 文件；sharedWorkDir（期间另一局游戏也用这个目录）时
    /// 崩溃报告和 latest.log 可能是那一局的，只用自己的输出和自己的 hs_err_pid 文件。
    /// </summary>
    public static CrashAnalysis AnalyzeRun(string workDir, DateTime startedUtc, IEnumerable<string> recentOutput, int? pid = null, bool sharedWorkDir = false)
    {
        var sources = FindRunFiles(workDir, startedUtc, pid, sharedWorkDir);
        var parts = new List<string>();
        foreach (var path in sources)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                // 大日志只看结尾，错误都在后面。
                parts.Add(content.Length > 400_000 ? content[^400_000..] : content);
            }
            catch (IOException) { }
        }
        parts.Add(string.Join('\n', recentOutput));
        return Analyze(string.Join("\n\n", parts), sources);
    }

    public static List<string> FindRunFiles(string workDir, DateTime startedUtc, int? pid = null, bool sharedWorkDir = false)
    {
        var result = new List<string>();
        var crashDir = Path.Combine(workDir, "crash-reports");
        if (!sharedWorkDir && Directory.Exists(crashDir))
        {
            result.AddRange(Directory.EnumerateFiles(crashDir, "crash-*.txt")
                .Where(f => File.GetLastWriteTimeUtc(f) >= startedUtc)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(1));
        }
        if (Directory.Exists(workDir))
        {
            if (pid is { } id)
            {
                var own = Path.Combine(workDir, $"hs_err_pid{id}.log");
                if (File.Exists(own)) result.Add(own);
            }
            else if (!sharedWorkDir)
            {
                result.AddRange(Directory.EnumerateFiles(workDir, "hs_err_pid*.log")
                    .Where(f => File.GetLastWriteTimeUtc(f) >= startedUtc)
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(1));
            }
        }
        // 进程没来得及写日志时，latest.log 还是上一次的，不能算进来。
        var latest = Path.Combine(workDir, "logs", "latest.log");
        if (!sharedWorkDir && File.Exists(latest) && File.GetLastWriteTimeUtc(latest) >= startedUtc) result.Add(latest);
        return result;
    }

    /// <summary>把这次运行的日志打包，方便发给别人求助。账号令牌不会写进这些文件。</summary>
    public static void ExportBundle(string outputZip, IEnumerable<string> files, IEnumerable<string> launcherLog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
        // 保存对话框里确认过覆盖，旧文件直接替换。
        using var stream = new FileStream(outputZip, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files.Distinct())
        {
            if (!File.Exists(file)) continue;
            var entry = zip.CreateEntry(Path.GetFileName(file));
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            input.CopyTo(output);
        }
        using var writer = new StreamWriter(zip.CreateEntry("launcher-output.txt").Open());
        foreach (var line in launcherLog) writer.WriteLine(line);
    }
}
