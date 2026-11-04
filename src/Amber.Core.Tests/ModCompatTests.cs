using System.IO.Compression;
using Amber.Core.Content;

namespace Amber.Core.Tests;

public class ModCompatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-compat-" + Guid.NewGuid().ToString("N"));

    public ModCompatTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Jar(string name, params (string Entry, string Text)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(text);
        }
        return path;
    }

    [Theory]
    [InlineData("1.20.1", ">=1.20 <1.21", "fabric", true)]
    [InlineData("1.21.1", ">=1.20 <1.21", "fabric", false)]
    [InlineData("1.20.4", "1.20.x", "fabric", true)]
    [InlineData("1.21", "1.20.x", "fabric", false)]
    [InlineData("1.20.1", "~1.20", "fabric", true)]
    [InlineData("1.20.6", "~1.20.1", "fabric", true)]
    [InlineData("1.21.0", "~1.20.1", "fabric", false)]
    [InlineData("1.21.1", "1.21.1", "fabric", true)]
    [InlineData("1.21", "1.20.1 || 1.21", "fabric", true)]
    [InlineData("1.20.1", "*", "fabric", true)]
    [InlineData("1.20.1", "[1.20,1.21)", "maven", true)]
    [InlineData("1.21", "[1.20,1.21)", "maven", false)]
    [InlineData("1.21.1", "[1.21.1]", "maven", true)]
    [InlineData("1.21.2", "[1.21.1]", "maven", false)]
    [InlineData("1.21.1", "[1.21,)", "maven", true)]
    [InlineData("1.20.1", "[1.20.1,1.20.2)", "maven", true)]
    [InlineData("24w14a", ">=1.20", "fabric", true)]   // 快照版本号看不懂就放行，不误报
    public void VersionRanges(string version, string range, string syntax, bool expected) =>
        Assert.Equal(expected, ModCompat.VersionMatches(version, range, syntax));

    private const string FabricJson = """{"schemaVersion":1,"id":"coolmod","version":"1.0","depends":{"fabricloader":">=0.15","minecraft":"~1.21.1","fabric-api":"*","cloth-config":">=15"}}""";

    private const string NeoToml = """
        modLoader="javafml"
        loaderVersion="[4,)"
        [[mods]]
        modId="neomod"
        [[dependencies.neomod]]
        modId="neoforge"
        type="required"
        versionRange="[21.1,)"
        [[dependencies.neomod]]
        modId="minecraft"
        type="required"
        versionRange="[1.21.1,1.21.2)"
        [[dependencies.neomod]]
        modId="jei"
        type="optional"
        versionRange="*"
        """;

    [Fact]
    public void ReadsLoadersRangesAndDeps()
    {
        var fabric = ModCompat.Read(Jar("f.jar", ("fabric.mod.json", FabricJson)))!;
        Assert.Equal(["fabric"], fabric.Loaders);
        Assert.Equal("~1.21.1", fabric.For("fabric")!.MinecraftRange);
        Assert.Equal(["fabric-api", "cloth-config"], fabric.For("fabric")!.RequiredDeps);
        Assert.Same(fabric.For("fabric"), fabric.For("quilt"));   // Quilt 实例用 Fabric 那份

        var neo = ModCompat.Read(Jar("n.jar", ("META-INF/neoforge.mods.toml", NeoToml)))!;
        Assert.Equal(["neoforge"], neo.Loaders);
        Assert.Equal("[1.21.1,1.21.2)", neo.For("neoforge")!.MinecraftRange);
        Assert.Equal("maven", neo.For("neoforge")!.Syntax);
        Assert.Empty(neo.For("neoforge")!.RequiredDeps);   // neoforge 自带，jei 是可选的

        var both = ModCompat.Read(Jar("b.jar", ("fabric.mod.json", FabricJson), ("META-INF/mods.toml", "[[mods]]\nmodId=\"coolmod\"\n")))!;
        Assert.Equal(["fabric", "forge"], both.Loaders.Order());
        Assert.Null(ModCompat.Read(Jar("x.jar", ("readme.txt", "hi"))));
    }

    /// <summary>像 Sinytra Connector 的 -full.jar：外壳没有元数据，真正的模组在 META-INF/jarjar 里。</summary>
    [Fact]
    public void ReadsNestedJarJarMods()
    {
        var inner = Path.Combine(_dir, "inner.jar");
        using (var zip = ZipFile.Open(inner, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("META-INF/neoforge.mods.toml").Open()))
            w.Write("[[mods]]\nmodId=\"connector\"\n");
        var outer = Path.Combine(_dir, "connector-full.jar");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("META-INF/jarjar/metadata.json").Open()))
                w.Write("""{"jars":[{"identifier":{"group":"org.sinytra","artifact":"connector-mod"},"path":"META-INF/jarjar/connector-mod.jar"}]}""");
            zip.CreateEntryFromFile(inner, "META-INF/jarjar/connector-mod.jar");
        }
        var mod = ModCompat.Read(outer)!;
        Assert.Contains("connector", mod.Ids);
        Assert.Contains("neoforge", mod.Loaders);
        Assert.True(ConnectorInstaller.IsInstalled([mod]));
        Assert.False(ConnectorInstaller.IsInstalled([ModCompat.Read(Jar("f.jar", ("fabric.mod.json", FabricJson)))]));
    }

    /// <summary>同一个 jar 带 Fabric 和 Forge 两份元数据（很多多加载器模组这样做）：只按实例实际用的那份检查。</summary>
    [Fact]
    public void MultiLoaderJarUsesOnlyTheMatchingMetadata()
    {
        var jar = Jar("multi.jar",
            ("fabric.mod.json", """{"schemaVersion":1,"id":"multi","depends":{"minecraft":"~1.19","fabric-api":"*"}}"""),
            ("META-INF/mods.toml", """
                [[mods]]
                modId="multi"
                [[dependencies.multi]]
                modId="minecraft"
                mandatory=true
                versionRange="[1.20.1,1.21)"
                """),
            ("META-INF/neoforge.mods.toml", """
                [[mods]]
                modId="multi"
                [[dependencies.multi]]
                modId="minecraft"
                type="required"
                versionRange="[1.21.1]"
                """));
        var mod = ModCompat.Read(jar)!;
        var none = new HashSet<string>();
        Assert.Equal(CompatLevel.Ok, ModCompat.Check(mod, "forge", "1.20.1", none, false).Level);       // 不受 Fabric 的 ~1.19 和 fabric-api 影响
        Assert.Equal(CompatLevel.Ok, ModCompat.Check(mod, "neoforge", "1.21.1", none, false).Level);    // 用 neoforge.mods.toml
        Assert.Equal(CompatLevel.WrongVersion, ModCompat.Check(mod, "fabric", "1.20.1", new HashSet<string> { "fabric-api" }, false).Level);
    }

    [Fact]
    public void CheckAgainstInstances()
    {
        var fabric = ModCompat.Read(Jar("f.jar", ("fabric.mod.json", FabricJson)))!;
        var neo = ModCompat.Read(Jar("n.jar", ("META-INF/neoforge.mods.toml", NeoToml)))!;
        var withDeps = new HashSet<string> { "fabric-api", "cloth-config" };

        Assert.Equal(CompatLevel.Ok, ModCompat.Check(fabric, "fabric", "1.21.1", withDeps, false).Level);
        Assert.Equal(CompatLevel.Ok, ModCompat.Check(fabric, "quilt", "1.21.1", withDeps, false).Level);   // Quilt 直接用 Fabric 模组
        Assert.Equal(CompatLevel.MissingDeps, ModCompat.Check(fabric, "fabric", "1.21.1", new HashSet<string>(), false).Level);
        Assert.Contains("cloth-config", ModCompat.Check(fabric, "fabric", "1.21.1", new HashSet<string> { "fabric-api" }, false).Text);
        Assert.Equal(CompatLevel.WrongVersion, ModCompat.Check(fabric, "fabric", "1.20.1", withDeps, false).Level);

        // Forge / NeoForge 上的 Fabric 模组：没装 Connector 提示装，装了算“经 Connector”；fabric-api 由 Forgified Fabric API 提供。
        Assert.Equal(CompatLevel.NeedsConnector, ModCompat.Check(fabric, "neoforge", "1.21.1", new HashSet<string> { "cloth-config" }, false).Level);
        Assert.Equal(CompatLevel.ViaConnector, ModCompat.Check(fabric, "neoforge", "1.21.1", new HashSet<string> { "cloth-config" }, true).Level);
        // 前置装的是原生 NeoForge 版（id 是 cloth_config），Connector 认这种别名，不算缺。
        Assert.Equal(CompatLevel.ViaConnector, ModCompat.Check(fabric, "neoforge", "1.21.1", new HashSet<string> { "cloth_config" }, true).Level);
        // 原生加载时不按别名算，id 必须对得上。
        Assert.Equal(CompatLevel.MissingDeps, ModCompat.Check(fabric, "fabric", "1.21.1", new HashSet<string> { "fabric-api", "cloth_config" }, false).Level);

        // Fabric 上的 NeoForge 模组：没有兼容层。
        var wrong = ModCompat.Check(neo, "fabric", "1.21.1", new HashSet<string>(), false);
        Assert.Equal(CompatLevel.WrongLoader, wrong.Level);
        Assert.Contains("没有可靠的办法", wrong.Text);
        Assert.Equal(CompatLevel.Ok, ModCompat.Check(neo, "neoforge", "1.21.1", new HashSet<string>(), false).Level);
        Assert.Equal(CompatLevel.WrongVersion, ModCompat.Check(neo, "neoforge", "1.21.4", new HashSet<string>(), false).Level);
        Assert.Equal(CompatLevel.WrongLoader, ModCompat.Check(neo, null, "1.21.1", new HashSet<string>(), false).Level);
    }
}

public class ConnectorWarmupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-warmup-" + Guid.NewGuid().ToString("N"));

    public ConnectorWarmupTests() => Directory.CreateDirectory(Path.Combine(_dir, "mods"));

    public void Dispose() => Directory.Delete(_dir, true);

    private string Jar(string name, string entry, string text)
    {
        var path = Path.Combine(_dir, "mods", name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry(entry).Open());
        w.Write(text);
        return path;
    }

    [Fact]
    public void PendingUntilConnectorCacheHasTheHash()
    {
        var fabric = Jar("fab.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"fab"}""");
        Jar("neo.jar", "META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"neo\"\n");
        Jar("both.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"both"}""");
        using (var zip = ZipFile.Open(Path.Combine(_dir, "mods", "both.jar"), ZipArchiveMode.Update))
        using (var w = new StreamWriter(zip.CreateEntry("META-INF/neoforge.mods.toml").Open())) w.Write("[[mods]]\nmodId=\"both\"\n");
        var mods = new ModManager(Path.Combine(_dir, "mods")).List();

        // 只有纯 Fabric 的那个要经 Connector；原生 NeoForge 和同时带 NeoForge 元数据的不用。
        Assert.Equal(["fab.jar"], ConnectorWarmup.Pending(_dir, mods, "neoforge").Select(m => m.FileName));
        Assert.Empty(ConnectorWarmup.Pending(_dir, mods, "fabric"));

        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fabric)));
        var cache = ConnectorWarmup.CacheDir(_dir);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "fab_mapped_moj_1.21.1.jar.input"), $"2.0.0-beta.17+1.21.1,client,{sha}");
        Assert.Empty(ConnectorWarmup.Pending(_dir, mods, "neoforge"));
    }

    [Fact]
    public void CacheFromAnotherConnectorVersionDoesNotCount()
    {
        var fabric = Jar("fab.jar", "fabric.mod.json", """{"schemaVersion":1,"id":"fab"}""");
        Jar("connector.jar", "META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"connector\"\nversion=\"2.0.0-beta.18+1.21.1\"\n");
        var mods = new ModManager(Path.Combine(_dir, "mods")).List();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fabric)));
        Directory.CreateDirectory(ConnectorWarmup.CacheDir(_dir));
        var input = Path.Combine(ConnectorWarmup.CacheDir(_dir), "fab_mapped_moj_1.21.1.jar.input");

        // 旧版本 Connector 留下的缓存：更新后它会重新转换，所以还算没处理。
        File.WriteAllText(input, $"2.0.0-beta.17+1.21.1,client,{sha}");
        Assert.Equal(["fab.jar"], ConnectorWarmup.Pending(_dir, mods, "neoforge").Select(m => m.FileName));

        File.WriteAllText(input, $"2.0.0-beta.18+1.21.1,client,{sha}");
        Assert.Empty(ConnectorWarmup.Pending(_dir, mods, "neoforge"));
    }

    [Theory]
    [InlineData("[19:13:51] [main/INFO]: Successfully made module authlib transformable", true)]
    [InlineData("[Render thread/INFO] [net.minecraft.server.packs.resources.ReloadableResourceManager/]: Reloading ResourceManager: vanilla", true)]
    [InlineData("[19:13:50] [main/INFO]: Found mod file \"x_mapped_moj_1.21.1.jar\"", false)]
    public void DetectsEndOfModDiscovery(string line, bool expected) => Assert.Equal(expected, ConnectorWarmup.IsPastModDiscovery(line));
}
