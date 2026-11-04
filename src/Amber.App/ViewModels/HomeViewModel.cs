using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>启动页：选版本、账号、启动，退出后给出崩溃分析。</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    private const int KeptOutputLines = 400;

    private readonly LauncherService _service;
    private readonly LogViewModel _log;
    private readonly Queue<string> _recentOutput = new();
    private CancellationTokenSource? _cts;
    private Process? _game;
    private bool _stoppedByUser;
    private List<string> _lastRunFiles = [];
    private bool _refreshingAccounts;

    public override string Title => L.T("home.title");
    public override string Icon => "M8 5v14l11-7z";

    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    public partial string? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    public partial Account? SelectedAccount { get; set; }

    [ObservableProperty]
    public partial bool AutoMemory { get; set; }

    [ObservableProperty]
    public partial int MaxMemoryMb { get; set; }

    [ObservableProperty]
    public partial string VersionInfo { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = L.T("home.status.ready");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrash))]
    public partial CrashAnalysis? Crash { get; set; }

    public bool HasCrash => Crash is not null;

    public int TotalMemoryMb { get; } = (int)MemoryPlanner.TotalPhysicalMb();

    public HomeViewModel(LauncherService service, LogViewModel log)
    {
        _service = service;
        _log = log;
        AutoMemory = service.Settings.AutoMemory;
        MaxMemoryMb = service.Settings.MaxMemoryMb;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshVersions);
        service.AccountsChanged += RefreshAccounts;
        service.LaunchRequested += request => Dispatcher.UIThread.Post(() => _ = RunAsync(request));
        RefreshVersions();
        RefreshAccounts();
    }

    private void RefreshAccounts()
    {
        _refreshingAccounts = true;
        Accounts.Clear();
        foreach (var a in _service.Accounts.Store.Accounts) Accounts.Add(a);
        SelectedAccount = _service.Accounts.Store.Selected;
        _refreshingAccounts = false;
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        if (_refreshingAccounts || value is null || value.Id == _service.Accounts.Store.SelectedId) return;
        _service.Accounts.Store.SelectedId = value.Id;
        _service.Accounts.Store.Save();
        _service.NotifyAccountsChanged();
    }

    private void RefreshVersions()
    {
        var keep = SelectedVersion ?? _service.Settings.LastVersion;
        Versions.Clear();
        foreach (var v in _service.InstalledVersions()) Versions.Add(v);
        SelectedVersion = keep is not null && Versions.Contains(keep) ? keep : Versions.FirstOrDefault();
        if (Versions.Count == 0) Status = L.T("home.status.no_versions");
    }

    partial void OnSelectedVersionChanged(string? value)
    {
        VersionInfo = "";
        if (value is null) return;
        try
        {
            var instance = _service.GetInstance(value);
            var loader = instance.Loader is null ? L.T("home.version.vanilla") : $"{instance.Loader} {instance.LoaderVersion}";
            var mods = new ModManager(instance.ModsDir).CountEnabled();
            VersionInfo = L.F("home.version.info", instance.McVersion, loader, mods > 0 ? L.F("home.version.mod_count", mods) : "");
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            VersionInfo = L.F("home.version.read_failed", e.Message);
        }
    }

    partial void OnMaxMemoryMbChanged(int value)
    {
        _service.Settings.MaxMemoryMb = value;
        _service.Settings.Save();
    }

    partial void OnAutoMemoryChanged(bool value)
    {
        _service.Settings.AutoMemory = value;
        _service.Settings.Save();
    }

    private bool CanLaunch() => !IsBusy && SelectedVersion is not null && SelectedAccount is not null;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private Task LaunchAsync() => RunAsync(new LaunchRequest(SelectedVersion!));

    private async Task RunAsync(LaunchRequest request)
    {
        if (IsBusy) return;
        if (SelectedAccount is not { } account)
        {
            Status = L.T("home.status.account_required");
            return;
        }
        var id = request.VersionId;
        SelectedVersion = id;
        var global = _service.Settings;
        global.LastVersion = id;
        global.Save();

        IsBusy = true;
        Crash = null;
        _stoppedByUser = false;
        _cts = new CancellationTokenSource();
        var workDir = _service.WorkDirFor(id);
        var started = DateTime.UtcNow;
        try
        {
            var settings = _service.EffectiveSettings(id);
            Status = L.T("home.status.checking_game_files");
            await _service.Installer.CompleteAsync(id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
                {
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    Status = p.Files.BytesDownloaded > 0
                        ? L.F("home.status.completing_files", p.Files.CompletedFiles, p.Files.TotalFiles)
                        : L.F("home.status.checking_files", p.Files.CompletedFiles, p.Files.TotalFiles);
                }
            }), _cts.Token);

            var version = _service.Directory.ResolveVersion(id);
            var javaPath = settings.JavaPath;
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                _service.Java.Status = new Progress<string>(text => Status = text);
                javaPath = await _service.Java.ForVersionAsync(version, _cts.Token);
            }

            Status = L.T("home.status.verifying_account");
            var session = await _service.Accounts.ResolveAsync(account, _cts.Token);

            var options = _service.BaseLaunchOptions(id, javaPath, session.Session, session.ExtraJvmArgs) with
            {
                JoinServer = request.JoinServer ?? settings.JoinServer,
                JoinWorld = request.JoinWorld,
            };
            var memory = options.MaxMemoryMb;

            if (!string.IsNullOrWhiteSpace(settings.PreLaunchCommand))
            {
                Status = L.T("home.status.pre_launch_command");
                await RunUserCommandAsync(settings.PreLaunchCommand, id, workDir, _cts.Token);
            }

            Status = L.T("home.status.launching");
            Progress = 100;
            _log.Clear();
            _recentOutput.Clear();
            _log.AddRaw(L.F("home.log.launch", id, javaPath, memory));
            var parser = new GameLogParser();
            var launcher = new GameLauncher(_service.Directory);
            started = DateTime.UtcNow;
            using var running = _service.MarkRunning(workDir);
            _game = launcher.Launch(options, line =>
            {
                lock (parser)
                {
                    _recentOutput.Enqueue(line);
                    if (_recentOutput.Count > KeptOutputLines) _recentOutput.Dequeue();
                    if (parser.Feed(line) is { } entry) _log.Add(entry);
                }
                _service.NotifyGameOutput(id, line);
            });
            IsRunning = true;
            Status = L.F("home.status.running", id, memory);
            await _game.WaitForExitAsync();
            IsRunning = false;

            var code = _game.ExitCode;
            if (_stoppedByUser)
            {
                Status = L.T("home.status.stopped");
            }
            else if (code == 0)
            {
                Status = L.T("home.status.exited");
            }
            else
            {
                Status = L.F("home.status.crashed", code);
                string[] output;
                lock (parser) output = _recentOutput.ToArray();
                var analysis = await Task.Run(() => CrashAnalyzer.AnalyzeRun(workDir, started, output));
                _lastRunFiles = [.. analysis.SourceFiles];
                _log.AddRaw(L.F("home.log.crash_analysis", analysis.Summary));
                Crash = analysis;
            }

            if (!string.IsNullOrWhiteSpace(settings.PostExitCommand))
                await RunUserCommandAsync(settings.PostExitCommand, id, workDir, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Status = L.T("common.cancelled");
        }
        catch (Exception e)
        {
            Status = L.F("home.status.launch_failed", e.Message);
            _log.AddRaw("[Amber] " + e);
        }
        finally
        {
            IsBusy = false;
            IsRunning = false;
            Progress = 0;
            _game = null;
        }
    }

    /// <summary>用户自己配置的启动前 / 退出后命令。命令失败只记日志，不拦启动。</summary>
    private async Task RunUserCommandAsync(string command, string id, string workDir, CancellationToken ct)
    {
        // cmd 不按 C 规则解析引号，整条命令要原样放在 /s /c "…" 里。
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/s /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = workDir;
        psi.Environment["AMBER_VERSION"] = id;
        psi.Environment["AMBER_GAME_DIR"] = workDir;
        System.IO.Directory.CreateDirectory(workDir);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return;
            await process.WaitForExitAsync(ct);
            _log.AddRaw(L.F("home.log.command_exited", command, process.ExitCode));
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log.AddRaw(L.F("home.log.command_failed", command, e.Message));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _stoppedByUser = true;
        _cts?.Cancel();
        if (_game is { HasExited: false } game) game.Kill(true);
    }

    public void ExportCrashBundle(string zipPath)
    {
        CrashAnalyzer.ExportBundle(zipPath, _lastRunFiles, _recentOutput);
        Status = L.F("home.status.report_exported", zipPath);
    }

    [RelayCommand]
    private void OpenLatestCrashFile()
    {
        if (_lastRunFiles.FirstOrDefault() is { } file)
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
