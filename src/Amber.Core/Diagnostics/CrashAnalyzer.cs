using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Amber.Core.Diagnostics;

public enum CrashSeverity { Likely, Possible }

public sealed record CrashFinding(string Title, string Advice, CrashSeverity Severity = CrashSeverity.Likely, string? Evidence = null);

public sealed record CrashAnalysis(IReadOnlyList<CrashFinding> Findings, IReadOnlyList<string> SuspectMods, IReadOnlyList<string> SourceFiles)
{
    public string Summary => Findings.Count == 0
        ? "没有识别出已知原因，可以把日志导出后发给懂的人看。"
        : string.Join("\n", Findings.Select(f => $"• {f.Title}：{f.Advice}"));
}

/// <summary>
/// 按日志内容判断崩溃原因。规则自己写的，一条规则只看一种明确的特征；
/// 宁可说不知道，也不给没有依据的结论。
/// </summary>
public static partial class CrashAnalyzer
{
    private sealed record Rule(Regex Pattern, Func<Match, CrashFinding> Build);

    private static Rule R(string pattern, string title, string advice, CrashSeverity severity = CrashSeverity.Likely) =>
        new(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), m => new CrashFinding(title, advice, severity, m.Value));

    private static Rule R(string pattern, Func<Match, CrashFinding> build) =>
        new(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), build);

    private static readonly Rule[] Rules =
    [
        // 内存
        R(@"java\.lang\.OutOfMemoryError", "内存不够用", "在实例设置里调大最大内存，或者减少模组和光影。"),
        R(@"Could not reserve enough space for \d+\w* object heap", "Java 申请不到设置的内存", "把最大内存调小；如果用的是 32 位 Java，换成 64 位。"),
        R(@"There is insufficient memory for the Java Runtime", "系统可用内存不足", "关掉其他程序或者把最大内存调小。"),

        // Java 版本
        R(@"has been compiled by a more recent version of the Java Runtime \(class file version (\d+)", m =>
            new CrashFinding($"Java 版本太旧，需要 Java {int.Parse(m.Groups[1].Value) - 44} 或更高", "在实例设置里把 Java 改成自动，或选一个足够新的版本。", CrashSeverity.Likely, m.Value)),
        R(@"AppClassLoader cannot be cast to (class )?java\.net\.URLClassLoader", "这个版本要用 Java 8", "旧版 Forge 和 LiteLoader 不支持 Java 9 以上，把实例的 Java 改成 Java 8。"),
        R(@"InaccessibleObjectException|Unable to make .{1,200} accessible", "Java 版本太新", "这个版本的加载器不支持当前 Java。把实例的 Java 改成自动，会按游戏要求选择。"),
        R(@"Unsupported class file major version (\d+)", m =>
            new CrashFinding($"Java 太新，加载器不认识版本号 {m.Groups[1].Value} 的类文件", "把实例的 Java 改成自动，或升级加载器。", CrashSeverity.Likely, m.Value)),
        R(@"Error occurred during initialization of VM\s+.{0,120}", "Java 虚拟机启动失败", "检查实例设置里的 JVM 参数，把可疑的参数删掉再试。"),
        R(@"Unrecognized (VM|command-line) option", "JVM 参数不被这个 Java 支持", "检查实例设置里的 JVM 参数，删掉不支持的那项。"),

        // 模组依赖与冲突
        R(@"(Found|Duplicate) .{0,40}duplicate mods?|DuplicateModsFoundException|Mod .{1,80} is (present|loaded) .{0,40}multiple", "同一个模组装了不止一份", "在模组页找到重复的模组，只留一个版本。"),
        R(@"Incompatible mods? found|net\.fabricmc\.loader\.impl\.FormattedException", "模组之间不兼容或缺少依赖", "看日志里 Fabric 列出的模组和版本要求，安装或更换对应模组。"),
        R(@"requires (version )?.{1,80} of (mod )?'?([\w-]+)'?,? (but|which)", m =>
            new CrashFinding($"缺少依赖或版本不对：{m.Groups[3].Value}", "安装模组要求的依赖版本。资源页安装模组时会自动带上必需依赖。", CrashSeverity.Likely, m.Value)),
        R(@"Missing or unsupported mandatory dependencies:?[\s\S]{0,300}", m =>
            new CrashFinding("缺少必需的模组依赖", "看证据里列出的模组名，把它们装上或换成匹配的版本。", CrashSeverity.Likely, m.Value)),
        R(@"Mod (\S+) requires (\S+)", m =>
            new CrashFinding($"{m.Groups[1].Value} 需要 {m.Groups[2].Value}", "安装它要求的依赖。", CrashSeverity.Likely, m.Value)),
        R(@"(Invalid|Failed to apply) mixin|MixinApplyError|InvalidMixinException|Mixin apply for mod (\S+) failed|Mixin transformation of .{1,120} failed", m =>
            new CrashFinding(m.Groups[2].Success ? $"模组 {m.Groups[2].Value} 的 Mixin 注入失败" : "模组的 Mixin 注入失败", "通常是模组版本和游戏版本不匹配，或者两个模组冲突。看疑似模组，先更新或禁用它。", CrashSeverity.Likely, m.Value)),
        // 崩溃报告的系统信息里同时写着 OptiFine 和 Forge 版本：OptiFine 只适配某几个 Forge 版本，换 Forge 后最常见的就是 NoSuchMethodError。
        R(@"(NoSuchMethodError|NoSuchFieldError)[\s\S]{0,60000}OptiFine Version: (OptiFine_\S+)[\s\S]{0,20000}Forge: net\.minecraftforge:(\S+)", m =>
            new CrashFinding($"{m.Groups[2].Value} 和 Forge {m.Groups[3].Value} 不兼容", "OptiFine 只支持特定的 Forge 版本（下载页选 OptiFine 时，版本旁边写着对应的 Forge）。换成那个 Forge 版本，或者去掉 OptiFine。", CrashSeverity.Likely, m.Groups[1].Value)),
        R(@"NoSuchMethodError|NoSuchFieldError", "模组和游戏或其他模组版本不匹配", "更新模组，确认下载的是这个游戏版本和加载器用的文件。", CrashSeverity.Possible),
        R(@"ClassNotFoundException: (\S+)|NoClassDefFoundError: (\S+)", m =>
            new CrashFinding($"找不到类 {(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}", "可能缺少前置模组，或者文件不完整。", CrashSeverity.Possible, m.Value)),
        R(@"optifine[\s\S]{0,400}(ClassCastException|NoSuchMethodError|TransformerException)", "OptiFine 和其他模组冲突", "OptiFine 和很多模组不兼容，先把它禁用试试。Fabric 上可以换成 Sodium + Iris。"),

        // 文件损坏
        R(@"ZipException|zip file (is )?(closed|empty|corrupt)|error in opening zip file|Invalid (or corrupt )?(jar|zip)|invalid (LOC|CEN) header", "有文件损坏", "到实例页用“补全文件”重新校验，模组文件损坏就删掉重下。", CrashSeverity.Likely),
        R(@"Checksum|Hash mismatch|SHA-?1 mismatch", "文件校验不通过", "重新补全这个版本的文件。", CrashSeverity.Possible),

        // 显卡与驱动
        R(@"Pixel format not accelerated|WGL_ARB_create_context|No OpenGL context|GLFW error 65542|Could not create context|WGL: The driver does not appear to support OpenGL", "显卡驱动不支持 OpenGL", "更新显卡驱动。笔记本把 Java 设为用独立显卡运行。"),
        R(@"Couldn't set pixel format|Display.create|Failed to create window|GLFW error 6500\d", "创建游戏窗口失败", "更新显卡驱动，或者删掉实例里 options.txt 中异常的分辨率设置。", CrashSeverity.Possible),
        R(@"EXCEPTION_ACCESS_VIOLATION[\s\S]{0,4000}(atio6axx|atioglxx|nvoglv|ig\d{1,2}icd|amdvlk|vulkan)", "显卡驱动崩溃", "更新或回退显卡驱动；关掉光影试试。"),
        R(@"Problematic frame:\s*#\s*C\s+\[(atio6axx|atioglxx|nvoglv\w*|ig\d{1,2}icd\w*)", "崩溃发生在显卡驱动里", "更新或回退显卡驱动。"),
        R(@"Shader .{1,120}(compile|link).{0,20}(error|fail)|Failed to compile shader", "光影着色器编译失败", "换一个光影包或关掉光影。", CrashSeverity.Possible),

        // 其他明确特征
        R(@"UnsatisfiedLinkError", "本地库加载失败", "可能是 natives 不完整或 Java 位数不对。到工具箱清理 natives 后重新启动。"),
        R(@"Manually triggered debug crash", "这是手动触发的调试崩溃", "按住 F3+C 会主动让游戏崩溃，不是故障。"),
        R(@"Exception ticking world|Exception in world tick|Ticking entity|Ticking block entity", "存档里的实体或方块导致崩溃", "先备份存档。看疑似模组，或用模组提供的工具清除出问题的实体。", CrashSeverity.Possible),
        R(@"Failed to load (config|configuration)|ParsingException.{0,80}\.(toml|json)|Failed loading config file", "配置文件损坏", "删掉 config 文件夹里报错的那个文件，模组会重新生成。"),
        R(@"session.?(server)?.{0,40}(Invalid|Failed to verify)|Bad login|Invalid session", "登录会话无效", "到账号页重新登录。", CrashSeverity.Possible),
        R(@"java\.net\.BindException|Address already in use", "网络端口被占用", "关掉占用端口的程序再试。", CrashSeverity.Possible),
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

    /// <summary>分析一次运行：取启动之后产生的崩溃报告和 JVM 错误文件，加上 latest.log 和内存里的输出。</summary>
    public static CrashAnalysis AnalyzeRun(string workDir, DateTime startedUtc, IEnumerable<string> recentOutput)
    {
        var sources = FindRunFiles(workDir, startedUtc);
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

    public static List<string> FindRunFiles(string workDir, DateTime startedUtc)
    {
        var result = new List<string>();
        var crashDir = Path.Combine(workDir, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            result.AddRange(Directory.EnumerateFiles(crashDir, "crash-*.txt")
                .Where(f => File.GetLastWriteTimeUtc(f) >= startedUtc)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(1));
        }
        if (Directory.Exists(workDir))
        {
            result.AddRange(Directory.EnumerateFiles(workDir, "hs_err_pid*.log")
                .Where(f => File.GetLastWriteTimeUtc(f) >= startedUtc)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(1));
        }
        // 进程没来得及写日志时，latest.log 还是上一次的，不能算进来。
        var latest = Path.Combine(workDir, "logs", "latest.log");
        if (File.Exists(latest) && File.GetLastWriteTimeUtc(latest) >= startedUtc) result.Add(latest);
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
