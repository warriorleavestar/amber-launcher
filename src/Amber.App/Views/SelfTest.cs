using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Game;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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

    private async Task InstallLoaderAsync()
    {
        var download = Page<DownloadViewModel>();
        main.CurrentPage = download;
        await WaitAsync(() => download.Items.Count > 0, 30);
        Check("下载页加载版本列表", download.Items.Count > 0, $"{download.Items.Count} 个");
        download.ConfigureCommand.Execute(download.Items.First(i => i.Id == "1.21.1"));
        download.SelectedLoader = download.LoaderChoices.First(c => c.Label == "Fabric");
        await WaitAsync(() => download.SelectedLoaderVersion is not null, 30);
        Check("加载器版本列表", download.SelectedLoaderVersion is not null, download.SelectedLoaderVersion?.Version ?? "");
        download.CustomName = FabricName;
        await ShotAsync("安装面板");
        await download.InstallCommand.ExecuteAsync(null);
        Check("安装 Fabric（自定义名字）", download.Status.Contains("安装完成"), download.Status);
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
        instance.DeleteWorldCommand.Execute(instance.Worlds.First());
        Check("删除世界进回收站", !Directory.Exists(world), instance.Status);

        for (var tab = 3; tab <= 7; tab++)
        {
            instance.TabIndex = tab;
            await ShotAsync($"实例标签{tab}");
        }
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
