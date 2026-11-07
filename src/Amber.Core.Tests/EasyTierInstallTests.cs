using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>联机组件安装：自带安装包、多个下载来源依次尝试、哈希不对就拒绝、手动导入。</summary>
public sealed class EasyTierInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-et-install-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _good;
    private readonly EasyTierRelease.Asset _asset;

    public EasyTierInstallTests()
    {
        Directory.CreateDirectory(_root);
        _good = MakeZip("good");
        _asset = new EasyTierRelease.Asset("test-x64", "easytier-test-x64-v2.6.4.zip",
            Convert.ToHexStringLower(SHA256.HashData(_good)), _good.Length, "easytier-core.exe", "easytier-cli.exe");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static byte[] MakeZip(string marker)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "easytier-core.exe", "easytier-cli.exe", "WinDivert64.sys" })
                using (var w = new StreamWriter(zip.CreateEntry("easytier-test/" + name).Open())) w.Write(marker + name);
        return ms.ToArray();
    }

    /// <summary>同样大小但内容不同的数据（模拟被篡改的镜像）。</summary>
    private byte[] Tampered()
    {
        var bad = (byte[])_good.Clone();
        bad[bad.Length / 2] ^= 0xFF;
        return bad;
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private (EasyTierInstaller Installer, Handler Handler) Create(Func<string, HttpResponseMessage> respond, string? bundledDir = null)
    {
        var handler = new Handler(respond);
        var downloader = new Downloader(new HttpClient(handler), new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        IReadOnlyList<(string, string)> sources = [("A", "https://a.test/et.zip"), ("B", "https://b.test/et.zip"), ("C", "https://c.test/et.zip")];
        var installer = new EasyTierInstaller(downloader, Path.Combine(_root, "data"), bundledDir ?? Path.Combine(_root, "no-bundle"), _asset, sources);
        return (installer, handler);
    }

    [Fact]
    public async Task UsesBundledPackageWithoutNetwork()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        var (installer, handler) = Create(_ => throw new InvalidOperationException("不该联网"), bundle);

        await installer.EnsureAsync();

        Assert.True(installer.IsInstalled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FallsBackFromBrokenBundleAndFailedSources()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), Tampered());
        var (installer, handler) = Create(url => url.Contains("a.test") ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : url.Contains("b.test") ? Bytes(Tampered()) : Bytes(_good), bundle);

        await installer.EnsureAsync();

        Assert.True(installer.IsInstalled);
        Assert.Contains(handler.Requests, u => u.Contains("c.test"));
        // 装好后下载用的临时文件不留。
        Assert.False(File.Exists(Path.Combine(_root, "data", "easytier", "download", _asset.Name)));
    }

    [Fact]
    public async Task ReportsEverySourceWhenAllFail()
    {
        var (installer, _) = Create(url => url.Contains("a.test") ? throw new TaskCanceledException("timeout")
            : url.Contains("b.test") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Bytes(Tampered()));

        var e = await Assert.ThrowsAsync<EasyTierInstallException>(() => installer.EnsureAsync());

        Assert.Equal(["A", "B", "C"], e.Failures.Select(f => f.Source));
        Assert.Equal(L.T("common.request_timeout"), e.Failures[0].Reason);
        Assert.Contains("403", e.Failures[1].Reason);
        Assert.Contains(_asset.Name, e.Message);
        Assert.False(installer.IsInstalled);
        // 内容被改过的包丢掉，不能留给下一次续传。
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "data", "easytier", "download")));
    }

    [Fact]
    public async Task ImportRequiresPinnedHash()
    {
        var (installer, _) = Create(_ => throw new InvalidOperationException("不该联网"));
        var bad = Path.Combine(_root, "bad.zip");
        File.WriteAllBytes(bad, Tampered());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(bad));
        var other = Path.Combine(_root, "other.zip");
        File.WriteAllBytes(other, MakeZip("other-size-" + new string('x', 50)));
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(other));
        Assert.Contains(_asset.Name, wrong.Message);
        Assert.False(installer.IsInstalled);

        var good = Path.Combine(_root, "good.zip");
        File.WriteAllBytes(good, _good);
        await installer.ImportAsync(good);
        Assert.True(installer.IsInstalled);
    }

    [Fact]
    public void OrdersMirrorsAndKeepsOfficial()
    {
        var asset = EasyTierRelease.For("windows", System.Runtime.InteropServices.Architecture.X64);
        var official = EasyTierRelease.Sources(asset, false);
        var mirrored = EasyTierRelease.Sources(asset, true);
        Assert.Equal("GitHub", official[0].Name);
        Assert.Equal("GitHub", mirrored[^1].Name);
        Assert.All(mirrored, s => Assert.EndsWith(asset.Url, s.Url));
        Assert.Equal(EasyTierRelease.Mirrors.Count + 1, mirrored.Count);
    }

    [Fact]
    public void TimeoutIsNotShownAsCancel()
    {
        Assert.Equal(L.T("common.request_timeout"), EasyTierInstaller.Describe(new TaskCanceledException()));
        Assert.Equal(L.T("common.request_timeout"), EasyTierInstaller.Describe(new TimeoutException("x", new TaskCanceledException())));
    }
}
