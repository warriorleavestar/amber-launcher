using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Localization;
using Amber.Core.Net;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>从真实界面命令验证安装标记与独占目录，避免裸 installer 测试漏掉调用方提前建目录。</summary>
public sealed class GuiLoaderOwnershipMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-gui-install-" + Guid.NewGuid().ToString("N"));

    public enum Outcome { Success, Failure, Cancelled, Existing }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var onboarding in new[] { false, true })
        foreach (var (kind, mc) in new[] { (LoaderKind.Forge, "1.20.1"), (LoaderKind.NeoForge, "1.21.1"), (LoaderKind.Forge, "1.5.2") })
        foreach (var outcome in Enum.GetValues<Outcome>())
            yield return [onboarding, kind, mc, outcome];
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void GuiInstallationOwnsItsMarkerAndOnlyItsNewDirectory(bool onboarding, LoaderKind kind, string mc, Outcome outcome)
    {
        UiTestThread.Run(() =>
        {
            var version = new LoaderVersion(kind, mc, kind == LoaderKind.NeoForge ? "21.1.1" : mc == "1.5.2" ? "1.5.2-7.8.1.738" : "1.20.1-47.2.0");
            var id = onboarding ? version.DefaultId : "CustomForge";
            Directory.CreateDirectory(_root);
            using var service = new LauncherService(new AppSettings
            {
                GameDirectory = Path.Combine(_root, "game"),
                SavePathForTest = Path.Combine(_root, "settings.json"),
                PlayerName = "", // 不迁移或创建测试机器上的账号。
                AutoJavaDownload = false,
            });
            var dir = service.Directory;
            var target = dir.VersionDir(id);
            var marker = Path.Combine(target, GameDirectory.IncompleteMarker);
            var baseBytes = BaseVersion(dir, mc);
            var sentinel = Path.Combine(_root, "unrelated.txt");
            File.WriteAllText(sentinel, "保留其他数据");
            if (outcome == Outcome.Existing)
            {
                Directory.CreateDirectory(Path.Combine(target, "saves"));
                File.WriteAllText(Path.Combine(target, "saves", "keep.dat"), "原有存档");
                File.WriteAllText(dir.VersionJsonPath(id), $$"""{"id":"{{id}}","inheritsFrom":"{{mc}}","mainClass":"Example","libraries":[]}""");
            }
            var existing = outcome == Outcome.Existing
                ? Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes)
                : null;

            var sawStartedMarker = false;
            using var handler = new InstallHandler(version, outcome, () =>
            {
                // 在真正请求 Forge 安装包时，目录已由安装器保留，界面标记也已经写入。
                sawStartedMarker = File.Exists(marker);
                Assert.True(sawStartedMarker);
                Assert.Equal(InstanceState.Incomplete, dir.StateOf(id));
            });
            using var http = new HttpClient(handler);
            InstallDependencies(service, http, cachedJava: true);

            StatusMessage? status;
            if (onboarding)
            {
                var vm = new OnboardingViewModel(service);
                handler.Cancel = () => vm.CancelInstallCommand.Execute(null);
                Pump(vm.InstallAsync(mc, kind, fallbackToVanilla: false));
                Assert.False(vm.IsInstalling);
                status = vm.Message;
            }
            else
            {
                var vm = new DownloadViewModel(service) { IsCurrent = true };
                // 在设置 Target 前选 loader，避免准备测试时另发版本列表请求。
                vm.SelectedLoader = vm.LoaderChoices.Single(c => c.Kind == kind);
                vm.Target = new RemoteVersionItem(new ManifestEntry { Id = mc, Type = "release" }, installed: true);
                vm.SelectedLoaderVersion = version;
                vm.CustomName = id;
                handler.Cancel = () => vm.CancelInstallCommand.Execute(null);
                Pump(vm.InstallCommand.ExecuteAsync(null));
                Assert.False(vm.IsBusy);
                Assert.Equal(outcome == Outcome.Success ? id : null, vm.JustInstalled);
                status = vm.StatusInfo;
            }

            Assert.Equal(baseBytes, File.ReadAllBytes(dir.VersionJsonPath(mc)));
            Assert.Equal("保留其他数据", File.ReadAllText(sentinel));
            Assert.False(File.Exists(marker));
            if (outcome == Outcome.Existing)
            {
                Assert.False(sawStartedMarker);
                Assert.Equal(0, handler.ArchiveRequests);
                Assert.Equal(existing!.Keys.Order(), Directory.GetFiles(target, "*", SearchOption.AllDirectories).Order());
                foreach (var (path, bytes) in existing) Assert.Equal(bytes, File.ReadAllBytes(path));
                Assert.Equal(InstanceState.Ready, dir.StateOf(id));
                Assert.Equal(StatusKind.Error, status?.Kind);
            }
            else
            {
                Assert.True(sawStartedMarker);
                Assert.True(handler.ArchiveRequests > 0);
                if (outcome == Outcome.Success)
                {
                    Assert.Equal(id, service.CurrentInstance);
                    Assert.Equal(InstanceState.Ready, dir.StateOf(id));
                }
                else
                {
                    Assert.False(Directory.Exists(target));
                    Assert.NotEqual(id, service.CurrentInstance);
                    Assert.Equal(outcome == Outcome.Cancelled ? "download.status.cancelled" : "common.install_failed", status?.Key);
                }
            }
        });
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failure")]
    [InlineData("switch-source")]
    [InlineData("switch-source-after-java-warning")]
    [InlineData("different-loader")]
    [InlineData("different-root")]
    public void OnboardingResumesOnlyItsOwnPreparedGameAfterJavaInterruption(string mode)
    {
        UiTestThread.Run(() =>
        {
            const string mc = "1.20.1";
            var version = new LoaderVersion(LoaderKind.Forge, mc, "1.20.1-47.2.0");
            Directory.CreateDirectory(_root);
            using var service = new LauncherService(new AppSettings
            {
                GameDirectory = Path.Combine(_root, "game"),
                SavePathForTest = Path.Combine(_root, "settings.json"),
                PlayerName = "",
                AutoJavaDownload = true,
            });
            BaseVersion(service.Directory, mc);
            var originalTarget = service.Directory.VersionDir(version.DefaultId);
            var originalMarker = Path.Combine(originalTarget, GameDirectory.IncompleteMarker);
            var expectedId = version.DefaultId;
            using var handler = new InstallHandler(version, Outcome.Success, () =>
                Assert.True(File.Exists(Path.Combine(service.Directory.VersionDir(expectedId), GameDirectory.IncompleteMarker))));
            using var http = new HttpClient(handler);
            var warningSwitch = mode == "switch-source-after-java-warning";
            var switchSource = mode == "switch-source" || warningSwitch;
            InstallDependencies(service, http, cachedJava: false, downloadJava: !warningSwitch);
            var vm = new OnboardingViewModel(service);
            var warningSwitches = 0;
            vm.PropertyChanged += (_, args) =>
            {
                // Java 缺失被向导容忍为警告，随后正常返回；取消必须由游戏阶段完成边界再次检查。
                if (warningSwitch && warningSwitches == 0 && args.PropertyName == nameof(vm.Message) && vm.Message is { Kind: StatusKind.Warning })
                {
                    warningSwitches++;
                    vm.SwitchSourceCommand.Execute(null);
                }
            };
            var reloads = 0;
            service.SettingsChangedElsewhere += () =>
            {
                reloads++;
                InstallDependencies(service, http, cachedJava: true);
            };
            var javaRequests = 0;
            handler.JavaRequest = cancellationToken =>
            {
                javaRequests++;
                Assert.Equal(1, handler.ArchiveRequests);
                Assert.True(File.Exists(originalMarker));
                Assert.True(File.Exists(service.Directory.VersionJsonPath(version.DefaultId)));
                if (mode == "failure") return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (mode == "switch-source") vm.SwitchSourceCommand.Execute(null);
                else vm.CancelInstallCommand.Execute(null);
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Java 阶段未被取消");
            };

            Pump(vm.InstallAsync(mc, LoaderKind.Forge, fallbackToVanilla: false));
            if (warningSwitch)
            {
                Assert.Equal(0, javaRequests);
                Assert.Equal(1, warningSwitches);
            }
            else Assert.True(javaRequests > 0);
            Assert.Equal(1, handler.ArchiveRequests);
            if (!switchSource)
            {
                Assert.True(File.Exists(originalMarker));
                Assert.Equal(mode == "failure" ? "common.install_failed" : "download.status.cancelled", vm.Message?.Key);
                if (mode == "different-root")
                {
                    service.Settings.GameDirectory = Path.Combine(_root, "another-game");
                    service.Reload();
                    BaseVersion(service.Directory, mc);
                }
                var nextKind = mode == "different-loader" ? LoaderKind.NeoForge : LoaderKind.Forge;
                if (mode == "different-loader") expectedId = new LoaderVersion(nextKind, mc, version.Version).DefaultId;
                InstallDependencies(service, http, cachedJava: true);
                Pump(vm.InstallAsync(mc, nextKind, fallbackToVanilla: false));
            }

            Assert.Equal(mode is "different-root" or "different-loader" ? 2 : 1, handler.ArchiveRequests);
            Assert.Equal(switchSource ? 1 : 0, reloads);
            Assert.Equal(expectedId, service.CurrentInstance);
            Assert.Equal(StatusKind.Success, vm.Message?.Kind);
            Assert.Equal(InstanceState.Ready, service.Directory.StateOf(expectedId));
            Assert.False(File.Exists(Path.Combine(service.Directory.VersionDir(expectedId), GameDirectory.IncompleteMarker)));
            if (mode is "different-root" or "different-loader")
                Assert.True(File.Exists(originalMarker)); // 切换请求不接管或清理上一次已完成的游戏目录。
        });
    }

    private void InstallDependencies(LauncherService service, HttpClient http, bool cachedJava, bool downloadJava = true)
    {
        var dir = service.Directory;
        var downloader = new Downloader(http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        var vanilla = new VanillaInstaller(downloader, dir);
        var loaders = new LoaderContext(downloader, dir, vanilla, (_, _) => throw new InvalidOperationException("测试不应执行 Java"));
        var java = new JavaManager(downloader, dir) { AutoDownload = !cachedJava && downloadJava };
        // 新手流程仍走真实 JavaManager，但不扫描或启动测试机器上的 Java。
        var cache = new List<JavaInstall>();
        if (cachedJava) cache.Add(new JavaInstall(Path.Combine(_root, "unused-java"), "1.8.0", 8, true));
        typeof(JavaManager).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(java, cache);
        // 服务的依赖都在 Context 里：整份换成测试用的下载器、安装器、加载器和 Java。
        SetContext(service, service.Context with { Downloader = downloader, Installer = vanilla, Loaders = loaders, Java = java });
    }

    private static void SetContext(LauncherService service, GameContext context) =>
        typeof(LauncherService).GetProperty(nameof(LauncherService.Context))!.SetValue(service, context);

    private static byte[] BaseVersion(GameDirectory dir, string mc)
    {
        Directory.CreateDirectory(dir.VersionDir(mc));
        File.WriteAllText(dir.VersionJsonPath(mc), $$"""{"id":"{{mc}}","mainClass":"Example","libraries":[]}""");
        using (var zip = ZipFile.Open(dir.VersionJarPath(mc), ZipArchiveMode.Create)) Write(zip, "base.txt", "base");
        return File.ReadAllBytes(dir.VersionJsonPath(mc));
    }

    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "界面安装流程未完成");
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }

    private sealed class InstallHandler(LoaderVersion version, Outcome outcome, Action beforeArchive) : HttpMessageHandler
    {
        public int ArchiveRequests { get; private set; }
        public Action? Cancel { get; set; }
        public Func<CancellationToken, HttpResponseMessage>? JavaRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/java-runtime/", StringComparison.Ordinal))
                return Task.FromResult(JavaRequest!(cancellationToken));
            if (path.EndsWith("maven-metadata.json", StringComparison.Ordinal))
                return Text($$"""{"{{version.McVersion}}":["{{version.Version}}"]}""");
            if (path.EndsWith("promotions_slim.json", StringComparison.Ordinal)) return Text("""{"promos":{}}""");
            if (path.StartsWith("/api/maven/versions/", StringComparison.Ordinal))
                return Text($$"""{"versions":["{{version.Version}}"]}""");
            if (!path.EndsWith(".jar", StringComparison.Ordinal) && !path.EndsWith(".zip", StringComparison.Ordinal))
                throw new InvalidOperationException("未安排的测试请求：" + path);

            ArchiveRequests++;
            beforeArchive();
            if (outcome == Outcome.Cancelled)
            {
                Cancel!();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("取消命令没有取消实际下载");
            }
            if (outcome == Outcome.Failure) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (LegacyForge.IsJarModEra(version.McVersion)) Write(zip, "forge.txt", "forge");
                else
                {
                    Write(zip, "install_profile.json", """{"json":"/version.json","processors":[]}""");
                    Write(zip, "version.json", """{"mainClass":"Example","libraries":[]}""");
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }

        private static Task<HttpResponseMessage> Text(string text) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
    }
}
