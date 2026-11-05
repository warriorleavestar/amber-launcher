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

    private const string Log4j2 = """<Configuration><Appenders><RollingRandomAccessFile name="File" fileName="logs/latest.log"/></Appenders><Loggers><Root level="info"><AppenderRef ref="File"/></Root></Loggers></Configuration>""";

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

    /// <summary>脚本保存自己的无痕配置；启动器清理过期的单次启动配置后，脚本仍引用存在且带过滤器的文件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportedScriptKeepsConfigAfterTemporarySweep(bool forge)
    {
        if (forge)
        {
            Library("org.apache.logging.log4j", "log4j-core", "2.19.0", null);
            Library("net.minecraftforge", "fmlloader", "1.20.1-47.2.0", Log4j2);
            Version("v", "org.apache.logging.log4j:log4j-core:2.19.0", "net.minecraftforge:fmlloader:1.20.1-47.2.0");
        }
        else
        {
            var logDir = Directory.CreateDirectory(Path.Combine(_game, "assets", "log_configs")).FullName;
            File.WriteAllText(Path.Combine(logDir, "client.xml"), Log4j2);
            Version("v");
            File.WriteAllText(Path.Combine(_game, "versions", "v", "v.json"),
                """{"id":"v","type":"release","mainClass":"x","libraries":[],"logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","file":{"id":"client.xml","sha1":"0","size":1,"url":"https://example.invalid/x"},"type":"log4j2-xml"}},"arguments":{"game":[],"jvm":[]}}""");
        }
        var options = new LaunchOptions
        {
            VersionId = "v", JavaPath = "java", Session = new GameSession("Tester", Guid.Empty, "0", "legacy"), NoTrace = true,
        };
        var exportDir = Directory.CreateDirectory(Path.Combine(_game, "exports")).FullName;
        var script = Path.Combine(exportDir, OperatingSystem.IsWindows() ? "run0.bat" : "run0.sh");
        var launcher = Launcher();
        launcher.WriteLaunchScript(script, options);
        var config = Assert.Single(Directory.GetFiles(script + ".amber", "*.amber-notrace.xml"));
        Assert.Contains(config, File.ReadAllText(script));
        Assert.Contains("RegexFilter", File.ReadAllText(config));

        // 普通启动仍使用临时配置；它被清理不能影响已经导出的脚本。
        launcher.BuildArguments(options, null, out var temporary);
        Assert.NotNull(temporary);
        Assert.Equal(1, NoTraceDirs.Sweep(_temp, DateTime.UtcNow.AddDays(1)));
        Assert.False(Directory.Exists(temporary));
        Assert.True(File.Exists(config));
        Assert.Contains(config, File.ReadAllText(script));

        // 再次导出只更新自己的配置，不依赖上一次生成的临时目录。
        launcher.WriteLaunchScript(script, options);
        Assert.Equal(config, Assert.Single(Directory.GetFiles(script + ".amber", "*.amber-notrace.xml")));
        Assert.True(File.Exists(config));
        Assert.Contains(config, File.ReadAllText(script));
    }

    [Fact]
    public void NormalScriptDoesNotCreateNoTraceSidecar()
    {
        Version("v");
        var script = Path.Combine(_game, "normal.bat");
        Launcher().WriteLaunchScript(script, new LaunchOptions
        {
            VersionId = "v", JavaPath = "java", Session = new GameSession("Tester", Guid.Empty, "0", "legacy"),
        });
        Assert.True(File.Exists(script));
        Assert.False(Directory.Exists(script + ".amber"));
        Assert.DoesNotContain("amber-notrace.xml", File.ReadAllText(script));
    }

    /// <summary>脚本自己的合法文件名可带 cmd 元字符；生成的无痕配置路径必须经过批处理引用。</summary>
    [Fact]
    public void WindowsScriptEscapesSidecarPathFromExportName()
    {
        if (!OperatingSystem.IsWindows()) return;
        Library("net.minecraftforge", "fmlloader", "1.20.1-47.2.0", Log4j2);
        Version("v", "net.minecraftforge:fmlloader:1.20.1-47.2.0");
        var script = Path.Combine(_game, "run0 & (100%) !PATH!.bat");
        Launcher().WriteLaunchScript(script, new LaunchOptions
        {
            VersionId = "v", JavaPath = "java", Session = new GameSession("Tester", Guid.Empty, "0", "legacy"), NoTrace = true,
        });
        var config = Assert.Single(Directory.GetFiles(script + ".amber", "*.amber-notrace.xml"));
        Assert.Contains("RegexFilter", File.ReadAllText(config));
        var text = File.ReadAllText(script);
        Assert.Contains("setlocal DisableDelayedExpansion", text);
        Assert.Contains("run0 ^& ^(100%%^) !PATH!.bat.amber", text);
        Assert.Contains("-Dlog4j.configurationFile=", text);
    }

    /// <summary>启动时的清理：用它的游戏已经不在了的删掉；游戏还在运行的、刚建好还没有 owner 的留着。</summary>
    [Fact]
    public void SweepRemovesOnlyAbandonedDirs()
    {
        Directory.CreateDirectory(_temp);
        string Dir(string name, string? owner, TimeSpan age)
        {
            var d = Directory.CreateDirectory(Path.Combine(_temp, name)).FullName;
            File.WriteAllText(Path.Combine(d, "client.amber-notrace.xml"), "<Configuration/>");
            if (owner is not null) File.WriteAllText(Path.Combine(d, NoTraceDirs.OwnerFile), owner);
            Directory.SetCreationTimeUtc(d, DateTime.UtcNow - age);
            return d;
        }
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var running = Dir("running", $"{self.Id}\n{self.StartTime.ToUniversalTime().Ticks}", TimeSpan.FromDays(1));
        var reused = Dir("reused", $"{self.Id}\n1", TimeSpan.FromDays(1)); // 进程号被别的进程重用了
        var gone = Dir("gone", "999999999\n1", TimeSpan.FromDays(1));
        var fresh = Dir("fresh", null, TimeSpan.FromMinutes(1));
        var stale = Dir("stale", null, TimeSpan.FromHours(1));
        var garbage = Dir("garbage", "not a pid", TimeSpan.FromDays(1));

        Assert.Equal(4, GameLauncher.SweepNoTrace(_temp));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(reused));
        Assert.False(Directory.Exists(gone));
        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(garbage));
        Assert.Equal(0, GameLauncher.SweepNoTrace(Path.Combine(_temp, "missing")));
    }

    /// <summary>退出时删不掉（被占用）：过一会儿再试，占用解除后删掉。</summary>
    [Fact]
    public async Task ExitDeleteRetriesWhileLocked()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix 可以删除仍被打开的文件，这里验证 Windows 的文件锁重试。
        var dir = Directory.CreateDirectory(Path.Combine(_temp, "locked")).FullName;
        var file = Path.Combine(dir, "client.amber-notrace.xml");
        File.WriteAllText(file, "<Configuration/>");
        var launcher = Launcher();
        var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var deleting = launcher.DeleteNoTraceDirWithRetryAsync(dir);
        await Task.Delay(300);
        Assert.True(Directory.Exists(dir));
        locked.Dispose();
        Assert.True(await deleting);
        Assert.False(Directory.Exists(dir));
    }
}
