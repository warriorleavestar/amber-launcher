using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Game;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 开发用端到端自检：设置环境变量 AMBER_SELFTEST=&lt;报告目录&gt; 后，在真实窗口里驱动各页面的命令，
/// 走完“装加载器 → 装模组 → 改设置 → 启动 → 崩溃分析 → 导出 → 导入”，把结果和截图写进报告目录，然后退出。
/// </summary>
internal sealed class SelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private const string FabricName = "E2E-Fabric";
    private const string PackName = "E2E-Pack";
    private readonly List<string> _report = [];
    private int _shot;

    private T Page<T>() where T : PageViewModel => main.Pages.OfType<T>().Single();

    private void Log(string line)
    {
        _report.Add(line);
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
    }

    private void Check(string name, bool ok, string detail = "") =>
        Log($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "：" + detail : "")}");

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    private async Task ShotAsync(string name)
    {
        await Task.Delay(400);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(reportDir, $"{++_shot:D2}-{name}.png"));
        });
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        try
        {
            await InstallLoaderAsync();
            await InstallModAsync();
            await InstanceAsync();
            await LaunchAsync();
            await CrashAsync();
            await ServersAndWorldsAsync();
            await ToolsAsync();
            await ExportImportAsync();
            Log("DONE");
        }
        catch (Exception e)
        {
            Log("FAIL 自检中断：" + e);
        }
        window.Close();
    }

    /// <summary>在当前窗口的控件树里找可见的控件（界面刚切换时要等布局完成）。</summary>
    private async Task<T?> FindAsync<T>(Func<T, bool> match, int seconds = 10) where T : Control
    {
        T? found = null;
        await WaitAsync(() =>
        {
            found = Dispatcher.UIThread.Invoke(() => window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && match(c)));
            return found is not null;
        }, seconds);
        return found;
    }

    /// <summary>像读屏软件一样“按下”按钮：走自动化接口，按钮禁用或不可见时按不动。</summary>
    private static bool Press(Button? button) => Dispatcher.UIThread.Invoke(() =>
    {
        if (button is not { IsEffectivelyEnabled: true, IsEffectivelyVisible: true }) return false;
        if (ControlAutomationPeer.CreatePeerForElement(button).GetProvider<IInvokeProvider>() is not { } invoke) return false;
        invoke.Invoke();
        return true;
    });

    /// <summary>下载页全程操作控件：搜索框输入、点“安装…”、在下拉框选 Fabric、改名字、点“开始安装”。</summary>
    private async Task InstallLoaderAsync()
    {
        var download = Page<DownloadViewModel>();
        main.CurrentPage = download;
        await WaitAsync(() => download.Items.Count > 0, 30);
        Check("下载页加载版本列表", download.Items.Count > 0, $"{download.Items.Count} 个");

        var search = await FindAsync<TextBox>(t => t.PlaceholderText?.StartsWith("搜索版本号", StringComparison.Ordinal) == true);
        Dispatcher.UIThread.Invoke(() => search!.Text = "1.21.1");
        var row = await FindAsync<Button>(b => b.Content as string == "安装…" && b.DataContext is RemoteVersionItem { Id: "1.21.1" });
        Check("搜索框过滤出 1.21.1 并显示安装按钮", row is not null && download.Items.All(i => i.Id.Contains("1.21.1")), $"{download.Items.Count} 个");
        Check("点“安装…”打开安装面板", Press(row) && await WaitAsync(() => download.IsConfiguring && download.Target?.Id == "1.21.1", 5));

        var loaderBox = await FindAsync<ComboBox>(c => ReferenceEquals(c.ItemsSource, download.LoaderChoices));
        Dispatcher.UIThread.Invoke(() => loaderBox!.SelectedItem = download.LoaderChoices.First(c => c.Label == "Fabric"));
        var versionBox = await FindAsync<ComboBox>(c => ReferenceEquals(c.ItemsSource, download.LoaderVersions));
        var gotVersion = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => versionBox?.SelectedItem is not null), 30);
        Check("下拉框选 Fabric 后列出加载器版本", gotVersion && download.SelectedLoader.Label == "Fabric", download.SelectedLoaderVersion?.Version ?? download.LoaderHint);

        var nameBox = await FindAsync<TextBox>(t => t.Text == download.CustomName && download.CustomName.StartsWith("fabric-loader-", StringComparison.Ordinal));
        Check("默认名字按加载器版本生成", nameBox is not null, download.CustomName);
        Dispatcher.UIThread.Invoke(() => nameBox!.Text = FabricName);
        Check("名字输入框写回", download.CustomName == FabricName, download.CustomName);
        await ShotAsync("安装面板");

        var install = await FindAsync<Button>(b => b.Content as string == "开始安装");
        Check("点“开始安装”", Press(install));
        await WaitAsync(() => download.IsBusy, 5);
        Check("安装中按钮变灰", !download.IsBusy || Dispatcher.UIThread.Invoke(() => !install!.IsEffectivelyEnabled));
        await WaitAsync(() => !download.IsBusy, 600);
        Check("安装 Fabric（自定义名字）", download.Status.Contains("安装完成") && !download.IsConfiguring
                                         && File.Exists(Path.Combine(GameRoot(), "versions", FabricName, FabricName + ".json")), download.Status);
        Dispatcher.UIThread.Invoke(() => search!.Text = "");
    }

    private async Task InstallModAsync()
    {
        var content = Page<ContentViewModel>();
        main.CurrentPage = content;
        await WaitAsync(() => content.Instances.Contains(FabricName), 5);
        content.TargetInstance = FabricName;
        content.Query = "sodium";
        await content.SearchCommand.ExecuteAsync(null);
        Check("Modrinth 搜索（按实例过滤）", content.Results.Count > 0, content.Status);
        content.Selected = content.Results.First(r => r.Title == "Sodium");
        await WaitAsync(() => content.SelectedFile is not null, 20);
        await ShotAsync("资源页");
        await content.InstallCommand.ExecuteAsync(null);
        var instance = Path.Combine(GameRoot(), "versions", FabricName, "mods");
        Check("安装模组到实例", Directory.Exists(instance) && Directory.EnumerateFiles(instance, "sodium*").Any(), content.Status);

        content.SourceIndex = 1;
        await WaitAsync(() => content.Status.Contains("失败") || content.Results.Count > 0, 20);
        Check("没配 Key 时 CurseForge 给出明确提示", content.Status.Contains("API Key") || content.Results.Count > 0, content.Status);
        content.SourceIndex = 0;
    }

    private string GameRoot() => Page<SettingsViewModel>().GameDirectory;

    private async Task InstanceAsync()
    {
        var instance = Page<InstanceViewModel>();
        main.CurrentPage = instance;
        await WaitAsync(() => instance.Instances.Contains(FabricName), 5);
        instance.SelectedId = FabricName;
        instance.TabIndex = 1;
        await WaitAsync(() => instance.Mods.Count > 0, 10);
        var mod = instance.Mods.FirstOrDefault();
        Check("模组列表读到元数据", mod is { Name: "Sodium" }, mod?.Meta ?? "");
        if (mod is not null)
        {
            mod.Enabled = false;
            Check("禁用模组", mod.Mod.Path.EndsWith(".disabled"), mod.Mod.FileName);
            mod.Enabled = true;
            Check("启用模组", !mod.Mod.Path.EndsWith(".disabled"), mod.Mod.FileName);
        }
        await instance.CheckModUpdatesCommand.ExecuteAsync(null);
        Check("检查模组更新", instance.Status.Contains("最新") || instance.Status.Contains("可更新"), instance.Status);
        await ShotAsync("模组页");

        instance.TabIndex = 0;
        await Task.Delay(300);
        instance.MemoryModeIndex = 2;
        instance.MemoryText = "3072";
        instance.JvmArgs = "-Damber.selftest=1";
        instance.WidthText = "1000";
        instance.HeightText = "600";
        var marker = Path.Combine(reportDir, "prelaunch.txt");
        instance.PreLaunch = $"echo %AMBER_VERSION%> \"{marker}\"";
        instance.SaveSettingsCommand.Execute(null);
        await ShotAsync("实例设置");
    }

    private async Task LaunchAsync()
    {
        var home = Page<HomeViewModel>();
        var log = Page<LogViewModel>();
        main.CurrentPage = home;
        await WaitAsync(() => home.Versions.Contains(FabricName), 5);
        home.SelectedVersion = FabricName;
        var launching = home.LaunchCommand.ExecuteAsync(null);
        var reached = await WaitAsync(() => log.Lines.Any(l => l.Message.Contains("Sound engine started") || l.Message.Contains("OpenAL initialized")), 180);
        Check("启动游戏到主界面", reached, home.Status);
        Check("启动前命令运行过", File.Exists(Path.Combine(reportDir, "prelaunch.txt")) && File.ReadAllText(Path.Combine(reportDir, "prelaunch.txt")).Contains(FabricName));
        Check("实例内存生效", log.Lines.Any(l => l.Message.Contains("最大内存 3072 MB")));
        await ShotAsync("运行中");
        main.CurrentPage = log;
        await ShotAsync("日志页");
        main.CurrentPage = home;
        home.CancelCommand.Execute(null);
        await launching;
        Check("结束游戏后状态正确（不报崩溃）", home.Status.Contains("已结束") && !home.HasCrash, home.Status);
    }

    private async Task CrashAsync()
    {
        var instance = Page<InstanceViewModel>();
        main.CurrentPage = instance;
        instance.TabIndex = 0;
        await Task.Delay(300);
        instance.JvmArgs = "-XX:AmberBadOption=1";
        instance.SaveSettingsCommand.Execute(null);

        var home = Page<HomeViewModel>();
        main.CurrentPage = home;
        await home.LaunchCommand.ExecuteAsync(null);
        var found = home.Crash?.Findings.Any(f => f.Title.Contains("JVM 参数")) == true;
        Check("崩溃分析识别出坏的 JVM 参数", found, home.Crash?.Summary ?? home.Status);
        await ShotAsync("崩溃分析");

        var bundle = Path.Combine(reportDir, "error.zip");
        home.ExportCrashBundle(bundle);
        using (var zip = ZipFile.OpenRead(bundle))
            Check("导出错误报告", zip.Entries.Any(e => e.Name == "launcher-output.txt"), string.Join(", ", zip.Entries.Select(e => e.Name)));

        main.CurrentPage = instance;
        instance.JvmArgs = "";
        instance.SaveSettingsCommand.Execute(null);
    }

    private async Task ServersAndWorldsAsync()
    {
        var instance = Page<InstanceViewModel>();
        instance.SelectedId = FabricName;
        instance.TabIndex = 6;
        await Task.Delay(300);
        instance.NewServerName = "自检服务器";
        instance.NewServerAddress = "127.0.0.1:1";
        instance.AddServerCommand.Execute(null);
        await WaitAsync(() => instance.Servers.Any(s => !s.StatusText.Contains("查询")), 10);
        Check("添加服务器并查询状态", instance.Servers.Any(s => s.StatusText.Contains("离线")), instance.Servers.FirstOrDefault()?.StatusText ?? "");
        await ShotAsync("服务器页");
        instance.RemoveServerCommand.Execute(instance.Servers.First());
        Check("移除服务器", instance.Servers.Count == 0);

        // 启动时游戏只在进入世界时才建存档，这里放一个假的来验证世界页。
        var world = Path.Combine(GameRoot(), "versions", FabricName, "saves", "selftest-world");
        Directory.CreateDirectory(world);
        Nbt.WriteFile(Path.Combine(world, "level.dat"), new Dictionary<string, object>
        {
            ["Data"] = new Dictionary<string, object> { ["LevelName"] = "自检世界", ["GameType"] = 0, ["LastPlayed"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        });
        instance.SelectedId = null;
        instance.SelectedId = FabricName;
        instance.TabIndex = 2;
        await WaitAsync(() => instance.Worlds.Count > 0, 5);
        Check("世界列表", instance.Worlds.Any(w => w.Name == "自检世界"));
        await instance.BackupWorldCommand.ExecuteAsync(instance.Worlds.First());
        Check("备份世界", instance.Status.Contains("备份完成"), instance.Status);
        await ShotAsync("世界页");
        await DatapacksAsync(instance, world);
        instance.DeleteWorldCommand.Execute(instance.Worlds.First());
        Check("删除世界进回收站", !Directory.Exists(world), instance.Status);

        for (var tab = 3; tab <= 7; tab++)
        {
            instance.TabIndex = tab;
            await ShotAsync($"实例标签{tab}");
        }
    }

    private async Task DatapacksAsync(InstanceViewModel instance, string world)
    {
        main.CurrentPage = instance;
        instance.TabIndex = 2;
        await WaitAsync(() => instance.Worlds.Count > 0, 5);
        var button = await FindAsync<Button>(b => b.Content as string == "数据包" && b.DataContext is WorldItem w && w.World.Path == world);
        Check("点世界的“数据包”", Press(button) && instance.HasDatapackWorld);

        var zip = Path.Combine(reportDir, "selftest-datapack.zip");
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open())) w.Write("""{"pack":{"pack_format":48,"description":"自检数据包"}}""");
            using (var w = new StreamWriter(archive.CreateEntry("data/selftest/function/hi.mcfunction").Open())) w.Write("say hi");
        }
        instance.AddDatapacks([zip]);
        var item = instance.Datapacks.FirstOrDefault(d => d.Name == "selftest-datapack.zip");
        Check("添加数据包", item is { Enabled: true } && item.Meta.Contains("自检数据包"), instance.Status);
        if (item is not null)
        {
            item.Enabled = false;
            var disabled = DatapackManager.List(world).Single(d => d.FileName == "selftest-datapack.zip");
            Check("禁用数据包写进 level.dat", !disabled.Enabled && File.Exists(Path.Combine(world, "level.dat_old")), instance.Status);
            item.Enabled = true;
        }
        await ShotAsync("数据包");

        // “下载数据包…”跳到资源页，类型、版本、世界都选好。
        Check("点“下载数据包…”", Press(await FindAsync<Button>(b => b.Content as string == "下载数据包…")));
        var content = Page<ContentViewModel>();
        Check("跳到资源页的数据包搜索", main.CurrentPage == content && content.IsDatapack && content.TargetInstance == FabricName
                                    && content.TargetWorld?.World.Path == world, $"{content.TargetInstance} / {content.TargetWorld?.Label}");
        await WaitAsync(() => content.Results.Count > 0 || content.Status.Contains("失败"), 30);
        Check("Modrinth 数据包搜索", content.Results.Count > 0, content.Status);
        var installed = false;
        foreach (var project in content.Results.Take(6).ToList())
        {
            content.Selected = project;
            if (!await WaitAsync(() => content.SelectedFile is not null, 15)) continue;
            await content.InstallCommand.ExecuteAsync(null);
            installed = content.Status.StartsWith("已把", StringComparison.Ordinal);
            if (installed) break;
        }
        var dir = DatapackManager.DirectoryOf(world);
        Check("下载数据包进世界", installed && Directory.EnumerateFiles(dir, "*.zip").Count() >= 2, content.Status);
        await ShotAsync("资源页数据包");
        content.TypeIndex = 0;
        main.CurrentPage = instance;
        instance.SelectedId = null;
        instance.SelectedId = FabricName;
        instance.TabIndex = 2;
        await WaitAsync(() => instance.Worlds.Count > 0, 5);
    }

    private async Task ToolsAsync()
    {
        var tools = Page<ToolsViewModel>();
        main.CurrentPage = tools;
        await tools.ScanJunkCommand.ExecuteAsync(null);
        Check("扫描垃圾", tools.Junk.Count == 4, tools.JunkStatus);
        await tools.CheckUpdateCommand.ExecuteAsync(null);
        Check("未配置更新地址时不联网", tools.UpdateStatus.Contains("不会联网"), tools.UpdateStatus);
        tools.DownloadUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        tools.DownloadFolder = reportDir;
        await tools.DownloadFileCommand.ExecuteAsync(null);
        Check("下载任意文件", File.Exists(Path.Combine(reportDir, "version_manifest_v2.json")), tools.Status);
        await ShotAsync("工具箱");
    }

    private async Task ExportImportAsync()
    {
        var instance = Page<InstanceViewModel>();
        main.CurrentPage = instance;
        instance.SelectedId = FabricName;
        instance.TabIndex = 7;
        await WaitAsync(() => instance.ExportOptions.Count > 0, 5);
        var pack = Path.Combine(reportDir, "e2e.mrpack");
        await instance.ExportPackAsync(pack);
        var info = File.Exists(pack) ? ModpackService.Inspect(pack) : null;
        Check("导出 mrpack", info is { McVersion: "1.21.1", LoaderVersion: not null }, instance.Status);

        var content = Page<ContentViewModel>();
        main.CurrentPage = content;
        var renamed = Path.Combine(reportDir, PackName + ".mrpack");
        await RewriteNameAsync(pack, renamed, PackName);
        await content.ImportLocalPackAsync(renamed);
        var mods = Path.Combine(GameRoot(), "versions", PackName, "mods");
        Check("导入 mrpack", Directory.Exists(mods) && Directory.EnumerateFiles(mods, "sodium*").Any(), content.Status);

        // Prism / MultiMC 实例压缩包：套一层文件夹，带内存设置。
        var prism = Path.Combine(reportDir, "E2E-Prism.zip");
        File.Delete(prism);
        using (var zip = ZipFile.Open(prism, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("E2E-Prism/instance.cfg").Open()))
                w.Write("[General]\nInstanceType=OneSix\nname=E2E-Prism\nOverrideMemory=true\nMaxMemAlloc=2500\n");
            using (var w = new StreamWriter(zip.CreateEntry("E2E-Prism/mmc-pack.json").Open()))
                w.Write("""{"components":[{"uid":"net.minecraft","version":"1.21.1"},{"uid":"net.fabricmc.fabric-loader","version":"0.16.5"}],"formatVersion":1}""");
            var sodium = Directory.EnumerateFiles(Path.Combine(GameRoot(), "versions", FabricName, "mods"), "sodium*.jar").First();
            zip.CreateEntryFromFile(sodium, "E2E-Prism/.minecraft/mods/" + Path.GetFileName(sodium));
        }
        await content.ImportLocalPackAsync(prism);
        var gameDir = new GameDirectory(GameRoot());
        var prismDir = Path.Combine(GameRoot(), "versions", "E2E-Prism");
        var prismMods = Path.Combine(prismDir, "mods");
        var prismSettings = Amber.Core.Launch.InstanceSettings.Load(gameDir, "E2E-Prism");
        Check("导入 Prism 实例", Directory.Exists(prismMods) && Directory.EnumerateFiles(prismMods, "sodium*").Any() && prismSettings.MaxMemoryMb == 2500
                              && GameInstance.Detect(gameDir, "E2E-Prism", prismDir).Loader == "fabric", content.Status);
    }

    /// <summary>导入时版本名字来自整合包名字，改一下避免和原实例同名。</summary>
    private static async Task RewriteNameAsync(string source, string target, string name)
    {
        File.Copy(source, target, true);
        using var zip = ZipFile.Open(target, ZipArchiveMode.Update);
        var entry = zip.GetEntry("modrinth.index.json")!;
        string text;
        using (var reader = new StreamReader(entry.Open())) text = await reader.ReadToEndAsync();
        var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        node["name"] = name;
        entry.Delete();
        await using var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open());
        await writer.WriteAsync(node.ToJsonString());
    }
}
