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
    /// 写文件的 appender 上的过滤（所有日志都过一遍，所以只认不会和普通日志撞上的格式）：
    /// - 客户端收到的聊天都带“[CHAT]”（1.19 起前面还有“[System] ”）；
    /// - 内置服务器记的玩家聊天“&lt;名字&gt; 内容”（显示名可以有空格、队伍前缀），1.19 起前面可能有“[Not Secure] ”“[Modified] ”。
    /// </summary>
    public const string ChatRegex = @"(?s)(.*\[CHAT\].*|" + Signed + @"<[^<>]{1,64}> .*)";

    /// <summary>
    /// 只加在内置服务器的 logger（<see cref="ServerLogger"/>）上的过滤：除了上面的，还有 /say 的“[名字] 内容”（名字里可以再套一层方括号，
    /// 比如“[[Red] Steve] hi”）和 /me 的“* 名字 内容”。这两种格式模组日志里很常见（“[OptiFine] …”“[Indigo] …”），所以不放在全局过滤里。
    /// </summary>
    public const string ServerChatRegex = @"(?s)(.*\[CHAT\].*|" + Signed + @"(<[^<>]{1,64}> |\[(?:[^\[\]]|\[[^\[\]]{1,32}\]){1,64}\] |\* \S{1,64} ).*)";

    private const string Signed = @"((\[Not Secure\]|\[Modified\]) )*";

    /// <summary>
    /// 内置服务器写聊天、/say、/me 用的 logger：MinecraftServer 这个类名在各个版本里都不混淆（Forge、Fabric 下也一样），
    /// 1.17 以后按调用的类取名，还是它。
    /// </summary>
    public const string ServerLogger = "net.minecraft.server.MinecraftServer";

    /// <summary>log4j2 的配置元素名、属性名不分大小写（Mojang 自己的配置就用小写的 &lt;filters&gt;），这里也不分。</summary>
    private static readonly HashSet<string> FileAppenders = new(["RollingRandomAccessFile", "RollingFile", "RandomAccessFile", "File"], StringComparer.OrdinalIgnoreCase);

    private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static string? Attr(XElement e, string name) =>
        (string?)e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>log4j2 的 RegexFilter 用 Java 的 matches()（整条消息匹配），这里同样整条匹配。</summary>
    [GeneratedRegex(@"\A(?:" + ChatRegex + @")\z")]
    private static partial Regex WholeMessage();

    [GeneratedRegex(@"\A(?:" + ServerChatRegex + @")\z")]
    private static partial Regex WholeServerMessage();

    /// <summary>控制台文本行里线程名或 logger 指向内置服务器：“[Server thread/INFO]”或“[minecraft/MinecraftServer]”。</summary>
    [GeneratedRegex(@"^\[[^\]]*\] (?:\[Server thread/[^\]]*\]|\[[^\]]*\] \[[^\]]*MinecraftServer\])")]
    private static partial Regex ServerPrefix();

    /// <summary>原版 XML 事件的 logger 和线程。</summary>
    [GeneratedRegex(@"<log4j:Event\b[^>]*?\b(?:logger=""net\.minecraft\.server\.MinecraftServer""|thread=""Server thread"")")]
    private static partial Regex ServerEvent();

    /// <summary>控制台一行的前缀“[12:00:00] [Render thread/INFO]: ”（有些版本还带“[来源]”）。</summary>
    [GeneratedRegex(@"^\[[^\]]*\] \[[^\]]*\](?: \[[^\]]*\])?: ")]
    private static partial Regex ConsolePrefix();

    [GeneratedRegex(@"<!\[CDATA\[(.*?)(\]\]>|$)", RegexOptions.Singleline)]
    private static partial Regex Cdata();

    /// <summary>这条日志消息是不是聊天（和游戏日志里的过滤规则一样）。fromServer：是不是内置服务器的 logger 记的。</summary>
    public static bool IsChat(string message, bool fromServer = false) => (fromServer ? WholeServerMessage() : WholeMessage()).IsMatch(message);

    /// <summary>
    /// 游戏控制台的一行原始输出是不是聊天：原版的 XML 事件取 CDATA 里的消息，普通文本去掉时间和线程前缀再比。
    /// 带“[CHAT]”的一律算；“[名字] ”“* 名字 ”只在内置服务器线程或 logger 的行里算。启动器自己的“[Jay] ”和旧“[Amber] ”行不算。
    /// </summary>
    public static bool IsChatLine(string line)
    {
        if (IsLauncherLine(line)) return false;
        if (line.Contains("[CHAT]", StringComparison.Ordinal)) return true;
        if (Cdata().Match(line) is { Success: true } cdata) return IsChat(cdata.Groups[1].Value, ServerEvent().IsMatch(line));
        return IsChat(ConsolePrefix().Replace(line, "", 1), ServerPrefix().IsMatch(line));
    }

    private static bool IsLauncherLine(string line) =>
        line.StartsWith("[Jay] ", StringComparison.Ordinal) || line.StartsWith("[Amber] ", StringComparison.Ordinal);

    /// <summary>按完整日志事件去掉聊天，消息换行后的内容也属于同一事件。</summary>
    public static IEnumerable<string> WithoutChat(IEnumerable<string> lines)
    {
        var filter = new OutputFilter();
        // 先读完再筛选：XML 的事件头在消息之前，聊天标记也可能出现在消息的后续行。
        var output = lines.Select(filter.Add).ToList();
        return output.Where(l => !l.IsChat).Select(l => l.Text);
    }

    /// <summary>一行原始输出及其所属事件。事件后来被认出是聊天时，之前的行也一并排除。</summary>
    public sealed class OutputLine
    {
        private readonly OutputEvent _event;
        internal OutputLine(string text, OutputEvent outputEvent) { Text = text; _event = outputEvent; }
        public string Text { get; }
        public bool IsChat => _event.IsChat;
    }

    /// <summary>
    /// 在接收输出时识别事件，状态不依赖错误报告保留多少行。
    /// 原版 XML 按 Event 边界识别；Forge 文本按时间和线程前缀识别，其后的无前缀行包括消息续行和异常堆栈。
    /// </summary>
    public sealed class OutputFilter
    {
        private OutputEvent? _current;

        public OutputLine Add(string line)
        {
            // CDATA 内的文字不是 XML 标记，也不能冒充下一条日志或启动器自己的输出。
            if (_current?.InCdata != true && line.TrimStart().StartsWith("<log4j:Event", StringComparison.Ordinal))
                _current = new OutputEvent(isXml: true);
            else if (_current?.IsXml != true &&
                     (IsLauncherLine(line) || ConsolePrefix().IsMatch(line)))
                _current = new OutputEvent(isXml: false);

            _current ??= new OutputEvent(isXml: false);
            var result = new OutputLine(line, _current);
            _current.Observe(line);
            if (_current.Closed) _current = null;
            return result;
        }
    }

    internal sealed class OutputEvent(bool isXml)
    {
        private readonly System.Text.StringBuilder _tag = new();
        private bool _fromServer;
        private bool _inMessage;
        private bool _messageStarted;
        private bool _firstLine = true;

        public bool IsXml { get; } = isXml;
        public bool InCdata { get; private set; }
        public bool IsChat { get; private set; }
        public bool Closed { get; private set; }

        public void Observe(string line)
        {
            if (!IsXml)
            {
                if (_firstLine) IsChat = IsChatLine(line);
                else if (line.Contains("[CHAT]", StringComparison.Ordinal)) IsChat = true;
                _firstLine = false;
                return;
            }

            // 只识别消息部分；CDATA 里的 </log4j:Event> 等字样属于消息，不能提前结束过滤。
            for (var index = 0; index < line.Length;)
            {
                if (InCdata)
                {
                    var end = line.IndexOf("]]>", index, StringComparison.Ordinal);
                    ObserveMessage(line[index..(end < 0 ? line.Length : end)]);
                    if (end < 0) break;
                    InCdata = false;
                    index = end + 3;
                }
                else if (_tag.Length > 0)
                {
                    var end = line.IndexOf('>', index);
                    _tag.Append(line, index, (end < 0 ? line.Length : end + 1) - index);
                    if (end < 0) { _tag.Append('\n'); break; }
                    ObserveTag(_tag.ToString());
                    _tag.Clear();
                    index = end + 1;
                }
                else if (line.AsSpan(index).StartsWith("<![CDATA[", StringComparison.Ordinal))
                {
                    InCdata = true;
                    index += 9;
                }
                else if (line[index] == '<')
                {
                    _tag.Append('<');
                    index++;
                }
                else
                {
                    var end = line.IndexOf('<', index);
                    ObserveMessage(System.Net.WebUtility.HtmlDecode(line[index..(end < 0 ? line.Length : end)]));
                    index = end < 0 ? line.Length : end;
                }
            }
        }

        private void ObserveTag(string tag)
        {
            if (tag.StartsWith("<log4j:Event", StringComparison.Ordinal)) _fromServer = ServerEvent().IsMatch(tag);
            else if (tag.StartsWith("<log4j:Message", StringComparison.Ordinal)) { _inMessage = true; _messageStarted = false; }
            else if (tag == "</log4j:Message>") _inMessage = false;
            else if (tag == "</log4j:Event>") Closed = true;
        }

        private void ObserveMessage(string text)
        {
            if (!_inMessage || text.Length == 0) return;
            if (text.Contains("[CHAT]", StringComparison.Ordinal) || (!_messageStarted && ChatLogFilter.IsChat(text, _fromServer)))
                IsChat = true;
            _messageStarted = true;
        }
    }

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

    /// <summary>
    /// 在 targetDir 里生成过滤掉聊天的配置（每次重写），返回它的路径。targetDir 是 Amber 自己的临时目录，不放进游戏目录。
    /// 写不了时抛异常：宁可不启动，也不偷偷记下聊天。
    /// </summary>
    public static string Prepare(string originalPath, string targetDir)
    {
        var doc = XDocument.Load(originalPath, LoadOptions.PreserveWhitespace);
        var added = Transform(doc);
        if (added == 0) throw new InvalidDataException(L.T("core.launch.notrace.no_file_appender"));
        // /say、/me 的过滤加不上（配置里没有 Loggers）：宁可不启动，也不偷偷记下聊天。
        if (!HasServerLoggerFilter(doc)) throw new InvalidDataException(L.T("core.launch.notrace.no_server_logger"));
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(originalPath) + ".amber-notrace.xml");
        var temp = target + ".tmp";
        doc.Save(temp);
        File.Move(temp, target, true);
        return target;
    }

    /// <summary>
    /// 给每个写文件的 appender 加聊天过滤，返回加了几个；再给内置服务器的 logger 加上认 /say、/me 的过滤
    /// （logger 上的过滤对它的所有输出都生效，控制台里也就看不到这几行）。
    /// </summary>
    public static int Transform(XDocument doc)
    {
        var count = 0;
        foreach (var appender in doc.Descendants().Where(e => FileAppenders.Contains(e.Name.LocalName)).ToList())
        {
            AddFilter(appender, ChatRegex);
            count++;
        }
        if (count > 0) AddServerLoggerFilter(doc);
        return count;
    }

    /// <summary>内置服务器的 logger 上有没有 /say、/me 过滤。</summary>
    public static bool HasServerLoggerFilter(XDocument doc) =>
        doc.Descendants().Where(e => Is(e, "Logger") || Is(e, "AsyncLogger"))
            .Where(e => Attr(e, "name") == ServerLogger)
            .SelectMany(e => e.Descendants())
            .Any(f => Is(f, "RegexFilter") && Attr(f, "regex") == ServerChatRegex);

    private static void AddFilter(XElement owner, string regex)
    {
        var ns = owner.Name.Namespace;
        var filter = new XElement(ns + "RegexFilter",
            new XAttribute("regex", regex), new XAttribute("onMatch", "DENY"), new XAttribute("onMismatch", "NEUTRAL"));
        // log4j2 的 appender 和 logger 都只认一个过滤器元素：已有 Filters 就放进去（另外单放的过滤器也并进去），
        // 只有单个过滤器就和它一起包进新的 Filters。
        var filters = owner.Elements().FirstOrDefault(e => Is(e, "Filters"));
        var singles = owner.Elements().Where(e => !Is(e, "Filters") && e.Name.LocalName.EndsWith("Filter", StringComparison.OrdinalIgnoreCase)).ToList();
        if (filters is not null)
        {
            foreach (var s in singles) s.Remove();
            filters.Add(singles);
            filters.AddFirst(filter);
        }
        else if (singles.Count > 0)
        {
            foreach (var s in singles) s.Remove();
            owner.AddFirst(new XElement(ns + "Filters", filter, singles));
        }
        else
        {
            owner.AddFirst(filter);
        }
    }

    /// <summary>
    /// 配置里已有这个 logger 就给它加过滤；没有就新建一个，级别照抄离它最近的上级（都没写就是 Root 的，Root 也没写就是 log4j 默认的 error），
    /// additivity 保持默认（true），输出照旧交给上级的 appender。
    /// </summary>
    private static void AddServerLoggerFilter(XDocument doc)
    {
        if (doc.Descendants().FirstOrDefault(e => Is(e, "Loggers")) is not { } loggers) return;
        var ns = loggers.Name.Namespace;
        var all = loggers.Elements().Where(e => Is(e, "Logger") || Is(e, "AsyncLogger")).ToList();
        if (all.FirstOrDefault(e => Attr(e, "name") == ServerLogger) is { } existing)
        {
            AddFilter(existing, ServerChatRegex);
            return;
        }
        var level = all
            .Where(e => Attr(e, "name") is { } n && ServerLogger.StartsWith(n + ".", StringComparison.Ordinal) && Attr(e, "level") is not null)
            .OrderByDescending(e => Attr(e, "name")!.Length)
            .Select(e => Attr(e, "level"))
            .FirstOrDefault()
            ?? (loggers.Elements().FirstOrDefault(e => Is(e, "Root") || Is(e, "AsyncRoot")) is { } root ? Attr(root, "level") : null)
            ?? "error";
        var logger = new XElement(ns + "Logger", new XAttribute("name", ServerLogger), new XAttribute("level", level));
        AddFilter(logger, ServerChatRegex);
        loggers.AddFirst(logger);
    }
}
