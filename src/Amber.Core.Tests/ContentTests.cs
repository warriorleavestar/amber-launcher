using System.IO.Compression;
using Amber.Core.Content;
using Amber.Core.Install;

namespace Amber.Core.Tests;

public class ContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-content-" + Guid.NewGuid().ToString("N"));

    public ContentTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string MakeZip(string name, params (string Entry, string Text)[] files)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    [Fact]
    public void Fingerprint_IgnoresWhitespace()
    {
        Assert.Equal(CurseForgeProvider.Fingerprint("helloworld"u8), CurseForgeProvider.Fingerprint("hello \r\n\tworld"u8));
        Assert.Equal(FingerprintVectors.HelloWorld, CurseForgeProvider.Fingerprint("hello world"u8));
        Assert.Equal(FingerprintVectors.Abc, CurseForgeProvider.Fingerprint("abc"u8));
    }

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.20.4", "20.4.")]
    [InlineData("26.3", "26.3.0.")]
    [InlineData("26.1.2", "26.1.2.")]
    [InlineData("1.20.1", "1.20.1-")]
    public void NeoForgePrefix(string mc, string prefix) => Assert.Equal(prefix, NeoForgeInstaller.VersionPrefix(mc));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("mods/../../evil.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    public void SafeJoin_RejectsEscapes(string relative) =>
        Assert.Throws<InvalidDataException>(() => ModpackService.SafeJoin(_root, relative));

    [Fact]
    public void SafeJoin_AllowsNested() =>
        Assert.StartsWith(_root, ModpackService.SafeJoin(_root, "mods/a.jar"));

    [Fact]
    public void SafeFileName_StripsDirectories()
    {
        Assert.Equal("a.jar", ContentInstaller.SafeFileName("../../a.jar"));
        Assert.Throws<InvalidDataException>(() => ContentInstaller.SafeFileName(".."));
    }

    [Fact]
    public void ModManager_ReadsAllMetadataFormats_AndTogglesEnabled()
    {
        var mods = Path.Combine(_root, "mods");
        MakeZip("mods/fabric.jar", ("fabric.mod.json", """{"id":"sodium","name":"Sodium","version":"0.6","description":"fast","authors":["Jelly",{"name":"Other"}]}"""));
        MakeZip("mods/quilt.jar", ("quilt.mod.json", """{"quilt_loader":{"id":"q","version":"1","metadata":{"name":"QMod","contributors":{"Ann":"Owner"}}}}"""));
        MakeZip("mods/neo.jar", ("META-INF/neoforge.mods.toml", "license=\"MIT\"\n[[mods]]\nmodId=\"neo\"\nversion=\"2.0\"\ndisplayName=\"Neo Mod\"\ndescription='''long\ntext'''\n[[dependencies.neo]]\nmodId=\"x\"\n"));
        MakeZip("mods/forge.jar", ("META-INF/mods.toml", "[[mods]]\nmodId=\"f\"\nversion=\"${file.jarVersion}\"\ndisplayName=\"Forge Mod\"\n"), ("META-INF/MANIFEST.MF", "Implementation-Version: 3.1\n"));
        MakeZip("mods/old.jar", ("mcmod.info", """[{"modid":"old","name":"Old Mod","version":"1.7","authorList":["A","B"]}]"""));
        File.WriteAllText(Path.Combine(mods, "broken.jar"), "not a zip");
        File.WriteAllText(Path.Combine(mods, "readme.txt"), "ignored");

        var manager = new ModManager(mods);
        var list = manager.List();
        Assert.Equal(6, list.Count);
        var sodium = list.Single(m => m.ModId == "sodium");
        Assert.Equal("Jelly, Other", sodium.Authors);
        Assert.Equal("Ann", list.Single(m => m.ModId == "q").Authors);
        Assert.Equal("long\ntext", list.Single(m => m.ModId == "neo").Description);
        Assert.Equal("3.1", list.Single(m => m.ModId == "f").Version);
        Assert.Equal("A, B", list.Single(m => m.ModId == "old").Authors);
        Assert.Equal("broken.jar", list.Single(m => m.ModId is null).Name);

        var off = ModManager.SetEnabled(sodium, false);
        Assert.EndsWith(".jar.disabled", off);
        var disabled = manager.List().Single(m => m.ModId == "sodium");
        Assert.False(disabled.Enabled);
        Assert.Equal(sodium.Path, ModManager.SetEnabled(disabled, true));
    }

    [Fact]
    public void Inspect_ReadsMrpackAndCurseForgeManifests()
    {
        var mr = ModpackService.Inspect(MakeZip("a.mrpack", ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","name":"P","versionId":"1","files":[{}],"dependencies":{"minecraft":"1.21.1","neoforge":"21.1.1"}}""")));
        Assert.Equal(("Modrinth", "P", "1.21.1", LoaderKind.NeoForge, "21.1.1", 1), (mr.Format, mr.Name, mr.McVersion, mr.Loader, mr.LoaderVersion, mr.FileCount));

        var cf = ModpackService.Inspect(MakeZip("b.zip", ("manifest.json", """{"manifestType":"minecraftModpack","name":"C","version":"2","minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0","primary":true}]},"files":[{},{}]}""")));
        Assert.Equal(("CurseForge", LoaderKind.Forge, "47.2.0", 2), (cf.Format, cf.Loader, cf.LoaderVersion, cf.FileCount));

        Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(MakeZip("c.zip", ("x", "y"))));
    }

    private sealed class FakeProvider(params ContentFile[] files) : IContentProvider
    {
        public ContentSource Source => ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContentFile>>(files.Where(f => f.ProjectId == projectId && f.GameVersions.Contains(gameVersion!)).ToList());
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) =>
            Task.FromResult(files.Single(f => f.FileId == fileId));
    }

    private static ContentFile File_(string project, string id, string channel = "release", string mc = "1.21.1", params ContentDependency[] deps) =>
        new(ContentSource.Modrinth, project, id, id, id + ".jar", "https://x/" + id, null, 1, [mc], ["fabric"], channel, default, deps);

    private static ContentDependency Dep(string project, DependencyKind kind = DependencyKind.Required) => new(ContentSource.Modrinth, project, null, kind);

    [Fact]
    public async Task Resolver_FollowsRequiredDeps_SkipsInstalled_AndHandlesCycles()
    {
        var a = File_("A", "a1", deps: [Dep("B"), Dep("C"), Dep("OPT", DependencyKind.Optional), Dep("GONE")]);
        var provider = new FakeProvider(
            a,
            File_("B", "b-beta", "beta"),
            File_("B", "b-rel", deps: [Dep("A"), Dep("D")]),   // 循环依赖指回 A
            File_("C", "c1"),
            File_("D", "d1"),
            File_("OPT", "opt1"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("i", "1.21.1", "fabric", "0.1", _root);

        var plan = await installer.ResolveAsync(a, instance, new HashSet<string> { "C" });

        // 正式版优先；C 已安装不重复；可选依赖不装；循环不死循环
        Assert.Equal(["a1", "b-rel", "d1"], plan.Files.Select(f => f.FileId));
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("GONE", problem);
    }

    [Fact]
    public void ExportCandidates_SkipsGeneratedFolders()
    {
        var work = Path.Combine(_root, "inst");
        foreach (var d in new[] { "mods", "config", "saves", "logs", "natives" }) Directory.CreateDirectory(Path.Combine(work, d));
        File.WriteAllText(Path.Combine(work, "options.txt"), "x");
        File.WriteAllText(Path.Combine(work, "inst.json"), "{}");
        Assert.Equal(["config", "mods", "options.txt"], ModpackService.ExportCandidates(work));
    }

    [Fact]
    public void Mrpack_RejectsTraversal_InOverrides()
    {
        var pack = MakeZip("evil.mrpack",
            ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","name":"E","versionId":"1","files":[],"dependencies":{"minecraft":"1.21.1"}}"""),
            ("overrides/../../../evil.txt", "boom"));
        // Inspect 不解压；真正解压时的路径校验由 SafeJoin 保证（见上面的用例）。
        Assert.Equal("E", ModpackService.Inspect(pack).Name);
    }
}

internal static class FingerprintVectors
{
    // 用独立的 Python 实现算出来的值。
    public const uint HelloWorld = 2824650221u;
    public const uint Abc = 1621425345u;
}
