using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.App.Services;
using Amber.Core.Launch;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel()
    {
        // 换语言时让这个页面的所有绑定重新取值（标题、下拉选项这些在 C# 里生成的文字跟着换）。
        Amber.Core.Localization.L.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnLanguageChanged();
            OnPropertyChanged((string?)null);
        });
    }

    /// <summary>换语言后（界面线程上）：把存成文字的内容（汇总、提示）按新语言重新算一遍。</summary>
    protected virtual void OnLanguageChanged() { }

    public abstract string Title { get; }

    /// <summary>导航图标的路径数据（24×24）。</summary>
    public abstract string Icon { get; }

    private Geometry? _iconGeometry;
    public Geometry IconGeometry => _iconGeometry ??= Geometry.Parse(Icon);

    private bool _activated;
    private int _badge;
    private bool _isCurrent;

    /// <summary>导航栏上的未读数，0 不显示。</summary>
    public int Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value)) OnPropertyChanged(nameof(HasBadge));
        }
    }

    public bool HasBadge => Badge > 0;

    /// <summary>是不是当前显示的页面。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value) && value) Badge = 0;
        }
    }

    /// <summary>页面第一次显示时调用一次。启动时不联网、不扫描，都放在这里。</summary>
    public void Activate()
    {
        if (_activated) return;
        _activated = true;
        OnFirstActivated();
    }

    protected virtual void OnFirstActivated() { }
}

/// <summary>开发用：截图模式下让页面走一遍真实流程。</summary>
public interface ISnapshotDemo
{
    Task RunSnapshotDemoAsync();
}

public static class Ui
{
    /// <summary>等已排队的进度回调跑完，这样最后写的状态文字不会被迟到的进度盖掉。</summary>
    public static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}

public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    public string VersionText => L.F("main.version", UpdateChecker.CurrentVersion);

    partial void OnCurrentPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsCurrent = false;
        newValue.IsCurrent = true;
        newValue.Activate();
    }

    public MainViewModel(LauncherService service)
    {
        L.Changed += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(VersionText)));
        var log = new LogViewModel();
        var home = new HomeViewModel(service, log);
        var instance = new InstanceViewModel(service) { NavigateHome = null };
        var content = new ContentViewModel(service);
        var multiplayer = new MultiplayerViewModel(service);
        Pages =
        [
            home,
            new AccountsViewModel(service),
            instance,
            new DownloadViewModel(service),
            content,
            multiplayer,
            new ToolsViewModel(service),
            new SettingsViewModel(service),
            log,
        ];
        instance.NavigateHome = () => CurrentPage = home;
        multiplayer.NavigateHome = () => CurrentPage = home;
        instance.NavigateToDatapacks = (id, world) =>
        {
            CurrentPage = content;
            content.BrowseDatapacksFor(id, world);
        };
        CurrentPage = Pages[0];
        CurrentPage.Activate();
    }
}

public sealed record JavaOption(string Label, string? Path);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly LauncherService _service;

    public override string Title => L.T("instance.settings.title");
    public override string Icon => "M4 6h8v2H4zM16 6h4v2h-4zM12 4h4v6h-4zM4 16h4v2H4zM12 16h8v2h-8zM8 14h4v6H8z";

    /// <summary>下拉框的选项列表本身不变，换语言时只换文字（不然选中的序号会被清成 -1、保存时丢设置）。</summary>
    public Amber.App.Localization.OptionItem[] SourceOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.source.auto", "settings.source.official", "settings.source.mirror");
    private static readonly string[] SourceKeys = ["auto", "official", "mirror"];

    public ObservableCollection<JavaOption> JavaOptions { get; } = [];

    [ObservableProperty] public partial string GameDirectory { get; set; }
    [ObservableProperty] public partial int SourceIndex { get; set; }
    [ObservableProperty] public partial string Concurrency { get; set; }
    [ObservableProperty] public partial JavaOption? SelectedJava { get; set; }
    [ObservableProperty] public partial bool AutoJavaDownload { get; set; }
    [ObservableProperty] public partial bool IsolateVersions { get; set; }
    [ObservableProperty] public partial string WindowWidth { get; set; }
    [ObservableProperty] public partial string WindowHeight { get; set; }
    [ObservableProperty] public partial bool Fullscreen { get; set; }
    [ObservableProperty] public partial string JvmArgs { get; set; }
    [ObservableProperty] public partial string GameArgs { get; set; }
    [ObservableProperty] public partial string PreLaunch { get; set; }
    [ObservableProperty] public partial string PostExit { get; set; }
    [ObservableProperty] public partial string CurseForgeKey { get; set; }
    [ObservableProperty] public partial string CurseForgeRoot { get; set; }
    [ObservableProperty] public partial string UpdateFeed { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    public string SettingsPath => AppSettings.FilePath;
    public string SettingsPathLabel => L.F("settings.file_path", SettingsPath);

    public SettingsViewModel(LauncherService service)
    {
        _service = service;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            var automatic = JavaOptions.FirstOrDefault(o => o.Path is null);
            if (automatic is null) return;
            var selected = SelectedJava == automatic;
            var replacement = new JavaOption(L.T("settings.java.automatic"), null);
            JavaOptions[JavaOptions.IndexOf(automatic)] = replacement;
            if (selected) SelectedJava = replacement;
        });
        var s = service.Settings;
        GameDirectory = s.GameDirectory;
        SourceIndex = Math.Max(0, Array.IndexOf(SourceKeys, s.DownloadSource));
        Concurrency = s.DownloadConcurrency.ToString();
        AutoJavaDownload = s.AutoJavaDownload;
        IsolateVersions = s.IsolateVersions;
        WindowWidth = s.WindowWidth?.ToString() ?? "";
        WindowHeight = s.WindowHeight?.ToString() ?? "";
        Fullscreen = s.Fullscreen;
        JvmArgs = s.JvmArgs ?? "";
        GameArgs = s.GameArgs ?? "";
        PreLaunch = s.PreLaunchCommand ?? "";
        PostExit = s.PostExitCommand ?? "";
        CurseForgeKey = s.CurseForgeApiKey ?? "";
        CurseForgeRoot = s.CurseForgeApiRoot ?? "";
        UpdateFeed = s.UpdateFeedUrl ?? "";
        SelectedThemeIndex = Math.Max(0, Array.IndexOf(ThemeKeys, s.Theme));
        // 启动时只把选单对上已经生效的语言，不再重新应用、不写设置文件。
        _initializing = true;
        SelectedLanguageIndex = Math.Max(0, Array.IndexOf(LanguageKeys, Amber.Core.Localization.L.Resolve(s.Language) ?? "system"));
        _initializing = false;
    }

    private bool _initializing;

    /// <summary>界面语言：第一项跟随系统，其余每种语言用自己的文字写名字（不管现在是什么语言都认得出来）。</summary>
    public Amber.App.Localization.OptionItem[] LanguageOptions { get; } =
        [Amber.App.Localization.OptionItem.Key("settings.language.system"), .. Amber.Core.Localization.L.Languages.Select(l => Amber.App.Localization.OptionItem.Fixed(l.Name))];
    private static readonly string[] LanguageKeys = ["system", .. Amber.Core.Localization.L.Languages.Select(l => l.Code)];

    [ObservableProperty] public partial int SelectedLanguageIndex { get; set; }

    /// <summary>选了就立刻换（不用点保存），同时记进设置。</summary>
    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_initializing || value < 0 || value >= LanguageKeys.Length) return;
        var code = LanguageKeys[Math.Clamp(value, 0, LanguageKeys.Length - 1)];
        _service.Settings.Language = code == "system" ? null : code;
        Amber.Core.Localization.L.SetLanguage(_service.Settings.Language);
        try { _service.Settings.Save(); } catch (IOException) { }
    }

    protected override void OnFirstActivated() => _ = LoadJavaAsync(false);

    public Amber.App.Localization.OptionItem[] ThemeOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.theme.system", "settings.theme.light", "settings.theme.dark");
    private static readonly string[] ThemeKeys = ["system", "light", "dark"];

    [ObservableProperty] public partial int SelectedThemeIndex { get; set; }

    [RelayCommand]
    private Task RefreshJavaAsync() => LoadJavaAsync(true);

    private async Task LoadJavaAsync(bool refresh)
    {
        var current = _service.Settings.JavaPath;
        var found = await _service.Java.ScanAsync(refresh);
        JavaOptions.Clear();
        JavaOptions.Add(new JavaOption(L.T("settings.java.automatic"), null));
        foreach (var j in found) JavaOptions.Add(new JavaOption(j.ToString(), j.JavaPath));
        SelectedJava = JavaOptions.FirstOrDefault(o => o.Path == current) ?? JavaOptions[0];
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [RelayCommand]
    private void Save()
    {
        var s = _service.Settings;
        var dirChanged = !string.Equals(s.GameDirectory, GameDirectory.Trim(), StringComparison.OrdinalIgnoreCase);
        s.GameDirectory = GameDirectory.Trim();
        if (SourceIndex >= 0) s.DownloadSource = SourceKeys[Math.Min(SourceIndex, SourceKeys.Length - 1)];
        s.DownloadConcurrency = int.TryParse(Concurrency, out var c) ? Math.Clamp(c, 1, 128) : 32;
        Concurrency = s.DownloadConcurrency.ToString();
        s.JavaPath = SelectedJava?.Path;
        s.AutoJavaDownload = AutoJavaDownload;
        s.IsolateVersions = IsolateVersions;
        s.WindowWidth = int.TryParse(WindowWidth, out var w) && w > 0 ? w : null;
        s.WindowHeight = int.TryParse(WindowHeight, out var h) && h > 0 ? h : null;
        s.Fullscreen = Fullscreen;
        s.JvmArgs = Blank(JvmArgs);
        s.GameArgs = Blank(GameArgs);
        s.PreLaunchCommand = Blank(PreLaunch);
        s.PostExitCommand = Blank(PostExit);
        s.CurseForgeApiKey = Blank(CurseForgeKey);
        s.CurseForgeApiRoot = Blank(CurseForgeRoot);
        s.UpdateFeedUrl = Blank(UpdateFeed);
        if (SelectedThemeIndex >= 0) s.Theme = ThemeKeys[Math.Min(SelectedThemeIndex, ThemeKeys.Length - 1)];
        App.ApplyTheme(s.Theme);
        s.Save();
        _service.Reload();
        Status = dirChanged ? L.T("settings.saved_directory_changed") : L.T("common.saved");
        if (dirChanged) _ = LoadJavaAsync(true);
    }
}

public sealed record LogEntry(string Time, string Level, string Message, GameLogLevel Severity);

public sealed partial class LogViewModel : PageViewModel
{
    private const int MaxLines = 5000;

    public override string Title => L.T("log.title");
    public override string Icon => "M4 5h16v2H4zM4 11h16v2H4zM4 17h11v2H4z";

    public ObservableCollection<LogEntry> Lines { get; } = [];

    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }

    [ObservableProperty]
    public partial bool FollowLatest { get; set; } = true;

    private readonly List<LogEntry> _all = [];
    private readonly List<LogEntry> _pending = [];
    private bool _flushQueued;

    partial void OnOnlyProblemsChanged(bool value) => Rebuild();

    public void Add(GameLogLine line) => Post(new LogEntry(line.Time.ToString("HH:mm:ss"), line.Level.ToString().ToUpperInvariant(), line.Message, line.Level));

    public void AddRaw(string message) => Post(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), "AMBER", message, GameLogLevel.Info));

    public void Clear()
    {
        lock (_pending) _pending.Clear();
        Dispatcher.UIThread.Post(() => { _all.Clear(); Lines.Clear(); });
    }

    /// <summary>模组加载时日志一秒上千行，攒成一批再进界面，不然界面会卡。</summary>
    private void Post(LogEntry entry)
    {
        lock (_pending)
        {
            _pending.Add(entry);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        // 游戏输出来自后台线程，这里不能用 DispatcherTimer（它绑定当前线程的调度器）。
        _ = Task.Delay(100).ContinueWith(_ => Dispatcher.UIThread.Post(Flush), TaskScheduler.Default);
    }

    private void Flush()
    {
        List<LogEntry> batch;
        lock (_pending)
        {
            batch = [.. _pending];
            _pending.Clear();
            _flushQueued = false;
        }
        _all.AddRange(batch);
        if (_all.Count > MaxLines) _all.RemoveRange(0, _all.Count - MaxLines);
        foreach (var entry in batch.Where(Visible)) Lines.Add(entry);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    private bool Visible(LogEntry e) => !OnlyProblems || e.Severity >= GameLogLevel.Warn;

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var e in _all.Where(Visible)) Lines.Add(e);
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = string.Join(Environment.NewLine, _all.Select(e => $"[{e.Time}] [{e.Level}] {e.Message}"));
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            await clipboard.SetTextAsync(text);
    }
}
