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
        Assert.Equal("~1.21.1", fabric.MinecraftRange);
        Assert.Equal(["fabric-api", "cloth-config"], fabric.RequiredDeps);

        var neo = ModCompat.Read(Jar("n.jar", ("META-INF/neoforge.mods.toml", NeoToml)))!;
        Assert.Equal(["neoforge"], neo.Loaders);
        Assert.Equal("[1.21.1,1.21.2)", neo.MinecraftRange);
        Assert.Equal("maven", neo.MinecraftRangeSyntax);
        Assert.Empty(neo.RequiredDeps);   // neoforge 自带，jei 是可选的

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

        // Fabric 上的 NeoForge 模组：没有兼容层。
        var wrong = ModCompat.Check(neo, "fabric", "1.21.1", new HashSet<string>(), false);
        Assert.Equal(CompatLevel.WrongLoader, wrong.Level);
        Assert.Contains("没有可靠的办法", wrong.Text);
        Assert.Equal(CompatLevel.Ok, ModCompat.Check(neo, "neoforge", "1.21.1", new HashSet<string>(), false).Level);
        Assert.Equal(CompatLevel.WrongVersion, ModCompat.Check(neo, "neoforge", "1.21.4", new HashSet<string>(), false).Level);
        Assert.Equal(CompatLevel.WrongLoader, ModCompat.Check(neo, null, "1.21.1", new HashSet<string>(), false).Level);
    }
}
