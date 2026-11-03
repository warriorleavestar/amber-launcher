using System.IO.Compression;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;

namespace Amber.Core.Tests;

/// <summary>其他启动器的整合包格式、数据包管理、NBT 字符串。</summary>
public class PackFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-packfmt-" + Guid.NewGuid().ToString("N"));

    public PackFormatTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string MakeZip(string name, params (string Entry, string Text)[] files)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    private const string MmcPack = """
        {"components":[
          {"uid":"org.lwjgl3","version":"3.3.1"},
          {"uid":"net.minecraft","version":"1.20.1"},
          {"uid":"net.fabricmc.intermediary","version":"1.20.1"},
          {"uid":"net.fabricmc.fabric-loader","version":"0.15.11"}],"formatVersion":1}
        """;

    [Fact]
    public void Inspect_MultiMcInstanceInsideFolder()
    {
        var pack = MakeZip("mmc.zip",
            ("My Pack/instance.cfg", "[General]\nInstanceType=OneSix\nname=我的整合包\nOverrideMemory=true\nMaxMemAlloc=6144\n"),
            ("My Pack/mmc-pack.json", MmcPack),
            ("My Pack/.minecraft/mods/a.jar", "x"),
            ("My Pack/.minecraft/options.txt", "lang:zh_cn"));
        var info = ModpackService.Inspect(pack);
        Assert.Equal(("MultiMC", "我的整合包", "1.20.1", LoaderKind.Fabric, "0.15.11", "My Pack/.minecraft/"),
            (info.Format, info.Name, info.McVersion, info.Loader, info.LoaderVersion, info.Overrides));
    }

    [Theory]
    [InlineData("net.minecraftforge", LoaderKind.Forge, "47.2.0")]
    [InlineData("net.neoforged", LoaderKind.NeoForge, "21.1.77")]
    [InlineData("org.quiltmc.quilt-loader", LoaderKind.Quilt, "0.26.0")]
    public void Inspect_PrismComponents(string uid, LoaderKind kind, string version)
    {
        var pack = MakeZip($"prism-{kind}.zip",
            ("instance.cfg", "name=P\n"),
            ("mmc-pack.json", $$"""{"components":[{"uid":"net.minecraft","version":"1.21.1"},{"uid":"{{uid}}","version":"{{version}}"}]}"""),
            ("minecraft/config/a.toml", "x"));
        var info = ModpackService.Inspect(pack);
        Assert.Equal((kind, version, "minecraft/"), (info.Loader!.Value, info.LoaderVersion, info.Overrides));
    }

    [Fact]
    public void Inspect_LegacyMultiMcUsesIntendedVersion()
    {
        var info = ModpackService.Inspect(MakeZip("old.zip", ("instance.cfg", "name=Old\nIntendedVersion=1.7.10\n")));
        Assert.Equal(("1.7.10", (LoaderKind?)null), (info.McVersion, info.Loader));
    }

    [Fact]
    public void Inspect_LiteLoaderIsRejectedClearly()
    {
        var pack = MakeZip("lite.zip", ("mmc-pack.json", """{"components":[{"uid":"net.minecraft","version":"1.12.2"},{"uid":"com.mumfrey.liteloader","version":"1.12.2"}]}"""));
        Assert.Contains("LiteLoader", Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(pack)).Message);
    }

    [Fact]
    public void Inspect_HmclWithAddons()
    {
        var pack = MakeZip("hmcl.zip",
            ("modpack.json", """{"name":"H","version":"1.0","gameVersion":"1.20.1","addons":[{"id":"game","version":"1.20.1"},{"id":"forge","version":"47.2.0"},{"id":"optifine","version":"HD_U_I6"}]}"""),
            ("minecraft/pack.json", "{}"));
        var info = ModpackService.Inspect(pack);
        Assert.Equal(("HMCL", "H", "1.20.1", LoaderKind.Forge, "47.2.0", "minecraft/"), (info.Format, info.Name, info.McVersion, info.Loader, info.LoaderVersion, info.Overrides));
    }

    [Fact]
    public void Inspect_HmclReadsLoaderFromPackJson()
    {
        var pack = MakeZip("hmcl-old.zip",
            ("modpack.json", """{"name":"H2","gameVersion":"1.21.1"}"""),
            ("minecraft/pack.json", """{"id":"H2","inheritsFrom":"1.21.1","libraries":[{"name":"net.fabricmc:intermediary:1.21.1"},{"name":"net.fabricmc:fabric-loader:0.16.5"}]}"""));
        var info = ModpackService.Inspect(pack);
        Assert.Equal((LoaderKind.Fabric, "0.16.5"), (info.Loader!.Value, info.LoaderVersion));
    }

    [Theory]
    [InlineData("""[{"name":"net.minecraftforge:forge:1.20.1-47.2.0:universal"}]""", LoaderKind.Forge, "1.20.1-47.2.0")]
    [InlineData("""[{"name":"net.minecraftforge:fmlloader:1.20.1-47.2.0"},{"name":"net.neoforged:neoforge:21.1.77:universal"}]""", LoaderKind.NeoForge, "21.1.77")]
    [InlineData("""[{"name":"optifine:OptiFine:1.20.1_HD_U_I6"}]""", LoaderKind.OptiFine, "HD_U_I6")]
    [InlineData("""[{"name":"org.quiltmc:quilt-loader:0.26.0"}]""", LoaderKind.Quilt, "0.26.0")]
    public void FromLibraries(string libraries, LoaderKind kind, string version)
    {
        var json = new JsonObject { ["libraries"] = JsonNode.Parse(libraries) };
        Assert.Equal(((LoaderKind?)kind, version), ModpackService.FromLibraries(json));
    }

    [Fact]
    public void Inspect_McbbsPreferredOverManifest()
    {
        var pack = MakeZip("mcbbs.zip",
            ("mcbbs.packmeta", """{"manifestType":"minecraftModpack","manifestVersion":2,"name":"M","version":"3","addons":[{"id":"game","version":"1.12.2"},{"id":"forge","version":"14.23.5.2864"}],"files":[]}"""),
            ("manifest.json", """{"manifestType":"minecraftModpack","name":"other","minecraft":{"version":"1.12.2","modLoaders":[]},"files":[]}"""));
        var info = ModpackService.Inspect(pack);
        Assert.Equal(("MCBBS", "M", LoaderKind.Forge, "14.23.5.2864", "overrides/"), (info.Format, info.Name, info.Loader!.Value, info.LoaderVersion, info.Overrides));
    }

    [Fact]
    public void Inspect_CurseForgeWithInstanceCfgInOverridesStaysCurseForge()
    {
        var pack = MakeZip("cf.zip",
            ("manifest.json", """{"manifestType":"minecraftModpack","name":"C","minecraft":{"version":"1.20.1","modLoaders":[]},"files":[]}"""),
            ("overrides/instance.cfg", "name=x"));
        Assert.Equal("CurseForge", ModpackService.Inspect(pack).Format);
    }

    // ---- 数据包 ----

    private string MakeWorld(Dictionary<string, object>? dataPacks = null)
    {
        var world = Path.Combine(_root, "saves", "w" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(world, "datapacks"));
        var data = new Dictionary<string, object> { ["LevelName"] = "测试世界 😀", ["GameType"] = 1, ["LongArr"] = new long[] { 1, 2 } };
        if (dataPacks is not null) data["DataPacks"] = dataPacks;
        Nbt.WriteFile(Path.Combine(world, "level.dat"), new Dictionary<string, object> { ["Data"] = data }, gzip: true);
        return world;
    }

    private string MakeDatapackZip(string name, string description)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open()))
            w.Write("{\"pack\":{\"pack_format\":48,\"description\":" + description + "}}");
        using (var w = new StreamWriter(zip.CreateEntry("data/demo/function/hi.mcfunction").Open()))
            w.Write("say hi");
        return path;
    }

    [Fact]
    public void Datapacks_ListAddAndToggle()
    {
        var world = MakeWorld(new Dictionary<string, object>
        {
            ["Enabled"] = new Nbt.NbtList(8, ["vanilla", "file/old.zip"]),
            ["Disabled"] = new Nbt.NbtList(8, []),
        });
        DatapackManager.Add(world, MakeDatapackZip("new.zip", "\"§a绿色 说明\""));
        DatapackManager.Add(world, MakeDatapackZip("old.zip", """[{"text":"A"},{"text":"B"}]"""));
        var folder = Path.Combine(world, "datapacks", "folderpack");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.mcmeta"), """{"pack":{"pack_format":15,"description":"folder"}}""");
        Directory.CreateDirectory(Path.Combine(world, "datapacks", "not-a-pack"));

        var list = DatapackManager.List(world);
        Assert.Equal(["folderpack", "new.zip", "old.zip"], list.Select(p => p.FileName));
        Assert.All(list, p => Assert.True(p.Enabled));
        Assert.Equal("绿色 说明", list.Single(p => p.FileName == "new.zip").Description);
        Assert.Equal("AB", list.Single(p => p.FileName == "old.zip").Description);

        DatapackManager.SetEnabled(world, "old.zip", false);
        DatapackManager.SetEnabled(world, "folderpack", false);
        list = DatapackManager.List(world);
        Assert.False(list.Single(p => p.FileName == "old.zip").Enabled);
        Assert.False(list.Single(p => p.FileName == "folderpack").Enabled);
        Assert.True(list.Single(p => p.FileName == "new.zip").Enabled);

        // level.dat 里其他内容（含增补字符、long 数组）原样保留，旧文件留成 level.dat_old。
        var data = (Dictionary<string, object>)Nbt.ReadFile(Path.Combine(world, "level.dat"))["Data"];
        Assert.Equal("测试世界 😀", data["LevelName"]);
        Assert.Equal(new long[] { 1, 2 }, data["LongArr"]);
        var packs = (Dictionary<string, object>)data["DataPacks"];
        Assert.Equal(["vanilla"], ((Nbt.NbtList)packs["Enabled"]).Items);
        Assert.Equal(["file/old.zip", "file/folderpack"], ((Nbt.NbtList)packs["Disabled"]).Items);
        Assert.True(File.Exists(Path.Combine(world, "level.dat_old")));

        DatapackManager.SetEnabled(world, "old.zip", true);
        Assert.True(DatapackManager.List(world).Single(p => p.FileName == "old.zip").Enabled);
    }

    [Fact]
    public void Datapacks_WorldWithoutDataPacksTag()
    {
        var world = MakeWorld();
        DatapackManager.Add(world, MakeDatapackZip("a.zip", "\"x\""));
        DatapackManager.SetEnabled(world, "a.zip", false);
        Assert.False(DatapackManager.List(world).Single().Enabled);
    }

    [Fact]
    public void Datapacks_RejectsResourcePack()
    {
        var path = MakeZip("rp.zip", ("pack.mcmeta", "{}"), ("assets/minecraft/x.png", "x"));
        Assert.False(DatapackManager.IsDatapackZip(path));
        Assert.Throws<InvalidDataException>(() => DatapackManager.Add(MakeWorld(), path));
    }

    [Fact]
    public void CrashAnalyzer_OptiFineForgeMismatch()
    {
        var report = "java.lang.NoSuchMethodError: 'net.minecraft.resources.ResourceLocation net.minecraft.resources.ResourceLocation.parse(java.lang.String)'\n"
                     + "\tat net.minecraftforge.registries.ObjectHolderRegistry.scanTarget\n-- System Details --\n"
                     + "\tOptiFine Version: OptiFine_1.20.1_HD_U_I6\n\tFML: 47.4\n\tForge: net.minecraftforge:47.4.28\n";
        var first = Amber.Core.Diagnostics.CrashAnalyzer.Analyze(report).Findings[0];
        Assert.Equal("OptiFine_1.20.1_HD_U_I6 和 Forge 47.4.28 不兼容", first.Title);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("中文")]
    [InlineData("a\0b")]
    [InlineData("emoji 😀 𝄞")]
    public void ModifiedUtf8RoundTrip(string text)
    {
        var bytes = Nbt.EncodeModifiedUtf8(text);
        Assert.Equal(text, Nbt.DecodeModifiedUtf8(bytes));
        // Java 的写法：U+0000 是 C0 80，增补字符是两个三字节代理。
        if (text.Contains('\0')) Assert.Contains((byte)0xc0, bytes);
        if (text.Contains("😀")) Assert.DoesNotContain(bytes, b => (b & 0xf8) == 0xf0);
    }
}
