using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>资源页离线窗口验收：AMBER_SELFTEST=报告目录，AMBER_SELFTEST_SCOPE=content-layout。</summary>
internal sealed class ContentPageSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _lines = [];
    private readonly FixtureProvider _provider = new();
    private ContentViewModel _vm = null!;
    private Control _root = null!;
    private bool _hasKey = true;
    private bool _openedSettings;

    private void Log(string line)
    {
        _lines.Add(line);
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _lines);
    }

    private void Check(string name, bool ok, string detail = "") => Log($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
    private ContentPage Page => window.GetVisualDescendants().OfType<ContentPage>().Single(p => p.IsEffectivelyVisible);

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        var previous = main.CurrentPage;
        var index = main.Pages.IndexOf(main.Pages.OfType<ContentViewModel>().Single());
        var original = main.Pages[index];
        var language = L.Language;
        var pseudo = L.Pseudo;
        var theme = Application.Current?.RequestedThemeVariant;
        try
        {
            _root = (Control)window.Content!;
            var game = Path.Combine(reportDir, "game");
            WriteVersion(game, "1.21.1", """{"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
            WriteVersion(game, "layout-fabric", """{"id":"layout-fabric","inheritsFrom":"1.21.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
            using var service = new LauncherService(new AppSettings { GameDirectory = game, PlayerName = "", LastVersion = "1.21.1", IsolateVersions = true, SavePathForTest = Path.Combine(reportDir, "settings.json") });
            _vm = new ContentViewModel(service, _ => _provider, () => _hasKey) { NavigateToSettings = () => _openedSettings = true };
            main.Pages[index] = _vm;
            main.CurrentPage = _vm;
            L.Pseudo = false;
            L.SetLanguage("zh-Hans");
            App.ApplyTheme("dark");
            await SizeAsync(760, 440);
            Check("隔离原版和 Fabric 实例可选", _vm.Instances.Contains("1.21.1") && _vm.Instances.Contains("layout-fabric") && _vm.TargetInstance == "1.21.1");
            foreach (var size in new[] { (760d, 440d), (1280d, 800d) })
            {
                await SizeAsync(size.Item1, size.Item2);
                var tag = $"{size.Item1}x{size.Item2}";
                await ScenarioAsync("loading");
                Check($"加载状态 {tag}", _vm.IsSearching && _vm.Results.Count == 0);
                Shot($"loading-{tag}");
                await ScenarioAsync("empty");
                Shot($"empty-{tag}");
                _provider.Mode = "results";
                var emptyPressed = Press("ShowAllVersionsButton", $"空结果按钮 {tag}");
                await Task.Delay(80);
                Check($"取消过滤后保留目标实例 {tag}", emptyPressed && !_vm.FilterByInstance && _vm.Results.Count > 0 && _vm.TargetInstance == "1.21.1");
                await ScenarioAsync("error");
                Shot($"error-{tag}");
                _provider.Mode = "results";
                var retried = Press("RetrySearchButton", $"错误重试按钮 {tag}");
                await Task.Delay(80);
                Check($"重试恢复列表并保留实例 {tag}", retried && !_vm.IsSearchError && _vm.Results.Count > 0 && _vm.TargetInstance == "1.21.1");
                await ScenarioAsync("no-key");
                Shot($"no-key-{tag}");
                _openedSettings = false;
                var configured = Press("ConfigureCurseForgeButton", $"无密钥设置按钮 {tag}");
                Check($"设置导航触发 {tag}", configured && _openedSettings);
                var switched = Press("UseModrinthButton", $"无密钥切来源按钮 {tag}");
                await Task.Delay(80);
                Check($"改用 Modrinth 恢复列表 {tag}", switched && _vm.SourceIndex == 0 && !_vm.NeedsCurseForgeKey && _vm.Results.Count > 0);
            }
            foreach (var (code, isPseudo, tag) in L.Languages.Select(x => (x.Code, false, x.Code)).Append(("en", true, "pseudo")))
            {
                L.Pseudo = isPseudo;
                L.SetLanguage(code);
                foreach (var mode in new[] { "results", "error" })
                {
                    await ScenarioAsync(mode);
                    foreach (var (width, height, size) in LayoutCheck.Sizes)
                    {
                        await SizeAsync(width, height);
                        var issues = LayoutCheck.Find(_root, _root.Bounds.Size).Concat(LayoutCheck.FindAdaptive(_root, _root.Bounds.Size)).ToList();
                        Check($"布局 {tag}/{mode}/{size}", issues.Count == 0, string.Join("；", issues));
                        if (mode == "results")
                        {
                            var search = Page.FindControl<TextBox>("SearchInput")!;
                            var list = Page.FindControl<ListBox>("ResultsList")!;
                            var summary = Page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ProjectSummary" && t.IsEffectivelyVisible).ToList();
                            Check($"搜索与摘要行宽 {tag}/{size}", search.Bounds.Width >= Math.Min(200, Page.Bounds.Width - 380) && search.Bounds.Width <= 720 && summary.Count > 0 && summary.All(t => t.TextLayout.Width <= 760), $"搜索 {search.Bounds.Width:F0}px，最长摘要 {summary.Select(t => t.TextLayout.Width).DefaultIfEmpty().Max():F0}px");
                            Check($"主列表保留页面宽度 {tag}/{size}", list.Bounds.Width >= Page.Bounds.Width - 32 && list.Bounds.Width >= 200, $"列表 {list.Bounds.Width:F0}px / 页面 {Page.Bounds.Width:F0}px");
                        }
                        if (tag is "zh-Hans" or "pseudo" && LayoutCheck.ReportSizes.Contains(size)) Shot($"dark-{tag}-{mode}-{size}");
                    }
                }
            }
            L.Pseudo = false;
            L.SetLanguage("zh-Hans");
            foreach (var shotTheme in new[] { "dark", "light" })
            {
                App.ApplyTheme(shotTheme);
                foreach (var mode in new[] { "results", "error", "empty", "no-key", "loading" })
                {
                    await ScenarioAsync(mode);
                    foreach (var (w, h, size) in LayoutCheck.Sizes.Where(s => LayoutCheck.ReportSizes.Contains(s.Name)))
                    {
                        await SizeAsync(w, h);
                        Shot($"{shotTheme}-zh-Hans-{mode}-{size}");
                    }
                }
            }
            await ScenarioAsync("results");
            Check("未选文件时不能安装", !_vm.InstallCommand.CanExecute(null));
            Log("DONE");
        }
        catch (Exception e) { Log("FAIL 自检中断：" + e); }
        finally
        {
            _provider.Release();
            if (_root is not null) { _root.Width = double.NaN; _root.Height = double.NaN; _root.HorizontalAlignment = HorizontalAlignment.Stretch; _root.VerticalAlignment = VerticalAlignment.Stretch; }
            main.Pages[index] = original;
            main.CurrentPage = previous;
            L.Pseudo = pseudo;
            L.SetLanguage(language);
            if (Application.Current is { } app) app.RequestedThemeVariant = theme;
            window.Close();
        }
    }

    private async Task ScenarioAsync(string mode)
    {
        _provider.Release();
        _hasKey = mode != "no-key";
        _provider.Mode = mode == "no-key" ? "results" : mode;
        _vm.SourceIndex = mode == "no-key" ? 1 : 0;
        // 走真实回到资源页的刷新路径，密钥提示条和来源选项同时更新。
        _vm.IsCurrent = false;
        _vm.IsCurrent = true;
        _vm.FilterByInstance = true;
        _vm.Query = mode == "empty" ? "layout-missing" : "";
        var search = _vm.SearchCommand.ExecuteAsync(null);
        if (mode != "loading") await search;
        await Task.Delay(80);
        _root.UpdateLayout();
    }

    private async Task SizeAsync(double width, double height)
    {
        _root.HorizontalAlignment = HorizontalAlignment.Left;
        _root.VerticalAlignment = VerticalAlignment.Top;
        _root.Width = width;
        _root.Height = height;
        await Task.Delay(150);
        _root.UpdateLayout();
        if (Math.Abs(_root.Bounds.Width - width) > 1 || Math.Abs(_root.Bounds.Height - height) > 1)
            throw new InvalidOperationException($"布局尺寸不符：要求 {width}×{height}，实际 {_root.Bounds.Size}");
    }

    private bool Press(string name, string label)
    {
        var button = Page.FindControl<Button>(name);
        var point = button?.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), _root);
        var hit = point is { } p ? _root.InputHitTest(p) as Visual : null;
        var reachesButton = button is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } && hit is not null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button));
        Check(label + "中心命中", reachesButton, $"命中 {hit?.GetType().Name ?? "null"}，按钮 {button?.Bounds}");
        if (!reachesButton || ControlAutomationPeer.CreatePeerForElement(button!).GetProvider<IInvokeProvider>() is not { } invoke) return false;
        invoke.Invoke();
        return true;
    }

    private void Shot(string name)
    {
        var size = _root.Bounds.Size;
        var panel = _root as Panel;
        var background = panel?.Background;
        if (panel is not null && background is null) panel.Background = window.Background ?? Brushes.Black;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
        bitmap.Render(_root);
        bitmap.Save(Path.Combine(reportDir, name + ".png"));
        if (panel is not null) panel.Background = background;
    }

    private static void WriteVersion(string game, string id, string json)
    {
        var folder = Directory.CreateDirectory(Path.Combine(game, "versions", id));
        File.WriteAllText(Path.Combine(folder.FullName, id + ".json"), json);
    }

    private sealed class FixtureProvider : IContentProvider
    {
        public ContentSource Source => ContentSource.Modrinth;
        public string Mode { get; set; } = "results";
        private TaskCompletionSource<SearchResult>? _pending;
        private static SearchResult Results() => new(Enumerable.Range(1, 12).Select(i => new ContentProject(ContentSource.Modrinth, "layout-" + i, "Layout resource " + i,
            "A long resource description that explains supported game versions, available features, dependencies and installation requirements. This text must remain readable across narrow windows, large screens and translated user interface labels.", "Layout fixture", null, 123456, null)).ToArray(), 12);
        public void Release() { _pending?.TrySetResult(Results()); _pending = null; }
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => Mode switch
        {
            "loading" => (_pending ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct),
            "error" => Task.FromException<SearchResult>(new HttpRequestException("The resource service is unavailable. Check your connection and retry.")),
            "empty" => Task.FromResult(new SearchResult([], 0)),
            _ => Task.FromResult(Results()),
        };
        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string id, string? game, string? loader, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentFile>>([]);
        public Task<ContentProject> GetProjectAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentFile> GetFileAsync(string id, string file, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
