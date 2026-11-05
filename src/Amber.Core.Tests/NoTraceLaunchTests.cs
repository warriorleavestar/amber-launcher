using System.IO.Compression;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>无痕模式下启动参数：Forge 1.13+ / NeoForge 用加载器 jar 里的日志配置；找不到就不启动并说出加载器名字。</summary>
public sealed class NoTraceLaunchTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "amber-notrace-launch-" + Guid.NewGuid().ToString("N")[..8]);

    public NoTraceLaunchTests() => Directory.CreateDirectory(_game);

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "amber-notrace-root-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
        try { Directory.Delete(_temp, true); } catch (IOException) { }
    }

    private const string Log4j2 = """<Configuration><Appenders><RollingRandomAccessFile name="File" fileName="logs/latest.log"/></Appenders></Configuration>""";

    private void Library(string group, string artifact, string version, string? log4j)
    {
        var path = Path.Combine(_game, "libraries", Path.Combine(group.Split('.')), artifact, version, $"{artifact}-{version}.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("META-INF/MANIFEST.MF");
        if (log4j is not null)
            using (var w = new StreamWriter(zip.CreateEntry("log4j2.xml").Open())) w.Write(log4j);
    }

    private void Version(string id, params string[] libraries)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_game, "versions", id)).FullName;
        var libs = string.Join(",", libraries.Select(n => $$"""{"name":"{{n}}"}"""));
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$$"""{"id":"{{{id}}}","type":"release","mainClass":"x","libraries":[{{{libs}}}],"logging":{},"arguments":{"game":[],"jvm":[]}}""");
    }

    private GameLauncher Launcher() => new(new GameDirectory(_game)) { NoTraceRoot = _temp };

    private List<string> Build(string id, List<string> notices) => Build(id, notices, out _);

    private List<string> Build(string id, List<string> notices, out string? noTraceDir) => Launcher().BuildArguments(new LaunchOptions
    {
        VersionId = id,
        JavaPath = "java",
        Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        NoTrace = true,
    }, notices.Add, out noTraceDir);

    private static List<string> GameDirFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList();

    /// <summary>原版的日志配置：过滤后的配置写到 Amber 自己的临时目录，游戏目录（可能是真的 .minecraft）里不多出文件；用完能删。</summary>
    [Fact]
    public void VanillaConfigGoesToAmberTempDir()
    {
        var logDir = Directory.CreateDirectory(Path.Combine(_game, "assets", "log_configs")).FullName;
        File.WriteAllText(Path.Combine(logDir, "client-1.12.xml"), Log4j2);
        var dir = Directory.CreateDirectory(Path.Combine(_game, "versions", "v")).FullName;
        File.WriteAllText(Path.Combine(dir, "v.json"),
            """{"id":"v","type":"release","mainClass":"x","libraries":[],"logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","file":{"id":"client-1.12.xml","sha1":"0","size":1,"url":"https://example.invalid/x"},"type":"log4j2-xml"}},"arguments":{"game":[],"jvm":[]}}""");
        var args = Build("v", [], out var noTraceDir);
        var arg = Assert.Single(args, a => a.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal));
        var path = arg["-Dlog4j.configurationFile=".Length..];
        Assert.StartsWith(_temp, path, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(noTraceDir);
        Assert.Contains("RegexFilter", File.ReadAllText(path));
        Assert.Empty(GameDirFiles(_game, "*.amber-notrace.xml"));

        Launcher().DeleteNoTraceDir(noTraceDir);
        Assert.False(Directory.Exists(noTraceDir));
        // 只删 NoTraceRoot 下面的目录。
        Launcher().DeleteNoTraceDir(logDir);
        Assert.True(File.Exists(Path.Combine(logDir, "client-1.12.xml")));
    }

    [Fact]
    public void ForgeUsesLoaderConfig()
    {
        Library("org.apache.logging.log4j", "log4j-core", "2.19.0", null);
        Library("net.minecraftforge", "fmlloader", "1.20.1-47.2.0", Log4j2);
        Version("forge-x", "org.apache.logging.log4j:log4j-core:2.19.0", "net.minecraftforge:fmlloader:1.20.1-47.2.0");
        var notices = new List<string>();
        var args = Build("forge-x", notices);
        var arg = Assert.Single(args, a => a.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal));
        Assert.StartsWith(_temp, arg["-Dlog4j.configurationFile=".Length..], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RegexFilter", File.ReadAllText(arg["-Dlog4j.configurationFile=".Length..]));
        Assert.Equal([L.T("core.launch.notrace.on")], notices);
        Assert.Empty(GameDirFiles(_game, "*log4j2*.xml").Where(f => !f.EndsWith(".jar", StringComparison.Ordinal)));
    }

    [Fact]
    public void RefusesWhenLoaderConfigMissing()
    {
        Library("org.apache.logging.log4j", "log4j-core", "2.19.0", null);
        Library("net.neoforged.fancymodloader", "loader", "4.0.0", null);
        Version("neo-x", "org.apache.logging.log4j:log4j-core:2.19.0", "net.neoforged.fancymodloader:loader:4.0.0");
        var e = Assert.Throws<InvalidOperationException>(() => Build("neo-x", []));
        Assert.Equal(L.F("core.launch.notrace.loader_config_missing", "NeoForge"), e.Message);
    }
}
