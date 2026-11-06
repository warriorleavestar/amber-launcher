using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>真实 VM 导入入口使用本机 HTTP，区分包内缺文件、归档下载和普通资源补文件。</summary>
public sealed class CurseForgeManualHintTests
{
    [Theory]
    [InlineData("CurseForge", false)]
    [InlineData("CurseForge", true)]
    [InlineData("MCBBS", false)]
    [InlineData("MCBBS", true)]
    public void RestrictedPackDependencyExplainsHowToFixTheArchiveAfterRollback(string format, bool online) => Run((fixture, vm) =>
    {
        fixture.Import(vm, fixture.Pack(format), online);

        Assert.Equal("content.curseforge.pack_import_incomplete", vm.StatusInfo?.Key);
        Assert.Equal(L.T("content.curseforge.manual_pack_import_hint"), vm.ManualDownloadHint);
        Assert.True(vm.HasManualDownload);
        Assert.False(vm.IsBusy);
        Assert.False(Directory.Exists(fixture.Service.Directory.VersionDir("HintPack")));
        Assert.True(File.Exists(fixture.Service.Directory.VersionJsonPath("1.21.1")));
        Assert.Null(fixture.Service.CurrentInstance);

        // 本地导入不依赖资源页当前分类；之后选普通资源不能继续显示整合包修复提示。
        vm.TypeIndex = (int)ContentType.Mod;
        vm.SelectedFile = new FileItem(RestrictedFile("example.jar"));
        Pump(vm.InstallCommand.ExecuteAsync(null));
        Assert.Equal("core.content.curseforge.manual_download", vm.StatusInfo?.Key);
        Assert.Equal(L.T("content.curseforge.manual_hint"), vm.ManualDownloadHint);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherImportFailuresDoNotClaimARestrictedDependencyOrShowItsHint(bool online) => Run((fixture, vm) =>
    {
        var path = Path.Combine(fixture.Root, "invalid.zip");
        File.WriteAllText(path, "not an archive");
        fixture.Import(vm, path, online);

        Assert.Equal(online ? "common.install_failed" : "instance.worlds.import_failed", vm.StatusInfo?.Key);
        Assert.False(vm.HasManualDownload);
        Assert.NotEqual(L.T("content.curseforge.manual_pack_import_hint"), vm.ManualDownloadHint);
        Assert.False(Directory.Exists(fixture.Service.Directory.VersionDir("HintPack")));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingTargetIsPreservedAndDoesNotShowRollbackInstructions(bool online) => Run((fixture, vm) =>
    {
        var target = fixture.Service.Directory.VersionDir("HintPack");
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "keep.txt");
        File.WriteAllText(existing, "keep");

        fixture.Import(vm, fixture.Pack("CurseForge"), online);

        Assert.Equal(online ? "common.install_failed" : "instance.worlds.import_failed", vm.StatusInfo?.Key);
        Assert.False(vm.HasManualDownload);
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.NotEqual(L.T("content.curseforge.manual_pack_import_hint"), vm.ManualDownloadHint);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestrictedArchiveAndResourceHaveDifferentNextStepsBeforeAnyImport(bool modpack) => Run((fixture, vm) =>
    {
        vm.TypeIndex = (int)(modpack ? ContentType.Modpack : ContentType.Mod);
        vm.SelectedFile = new FileItem(RestrictedFile(modpack ? "pack.zip" : "example.jar"));
        Pump(vm.InstallCommand.ExecuteAsync(null));

        Assert.Equal("core.content.curseforge.manual_download", vm.StatusInfo?.Key);
        Assert.Equal(L.T(modpack ? "content.curseforge.manual_pack_archive_hint" : "content.curseforge.manual_hint"), vm.ManualDownloadHint);
        Assert.True(vm.HasManualDownload);
        Assert.False(Directory.Exists(fixture.Service.Directory.VersionDir("HintPack")));

        vm.SelectedFile = null;
        Assert.False(vm.HasManualDownload);
        Assert.Equal(L.T("content.curseforge.manual_hint"), vm.ManualDownloadHint);
    });

    private static ContentFile RestrictedFile(string name) =>
        new(ContentSource.CurseForge, "123", "456", "Example", name, "", null, 0, [], [], "release", default, [])
        {
            ManualDownloadPage = "https://www.curseforge.com/projects/123",
        };

    private static void Run(Action<Fixture, ContentViewModel> test) => UiTestThread.Run(() =>
    {
        using var fixture = new Fixture();
        var vm = new ContentViewModel(fixture.Service, _ => new EmptyProvider());
        test(fixture, vm);
    });

    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "本机整合包提示测试未完成");
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "amber-cf-manual-hint-" + Guid.NewGuid().ToString("N"));
        private readonly TestServer _server = new();
        public LauncherService Service { get; }

        public Fixture()
        {
            Service = new LauncherService(new AppSettings { GameDirectory = Path.Combine(Root, "game"), PlayerName = "", CurseForgeApiRoot = _server.Root + "v1" });
            Directory.CreateDirectory(Service.Directory.VersionDir("1.21.1"));
            File.WriteAllText(Service.Directory.VersionJsonPath("1.21.1"), """{"id":"1.21.1","mainClass":"Example","libraries":[]}""");
            _server.Add("v1/mods/files", Encoding.UTF8.GetBytes("""
                {"data":[{"id":456,"modId":123,"fileName":"example.jar","downloadUrl":null,"fileLength":5,
                "hashes":[{"algo":1,"value":"aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d"}],"gameVersions":["1.21.1"],"dependencies":[]}]}
                """));
            _server.Add("v1/mods", Encoding.UTF8.GetBytes("""
                {"data":[{"id":123,"name":"Example","classId":6,"allowModDistribution":false,
                "links":{"websiteUrl":"https://www.curseforge.com/minecraft/mc-mods/example"}}]}
                """));
        }

        public string Pack(string format)
        {
            var path = Path.Combine(Root, "pack.zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry(format == "CurseForge" ? "manifest.json" : "mcbbs.packmeta").Open(), new UTF8Encoding(false));
            writer.Write(format == "CurseForge"
                ? """{"manifestType":"minecraftModpack","name":"HintPack","minecraft":{"version":"1.21.1"},"files":[{"projectID":123,"fileID":456,"required":true}],"overrides":"custom-overrides"}"""
                : """{"name":"HintPack","addons":[{"id":"game","version":"1.21.1"}],"files":[{"type":"curse","projectID":123,"fileID":456}]}""");
            // 刻意不附带受限文件：必须走导入失败和新目标回滚。
            return path;
        }

        public void Import(ContentViewModel vm, string path, bool online)
        {
            if (!online)
            {
                Pump(vm.ImportLocalPackAsync(path));
                return;
            }
            var bytes = File.ReadAllBytes(path);
            var url = _server.Add("download/pack.zip", bytes);
            vm.TypeIndex = (int)ContentType.Modpack;
            vm.NewInstanceName = "HintPack";
            vm.SelectedFile = new FileItem(new ContentFile(ContentSource.Modrinth, "pack", "pack-file", "HintPack", "pack.zip", url, null, bytes.Length,
                ["1.21.1"], [], "release", default, []));
            Pump(vm.InstallCommand.ExecuteAsync(null));
        }

        public void Dispose()
        {
            Service.Dispose();
            _server.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class EmptyProvider : IContentProvider
    {
        public ContentSource Source => ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => Task.FromResult(new SearchResult([], 0));
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentFile>>([]);
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
