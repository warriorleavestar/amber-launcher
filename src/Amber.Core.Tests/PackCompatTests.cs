using System.IO.Compression;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>套了一层文件夹的整合包、带启动器的压缩包、直接压缩的游戏文件夹、PCL / HMCL 的版本隔离设置。</summary>
public sealed class PackCompatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-packcompat-" + Guid.NewGuid().ToString("N"));

    public PackCompatTests() => Directory.CreateDirectory(_root);

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

    private string MakeBundle(string name, string innerName, string innerPack, params (string Entry, string Text)[] extra)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(innerPack, innerName);
        foreach (var (entry, text) in extra)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    private const string Mrpack = """{"formatVersion":1,"game":"minecraft","name":"R","versionId":"2","files":[],"dependencies":{"minecraft":"1.20.1","fabric-loader":"0.15.11"}}""";

    [Fact]
    public void Inspect_MrpackInsideFolder()
    {
        var info = ModpackService.Inspect(MakeZip("wrapped.zip", ("我的包/modrinth.index.json", Mrpack), ("我的包/overrides/options.txt", "x")));
        Assert.Equal(("Modrinth", "R", "1.20.1", "我的包/"), (info.Format, info.Name, info.McVersion, info.Root));
    }

    [Fact]
    public void Inspect_CurseForgeInsideFolder()
    {
        var info = ModpackService.Inspect(MakeZip("cf-wrapped.zip",
            ("Pack/manifest.json", """{"manifestType":"minecraftModpack","name":"C","minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0","primary":true}]},"files":[]}"""),
            ("Pack/overrides/config/a.toml", "x")));
        Assert.Equal(("CurseForge", LoaderKind.Forge, "Pack/"), (info.Format, info.Loader!.Value, info.Root));
    }

    [Fact]
    public void Inspect_HmclAndMcbbsInsideFolder()
    {
        var hmcl = ModpackService.Inspect(MakeZip("h.zip",
            ("H/modpack.json", """{"name":"H","gameVersion":"1.20.1","addons":[{"id":"game","version":"1.20.1"},{"id":"fabric","version":"0.15.11"}]}"""),
            ("H/minecraft/mods/a.jar", "x")));
        Assert.Equal(("HMCL", "H/minecraft/", "H/"), (hmcl.Format, hmcl.Overrides, hmcl.Root));

        var mcbbs = ModpackService.Inspect(MakeZip("m.zip",
            ("M/mcbbs.packmeta", """{"name":"M","addons":[{"id":"game","version":"1.12.2"},{"id":"forge","version":"14.23.5.2864"}],"files":[]}"""),
            ("M/overrides/mods/a.jar", "x")));
        Assert.Equal(("MCBBS", "M/overrides/"), (mcbbs.Format, mcbbs.Overrides));
    }

    [Fact]
    public void Inspect_ManifestWithAddonsIsMcbbs()
    {
        var info = ModpackService.Inspect(MakeZip("mcbbs-manifest.zip",
            ("manifest.json", """{"manifestType":"minecraftModpack","manifestVersion":2,"name":"B","addons":[{"id":"game","version":"1.16.5"},{"id":"forge","version":"36.2.39"}],"files":[]}"""),
            ("overrides/mods/a.jar", "x")));
        Assert.Equal(("MCBBS", "B", "1.16.5", LoaderKind.Forge, "36.2.39"), (info.Format, info.Name, info.McVersion, info.Loader!.Value, info.LoaderVersion));
    }

    [Fact]
    public void Inspect_TwoTopFoldersIsNotWrapped()
    {
        var path = MakeZip("two.zip", ("A/modrinth.index.json", Mrpack), ("B/readme.txt", "x"));
        Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(path));
    }

    [Theory]
    [InlineData("modpack.mrpack")]
    [InlineData("整合包/modpack.mrpack")]
    public void Inspect_LauncherBundleOpensInnerPack(string innerName)
    {
        var inner = MakeZip("inner.mrpack", ("modrinth.index.json", Mrpack), ("overrides/options.txt", "x"));
        var bundle = MakeBundle("bundle.zip", innerName, inner, ("Plain Craft Launcher 2.exe", "MZ"), ("说明.txt", "双击启动器"));
        var info = ModpackService.Inspect(bundle);
        Assert.Equal(("Modrinth", "R", LoaderKind.Fabric), (info.Format, info.Name, info.Loader!.Value));
    }

    [Fact]
    public void Inspect_RealPackIsNotTreatedAsBundle()
    {
        // 整合包自己的 overrides 里碰巧有 modpack.zip（比如资源），不能当成带启动器的压缩包。
        var path = MakeZip("real.mrpack", ("modrinth.index.json", Mrpack), ("overrides/modpack.zip", "x"));
        Assert.Equal("Modrinth", ModpackService.Inspect(path).Format);
    }

    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "RAR")]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00 }, "7z")]
    public void Inspect_RarAnd7zGetClearMessage(byte[] head, string kind)
    {
        var path = Path.Combine(_root, "pack." + kind.ToLowerInvariant());
        File.WriteAllBytes(path, [.. head, .. new byte[64]]);
        Assert.Contains(kind, Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(path)).Message);
    }

    // ---- 直接压缩的游戏文件夹 ----

    [Fact]
    public void Inspect_GameFolderIsolatedFabric()
    {
        var path = MakeZip("client.zip",
            ("我的整合包/启动器.exe", "MZ"),
            ("我的整合包/.minecraft/versions/1.20.1-Fabric/1.20.1-Fabric.json",
                """{"id":"1.20.1-Fabric","inheritsFrom":"1.20.1","libraries":[{"name":"net.fabricmc:intermediary:1.20.1"},{"name":"net.fabricmc:fabric-loader:0.15.11"}]}"""),
            ("我的整合包/.minecraft/versions/1.20.1-Fabric/mods/sodium.jar", "x"),
            ("我的整合包/.minecraft/versions/1.20.1/1.20.1.json", """{"id":"1.20.1"}"""),
            ("我的整合包/.minecraft/libraries/a.jar", "x"));
        var info = ModpackService.Inspect(path);
        Assert.Equal(("GameFolder", "1.20.1-Fabric", "1.20.1", LoaderKind.Fabric, "0.15.11"), (info.Format, info.Name, info.McVersion, info.Loader!.Value, info.LoaderVersion));
        Assert.Equal(("我的整合包/.minecraft/versions/1.20.1-Fabric/", "我的整合包/.minecraft/"), (info.Overrides, info.Root));
    }

    [Fact]
    public void Inspect_GameFolderSharedMergedForge()
    {
        // 没有 inheritsFrom 的合并版本文件：从 forge 库认出游戏版本。
        var path = MakeZip("forge.zip",
            (".minecraft/versions/整合包/整合包.json", """{"id":"整合包","libraries":[{"name":"net.minecraftforge:forge:1.12.2-14.23.5.2860"},{"name":"optifine:OptiFine:1.12.2_HD_U_G5"}]}"""),
            (".minecraft/mods/a.jar", "x"),
            (".minecraft/options.txt", "x"));
        var info = ModpackService.Inspect(path);
        Assert.Equal(("1.12.2", LoaderKind.Forge, "1.12.2-14.23.5.2860", ".minecraft/"), (info.McVersion, info.Loader!.Value, info.LoaderVersion, info.Overrides));
    }

    [Fact]
    public void Inspect_GameFolderWithoutVersionFails()
    {
        var path = MakeZip("bad.zip", ("versions/我的世界/我的世界.json", """{"id":"我的世界","libraries":[]}"""));
        Assert.Contains("我的世界", Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(path)).Message);
    }

    [Theory]
    [InlineData("""{"inheritsFrom":"1.21.1"}""", "x", "1.21.1")]
    [InlineData("""{"patches":[{"id":"game","version":"1.19.2"},{"id":"forge","version":"43.2.0"}]}""", "x", "1.19.2")]
    [InlineData("""{"libraries":[{"name":"org.quiltmc:hashed:1.20.4"}]}""", "x", "1.20.4")]
    [InlineData("""{"arguments":{"game":["--fml.neoForgeVersion","21.1.77","--fml.mcVersion","1.21.1"]}}""", "x", "1.21.1")]
    [InlineData("""{"jar":"1.8.9"}""", "x", "1.8.9")]
    [InlineData("""{}""", "1.16.5", "1.16.5")]
    [InlineData("""{}""", "24w14a", "24w14a")]
    [InlineData("""{}""", "生存", null)]
    public void McVersionOf(string json, string id, string? expected) =>
        Assert.Equal(expected, ModpackService.McVersionOf(JsonNode.Parse(json)!.AsObject(), id));

    [Theory]
    [InlineData(".minecraft/", "mods/a.jar", false)]
    [InlineData(".minecraft/", "saves/w/level.dat", false)]
    [InlineData(".minecraft/", "versions/x/x.json", true)]
    [InlineData(".minecraft/", "libraries/a/b.jar", true)]
    [InlineData(".minecraft/", "assets/indexes/5.json", true)]
    [InlineData(".minecraft/", "launcher_accounts.json", true)]
    [InlineData(".minecraft/", "launcher_profiles.json", true)]
    [InlineData(".minecraft/", "PCL/Setup.ini", true)]
    [InlineData(".minecraft/", "logs/latest.log", true)]
    [InlineData(".minecraft/versions/V/", "V.json", true)]
    [InlineData(".minecraft/versions/V/", "v.JAR", true)]
    [InlineData(".minecraft/versions/V/", "V-natives/lwjgl.dll", true)]
    [InlineData(".minecraft/versions/V/", "natives/lwjgl.dll", true)]
    [InlineData(".minecraft/versions/V/", "hmclversion.cfg", true)]
    [InlineData(".minecraft/versions/V/", "mods/V.json.jar", false)]
    [InlineData(".minecraft/versions/V/", "config/V.json", false)]
    public void GameFolderSkipsLauncherFiles(string overrides, string relative, bool skipped)
    {
        var info = new ModpackInfo("GameFolder", "V", null, "1.20.1", null, null, 0, overrides, ".minecraft/");
        Assert.Equal(skipped, ModpackService.SkipGameFolderEntry(info, relative));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GameFolderExtractsOnlyGameContent(bool isolated)
    {
        var v = isolated ? ".minecraft/versions/V/" : ".minecraft/";
        var path = MakeZip($"extract-{isolated}.zip",
            (".minecraft/versions/V/V.json", """{"id":"V","inheritsFrom":"1.20.1"}"""),
            (".minecraft/versions/V/V.jar", "jar"),
            (".minecraft/libraries/x/y.jar", "lib"),
            (".minecraft/launcher_accounts.json", "token"),
            (v + "mods/a.jar", "mod"),
            (v + "config/a.toml", "cfg"),
            (v + "saves/w/level.dat", "world"),
            (v + "options.txt", "opt"),
            (v + "amber-instance.json", """{"PreLaunchCommand":"calc"}"""),
            (v + "PCL/Setup.ini", "x"),
            (v + "logs/latest.log", "log"));
        using var zip = ZipFile.OpenRead(path);
        var info = ModpackService.InspectGameFolder(zip)!;
        Assert.Equal(v, info.Overrides);

        var target = Path.Combine(_root, "game", "versions", "Imported");
        ModpackService.ExtractGameFolder(zip, info, new ModpackService.PackTarget(target, target, "Imported"));
        var files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(target, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["config/a.toml", "mods/a.jar", "options.txt", "saves/w/level.dat"], files);
    }

    // ---- PCL / HMCL 的版本隔离 ----

    private GameDirectory MakeVersion(string id, params (string Relative, string Text)[] files)
    {
        var dir = new GameDirectory(Path.Combine(_root, "game"));
        var versionDir = dir.VersionDir(id);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, id + ".json"), "{}");
        foreach (var (relative, text) in files)
        {
            var path = Path.Combine(versionDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return dir;
    }

    [Theory]
    [InlineData("VersionArgumentIndieV2:True\nVersionRamType:1", true)]
    [InlineData("VersionArgumentIndieV2:False", false)]
    [InlineData("VersionArgumentIndie:1", true)]
    [InlineData("VersionArgumentIndie:2", false)]
    [InlineData("VersionArgumentIndie:0", null)]
    public void PclIsolation(string ini, bool? expected)
    {
        var dir = MakeVersion("P", ("PCL/Setup.ini", ini));
        Assert.Equal(expected, OtherLauncherSettings.Isolation(dir, "P"));
    }

    [Theory]
    [InlineData("""{"gameDirType":1}""", true)]
    [InlineData("""{"gameDirType":0}""", false)]
    [InlineData("""{"gameDirType":"VERSION_FOLDER"}""", true)]
    [InlineData("""{"gameDirType":"ROOT_FOLDER"}""", false)]
    [InlineData("""{"gameDirType":2,"gameDir":"D:/x"}""", null)]
    [InlineData("""{"usesGlobal":true,"gameDirType":1}""", null)]
    [InlineData("not json", null)]
    public void HmclIsolation(string cfg, bool? expected)
    {
        var dir = MakeVersion("H", ("hmclversion.cfg", cfg));
        Assert.Equal(expected, OtherLauncherSettings.Isolation(dir, "H"));
    }

    [Fact]
    public void OtherLauncherVersionWithContentIsIsolated()
    {
        var dir = MakeVersion("H", ("hmclversion.cfg", """{"usesGlobal":true}"""), ("mods/a.jar", "x"));
        Assert.True(OtherLauncherSettings.Isolation(dir, "H"));
    }

    [Fact]
    public void OwnVersionIsLeftAlone()
    {
        // 不是其他启动器建的版本：即使版本目录里有 mods，也按 Amber 自己的设置来。
        var dir = MakeVersion("A", ("mods/a.jar", "x"));
        Assert.Null(OtherLauncherSettings.Isolation(dir, "A"));
        Assert.Null(OtherLauncherSettings.Isolation(dir, "missing"));
    }
}
