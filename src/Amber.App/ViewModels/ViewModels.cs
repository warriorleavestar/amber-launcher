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

    private StatusMessage? _statusInfo;

    /// <summary>
    /// 页面底部状态条显示的消息（null 时显示 DefaultStatus）。用 SetStatus 设：带翻译键和种类（成功/警告/错误），换语言时跟着换。
    /// </summary>
    public StatusMessage? StatusInfo => _statusInfo ?? DefaultStatus;

    /// <summary>
    /// 状态文字（StatusInfo 按当前语言格式化的结果）。直接赋字符串是旧写法：当作普通信息显示，不跟着换语言。
    /// </summary>
    public string Status
    {
        get => StatusInfo?.Text ?? "";
        set => SetStatus(string.IsNullOrEmpty(value) ? null : StatusMessage.Plain(value));
    }

    /// <summary>没有消息时显示的内容（比如首页的“准备就绪”）。</summary>
    protected virtual StatusMessage? DefaultStatus => null;

    public void SetStatus(StatusMessage? message)
    {
        _statusInfo = message;
        OnPropertyChanged(nameof(StatusInfo));
        OnPropertyChanged(nameof(Status));
        OnStatusChanged();
    }

    protected virtual void OnStatusChanged() { }
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
        var download = new DownloadViewModel(service);
        Pages =
        [
            home,
            new AccountsViewModel(service),
            instance,
            download,
            content,
            multiplayer,
            new ToolsViewModel(service),
            new SettingsViewModel(service),
            log,
        ];
        var accounts = Pages[1];
        home.NavigateToAccounts = () => CurrentPage = accounts;
        home.NavigateToNewInstance = () => CurrentPage = download;
        home.NavigateToLog = () => CurrentPage = log;
        home.NavigateHere = () => CurrentPage = home;
        instance.NavigateHome = () => CurrentPage = home;
        multiplayer.NavigateHome = () => CurrentPage = home;
        download.LaunchInstance = id =>
        {
            service.SetCurrentInstance(id);
            CurrentPage = home;
            service.RequestLaunch(new LaunchRequest(id));
        };
        download.OpenInstance = id =>
        {
            service.SetCurrentInstance(id);
            CurrentPage = instance;
        };
        download.BrowseModsFor = id =>
        {
            CurrentPage = content;
            content.BrowseModsFor(id);
        };
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
        SetStatus(StatusMessage.Success(dirChanged ? "settings.saved_directory_changed" : "common.saved"));
        if (dirChanged) _ = LoadJavaAsync(true);
    }
}

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message, GameLogLevel Severity)
{
    public string Time => Fmt.Time(Timestamp);
    public bool IsError => Severity >= GameLogLevel.Error;
    public bool IsWarning => Severity == GameLogLevel.Warn;
    public bool IsMuted => Severity <= GameLogLevel.Debug;
}

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
    private bool _clearPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorSummary))]
    public partial int ErrorCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningSummary))]
    public partial int WarningCount { get; private set; }

    public string ErrorSummary => L.F("log.errors", ErrorCount);
    public string WarningSummary => L.F("log.warnings", WarningCount);
    public string EmptyMessage => L.T(_all.Count > 0 && OnlyProblems ? "log.empty_filtered" : "log.empty");

    partial void OnOnlyProblemsChanged(bool value) => Rebuild();

    protected override void OnLanguageChanged() => Rebuild();

    public void Add(GameLogLine line)
    {
        var level = LogLevelParser.Parse(line.Message, line.Level);
        Post(new LogEntry(line.Time, level.ToString().ToUpperInvariant(), line.Message, level));
    }

    public void AddRaw(string message)
    {
        var level = LogLevelParser.Parse(message);
        Post(new LogEntry(DateTimeOffset.Now, level == GameLogLevel.Info ? "AMBER" : level.ToString().ToUpperInvariant(), message, level));
    }

    public void Clear()
    {
        lock (_pending)
        {
            _pending.Clear();
            _clearPending = true;
        }
        // 清空和新到的日志在同一批中按顺序处理，旧的延迟回调不能把它们混在一起。
        Dispatcher.UIThread.Post(Flush);
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

    internal void Flush()
    {
        List<LogEntry> batch;
        bool clear;
        lock (_pending)
        {
            batch = [.. _pending];
            _pending.Clear();
            _flushQueued = false;
            clear = _clearPending;
            _clearPending = false;
        }
        if (clear)
        {
            _all.Clear();
            Lines.Clear();
            ErrorCount = WarningCount = Badge = 0;
            OnPropertyChanged(nameof(EmptyMessage));
        }
        if (batch.Count == 0) return;

        var previousCount = _all.Count;
        _all.AddRange(batch);
        var removed = Math.Max(0, _all.Count - MaxLines);
        if (removed > 0)
        {
            var visibleRemoved = _all.Take(Math.Min(removed, previousCount)).Count(Visible);
            for (var i = 0; i < visibleRemoved; i++) Lines.RemoveAt(0);
            _all.RemoveRange(0, removed);
        }
        foreach (var entry in batch.Skip(Math.Max(0, removed - previousCount)).Where(Visible)) Lines.Add(entry);
        ErrorCount = _all.Count(e => e.IsError);
        WarningCount = _all.Count(e => e.IsWarning);
        if (!IsCurrent) Badge += batch.Count(e => e.IsError);
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool Visible(LogEntry e) => !OnlyProblems || e.Severity >= GameLogLevel.Warn;

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var e in _all.Where(Visible)) Lines.Add(e);
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = string.Join(Environment.NewLine, _all.Select(e => $"[{e.Time}] [{e.Level}] {e.Message}"));
        var ok = false;
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            {
                await clipboard.SetTextAsync(text);
                ok = true;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        CopyLabel = L.T(ok ? "common.copied" : "log.copy_failed");
        await Task.Delay(1500);
        CopyLabel = L.T("log.copy_all");
    }

    /// <summary>“复制全部”按钮的文字：点了之后短暂显示“已复制”或“复制失败”。</summary>
    [ObservableProperty]
    public partial string CopyLabel { get; private set; } = L.T("log.copy_all");

    // ---- 这次运行的实例和开始时间（页头小字） ----

    private string? _runInstance;
    private DateTimeOffset _runStartedAt;

    /// <summary>游戏开始启动时调用（在 Clear 之后）：页头显示“实例 · 开始时间”。</summary>
    public void BeginRun(string instance, DateTimeOffset startedAt)
    {
        _runInstance = instance;
        _runStartedAt = startedAt;
        OnPropertyChanged(nameof(RunSummary));
        OnPropertyChanged(nameof(HasRun));
    }

    public bool HasRun => _runInstance is not null;
    public string RunSummary => _runInstance is null ? "" : L.F("log.run_summary", _runInstance, Fmt.Time(_runStartedAt));

    // ---- 跳到下一个错误 / 警告 ----

    /// <summary>LogPage 订阅：滚动到这一行并选中它。</summary>
    public event Action<LogEntry>? JumpRequested;

    [ObservableProperty]
    public partial LogEntry? SelectedLine { get; set; }

    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;

    partial void OnErrorCountChanged(int value) => OnPropertyChanged(nameof(HasErrors));
    partial void OnWarningCountChanged(int value) => OnPropertyChanged(nameof(HasWarnings));

    [RelayCommand]
    private void NextError() => JumpTo(NextMatch(_all, SelectedLine, e => e.IsError));

    [RelayCommand]
    private void NextWarning() => JumpTo(NextMatch(_all, SelectedLine, e => e.IsWarning));

    private void JumpTo(LogEntry? target)
    {
        if (target is null) return;
        // 跳转时不再跟随最新，不然马上又滚回底部；筛选把它藏起来时（理论上不会，错误和警告都在筛选里）先关掉筛选。
        FollowLatest = false;
        if (!Visible(target)) OnlyProblems = false;
        SelectedLine = target;
        JumpRequested?.Invoke(target);
    }

    /// <summary>从 current 之后找下一个满足条件的行，到末尾回到开头；current 不在列表里时从头找。</summary>
    internal static LogEntry? NextMatch(IReadOnlyList<LogEntry> lines, LogEntry? current, Func<LogEntry, bool> match)
    {
        if (lines.Count == 0) return null;
        var start = current is null ? -1 : IndexOf(lines, current);
        for (var step = 1; step <= lines.Count; step++)
        {
            var entry = lines[(start + step + lines.Count) % lines.Count];
            if (match(entry)) return entry;
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<LogEntry> lines, LogEntry entry)
    {
        for (var i = 0; i < lines.Count; i++) if (ReferenceEquals(lines[i], entry)) return i;
        return -1;
    }
}
