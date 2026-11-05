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
        // 内置服务器的 logger 另加一个认 /say、/me 的过滤，级别照抄 Root。
        var server = doc.Descendants("Logger").Single(e => (string?)e.Attribute("name") == ChatLogFilter.ServerLogger);
        Assert.Equal("info", (string?)server.Attribute("level"));
        Assert.Null(server.Attribute("additivity"));
        Assert.Equal(ChatLogFilter.ServerChatRegex, (string?)server.Elements("RegexFilter").Single().Attribute("regex"));
    }

    /// <summary>log4j2 的元素名、属性名不分大小写：小写的 &lt;filters&gt;、&lt;loggers&gt; 也要认，并进已有的 filters 里，原来的过滤器不能丢。</summary>
    [Fact]
    public void LowercaseElementsAreRecognized()
    {
        var doc = XDocument.Parse("""<Configuration><Appenders><rollingrandomaccessfile name="f"><filters><MarkerFilter marker="NETWORK" onMatch="DENY" onMismatch="NEUTRAL"/></filters><ThresholdFilter level="info"/></rollingrandomaccessfile></Appenders><loggers><logger Name="net" Level="warn"/><root/></loggers></Configuration>""");
        Assert.Equal(1, ChatLogFilter.Transform(doc));
        var appender = doc.Descendants("rollingrandomaccessfile").Single();
        var filters = Assert.Single(appender.Elements());
        Assert.Equal("filters", filters.Name.LocalName);
        Assert.Equal(["RegexFilter", "MarkerFilter", "ThresholdFilter"], filters.Elements().Select(e => e.Name.LocalName));
        Assert.True(ChatLogFilter.HasServerLoggerFilter(doc));
        var server = doc.Descendants().Single(e => (string?)e.Attribute("name") == ChatLogFilter.ServerLogger);
        Assert.Equal("warn", (string?)server.Attribute("level"));
    }

    /// <summary>Root 没写级别时 log4j 默认是 error：新建的 logger 也用 error，不会让更多日志写进文件。</summary>
    [Fact]
    public void MissingRootLevelFallsBackToError()
    {
        var doc = XDocument.Parse("""<Configuration><Appenders><File name="f"/></Appenders><Loggers><Root><AppenderRef ref="f"/></Root></Loggers></Configuration>""");
        ChatLogFilter.Transform(doc);
        var server = doc.Descendants("Logger").Single();
        Assert.Equal("error", (string?)server.Attribute("level"));
    }

    /// <summary>没有 Loggers（加不上 /say、/me 过滤）：不生成配置，宁可不启动。</summary>
    [Fact]
    public void PrepareRefusesWithoutLoggers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amber-notrace-nologgers-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "x.xml");
            File.WriteAllText(original, """<Configuration><Appenders><File name="f" fileName="a.log"/></Appenders></Configuration>""");
            var e = Assert.Throws<InvalidDataException>(() => ChatLogFilter.Prepare(original, Path.Combine(dir, "out")));
            Assert.Equal(Amber.Core.Localization.L.T("core.launch.notrace.no_server_logger"), e.Message);
            Assert.False(Directory.Exists(Path.Combine(dir, "out")) && Directory.EnumerateFiles(Path.Combine(dir, "out")).Any());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ServerLoggerInheritsNearestLevelOrReusesExisting()
    {
        var doc = XDocument.Parse("""<Configuration><Appenders><File name="f"/></Appenders><Loggers><Logger name="net" level="warn"/><Logger name="net.minecraft" level="${sys:forge.logging.mojang.level:-info}"/><Root level="all"/></Loggers></Configuration>""");
        ChatLogFilter.Transform(doc);
        var server = doc.Descendants("Logger").Single(e => (string?)e.Attribute("name") == ChatLogFilter.ServerLogger);
        Assert.Equal("${sys:forge.logging.mojang.level:-info}", (string?)server.Attribute("level"));

        var existing = XDocument.Parse("""<Configuration><Appenders><File name="f"/></Appenders><Loggers><Logger name="net.minecraft.server.MinecraftServer" level="debug"><MarkerFilter marker="X" onMatch="DENY" onMismatch="NEUTRAL"/></Logger><Root level="info"/></Loggers></Configuration>""");
        ChatLogFilter.Transform(existing);
        var logger = existing.Descendants("Logger").Single();
        Assert.Equal("debug", (string?)logger.Attribute("level"));
        Assert.Equal(["RegexFilter", "MarkerFilter"], logger.Element("Filters")!.Elements().Select(e => e.Name.LocalName));
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
    public void PrepareWritesIntoTargetDirAndRefusesConsoleOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amber-notrace-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "client-1.12.xml");
            File.WriteAllText(original, Vanilla);
            var target = Path.Combine(dir, "out");
            var prepared = ChatLogFilter.Prepare(original, target);
            Assert.Equal(Path.Combine(target, "client-1.12.amber-notrace.xml"), prepared);
            Assert.Contains("RegexFilter", File.ReadAllText(prepared));
            Assert.DoesNotContain("RegexFilter", File.ReadAllText(original));
            Assert.Equal(["client-1.12.xml"], Directory.GetFiles(dir).Select(Path.GetFileName)); // 原配置旁边不多出文件
            Assert.Equal(prepared, ChatLogFilter.Prepare(original, target)); // 再来一次直接覆盖

            var consoleOnly = Path.Combine(dir, "console.xml");
            Assert.True(ChatLogFilter.HasServerLoggerFilter(XDocument.Load(prepared)));
            File.WriteAllText(consoleOnly, """<Configuration><Appenders><Console name="c"/></Appenders></Configuration>""");
            Assert.Throws<InvalidDataException>(() => ChatLogFilter.Prepare(consoleOnly, target));
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
        // 内置服务器 logger 上的规则包含全局的规则。
        if (chat) Assert.True(ChatLogFilter.IsChat(message, fromServer: true));
    }

    /// <summary>
    /// 模组和 Amber 自己常用“[名字] ”开头记日志（包括警告和错误）：全局过滤不能吞掉它们；
    /// /say、/me 只在内置服务器的 logger 上认（名字里可以再套一层方括号）。
    /// </summary>
    [Theory]
    [InlineData("[OptiFine] OptiFine_1.20.1_HD_U_I6")]
    [InlineData("[OptiFine] *** Reloading textures ***")]
    [InlineData("[Indigo] Different rendering plugin detected; not applying Indigo.")]
    [InlineData("[Jay] java.io.IOException: broken pipe")]
    [InlineData("[Amber] java.io.IOException: broken pipe")]
    [InlineData("[LibGui] Loaded config")]
    [InlineData("[forge] Found status: UP_TO_DATE Current: 47.2.0 Target: null")]
    [InlineData("* Launching target 'forgeclient' with arguments")]
    public void BracketedModLinesAreKept(string message)
    {
        var regex = new Regex(@"\A(?:" + ChatLogFilter.ChatRegex + @")\z");
        Assert.False(regex.IsMatch(message));
        Assert.False(ChatLogFilter.IsChat(message));
        Assert.False(ChatLogFilter.IsChatLine("[12:00:00] [Render thread/WARN]: " + message));
        Assert.False(ChatLogFilter.IsChatLine("[12:00:00] [Render thread/ERROR] [net.optifine.Config/]: " + message));
        Assert.False(ChatLogFilter.IsChatLine($"""<log4j:Event logger="ewi" timestamp="1" level="WARN" thread="Render thread"><log4j:Message><![CDATA[{message}]]></log4j:Message></log4j:Event>"""));
    }

    [Theory]
    [InlineData("[Server] restarting soon", true)]
    [InlineData("[Not Secure] [Steve] said via /say", true)]
    [InlineData("[[Red] Steve] hi", true)]
    [InlineData("[Not Secure] [[Red] Team Steve] hi", true)]
    [InlineData("* Steve waves", true)]
    [InlineData("[[[Too] deep] Steve] hi", false)]
    [InlineData("Steve joined the game", false)]
    [InlineData("Saving chunks for level 'ServerLevel[World]'/minecraft:overworld", false)]
    public void ServerLoggerAlsoCatchesSayAndMe(string message, bool chat)
    {
        var regex = new Regex(@"\A(?:" + ChatLogFilter.ServerChatRegex + @")\z");
        Assert.Equal(chat, regex.IsMatch(message));
        Assert.Equal(chat, ChatLogFilter.IsChat(message, fromServer: true));
        if (message.StartsWith('[') || message.StartsWith('*')) Assert.False(ChatLogFilter.IsChat(message));
    }

    /// <summary>错误报告里的游戏输出：原版控制台是 XML 事件（消息在 CDATA 里），其他是带时间和线程前缀的文本。</summary>
    [Theory]
    [InlineData("""<log4j:Event logger="ewi" timestamp="1" level="INFO" thread="Render thread"><log4j:Message><![CDATA[[System] [CHAT] <Steve> hi]]></log4j:Message></log4j:Event>""", true)]
    [InlineData("""<log4j:Message><![CDATA[<Steve> hi]]></log4j:Message>""", true)]
    [InlineData("""<log4j:Message><![CDATA[Loading 42 recipes]]></log4j:Message>""", false)]
    [InlineData("[12:00:00] [Server thread/INFO]: <Steve> hi", true)]
    [InlineData("[12:00:00] [Server thread/INFO] [minecraft/MinecraftServer]: [Not Secure] <Steve> hi", true)]
    [InlineData("[12:00:00] [Server thread/INFO]: * Steve waves", true)]
    [InlineData("[12:00:00] [Server thread/INFO]: [[Red] Steve] hi", true)]
    [InlineData("[12:00:00] [Server thread/INFO] [minecraft/MinecraftServer]: [Steve] hi", true)]
    [InlineData("""<log4j:Event logger="net.minecraft.server.MinecraftServer" timestamp="1" level="INFO" thread="Server thread"><log4j:Message><![CDATA[[Steve] hi]]></log4j:Message></log4j:Event>""", true)]
    [InlineData("[12:00:00] [Render thread/INFO]: [Steve] hi", false)]
    [InlineData("[Jay] <Steve> is not chat here", false)]
    [InlineData("[Jay] 游戏输出里有 [CHAT] 字样", false)]
    [InlineData("[Amber] <Steve> is not chat here", false)]
    [InlineData("[Amber] 游戏输出里有 [CHAT] 字样", false)]
    [InlineData("[12:00:00] [Render thread/INFO]: Loading 42 recipes", false)]
    [InlineData("[12:00:00] [Render thread/INFO]: [System] [CHAT] hi", true)]
    public void ConsoleLinesAreRecognized(string line, bool chat) => Assert.Equal(chat, ChatLogFilter.IsChatLine(line));

    [Fact]
    public void WithoutChatKeepsOtherLines() =>
        Assert.Equal(["[12:00:00] [main/INFO]: start", "[12:00:02] [main/ERROR]: crash here"],
            ChatLogFilter.WithoutChat(["[12:00:00] [main/INFO]: start", "[12:00:01] [Server thread/INFO]: <Steve> secret plan", "[12:00:02] [main/ERROR]: crash here"]));

    [Fact]
    public void TextChatContinuationsAreRemovedButExceptionStackIsKept()
    {
        string[] exception = ["[12:00:02] [Render thread/ERROR] [minecraft/Minecraft]: failed", "java.io.IOException: broken pipe", "\tat game.Renderer.draw(Renderer.java:12)", "Caused by: java.lang.IllegalStateException: stopped"];
        string[] lines = ["[12:00:00] [Render thread/INFO]: [CHAT] first private line", "second private line", "", "third private line", .. exception];
        Assert.Equal(exception, ChatLogFilter.WithoutChat(lines));
    }

    [Fact]
    public void XmlChatRemovesTheWholeEventAndKeepsTheFollowingException()
    {
        string[] exception =
        [
            """<log4j:Event logger="game" level="ERROR" thread="Render thread">""",
            "<log4j:Message><![CDATA[failed to draw]]></log4j:Message>",
            "<log4j:Throwable><![CDATA[java.io.IOException: broken pipe",
            "\tat game.Renderer.draw(Renderer.java:12)]]></log4j:Throwable>",
            "</log4j:Event>",
        ];
        string[] lines =
        [
            """<log4j:Event logger="game" level="INFO" thread="Render thread">""",
            "<log4j:Message><![CDATA[[CHAT] first private line",
            "second private line]]></log4j:Message>",
            "</log4j:Event>",
            .. exception,
        ];
        Assert.Equal(exception, ChatLogFilter.WithoutChat(lines));
    }

    [Fact]
    public void XmlServerMetadataAppliesToMessageOnALaterLine()
    {
        string[] lines =
        [
            "<log4j:Event",
            """ logger="net.minecraft.server.MinecraftServer" level="INFO" thread="Server thread">""",
            "<log4j:Message><![CDATA[[Steve] private /say message",
            "private continuation]]></log4j:Message>",
            "</log4j:Event>",
        ];
        Assert.Empty(ChatLogFilter.WithoutChat(lines));
    }

    [Theory]
    [InlineData("Jay")]
    [InlineData("Amber")]
    public void XmlCdataCannotForgeAnEventBoundary(string launcherName)
    {
        string[] lines =
        [
            """<log4j:Event logger="game" level="INFO" thread="Render thread">""",
            "<log4j:Message><![CDATA[[CHAT] private first line",
            "</log4j:Event>",
            """<log4j:Event logger="game" level="INFO" thread="Render thread">""",
            $"[{launcherName}] private continuation",
            "[12:00:01] [main/INFO]: still private]]></log4j:Message>",
            "</log4j:Event>",
            "[12:00:02] [main/ERROR]: real diagnostic",
        ];
        Assert.Equal([lines[^1]], ChatLogFilter.WithoutChat(lines));
    }

    [Fact]
    public void ChatMarkerOnALaterMessageLineRemovesEarlierLinesToo()
    {
        string[] lines =
        [
            """<log4j:Event logger="game" level="INFO" thread="Render thread">""",
            "<log4j:Message><![CDATA[private first line",
            "[CHAT] private second line]]></log4j:Message>",
            "</log4j:Event>",
        ];
        Assert.Empty(ChatLogFilter.WithoutChat(lines));
    }

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
            Assert.Contains("RegexFilter", File.ReadAllText(ChatLogFilter.Prepare(target, Path.Combine(dir, "logging"))));

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
