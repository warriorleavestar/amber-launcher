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
    [InlineData("[CHAT] line one\nline two", true)]
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
    }
}
