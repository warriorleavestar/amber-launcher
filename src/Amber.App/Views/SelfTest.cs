using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;
using Amber.Core.Launch;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media;
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

    /// <summary>当前页面现在的状态（填好的列表、房间）在四种语言、最小的两种窗口下有没有被切掉的东西。</summary>
    private async Task LayoutHereAsync(string tag)
    {
        var problems = (await new LayoutCheck(window, main, Path.Combine(reportDir, "layout")).CheckCurrentAsync(tag, Log))
            .Where(p => p.Kind == LayoutCheck.Kind.Clipped).ToList();
        Check($"界面尺寸（{tag}）：四种语言、小中大窗口下没有被切掉的内容", problems.Count == 0,
            string.Join("；", problems.Take(5).Select(p => $"{p.Page}@{p.Size} {p.What}")));
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
        // 下面的检查按简体中文的文字判断，不管这台电脑是什么系统语言都先切到简体中文。
        Amber.Core.Localization.L.SetLanguage(Amber.Core.Localization.L.Source);
        try
        {
            if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_SCOPE") == "game")
            {
                await GameLifecycleAsync();
                Log("DONE");
                window.Close();
                return;
            }
            // 必须在布局检查遍历所有页面之前验证，免得漏掉首次进入资源页的空列表。
            await ContentAutoLoadAsync();
            if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_SCOPE") == "content")
            {
                Log("DONE");
                window.Close();
                return;
            }
            await LogPageAsync();
            // 换语言前后各个下拉框的选中项和设置文件都不能变。
            string Selections() => Dispatcher.UIThread.Invoke(() =>
            {
                var s = Page<SettingsViewModel>();
                var i = Page<InstanceViewModel>();
                return $"源 {s.SourceIndex} 主题 {s.SelectedThemeIndex} 语言 {s.SelectedLanguageIndex} 登录 {Page<AccountsViewModel>().LoginTab} " +
                       $"类型 {Page<ContentViewModel>().TypeIndex} 筛选 {Page<DownloadViewModel>().Filter.Label.Length > 0} 隔离 {i.IsolateIndex} 内存 {i.MemoryModeIndex} 全屏 {i.FullscreenIndex} 导出 {i.ExportFormatIndex}";
            });
            // 先检查每个页面在各种窗口大小下有没有被切掉的东西（截图放在报告目录的 layout 文件夹）。
            var all = await new LayoutCheck(window, main, Path.Combine(reportDir, "layout")).RunAsync(Log);
            var layout = all.Where(p => p.Kind == LayoutCheck.Kind.Clipped).ToList();
            Check($"界面尺寸：{main.Pages.Count} 个页面 × {LayoutCheck.Sizes.Length} 种窗口大小 × 5 种语言没有被切掉的内容", layout.Count == 0,
                layout.Count == 0 ? "" : string.Join("；", layout.Take(5).Select(p => $"{p.Page}@{p.Size} {p.What}")));
            // 自适应问题（横向滚动、主要按钮要滚动、宽屏空白）是改版要逐页解决的，先记数，不算失败；详见 layout/layout.txt。
            Log($"自适应问题 {all.Count - layout.Count} 处（见 layout/layout.txt）");
            // 布局检查已经把每个页面都显示过（下拉框都绑定好了），再在四种语言之间切一圈。
            var selectionsBefore = Selections();
            var settingsBefore = File.Exists(Amber.App.Services.AppSettings.FilePath) ? File.ReadAllText(Amber.App.Services.AppSettings.FilePath) : "";
            foreach (var (code, _) in Amber.Core.Localization.L.Languages.Reverse())
            {
                Amber.Core.Localization.L.SetLanguage(code);
                await Task.Delay(300);
            }
            var selectionsAfter = Selections();
            var settingsAfter = File.Exists(Amber.App.Services.AppSettings.FilePath) ? File.ReadAllText(Amber.App.Services.AppSettings.FilePath) : "";
            Check("换语言：各下拉框的选中项不变，设置文件没被改写", selectionsBefore == selectionsAfter && settingsBefore == settingsAfter,
                $"{selectionsBefore} → {selectionsAfter}{(settingsBefore == settingsAfter ? "" : "；设置文件变了")}");
            window.Width = 1080;
            window.Height = 700;
            await InstallLoaderAsync();
            await InstallModAsync();
            await InstanceAsync();
            await LaunchAsync();
            await CrashAsync();
            await ServersAndWorldsAsync();
            await OldForgeAndCompatAsync();
            await ToolsAsync();
            await MultiplayerAsync();
            await DirectRoomAsync();
            await ExportImportAsync();
            await CurrentInstanceAsync();
            await OnboardingAsync();
            Log("DONE");
        }
        catch (Exception e)
        {
            Log("FAIL 自检中断：" + e);
        }
        window.Close();
    }

    private async Task ContentAutoLoadAsync()
    {
        var content = Page<ContentViewModel>();
        var previous = main.CurrentPage;
        main.CurrentPage = content;
        await WaitAsync(() => content.Results.Count > 0 || content.StatusInfo?.Kind == StatusKind.Error, 30);
        Check("首次进入资源页自动加载 Modrinth 列表（不点搜索）", content.Query == "" && content.Results.Count > 0, content.Status);
        await ShotAsync("资源页首次加载");

        var first = content.Results.FirstOrDefault();
        main.CurrentPage = previous;
        main.CurrentPage = content;
        Check("返回资源页保留已有列表", first is not null && ReferenceEquals(first, content.Results.FirstOrDefault()), content.Status);

        content.SourceIndex = 1;
        await WaitAsync(() => content.Results.Count > 0 || content.StatusInfo?.Kind == StatusKind.Error, 30);
        Check("切换 CurseForge 自动加载或提示缺少 API 密钥", content.Results.Count > 0 || content.Status.Contains(L.T("core.content.curseforge.missingapikey")), content.Status);
        await ShotAsync("资源页切换来源");
        content.SourceIndex = 0;
        await WaitAsync(() => content.Results.Count > 0 || content.StatusInfo?.Kind == StatusKind.Error, 30);
        Check("切回 Modrinth 自动加载列表", content.Results.Count > 0, content.Status);
        main.CurrentPage = previous;
    }

    private async Task LogPageAsync()
    {
        var previous = main.CurrentPage;
        var log = Page<LogViewModel>();
        main.CurrentPage = log;
        var page = await FindAsync<LogPage>(_ => true);
        if (page is null)
        {
            Check("日志页显示", false);
            return;
        }
        await CheckLogPageAsync(page, Check);
        await LayoutHereAsync("日志页");
        await ShotAsync("日志页");
        log.Clear();
        main.CurrentPage = previous;
    }

    /// <summary>只用合成日志检查真实控件，不下载、不启动游戏，也不读写游戏目录。</summary>
    internal static async Task CheckLogPageAsync(LogPage page, Action<string, bool, string> check)
    {
        var log = (LogViewModel)page.DataContext!;
        var list = page.FindControl<ListBox>("List")!;
        var filter = page.FindControl<CheckBox>("ProblemsFilter")!;
        var follow = page.FindControl<CheckBox>("FollowLatest")!;
        log.Clear();
        await Ui.FlushAsync();
        log.IsCurrent = false;
        foreach (var level in new[] { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" })
            log.AddRaw($"[12:34:56] [Render thread/{level}]: 合成日志 {level}");
        log.AddRaw("Exception in thread \"main\" java.lang.RuntimeException: 合成异常");
        log.AddRaw("\tat net.minecraft.client.main.Main.main(Main.java:42)");
        log.AddRaw("Caused by: java.io.IOException: 合成原因");
        check("日志：文本等级、异常和堆栈计数", await WaitAsync(() => log.Lines.Count == 9, 5)
            && log.ErrorCount == 5 && log.WarningCount == 1, $"错误 {log.ErrorCount}，警告 {log.WarningCount}");
        check("日志：离页时只累计错误徽标", log.Badge == 5 && log.HasBadge, log.Badge.ToString());
        log.IsCurrent = true;
        check("日志：进入页面清空徽标", !log.HasBadge, "");

        await Task.Delay(200);
        var messages = page.GetVisualDescendants().OfType<SelectableTextBlock>().Where(c => c.DataContext is LogEntry).ToList();
        check("日志：等级文字使用主题颜色和 1.5 倍行高", messages.Count > 0 && messages.All(c =>
        {
            var entry = (LogEntry)c.DataContext!;
            var key = entry.IsError ? "DangerText" : entry.IsWarning ? "WarningText" : entry.IsMuted ? "TextTertiary" : "TextPrimary";
            return c.Foreground is ISolidColorBrush actual && page.FindResource(page.ActualThemeVariant, key) is ISolidColorBrush expected
                && actual.Color == expected.Color && Math.Abs(c.LineHeight - c.FontSize * 1.5) < 0.01;
        }), "");

        filter.IsChecked = true;
        check("日志：勾选筛选保留问题行和总计数", await WaitAsync(() => log.OnlyProblems && log.Lines.Count == 6, 5)
            && log.Lines.All(e => e.Severity >= GameLogLevel.Warn) && log.ErrorCount == 5 && log.WarningCount == 1, "");
        filter.IsChecked = false;
        await WaitAsync(() => log.Lines.Count == 9, 5);
        log.AddRaw("[main/ERROR]: 当前页可见错误");
        check("日志：当前页新错误不产生未读徽标", await WaitAsync(() => log.ErrorCount == 6, 5) && !log.HasBadge, "");

        // 点“错误 N”芯片：选中第一个错误行，再点跳到下一个。
        var errorChip = page.FindControl<Button>("ErrorChip");
        var firstError = log.Lines.First(e => e.IsError);
        var pressed = Press(errorChip);
        await Task.Delay(100);
        var jumpedFirst = ReferenceEquals(log.SelectedLine, firstError) && ReferenceEquals(list.SelectedItem, firstError);
        Press(errorChip);
        await Task.Delay(100);
        check("日志：点错误芯片跳到第一个、再点跳到下一个错误", pressed && jumpedFirst && log.SelectedLine is { IsError: true } next
            && !ReferenceEquals(next, firstError) && !log.FollowLatest, log.SelectedLine?.Message ?? "");
        follow.IsChecked = true;

        follow.IsChecked = false;
        list.ScrollIntoView(0);
        await Task.Delay(200);
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset.Y;
        for (var i = 0; i < 200; i++) log.AddRaw($"[main/INFO]: 跟随测试 {i}");
        await WaitAsync(() => log.Lines.Count == 210, 5);
        await Task.Delay(300);
        check("日志：关闭跟随时不抢滚动位置", !log.FollowLatest && offset.HasValue && scroll is not null
            && Math.Abs(scroll.Offset.Y - offset.Value) < 1, "");
        follow.IsChecked = true;
        check("日志：重新跟随立即到末尾", await WaitAsync(() => scroll is not null && scroll.Offset.Y > 0
            && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 2, 5), "");
        check("日志：长列表使用虚拟化面板", list.GetVisualDescendants().Any(c => c is VirtualizingStackPanel)
            && list.GetVisualDescendants().OfType<ListBoxItem>().Count() < log.Lines.Count, "");

        log.Clear();
        await Ui.FlushAsync();
        log.AddRaw("[main/INFO]: 筛选空态");
        await WaitAsync(() => log.Lines.Count == 1, 5);
        filter.IsChecked = true;
        check("日志：筛选无结果时显示对应空态", await WaitAsync(() => log.Lines.Count == 0, 5)
            && log.EmptyMessage == Amber.Core.Localization.L.T("log.empty_filtered"), "");
        filter.IsChecked = false;
        log.AddRaw("[main/WARN]: 截图警告");
        log.AddRaw("[main/ERROR]: 截图错误");
        await WaitAsync(() => log.Lines.Count == 3, 5);
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

    /// <summary>在独立测试目录里，验证主按钮启动、退出，以及已保存世界的再次进入和保存。</summary>
    private async Task GameLifecycleAsync()
    {
        var id = Environment.GetEnvironmentVariable("AMBER_SELFTEST_VERSION") ?? "1.21.1";
        var home = Page<HomeViewModel>();
        var log = Page<LogViewModel>();
        main.CurrentPage = home;
        home.SelectedVersion = id;
        var launching = home.PrimaryCommand.ExecuteAsync(null);
        try
        {
            Check("准备期间主按钮仍可取消", home.PrimaryCommand.CanExecute(null));
            var reached = await WaitAsync(() => home.IsRunning && log.Lines.Any(l =>
                l.Message.Contains("Sound engine started") || l.Message.Contains("OpenAL initialized")), 180);
            Check("主按钮启动游戏到主界面", reached, home.Status);
            if (!reached) throw new InvalidOperationException(home.Status);
            if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_EXPECT_LANGUAGE") is { Length: > 0 } expectedLanguage)
            {
                var optionsPath = Path.Combine(home.Service.WorkDirFor(id), "options.txt");
                Check("游戏已应用系统语言预设", File.ReadAllLines(optionsPath).Contains("lang:" + expectedLanguage), expectedLanguage);
            }
            await ShotAsync("主按钮运行中");
            Check("运行期间主按钮仍可退出", home.PrimaryCommand.CanExecute(null));
            await home.PrimaryCommand.ExecuteAsync(null);
            Check("主按钮正常退出游戏", await WaitAsync(() => home.RunCount == 0, 60), home.Status);
            await launching.WaitAsync(TimeSpan.FromSeconds(5));
            Check("用户退出不显示崩溃", !home.HasCrash, home.Status);

            var instance = Page<InstanceViewModel>();
            main.CurrentPage = instance;
            instance.SelectedId = id;
            instance.TabIndex = 2;
            if (!await WaitAsync(() => instance.Worlds.Count > 0, 20))
                throw new InvalidOperationException("测试目录里没有已保存的世界");
            var world = instance.Worlds[0];
            var level = Path.Combine(world.World.Path, "level.dat");
            var savedBefore = File.GetLastWriteTimeUtc(level);
            instance.JoinWorldCommand.Execute(world);
            var joined = await WaitAsync(() => home.IsRunning && log.Lines.Any(l =>
                l.Message.Contains("logged in with entity id")), 180);
            Check("从实例页重新进入已保存世界", joined, home.Status);
            if (!joined) throw new InvalidOperationException(home.Status);
            main.CurrentPage = home;
            await ShotAsync("已进入存档");
            await home.PrimaryCommand.ExecuteAsync(null);
            Check("世界内主按钮正常退出", await WaitAsync(() => home.RunCount == 0, 60), home.Status);
            log.Flush();
            Check("退出时保存存档", File.GetLastWriteTimeUtc(level) > savedBefore
                && log.Lines.Any(l => l.Message.Contains("All dimensions are saved")));
            Check("保存退出不显示崩溃", !home.HasCrash, home.Status);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(reportDir, "game-log.txt"), log.Lines.Select(l => l.Message));
            if (home.RunCount > 0) home.CancelCommand.Execute(null);
        }
    }

    /// <summary>下载页全程操作控件：搜索框输入、点“安装…”、在下拉框选 Fabric、改名字、点“开始安装”。</summary>
    private async Task InstallLoaderAsync()
    {
        var download = Page<DownloadViewModel>();
        main.CurrentPage = download;
        await WaitAsync(() => download.Items.Count > 0, 30);
        Check("下载页加载版本列表", download.Items.Count > 0, $"{download.Items.Count} 个");

        var search = await FindAsync<TextBox>(t => t.PlaceholderText?.StartsWith("搜索版本号", StringComparison.Ordinal) == true);
        Dispatcher.UIThread.Invoke(() => search!.Text = "1.21.1");
        // 按行的数据和命令找按钮（不靠按钮文字）；每次找之前把这一行滚进可见区，列表是虚拟化的，没滚到的行不会生成。
        var list = await FindAsync<ListBox>(l => ReferenceEquals(l.ItemsSource, download.Items));
        Button? row = null;
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() =>
        {
            if (download.Items.FirstOrDefault(i => i.Id == "1.21.1") is { } item) list?.ScrollIntoView(item);
            row = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible
                && b.DataContext is RemoteVersionItem { Id: "1.21.1" } && ReferenceEquals(b.Command, download.ConfigureCommand));
            return row is not null;
        }), 20);
        var rowInfo = Dispatcher.UIThread.Invoke(() =>
        {
            var rows = window.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is RemoteVersionItem).ToList();
            return $"{download.Items.Count} 个；窗口 {window.Bounds.Width:0}×{window.Bounds.Height:0}，已生成的行 {string.Join("/", rows.Select(b => ((RemoteVersionItem)b.DataContext!).Id))}，" +
                   $"按钮文字「{row?.Content}」";
        });
        Check("搜索框过滤出 1.21.1 并显示安装按钮", row is not null && row.Content as string == "安装…" && download.Items.All(i => i.Id.Contains("1.21.1")), rowInfo);
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
        var launchButton = await FindAsync<Button>(b => b.Content as string == "立即启动" && b.IsEffectivelyVisible);
        Check("安装完成后显示成功卡、设为当前实例，并去掉“安装未完成”标记",
            download.HasInstalled && download.JustInstalled == FabricName && launchButton is not null
            && Page<HomeViewModel>().Service.CurrentInstance == FabricName
            && !File.Exists(Path.Combine(GameRoot(), "versions", FabricName, GameDirectory.IncompleteMarker)), download.InstalledTitle);
        // 换页会重建页面控件，先在这一页把搜索框清掉。
        Dispatcher.UIThread.Invoke(() => search!.Text = "");
        var addMods = await FindAsync<Button>(b => b.Content as string == "添加模组…" && b.IsEffectivelyVisible);
        Check("成功卡的“添加模组…”跳到资源页的模组，装到新实例",
            Press(addMods) && await WaitAsync(() => main.CurrentPage is ContentViewModel { TargetInstance: FabricName, TypeIndex: (int)ContentType.Mod }, 5),
            main.CurrentPage.Title);
        main.CurrentPage = download;
        download.CloseInstalledCommand.Execute(null);
    }

    private async Task InstallModAsync()
    {
        var openedUnsafePage = false;
        var acceptedUnsafePage = ContentViewModel.TryOpenProjectPage("file:///invalid-project-page", _ => openedUnsafePage = true);
        Check("项目网页拒绝非网页协议", !acceptedUnsafePage && !openedUnsafePage, "file 协议不会交给系统执行");
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
        Check("没配 Key 时 CurseForge 给出明确提示", content.Status.Contains("API 密钥") || content.Results.Count > 0, content.Status);
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
        Check("主按钮：有账号和实例时是“启动游戏”", home.PrimaryLabel == "启动游戏", home.PrimaryLabel);
        // 无痕模式：在设置页勾上，这次启动的游戏日志不记聊天（生成过滤后的日志配置，游戏照常写 latest.log）。
        var settings = Page<SettingsViewModel>();
        main.CurrentPage = settings;
        var noTraceBox = await FindAsync<CheckBox>(c => c.Name == "NoTraceBox");
        Dispatcher.UIThread.Invoke(() => { if (noTraceBox is not null) noTraceBox.IsChecked = true; });
        Dispatcher.UIThread.Invoke(() => settings.SaveCommand.Execute(null));
        Check("无痕模式：勾选后存进设置", noTraceBox is not null && settings.NoTrace && File.ReadAllText(Amber.App.Services.AppSettings.FilePath).Contains("\"NoTrace\": true"));
        var instance = Page<InstanceViewModel>();
        instance.SelectedId = FabricName;
        var script = Path.Combine(reportDir, OperatingSystem.IsWindows() ? "notrace-run.bat" : "notrace-run.sh");
        await instance.ExportLaunchScriptAsync(script);
        var scriptConfigs = Directory.Exists(script + ".amber")
            ? Directory.GetFiles(script + ".amber", "*.amber-notrace.xml") : [];
        Check("无痕模式：导出脚本附带持久日志配置", File.Exists(script) && scriptConfigs.Length == 1
            && File.ReadAllText(script).Contains(".amber-notrace.xml")
            && File.ReadAllText(scriptConfigs[0]).Contains("RegexFilter"), instance.Status);
        main.CurrentPage = home;
        var launchStart = DateTime.Now.AddSeconds(-2);
        var launching = home.LaunchCommand.ExecuteAsync(null);
        await WaitAsync(() => home.IsPreparing, 5);
        Check("主按钮：准备中变成“取消启动”", home.PrimaryLabel == "取消启动", home.PrimaryLabel);
        var reached = await WaitAsync(() => log.Lines.Any(l => l.Message.Contains("Sound engine started") || l.Message.Contains("OpenAL initialized")), 180);
        Check("启动游戏到主界面", reached, home.Status);
        Check("启动前命令运行过", File.Exists(Path.Combine(reportDir, "prelaunch.txt")) && File.ReadAllText(Path.Combine(reportDir, "prelaunch.txt")).Contains(FabricName));
        Check("实例内存生效", log.Lines.Any(l => l.Message.Contains("最大内存 3072 MB")));
        await WaitAsync(() => home.Status.Contains("运行中 ·"), 5);
        Check("进入游戏后显示运行时长，主按钮变成“退出游戏”", home.Status.Contains("运行中 ·") && home.PrimaryLabel == "退出游戏" && !home.ShowProgress,
            $"{home.Status} / {home.PrimaryLabel}");
        // 过滤后的配置放在 Amber 自己的数据目录里，游戏目录里一个都不能有；游戏退出后删掉。
        var noTraceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amber", "notrace");
        var filtered = Directory.Exists(noTraceRoot)
            ? Directory.EnumerateFiles(noTraceRoot, "*.amber-notrace.xml", SearchOption.AllDirectories).Where(f => File.GetLastWriteTime(f) >= launchStart).ToList()
            : [];
        var inGameDir = Directory.EnumerateFiles(GameRoot(), "*.amber-notrace.xml", SearchOption.AllDirectories).Where(f => File.GetLastWriteTime(f) >= launchStart).ToList();
        Check("无痕模式：启动时提示并用过滤后的日志配置", log.Lines.Any(l => l.Message.Contains(Amber.Core.Localization.L.T("core.launch.notrace.on")))
                                                && filtered.Count > 0 && File.ReadAllText(filtered[0]).Contains("RegexFilter")
                                                && File.ReadAllText(filtered[0]).Contains("net.minecraft.server.MinecraftServer"), string.Join(", ", filtered.Select(Path.GetFileName)));
        Check("无痕模式：生成的日志配置不写进游戏目录", inGameDir.Count == 0, string.Join(", ", inGameDir.Select(f => Path.GetRelativePath(GameRoot(), f))));
        var latest = Directory.EnumerateFiles(GameRoot(), "latest.log", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTime(f) >= launchStart).ToList();
        Check("无痕模式：游戏照常写 latest.log", latest.Count > 0 && new FileInfo(latest[0]).Length > 0, string.Join(", ", latest.Select(f => Path.GetRelativePath(GameRoot(), f))));
        await ShotAsync("运行中");
        main.CurrentPage = log;
        await ShotAsync("日志页");

        // 游戏在运行时从别的页面再请求启动：启动页给出多开警告，【仍然启动】能同时开第二个客户端。
        home.Service.RequestLaunch(new Amber.App.Services.LaunchRequest(FabricName));
        await WaitAsync(() => home.HasPendingLaunch && main.CurrentPage == home, 5);
        Check("多开：游戏运行中再启动出现警告并回到启动页", home.HasPendingLaunch && main.CurrentPage == home && home.PendingLaunchText.Contains(FabricName), home.PendingLaunchText);
        await ShotAsync("多开警告");
        home.LaunchAnywayCommand.Execute(null);
        // 第二个客户端自己的输出里出现进入主界面的标志，状态才会变成“运行中 ·”。
        var second = await WaitAsync(() => home.RunCount == 2 && home.IsRunning && home.Status.Contains("运行中 ·"), 180);
        Check("多开：【仍然启动】后同时开着两个客户端", second, $"{home.RunCount} 个 {home.Status}");
        home.CancelCommand.Execute(null);
        await WaitAsync(() => home.RunCount == 1, 30);

        // 【退出游戏】让游戏自己保存退出，不是直接结束进程。
        await home.PrimaryCommand.ExecuteAsync(null);
        Check("退出游戏：按钮变成“正在保存并退出…”", home.PrimaryLabel == "正在保存并退出…" || home.RunCount == 0, home.PrimaryLabel);
        var exited = await WaitAsync(() => home.RunCount == 0, 60);
        if (!exited) home.CancelCommand.Execute(null);
        await launching;
        Check("退出游戏：游戏自己退出，状态正确（不报崩溃）", exited && home.Status.Contains("已结束") && !home.HasCrash, home.Status);
        var cleaned = await WaitAsync(() => filtered.All(f => !File.Exists(f)), 15);
        Check("无痕模式：游戏退出后删掉生成的日志配置", filtered.Count > 0 && cleaned, string.Join(", ", filtered.Where(File.Exists).Select(Path.GetFileName)));
        // 后面的崩溃分析要看完整日志，把无痕模式关回去。
        Dispatcher.UIThread.Invoke(() => { settings.NoTrace = false; settings.SaveCommand.Execute(null); });
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
        var crashedIds = new List<string>();
        void OnCrashed(string id) => crashedIds.Add(id);
        home.GameCrashed += OnCrashed;
        await home.LaunchCommand.ExecuteAsync(null);
        home.GameCrashed -= OnCrashed;
        var found = home.Crash?.Findings.Any(f => f.Title.Contains("JVM 参数")) == true;
        Check("崩溃分析识别出坏的 JVM 参数", found, home.Crash?.Summary ?? home.Status);
        Check("崩溃时发出这一局的崩溃事件（多开时窗口靠它恢复）", crashedIds.SequenceEqual([FabricName]), string.Join(", ", crashedIds));
        await ShotAsync("崩溃分析");

        // 崩溃卡的“查看日志”打开崩的那一局的日志。
        var log = Page<LogViewModel>();
        home.ViewLogCommand.Execute(null);
        await WaitAsync(() => main.CurrentPage == log, 5);
        Check("崩溃卡“查看日志”打开崩的那一局", log.SelectedRun?.Instance == FabricName && log.OnlyProblems, log.RunSummary);
        log.OnlyProblems = false;
        main.CurrentPage = home;

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
        await LayoutHereAsync("世界页");
        await DatapacksAsync(instance, world);
        instance.DeleteWorldCommand.Execute(instance.Worlds.First());
        var trashRoot = Path.Combine(GameRoot(), TrashService.TrashFolder);
        Check("删除世界进回收目录", !Directory.Exists(world) && TrashService.List(trashRoot).Any(e => Path.GetFullPath(e.OriginalPath) == Path.GetFullPath(world)), instance.Status);
        var undone = Services.Toasts.UndoLatest();
        await WaitAsync(() => instance.Worlds.Any(w => w.Name == "自检世界"), 5);
        Check("撤销删除世界", undone && Directory.Exists(world) && instance.Worlds.Any(w => w.Name == "自检世界")
            && instance.StatusInfo is { Key: "trash.restored", Kind: StatusKind.Success }, instance.Status);
        instance.DeleteWorldCommand.Execute(instance.Worlds.First(w => w.Name == "自检世界"));
        Check("再次删除世界", !Directory.Exists(world), instance.Status);

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
        await LayoutHereAsync("数据包");

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

    /// <summary>
    /// 联机页：界面检查总是做；联机组件已下载时，再在本机起房主和加入方两个 EasyTier（只经 127.0.0.1 互连，不连公共节点），
    /// 用一个假的“游戏”TCP 服务确认加入方的转发地址真的能连到房主。
    /// </summary>
    private async Task MultiplayerAsync()
    {
        var host = Page<MultiplayerViewModel>();
        main.CurrentPage = host;
        await Task.Delay(500);
        host.JoinCode = "AMB-123";
        Check("联机：房间码格式错误有提示", Press(await FindAsync<Button>(b => b.Content as string == "加入房间")) && host.Status.Contains("格式") || !host.IsComponentReady && host.Status.Contains("组件"), host.Status);
        await ShotAsync("联机页");
        if (!host.IsComponentReady)
        {
            Log("SKIP 联机：联机组件没有下载，跳过真实连接测试");
            return;
        }

        // 假的游戏服务器：收到一行就回 pong:+原文。
        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var gamePort = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        using var stopServer = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stopServer.IsCancellationRequested)
            {
                using var client = await server.AcceptTcpClientAsync(stopServer.Token);
                var stream = client.GetStream();
                var reader = new StreamReader(stream);
                var line = await reader.ReadLineAsync(stopServer.Token);
                var bytes = System.Text.Encoding.UTF8.GetBytes("pong:" + line + "\n");
                await stream.WriteAsync(bytes, stopServer.Token);
            }
        });

        var relayPort = Amber.Core.Multiplayer.EasyTierArgs.FreeTcpPort();
        host.TestListeners = [$"tcp://127.0.0.1:{relayPort}"];
        host.TestPeers = [];
        host.ManualPort = gamePort.ToString();
        await host.CreateRoomCommand.ExecuteAsync(null);
        Check("联机：创建房间", host.IsHosting && host.RoomCodeText.StartsWith("AMB-", StringComparison.Ordinal), host.Status);
        await ShotAsync("联机房主");
        await LayoutHereAsync("联机房主");

        var guest = host.CreateTestPeer();
        guest.Activate();
        guest.TestPeers = [$"tcp://127.0.0.1:{relayPort}"];
        guest.JoinCode = "朋友发来的：" + host.RoomCodeText;
        guest.JoinCommand.Execute(null);
        Check("联机：加入房间", guest.IsJoined && guest.JoinAddress.StartsWith("127.0.0.1:", StringComparison.Ordinal), guest.Status);

        var connected = await WaitAsync(() => guest.Peers.Any(p => p.Peer.Ip == Amber.Core.Multiplayer.EasyTierArgs.HostIp), 60);
        Check("联机：加入方看到房主", connected, guest.Status);
        string? reply = null;
        for (var attempt = 0; attempt < 15 && reply is null; attempt++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var parts = guest.JoinAddress.Split(':');
                await client.ConnectAsync(parts[0], int.Parse(parts[1]));
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello\n"));
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                reply = await new StreamReader(stream).ReadLineAsync(readTimeout.Token);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException)
            {
                await Task.Delay(2000);
            }
        }
        Check("联机：经虚拟网络连到房主的游戏端口", reply == "pong:hello", reply ?? "没有回应");
        // 房间聊天：加入方经 EasyTier 转发连到房主的聊天服务，双方互发消息。
        var chatUp = await WaitAsync(() => host.ChatReady && guest.ChatReady, 40);
        Check("联机：双方进入房间聊天", chatUp, $"{host.ChatStatus} / {guest.ChatStatus}");
        guest.ChatInput = "你好，房主";
        await guest.SendChatCommand.ExecuteAsync(null);
        host.ChatInput = "欢迎";
        await host.SendChatCommand.ExecuteAsync(null);
        var chatOk = await WaitAsync(() => host.ChatLines.Any(l => l.Text == "你好，房主" && !l.Mine) && guest.ChatLines.Any(l => l.Text == "欢迎" && !l.Mine), 15);
        Check("联机：聊天消息双向送达", chatOk, string.Join(" | ", host.ChatLines.Select(l => l.Name + ":" + l.Text)));
        Check("联机：进入房间通知", host.ChatLines.Any(l => l.Message.Kind == Amber.Core.Multiplayer.ChatKind.Join && !l.Mine));
        main.CurrentPage = Page<HomeViewModel>();
        guest.ChatInput = "看得到未读吗";
        await guest.SendChatCommand.ExecuteAsync(null);
        Check("联机：不在联机页时显示未读数", await WaitAsync(() => host.Badge > 0, 10), host.Badge.ToString());
        main.CurrentPage = host;
        Check("联机：回到联机页未读清零", host.Badge == 0);
        await ShotAsync("联机聊天");
        await WaitAsync(() => host.Peers.Count > 1, 10);
        Check("联机：房主看到玩家", host.Peers.Any(p => p.Peer.Ip != Amber.Core.Multiplayer.EasyTierArgs.HostIp), host.Status);
        main.CurrentPage = host;
        await ShotAsync("联机房主有玩家");

        // 结束后用同一个房间码再加入：之前的聊天还在（只在内存里），后面有分隔行。
        var code = host.RoomCodeText;
        guest.StopCommand.Execute(null);
        Check("联机：结束后聊天清空", guest.ChatLines.Count == 0);
        guest.JoinCode = code;
        guest.JoinCommand.Execute(null);
        var restored = guest.ChatLines.Any(l => l.Text == "欢迎") && guest.ChatLines.Any(l => l.IsSeparator);
        Check("联机：重新加入同一房间看到之前的消息", restored, string.Join(" | ", guest.ChatLines.Select(l => l.Text)));
        Check("联机：重新加入后聊天重连", await WaitAsync(() => guest.ChatReady, 40), guest.ChatStatus);
        guest.StopCommand.Execute(null);
        host.StopCommand.Execute(null);

        await TransportRoomAsync(host, guest, gamePort, relayPort);

        stopServer.Cancel();
        server.Stop();
        var left = System.Diagnostics.Process.GetProcessesByName("easytier-core")
            .Count(p => { try { return p.MainModule?.FileName?.StartsWith(AppSettingsDataDir(), StringComparison.OrdinalIgnoreCase) == true; } catch { return false; } });
        Check("联机：结束后 easytier-core 都关掉了", left == 0, $"剩 {left} 个");
        host.TestListeners = null;
        host.TestPeers = null;
    }

    /// <summary>
    /// 封装模式的房间：高加密 + 伪装成 MC，房主允许转发。游戏端口和聊天都要经封装通道通；
    /// 加入方再开“仅游戏”转发（经房主），经它连一个公网地址要通，连局域网地址要被拒绝。
    /// </summary>
    private async Task TransportRoomAsync(MultiplayerViewModel host, MultiplayerViewModel guest, int gamePort, int relayPort)
    {
        await Task.Delay(1500);
        host.SelectedTransport = host.TransportChoices.First(c => c.Mode == Amber.Core.Multiplayer.Transport.TransportMode.High);
        host.DisguiseAsMinecraft = true;
        host.AllowGameProxy = true;
        host.ManualPort = gamePort.ToString();
        await host.CreateRoomCommand.ExecuteAsync(null);
        Check("封装：创建高加密 + 伪装的房间", host.IsHosting && host.RoomCodeText.StartsWith("AMB-T2D-", StringComparison.Ordinal) && host.TransportText.Contains("高加密"), host.TransportText + " / " + host.Status);
        guest.JoinCode = host.RoomCodeText;
        guest.JoinCommand.Execute(null);
        Check("封装：加入方按房间码用同样的封装", guest.IsJoined && guest.TransportText.Contains("高加密") && guest.TransportText.Contains("伪装"), guest.TransportText);
        await WaitAsync(() => guest.Peers.Any(p => p.Peer.Ip == Amber.Core.Multiplayer.EasyTierArgs.HostIp), 60);
        string? reply = null;
        for (var attempt = 0; attempt < 15 && reply is null; attempt++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var parts = guest.JoinAddress.Split(':');
                await client.ConnectAsync(parts[0], int.Parse(parts[1]));
                var stream = client.GetStream();
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("sealed\n"));
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                reply = await new StreamReader(stream).ReadLineAsync(readTimeout.Token);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException)
            {
                await Task.Delay(2000);
            }
        }
        Check("封装：经封装通道连到房主的游戏端口", reply == "pong:sealed", reply ?? "没有回应");
        var chatUp = await WaitAsync(() => host.ChatReady && guest.ChatReady, 40);
        guest.ChatInput = "封装后的聊天";
        await guest.SendChatCommand.ExecuteAsync(null);
        Check("封装：聊天经封装通道送达", chatUp && await WaitAsync(() => host.ChatLines.Any(l => l.Text == "封装后的聊天" && !l.Mine), 15), $"{host.ChatStatus} / {guest.ChatStatus}");

        guest.SelectedExitPeer = guest.ExitCandidates.FirstOrDefault(p => p.Peer.Ip == Amber.Core.Multiplayer.EasyTierArgs.HostIp);
        await guest.UseGameProxyCommand.ExecuteAsync(null);
        Check("转发：加入方开启仅游戏转发", guest.UsingGameProxy && guest.IsJoined, guest.Status);
        await WaitAsync(() => guest.Peers.Any(p => p.Peer.Ip == Amber.Core.Multiplayer.EasyTierArgs.HostIp), 60);
        var proxy = guest.TestGameProxy;
        string publicResult = "没有转发", privateResult = "没有转发";
        if (proxy is not null)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    await using var s1 = await proxy.Open(default);
                    await Amber.Core.Multiplayer.Transport.Socks5.ConnectAsync(s1, "api.minecraftservices.com", 443, default);
                    publicResult = "通";
                    break;
                }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException)
                {
                    publicResult = e.Message;
                    await Task.Delay(2000);
                }
            }
            try
            {
                await using var s2 = await proxy.Open(default);
                await Amber.Core.Multiplayer.Transport.Socks5.ConnectAsync(s2, "192.168.0.1", 80, default);
                privateResult = "竟然通了";
            }
            catch (IOException e)
            {
                privateResult = e.Message;
            }
        }
        Check("转发：经房主连公网", publicResult == "通", publicResult);
        Check("转发：经房主连局域网地址被拒绝", privateResult.Contains("局域网"), privateResult);
        await ShotAsync("联机封装和转发");
        await guest.StopGameProxyCommand.ExecuteAsync(null);
        Check("转发：停止后恢复", !guest.UsingGameProxy, guest.Status);
        guest.StopCommand.Execute(null);
        host.StopCommand.Execute(null);
        host.SelectedTransport = host.TransportChoices[0];
        host.DisguiseAsMinecraft = false;
        host.AllowGameProxy = false;
    }

    /// <summary>
    /// 直连房间（本机模拟：只用 127.0.0.1）：房主生成邀请码，加入方贴上后经直连桥连上，游戏端口和聊天都要通；
    /// 再跑一遍模拟的全条件测试，导出诊断报告并确认里面没有房间密钥。
    /// </summary>
    private async Task DirectRoomAsync()
    {
        var host = Page<MultiplayerViewModel>();
        if (!host.IsComponentReady)
        {
            Log("SKIP 直连：联机组件没有下载");
            return;
        }
        main.CurrentPage = host;
        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var gamePort = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        using var stopServer = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stopServer.IsCancellationRequested)
            {
                using var client = await server.AcceptTcpClientAsync(stopServer.Token);
                var stream = client.GetStream();
                var line = await new StreamReader(stream).ReadLineAsync(stopServer.Token);
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("pong:" + line + "\n"), stopServer.Token);
            }
        });

        host.DirectSimulate = true;
        host.DirectUseNodes = false;
        host.DebugMode = true;
        host.ManualPort = gamePort.ToString();
        await host.CreateDirectRoomCommand.ExecuteAsync(null);
        Check("直连：创建房间并生成邀请码", host.IsDirect && host.MyDirectCode.StartsWith("AMBDIRECT1-", StringComparison.Ordinal), host.Status);
        await ShotAsync("直连房主");

        var guest = host.CreateTestPeer();
        guest.Activate();
        guest.DirectSimulate = true;
        guest.DirectUseNodes = false;
        guest.DirectInviteInput = host.MyDirectCode;
        await guest.JoinDirectCommand.ExecuteAsync(null);
        Check("直连：加入并打洞", guest.IsDirect && guest.IsJoined, guest.Status);
        Check("直连：加入方生成回复码", guest.MyDirectCode.StartsWith("AMBREPLY1-", StringComparison.Ordinal));

        string? reply = null;
        for (var attempt = 0; attempt < 20 && reply is null; attempt++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var parts = guest.JoinAddress.Split(':');
                await client.ConnectAsync(parts[0], int.Parse(parts[1]));
                var stream = client.GetStream();
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("direct\n"));
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                reply = await new StreamReader(stream).ReadLineAsync(readTimeout.Token);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException)
            {
                await Task.Delay(1500);
            }
        }
        Check("直连：经直连桥连到房主的游戏端口", reply == "pong:direct", reply ?? "没有回应");
        await WaitAsync(() => guest.PathText.StartsWith("直连", StringComparison.Ordinal), 10);
        Check("直连：显示当前路径", guest.PathText.StartsWith("直连", StringComparison.Ordinal), guest.PathText);
        Check("直连：聊天连上", await WaitAsync(() => host.ChatReady && guest.ChatReady, 40), guest.ChatStatus);

        main.CurrentPage = host;
        var matrix = host.RunMatrixCommand.ExecuteAsync(null);
        // 加入方要先确认：确认前房主一步也不发。
        Check("直连：全条件测试先请加入方确认", await WaitAsync(() => guest.TestRequestPending, 20) && host.MatrixLines.Count == 0, guest.Status);
        await guest.AcceptTestRequestCommand.ExecuteAsync(null);
        // 测试进行时加入方能看到“测试中”横幅和停止按钮；结束后收起。
        var sawBanner = await WaitAsync(() => guest.GuestTestRunning && guest.GuestTestBanner.Length > 0, 60);
        await matrix;
        Check("直连：加入方测试中显示横幅，结束后收起", sawBanner && await WaitAsync(() => !guest.GuestTestRunning && !guest.TestRequestPending, 10),
            $"横幅 {sawBanner}，结束后 {guest.GuestTestRunning}");
        Check("直连：全条件测试（模拟）跑完", host.MatrixLines.Count >= 2 && host.MatrixLines.Skip(1).All(l => l.Contains("（模拟）")), string.Join(" / ", host.MatrixLines));
        Check("直连：模拟的局域网路径通过", host.MatrixLines.Any(l => l.Contains("同一局域网") && l.Contains("通过")), host.Status);
        await ShotAsync("直连全条件测试");

        var report = Path.Combine(reportDir, "direct-report.txt");
        host.ExportDiagnostics(report);
        var text = File.ReadAllText(report);
        var secret = host.RoomCodeText.Split('-')[2];
        Check("直连：诊断报告去掉了房间密钥", File.Exists(report) && !text.Contains(secret) && text.Contains("全条件测试结果"), $"{text.Length} 字");

        guest.StopCommand.Execute(null);
        host.StopCommand.Execute(null);
        host.DebugMode = false;
        host.DirectSimulate = false;
        stopServer.Cancel();
        server.Stop();
        await Task.Delay(3000);
        var left = System.Diagnostics.Process.GetProcessesByName("easytier-core")
            .Count(p => { try { return p.MainModule?.FileName?.StartsWith(AppSettingsDataDir(), StringComparison.OrdinalIgnoreCase) == true; } catch { return false; } });
        Check("直连：结束后 easytier-core 都关掉了", left == 0, $"剩 {left} 个");
    }

    private static string AppSettingsDataDir() => Amber.App.Services.AppSettings.DataDir;

    private static void FakeJar(string path, string entry, string text)
    {
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry(entry).Open());
        w.Write(text);
    }

    /// <summary>老 Forge 在下载页能选；模组页标出经 Connector 运行的 Fabric 模组、版本不对和加载器不对的模组。</summary>
    private async Task OldForgeAndCompatAsync()
    {
        var download = Page<DownloadViewModel>();
        main.CurrentPage = download;
        await WaitAsync(() => download.Items.Count > 0, 30);
        download.ConfigureCommand.Execute(download.Items.FirstOrDefault(i => i.Id == "1.5.2"));
        download.SelectedLoader = download.LoaderChoices.First(c => c.Label == "Forge");
        await WaitAsync(() => download.LoaderVersions.Count > 0 || download.LoaderHint.Length > 0 && !download.LoaderHint.StartsWith("正在", StringComparison.Ordinal), 45);
        Check("下载页：1.5.2 能选 Forge 版本", download.LoaderVersions.Any(v => v.Version.StartsWith("1.5.2-", StringComparison.Ordinal)), $"{download.LoaderVersions.Count} 个 {download.LoaderHint}");
        // 换语言时 Forge 的“推荐 / 最新”备注跟着换，但不能把用户填的名字改回默认。
        Dispatcher.UIThread.Invoke(() => download.CustomName = "MyPack");
        var noted = download.LoaderVersions.Count(v => v.Note is not null);
        Amber.Core.Localization.L.SetLanguage("en");
        await Task.Delay(800);
        var englishNotes = download.LoaderVersions.Where(v => v.Note is not null).Select(v => v.Note).Distinct().ToList();
        var nameKept = download.CustomName == "MyPack";
        Amber.Core.Localization.L.SetLanguage(Amber.Core.Localization.L.Source);
        await Task.Delay(500);
        Check("下载页：换语言后 Forge 备注换成英文，自己填的名字不变",
            nameKept && (noted == 0 || englishNotes.All(n => n is "Recommended" or "Latest")),
            $"名字 {download.CustomName}，备注 {string.Join("/", englishNotes)}");
        download.CloseConfigureCommand.Execute(null);

        var instance = Page<InstanceViewModel>();
        main.CurrentPage = instance;
        const string neo = "neoforge-21.1.265";
        if (!instance.Instances.Contains(neo))
        {
            Log("SKIP 模组兼容：测试目录里没有 " + neo);
            return;
        }
        instance.SelectedId = neo;
        instance.TabIndex = 1;
        await WaitAsync(() => instance.Mods.Count > 0 && instance.Mods.All(m => m.Compat is not null || !m.Enabled), 15);
        var fabricMod = instance.Mods.FirstOrDefault(m => m.Mod.FileName.StartsWith("morechathistory", StringComparison.Ordinal));
        Check("模组兼容：Fabric 模组标为经 Connector 运行", fabricMod?.CompatText.Contains("Connector") == true, fabricMod?.CompatText ?? "没有这个模组");
        Check("模组兼容：装了 Connector 就不再提示安装", !instance.NeedsConnector, instance.CompatSummary);
        var expected = ConnectorWarmup.Pending(instance.ModsDir![..^"mods".Length].TrimEnd('\\', '/'), instance.Mods.Select(m => m.Mod), "neoforge").Count;
        await WaitAsync(() => instance.HasConnectorPending == expected > 0, 15);
        Check("Connector 预先处理：提示和缓存一致", instance.HasConnectorPending == expected > 0 && instance.PrewarmConnectorCommand.CanExecute(null),
            $"还有 {expected} 个没转换；{(instance.HasConnectorPending ? instance.ConnectorPendingText : "不显示提示")}");

        var wrong = Path.Combine(instance.ModsDir!, "amber-selftest-wrongversion.jar");
        FakeJar(wrong, "META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"selftestold\"\n[[dependencies.selftestold]]\nmodId=\"minecraft\"\ntype=\"required\"\nversionRange=\"[1.20.4,1.20.5)\"\n");
        instance.SelectedId = null;
        instance.SelectedId = neo;
        instance.TabIndex = 1;
        await WaitAsync(() => instance.Mods.Any(m => m.Mod.FileName == "amber-selftest-wrongversion.jar" && m.Compat is not null), 15);
        var flagged = instance.Mods.FirstOrDefault(m => m.Mod.FileName == "amber-selftest-wrongversion.jar");
        Check("模组兼容：游戏版本不对的模组被标出", flagged is { HasCompatProblem: true, CanFindMatching: true } && flagged.CompatText.Contains("1.20.4"), flagged?.CompatText ?? "");
        Check("模组兼容：汇总里有不匹配数", instance.CompatSummary.Contains("不匹配"), instance.CompatSummary);
        await ShotAsync("模组兼容");
        await LayoutHereAsync("模组页");
        File.Delete(wrong);
        await FindMatchingAsync(instance, neo);

        instance.SelectedId = FabricName;
        instance.TabIndex = 1;
        var forgeOnFabric = Path.Combine(instance.ModsDir!, "amber-selftest-neoforge-only.jar");
        FakeJar(forgeOnFabric, "META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"selftestneo\"\n");
        instance.SelectedId = null;
        instance.SelectedId = FabricName;
        instance.TabIndex = 1;
        await WaitAsync(() => instance.Mods.Any(m => m.Mod.FileName == "amber-selftest-neoforge-only.jar" && m.Compat is not null), 15);
        var neoOnFabric = instance.Mods.FirstOrDefault(m => m.Mod.FileName == "amber-selftest-neoforge-only.jar");
        Check("模组兼容：Fabric 上的 NeoForge 模组说明没有兼容层", neoOnFabric?.CompatText.Contains("没有可靠的办法") == true, neoOnFabric?.CompatText ?? "");
        File.Delete(forgeOnFabric);
        instance.SelectedId = null;
        instance.SelectedId = FabricName;
    }

    /// <summary>“找对应版本”：放进一个真实的旧版本模组（FerriteCore 1.20.4 NeoForge），点按钮后应换成适合实例的版本。</summary>
    private async Task FindMatchingAsync(InstanceViewModel instance, string id)
    {
        const string ferriteCore = "uXXizFIs";
        using var http = Amber.Core.Net.Downloader.CreateHttpClient();
        var old = (await new Amber.Core.Content.ModrinthProvider(http).GetFilesAsync(ferriteCore, "1.20.4", "neoforge")).PickPreferred();
        if (old is null)
        {
            Log("SKIP 找对应版本：Modrinth 上没取到 FerriteCore 1.20.4 的文件");
            return;
        }
        var oldPath = Path.Combine(instance.ModsDir!, old.FileName);
        await File.WriteAllBytesAsync(oldPath, await http.GetByteArrayAsync(old.Url));
        var before = Directory.GetFiles(instance.ModsDir!).ToHashSet();
        instance.SelectedId = null;
        instance.SelectedId = id;
        instance.TabIndex = 1;
        await WaitAsync(() => instance.Mods.Any(m => m.Mod.FileName == old.FileName && m.Compat is not null), 15);
        var item = instance.Mods.FirstOrDefault(m => m.Mod.FileName == old.FileName);
        Check("找对应版本：真实的旧版本模组被标出", item is { CanFindMatching: true }, item?.CompatText ?? "没读到");
        if (item is not { CanFindMatching: true })
        {
            File.Delete(oldPath);
            return;
        }
        instance.FindMatchingCommand.Execute(item);
        await WaitAsync(() => !File.Exists(oldPath) && instance.Mods.Any(m => !before.Contains(m.Mod.Path) && m.Compat is not null), 60);
        var added = Directory.GetFiles(instance.ModsDir!).Where(f => !before.Contains(f)).ToList();
        var replacement = instance.Mods.FirstOrDefault(m => added.Contains(m.Mod.Path));
        Check("找对应版本：换成了适合实例的版本", !File.Exists(oldPath) && replacement is { HasCompatProblem: false },
            $"{old.FileName} → {replacement?.Mod.FileName ?? "无"}（{instance.Status}）");
        foreach (var f in added) File.Delete(f);
        if (File.Exists(oldPath)) File.Delete(oldPath);
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

        var importedVersion = new GameDirectory(GameRoot()).VersionJsonPath(PackName);
        var beforeCollision = await File.ReadAllBytesAsync(importedVersion);
        await content.ImportLocalPackAsync(renamed);
        var afterCollision = await File.ReadAllBytesAsync(importedVersion);
        Check("同名导入保留已有实例", content.Status.Contains(Amber.Core.Localization.L.T("core.install.targetexists"))
            && beforeCollision.SequenceEqual(afterCollision) && Directory.Exists(mods), content.Status);

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

    /// <summary>全局当前实例：实例页切换后启动页、资源页、联机页跟着变，写进设置；资源页临时换一个只算这一次。</summary>
    /// <summary>
    /// 新手卡（改版规格 3.1）：本机已经有账号和实例，所以把卡强制显示出来检查。
    /// 找目录只在报告目录里造的假 HMCL 目录里找；换目录后换回原来的测试目录；安装只补全测试目录里已有的原版 1.21.1。
    /// 不添加账号（不改账号文件）。
    /// </summary>
    private async Task OnboardingAsync()
    {
        var home = Page<HomeViewModel>();
        var service = home.Service;
        var onboarding = home.Onboarding;
        var originalDir = service.Settings.GameDirectory;
        var originalCurrent = service.CurrentInstance;
        main.CurrentPage = home;
        await Ui.FlushAsync();

        var fake = Path.Combine(reportDir, "fake-launcher", ".minecraft");
        Directory.CreateDirectory(Path.Combine(fake, "versions", "1.20.1"));
        File.WriteAllText(Path.Combine(fake, "versions", "1.20.1", "1.20.1.json"), "{}");
        onboarding.Candidates = () => [fake, originalDir];
        await Dispatcher.UIThread.InvokeAsync(() => onboarding.Update(true, true, null));
        await onboarding.DetectAsync();
        var card = await FindAsync<OnboardingCard>(_ => true, 5);
        Check("新手卡：没有账号和实例时替换实例卡，只展开“添加账号”这一步",
            card is not null && !home.ShowInstanceCard && onboarding.ShowAccountStep && !onboarding.ShowGameStep && onboarding.ShowGameWaiting,
            $"卡 {card is not null}，实例卡 {home.ShowInstanceCard}");
        Check("新手卡：找到已有游戏目录（跳过正在用的目录），写出实例数",
            onboarding.Detected?.Path == Path.GetFullPath(fake) && onboarding.Detected.InstanceCount == 1 && onboarding.DetectedText.Contains("1 个实例"),
            onboarding.DetectedText);

        onboarding.PlayerName = "ab";
        await Ui.FlushAsync();
        var add = card?.FindControl<Button>("AddOfflineButton");
        Check("新手卡：玩家名太短时禁用添加并说明", !onboarding.CanAddOffline && add?.IsEffectivelyEnabled == false && onboarding.NameProblem.Contains("3–16"), onboarding.NameProblem);
        onboarding.PlayerName = "小明 2";
        Check("新手卡：玩家名有空格时禁用添加", !onboarding.CanAddOffline && onboarding.NameProblem.Contains("空格"), onboarding.NameProblem);
        onboarding.PlayerName = "玩家小明";
        await Ui.FlushAsync();
        Check("新手卡：中文名只提醒，仍然能添加", onboarding.CanAddOffline && add?.IsEffectivelyEnabled == true && onboarding.NameWarning.Contains("服务器") && onboarding.NameProblem.Length == 0, onboarding.NameWarning);

        onboarding.RequestFocus(OnboardingStep.Account);
        var nameBox = card?.FindControl<TextBox>("PlayerNameBox");
        Check("新手卡：缺账号时点启动，焦点放到玩家名输入框", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => nameBox?.IsFocused == true), 3), "");
        await LayoutHereAsync("新手卡·账号");
        await ShotAsync("新手卡-账号");
        onboarding.PlayerName = "";

        // 账号有了，展开“安装游戏”。
        await Dispatcher.UIThread.InvokeAsync(() => onboarding.Update(false, true, "Steve"));
        await WaitAsync(() => onboarding.LatestRelease is not null, 20);
        Check("新手卡：账号做完收成一行，展开安装：最新正式版按钮、4 个热门版本、下载源",
            onboarding.ShowAccountDone && onboarding.ShowGameStep && onboarding.AccountDoneText.Contains("Steve")
            && onboarding.Hot.Length == 4 && onboarding.SourceText.StartsWith("下载源：", StringComparison.Ordinal),
            $"{onboarding.InstallLatestLabel} · {onboarding.SourceText}");
        Check("新手卡：拿到最新正式版号", onboarding.LatestRelease is { Length: > 0 } && onboarding.InstallLatestLabel.Contains(onboarding.LatestRelease!), onboarding.InstallLatestLabel);
        await LayoutHereAsync("新手卡·安装");
        await ShotAsync("新手卡-安装");

        // 切换下载源再切回来：设置跟着变，设置页也跟着显示。
        var settings = Page<SettingsViewModel>();
        var sourceBefore = service.Settings.DownloadSource;
        var mirrorBefore = onboarding.UsesMirror;
        onboarding.SwitchSourceCommand.Execute(null);
        await Ui.FlushAsync();
        var switched = onboarding.UsesMirror != mirrorBefore && settings.SourceIndex == Array.IndexOf(SettingsViewModel.SourceKeys, service.Settings.DownloadSource);
        onboarding.SwitchSourceCommand.Execute(null);
        service.Settings.DownloadSource = sourceBefore;
        service.SaveAndReload();
        await Ui.FlushAsync();
        Check("新手卡：【切换】换下载源，设置页跟着变", switched && onboarding.UsesMirror == mirrorBefore, onboarding.SourceText);

        // 补全已有的原版 1.21.1：阶段走到“完成”，Java 合并在同一条进度里，装完设为当前实例，卡收起。
        var stages = new List<string>();
        void OnStage(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OnboardingViewModel.StageText) && !stages.Contains(onboarding.StageText)) stages.Add(onboarding.StageText);
        }
        onboarding.PropertyChanged += OnStage;
        await onboarding.InstallAsync("1.21.1", null, fallbackToVanilla: false);
        onboarding.PropertyChanged -= OnStage;
        await WaitAsync(() => !onboarding.IsVisible, 5);
        Check("新手卡：安装阶段是 下载游戏文件 → 准备 Java → 完成，装完设为当前实例、卡收起",
            stages.SequenceEqual(["下载游戏文件", "准备 Java", "完成"]) && service.CurrentInstance == "1.21.1" && !onboarding.IsVisible && home.ShowInstanceCard
            && onboarding.Message?.Kind == StatusKind.Success,
            $"{string.Join(" → ", stages)}；当前 {service.CurrentInstance}；{onboarding.Message?.Text}");

        // 用找到的目录：换过去能看到里面的实例，再换回测试目录。
        await Dispatcher.UIThread.InvokeAsync(() => onboarding.Update(false, true, "Steve"));
        onboarding.UseDirectory(Path.GetDirectoryName(fake)!);
        await WaitAsync(() => settings.GameDirectory == Path.GetFullPath(fake), 5);
        var usedRoot = service.Directory.Root;
        var used = usedRoot == Path.GetFullPath(fake) && service.InstalledVersions().Contains("1.20.1") && settings.GameDirectory == Path.GetFullPath(fake);
        service.Settings.GameDirectory = originalDir;
        service.SaveAndReload();
        if (originalCurrent is not null) service.SetCurrentInstance(originalCurrent);
        await WaitAsync(() => home.Versions.Contains(FabricName) && settings.GameDirectory == originalDir, 10);
        Check("新手卡：选启动器文件夹也能认出里面的 .minecraft，换过去后换回测试目录",
            used && service.Directory.Root == Path.GetFullPath(originalDir) && settings.GameDirectory == originalDir && !onboarding.IsVisible,
            $"换过去 {used}（{usedRoot}）；现在 {service.Directory.Root}；设置页 {settings.GameDirectory}；卡 {onboarding.IsVisible}");
    }

    private async Task CurrentInstanceAsync()
    {
        var home = Page<HomeViewModel>();
        var instance = Page<InstanceViewModel>();
        var content = Page<ContentViewModel>();
        var multiplayer = Page<MultiplayerViewModel>();
        var service = home.Service;
        Check("当前实例：刚导入的整合包成为当前实例", service.CurrentInstance == "E2E-Prism", service.CurrentInstance ?? "");

        instance.Activate();
        await WaitAsync(() => instance.Instances.Contains(FabricName), 5);
        instance.SelectedId = FabricName;
        await WaitAsync(() => home.SelectedVersion == FabricName && content.TargetInstance == FabricName && multiplayer.TargetInstance == FabricName, 5);
        Check("当前实例：实例页切换后启动页、资源页、联机页都跟着变，并写进设置",
            home.SelectedVersion == FabricName && content.TargetInstance == FabricName && multiplayer.TargetInstance == FabricName
            && service.Settings.LastVersion == FabricName && !content.IsTemporaryTarget,
            $"启动 {home.SelectedVersion}，资源 {content.TargetInstance}，联机 {multiplayer.TargetInstance}");

        content.TargetInstance = "E2E-Pack";
        await Task.Delay(100);
        Check("当前实例：资源页临时换实例显示“仅本次”，不改当前实例",
            content.IsTemporaryTarget && service.CurrentInstance == FabricName && home.SelectedVersion == FabricName, service.CurrentInstance ?? "");

        home.SelectedVersion = "E2E-Pack";
        await WaitAsync(() => instance.SelectedId == "E2E-Pack" && content.TargetInstance == "E2E-Pack", 5);
        Check("当前实例：启动页切换后实例页跟着变，资源页回到当前实例",
            instance.SelectedId == "E2E-Pack" && content.TargetInstance == "E2E-Pack" && !content.IsTemporaryTarget, $"实例 {instance.SelectedId}");

        // 安装中途停下的实例（版本文件夹里留着标记）：启动页和实例页都提示“安装未完成”，标记去掉后提示消失。
        var note = L.T("instance.state.incomplete");
        service.Directory.MarkInstallStarted("E2E-Pack");
        home.SelectedVersion = FabricName;
        await WaitAsync(() => instance.SelectedId == FabricName, 5);
        home.SelectedVersion = "E2E-Pack";
        await WaitAsync(() => instance.SelectedId == "E2E-Pack" && instance.InfoText.Contains(note), 5);
        Check("实例状态：安装没走完的实例在启动页和实例页提示“安装未完成”",
            home.VersionInfo.Contains(note) && instance.InfoText.Contains(note), home.VersionInfo);
        service.Directory.MarkInstallFinished("E2E-Pack");
        home.SelectedVersion = FabricName;
        await WaitAsync(() => instance.SelectedId == FabricName, 5);
        home.SelectedVersion = "E2E-Pack";
        await WaitAsync(() => instance.SelectedId == "E2E-Pack" && !instance.InfoText.Contains(note), 5);
        Check("实例状态：标记去掉后不再提示", !home.VersionInfo.Contains(note) && !instance.InfoText.Contains(note), home.VersionInfo);

        // 关掉某个实例的“按实例分开文件夹”：实例页提示共用，资源页“装到”标成共享目录；改回后提示消失。
        var saved = InstanceSettings.Load(service.Directory, FabricName);
        var initiallyShared = service.EffectiveSettings(FabricName).Isolate != true;
        var shared = InstanceSettings.Load(service.Directory, FabricName);
        shared.Isolate = false;
        shared.Save(service.Directory, FabricName);
        try
        {
            home.SelectedVersion = FabricName;
            await WaitAsync(() => instance.SelectedId == FabricName && instance.IsSharedDir, 5);
            content.TypeIndex = (int)ContentType.Mod;
            content.TargetInstance = FabricName;
            await Task.Delay(100);
            Check("共享目录：关掉分开文件夹后实例页显示共用提示，资源页“装到”标成共享目录", instance.IsSharedDir && content.IsSharedTarget, instance.InfoText);
        }
        finally
        {
            saved.Save(service.Directory, FabricName);
        }
        instance.SelectedId = null;
        instance.SelectedId = FabricName;
        await WaitAsync(() => instance.Current is not null && instance.IsSharedDir == initiallyShared, 5);
        content.TargetInstance = null;
        content.TargetInstance = FabricName;
        Check("共享目录：改回后恢复原样", instance.IsSharedDir == initiallyShared && content.IsSharedTarget == initiallyShared, $"原来共用 {initiallyShared}");
        home.SelectedVersion = "E2E-Pack";
        await WaitAsync(() => instance.SelectedId == "E2E-Pack", 5);
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
