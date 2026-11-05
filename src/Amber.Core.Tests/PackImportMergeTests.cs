using System.IO.Compression;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>非标准整合包入口也必须遵循实例目录的独占安装和失败回滚约定。</summary>
public sealed class PackImportMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-pack-import-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new(new NoNetworkHandler());
    private readonly GameDirectory _dir;
    private readonly ModpackService _packs;

    public PackImportMergeTests()
    {
        Directory.CreateDirectory(_root);
        _dir = new GameDirectory(Path.Combine(_root, "game"));
        var downloader = new Downloader(_http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        var context = new LoaderContext(downloader, _dir, new VanillaInstaller(downloader, _dir),
            (_, _) => throw new InvalidOperationException("测试不应执行 Java"));
        _packs = new ModpackService(context, new ModrinthProvider(_http), new CurseForgeProvider(_http, () => null));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private byte[] BaseVersion()
    {
        Directory.CreateDirectory(_dir.VersionDir("1.20.1"));
        var path = _dir.VersionJsonPath("1.20.1");
        File.WriteAllText(path, """{"id":"1.20.1","mainClass":"Example","libraries":[]}""");
        return File.ReadAllBytes(path);
    }

    private string Pack(string format, bool escape = false)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var prefix = format == "folder" ? ".minecraft/versions/Original/" : "Wrapped/overrides/";
            if (format == "folder")
                Write(zip, prefix + "Original.json", """{"id":"Original","inheritsFrom":"1.20.1","libraries":[]}""");
            else
                Write(zip, "Wrapped/modrinth.index.json", """{"name":"Wrapped","dependencies":{"minecraft":"1.20.1"},"files":[]}""");
            Write(zip, prefix + "config/pack.txt", "整合包配置");
            if (escape) Write(zip, prefix + "../../escape.txt", "拒绝越界");
        }
        if (format != "bundle") return path;
        var bundle = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(path, "整合包/modpack.mrpack");
            Write(zip, "Plain Craft Launcher 2.exe", "MZ");
        }
        return bundle;
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(text);
    }

    [Theory]
    [InlineData("wrapped")]
    [InlineData("bundle")]
    [InlineData("folder")]
    public async Task CompatiblePackImportsIntoNewIsolatedDirectory(string format)
    {
        var baseBytes = BaseVersion();
        var work = Path.Combine(_dir.VersionDir("Fresh"), "game");

        Assert.Equal("Fresh", await _packs.ImportAsync(Pack(format), "Fresh", _ => work));

        Assert.Equal("整合包配置", File.ReadAllText(Path.Combine(work, "config", "pack.txt")));
        Assert.True(File.Exists(_dir.VersionJsonPath("Fresh")));
        Assert.Equal(baseBytes, File.ReadAllBytes(_dir.VersionJsonPath("1.20.1")));
        Assert.Equal([Path.Combine(work, "config", "pack.txt")], Directory.GetFiles(work, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("wrapped")]
    [InlineData("bundle")]
    [InlineData("folder")]
    public async Task CompatiblePackCannotAdoptExistingInstance(string format)
    {
        var target = _dir.VersionDir("Existing");
        Directory.CreateDirectory(Path.Combine(target, "saves"));
        var save = Path.Combine(target, "saves", "keep.dat");
        File.WriteAllText(save, "原有存档");

        await Assert.ThrowsAsync<IOException>(() => _packs.ImportAsync(Pack(format), "Existing", _dir.VersionDir));

        Assert.Equal("原有存档", File.ReadAllText(save));
        Assert.False(Directory.Exists(_dir.VersionDir("1.20.1")));
    }

    [Theory]
    [InlineData("wrapped")]
    [InlineData("bundle")]
    [InlineData("folder")]
    public async Task CompatiblePackFailureRollsBackOnlyNewDirectories(string format)
    {
        var baseBytes = BaseVersion();
        var work = Path.Combine(_root, "fresh-work");
        var sentinel = Path.Combine(_root, "keep.txt");
        File.WriteAllText(sentinel, "保留");

        await Assert.ThrowsAsync<InvalidDataException>(() => _packs.ImportAsync(Pack(format, escape: true), "Fresh", _ => work));

        Assert.False(Directory.Exists(_dir.VersionDir("Fresh")));
        Assert.False(Directory.Exists(work));
        Assert.Equal("保留", File.ReadAllText(sentinel));
        Assert.Equal(baseBytes, File.ReadAllBytes(_dir.VersionJsonPath("1.20.1")));
        Assert.Empty(Directory.GetFiles(_root, "escape.txt", SearchOption.AllDirectories));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("测试不应访问网络");
    }
}
