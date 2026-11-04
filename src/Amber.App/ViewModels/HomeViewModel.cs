using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Install;
using Amber.Core.Launch;
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
            _proxyNotes.Add($"[Amber] 仅游戏转发：游戏里的 HTTP 请求（进服务器时的会话校验、皮肤）经 {proxy.ExitIp} 的电脑出去；域名在本机查询，Amber 自己刷新登录不经过它");
        else
            _proxyNotes.Add("[Amber] 游戏参数里已经有你自己写的 --proxyHost：游戏会用你的代理，进服务器时的会话校验不经出口");
        if (options.JoinServer is not { Length: > 0 } server) return (options, null);
        // 本机、局域网、本机解析成私有地址的服务器不经出口（出口也会拒绝），游戏照常直连；端口是 25565 时像游戏一样查 SRV。
        if (await Amber.Core.Multiplayer.Transport.JoinTarget.ResolveForRelayAsync(server, onSkip: reason => _proxyNotes.Add("[Amber] 进服务器不经出口：" + reason)) is not { } plan)
            return (options, null);
        var relay = proxy.RelayTo(plan);
        _proxyNotes.Add($"[Amber] 进入 {plan.HandshakeHost} 也经 {proxy.ExitIp} 转发（游戏里显示的地址是本机转发端口 127.0.0.1:{relay.Port}）");
        return (options with { JoinServer = $"127.0.0.1:{relay.Port}" }, relay);
    }

    private const int KeptOutputLines = 400;

    private readonly LauncherService _service;
    private readonly LogViewModel _log;
    private readonly Queue<string> _recentOutput = new();
    private CancellationTokenSource? _cts;
    private Process? _game;
    private bool _stoppedByUser;
    private List<string> _lastRunFiles = [];
    private bool _refreshingAccounts;

    public override string Title => "启动";
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
    public partial string Status { get; set; } = "准备就绪";

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
        if (Versions.Count == 0) Status = "还没有安装任何版本，去「下载」页装一个吧";
    }

    partial void OnSelectedVersionChanged(string? value)
    {
        VersionInfo = "";
        if (value is null) return;
        try
        {
            var instance = _service.GetInstance(value);
            var loader = instance.Loader is null ? "原版" : $"{instance.Loader} {instance.LoaderVersion}";
            var mods = new ModManager(instance.ModsDir).CountEnabled();
            VersionInfo = $"游戏 {instance.McVersion} · {loader}{(mods > 0 ? $" · {mods} 个模组" : "")}";
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            VersionInfo = "版本文件读取失败：" + e.Message;
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
            Status = "请先到「账号」页添加账号";
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
            Status = "正在检查游戏文件……";
            await _service.Installer.CompleteAsync(id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
                {
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    Status = p.Files.BytesDownloaded > 0
                        ? $"补全文件 {p.Files.CompletedFiles}/{p.Files.TotalFiles}"
                        : $"检查文件 {p.Files.CompletedFiles}/{p.Files.TotalFiles}";
                }
            }), _cts.Token);

            var version = _service.Directory.ResolveVersion(id);
            var javaPath = settings.JavaPath;
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                _service.Java.Status = new Progress<string>(text => Status = text);
                javaPath = await _service.Java.ForVersionAsync(version, _cts.Token);
            }

            Status = "正在验证账号……";
            var session = await _service.Accounts.ResolveAsync(account, _cts.Token);

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
                Status = "正在运行启动前命令……";
                await RunUserCommandAsync(settings.PreLaunchCommand, id, workDir, _cts.Token);
            }

            Status = "正在启动……";
            Progress = 100;
            _log.Clear();
            _recentOutput.Clear();
            _log.AddRaw($"[Amber] 启动 {id}，Java：{javaPath}，最大内存 {memory} MB");
            foreach (var note in _proxyNotes) _log.AddRaw(note);
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
            Status = $"{id} 运行中（{memory} MB）";
            await _game.WaitForExitAsync();
            IsRunning = false;

            var code = _game.ExitCode;
            if (_stoppedByUser)
            {
                Status = "已结束游戏";
            }
            else if (code == 0)
            {
                Status = "游戏已正常退出";
            }
            else
            {
                Status = $"游戏异常退出（代码 {code}）";
                string[] output;
                lock (parser) output = _recentOutput.ToArray();
                var analysis = await Task.Run(() => CrashAnalyzer.AnalyzeRun(workDir, started, output));
                _lastRunFiles = [.. analysis.SourceFiles];
                _log.AddRaw("[Amber] 崩溃分析：" + analysis.Summary);
                Crash = analysis;
            }

            if (!string.IsNullOrWhiteSpace(settings.PostExitCommand))
                await RunUserCommandAsync(settings.PostExitCommand, id, workDir, CancellationToken.None);
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
            _log.AddRaw($"[Amber] 命令“{command}”退出代码 {process.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log.AddRaw($"[Amber] 命令“{command}”运行失败：{e.Message}");
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
        Status = "错误报告已导出：" + zipPath;
    }

    [RelayCommand]
    private void OpenLatestCrashFile()
    {
        if (_lastRunFiles.FirstOrDefault() is { } file)
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
