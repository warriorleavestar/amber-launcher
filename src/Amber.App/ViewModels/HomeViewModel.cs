using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Game;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>启动页：选版本、账号、启动，退出后给出崩溃分析。</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    /// <summary>
    /// 有“仅游戏”转发时改启动参数：加 SOCKS5 的 JVM 参数；要进的服务器（不是本机或房间里的地址）改成经出口的本机转发端口。
    /// 返回那个转发（游戏结束时关掉），没有时返回 null。
    /// </summary>
    private readonly List<string> _proxyNotes = [];

    private async Task<(LaunchOptions Options, Amber.Core.Multiplayer.Transport.TunnelClient? Relay)> ApplyGameProxyAsync(LaunchOptions options)
    {
        _proxyNotes.Clear();
        if (_service.ActiveGameProxy is not { } proxy) return (options, null);
        // 用户自己在游戏参数里写了 --proxyHost（或 --proxyHost=地址）时不再加第二个。
        var hasProxyArg = options.ExtraGameArgs.Any(a => a.Equals("--proxyHost", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--proxyHost=", StringComparison.OrdinalIgnoreCase));
        options = options with
        {
            ExtraJvmArgs = [.. options.ExtraJvmArgs, .. proxy.JvmArgs],
            ExtraGameArgs = hasProxyArg ? options.ExtraGameArgs : [.. options.ExtraGameArgs, .. proxy.GameArgs],
        };
        if (!hasProxyArg)
            _proxyNotes.Add(L.F("home.proxy.http_forwarding", proxy.ExitIp));
        else
            _proxyNotes.Add(L.T("home.proxy.custom_proxy"));
        if (options.JoinServer is not { Length: > 0 } server) return (options, null);
        // 本机、局域网、本机解析成私有地址的服务器不经出口（出口也会拒绝），游戏照常直连；端口是 25565 时像游戏一样查 SRV。
        if (await Amber.Core.Multiplayer.Transport.JoinTarget.ResolveForRelayAsync(server, onSkip: reason => _proxyNotes.Add(L.T("home.proxy.direct_join") + reason)) is not { } plan)
            return (options, null);
        var relay = proxy.RelayTo(plan);
        _proxyNotes.Add(L.F("home.proxy.server_forwarding", plan.HandshakeHost, proxy.ExitIp, relay.Port));
        return (options with { JoinServer = $"127.0.0.1:{relay.Port}" }, relay);
    }

    private const int KeptOutputLines = 400;

    /// <summary>进程起来后这么久还没在日志里看到进入游戏的标志，就当作“可能已进入游戏”（老版本、Forge 的日志写法不一样）。</summary>
    private static readonly TimeSpan EnterGameFallback = TimeSpan.FromMinutes(3);

    /// <summary>点“退出游戏”后这么久进程还在，就给出“强制结束”。</summary>
    private static readonly TimeSpan ForceKillDelay = TimeSpan.FromSeconds(30);

    private readonly LauncherService _service;

    /// <summary>自检用。</summary>
    internal LauncherService Service => _service;
    private readonly LogViewModel _log;
    private List<string> _lastRunFiles = [];
    private Queue<string> _lastRunOutput = new();
    private bool _refreshingAccounts;
    private (string WorkDir, DateTime Started, string[] Output)? _crashInput;
    /// <summary>每次显示新的崩溃结果（或清掉）加一：换语言的重新分析只在期间没有新结果时才替换。</summary>
    private int _crashGeneration;

    /// <summary>一次启动，从准备到游戏退出。可以同时有好几个（同时开两个客户端测联机）。</summary>
    private sealed class GameRun(string id)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cts { get; } = new();
        public Process? Game { get; set; }
        public bool StoppedByUser { get; set; }
        public DateTime Started { get; set; } = DateTime.UtcNow;
        /// <summary>日志里出现了进入游戏的标志，或者等够了兜底时间。</summary>
        public bool InGame { get; set; }
        /// <summary>是兜底时间到了才算进入的（没看到标志）。</summary>
        public bool InGameGuessed { get; set; }
        public bool Exiting { get; set; }
        public Queue<string> Output { get; } = new();
    }

    private readonly List<GameRun> _runs = [];
    private GameRun? Latest => _runs.Count > 0 ? _runs[^1] : null;

    /// <summary>进行中的启动有几个（自检用）。</summary>
    internal int RunCount => _runs.Count;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    partial void OnCrashChanged(CrashAnalysis? value) => _crashGeneration++;

    protected override void OnLanguageChanged()
    {
        UpdateRunState();
        if (Crash is null || _crashInput is not { } input) return;
        var generation = _crashGeneration;
        _ = Task.Run(() => CrashAnalyzer.AnalyzeRun(input.WorkDir, input.Started, input.Output)).ContinueWith(
            t => { if (t.IsCompletedSuccessfully && Crash is not null && generation == _crashGeneration) Crash = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public override string Title => L.T("home.title");
    public override string Icon => "M8 5v14l11-7z";

    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty]
    public partial string? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountHint))]
    public partial Account? SelectedAccount { get; set; }

    /// <summary>启动按钮下面的一行：“正版 · Steve”，免得拿离线账号去进正版服。</summary>
    public string AccountHint => SelectedAccount is { } a
        ? L.F(a.Kind switch { AccountKind.Microsoft => "home.account.online", AccountKind.External => "home.account.external", _ => "home.account.offline" }, a.Name)
        : "";

    [ObservableProperty]
    public partial bool AutoMemory { get; set; }

    [ObservableProperty]
    public partial int MaxMemoryMb { get; set; }

    [ObservableProperty]
    public partial string VersionInfo { get; set; } = "";

    /// <summary>有启动在进行（准备中或游戏在运行）。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>最近一次启动还在准备（检查文件、Java、账号）。</summary>
    [ObservableProperty]
    public partial bool IsPreparing { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>点过“退出游戏”，等它保存退出。</summary>
    [ObservableProperty]
    public partial bool IsExiting { get; set; }

    /// <summary>退出等太久了，给出“强制结束”。</summary>
    [ObservableProperty]
    public partial bool ShowForceKill { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>时长不知道的阶段（准备里没有文件进度、游戏加载中、正在退出）用不确定进度条。</summary>
    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    /// <summary>主按钮的文字：不禁用，缺什么就写什么（先添加账号、先安装游戏），运行中变成退出游戏。</summary>
    [ObservableProperty]
    public partial string PrimaryLabel { get; set; } = "";

    /// <summary>游戏在运行时又要启动：先问一声（多开是允许的，测联机常用）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingLaunch), nameof(PendingLaunchText))]
    public partial LaunchRequest? PendingLaunch { get; set; }

    private string _pendingRunningId = "";
    public bool HasPendingLaunch => PendingLaunch is not null;
    public string PendingLaunchText => L.F("home.multi.warning", _pendingRunningId);

    /// <summary>主按钮在缺账号、缺实例时去的地方，崩溃卡的“查看日志”（MainViewModel 接上）。</summary>
    public Action? NavigateToAccounts { get; set; }
    public Action? NavigateToNewInstance { get; set; }
    public Action? NavigateToLog { get; set; }
    public Action? NavigateHere { get; set; }

    /// <summary>游戏进程起来了 / 退出了（参数：是不是崩溃）。窗口按“启动后启动器”的设置最小化、恢复。</summary>
    public event Action? GameStarted;
    public event Action<bool>? GameExited;

    protected override StatusMessage? DefaultStatus => StatusMessage.Info("home.status.ready");

    private void SetIdleStatus(string key) => SetStatus(StatusMessage.Info(key));

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
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_service.CurrentInstance is { } id && Versions.Contains(id)) SelectedVersion = id;
        });
        _clock.Tick += (_, _) => Tick();
        RefreshVersions();
        RefreshAccounts();
        UpdateRunState();
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            OnSelectedVersionChanged(SelectedVersion);
            OnPropertyChanged(nameof(AccountHint));
            OnPropertyChanged(nameof(PendingLaunchText));
        });
    }

    private void RefreshAccounts()
    {
        _refreshingAccounts = true;
        Accounts.Clear();
        foreach (var a in _service.Accounts.Store.Accounts) Accounts.Add(a);
        SelectedAccount = _service.Accounts.Store.Selected;
        _refreshingAccounts = false;
        UpdateRunState();
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        UpdateRunState();
        if (_refreshingAccounts || value is null || value.Id == _service.Accounts.Store.SelectedId) return;
        _service.Accounts.Store.SelectedId = value.Id;
        _service.Accounts.Store.Save();
        _service.NotifyAccountsChanged();
    }

    private void RefreshVersions()
    {
        Versions.Clear();
        foreach (var v in _service.InstalledVersions()) Versions.Add(v);
        SelectedVersion = _service.CurrentOr(Versions, Versions.FirstOrDefault());
        if (Versions.Count == 0) SetIdleStatus("home.status.no_versions");
        UpdateRunState();
    }

    partial void OnSelectedVersionChanged(string? value)
    {
        VersionInfo = "";
        UpdateRunState();
        if (value is null) return;
        _service.SetCurrentInstance(value);
        try
        {
            var instance = _service.GetInstance(value);
            var loader = instance.Loader is null ? L.T("home.version.vanilla") : $"{instance.Loader} {instance.LoaderVersion}";
            var mods = new ModManager(instance.ModsDir).CountEnabled();
            VersionInfo = _service.WithStateNote(value, L.F("home.version.info", instance.McVersion, loader, mods == 0 ? "" : L.P("home.version.mod_count", mods, mods)));
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

    /// <summary>按最近一次启动的阶段和当前选择，重算主按钮文字和进度条。</summary>
    private void UpdateRunState()
    {
        var run = Latest;
        IsBusy = _runs.Count > 0;
        IsPreparing = run is { Game: null };
        IsRunning = run is { Game: not null };
        IsExiting = run is { Exiting: true };
        if (run is null) ShowForceKill = false;
        ShowProgress = run is not null && (run.Game is null || !run.InGame || run.Exiting);
        ProgressIndeterminate = run is not null && (run.Game is not null || Progress <= 0);
        PrimaryLabel = L.T(PrimaryKey(run));
        if (IsRunning) _clock.Start();
        else _clock.Stop();
    }

    private string PrimaryKey(GameRun? run)
    {
        if (run is not null)
            return run.Game is null ? "home.button.cancel_launch" : run.Exiting ? "home.button.exiting" : "home.button.exit_game";
        if (SelectedAccount is null) return "home.button.need_account";
        if (SelectedVersion is not { } id) return "home.button.need_instance";
        return _service.Directory.StateOf(id) switch
        {
            Amber.Core.Game.InstanceState.Incomplete => "home.button.resume_install",
            Amber.Core.Game.InstanceState.Broken => "home.button.repair",
            _ => "home.launch",
        };
    }

    partial void OnProgressChanged(double value)
    {
        if (Latest is { Game: null }) ProgressIndeterminate = value <= 0;
    }

    /// <summary>主按钮：缺什么去补什么；准备中是取消，运行中是退出游戏。</summary>
    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (Latest is { } run)
        {
            if (run.Game is null) CancelRun(run);
            else if (!run.Exiting) ExitGame(run);
            return;
        }
        if (SelectedAccount is null)
        {
            SetStatus(StatusMessage.Warning("home.status.account_required"));
            NavigateToAccounts?.Invoke();
            return;
        }
        if (SelectedVersion is not { } id)
        {
            NavigateToNewInstance?.Invoke();
            return;
        }
        await RunAsync(new LaunchRequest(id));
    }

    [RelayCommand]
    private Task LaunchAsync() => SelectedVersion is { } id ? RunAsync(new LaunchRequest(id)) : Task.CompletedTask;

    [RelayCommand]
    private void LaunchAnyway()
    {
        if (PendingLaunch is not { } request) return;
        PendingLaunch = null;
        _ = RunAsync(request, allowSecond: true);
    }

    [RelayCommand]
    private void DismissPendingLaunch() => PendingLaunch = null;

    private async Task RunAsync(LaunchRequest request, bool allowSecond = false)
    {
        if (_runs.Any(r => r.Game is null))
        {
            SetStatus(StatusMessage.Warning("home.status.preparing_other"));
            return;
        }
        if (!allowSecond && _runs.FirstOrDefault(r => r.Game is not null) is { } other)
        {
            _pendingRunningId = other.Id;
            PendingLaunch = request;
            NavigateHere?.Invoke();
            return;
        }
        if (SelectedAccount is not { } account)
        {
            SetStatus(StatusMessage.Warning("home.status.account_required"));
            return;
        }
        var id = request.VersionId;
        SelectedVersion = id;
        _service.SetCurrentInstance(id);

        var run = new GameRun(id);
        _runs.Add(run);
        Crash = null;
        Progress = 0;
        UpdateRunState();
        var ct = run.Cts.Token;
        var workDir = _service.WorkDirFor(id);
        var crashed = false;
        var gameStarted = false;
        try
        {
            var settings = _service.EffectiveSettings(id);
            await RepairIfBrokenAsync(id, ct);
            SetStatus(StatusMessage.Info("home.status.checking_game_files"));
            await _service.Installer.CompleteAsync(id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
                {
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    Status = p.Files.BytesDownloaded > 0
                        ? L.F("home.status.completing_files", p.Files.CompletedFiles, p.Files.TotalFiles)
                        : L.F("home.status.checking_files", p.Files.CompletedFiles, p.Files.TotalFiles);
                }
            }), ct);
            // 文件补齐了：上次没装完的实例到这里也算装完了。
            try { _service.Directory.MarkInstallFinished(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            var version = _service.Directory.ResolveVersion(id);
            var javaPath = settings.JavaPath;
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                Progress = 0;
                _service.Java.Status = new Progress<string>(text => Status = text);
                javaPath = await _service.Java.ForVersionAsync(version, ct);
            }

            Progress = 0;
            SetStatus(StatusMessage.Info("home.status.verifying_account"));
            var session = await _service.Accounts.ResolveAsync(account, ct);

            var options = _service.BaseLaunchOptions(id, javaPath, session.Session, session.ExtraJvmArgs) with
            {
                JoinServer = request.JoinServer ?? settings.JoinServer,
                JoinWorld = request.JoinWorld,
            };
            // 联机页开了“仅游戏”转发：游戏的 HTTP 走本机 SOCKS5，要进的服务器另开一个经出口的转发端口。
            var (proxied, relayClient) = await ApplyGameProxyAsync(options);
            options = proxied;
            await using var relay = relayClient;
            var memory = options.MaxMemoryMb;

            if (!string.IsNullOrWhiteSpace(settings.PreLaunchCommand))
            {
                SetStatus(StatusMessage.Info("home.status.pre_launch_command"));
                await RunUserCommandAsync(settings.PreLaunchCommand, id, workDir, ct);
            }

            SetStatus(StatusMessage.Info("home.status.launching"));
            _log.Clear();
            _log.BeginRun(id, DateTimeOffset.Now);
            _log.AddRaw(L.F("home.log.launch", id, javaPath, memory));
            foreach (var note in _proxyNotes) _log.AddRaw(note);
            var parser = new GameLogParser();
            var launcher = new GameLauncher(_service.Directory);
            using var running = _service.MarkRunning(workDir);
            var game = launcher.Launch(options, line =>
            {
                lock (parser)
                {
                    run.Output.Enqueue(line);
                    if (run.Output.Count > KeptOutputLines) run.Output.Dequeue();
                    if (parser.Feed(line) is { } entry) _log.Add(entry);
                }
                if (!run.InGame && IsEnterGameLine(line)) Dispatcher.UIThread.Post(() => EnterGame(run, guessed: false));
                _service.NotifyGameOutput(id, line);
            });
            run.Started = DateTime.UtcNow;
            run.Game = game;
            UpdateRunState();
            Tick();
            gameStarted = true;
            GameStarted?.Invoke();
            await game.WaitForExitAsync();

            var code = game.ExitCode;
            if (run.StoppedByUser)
            {
                SetStatus(StatusMessage.Info("home.status.stopped"));
            }
            else if (code == 0)
            {
                SetStatus(StatusMessage.Info("home.status.exited"));
            }
            else
            {
                crashed = true;
                SetStatus(StatusMessage.Error("home.status.crashed", code));
                string[] output;
                lock (parser) output = run.Output.ToArray();
                var analysis = await Task.Run(() => CrashAnalyzer.AnalyzeRun(workDir, run.Started, output));
                // 崩溃分析的标题和建议是按当时的语言写的：记下输入，换语言时重新分析一遍。
                _crashInput = (workDir, run.Started, output);
                _lastRunFiles = [.. analysis.SourceFiles];
                _lastRunOutput = new Queue<string>(output);
                _log.AddRaw(L.F("home.log.crash_analysis", analysis.Summary));
                Crash = analysis;
                // 玩家在别的页面：弹个 Toast，带“查看”。
                if (!IsCurrent) Toasts.WithAction(L.F("home.crash.toast", id), L.T("home.crash.show"), () => NavigateHere?.Invoke());
            }

            if (!string.IsNullOrWhiteSpace(settings.PostExitCommand))
                await RunUserCommandAsync(settings.PostExitCommand, id, workDir, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("home.status.launch_failed", e, ErrorText.Describe(e)));
            _log.AddRaw("[Amber] " + e);
        }
        finally
        {
            _runs.Remove(run);
            run.Cts.Dispose();
            Progress = 0;
            UpdateRunState();
            if (gameStarted && _runs.Count == 0) GameExited?.Invoke(crashed);
        }
    }

    /// <summary>“已损坏”最常见的原因是继承的原版不在了：补装原版。版本文件本身坏了修不了，提示重新安装。</summary>
    private async Task RepairIfBrokenAsync(string id, CancellationToken ct)
    {
        if (_service.Directory.StateOf(id) != Amber.Core.Game.InstanceState.Broken) return;
        Amber.Core.Game.VersionJson own;
        try { own = _service.Directory.ReadVersion(id); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            throw new InvalidDataException(L.T("instance.state.broken"), e);
        }
        if (own.InheritsFrom is not { } parent || File.Exists(_service.Directory.VersionJsonPath(parent)))
            throw new InvalidDataException(L.T("instance.state.broken"));
        SetStatus(StatusMessage.Info("home.status.repairing", parent));
        await _service.Installer.InstallAsync(parent, new Progress<InstallProgress>(p =>
        {
            if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0) Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
        }), ct);
    }

    /// <summary>日志里表示“已经到了游戏主界面”的几行（新版本是声音引擎，老版本是 OpenAL）。</summary>
    internal static bool IsEnterGameLine(string line) =>
        line.Contains("Sound engine started", StringComparison.Ordinal) || line.Contains("OpenAL initialized", StringComparison.Ordinal);

    private void EnterGame(GameRun run, bool guessed)
    {
        if (run.InGame) return;
        run.InGame = true;
        run.InGameGuessed = guessed;
        UpdateRunState();
        Tick();
    }

    /// <summary>每秒更新一次运行状态：加载中显示已等了多久，进了游戏显示运行时长。</summary>
    private void Tick()
    {
        if (Latest is not { Game: not null } run || run.Exiting) return;
        var elapsed = DateTime.UtcNow - run.Started;
        if (!run.InGame && elapsed >= EnterGameFallback)
        {
            EnterGame(run, guessed: true);
            return;
        }
        if (!run.InGame)
            SetStatus(StatusMessage.Info("home.status.loading_game", (int)elapsed.TotalSeconds));
        else if (run.InGameGuessed)
            SetStatus(StatusMessage.Info("home.status.maybe_in_game", run.Id, (int)elapsed.TotalMinutes));
        else
            SetStatus(StatusMessage.Info("home.status.in_game", run.Id, (int)elapsed.TotalMinutes));
    }

    private void CancelRun(GameRun run)
    {
        run.StoppedByUser = true;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>像点窗口的关闭按钮一样让游戏自己保存退出；等太久了再给“强制结束”。</summary>
    private void ExitGame(GameRun run)
    {
        if (run.Game is not { } game) return;
        run.StoppedByUser = true;
        run.Exiting = true;
        UpdateRunState();
        SetStatus(StatusMessage.Info("home.status.exiting", run.Id));
        bool closed;
        try { closed = game.CloseMainWindow(); }
        catch (InvalidOperationException) { closed = false; }
        if (!closed)
        {
            // 没有能关的主窗口（还在加载、或者是无窗口的老版本）：直接给出强制结束。
            ShowForceKill = true;
            return;
        }
        _ = Task.Delay(ForceKillDelay).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_runs.Contains(run) && run.Game is { HasExited: false }) ShowForceKill = true;
        }), TaskScheduler.Default);
    }

    /// <summary>强制结束最近一次启动（Flyout 里确认过）：准备中就是取消。</summary>
    [RelayCommand]
    private void ForceKill()
    {
        ShowForceKill = false;
        Cancel();
    }

    /// <summary>立即结束最近一次启动：准备中取消，运行中结束进程（自检和强制结束用）。</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (Latest is not { } run) return;
        CancelRun(run);
        try
        {
            if (run.Game is { HasExited: false } game) game.Kill(true);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>崩溃卡上的“查看日志”：打开日志页，只看警告和错误。</summary>
    [RelayCommand]
    private void ViewLog()
    {
        _log.OnlyProblems = true;
        NavigateToLog?.Invoke();
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

    public void ExportCrashBundle(string zipPath)
    {
        CrashAnalyzer.ExportBundle(zipPath, _lastRunFiles, _lastRunOutput);
        SetStatus(StatusMessage.Success("home.status.report_exported", zipPath));
    }

    [RelayCommand]
    private void OpenLatestCrashFile()
    {
        if (_lastRunFiles.FirstOrDefault() is { } file)
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
