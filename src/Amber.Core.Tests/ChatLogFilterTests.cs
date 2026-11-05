using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>无痕模式：log4j2 配置里每个写文件的 appender 都加上聊天过滤，控制台不动；聊天行被认出来，普通日志不误伤。</summary>
public class ChatLogFilterTests
{
    private const string Vanilla = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Configuration status="WARN">
            <Appenders>
                <Console name="SysOut" target="SYSTEM_OUT"><PatternLayout pattern="%m%n"/></Console>
                <RollingRandomAccessFile name="File" fileName="logs/latest.log" filePattern="logs/%d{yyyy-MM-dd}-%i.log.gz">
                    <PatternLayout pattern="[%d{HH:mm:ss}] [%t/%level]: %msg%n"/>
                    <Policies><TimeBasedTriggeringPolicy/><OnStartupTriggeringPolicy/></Policies>
                </RollingRandomAccessFile>
            </Appenders>
            <Loggers><Root level="info"><AppenderRef ref="SysOut"/><AppenderRef ref="File"/></Root></Loggers>
        </Configuration>
        """;

    [Fact]
    public void AddsFilterToFileAppenderOnly()
    {
        var doc = XDocument.Parse(Vanilla);
        Assert.Equal(1, ChatLogFilter.Transform(doc));
        var file = doc.Descendants("RollingRandomAccessFile").Single();
        var filter = file.Elements().First();
        Assert.Equal("RegexFilter", filter.Name.LocalName);
        Assert.Equal("DENY", (string?)filter.Attribute("onMatch"));
        Assert.Equal("NEUTRAL", (string?)filter.Attribute("onMismatch"));
        Assert.Equal(ChatLogFilter.ChatRegex, (string?)filter.Attribute("regex"));
        Assert.Empty(doc.Descendants("Console").Single().Elements("RegexFilter"));
    }

    [Fact]
    public void WrapsExistingSingleFilter()
    {
        var doc = XDocument.Parse("""<Configuration><Appenders><File name="f" fileName="a.log"><MarkerFilter marker="NETWORK" onMatch="DENY" onMismatch="NEUTRAL"/></File></Appenders></Configuration>""");
        Assert.Equal(1, ChatLogFilter.Transform(doc));
        var file = doc.Descendants("File").Single();
        var filters = Assert.Single(file.Elements());
        Assert.Equal("Filters", filters.Name.LocalName);
        Assert.Equal(["RegexFilter", "MarkerFilter"], filters.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void AddsIntoExistingFilters()
    {
        var doc = XDocument.Parse("""<Configuration><Appenders><RollingFile name="f"><Filters><ThresholdFilter level="info"/></Filters></RollingFile><RandomAccessFile name="g"/></Appenders></Configuration>""");
        Assert.Equal(2, ChatLogFilter.Transform(doc));
        var filters = doc.Descendants("Filters").Single();
        Assert.Equal(["RegexFilter", "ThresholdFilter"], filters.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("RegexFilter", doc.Descendants("RandomAccessFile").Single().Elements().Single().Name.LocalName);
    }

    [Fact]
    public void PrepareWritesSiblingFileAndRefusesConsoleOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amber-notrace-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "client-1.12.xml");
            File.WriteAllText(original, Vanilla);
            var prepared = ChatLogFilter.Prepare(original);
            Assert.Equal(Path.Combine(dir, "client-1.12.amber-notrace.xml"), prepared);
            Assert.Contains("RegexFilter", File.ReadAllText(prepared));
            Assert.DoesNotContain("RegexFilter", File.ReadAllText(original));
            Assert.Equal(prepared, ChatLogFilter.Prepare(original)); // 再来一次直接覆盖

            var consoleOnly = Path.Combine(dir, "console.xml");
            File.WriteAllText(consoleOnly, """<Configuration><Appenders><Console name="c"/></Appenders></Configuration>""");
            Assert.Throws<InvalidDataException>(() => ChatLogFilter.Prepare(consoleOnly));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("[System] [CHAT] <Steve> hello there", true)]
    [InlineData("[CHAT] Alex joined the game", true)]
    [InlineData("<Steve> hi", true)]
    [InlineData("[Not Secure] <Steve> hi", true)]
    [InlineData("[Modified] <Steve> hi", true)]
    [InlineData("[Not Secure] [Modified] <Steve> hi", true)]
    [InlineData("<[Red] Team Steve> hi", true)]
    [InlineData("<Steve the Builder> hi", true)]
    [InlineData("[Server] restarting soon", true)]
    [InlineData("[Not Secure] [Steve] said via /say", true)]
    [InlineData("* Steve waves", true)]
    [InlineData("[CHAT] line one\nline two", true)]
    [InlineData("<Steve> line one\nline two", true)]
    [InlineData("Loading 42 recipes", false)]
    [InlineData("Connecting to 127.0.0.1, 25565", false)]
    [InlineData("Reloading ResourceManager: vanilla, fabric", false)]
    [InlineData("Unable to resolve <unknown>", false)]
    [InlineData("Steve lost connection: Disconnected", false)]
    public void RegexMatchesChatOnly(string message, bool chat)
    {
        // log4j2 的 RegexFilter 用 Java 的 matches()，要整行匹配；.NET 用 \A...\z 模拟。
        var regex = new Regex(@"\A(?:" + ChatLogFilter.ChatRegex + @")\z");
        Assert.Equal(chat, regex.IsMatch(message));
        Assert.Equal(chat, ChatLogFilter.IsChat(message));
    }

    /// <summary>错误报告里的游戏输出：原版控制台是 XML 事件（消息在 CDATA 里），其他是带时间和线程前缀的文本。</summary>
    [Theory]
    [InlineData("""<log4j:Event logger="ewi" timestamp="1" level="INFO" thread="Render thread"><log4j:Message><![CDATA[[System] [CHAT] <Steve> hi]]></log4j:Message></log4j:Event>""", true)]
    [InlineData("""<log4j:Message><![CDATA[<Steve> hi]]></log4j:Message>""", true)]
    [InlineData("""<log4j:Message><![CDATA[Loading 42 recipes]]></log4j:Message>""", false)]
    [InlineData("[12:00:00] [Server thread/INFO]: <Steve> hi", true)]
    [InlineData("[12:00:00] [Server thread/INFO] [minecraft/MinecraftServer]: [Not Secure] <Steve> hi", true)]
    [InlineData("[12:00:00] [Server thread/INFO]: * Steve waves", true)]
    [InlineData("[12:00:00] [Render thread/INFO]: Loading 42 recipes", false)]
    [InlineData("[12:00:00] [Render thread/INFO]: [System] [CHAT] hi", true)]
    public void ConsoleLinesAreRecognized(string line, bool chat) => Assert.Equal(chat, ChatLogFilter.IsChatLine(line));

    [Fact]
    public void WithoutChatKeepsOtherLines() =>
        Assert.Equal(["[12:00:00] [main/INFO]: start", "crash here"],
            ChatLogFilter.WithoutChat(["[12:00:00] [main/INFO]: start", "[12:00:01] [Server thread/INFO]: <Steve> secret plan", "crash here"]));

    /// <summary>Forge 1.13+ / NeoForge：版本文件没有 logging，log4j2.xml 从加载器 jar 里取。</summary>
    [Fact]
    public void ExtractsLoaderConfigFromClasspath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amber-notrace-jar-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string Jar(string name, string? log4j)
            {
                var path = Path.Combine(dir, name);
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                zip.CreateEntry("META-INF/MANIFEST.MF");
                if (log4j is not null)
                    using (var w = new StreamWriter(zip.CreateEntry("log4j2.xml").Open())) w.Write(log4j);
                return path;
            }
            var other = Jar("other-1.0.jar", """<Configuration><Appenders><File name="wrong" fileName="x.log"/></Appenders></Configuration>""");
            var loader = Jar("fmlloader-1.20.1-47.2.0.jar", Vanilla);
            var core = Jar("log4j-core-2.19.0.jar", null);
            var target = Path.Combine(dir, "logging", "x-log4j2.xml");

            Assert.Equal(target, ChatLogFilter.ExtractFromClasspath([other, core, loader], target));
            Assert.Contains("RollingRandomAccessFile", File.ReadAllText(target));
            Assert.Contains("RegexFilter", File.ReadAllText(ChatLogFilter.Prepare(target)));

            Assert.Null(ChatLogFilter.ExtractFromClasspath([core, Path.Combine(dir, "missing.jar")], Path.Combine(dir, "y.xml")));
            Assert.True(ChatLogFilter.UsesLog4j([core]));
            Assert.False(ChatLogFilter.UsesLog4j([other]));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
