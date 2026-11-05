using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public sealed class InstanceOwnershipSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-instance-owner-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;
    private readonly FixtureHandler _handler = new();
    private readonly LoaderContext _ctx;
    private readonly ModpackService _packs;

    public InstanceOwnershipSecurityTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_handler);
        var dir = new GameDirectory(Path.Combine(_root, "game"));
        var downloader = new Downloader(_http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        _ctx = new LoaderContext(downloader, dir, new VanillaInstaller(downloader, dir), (_, _) => throw new InvalidOperationException("测试不应执行 Java"));
        _packs = new ModpackService(_ctx, new ModrinthProvider(_http), new CurseForgeProvider(_http, () => null));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Pack(string? loader = null, string mc = "1.20.1", string name = "Existing")
    {
        var dependencies = new JsonObject { ["minecraft"] = mc };
        if (loader is not null) dependencies[loader] = loader == "forge" ? "47.2.0" : "0.16.0";
        var index = new JsonObject { ["name"] = name, ["dependencies"] = dependencies, ["files"] = new JsonArray() };
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mrpack");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open())) writer.Write(index.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/pack.txt").Open())) writer.Write("整合包配置");
        return file;
    }

    private void BaseVersion(string mc = "1.20.1")
    {
        Directory.CreateDirectory(_ctx.Dir.VersionDir(mc));
        File.WriteAllText(_ctx.Dir.VersionJsonPath(mc), new JsonObject { ["id"] = mc, ["mainClass"] = "Example", ["libraries"] = new JsonArray() }.ToJsonString());
        using var zip = ZipFile.Open(_ctx.Dir.VersionJarPath(mc), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("base.txt").Open());
        writer.Write("base");
    }

    private Dictionary<string, byte[]> Existing(string id)
    {
        var root = _ctx.Dir.VersionDir(id);
        Directory.CreateDirectory(Path.Combine(root, "saves", "World"));
        var paths = new[] { Path.Combine(root, "saves", "World", "level.dat"), Path.Combine(root, "amber-instance.json"), _ctx.Dir.VersionJsonPath(id), _ctx.Dir.VersionJarPath(id) };
        foreach (var path in paths) File.WriteAllText(path, "必须保留：" + Path.GetFileName(path));
        return paths.ToDictionary(p => p, File.ReadAllBytes);
    }

    private static void Unchanged(Dictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fabric-loader")]
    [InlineData("forge")]
    [InlineData("neoforge")]
    public async Task CollidingImportPreservesExistingInstanceBeforeAnyDownload(string? loader)
    {
        var saved = Existing("Existing");
        await Assert.ThrowsAsync<IOException>(() => _packs.ImportAsync(Pack(loader), "Existing", _ctx.Dir.VersionDir));
        Unchanged(saved);
        Assert.Equal(0, _handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyDirectoryAndFileCollisionsAreNotAdopted(bool file)
    {
        Directory.CreateDirectory(_ctx.Dir.VersionsDir);
        var target = _ctx.Dir.VersionDir("Existing");
        if (file) File.WriteAllText(target, "保留"); else Directory.CreateDirectory(target);
        await Assert.ThrowsAsync<IOException>(() => _packs.ImportAsync(Pack(), "Existing", _ctx.Dir.VersionDir));
        if (file) Assert.Equal("保留", File.ReadAllText(target)); else Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task WindowsCaseAliasCannotReplaceAnExistingInstance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var saved = Existing("Existing");
        await Assert.ThrowsAsync<IOException>(() => _packs.ImportAsync(Pack(), "existing", _ctx.Dir.VersionDir));
        Unchanged(saved);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task ExistingIndependentWorkDirectoryIsPreserved()
    {
        var work = Path.Combine(_root, "chosen-work");
        Directory.CreateDirectory(work);
        var save = Path.Combine(work, "save.dat");
        File.WriteAllText(save, "保留工作目录");
        await Assert.ThrowsAsync<IOException>(() => _packs.ImportAsync(Pack(), "Fresh", _ => work));
        Assert.Equal("保留工作目录", File.ReadAllText(save));
        Assert.False(Directory.Exists(_ctx.Dir.VersionDir("Fresh")));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task BaseVersionAliasIsRejectedBeforeCreatingDependencies()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _packs.ImportAsync(Pack(), "1.20.1", _ctx.Dir.VersionDir));
        Assert.False(Directory.Exists(_ctx.Dir.VersionDir("1.20.1")));
        Assert.Equal(0, _handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WorkDirectoryCannotOverlapBaseOrSharedRoot(int form)
    {
        var work = form switch { 0 => _ctx.Dir.VersionDir("1.20.1"), 1 => Path.Combine(_ctx.Dir.VersionDir("1.20.1"), "game"), _ => _ctx.Dir.Root };
        await Assert.ThrowsAsync<InvalidDataException>(() => _packs.ImportAsync(Pack(), "Fresh", _ => work));
        Assert.False(Directory.Exists(_ctx.Dir.VersionDir("Fresh")));
        Assert.Equal(0, _handler.Requests);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, "1.20.1")]
    [InlineData(LoaderKind.NeoForge, "1.20.1")]
    [InlineData(LoaderKind.Forge, "1.5.2")]
    public async Task DirectLoaderCollisionPreservesExistingSaves(LoaderKind kind, string mc)
    {
        var saved = Existing("Existing");
        var loader = LoaderInstallers.Create(kind, _ctx);
        await Assert.ThrowsAsync<IOException>(() => loader.InstallAsync(new LoaderVersion(kind, mc, "invalid"), "Existing"));
        Unchanged(saved);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task DirectLegacyEntryAlsoRefusesExistingDirectory()
    {
        var saved = Existing("Existing");
        await Assert.ThrowsAsync<IOException>(() => LegacyForge.InstallAsync(_ctx, new LoaderVersion(LoaderKind.Forge, "1.5.2", "invalid"), "Existing", null, default));
        Unchanged(saved);
        Assert.Equal(0, _handler.Requests);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, "1.20.1")]
    [InlineData(LoaderKind.NeoForge, "1.20.1")]
    [InlineData(LoaderKind.Forge, "1.5.2")]
    public async Task LoaderCannotAdoptItsOwnBaseVersion(LoaderKind kind, string mc)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => LoaderInstallers.Create(kind, _ctx).InstallAsync(new LoaderVersion(kind, mc, "invalid"), mc));
        Assert.False(Directory.Exists(_ctx.Dir.VersionDir(mc)));
        Assert.Equal(0, _handler.Requests);
    }

    [Theory]
    [InlineData("1.20.1", false)]
    [InlineData("1.20.1", true)]
    [InlineData("1.5.2", false)]
    [InlineData("1.5.2", true)]
    public async Task FailedOrCancelledLoaderRemovesOnlyItsNewTarget(string mc, bool cancel)
    {
        BaseVersion(mc);
        var saved = Existing("Unrelated");
        var baseBytes = File.ReadAllBytes(_ctx.Dir.VersionJsonPath(mc));
        using var cts = new CancellationTokenSource();
        _handler.Respond = _ =>
        {
            Assert.True(Directory.Exists(_ctx.Dir.VersionDir("Fresh")));
            if (cancel) { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var install = new ForgeInstaller(_ctx).InstallAsync(new LoaderVersion(LoaderKind.Forge, mc, "invalid"), "Fresh", ct: cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        else if (mc == "1.5.2") await Assert.ThrowsAsync<InvalidDataException>(() => install);
        else await Assert.ThrowsAsync<HttpRequestException>(() => install);
        Assert.True(_handler.Requests > 0);
        Assert.False(Directory.Exists(_ctx.Dir.VersionDir("Fresh")));
        Assert.Equal(baseBytes, File.ReadAllBytes(_ctx.Dir.VersionJsonPath(mc)));
        Unchanged(saved);
    }

    [Fact]
    public async Task ImportFailureAfterInstallationRollsBackOnlyItsReservedDirectories()
    {
        BaseVersion();
        var saved = Existing("Unrelated");
        var pack = Pack();
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("overrides/../../escape.txt").Open());
            writer.Write("拒绝越界");
        }
        var work = Path.Combine(_root, "fresh-work");
        await Assert.ThrowsAsync<InvalidDataException>(() => _packs.ImportAsync(pack, "Fresh", _ => work));
        Assert.False(Directory.Exists(_ctx.Dir.VersionDir("Fresh")));
        Assert.False(Directory.Exists(work));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.True(File.Exists(_ctx.Dir.VersionJsonPath("1.20.1")));
        Unchanged(saved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fabric-loader")]
    [InlineData("forge")]
    public async Task FreshImportRetainsItsFilesAndSharedVanilla(string? loader)
    {
        BaseVersion();
        var baseBytes = File.ReadAllBytes(_ctx.Dir.VersionJsonPath("1.20.1"));
        _handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith(".jar", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InstallerArchive()) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"inheritsFrom\":\"1.20.1\",\"mainClass\":\"Example\",\"libraries\":[]}") };
        Assert.Equal("Fresh", await _packs.ImportAsync(Pack(loader), "Fresh", _ctx.Dir.VersionDir));
        Assert.Equal("整合包配置", File.ReadAllText(Path.Combine(_ctx.Dir.VersionDir("Fresh"), "config", "pack.txt")));
        Assert.True(File.Exists(_ctx.Dir.VersionJsonPath("Fresh")));
        Assert.Equal(baseBytes, File.ReadAllBytes(_ctx.Dir.VersionJsonPath("1.20.1")));
        await _ctx.Vanilla.InstallAsync("1.20.1");
        await _ctx.Vanilla.CompleteAsync("Fresh");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSeparateAndNestedWorkDirectoriesRemainSupported(bool nested)
    {
        BaseVersion();
        var work = nested ? Path.Combine(_ctx.Dir.VersionDir("Fresh"), "game") : Path.Combine(_root, "fresh-work");
        await _packs.ImportAsync(Pack(), "Fresh", _ => work);
        Assert.Equal("整合包配置", File.ReadAllText(Path.Combine(work, "config", "pack.txt")));
        Assert.True(File.Exists(_ctx.Dir.VersionJsonPath("Fresh")));
    }

    [Fact]
    public void ReservingTheSameTargetTwiceCannotAdoptTheFirstOperation()
    {
        var path = Path.Combine(_root, "reserved");
        using var first = InstanceInstallDirectory.Reserve(path);
        var sentinel = Path.Combine(path, "owned.txt");
        File.WriteAllText(sentinel, "first");
        Assert.Throws<IOException>(() => InstanceInstallDirectory.Reserve(path));
        Assert.Equal("first", File.ReadAllText(sentinel));
        first.Complete();
    }

    private static byte[] InstallerArchive()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("install_profile.json").Open())) writer.Write("{\"json\":\"/version.json\",\"processors\":[]}");
            using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open())) writer.Write("{\"mainClass\":\"Example\",\"libraries\":[]}");
        }
        return stream.ToArray();
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(Respond(request));
        }
    }
}
