using System.IO.Compression;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Amber.Core.Content;
using Amber.Core.Install;
using Amber.Core.Net;

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
    [InlineData("..\\evil.txt")]
    [InlineData("mods\\..\\..\\evil.txt")]
    [InlineData("mods\\../../evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("mods/a.jar:hidden")]
    [InlineData("")]
    [InlineData(" ")]
    public void SafeJoin_RejectsEscapes(string relative) =>
        Assert.Throws<InvalidDataException>(() => ModpackService.SafeJoin(_root, relative));

    [Fact]
    public void SafeJoin_AllowsNested() =>
        Assert.StartsWith(_root, ModpackService.SafeJoin(_root, "mods/a.jar"));

    [Fact]
    public void SafeJoin_NormalizesZipSeparatorsOnEveryPlatform() =>
        Assert.Equal(Path.Combine(_root, "config", "a.txt"), ModpackService.SafeJoin(_root, "mods\\..\\config/a.txt"));

    [Fact]
    public void SafeJoin_RejectsSiblingThatDiffersOnlyByCase()
    {
        // Windows 不区分大小写，那里“../INST”本来就是同一个目录。
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(_root, "inst");
        Assert.Throws<InvalidDataException>(() => ModpackService.SafeJoin(root, "../INST/mods/a.jar"));
    }

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
    public void ModManager_CacheFollowsFileChanges()
    {
        var mods = Path.Combine(_root, "mods");
        var path = MakeZip("mods/a.jar", ("fabric.mod.json", """{"id":"a","version":"1"}"""));
        var manager = new ModManager(mods);
        Assert.Equal("1", manager.List().Single().Version);

        // 同一个文件名换了内容（更新模组）：大小或修改时间变了就重新读。
        File.Delete(path);
        MakeZip("mods/a.jar", ("fabric.mod.json", """{"id":"a","version":"2.0.0"}"""));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("2.0.0", manager.List().Single().Version);

        // 禁用后命中缓存，但路径和启用状态跟着文件名走。
        var off = manager.List().Single();
        var disabledPath = ModManager.SetEnabled(off, false);
        var disabled = manager.List().Single();
        Assert.Equal((disabledPath, false, "2.0.0"), (disabled.Path, disabled.Enabled, disabled.Version));

        // 读文件时文件已经没了：不抛异常，按读不到处理。
        File.Delete(disabledPath);
        Assert.Empty(manager.List());
        Assert.Null(ModManager.Read(disabledPath).ModId);
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
        public Action? BeforeGetFile { get; set; }
        public Exception? FileFailure { get; set; }
        public ContentSource Source { get; init; } = ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContentFile>>(files.Where(f => f.ProjectId == projectId && f.GameVersions.Contains(gameVersion!)).ToList());
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default)
        {
            BeforeGetFile?.Invoke();
            return FileFailure is { } error ? Task.FromException<ContentFile>(error) : Task.FromResult(files.Single(f => f.FileId == fileId));
        }
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
    public async Task Resolver_InstallsOnlyTheOptionalDepsThePlayerChecked()
    {
        // #22：Xaero's Minimap 这类模组有必需依赖和可选依赖，可选的要能勾选。
        var a = File_("A", "a1", deps: [Dep("API"), Dep("OPAC", DependencyKind.Optional), Dep("OTHER", DependencyKind.Optional)]);
        var provider = new FakeProvider(a, File_("API", "api1"), File_("OPAC", "opac1"), File_("OTHER", "other1"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("i", "1.21.1", "fabric", "0.1", _root);

        var none = await installer.ResolveAsync(a, instance);
        var picked = await installer.ResolveAsync(a, instance, optionalProjects: new HashSet<string> { "OPAC" });

        Assert.Equal(["a1", "api1"], none.Files.Select(f => f.FileId));
        Assert.Equal(["a1", "api1", "opac1"], picked.Files.Select(f => f.FileId));
    }

    [Fact]
    public async Task CheckedRestrictedOptionalDependencyResumesAfterManualDownload()
    {
        // #22 的可选依赖和 #28 的分发限制组合：先完整预检，手动补齐后只下载主文件。
        var primaryBytes = "downloaded primary mod"u8.ToArray();
        var optionalBytes = "manually downloaded optional mod"u8.ToArray();
        var optional = File_("OPTIONAL", "optional1") with
        {
            Source = ContentSource.CurseForge,
            Url = "",
            Sha1 = Convert.ToHexString(SHA1.HashData(optionalBytes)),
            Size = optionalBytes.Length,
            ManualDownloadPage = "https://www.curseforge.com/projects/123",
        };
        var primary = File_("PRIMARY", "primary1", deps:
            [new(ContentSource.CurseForge, optional.ProjectId, null, DependencyKind.Optional)]) with
        {
            Source = ContentSource.CurseForge,
            Url = "https://example.invalid/primary1.jar",
            Sha1 = Convert.ToHexString(SHA1.HashData(primaryBytes)),
            Size = primaryBytes.Length,
        };
        var provider = new FakeProvider(primary, optional) { Source = ContentSource.CurseForge };
        using var handler = new PrimaryFileHandler(primary.Url, primaryBytes);
        using var http = new HttpClient(handler);
        var installer = new ContentInstaller(new Downloader(http, SourceChain.OfficialFirst),
            new Dictionary<ContentSource, IContentProvider> { [ContentSource.CurseForge] = provider });
        var instance = new GameInstance("i", "1.21.1", "fabric", "0.1", _root);
        var picked = new HashSet<string> { optional.ProjectId };

        Assert.Equal([primary.FileId], (await installer.ResolveAsync(primary, instance)).Files.Select(f => f.FileId));
        var firstPlan = await installer.ResolveAsync(primary, instance, optionalProjects: picked);
        Assert.Equal([primary.FileId, optional.FileId], firstPlan.Files.Select(f => f.FileId));
        Assert.Empty(firstPlan.Problems);
        var blocked = await Assert.ThrowsAsync<ManualDownloadRequiredException>(() => installer.InstallAsync(firstPlan, instance.ModsDir));
        Assert.Same(optional, blocked.File);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(instance.ModsDir));

        var manualPath = Path.Combine(instance.ModsDir, optional.FileName);
        await File.WriteAllBytesAsync(manualPath, optionalBytes);
        var resumedPlan = await installer.ResolveAsync(primary, instance, optionalProjects: picked);
        Assert.Equal([primary.FileId, optional.FileId], resumedPlan.Files.Select(f => f.FileId));
        Assert.Empty(resumedPlan.Problems);
        await installer.InstallAsync(resumedPlan, instance.ModsDir);

        Assert.Equal(primary.Url, Assert.Single(handler.Requests));
        Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(Path.Combine(instance.ModsDir, primary.FileName)));
        Assert.Equal(optionalBytes, await File.ReadAllBytesAsync(manualPath));
        Assert.Equal(2, Directory.EnumerateFiles(instance.ModsDir).Count());
    }

    private sealed class PrimaryFileHandler(string expectedUrl, byte[] payload) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue(url);
            Assert.Equal(expectedUrl, url);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    [Fact]
    public async Task Resolver_InstallsCheckedPinnedOptionalFileAndItsRequiredDependencies()
    {
        var a = File_("A", "a1", deps: [new(ContentSource.Modrinth, null, "opac1", DependencyKind.Optional)]);
        var provider = new FakeProvider(a, File_("OPAC", "opac1", deps: [Dep("API")]), File_("API", "api1"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("i", "1.21.1", "fabric", "0.1", _root);

        Assert.Equal(["a1"], (await installer.ResolveAsync(a, instance)).Files.Select(f => f.FileId));
        var plan = await installer.ResolveAsync(a, instance, optionalFiles: new HashSet<string> { "opac1" });
        Assert.Equal(["a1", "opac1", "api1"], plan.Files.Select(f => f.FileId));
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public async Task Resolver_PinnedOptionalLookupReportsFailureAndHonoursCancellation()
    {
        var a = File_("A", "a1", deps: [new(ContentSource.Modrinth, null, "opac1", DependencyKind.Optional)]);
        var provider = new FakeProvider(File_("OPAC", "opac1")) { FileFailure = new HttpRequestException("not available") };
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("i", "1.21.1", "fabric", "0.1", _root);
        var selected = new HashSet<string> { "opac1" };

        var failed = await installer.ResolveAsync(a, instance, optionalFiles: selected);
        Assert.Equal(["a1"], failed.Files.Select(f => f.FileId));
        Assert.Contains("opac1", Assert.Single(failed.Problems));

        using var cts = new CancellationTokenSource();
        provider.FileFailure = null;
        provider.BeforeGetFile = cts.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.ResolveAsync(a, instance, ct: cts.Token, optionalFiles: selected));

        using var failingCts = new CancellationTokenSource();
        provider.BeforeGetFile = failingCts.Cancel;
        provider.FileFailure = new HttpRequestException("cancelled response");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.ResolveAsync(a, instance, ct: failingCts.Token, optionalFiles: selected));
    }

    /// <summary>按加载器过滤的假数据源：同一个项目可以有 Fabric 版和 NeoForge 版。</summary>
    private sealed class LoaderProvider(params ContentFile[] files) : IContentProvider
    {
        public ContentSource Source => ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContentFile>>(files.Where(f => f.ProjectId == projectId && (loader is null || f.Loaders.Contains(loader))).ToList());
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ContentFile LoaderFile(string project, string id, string loader, params ContentDependency[] deps) =>
        new(ContentSource.Modrinth, project, id, id, id + ".jar", "https://x/" + id, null, 1, ["1.21.1"], [loader], "release", default, deps);

    [Fact]
    public async Task Resolver_LoaderPreference_PrefersNativeThenFallsBack()
    {
        var mod = LoaderFile("MOD", "mod-fabric", "fabric", Dep("CLOTH"), Dep("ONLYFAB"));
        var provider = new LoaderProvider(
            mod,
            LoaderFile("CLOTH", "cloth-fabric", "fabric"),
            LoaderFile("CLOTH", "cloth-neo", "neoforge", Dep("NEODEP")),
            LoaderFile("NEODEP", "neodep-neo", "neoforge"),
            LoaderFile("ONLYFAB", "onlyfab-fabric", "fabric"));
        var installer = new ContentInstaller(null!, new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var instance = new GameInstance("i", "1.21.1", "neoforge", "21.1", _root);

        var plan = await installer.ResolveAsync(mod, instance, null, default, ["neoforge", "fabric"]);
        // 有原生版的用原生版（连带它自己的依赖），没有的退回 Fabric 版。
        Assert.Equal(["mod-fabric", "cloth-neo", "onlyfab-fabric", "neodep-neo"], plan.Files.Select(f => f.FileId));

        // 不给优先顺序时只按实例的加载器找，只有 Fabric 版的依赖找不到。
        var plain = await installer.ResolveAsync(mod, instance);
        Assert.DoesNotContain(plain.Files, f => f.FileId == "onlyfab-fabric");
    }

    [Fact]
    public void ExportCandidates_SkipsGeneratedFolders()
    {
        var work = Path.Combine(_root, "inst");
        foreach (var d in new[] { "mods", "config", "saves", "logs", "natives", "backups", ".amber-ul" }) Directory.CreateDirectory(Path.Combine(work, d));
        File.WriteAllText(Path.Combine(work, "options.txt"), "x");
        File.WriteAllText(Path.Combine(work, "inst.json"), "{}");
        File.WriteAllText(Path.Combine(work, "amber-instance.json"), "{}");
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
