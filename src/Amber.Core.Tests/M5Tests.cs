using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.Core.Diagnostics;
using Amber.Core.Game;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

public class M5Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-m5-" + Guid.NewGuid().ToString("N"));

    public M5Tests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("java.lang.OutOfMemoryError: Java heap space", "内存不够用")]
    [InlineData("Error: LinkageError ... has been compiled by a more recent version of the Java Runtime (class file version 65.0), this version", "Java 21")]
    [InlineData("java.lang.ClassCastException: class jdk.internal.loader.ClassLoaders$AppClassLoader cannot be cast to class java.net.URLClassLoader", "Java 8")]
    [InlineData("java.lang.reflect.InaccessibleObjectException: Unable to make field accessible", "Java 版本太新")]
    [InlineData("net.fabricmc.loader.impl.FormattedException: Some of your mods are incompatible", "不兼容")]
    [InlineData("Mod 'x' requires version 1.2 of 'fabric-api', which is missing", "fabric-api")]
    [InlineData("Missing or unsupported mandatory dependencies:\n\tMod ID: 'geckolib', Requested by: 'x'", "必需的模组依赖")]
    [InlineData("org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: Mixin apply for mod sodium failed", "sodium")]
    [InlineData("java.util.zip.ZipException: invalid LOC header", "损坏")]
    [InlineData("Pixel format not accelerated", "显卡驱动")]
    [InlineData("#  EXCEPTION_ACCESS_VIOLATION (0xc0000005)\n# Problematic frame:\n# C  [atio6axx.dll+0x1]", "显卡驱动崩溃")]
    [InlineData("Manually triggered debug crash", "手动")]
    [InlineData("Found 2 duplicate mods", "不止一份")]
    public void CrashRules_MatchRealisticText(string log, string expected)
    {
        var analysis = CrashAnalyzer.Analyze(log);
        Assert.Contains(analysis.Findings, f => f.Title.Contains(expected));
    }

    [Fact]
    public void CrashAnalyzer_NoGuessOnCleanLog()
    {
        var analysis = CrashAnalyzer.Analyze("[12:00:00] [Render thread/INFO]: Setting user: Tester\nSound engine started");
        Assert.Empty(analysis.Findings);
        Assert.Contains("没有识别出", analysis.Summary);
    }

    [Fact]
    public void CrashAnalyzer_ExtractsSuspects()
    {
        var analysis = CrashAnalyzer.Analyze("Suspected Mods: Sodium (sodium), Iris (iris)\n\tMod File: /mods/create-1.0.jar\n\tMod File: NO FILE\n");
        Assert.Contains("Sodium (sodium), Iris (iris)", analysis.SuspectMods);
        Assert.Contains("create-1.0.jar", analysis.SuspectMods);
        Assert.Equal(2, analysis.SuspectMods.Count);
    }

    [Fact]
    public void AnalyzeRun_OnlyUsesReportsFromThisRun()
    {
        var crash = Path.Combine(_root, "crash-reports");
        Directory.CreateDirectory(crash);
        var old = Path.Combine(crash, "crash-old.txt");
        File.WriteAllText(old, "java.lang.OutOfMemoryError");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        Assert.Empty(CrashAnalyzer.AnalyzeRun(_root, DateTime.UtcNow.AddMinutes(-1), []).Findings);

        File.WriteAllText(Path.Combine(crash, "crash-new.txt"), "Pixel format not accelerated");
        var analysis = CrashAnalyzer.AnalyzeRun(_root, DateTime.UtcNow.AddMinutes(-1), []);
        Assert.Contains(analysis.Findings, f => f.Title.Contains("显卡"));
    }

    [Fact]
    public void AnalyzeRun_IgnoresLatestLogFromPreviousRun()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        var latest = Path.Combine(logs, "latest.log");
        File.WriteAllText(latest, "java.lang.OutOfMemoryError");
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddHours(-1));
        Assert.Empty(CrashAnalyzer.AnalyzeRun(_root, DateTime.UtcNow.AddMinutes(-1), []).Findings);
    }

    [Fact]
    public void CrashAnalyzer_DriverDllInLoadedModuleListIsNotACrash()
    {
        Assert.Empty(CrashAnalyzer.Analyze("Loaded: C:\\Windows\\System32\\nvoglv64.dll").Findings);
        var crash = CrashAnalyzer.Analyze("# Problematic frame:\n# C  [nvoglv64.dll+0xabc]");
        Assert.Contains(crash.Findings, f => f.Title.Contains("显卡驱动"));
    }

    [Fact]
    public void Nbt_RoundTrips_AndServerListKeepsUnknownFields()
    {
        var path = Path.Combine(_root, "servers.dat");
        Nbt.WriteFile(path, new Dictionary<string, object>
        {
            ["servers"] = new Nbt.NbtList(10, [new Dictionary<string, object> { ["name"] = "老服", ["ip"] = "a.example:25566", ["icon"] = "base64", ["acceptTextures"] = (byte)1 }]),
        });
        Assert.Equal([new ServerEntry("老服", "a.example:25566")], ServerList.Read(_root));

        ServerList.Add(_root, new ServerEntry("新服", "b.example"));
        var read = ServerList.Read(_root);
        Assert.Equal(2, read.Count);
        var root = Nbt.ReadFile(path);
        var first = (Dictionary<string, object>)((Nbt.NbtList)root["servers"]).Items[0];
        Assert.Equal("base64", first["icon"]);

        var removed = ServerList.Remove(_root, "a.example:25566");
        Assert.Equal("新服", Assert.Single(ServerList.Read(_root)).Name);

        // 撤销：放回原来的位置，图标这类不认识的字段原样保留；再撤销一次不会重复。
        ServerList.Restore(_root, removed!);
        ServerList.Restore(_root, removed!);
        Assert.Equal(["老服", "新服"], ServerList.Read(_root).Select(s => s.Name));
        var restored = (Dictionary<string, object>)((Nbt.NbtList)Nbt.ReadFile(path)["servers"]).Items[0];
        Assert.Equal("base64", restored["icon"]);
        Assert.Null(ServerList.Remove(_root, "missing.example"));
    }

    [Theory]
    [InlineData("play.example.com", "play.example.com", 25565)]
    [InlineData("play.example.com:1234", "play.example.com", 1234)]
    [InlineData("[::1]:25570", "::1", 25570)]
    public void ParseServerAddress(string input, string host, int port) =>
        Assert.Equal((host, port), ServerList.ParseAddress(input));

    /// <summary>一个只回状态的假服务器，验证 Ping 协议来回。</summary>
    [Fact]
    public async Task ServerPing_ReadsStatusFromRealSocket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            await ReadPacket(s);
            await ReadPacket(s);
            var json = Encoding.UTF8.GetBytes("""{"version":{"name":"1.21.1","protocol":767},"players":{"max":20,"online":3},"description":{"text":"你好","extra":[{"text":" 世界"}]}}""");
            var body = new MemoryStream();
            body.WriteByte(0);
            WriteVarInt(body, json.Length);
            body.Write(json);
            await SendPacket(s, body.ToArray());
            var ping = await ReadPacket(s);
            await SendPacket(s, ping);
        });

        var status = await ServerList.PingAsync($"127.0.0.1:{port}");
        await server;
        listener.Stop();
        Assert.True(status.Online, status.Error);
        Assert.Equal(("1.21.1", 3, 20, "你好 世界"), (status.VersionName, status.Players, status.MaxPlayers, status.Motd));
        Assert.NotNull(status.PingMs);
    }

    [Fact]
    public async Task ServerPing_OfflineDoesNotThrow()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False((await ServerList.PingAsync($"127.0.0.1:{port}", TimeSpan.FromSeconds(2))).Online);
    }

    private static async Task<byte[]> ReadPacket(NetworkStream s)
    {
        var length = 0;
        var buffer = new byte[1];
        for (var shift = 0; ; shift += 7)
        {
            await s.ReadExactlyAsync(buffer);
            length |= (buffer[0] & 0x7f) << shift;
            if ((buffer[0] & 0x80) == 0) break;
        }
        var body = new byte[length];
        await s.ReadExactlyAsync(body);
        return body;
    }

    private static async Task SendPacket(NetworkStream s, byte[] body)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);
        await s.WriteAsync(packet.ToArray());
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    [Fact]
    public void Worlds_ReadLevelDat_Backup_AndImport()
    {
        var saves = Path.Combine(_root, "saves");
        var world = Path.Combine(saves, "w1");
        Directory.CreateDirectory(Path.Combine(world, "region"));
        File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "data");
        File.WriteAllText(Path.Combine(world, "session.lock"), "lock");
        WriteGzipLevelDat(Path.Combine(world, "level.dat"), "我的世界", 1, 1793171003328);

        var info = Assert.Single(WorldManager.List(saves));
        Assert.Equal(("我的世界", "创造", "1.21.1"), (info.Name, info.GameMode, info.Version));
        Assert.Equal(1793171003328, info.LastPlayed!.Value.ToUnixTimeMilliseconds());

        var backup = WorldManager.Backup(info, Path.Combine(_root, "backups"));
        using (var zip = ZipFile.OpenRead(backup))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "w1/region/r.0.0.mca");
            Assert.DoesNotContain(zip.Entries, e => e.Name == "session.lock");
        }

        Assert.Equal("w1 (2)", WorldManager.Import(backup, saves));
        Assert.Equal(2, WorldManager.List(saves).Count);
    }

    private static void WriteGzipLevelDat(string path, string name, int gameType, long lastPlayed)
    {
        var plain = Path.GetTempFileName();
        Nbt.WriteFile(plain, new Dictionary<string, object>
        {
            ["Data"] = new Dictionary<string, object>
            {
                ["LevelName"] = name,
                ["GameType"] = gameType,
                ["LastPlayed"] = lastPlayed,
                ["Version"] = new Dictionary<string, object> { ["Name"] = "1.21.1" },
            },
        });
        using (var output = File.Create(path))
        using (var gz = new GZipStream(output, CompressionMode.Compress))
            gz.Write(File.ReadAllBytes(plain));
        File.Delete(plain);
    }

    [Fact]
    public void InstanceSettings_FallBackToGlobal_AndPersist()
    {
        var dir = new GameDirectory(_root);
        Directory.CreateDirectory(dir.VersionDir("v"));
        new InstanceSettings { MaxMemoryMb = 8192, JvmArgs = "  " }.Save(dir, "v");
        var merged = InstanceSettings.Load(dir, "v").Over(new InstanceSettings { MaxMemoryMb = 2048, JvmArgs = "-Dx=1", Isolate = true });
        Assert.Equal((8192, "-Dx=1", true), (merged.MaxMemoryMb!.Value, merged.JvmArgs, merged.Isolate!.Value));
    }

    [Theory]
    [InlineData(0, 16000, 2560)]
    [InlineData(150, 16000, 6144)]
    [InlineData(500, 16000, 8192)]
    [InlineData(150, 3000, 2488)]
    [InlineData(150, 800, 1024)]
    public void AutoMemory(int mods, long free, int expected) => Assert.Equal(expected, MemoryPlanner.AutoMemoryMb(mods, free));

    [Fact]
    public void ArgumentSplitter_KeepsQuotedSpaces() =>
        Assert.Equal(["-Da=b", "-Dpath=C:\\x y", "--z"], ArgumentSplitter.Split("  -Da=b  -Dpath=\"C:\\x y\" --z "));

    [Fact]
    public void Junk_ScanAndClean_NeverTouchesSavesOrMods()
    {
        var dir = new GameDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, "logs", "2026-01-01-1.log.gz"), "x");
        File.WriteAllText(Path.Combine(_root, "logs", "latest.log"), "keep");
        Directory.CreateDirectory(Path.Combine(_root, "saves", "w"));
        File.WriteAllText(Path.Combine(_root, "saves", "w", "level.dat"), "keep");
        Directory.CreateDirectory(dir.NativesDir("v"));
        File.WriteAllText(dir.VersionJsonPath("v"), "{}");
        File.WriteAllText(Path.Combine(dir.NativesDir("v"), "a.dll"), "12345");

        var groups = Toolbox.ScanJunk(dir);
        Assert.Equal(6, Toolbox.Clean(groups));
        Assert.True(File.Exists(Path.Combine(_root, "logs", "latest.log")));
        Assert.True(File.Exists(Path.Combine(_root, "saves", "w", "level.dat")));
        Assert.False(Directory.Exists(dir.NativesDir("v")));
    }

    [Fact]
    public async Task UpdateChecker_NoFeedMeansNoNetwork_OnlyReportsNewer()
    {
        var fake = new FakeHttp().On("https://u.example/feed", """{"tag_name":"v1.2.0","html_url":"https://u.example/r","body":"new"}""");
        var checker = new UpdateChecker(new HttpClient(fake));

        Assert.Null(await checker.CheckAsync(null, "1.0.0"));
        Assert.Empty(fake.Requests);

        var update = await checker.CheckAsync("https://u.example/feed", "1.0.0");
        Assert.Equal(("1.2.0", "https://u.example/r"), (update!.Version, update.Url));
        Assert.Null(await checker.CheckAsync("https://u.example/feed", "1.2.0"));
    }

    [Fact]
    public void LaunchScript_NeverContainsRealToken()
    {
        var path = Path.Combine(_root, "run.bat");
        Toolbox.WriteLaunchScript(path, "C:\\java.exe", "C:\\game dir", ["-Xmx1m", "--accessToken", "SECRET123"], "SECRET123");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("SECRET123", text);
        Assert.Contains("REPLACE_WITH_TOKEN", text);
    }

    [Fact]
    public void LaunchScript_OfflinePlaceholderDoesNotChangeArguments()
    {
        var path = Path.Combine(_root, "offline.bat");
        Toolbox.WriteLaunchScript(path, "java", _root, ["-Xmx4096m", "-Dlog4j.configurationFile=run0.amber/config.xml", "--accessToken", "0"], "0");
        var text = File.ReadAllText(path);
        Assert.Contains("-Xmx4096m", text);
        Assert.Contains("run0.amber/config.xml", text);
        Assert.DoesNotContain("REPLACE_WITH_TOKEN", text);
    }

    /// <summary>真的交给 cmd（且调用者开着延迟展开），验证目标进程收到原始参数，而不是只检查脚本文字。</summary>
    [Fact]
    public async Task LaunchScript_WindowsPreservesMetacharactersAndEmbeddedQuotes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var work = Directory.CreateDirectory(Path.Combine(_root, "game & (100%) !PATH!")).FullName;
        var receiver = Path.Combine(_root, "receive.ps1");
        var result = Path.Combine(_root, "arguments.json");
        File.WriteAllText(receiver, "[IO.File]::WriteAllText($env:AMBER_TEST_ARGS_OUTPUT, (ConvertTo-Json -InputObject @($args) -Compress))");
        var expected = new[]
        {
            "plain", "space here", "", "line\tspace",
            "-Dlog4j.configurationFile=" + Path.Combine(work, "run0 & (100%) !PATH!.bat.amber", "client.xml"),
            "%PATH%", "!PATH!", "x^y", "x|y", "a>b", "a<b",
            "-Dquoted=\"value & other\"", "C:\\last\\", "a\\\"b", "\"&echo escaped",
        };
        var script = Path.Combine(_root, "run.bat");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Toolbox.WriteLaunchScript(script, powershell, work,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", receiver, .. expected], "0");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /v:on /c \"\"{script}\"\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["AMBER_TEST_ARGS_OUTPUT"] = result;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        Assert.Equal(expected, System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(result)));
    }

    [Fact]
    public void LaunchScript_OnUnixQuotesPathsAndIsExecutable()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "run.sh");
        Toolbox.WriteLaunchScript(path, "/opt/Java's/bin/java", "/tmp/game dir", ["-Dname=Steve's world"], "");
        var text = File.ReadAllText(path);
        Assert.StartsWith("#!/bin/sh\n", text);
        Assert.Contains("'/opt/Java'\"'\"'s/bin/java'", text);
        Assert.Contains("'-Dname=Steve'\"'\"'s world'", text);
        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
    }
}
