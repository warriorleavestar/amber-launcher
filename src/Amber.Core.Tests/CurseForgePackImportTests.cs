using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>整合包自己带了受限文件时，校验后复用；普通下载仍保持原来的 overrides 覆盖顺序。</summary>
public sealed class CurseForgePackImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-cf-pack-" + Guid.NewGuid().ToString("N"));
    private readonly PackHandler _handler = new();
    private readonly HttpClient _http;
    private readonly GameDirectory _dir;
    private readonly ModpackService _packs;

    public CurseForgePackImportTests()
    {
        _http = new HttpClient(_handler);
        _dir = new GameDirectory(Path.Combine(_root, "game"));
        Directory.CreateDirectory(_dir.VersionDir("1.21.1"));
        File.WriteAllText(_dir.VersionJsonPath("1.21.1"), """{"id":"1.21.1","mainClass":"Example","libraries":[]}""");
        var downloader = new Downloader(_http, SourceChain.OfficialFirst) { RetriesPerSource = 1 };
        var context = new LoaderContext(downloader, _dir, new VanillaInstaller(downloader, _dir),
            (_, _) => throw new InvalidOperationException("测试不应执行 Java"));
        _packs = new ModpackService(context, new ModrinthProvider(_http), new CurseForgeProvider(_http, () => "test-key"));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    private string Pack(string format, string contents, string root = "", string? allowedOverride = null)
    {
        var path = Path.Combine(_root, "pack.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = format == "CurseForge" ? "manifest.json" : "mcbbs.packmeta";
        // CF 的 overrides 目录可以改名；MCBBS 固定使用 overrides。
        var overrides = format == "CurseForge" ? "custom-overrides/" : "overrides/";
        var metadata = JsonNode.Parse(format == "CurseForge"
            ? """{"manifestType":"minecraftModpack","name":"Pack","minecraft":{"version":"1.21.1"},"files":[{"projectID":123,"fileID":456,"required":true}],"overrides":"custom-overrides"}"""
            : """{"name":"Pack","addons":[{"id":"game","version":"1.21.1"}],"files":[{"type":"curse","projectID":123,"fileID":456}]}""")!;
        if (allowedOverride is not null)
        {
            metadata["files"]!.AsArray().Add(new JsonObject { ["projectID"] = 789, ["fileID"] = 101, ["required"] = true });
            Write(zip, root + overrides + "mods/allowed.jar", allowedOverride);
        }
        Write(zip, root + entry, metadata.ToJsonString());
        Write(zip, root + overrides + "mods/example.jar", contents);
        Write(zip, root + overrides + "config/pack.txt", "pack configuration");
        return path;
    }

    private static void Write(ZipArchive zip, string path, string contents)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    [Theory]
    [InlineData("CurseForge", "")]
    [InlineData("CurseForge", "Wrapped/")]
    [InlineData("MCBBS", "")]
    [InlineData("MCBBS", "Wrapped/")]
    public async Task MatchingRestrictedFileInOverridesCompletesWithoutDownloading(string format, string root)
    {
        Assert.Equal("Fresh", await _packs.ImportAsync(Pack(format, "hello", root), "Fresh", _dir.VersionDir));

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_dir.VersionDir("Fresh"), "mods", "example.jar")));
        Assert.Equal("pack configuration", await File.ReadAllTextAsync(Path.Combine(_dir.VersionDir("Fresh"), "config", "pack.txt")));
        Assert.Equal(0, _handler.FileDownloads);
    }

    [Theory]
    [InlineData("CurseForge", false)]
    [InlineData("CurseForge", true)]
    [InlineData("MCBBS", false)]
    [InlineData("MCBBS", true)]
    public async Task UnverifiedOverrideIsRejectedAndNewInstanceIsRolledBack(string format, bool wrongSize)
    {
        if (wrongSize) _handler.Size = 99;
        var error = await Assert.ThrowsAsync<ManualDownloadRequiredException>(() =>
            _packs.ImportAsync(Pack(format, wrongSize ? "hello" : "wrong"), "Fresh", _dir.VersionDir));

        Assert.Equal("456", error.File.FileId);
        Assert.Equal(0, _handler.FileDownloads);
        Assert.False(Directory.Exists(_dir.VersionDir("Fresh")));
        Assert.True(File.Exists(_dir.VersionJsonPath("1.21.1")));
    }

    [Fact]
    public async Task AllowedCurseForgeFileIsDownloadedBeforeOverrideWins()
    {
        _handler.AllowDistribution = true;

        await _packs.ImportAsync(Pack("CurseForge", "override wins"), "Fresh", _dir.VersionDir);

        Assert.Equal(1, _handler.FileDownloads);
        Assert.Equal("override wins", await File.ReadAllTextAsync(Path.Combine(_dir.VersionDir("Fresh"), "mods", "example.jar")));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("override wins")]
    public async Task RestrictedPreflightKeepsAllowedFilesDownloadThenOverrideOrder(string allowedOverride)
    {
        _handler.IncludeAllowedFile = true;

        await _packs.ImportAsync(Pack("CurseForge", "hello", allowedOverride: allowedOverride), "Fresh", _dir.VersionDir);

        // 即使 allowed.jar 在包内与下载内容相同，也不能因提前释放所有 overrides 而跳过原有下载步骤。
        Assert.Equal(1, _handler.FileDownloads);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_dir.VersionDir("Fresh"), "mods", "example.jar")));
        Assert.Equal(allowedOverride, await File.ReadAllTextAsync(Path.Combine(_dir.VersionDir("Fresh"), "mods", "allowed.jar")));
    }

    private sealed class PackHandler : HttpMessageHandler
    {
        private const string DownloadUrl = "https://files.example.invalid/example.jar";
        public bool AllowDistribution { get; set; }
        public bool IncludeAllowedFile { get; set; }
        public long Size { get; set; } = 5;
        public int FileDownloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsoluteUri == DownloadUrl)
            {
                FileDownloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello", Encoding.UTF8) });
            }
            JsonArray data = request.RequestUri.AbsolutePath switch
            {
                "/v1/mods/files" => new JsonArray(new JsonObject
                {
                    ["id"] = 456, ["modId"] = 123, ["fileName"] = "example.jar", ["downloadUrl"] = DownloadUrl, ["fileLength"] = Size,
                    ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d" }),
                    ["gameVersions"] = new JsonArray("1.21.1"), ["dependencies"] = new JsonArray(),
                }),
                "/v1/mods" => new JsonArray(new JsonObject
                {
                    ["id"] = 123, ["name"] = "Example", ["classId"] = 6, ["allowModDistribution"] = AllowDistribution,
                    ["links"] = new JsonObject { ["websiteUrl"] = "https://www.curseforge.com/projects/123" },
                }),
                _ => throw new InvalidOperationException("未预期的请求：" + request.RequestUri),
            };
            if (IncludeAllowedFile)
            {
                var allowed = data[0]!.DeepClone().AsObject();
                allowed["id"] = request.RequestUri.AbsolutePath == "/v1/mods/files" ? 101 : 789;
                if (request.RequestUri.AbsolutePath == "/v1/mods/files")
                {
                    allowed["modId"] = 789;
                    allowed["fileName"] = "allowed.jar";
                }
                else allowed["allowModDistribution"] = true;
                data.Add(allowed);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
