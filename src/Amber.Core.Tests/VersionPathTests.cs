using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>
/// R1：版本文件（可能来自整合包）里的 id、jar、inheritsFrom、assetIndex.id、logging.file.id、库路径和 natives classifier 路径
/// 不能让文件写到游戏目录的对应子目录外面。每种字段都放一个“诱饵”文件在外面，补全文件后诱饵必须原封不动，而且一次网络请求都不发。
/// </summary>
public sealed class VersionPathTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "amber-r1-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _game;
    private readonly string _sentinel;

    public VersionPathTests()
    {
        _game = Path.Combine(_base, "game");
        Directory.CreateDirectory(_game);
        _sentinel = Path.Combine(_base, "sentinel.txt");
        File.WriteAllText(_sentinel, "keep");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { }
    }

    /// <summary>任何请求都算失败：路径检查必须在下载之前拦住。</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent("evil"u8.ToArray()) });
        }
    }

    private void Version(string id, string body)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_game, "versions", id)).FullName;
        File.WriteAllText(Path.Combine(dir, id + ".json"), body);
    }

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    // 每个值都指向 game 外面的 sentinel.txt（相对游戏目录下各子目录的不同深度）。
    public static TheoryData<string> Attacks => new()
    {
        // 库：artifact.path
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[{"name":"a:b:1","downloads":{"artifact":{"path":"../../sentinel.txt","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}}]}""",
        // 库：绝对路径、盘符、UNC
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[{"name":"a:b:1","downloads":{"artifact":{"path":"C:\\sentinel.txt","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}}]}""",
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[{"name":"a:b:1","downloads":{"artifact":{"path":"\\\\server\\share\\x","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}}]}""",
        // 库：Maven 名字里藏 ..
        """{"id":"v","type":"release","mainClass":"x","libraries":[{"name":"..:..:..","url":"https://example.invalid/"}]}""",
        // 旧格式 natives 的 classifier 路径
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[{"name":"a:b:1","natives":{"windows":"natives-windows","linux":"natives-linux","osx":"natives-osx"},"downloads":{"classifiers":{"natives-windows":{"path":"../../sentinel.txt","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4},"natives-linux":{"path":"../../sentinel.txt","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4},"natives-osx":{"path":"../../sentinel.txt","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}}}]}""",
        // jar
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[],"jar":"../../../sentinel","downloads":{"client":{"url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}}""",
        // assetIndex.id
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[],"assetIndex":{"id":"../../../sentinel","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}""",
        // logging.file.id
        $$$$$"""{"id":"v","type":"release","mainClass":"x","libraries":[],"logging":{"client":{"argument":"-Dx=${path}","file":{"id":"../../../sentinel.txt","url":"https://example.invalid/x","sha1":"{{{{{Sha}}}}}","size":4}}}}""",
        // inheritsFrom
        """{"id":"v","type":"release","mainClass":"x","libraries":[],"inheritsFrom":"../../../x"}""",
    };

    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task UntrustedVersionFieldsCannotEscape(string json)
    {
        Version("v", json);
        var handler = new NoNetwork();
        var installer = new VanillaInstaller(new Downloader(new HttpClient(handler), SourceChain.OfficialFirst), new GameDirectory(_game));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.CompleteAsync("v"));
        Assert.Equal("keep", File.ReadAllText(_sentinel));
        Assert.Equal(0, handler.Calls);
        Assert.Single(Directory.EnumerateFileSystemEntries(_base, "*", SearchOption.TopDirectoryOnly).Where(p => File.Exists(p)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData("x.")]
    [InlineData("")]
    public void BadIdsAreRejected(string id) => Assert.Throws<InvalidDataException>(() => new GameDirectory(_game).VersionDir(id));

    [Theory]
    [InlineData("org/a/b/1/b-1.jar", true)]
    [InlineData("../x.jar", false)]
    [InlineData("a/../../x.jar", false)]
    [InlineData("/x.jar", false)]
    [InlineData("C:/x.jar", false)]
    [InlineData("a/x.jar:ads", false)]
    public void LibraryPathsStayInside(string relative, bool ok)
    {
        var dir = new GameDirectory(_game);
        if (ok) Assert.StartsWith(dir.LibrariesDir + Path.DirectorySeparatorChar, dir.LibraryPath(relative));
        else Assert.Throws<InvalidDataException>(() => dir.LibraryPath(relative));
    }

    [Fact]
    public void AssetHashesMustBeHex()
    {
        var dir = new GameDirectory(_game);
        Assert.Throws<InvalidDataException>(() => dir.AssetObjectPath("../../../../sentinel.txt0000000000000000"));
        Assert.EndsWith(Sha, dir.AssetObjectPath(Sha));
    }

    [Fact]
    public void DownloaderChecksFinalTarget()
    {
        var libraries = Path.Combine(_game, "libraries");
        var inside = Path.Combine(libraries, "a.jar");
        Downloader.CheckTarget(new DownloadItem("https://x", inside, Within: libraries));
        Assert.Throws<InvalidDataException>(() => Downloader.CheckTarget(new DownloadItem("https://x", _sentinel, Within: libraries)));
        Assert.Throws<InvalidDataException>(() => Downloader.CheckTarget(new DownloadItem("https://x", Path.Combine(libraries, "..", "..", "sentinel.txt"), Within: libraries)));
        Assert.Throws<InvalidDataException>(() => Downloader.CheckTarget(new DownloadItem("https://x", "relative.jar", Within: libraries)));
        // 没设 Within：用户自己选的位置（工具箱里手打的文件夹，可能不是规范写法）照原样用。
        Downloader.CheckTarget(new DownloadItem("https://x", Path.Combine(_game, "tools", "..", "tools", "x.zip")));
        Downloader.CheckTarget(new DownloadItem("https://x", _game + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + "x.zip"));
    }

    /// <summary>1.5.2 以前的 Forge：版本文件里的 amberLegacyLibs 名字带路径时不复制，lib/ 外面的文件原封不动。</summary>
    [Theory]
    [InlineData("../../../sentinel.txt")]
    [InlineData("..\\..\\..\\sentinel.txt")]
    [InlineData("C:\\sentinel.txt")]
    [InlineData("sub/x.jar")]
    [InlineData("..")]
    public void LegacyForgeLibNamesCannotEscape(string name)
    {
        var cache = LegacyForge.FmlCacheDir(_game);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "evil.jar"), "evil");
        var work = Path.Combine(_game, "versions", "old");
        Assert.Throws<InvalidDataException>(() => LegacyForge.EnsureLibraries(_game, work, [new FmlLibrary(name, Sha)], null));
        Assert.Equal("keep", File.ReadAllText(_sentinel));
    }

    /// <summary>旧版资源索引（virtual / map_to_resources）里的名字跳出目录：不复制，直接报错。</summary>
    [Theory]
    [InlineData(false, "../../../../sentinel.txt")]
    [InlineData(true, "../../sentinel.txt")]
    [InlineData(true, "C:/sentinel.txt")]
    [InlineData(false, "/sentinel.txt")]
    public void LegacyAssetNamesCannotEscape(bool mapToResources, string name)
    {
        var dir = new GameDirectory(_game);
        var obj = dir.AssetObjectPath(Sha);
        Directory.CreateDirectory(Path.GetDirectoryName(obj)!);
        File.WriteAllText(obj, "evil");
        Directory.CreateDirectory(Path.GetDirectoryName(dir.AssetIndexPath("legacy"))!);
        File.WriteAllText(dir.AssetIndexPath("legacy"),
            $$$$"""{"{{{{(mapToResources ? "map_to_resources" : "virtual")}}}}":true,"objects":{"{{{{name}}}}":{"hash":"{{{{Sha}}}}","size":4}}}""");
        Version("old", """{"id":"old","type":"release","mainClass":"x","libraries":[],"assets":"legacy"}""");
        var launcher = new Amber.Core.Launch.GameLauncher(dir);
        var work = Path.Combine(_game, "versions", "old");
        Assert.Throws<InvalidDataException>(() => launcher.PrepareLegacyAssets(dir.ResolveVersion("old"), work));
        Assert.Equal("keep", File.ReadAllText(_sentinel));
        Assert.Single(Directory.EnumerateFiles(_base));
    }

    /// <summary>natives 包里的条目名带反斜杠的“..”：只取文件名平铺进 natives；带盘符的直接拒绝。</summary>
    [Fact]
    public void NativeEntriesStayInNativesDir()
    {
        var dir = new GameDirectory(_game);
        var jar = dir.LibraryPath("a/b/1/b-1-natives-windows.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
        void Pack(params string[] names)
        {
            File.Delete(jar);
            using var zip = System.IO.Compression.ZipFile.Open(jar, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var n in names)
                using (var w = new StreamWriter(zip.CreateEntry(n).Open())) w.Write("dll");
        }
        Version("n", """{"id":"n","type":"release","mainClass":"x","libraries":[{"name":"a:b:1:natives-windows"}]}""");
        var launcher = new Amber.Core.Launch.GameLauncher(dir) { BaseRules = new RuleEvaluator { OsName = "windows", Arch = "x86_64", OsVersion = "10.0" } };

        Pack("..\\..\\..\\..\\..\\evil.dll", "sub/../../ok.dll");
        launcher.ExtractNatives("n", dir.ResolveVersion("n"));
        Assert.True(File.Exists(Path.Combine(dir.NativesDir("n"), "evil.dll")));
        Assert.True(File.Exists(Path.Combine(dir.NativesDir("n"), "ok.dll")));
        Assert.Single(Directory.EnumerateFiles(_base));
        Assert.Empty(Directory.EnumerateFiles(_game, "*.dll", SearchOption.TopDirectoryOnly));

        Pack("C:\\evil.dll");
        Assert.Throws<InvalidDataException>(() => launcher.ExtractNatives("n", dir.ResolveVersion("n")));
    }
}
