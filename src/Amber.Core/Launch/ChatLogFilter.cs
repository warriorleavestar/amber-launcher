using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>
/// 无痕模式：游戏照常写 logs/latest.log，但聊天行不写进去。
/// 做法是在游戏用的 log4j2 配置基础上，给每个写文件的 appender 加一个 RegexFilter，生成一份新配置交给游戏；
/// 控制台输出（Amber 只放在内存里显示）不受影响。
/// 配置来自版本文件的 logging.client（原版、Fabric、Quilt、1.12 以前的 Forge 继承原版的），
/// Forge 1.13+ 和 NeoForge 的版本文件把 logging 清空了，用的是加载器 jar 里自带的 log4j2.xml，就从 classpath 里取出来改。
/// 没有 log4j 的老版本（1.7 以前）本来就不写 latest.log。
/// </summary>
public static partial class ChatLogFilter
{
    /// <summary>
    /// 聊天行（宁可多滤也不漏）：
    /// - 客户端收到的聊天都带“[CHAT]”（1.19 起前面还有“[System] ”）；
    /// - 本机开的单人或局域网世界，内置服务器记的玩家聊天“&lt;名字&gt; 内容”（显示名可以有空格、队伍前缀），
    ///   /say 的“[名字] 内容”，/me 的“* 名字 内容”；
    /// - 1.19 起前面可能有“[Not Secure] ”“[Modified] ”。
    /// </summary>
    public const string ChatRegex = @"(?s)(.*\[CHAT\].*|((\[Not Secure\]|\[Modified\]) )*(<[^<>]{1,64}> |\[[^\[\]]{1,64}\] |\* \S{1,64} ).*)";

    private static readonly HashSet<string> FileAppenders = ["RollingRandomAccessFile", "RollingFile", "RandomAccessFile", "File"];

    /// <summary>log4j2 的 RegexFilter 用 Java 的 matches()（整条消息匹配），这里同样整条匹配。</summary>
    [GeneratedRegex(@"\A(?:" + ChatRegex + @")\z")]
    private static partial Regex WholeMessage();

    /// <summary>控制台一行的前缀“[12:00:00] [Render thread/INFO]: ”（有些版本还带“[来源]”）。</summary>
    [GeneratedRegex(@"^\[[^\]]*\] \[[^\]]*\](?: \[[^\]]*\])?: ")]
    private static partial Regex ConsolePrefix();

    [GeneratedRegex(@"<!\[CDATA\[(.*?)(\]\]>|$)", RegexOptions.Singleline)]
    private static partial Regex Cdata();

    /// <summary>这条日志消息是不是聊天（和游戏日志里的过滤规则一样）。</summary>
    public static bool IsChat(string message) => WholeMessage().IsMatch(message);

    /// <summary>
    /// 游戏控制台的一行原始输出是不是聊天：原版的 XML 事件取 CDATA 里的消息，普通文本去掉时间和线程前缀再比。
    /// 带“[CHAT]”的一律算。
    /// </summary>
    public static bool IsChatLine(string line)
    {
        if (line.Contains("[CHAT]", StringComparison.Ordinal)) return true;
        if (Cdata().Match(line) is { Success: true } cdata) return IsChat(cdata.Groups[1].Value);
        return IsChat(ConsolePrefix().Replace(line, "", 1));
    }

    /// <summary>去掉聊天行（无痕模式下导出错误报告用）。</summary>
    public static IEnumerable<string> WithoutChat(IEnumerable<string> lines) => lines.Where(l => !IsChatLine(l));

    /// <summary>这个版本的版本文件里有没有 log4j2 配置。</summary>
    public static bool Supported(VersionJson version) => version.Logging?.GetValueOrDefault("client") is { Argument: not null, File: not null };

    /// <summary>classpath 里有 log4j（有的话游戏会写 latest.log）。</summary>
    public static bool UsesLog4j(IEnumerable<string> classpath) =>
        classpath.Any(p => Path.GetFileName(p).StartsWith("log4j-core", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从 classpath 的 jar 里找加载器自带的 log4j2.xml（Forge 1.13+ 的 fmlloader、NeoForge 的 loader），复制到 target，返回 target；找不到返回 null。
    /// 加载器的 jar 先找，其他 jar 按 classpath 顺序。
    /// </summary>
    public static string? ExtractFromClasspath(IEnumerable<string> classpath, string target)
    {
        var jars = classpath.Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        static bool IsLoader(string p) => p.Contains("forge", StringComparison.OrdinalIgnoreCase) || p.Contains("neoforged", StringComparison.OrdinalIgnoreCase)
                                          || p.Contains("fml", StringComparison.OrdinalIgnoreCase);
        foreach (var jar in jars.Where(IsLoader).Concat(jars.Where(p => !IsLoader(p))))
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                if (zip.GetEntry("log4j2.xml") is not { } entry) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".tmp";
                entry.ExtractToFile(temp, true);
                File.Move(temp, target, true);
                return target;
            }
            catch (InvalidDataException) { }
        }
        return null;
    }

    /// <summary>加载器的名字（提示里说是哪个加载器的问题）。</summary>
    public static string LoaderName(VersionJson version)
    {
        var names = version.Libraries.Select(l => l.Name).ToList();
        if (names.Any(n => n.StartsWith("net.neoforged", StringComparison.Ordinal))) return "NeoForge";
        if (names.Any(n => n.StartsWith("net.minecraftforge", StringComparison.Ordinal))) return "Forge";
        if (names.Any(n => n.StartsWith("org.quiltmc", StringComparison.Ordinal))) return "Quilt";
        if (names.Any(n => n.StartsWith("net.fabricmc", StringComparison.Ordinal))) return "Fabric";
        return version.Id;
    }

    /// <summary>在原配置旁边生成过滤掉聊天的配置（每次重写），返回它的路径。写不了时抛异常：宁可不启动，也不偷偷记下聊天。</summary>
    public static string Prepare(string originalPath)
    {
        var doc = XDocument.Load(originalPath, LoadOptions.PreserveWhitespace);
        var added = Transform(doc);
        if (added == 0) throw new InvalidDataException(L.T("core.launch.notrace.no_file_appender"));
        var target = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + ".amber-notrace.xml");
        var temp = target + ".tmp";
        doc.Save(temp);
        File.Move(temp, target, true);
        return target;
    }

    /// <summary>给每个写文件的 appender 加聊天过滤，返回加了几个。</summary>
    public static int Transform(XDocument doc)
    {
        var count = 0;
        foreach (var appender in doc.Descendants().Where(e => FileAppenders.Contains(e.Name.LocalName)).ToList())
        {
            var ns = appender.Name.Namespace;
            var filter = new XElement(ns + "RegexFilter",
                new XAttribute("regex", ChatRegex), new XAttribute("onMatch", "DENY"), new XAttribute("onMismatch", "NEUTRAL"));
            // log4j2 的 appender 只认一个过滤器元素：已有 Filters 就放进去，已有单个过滤器就和它一起包进 Filters。
            var filters = appender.Elements().FirstOrDefault(e => e.Name.LocalName == "Filters");
            var single = appender.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Filter", StringComparison.Ordinal));
            if (filters is not null)
            {
                filters.AddFirst(filter);
            }
            else if (single is not null)
            {
                single.Remove();
                appender.AddFirst(new XElement(ns + "Filters", filter, single));
            }
            else
            {
                appender.AddFirst(filter);
            }
            count++;
        }
        return count;
    }
}
