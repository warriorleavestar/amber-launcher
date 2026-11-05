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

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
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

    private List<string> Build(string id, List<string> notices) => new GameLauncher(new GameDirectory(_game)).BuildArguments(new LaunchOptions
    {
        VersionId = id,
        JavaPath = "java",
        Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        NoTrace = true,
    }, notices.Add);

    [Fact]
    public void ForgeUsesLoaderConfig()
    {
        Library("org.apache.logging.log4j", "log4j-core", "2.19.0", null);
        Library("net.minecraftforge", "fmlloader", "1.20.1-47.2.0", Log4j2);
        Version("forge-x", "org.apache.logging.log4j:log4j-core:2.19.0", "net.minecraftforge:fmlloader:1.20.1-47.2.0");
        var notices = new List<string>();
        var args = Build("forge-x", notices);
        var arg = Assert.Single(args, a => a.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal));
        Assert.Contains("RegexFilter", File.ReadAllText(arg["-Dlog4j.configurationFile=".Length..]));
        Assert.Equal([L.T("core.launch.notrace.on")], notices);
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
