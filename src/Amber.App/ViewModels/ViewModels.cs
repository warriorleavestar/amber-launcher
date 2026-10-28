using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Launch;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public abstract string Icon { get; }
}

public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    public MainViewModel(LauncherService service)
    {
        var log = new LogViewModel();
        Pages =
        [
            new HomeViewModel(service, log),
            new DownloadViewModel(service),
            new SettingsViewModel(service),
            log,
        ];
        CurrentPage = Pages[0];
    }
}

/// <summary>启动页：选版本、填玩家名、启动。</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    private readonly LauncherService _service;
    private readonly LogViewModel _log;
    private CancellationTokenSource? _cts;
    private Process? _game;

    public override string Title => "启动";
    public override string Icon => "▶";

    public ObservableCollection<string> Versions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    public partial string? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    public partial string PlayerName { get; set; }

    [ObservableProperty]
    public partial int MaxMemoryMb { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "准备就绪";

    public int TotalMemoryMb { get; } = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);

    public HomeViewModel(LauncherService service, LogViewModel log)
    {
        _service = service;
        _log = log;
        PlayerName = service.Settings.PlayerName;
        MaxMemoryMb = service.Settings.MaxMemoryMb;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshVersions);
        RefreshVersions();
    }

    private void RefreshVersions()
    {
        var keep = SelectedVersion ?? _service.Settings.LastVersion;
        Versions.Clear();
        foreach (var v in _service.InstalledVersions()) Versions.Add(v);
        SelectedVersion = keep is not null && Versions.Contains(keep) ? keep : Versions.FirstOrDefault();
        if (Versions.Count == 0) Status = "还没有安装任何版本，去「下载」页装一个吧";
    }

    partial void OnMaxMemoryMbChanged(int value)
    {
        _service.Settings.MaxMemoryMb = value;
        _service.Settings.Save();
    }

    private bool CanLaunch() => !IsBusy && SelectedVersion is not null && !string.IsNullOrWhiteSpace(PlayerName);

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        var id = SelectedVersion!;
        var settings = _service.Settings;
        settings.PlayerName = PlayerName.Trim();
        settings.LastVersion = id;
        settings.Save();

        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            Status = "正在检查游戏文件……";
            await _service.Installer.CompleteAsync(id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
                {
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    Status = $"补全文件 {p.Files.CompletedFiles}/{p.Files.TotalFiles}";
                }
            }), _cts.Token);

            var version = _service.Directory.ResolveVersion(id);
            var required = version.JavaVersion?.MajorVersion ?? 8;
            var javaPath = settings.JavaPath;
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                var java = JavaFinder.Pick(await _service.GetJavaAsync(), required)
                           ?? throw new InvalidOperationException($"没有找到 Java {required} 或更高版本，请先安装 Java {required}");
                javaPath = java.JavaPath;
            }

            var options = new LaunchOptions
            {
                VersionId = id,
                JavaPath = javaPath,
                Session = OfflineAccount.Create(settings.PlayerName),
                MaxMemoryMb = MaxMemoryMb,
                WorkingDirectory = settings.IsolateVersions ? _service.Directory.VersionDir(id) : null,
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
            };

            Status = "正在启动……";
            Progress = 100;
            _log.Clear();
            _log.AddRaw($"[Amber] 启动 {id}，Java：{javaPath}");
            var parser = new GameLogParser();
            var launcher = new GameLauncher(_service.Directory);
            _game = launcher.Launch(options, line =>
            {
                lock (parser)
                {
                    if (parser.Feed(line) is { } entry) _log.Add(entry);
                }
            });
            Status = $"{id} 运行中";
            await _game.WaitForExitAsync();
            Status = _game.ExitCode == 0 ? "游戏已正常退出" : $"游戏异常退出（代码 {_game.ExitCode}），详情见「日志」页";
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception e)
        {
            Status = "启动失败：" + e.Message;
            _log.AddRaw("[Amber] " + e);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _game = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        if (_game is { HasExited: false } game) game.Kill(true);
    }
}

/// <summary>下载页的一行。</summary>
public sealed partial class RemoteVersionItem(ManifestEntry entry, bool installed) : ObservableObject
{
    public ManifestEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string TypeText => Entry.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        var t => t,
    };
    public string Date => Entry.ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");

    [ObservableProperty]
    public partial bool Installed { get; set; } = installed;
}

public sealed partial class DownloadViewModel : PageViewModel
{
    private readonly LauncherService _service;
    private List<ManifestEntry> _all = [];

    public override string Title => "下载";
    public override string Icon => "⬇";

    public string[] Filters { get; } = ["正式版", "快照", "全部"];
    public ObservableCollection<RemoteVersionItem> Items { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "正式版";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public DownloadViewModel(LauncherService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Status = "正在获取版本列表……";
        try
        {
            var manifest = await _service.Installer.GetManifestAsync();
            _all = manifest.Versions;
            ApplyFilter();
            Status = $"最新正式版 {manifest.Latest.GetValueOrDefault("release")}，最新快照 {manifest.Latest.GetValueOrDefault("snapshot")}";
        }
        catch (Exception e)
        {
            Status = "获取版本列表失败：" + e.Message;
        }
    }

    private void ApplyFilter()
    {
        var installed = _service.InstalledVersions().ToHashSet();
        IEnumerable<ManifestEntry> list = Filter switch
        {
            "正式版" => _all.Where(v => v.Type == "release"),
            "快照" => _all.Where(v => v.Type == "snapshot"),
            _ => _all,
        };
        if (!string.IsNullOrWhiteSpace(Search)) list = list.Where(v => v.Id.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
        Items.Clear();
        foreach (var v in list) Items.Add(new RemoteVersionItem(v, installed.Contains(v.Id)));
    }

    private bool CanInstall(RemoteVersionItem? item) => !IsBusy && item is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(RemoteVersionItem? item)
    {
        if (item is null) return;
        IsBusy = true;
        var watch = Stopwatch.StartNew();
        try
        {
            Status = $"正在安装 {item.Id}……";
            await _service.Installer.InstallAsync(item.Id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage != InstallStage.Files || p.Files.TotalFiles == 0) return;
                Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                var mb = p.Files.BytesDownloaded / 1024.0 / 1024;
                Status = $"安装 {item.Id}：{p.Files.CompletedFiles}/{p.Files.TotalFiles} 个文件，{mb:F1} MB，{mb / Math.Max(watch.Elapsed.TotalSeconds, 0.1):F1} MB/s";
            }));
            item.Installed = true;
            Status = $"{item.Id} 安装完成，用时 {watch.Elapsed.TotalSeconds:F0} 秒";
            _service.NotifyInstalledChanged();
        }
        catch (Exception e)
        {
            Status = $"安装 {item.Id} 失败：{e.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }
}

public sealed record JavaOption(string Label, string? Path);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly LauncherService _service;

    public override string Title => "设置";
    public override string Icon => "⚙";

    public string[] SourceOptions { get; } = ["自动（按时区选择）", "官方优先", "BMCLAPI 镜像优先"];
    private static readonly string[] SourceKeys = ["auto", "official", "mirror"];

    public ObservableCollection<JavaOption> JavaOptions { get; } = [];

    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    [ObservableProperty]
    public partial int SourceIndex { get; set; }

    [ObservableProperty]
    public partial JavaOption? SelectedJava { get; set; }

    [ObservableProperty]
    public partial bool IsolateVersions { get; set; }

    [ObservableProperty]
    public partial string WindowWidth { get; set; }

    [ObservableProperty]
    public partial string WindowHeight { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public string SettingsPath => AppSettings.FilePath;

    public SettingsViewModel(LauncherService service)
    {
        _service = service;
        var s = service.Settings;
        GameDirectory = s.GameDirectory;
        SourceIndex = Math.Max(0, Array.IndexOf(SourceKeys, s.DownloadSource));
        IsolateVersions = s.IsolateVersions;
        WindowWidth = s.WindowWidth?.ToString() ?? "";
        WindowHeight = s.WindowHeight?.ToString() ?? "";
        _ = LoadJavaAsync(false);
    }

    [RelayCommand]
    private Task RefreshJavaAsync() => LoadJavaAsync(true);

    private async Task LoadJavaAsync(bool refresh)
    {
        var current = _service.Settings.JavaPath;
        var found = await _service.GetJavaAsync(refresh);
        JavaOptions.Clear();
        JavaOptions.Add(new JavaOption("自动选择（按游戏版本要求）", null));
        foreach (var j in found) JavaOptions.Add(new JavaOption(j.ToString(), j.JavaPath));
        SelectedJava = JavaOptions.FirstOrDefault(o => o.Path == current) ?? JavaOptions[0];
    }

    [RelayCommand]
    private void Save()
    {
        var s = _service.Settings;
        var dirChanged = !string.Equals(s.GameDirectory, GameDirectory.Trim(), StringComparison.OrdinalIgnoreCase);
        s.GameDirectory = GameDirectory.Trim();
        s.DownloadSource = SourceKeys[Math.Clamp(SourceIndex, 0, SourceKeys.Length - 1)];
        s.JavaPath = SelectedJava?.Path;
        s.IsolateVersions = IsolateVersions;
        s.WindowWidth = int.TryParse(WindowWidth, out var w) && w > 0 ? w : null;
        s.WindowHeight = int.TryParse(WindowHeight, out var h) && h > 0 ? h : null;
        s.Save();
        _service.Reload();
        Status = dirChanged ? "已保存，已切换到新的游戏目录" : "已保存";
        if (dirChanged) _ = LoadJavaAsync(true);
    }
}

public sealed record LogEntry(string Time, string Level, string Message, GameLogLevel Severity);

public sealed partial class LogViewModel : PageViewModel
{
    private const int MaxLines = 5000;

    public override string Title => "日志";
    public override string Icon => "☰";

    public ObservableCollection<LogEntry> Lines { get; } = [];

    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }

    private readonly List<LogEntry> _all = [];

    partial void OnOnlyProblemsChanged(bool value) => Rebuild();

    public void Add(GameLogLine line) => Post(new LogEntry(line.Time.ToString("HH:mm:ss"), line.Level.ToString().ToUpperInvariant(), line.Message, line.Level));

    public void AddRaw(string message) => Post(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), "AMBER", message, GameLogLevel.Info));

    public void Clear() => Dispatcher.UIThread.Post(() => { _all.Clear(); Lines.Clear(); });

    private void Post(LogEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        _all.Add(entry);
        if (_all.Count > MaxLines) _all.RemoveRange(0, _all.Count - MaxLines);
        if (Visible(entry))
        {
            Lines.Add(entry);
            if (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
    });

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
