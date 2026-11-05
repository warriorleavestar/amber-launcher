using System.Xml.Linq;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>
/// 无痕模式：游戏照常写 logs/latest.log，但聊天行不写进去。
/// 做法是在版本自带的 log4j2 配置（version.json 的 logging.client）基础上，给每个写文件的 appender 加一个 RegexFilter，
/// 生成一份新配置交给游戏；控制台输出（Amber 只放在内存里显示）不受影响。
/// 没有 log4j2 配置的老版本（1.7 以前）不支持。
/// </summary>
public static class ChatLogFilter
{
    /// <summary>
    /// 聊天行：客户端收到的聊天都带“[CHAT]”（1.19 起前面还有“[System] ”）；
    /// 本机开的单人或局域网世界，内置服务器把玩家聊天记成“&lt;名字&gt; 内容”（1.19 起未签名的前面有“[Not Secure] ”）。
    /// </summary>
    public const string ChatRegex = @"(?s)(.*\[CHAT\].*|(\[Not Secure\] )?<[^<>\s]{1,40}> .*)";

    private static readonly HashSet<string> FileAppenders = ["RollingRandomAccessFile", "RollingFile", "RandomAccessFile", "File"];

    /// <summary>这个版本能不能用无痕模式过滤聊天（有 log4j2 配置）。</summary>
    public static bool Supported(VersionJson version) => version.Logging?.GetValueOrDefault("client") is { Argument: not null, File: not null };

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
