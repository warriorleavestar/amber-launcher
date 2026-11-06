using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public sealed class CurseForgeDistributionTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("list")]
    [InlineData("batch")]
    [InlineData("fingerprint")]
    public async Task DisabledDistributionBlocksEveryFileEntryPoint(string entry)
    {
        using var handler = new ApiHandler { AllowDistribution = false };
        using var http = new HttpClient(handler);
        var provider = new CurseForgeProvider(http, () => "test-key");

        var file = await ReadFile(provider, entry);

        Assert.True(file.RequiresManualDownload);
        Assert.Empty(file.Url);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/example/files/456", file.ManualDownloadPage);
        var exception = Assert.Throws<ManualDownloadRequiredException>(() => ContentInstaller.ToItem(file, "unused"));
        Assert.Same(file, exception.File);
        Assert.Equal(1, handler.ProjectRequests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingDownloadUrlNeverSynthesizesCdnUrl(string? url)
    {
        using var handler = new ApiHandler { DownloadUrl = url };
        using var http = new HttpClient(handler);
        var file = await new CurseForgeProvider(http, () => "test-key").GetFileAsync("123", "456");

        Assert.True(file.RequiresManualDownload);
        Assert.DoesNotContain("forgecdn", file.Url);
        Assert.NotNull(file.ManualDownloadPage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task PermittedDistributionPreservesProvidedUrl(bool? allowed)
    {
        using var handler = new ApiHandler { AllowDistribution = allowed };
        using var http = new HttpClient(handler);
        var file = await new CurseForgeProvider(http, () => "test-key").GetFileAsync("123", "456");

        Assert.False(file.RequiresManualDownload);
        Assert.Null(file.ManualDownloadPage);
        Assert.Equal(ApiHandler.DefaultDownloadUrl, ContentInstaller.ToItem(file, "unused").Url);
    }

    [Fact]
    public async Task MissingProjectDoesNotGrantDistributionPermission()
    {
        using var handler = new ApiHandler { IncludeProject = false };
        using var http = new HttpClient(handler);
        var file = await new CurseForgeProvider(http, () => "test-key").GetFileAsync("123", "456");

        Assert.True(file.RequiresManualDownload);
        Assert.Equal("https://www.curseforge.com/projects/123", file.ManualDownloadPage);
    }

    [Fact]
    public async Task AuthorChangingDistributionSettingIsNotHiddenByCache()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var provider = new CurseForgeProvider(http, () => "test-key");
        Assert.False((await provider.GetFileAsync("123", "456")).RequiresManualDownload);

        handler.AllowDistribution = false;

        Assert.True((await provider.GetFileAsync("123", "456")).RequiresManualDownload);
        Assert.Equal(2, handler.ProjectRequests);
    }

    [Fact]
    public async Task RestrictedDependencyPreventsPartialDownloads()
    {
        using var handler = new ApiHandler { AllowDistribution = false };
        using var http = new HttpClient(handler);
        var file = await new CurseForgeProvider(http, () => "test-key").GetFileAsync("123", "456");
        var installer = new ContentInstaller(new Downloader(http, SourceChain.OfficialFirst), new Dictionary<ContentSource, IContentProvider>());
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            var available = file with { ProjectId = "789", FileId = "101", FileName = "root.jar", Url = ApiHandler.DefaultDownloadUrl, ManualDownloadPage = null };
            await Assert.ThrowsAsync<ManualDownloadRequiredException>(() => installer.InstallAsync(new InstallPlan([available, file], []), root));
            Assert.Empty(Directory.EnumerateFiles(root));
            Assert.Equal(0, handler.DownloadRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public async Task MatchingManualDependencyAllowsInstallationToResume(long size)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = ManualFile(size);
            await File.WriteAllTextAsync(Path.Combine(root, file.FileName), "hello");
            var primary = file with { ProjectId = "789", FileId = "101", FileName = "root.jar", Url = ApiHandler.DefaultDownloadUrl, ManualDownloadPage = null };
            await File.WriteAllTextAsync(Path.Combine(root, primary.FileName), "hello");
            using var handler = new ApiHandler();
            using var http = new HttpClient(handler);
            var installer = new ContentInstaller(new Downloader(http, SourceChain.OfficialFirst), new Dictionary<ContentSource, IContentProvider>());

            await installer.InstallAsync(new InstallPlan([primary, file], []), root);

            Assert.Equal(0, handler.DownloadRequests);
            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(root, file.FileName)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("no-hash")]
    [InlineData("invalid-hash")]
    [InlineData("wrong-size")]
    public async Task UnverifiedManualFileStillRequiresManualDownload(string state)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = ManualFile();
            if (state != "missing") await File.WriteAllTextAsync(Path.Combine(root, file.FileName), state == "corrupt" ? "wrong" : "hello");
            if (state == "no-hash") file = file with { Sha1 = null };
            if (state == "invalid-hash") file = file with { Sha1 = new string('z', 40) };
            if (state == "wrong-size") file = file with { Size = 99 };
            await Assert.ThrowsAsync<ManualDownloadRequiredException>(() => ContentInstaller.PrepareDownloadItemsAsync([(file, root)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestrictedUpdatePreservesInstalledFile()
    {
        using var handler = new ApiHandler { AllowDistribution = false };
        using var http = new HttpClient(handler);
        var file = await new CurseForgeProvider(http, () => "test-key").GetFileAsync("123", "456");
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var old = Path.Combine(root, "old.jar");
        await File.WriteAllTextAsync(old, "original");
        try
        {
            await Assert.ThrowsAsync<ManualDownloadRequiredException>(() => ModManager.ApplyUpdateAsync(new ModUpdate(old, file), new Downloader(http, SourceChain.OfficialFirst)));
            Assert.Equal("original", await File.ReadAllTextAsync(old));
            Assert.Equal(0, handler.DownloadRequests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ResourcePageShowsManualActionWithoutStartingInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                var vm = new ContentViewModel(service);
                var file = new ContentFile(ContentSource.CurseForge, "123", "456", "Example", "example.jar", "", null, 0, [], [], "release", default, [])
                {
                    ManualDownloadPage = "https://www.curseforge.com/projects/123",
                };
                vm.SelectedFile = new FileItem(file);
                Assert.True(vm.HasManualDownload);
                Assert.True(vm.SelectedRequiresManualDownload);
                vm.InstallCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Assert.Equal("core.content.curseforge.manual_download", vm.StatusInfo?.Key);
                Assert.False(vm.IsBusy);
                Assert.Empty(Directory.EnumerateFiles(root, "*.jar", SearchOption.AllDirectories));

                vm.SelectedFile = new FileItem(file with { Url = ApiHandler.DefaultDownloadUrl, ManualDownloadPage = null });
                Assert.False(vm.HasManualDownload);
                Assert.False(vm.SelectedRequiresManualDownload);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("source")]
    [InlineData("type")]
    public void NewSearchClearsManualLinkLeftByLocalImport(string trigger)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                using var handler = new ApiHandler();
                using var http = new HttpClient(handler);
                var provider = new CurseForgeProvider(http, () => "test-key");
                var vm = new ContentViewModel(service, _ => provider);
                Assert.Null(vm.SelectedFile);
                vm.ManualDownloadUrl = "https://www.curseforge.com/projects/123";

                if (trigger == "source") vm.SourceIndex = 1;
                else if (trigger == "type") vm.TypeIndex = (int)ContentType.ResourcePack;
                else vm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();

                Assert.False(vm.HasManualDownload);
                Assert.Null(vm.ManualDownloadUrl);
                Assert.Equal(0, handler.DownloadRequests);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstanceUpdateShowsManualDownloadAction(bool updateAll)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-cf-update-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "old.jar");
        File.WriteAllText(path, "old");
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                var vm = new InstanceViewModel(service);
                var item = new ModItem(new LocalMod(path, "old.jar", true, "example", "Example", "1.0", null, null, null))
                {
                    Update = new ModUpdate(path, ManualFile()),
                };
                vm.Mods.Add(item);
                if (updateAll) vm.UpdateAllModsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                else vm.UpdateModCommand.ExecuteAsync(item).GetAwaiter().GetResult();
                Assert.True(vm.HasManualDownload);
                Assert.Equal(ManualFile().ManualDownloadPage, vm.ManualDownloadUrl);
                Assert.Equal("core.content.curseforge.manual_download", vm.StatusInfo?.Key);
                Assert.Equal("old", File.ReadAllText(path));
                Assert.False(vm.IsBusy);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    private static ContentFile ManualFile(long size = 5) => new(ContentSource.CurseForge, "123", "456", "Example", "example.jar", "",
        "AAF4C61DDCC5E8A2DABEDE0F3B482CD9AEA9434D", size, [], [], "release", default, [])
    {
        ManualDownloadPage = "https://www.curseforge.com/projects/123",
    };

    private static async Task<ContentFile> ReadFile(CurseForgeProvider provider, string entry) => entry switch
    {
        "file" => await provider.GetFileAsync("123", "456"),
        "list" => Assert.Single(await provider.GetFilesAsync("123", null, null)),
        "batch" => Assert.Single(await provider.GetFilesByIdAsync([456])),
        "fingerprint" => Assert.Single(await provider.IdentifyByFingerprintAsync([789])).Value,
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };

    private sealed class ApiHandler : HttpMessageHandler
    {
        public const string DefaultDownloadUrl = "https://edge.forgecdn.net/files/0/456/example.jar";
        public bool? AllowDistribution { get; set; } = true;
        public bool IncludeProject { get; set; } = true;
        public string? DownloadUrl { get; set; } = DefaultDownloadUrl;
        public int ProjectRequests { get; private set; }
        public int DownloadRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var file = new JsonObject
            {
                ["id"] = 456, ["modId"] = 123, ["fileName"] = "example.jar", ["downloadUrl"] = DownloadUrl,
                ["fileFingerprint"] = 789, ["gameVersions"] = new JsonArray("1.21.1"), ["dependencies"] = new JsonArray(),
            };
            JsonNode data;
            if (path == "/v1/mods")
            {
                ProjectRequests++;
                var projects = new JsonArray();
                if (IncludeProject) projects.Add(new JsonObject
                {
                    ["id"] = 123, ["name"] = "Example", ["allowModDistribution"] = AllowDistribution,
                    ["links"] = new JsonObject { ["websiteUrl"] = "https://www.curseforge.com/minecraft/mc-mods/example" },
                });
                data = projects;
            }
            else if (path == "/v1/mods/search") data = new JsonArray();
            else if (path == "/v1/mods/123/files/456") data = file;
            else if (path is "/v1/mods/123/files" or "/v1/mods/files") data = new JsonArray(file);
            else if (path == "/v1/fingerprints") data = new JsonObject { ["exactMatches"] = new JsonArray(new JsonObject { ["file"] = file }) };
            else
            {
                DownloadRequests++;
                throw new InvalidOperationException("不应发起文件下载：" + request.RequestUri);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
