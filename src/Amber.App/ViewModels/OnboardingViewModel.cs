using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Localization;
using Amber.Core.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>新手卡上的一个热门版本芯片：“1.20.1 Forge”。</summary>
public sealed record HotVersion(string McVersion, LoaderKind? Loader)
{
    public string Label => Loader is { } kind ? $"{McVersion} {kind}" : McVersion;
}

/// <summary>
/// 首次使用的新手卡（改版规格 3.1）：没有账号或没有实例时在启动页替换实例卡。
/// 找已有的游戏目录；卡里直接添加离线账号；一键安装最新正式版（可同时装 Fabric）或热门版本，Java 下载合并进同一条进度。
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    public static readonly HotVersion[] HotVersions =
    [
        new("1.20.1", LoaderKind.Forge),
        new("1.12.2", LoaderKind.Forge),
        new("1.7.10", LoaderKind.Forge),
        new("1.21.1", LoaderKind.Fabric),
    ];

    /// <summary>这么久下载进度没动，就提示换下载源。</summary>
    internal static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(20);

    private readonly LauncherService _service;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _waited = new();
    private DateTime _lastProgress;
    private CancellationTokenSource? _installCts;
    private (string McVersion, LoaderKind? Loader, bool FallbackToVanilla)? _installing;
    private PreparedGame? _preparedGame;
    private bool _restartAfterCancel;
    private bool _detected;
    private bool _loadingLatest;

    // 只记本会话真正完成过的游戏阶段；Java 取消或失败后的重试不能再次安装并占用同一个 Forge 目录。
    private sealed record PreparedGame(string Root, string McVersion, LoaderKind? Loader, bool FallbackToVanilla, string Id);

    /// <summary>找已有游戏目录时看哪些位置（自检和开发截图不扫描真实的 .minecraft）。</summary>
    public Func<IEnumerable<string>> Candidates { get; set; } = () =>
        Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is { Length: > 0 }
            ? [] : GameDirectoryDetector.CommonCandidates(AppContext.BaseDirectory);

    /// <summary>去微软登录、去下载页选版本、导入整合包文件（MainViewModel 接上）。</summary>
    public Action? MicrosoftLogin { get; set; }
    public Action? ChooseVersion { get; set; }
    public Func<string, Task>? ImportPack { get; set; }

    /// <summary>启动按钮在缺东西时点了：页面把焦点放到对应的输入框或按钮上。</summary>
    public event Action<OnboardingStep>? FocusRequested;

    public OnboardingViewModel(LauncherService service)
    {
        _service = service;
        _clock.Tick += (_, _) => Tick();
        L.Changed += () => Dispatcher.UIThread.Post(RefreshTexts);
        RefreshTexts();
    }

    // —— 整体状态（Home 告诉它缺什么） ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(ShowAccountStep), nameof(ShowGameStep), nameof(AccountDone), nameof(ShowAccountDone), nameof(ShowGameWaiting), nameof(ShowGameDone))]
    public partial bool NeedsAccount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(ShowGameStep), nameof(GameDone), nameof(ShowDetected), nameof(ShowGameWaiting), nameof(ShowGameDone), nameof(ShowPickDirectory))]
    public partial bool NeedsGame { get; set; }

    /// <summary>新手卡显示着（缺账号或缺实例）。装的时候一直显示，装完才收起。</summary>
    public bool IsVisible => NeedsAccount || NeedsGame || IsInstalling;

    public bool AccountDone => !NeedsAccount;
    public bool GameDone => !NeedsGame;

    /// <summary>只展开当前这一步：先账号，再游戏。</summary>
    public bool ShowAccountStep => NeedsAccount;
    public bool ShowAccountDone => !NeedsAccount;
    public bool ShowGameStep => !NeedsAccount && (NeedsGame || IsInstalling);
    public bool ShowGameWaiting => NeedsAccount && NeedsGame;
    public bool ShowGameDone => NeedsAccount && !NeedsGame;

    public HotVersion[] Hot => HotVersions;

    [ObservableProperty]
    public partial string AccountDoneText { get; set; } = "";

    /// <summary>Home 的账号、实例变了之后调用。</summary>
    public void Update(bool needsAccount, bool needsGame, string? accountName)
    {
        NeedsAccount = needsAccount;
        NeedsGame = needsGame;
        AccountDoneText = accountName is null ? "" : L.F("onboarding.account.done", accountName);
        if (needsGame && !_detected) _ = DetectAsync();
        if (needsGame && LatestRelease is null && !_loadingLatest) _ = LoadLatestAsync();
    }

    // —— ① 已有游戏目录 ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetected), nameof(DetectedText))]
    public partial DetectedGameDirectory? Detected { get; set; }

    public bool ShowDetected => NeedsGame && Detected is not null && !IsInstalling;

    /// <summary>“使用已有的游戏目录…”入口：不管找没找到都有（装的时候不行）。</summary>
    public bool ShowPickDirectory => NeedsGame && !IsInstalling;

    public string DetectedText => Detected is { } d ? L.P("onboarding.detect.found", d.InstanceCount, d.Path, d.InstanceCount) : "";

    internal async Task DetectAsync()
    {
        _detected = true;
        var exclude = _service.Directory.Root;
        var candidates = Candidates;
        var found = await Task.Run(() => GameDirectoryDetector.Find(candidates(), exclude, 1));
        Detected = found.FirstOrDefault();
    }

    [RelayCommand]
    private void UseDetected()
    {
        if (Detected is { } d) UseDirectory(d.Path);
    }

    /// <summary>换到已有的游戏目录（有实例的话就不用再装了）。</summary>
    public void UseDirectory(string path)
    {
        if (IsInstalling) return;
        _preparedGame = null;
        // 选了启动器所在的文件夹（里面才是 .minecraft）也认。
        var inner = Path.Combine(path, ".minecraft");
        if (!Directory.Exists(Path.Combine(path, "versions")) && Directory.Exists(Path.Combine(inner, "versions"))) path = inner;
        _service.Settings.GameDirectory = path;
        _service.SaveAndReload();
        Detected = null;
        var count = GameDirectoryDetector.CountInstances(path);
        Message = count > 0 ? StatusMessage.Success("onboarding.detect.used", path) : StatusMessage.Warning("onboarding.detect.empty", path);
    }

    // —— ② 账号 ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameProblem), nameof(NameWarning), nameof(CanAddOffline))]
    [NotifyCanExecuteChangedFor(nameof(AddOfflineCommand))]
    public partial string PlayerName { get; set; } = "";

    private OfflineNameCheck NameCheck => OfflineNameRules.Check(PlayerName);

    /// <summary>输入框下面的说明：不满足硬性规则时写哪里不行（还没输入时不说）。</summary>
    public string NameProblem => PlayerName.Length == 0 ? "" : NameCheck.Problem switch
    {
        OfflineNameProblem.HasSpace => L.T("onboarding.name.space"),
        OfflineNameProblem.TooShort or OfflineNameProblem.TooLong => L.F("onboarding.name.length", OfflineNameRules.MinLength, OfflineNameRules.MaxLength),
        _ => "",
    };

    /// <summary>只提醒：能用，但部分服务器会拒绝。</summary>
    public string NameWarning => NameCheck is { Ok: true, NotAscii: true } ? L.T("onboarding.name.not_ascii") : "";

    public bool CanAddOffline => NameCheck.Ok;

    [RelayCommand(CanExecute = nameof(CanAddOffline))]
    private void AddOffline()
    {
        var name = PlayerName.Trim();
        if (!OfflineNameRules.Check(name).Ok) return;
        var account = _service.Accounts.AddOffline(name);
        _service.Accounts.Store.SelectedId = account.Id;
        _service.Accounts.Store.Save();
        PlayerName = "";
        _service.NotifyAccountsChanged();
    }

    [RelayCommand]
    private void Microsoft() => MicrosoftLogin?.Invoke();

    // —— ③ 安装游戏 ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLatestLabel))]
    [NotifyCanExecuteChangedFor(nameof(InstallLatestCommand))]
    public partial string? LatestRelease { get; set; }

    public string InstallLatestLabel => LatestRelease is { } v ? L.F("onboarding.install.latest", v) : L.T("onboarding.install.latest_loading");

    /// <summary>同时装 Fabric，方便加模组。</summary>
    [ObservableProperty]
    public partial bool WithFabric { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(ShowGameStep), nameof(ShowDetected), nameof(CanStartInstall), nameof(ShowPickDirectory))]
    [NotifyCanExecuteChangedFor(nameof(InstallLatestCommand), nameof(InstallHotCommand))]
    public partial bool IsInstalling { get; set; }

    public bool CanStartInstall => !IsInstalling;

    /// <summary>阶段：下载游戏文件 → 准备 Java → 完成。</summary>
    [ObservableProperty]
    public partial string StageText { get; set; } = "";

    /// <summary>阶段里的细节（文件数、Java 下载进度）。</summary>
    [ObservableProperty]
    public partial string DetailText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string WaitedText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowSlowHint { get; set; }

    [ObservableProperty]
    public partial string SourceText { get; set; } = "";

    /// <summary>卡里的结果或出错消息。</summary>
    [ObservableProperty]
    public partial StatusMessage? Message { get; set; }

    /// <summary>现在的下载源是不是镜像优先（“自动”按时区）。</summary>
    public bool UsesMirror => _service.Settings.DownloadSource switch
    {
        "mirror" => true,
        "official" => false,
        _ => ReferenceEquals(SourceChain.Default(), SourceChain.MirrorFirst),
    };

    private async Task LoadLatestAsync()
    {
        _loadingLatest = true;
        try
        {
            var manifest = await _service.Installer.GetManifestAsync();
            if (manifest.Latest.TryGetValue("release", out var release)) LatestRelease = release;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 拿不到版本列表时按钮写“正在获取”，点热门版本或去下载页照样能装。
            Trace.WriteLine("Jay: onboarding manifest failed: " + e.Message);
        }
        finally
        {
            _loadingLatest = false;
        }
    }

    private bool CanInstallLatest() => !IsInstalling && LatestRelease is not null;

    [RelayCommand(CanExecute = nameof(CanInstallLatest))]
    private Task InstallLatestAsync() =>
        InstallAsync(LatestRelease!, WithFabric ? LoaderKind.Fabric : null, fallbackToVanilla: true);

    private bool CanInstallHot(HotVersion? _) => !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanInstallHot))]
    private Task InstallHotAsync(HotVersion? hot) =>
        hot is null ? Task.CompletedTask : InstallAsync(hot.McVersion, hot.Loader, fallbackToVanilla: false);

    [RelayCommand]
    private void Choose() => ChooseVersion?.Invoke();

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    /// <summary>换下载源（官方 ⇄ BMCLAPI）。正在装时取消再用新的源接着装，已下好的文件不会重下。</summary>
    [RelayCommand]
    private void SwitchSource()
    {
        _service.Settings.DownloadSource = UsesMirror ? "official" : "mirror";
        if (IsInstalling)
        {
            _restartAfterCancel = true;
            _installCts?.Cancel();
        }
        else
        {
            _service.SaveAndReload();
        }
        RefreshTexts();
    }

    /// <summary>装一个版本：loader 为空时只装原版。装好后把 Java 也准备好（同一条进度），设为当前实例。</summary>
    internal async Task InstallAsync(string mcVersion, LoaderKind? loader, bool fallbackToVanilla)
    {
        if (IsInstalling) return;
        _installing = (mcVersion, loader, fallbackToVanilla);
        IsInstalling = true;
        Message = null;
        ShowSlowHint = false;
        _waited.Restart();
        _lastProgress = DateTime.UtcNow;
        _clock.Start();
        Tick();
        var cts = _installCts = new CancellationTokenSource();
        var ct = cts.Token;
        string? expectedId = null;
        try
        {
            SetStage("onboarding.stage.game", 0, indeterminate: true);
            var root = Path.TrimEndingDirectorySeparator(_service.Directory.Root);
            var id = ResumePreparedGame(root, mcVersion, loader, fallbackToVanilla);
            if (id is null)
            {
                LoaderVersion? chosen = null;
                if (loader is { } kind)
                {
                    DetailText = L.T("download.status.fetching_versions");
                    var versions = await LoaderInstallers.Create(kind, _service.Loaders).ListAsync(mcVersion, ct);
                    chosen = versions.FirstOrDefault(v => v.Recommended) ?? versions.FirstOrDefault(v => v.Stable) ?? versions.FirstOrDefault();
                    if (chosen is null && !fallbackToVanilla) throw new LoaderUnavailableException(kind, mcVersion);
                    if (chosen is null) Message = StatusMessage.Warning("onboarding.install.no_loader", kind, mcVersion);
                }
                expectedId = chosen?.DefaultId ?? mcVersion;

                if (chosen is not null)
                {
                    id = await _service.InstallLoaderAsync(chosen, null, new Progress<LoaderProgress>(p =>
                    {
                        DetailText = p.Stage;
                        if (p.Percent is { } percent) GameProgress(percent);
                    }), ct);
                }
                else
                {
                    id = mcVersion;
                    try { _service.MarkInstallStarted(id); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    await _service.Installer.InstallAsync(id, new Progress<InstallProgress>(p =>
                    {
                        if (p.Stage != InstallStage.Files || p.Files.TotalFiles == 0) return;
                        DetailText = L.F("onboarding.stage.files", p.Files.CompletedFiles, p.Files.TotalFiles, p.Files.BytesDownloaded / 1024.0 / 1024);
                        GameProgress(100.0 * p.Files.CompletedFiles / p.Files.TotalFiles);
                    }), ct);
                }
                _preparedGame = new PreparedGame(root, mcVersion, loader, fallbackToVanilla, id);
            }
            else expectedId = id;

            // Java 合并进同一条进度：没有合适的就在这里下好，第一次启动不用再等。
            SetStage("onboarding.stage.java", 90, indeterminate: true);
            DetailText = "";
            var java = _service.Java;
            java.Status = new Progress<string>(text =>
            {
                DetailText = text;
                _lastProgress = DateTime.UtcNow;
            });
            try
            {
                await java.ForVersionAsync(_service.Directory.ResolveVersion(id), ct);
            }
            catch (InvalidOperationException e)
            {
                // 游戏装好了，只是没找到 Java（关了自动下载）：启动时还会再提示。
                Message = StatusMessage.Plain(e.Message, StatusKind.Warning);
            }
            finally
            {
                java.Status = null;
            }

            ct.ThrowIfCancellationRequested();
            _service.MarkInstallFinished(id);
            if (id != expectedId) _service.MarkInstallFinished(expectedId);
            _preparedGame = null;
            SetStage("onboarding.stage.done", 100, indeterminate: false);
            DetailText = "";
            Message ??= StatusMessage.Success("onboarding.install.done", id, (long)Math.Round(_waited.Elapsed.TotalSeconds));
            _service.SetCurrentInstance(id);
            _service.NotifyInstalledChanged();
        }
        catch (OperationCanceledException) when (_restartAfterCancel)
        {
            // 换了下载源：用新源接着装。
        }
        // 不是玩家点的取消（比如连接超时也抛 TaskCanceledException）按失败显示，不能说成“已取消”。
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Message = StatusMessage.Warning("download.status.cancelled");
        }
        catch (LoaderUnavailableException e)
        {
            Message = StatusMessage.Error("download.loader.unsupported", e.Kind, e.McVersion);
        }
        catch (Exception e)
        {
            Message = StatusMessage.Failure("common.install_failed", e, ErrorText.Of(e));
        }
        finally
        {
            // 刷新失败或取消后的状态；已回滚的目录不再保留标记，其余未完成安装仍显示标注。
            if (expectedId is not null) _service.NotifyInstanceStateChanged(expectedId);
            _clock.Stop();
            _waited.Stop();
            ShowSlowHint = false;
            WaitedText = "";
            IsInstalling = false;
            Progress = 0;
            ProgressIndeterminate = false;
        }
        if (_restartAfterCancel && _installing is { } again)
        {
            _restartAfterCancel = false;
            _service.SaveAndReload();
            RefreshTexts();
            await InstallAsync(again.McVersion, again.Loader, again.FallbackToVanilla);
        }
    }

    private string? ResumePreparedGame(string root, string mcVersion, LoaderKind? loader, bool fallbackToVanilla)
    {
        var prepared = _preparedGame;
        _preparedGame = null;
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (prepared is null || !string.Equals(prepared.Root, root, pathComparison) || prepared.McVersion != mcVersion ||
            prepared.Loader != loader || prepared.FallbackToVanilla != fallbackToVanilla) return null;
        try
        {
            // 目标或继承的原版被移走、损坏时，不再把上次成功阶段当作可继续的安装。
            _service.Directory.ResolveVersion(prepared.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
        _preparedGame = prepared;
        return prepared.Id;
    }

    private void SetStage(string key, double progress, bool indeterminate)
    {
        StageText = L.T(key);
        Progress = progress;
        ProgressIndeterminate = indeterminate;
        _lastProgress = DateTime.UtcNow;
    }

    /// <summary>游戏文件占进度的 0–90%，Java 占最后一段。</summary>
    private void GameProgress(double percent)
    {
        var value = Math.Clamp(percent, 0, 100) * 0.9;
        if (Math.Abs(value - Progress) > 0.01) _lastProgress = DateTime.UtcNow;
        Progress = value;
        ProgressIndeterminate = false;
    }

    private void Tick()
    {
        var seconds = (long)_waited.Elapsed.TotalSeconds;
        WaitedText = L.P("onboarding.waited", seconds, seconds);
        ShowSlowHint = IsInstalling && DateTime.UtcNow - _lastProgress > SlowAfter;
    }

    private void RefreshTexts()
    {
        SourceText = L.F("onboarding.source", L.T(UsesMirror ? "onboarding.source.mirror" : "onboarding.source.official"));
        OnPropertyChanged(nameof(InstallLatestLabel));
        OnPropertyChanged(nameof(DetectedText));
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(NameWarning));
        if (IsInstalling) Tick();
    }

    /// <summary>让页面把焦点放到这一步（启动按钮在缺东西时点了）。</summary>
    public void RequestFocus(OnboardingStep step) => FocusRequested?.Invoke(step);

    /// <summary>自检用：当作下载进度已经停了一阵。</summary>
    internal void SimulateSlow() => _lastProgress = DateTime.UtcNow - SlowAfter - TimeSpan.FromSeconds(1);

    private sealed class LoaderUnavailableException(LoaderKind kind, string mcVersion) : Exception
    {
        public LoaderKind Kind { get; } = kind;
        public string McVersion { get; } = mcVersion;
    }
}

public enum OnboardingStep { Account, Game }
