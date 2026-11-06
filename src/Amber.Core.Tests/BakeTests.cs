using System.Text.Json.Nodes;
using Amber.Core.Auth;
using Amber.Core.Bake;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>实验功能“烘焙”：不需要真的加载器，检查启动参数、结果解析、缓存判断和支持范围。</summary>
public sealed class BakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-bake-" + Guid.NewGuid().ToString("N")[..8]);

    public BakeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Game => Path.Combine(_root, "game");

    private void Version(string id, string? inherits = null, string libraries = "[]")
    {
        var dir = Directory.CreateDirectory(Path.Combine(Game, "versions", id)).FullName;
        var parent = inherits is null ? "" : $"\"inheritsFrom\":\"{inherits}\",";
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$$"""{"id":"{{{id}}}",{{{parent}}}"type":"release","mainClass":"net.minecraft.client.main.Main","libraries":{{{libraries}}},"arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]}}""");
        File.WriteAllText(Path.Combine(dir, id + ".jar"), "jar");
    }

    private BakeToolkit Toolkit()
    {
        var root = Path.Combine(_root, "loader");
        Directory.CreateDirectory(Path.Combine(root, "build", "loader"));
        Directory.CreateDirectory(Path.Combine(root, "mc", "mappings-amber"));
        File.WriteAllText(Path.Combine(root, "build", "loader-cp.txt"), "libs/a.jar;libs/b.jar\n");
        return BakeToolkit.Find(root)!;
    }

    [Fact]
    public void ToolkitNeedsTheBuiltLoaderAndMappings()
    {
        Assert.Null(BakeToolkit.Find(Path.Combine(_root, "missing")));
        var toolkit = Toolkit();
        Assert.Equal(3, toolkit.Classpath.Count);
        Assert.EndsWith(Path.Combine("build", "loader"), toolkit.Classpath[0]);
        Assert.EndsWith(Path.Combine("libs", "b.jar"), toolkit.Classpath[2]);
        Assert.EndsWith("mappings-amber", toolkit.MappingsDir);
    }

    [Fact]
    public void BakedLaunchReplacesMainClassAndClasspathButKeepsGameArguments()
    {
        Version("1.21.1");
        var launcher = new GameLauncher(new GameDirectory(Game));
        var baked = new BakedLaunch("amber.ul.Launcher", [Path.Combine(_root, "loader.jar"), Path.Combine(_root, "lib.jar")],
            ["-Dlog4j2.formatMsgNoLookups=true"], ["--state", Path.Combine(_root, "state.json"), "--"], Path.Combine(_root, "state.json"), "key");
        var args = launcher.BuildArguments(new LaunchOptions
        {
            VersionId = "1.21.1",
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
            Baked = baked,
        });

        var main = args.IndexOf("amber.ul.Launcher");
        Assert.True(main > 0);
        Assert.DoesNotContain("net.minecraft.client.main.Main", args);
        Assert.Equal(["--state", Path.Combine(_root, "state.json"), "--"], args.Skip(main + 1).Take(3));
        Assert.Equal(["--username", "Tester"], args.Skip(main + 4).Take(2));
        Assert.Contains("-Dlog4j2.formatMsgNoLookups=true", args.Take(main));
        var classpath = args[args.IndexOf("-cp") + 1];
        Assert.Equal(string.Join(Path.PathSeparator, baked.Classpath), classpath);
        Assert.DoesNotContain("1.21.1.jar", classpath);
    }

    [Fact]
    public void NormalLaunchIsUnchanged()
    {
        Version("1.21.1");
        var args = new GameLauncher(new GameDirectory(Game)).BuildArguments(new LaunchOptions
        {
            VersionId = "1.21.1",
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        });
        Assert.Contains("net.minecraft.client.main.Main", args);
        Assert.DoesNotContain("amber.ul.Launcher", args);
    }

    [Fact]
    public void ResultParsingReadsLaunchAndRejectsIncompleteResults()
    {
        var result = BakeService.ParseLine("""{"type":"result","stateKey":"abc","launch":{"mainClass":"amber.ul.Launcher","classpath":["a","b"],"jvmArgs":["-Dx=1"],"gameArgsPrefix":["--state","s","--"],"stateFile":"s"}}""")!;
        var launch = BakeService.ParseLaunch(result);
        Assert.Equal("amber.ul.Launcher", launch.MainClass);
        Assert.Equal(["a", "b"], launch.Classpath);
        Assert.Equal("abc", launch.StateKey);
        Assert.Throws<InvalidDataException>(() => BakeService.ParseLaunch(new JsonObject { ["type"] = "result" }));
        Assert.Null(BakeService.ParseLine("[amber-ul] not json"));
        Assert.Null(BakeService.ParseLine("{broken"));
    }

    [Fact]
    public void ProblemsNameTheMod()
    {
        var problem = BakeService.ParseLine("""{"type":"problem","severity":"error","code":"missing_dep","modId":"sodium","message":"needs fabric-api"}""")!;
        Assert.Equal("sodium: needs fabric-api", BakeService.DescribeProblem(problem));
    }

    [Fact]
    public void OnlySupportedVersionsAndLoadersAreBaked()
    {
        var supported = new[] { "1.21.1" };
        Assert.True(BakeService.Supports(new GameInstance("a", "1.21.1", "fabric", "0.16", _root), supported, out _));
        Assert.True(BakeService.Supports(new GameInstance("b", "1.21.1", null, null, _root), supported, out _));
        Assert.False(BakeService.Supports(new GameInstance("c", "1.20.1", "fabric", "0.16", _root), supported, out var version));
        Assert.Contains("1.20.1", version);
        Assert.False(BakeService.Supports(new GameInstance("d", "1.21.1", "neoforge", null, _root), supported, out var loader));
        Assert.Contains("neoforge", loader);
    }

    [Fact]
    public void PreviousBakeIsReusedUntilModsChange()
    {
        Version("1.21.1");
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(work, "mods")).FullName;
        File.WriteAllText(Path.Combine(mods, "a.jar"), "a");
        var instance = new GameInstance("1.21.1", "1.21.1", "fabric", "0.16", work);
        var dir = new GameDirectory(Game);
        var bake = new BakeService(Toolkit(), Path.Combine(_root, "cache"));
        var state = Path.Combine(work, BakeService.StateFolder, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{}");
        var launch = new BakedLaunch("amber.ul.Launcher", ["a"], [], ["--state", state, "--"], state, "k");
        BakeRecord.Write(work, new BakeRecord(bake.InputKey(dir, instance, 21), launch));

        Assert.Equal(launch.MainClass, bake.Current(dir, instance, 21)?.MainClass);
        Assert.Null(bake.Current(dir, instance, 25));

        File.WriteAllText(Path.Combine(mods, "b.jar"), "b");
        Assert.Null(bake.Current(dir, instance, 21));

        File.Delete(Path.Combine(mods, "b.jar"));
        Assert.NotNull(bake.Current(dir, instance, 21));
        File.Delete(state);
        Assert.Null(bake.Current(dir, instance, 21));
    }

    private (BakeService Bake, GameDirectory Directory, GameInstance Instance) ReusableBake()
    {
        Version("1.21.1");
        var toolkit = Toolkit();
        foreach (var relative in new[] { "libs/a.jar", "libs/b.jar", "build/loader/amber/ul/Bake.class", "build/loader/loader.properties",
                     "mc/mappings-amber/client_mappings.txt", "mc/mappings-amber/intermediary-1.21.1-v2.jar" })
        {
            var file = Path.Combine(toolkit.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "old");
        }
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var state = Path.Combine(work, BakeService.StateFolder, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{}");
        var instance = new GameInstance("1.21.1", "1.21.1", "fabric", "0.16", work);
        var directory = new GameDirectory(Game);
        var bake = new BakeService(toolkit, Path.Combine(_root, "cache"));
        var launch = new BakedLaunch("amber.ul.Launcher", toolkit.Classpath, [], ["--state", state, "--"], state, "key");
        BakeRecord.Write(work, new BakeRecord(bake.InputKey(directory, instance, 21), launch));
        Assert.NotNull(bake.Current(directory, instance, 21));
        return (bake, directory, instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdatingClasspathInTheSameToolkitInvalidatesPreviousLaunch(bool removeOldDependency)
    {
        var (bake, dir, instance) = ReusableBake();
        var root = bake.Toolkit.Root;
        File.WriteAllText(Path.Combine(root, "libs", "a-v2.jar"), "new");
        File.WriteAllText(Path.Combine(root, "build", "loader-cp.txt"), "libs/a-v2.jar;libs/b.jar\n");
        if (removeOldDependency) File.Delete(Path.Combine(root, "libs", "a.jar"));

        // 与每次准备启动一样重新发现工具包：旧记录不能让新工具包继续使用旧依赖列表。
        var updated = new BakeService(BakeToolkit.Find(root)!, bake.CacheRoot);
        Assert.Null(updated.Current(dir, instance, 21));
    }

    [Theory]
    [InlineData("libs/a.jar")]
    [InlineData("build/loader/amber/ul/Bake.class")]
    [InlineData("build/loader/loader.properties")]
    [InlineData("mc/mappings-amber/client_mappings.txt")]
    [InlineData("mc/mappings-amber/intermediary-1.21.1-v2.jar")]
    public void ChangedToolkitInputsInvalidateEvenWhenSizeAndTimestampArePreserved(string relative)
    {
        var (bake, dir, instance) = ReusableBake();
        var file = Path.Combine(bake.Toolkit.Root, relative);
        var time = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "new");
        File.SetLastWriteTimeUtc(file, time);

        Assert.Null(bake.Current(dir, instance, 21));
    }

    [Fact]
    public void MergedMappingsTakePrecedenceAndTheirChangesInvalidate()
    {
        var (bake, dir, instance) = ReusableBake();
        var merged = Path.Combine(bake.Toolkit.MappingsDir, "merged.tiny");
        File.WriteAllText(merged, "old");
        Assert.Null(bake.Current(dir, instance, 21));
        var record = BakeRecord.Read(instance.WorkDir)!;
        BakeRecord.Write(instance.WorkDir, record with { Key = bake.InputKey(dir, instance, 21) });

        // 有预合并映射时原型不会读取源映射，改源文件不该让缓存反复失效。
        File.WriteAllText(Path.Combine(bake.Toolkit.MappingsDir, "client_mappings.txt"), "unused change");
        Assert.NotNull(bake.Current(dir, instance, 21));
        File.WriteAllText(merged, "new");
        Assert.Null(bake.Current(dir, instance, 21));
    }

    [Fact]
    public void GeneratedFilesAndUnusedMappingsDoNotInvalidatePreviousBake()
    {
        var (bake, dir, instance) = ReusableBake();
        Directory.CreateDirectory(bake.CacheRoot);
        File.WriteAllText(Path.Combine(bake.CacheRoot, "generated.jar"), "generated");
        File.WriteAllText(Path.Combine(bake.Toolkit.Root, "build", "build.log"), "build output");
        File.WriteAllText(Path.Combine(bake.Toolkit.MappingsDir, "intermediary-1.20.1-v2.jar"), "unused mapping");
        // 清单的换行和空白不改变实际 classpath。
        File.WriteAllText(Path.Combine(bake.Toolkit.Root, "build", "loader-cp.txt"), "libs/a.jar\r\n  libs/b.jar\r\n");
        var unchanged = new BakeService(BakeToolkit.Find(bake.Toolkit.Root)!, bake.CacheRoot);

        Assert.NotNull(unchanged.Current(dir, instance, 21));
    }

    [Fact]
    public void GameInputsUseTheVanillaParentWithoutLoaderLibraries()
    {
        Version("1.21.1", libraries: """[{"name":"com.example:base:1","downloads":{"artifact":{"path":"com/example/base/1/base-1.jar","url":"https://example.invalid/base-1.jar"}}}]""");
        Version("fabric-1.21.1", "1.21.1", """[{"name":"net.fabricmc:fabric-loader:0.16.0","url":"https://maven.fabricmc.net/"}]""");
        var (jar, libraries) = BakeService.GameInputs(new GameDirectory(Game), new GameInstance("fabric-1.21.1", "1.21.1", "fabric", "0.16.0", _root));
        Assert.EndsWith(Path.Combine("1.21.1", "1.21.1.jar"), jar);
        Assert.Single(libraries);
        Assert.DoesNotContain(libraries, l => l.Contains("fabric-loader"));
    }
}
