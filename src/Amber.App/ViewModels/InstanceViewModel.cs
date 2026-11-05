using Amber.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class ModItem(LocalMod mod) : LocalizedObservable
{
    public LocalMod Mod { get; private set; } = mod;
    public string Name => Mod.Name;
    public string Meta => string.Join(" · ", new[] { Mod.Version, Mod.Authors, Mod.FileName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string? Description => Mod.Description;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = mod.Enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial ModUpdate? Update { get; set; }

    public bool HasUpdate => Update is not null;
    public string UpdateText => Update is null ? "" : L.F("instance.mods.update_available", Update.Latest.FileName);

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    /// <summary>和实例的匹配情况（加载器、游戏版本、前置）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompatText), nameof(HasCompatProblem), nameof(CanFindMatching))]
    public partial CompatResult? Compat { get; set; }

    public ModRequirements? Requirements => Mod.Requirements;
    public string CompatText => Compat is null || Compat.Level == CompatLevel.Ok && Compat.Text == CompatResult.MatchText ? "" : Compat.Text;
    public bool HasCompatProblem => Compat?.IsProblem == true;

    /// <summary>启用状态改了，实例页要重新检查前置。</summary>
    public Action? EnabledChanged { get; set; }
    public bool CanFindMatching => Compat?.Level is CompatLevel.WrongLoader or CompatLevel.WrongVersion;

    partial void OnEnabledChanged(bool value)
    {
        if (value == Mod.Enabled) return;
        try
        {
            var path = ModManager.SetEnabled(Mod, value);
            Mod = Mod with { Path = path, FileName = Path.GetFileName(path), Enabled = value };
            OnPropertyChanged(nameof(Meta));
            EnabledChanged?.Invoke();
        }
        catch (IOException)
        {
            // 游戏运行时文件被占用，改不了就恢复勾选状态。
            Dispatcher.UIThread.Post(() => Enabled = Mod.Enabled);
        }
    }

    partial void OnUpdateChanged(ModUpdate? value) => OnPropertyChanged(nameof(UpdateText));
}

public sealed record WorldItem(WorldInfo World) : LocalizedRecord
{
    public string Name => World.Name;
    public string Meta => $"{World.GameMode} · {World.Version ?? L.T("instance.worlds.unknown_version")} · {World.LastPlayed?.LocalDateTime:yyyy-MM-dd HH:mm} · {World.FolderName}";
}

public sealed partial class DatapackItem(LocalDatapack pack, string worldPath, Action<string> report) : LocalizedObservable
{
    public LocalDatapack Pack { get; } = pack;
    public string Name => Pack.FileName;
    public string Meta => (Pack.IsFolder ? L.T("common.folder") : L.T("common.archive")) + (string.IsNullOrWhiteSpace(Pack.Description) ? "" : " · " + Pack.Description.ReplaceLineEndings(" "));

    [ObservableProperty]
    public partial bool Enabled { get; set; } = pack.Enabled;

    partial void OnEnabledChanged(bool value)
    {
        try
        {
            DatapackManager.SetEnabled(worldPath, Pack.FileName, value);
            report(L.F("instance.datapacks.toggled", value ? L.T("common.enabled") : L.T("common.disabled"), Pack.FileName));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            report(L.F("instance.status.change_failed", e.Message));
            Dispatcher.UIThread.Post(() => Enabled = !value);
        }
    }
}

public sealed partial class ScreenshotItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }
}

public sealed record PackFileItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Size
    {
        get
        {
            var bytes = File.Exists(Path) ? new FileInfo(Path).Length : Toolbox.SizeOf([Path]);
            return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1} MB" : $"{bytes / 1024.0:F0} KB";
        }
    }
}

public sealed partial class ServerItem(ServerEntry entry) : LocalizedObservable
{
    public ServerEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Address => Entry.Address;

    private string? _statusText;
    public string StatusText
    {
        get => _statusText ?? L.T("instance.servers.querying");
        set => SetProperty(ref _statusText, value);
    }
}

public sealed partial class ExportOption(string name, bool include) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool Include { get; set; } = include;
}

public sealed record JavaChoice(string LabelValue, string? Path) : LocalizedRecord
{
    public string Label => Path is null ? L.T("instance.settings.inherit") : LabelValue;
}

/// <summary>实例页：一个版本自己的设置、模组、世界、截图、资源包、光影、服务器和导出。</summary>
public sealed partial class InstanceViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private readonly HashSet<int> _loadedTabs = [];
    private CancellationTokenSource? _workCts;

    public override string Title => L.T("instance.title");
    public override string Icon => "M4 4h16v5H4zM4 11h16v4H4zM4 17h16v3H4z";

    public ObservableCollection<string> Instances { get; } = [];

    [ObservableProperty]
    public partial string? SelectedId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstance), nameof(SupportsQuickPlay), nameof(IsSharedDir))]
    public partial GameInstance? Current { get; set; }

    /// <summary>版本隔离关闭：模组、资源包、光影都在 .minecraft 下，所有实例共用（改版规格 2.3）。</summary>
    public bool IsSharedDir => Current is { } c && _service.UsesSharedDir(c.Id);

    /// <summary>共享目录提示上的【去开启】：切到设置 Tab。</summary>
    [RelayCommand]
    private void GoToIsolationSetting() => TabIndex = 0;

    public bool HasInstance => Current is not null;

    [ObservableProperty]
    public partial string InfoText { get; set; } = "";

    [ObservableProperty]
    public partial int TabIndex { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    public InstanceViewModel(LauncherService service)
    {
        _service = service;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_active && _service.CurrentInstance is { } id && Instances.Contains(id)) SelectedId = id;
        });
        service.ContentChanged += () => Dispatcher.UIThread.Post(() => { _loadedTabs.Clear(); LoadTab(TabIndex); });
    }

    private bool _active;

    protected override void OnFirstActivated()
    {
        _active = true;
        RefreshInstances();
    }

    private void RefreshInstances()
    {
        if (!_active) return;
        Instances.Clear();
        foreach (var v in _service.InstalledVersions()) Instances.Add(v);
        SelectedId = _service.CurrentOr(Instances, Instances.FirstOrDefault());
    }

    partial void OnSelectedIdChanged(string? value)
    {
        _service.SetCurrentInstance(value);
        _loadedTabs.Clear();
        Current = null;
        InfoText = "";
        if (value is null) return;
        try
        {
            Current = _service.GetInstance(value);
            UpdateInfoText();
            LoadTab(TabIndex);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            InfoText = L.F("home.version.read_failed", e.Message);
        }
    }

    partial void OnTabIndexChanged(int value) => LoadTab(value);

    private void LoadTab(int tab)
    {
        if (Current is null || !_loadedTabs.Add(tab)) return;
        switch (tab)
        {
            case 0: LoadSettings(); break;
            case 1: LoadMods(); break;
            case 2: LoadWorlds(); break;
            case 3: LoadScreenshots(); break;
            case 4: LoadPackFiles(ResourcePacks, Current.ResourcePacksDir); break;
            case 5: LoadPackFiles(ShaderPacks, Current.ShaderPacksDir); break;
            case 6: LoadServers(); break;
            case 7: LoadExport(); break;
        }
    }

    private static void OpenPath(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// 删除：移进回收目录（TrashService），弹“已删除 X【撤销】”。afterRestore 在撤销恢复后调用（刷新列表）。
    /// 失败时写状态并返回 false，原文件不动。
    /// </summary>
    private bool Trash(string path, Action afterRestore, bool toast = true)
    {
        try
        {
            var entry = TrashService.MoveToTrash(path, _service.Directory.Root);
            if (toast) Toasts.Deleted(entry, restored =>
            {
                afterRestore();
                SetStatus(StatusMessage.Success("trash.restored", Path.GetFileName(restored)));
            });
            SetStatus(StatusMessage.Success("trash.deleted", entry.Name));
            return true;
        }
        catch (TrashException e)
        {
            SetStatus(StatusMessage.Failure("instance.status.delete_failed", e, Toasts.Describe(e)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("instance.status.delete_failed", e, ErrorText.Of(e)));
        }
        return false;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Current is { } c) OpenPath(c.WorkDir);
    }

    [RelayCommand]
    private async Task CompleteFilesAsync()
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            await _service.Installer.CompleteAsync(c.Id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
                {
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    SetStatus(StatusMessage.Info("instance.files.progress", p.Files.CompletedFiles, p.Files.TotalFiles));
                }
            }), ct);
            await Ui.FlushAsync();
            // 补全完了，上次没装完的实例也就装完了。
            _service.Directory.MarkInstallFinished(c.Id);
            UpdateInfoText();
            SetStatus(StatusMessage.Success("instance.files.complete_status"));
        });
    }

    /// <summary>
    /// 删除实例：弹对话框写明实例名和占用空间，确认后直接删除实例文件夹（不进回收目录，对话框里写明无法撤销，规格 3.9）。
    /// </summary>
    [RelayCommand]
    private async Task DeleteVersionAsync()
    {
        if (Current is not { } c) return;
        var dir = _service.Directory.VersionDir(c.Id);
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            return;
        var ok = await Views.ConfirmDialog.AskAsync(owner, L.T("instance.version.delete"),
            L.F("instance.delete.body", c.Id, L.T("instance.delete.computing")), L.T("common.confirm_delete"),
            () => Task.Run(() => L.F("instance.delete.body", c.Id, Fmt.Size(TrashService.SizeOf(dir)))));
        if (!ok) return;
        // 游戏还在用这个实例的文件：不删（删到一半会留下半个实例）。
        if (_service.IsRunningIn(_service.WorkDirFor(c.Id)) || _service.IsRunningIn(dir))
        {
            SetStatus(StatusMessage.Warning("instance.delete.running", c.Id));
            return;
        }
        try
        {
            // 整个文件夹改名进回收目录（同卷改名，有文件被占用时整体失败、原样不动），可以撤销。
            var entry = await Task.Run(() => TrashService.MoveToTrash(dir, _service.Directory.Root));
            SetStatus(StatusMessage.Success("instance.delete.done", c.Id));
            _service.NotifyInstalledChanged();
            Toasts.Deleted(entry, _ => _service.NotifyInstalledChanged());
        }
        catch (TrashException e)
        {
            SetStatus(StatusMessage.Error("instance.status.delete_failed", Toasts.Describe(e)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("instance.status.delete_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>离线身份的启动参数（预处理和导出脚本用），其余和正式启动一样。</summary>
    private async Task<LaunchOptions> OfflineLaunchOptionsAsync(GameInstance c, CancellationToken ct)
    {
        var settings = _service.EffectiveSettings(c.Id);
        var version = _service.Directory.ResolveVersion(c.Id);
        var java = !string.IsNullOrEmpty(settings.JavaPath) && File.Exists(settings.JavaPath) ? settings.JavaPath : await _service.Java.ForVersionAsync(version, ct);
        var account = _service.Accounts.Store.Selected;
        var session = account is { Kind: AccountKind.Offline } ? OfflineAccount.Create(account.Name) : OfflineAccount.Create("Player");
        return _service.BaseLaunchOptions(c.Id, java, session, []);
    }

    [ObservableProperty] public partial string ConnectorPendingText { get; set; } = "";
    [ObservableProperty] public partial bool HasConnectorPending { get; set; }

    private void RecheckConnectorPending(bool connectorInstalled)
    {
        if (Current is not { } c) return;
        if (!connectorInstalled)
        {
            // 还没装 Connector 时由“安装 Sinytra Connector”那一行提示，这里不用算。
            HasConnectorPending = false;
            ConnectorPendingText = "";
            return;
        }
        // 连续勾选时只认最后一次的结果：旧的还在算的作废，算完也不会盖掉新的。
        _pendingCheck?.Cancel();
        var cts = _pendingCheck = new CancellationTokenSource();
        var mods = Mods.Select(m => m.Mod).ToList();
        _ = UpdateAsync();

        async Task UpdateAsync()
        {
            List<LocalMod> pending;
            try
            {
                pending = await Task.Run(() => ConnectorWarmup.Pending(c.WorkDir, mods, c.Loader), cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                return;
            }
            // await 之后回到界面线程。
            if (cts.IsCancellationRequested || Current != c) return;
            HasConnectorPending = pending.Count > 0;
            ConnectorPendingText = pending.Count == 0 ? "" :
                L.P("instance.connector.pending", pending.Count, pending.Count, string.Join(L.T("common.list_separator"), pending.Take(3).Select(m => m.Name)), pending.Count > 3 ? L.T("common.ellipsis") : "");
        }
    }

    private CancellationTokenSource? _pendingCheck;

    /// <summary>预先处理：启动一次游戏，等 Connector 转换完就结束，下次正式启动就快了。</summary>
    [RelayCommand]
    private async Task PrewarmConnectorAsync()
    {
        if (Current is not { } c) return;
        if (_service.IsRunningIn(c.WorkDir))
        {
            // 同一个目录同时跑两个游戏，两边的 Connector 会一起写缓存。
            SetStatus(StatusMessage.Info("instance.connector.running"));
            return;
        }
        await RunWorkAsync(async ct =>
        {
            using var running = _service.MarkRunning(c.WorkDir);
            SetStatus(StatusMessage.Info("home.status.checking_game_files"));
            await _service.Installer.CompleteAsync(c.Id, null, ct);
            SetStatus(StatusMessage.Info("instance.connector.starting"));
            var options = await OfflineLaunchOptionsAsync(c, ct);
            var result = await ConnectorWarmup.RunAsync(new GameLauncher(_service.Directory), options,
                new Progress<string>(p => Status = p), TimeSpan.FromMinutes(15), ct);
            SetStatus(result.Done ? StatusMessage.Success("instance.connector.done", result.Detail) : StatusMessage.Warning("instance.connector.incomplete", result.Detail));
        });
        RecheckCompat();
    }

    public async Task ExportLaunchScriptAsync(string path)
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            // 脚本里的路径也要能被 Java 按当前系统语言读出来（和正式启动用同一套英文联接）。
            var (launcher, options) = new GameLauncher(_service.Directory).ForCurrentCodePage(await OfflineLaunchOptionsAsync(c, ct));
            var args = launcher.BuildArguments(options);
            Toolbox.WriteLaunchScript(path, options.JavaPath, options.WorkingDirectory ?? launcher.GameDir.Root, args, options.Session.AccessToken);
            SetStatus(StatusMessage.Success("instance.export.script_saved", path));
        });
    }

    private async Task RunWorkAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        _workCts = new CancellationTokenSource();
        try
        {
            await work(_workCts.Token);
        }
        catch (OperationCanceledException) when (_workCts.IsCancellationRequested)
        {
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (OperationCanceledException)
        {
            SetStatus(StatusMessage.Error("instance.status.timeout"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("instance.status.failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void CancelWork() => _workCts?.Cancel();

    // ---- 设置 ----

    /// <summary>两个下拉框各用一份（共用同一个列表时选中状态会互相影响）。</summary>
    public OptionItem[] IsolateChoices { get; } = OptionItem.Keys("instance.settings.inherit", "common.on", "common.off");
    public OptionItem[] FullscreenChoices { get; } = OptionItem.Keys("instance.settings.inherit", "common.on", "common.off");
    public OptionItem[] MemoryModes { get; } = OptionItem.Keys("instance.settings.inherit", "common.automatic", "common.manual");
    public ObservableCollection<JavaChoice> JavaChoices { get; } = [];

    [ObservableProperty] public partial int IsolateIndex { get; set; }
    [ObservableProperty] public partial int MemoryModeIndex { get; set; }
    [ObservableProperty] public partial string MemoryText { get; set; } = "";
    [ObservableProperty] public partial JavaChoice? SelectedJava { get; set; }
    [ObservableProperty] public partial string JvmArgs { get; set; } = "";
    [ObservableProperty] public partial string GameArgs { get; set; } = "";
    [ObservableProperty] public partial string WidthText { get; set; } = "";
    [ObservableProperty] public partial string HeightText { get; set; } = "";
    [ObservableProperty] public partial int FullscreenIndex { get; set; }
    [ObservableProperty] public partial string PreLaunch { get; set; } = "";
    [ObservableProperty] public partial string PostExit { get; set; } = "";
    [ObservableProperty] public partial string JoinServer { get; set; } = "";

    private void LoadSettings()
    {
        var s = InstanceSettings.Load(_service.Directory, Current!.Id);
        IsolateIndex = s.Isolate switch { true => 1, false => 2, _ => 0 };
        MemoryModeIndex = s.AutoMemory switch { true => 1, false => 2, _ => 0 };
        MemoryText = s.MaxMemoryMb?.ToString() ?? "";
        JvmArgs = s.JvmArgs ?? "";
        GameArgs = s.GameArgs ?? "";
        WidthText = s.WindowWidth?.ToString() ?? "";
        HeightText = s.WindowHeight?.ToString() ?? "";
        FullscreenIndex = s.Fullscreen switch { true => 1, false => 2, _ => 0 };
        PreLaunch = s.PreLaunchCommand ?? "";
        PostExit = s.PostExitCommand ?? "";
        JoinServer = s.JoinServer ?? "";
        _ = LoadJavaChoicesAsync(s.JavaPath);
    }

    private async Task LoadJavaChoicesAsync(string? current)
    {
        // 扫描要一会儿，这期间下拉框不留空。
        if (JavaChoices.Count == 0)
        {
            JavaChoices.Add(new JavaChoice("", null));
            SelectedJava = JavaChoices[0];
        }
        var found = await _service.Java.ScanAsync();
        JavaChoices.Clear();
        JavaChoices.Add(new JavaChoice("", null));
        foreach (var j in found) JavaChoices.Add(new JavaChoice(j.ToString(), j.JavaPath));
        if (current is not null && !found.Any(j => j.JavaPath == current)) JavaChoices.Add(new JavaChoice(current, current));
        SelectedJava = JavaChoices.FirstOrDefault(c => c.Path == current) ?? JavaChoices[0];
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (Current is not { } c) return;
        var s = new InstanceSettings
        {
            Isolate = IsolateIndex switch { 1 => true, 2 => false, _ => null },
            AutoMemory = MemoryModeIndex switch { 1 => true, 2 => false, _ => null },
            MaxMemoryMb = int.TryParse(MemoryText, out var mb) && mb > 0 ? mb : null,
            JavaPath = SelectedJava?.Path,
            JvmArgs = NullIfBlank(JvmArgs),
            GameArgs = NullIfBlank(GameArgs),
            WindowWidth = int.TryParse(WidthText, out var w) && w > 0 ? w : null,
            WindowHeight = int.TryParse(HeightText, out var h) && h > 0 ? h : null,
            Fullscreen = FullscreenIndex switch { 1 => true, 2 => false, _ => null },
            PreLaunchCommand = NullIfBlank(PreLaunch),
            PostExitCommand = NullIfBlank(PostExit),
            JoinServer = NullIfBlank(JoinServer),
        };
        s.Save(_service.Directory, c.Id);
        var id = c.Id;
        OnSelectedIdChanged(id);
        SetStatus(StatusMessage.Success("instance.settings.saved", id));
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---- 模组 ----

    public ObservableCollection<ModItem> Mods { get; } = [];

    [ObservableProperty] public partial string ModSummary { get; set; } = "";
    [ObservableProperty] public partial bool NeedsConnector { get; set; }
    [ObservableProperty] public partial string CompatSummary { get; set; } = "";

    private void LoadMods() => _ = LoadModsAsync();

    private int _modsGeneration;

    private async Task LoadModsAsync()
    {
        if (Current is not { } c) return;
        // 快速切换实例或连续刷新时，只用最后一次读的结果；旧的读完不再往列表里加（否则会混进别的实例的模组、重复显示）。
        var generation = ++_modsGeneration;
        Mods.Clear();
        _modsLoadedFor = null;
        var list = await Task.Run(() => new ModManager(c.ModsDir).List());
        if (generation != _modsGeneration || Current != c) return;
        foreach (var mod in list)
        {
            var item = new ModItem(mod) { EnabledChanged = RecheckCompat };
            Mods.Add(item);
            if (mod.Icon is { } icon) _ = DecodeIconAsync(item, icon);
        }
        ModSummary = L.P("instance.mods.summary", list.Count, list.Count, list.Count(m => m.Enabled));
        _modsLoadedFor = c;
        RecheckCompat();
    }

    /// <summary>Mods 列表现在装的是哪个实例的模组（加载完才设；加载中或者换了实例时为 null）。</summary>
    private GameInstance? _modsLoadedFor;

    /// <summary>按当前启用的模组重新算一遍每个模组的匹配情况（启用、禁用、删除之后也要重算）。</summary>
    private void UpdateInfoText()
    {
        if (Current is { } c)
            InfoText = _service.WithStateNote(c.Id, L.F("instance.version.info", c.McVersion, c.Loader is null ? L.T("home.version.vanilla") : $"{c.Loader} {c.LoaderVersion}", c.WorkDir));
    }

    protected override void OnLanguageChanged()
    {
        UpdateInfoText();
        // 只在模组页已经加载完、而且是当前实例的模组时重算（加载中或者刚换实例时列表可能还是上一个实例的）。
        if (_modsLoadedFor is { } loaded && ReferenceEquals(loaded, Current))
        {
            ModSummary = L.P("instance.mods.summary", Mods.Count, Mods.Count, Mods.Count(m => m.Enabled));
            // 兼容提示是检查时按当时的语言写的，重新检查一遍（只读已经扫描好的信息，很快）。
            RecheckCompat();
        }
    }

    private void RecheckCompat()
    {
        if (Current is not { } c) return;
        var enabled = Mods.Where(m => m.Enabled).ToList();
        var ids = new HashSet<string>(enabled.SelectMany(m => m.Requirements?.Ids ?? []), StringComparer.OrdinalIgnoreCase);
        // Forgified Fabric API 提供 fabric-api。
        if (ids.Contains("fabric_api") || enabled.Any(m => m.Mod.FileName.Contains("forgified-fabric-api", StringComparison.OrdinalIgnoreCase))) ids.Add("fabric-api");
        var connector = ConnectorInstaller.IsInstalled(enabled.Select(m => m.Requirements));
        foreach (var m in Mods) m.Compat = m.Enabled ? ModCompat.Check(m.Requirements, c.Loader, c.McVersion, ids, connector) : null;
        var problems = Mods.Count(m => m.HasCompatProblem);
        NeedsConnector = !connector && Mods.Any(m => m.Compat?.Level == CompatLevel.NeedsConnector);
        CompatSummary = problems == 0 ? (Mods.Count > 0 ? L.T("instance.mods.all_matching") : "") : L.P("instance.mods.mismatched", problems, problems);
        RecheckConnectorPending(connector);
    }

    [RelayCommand]
    private async Task InstallConnectorAsync()
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            SetStatus(StatusMessage.Info("instance.connector.searching"));
            var plan = await ConnectorInstaller.PlanAsync(c, _service.Modrinth, _service.ContentInstaller, null, ct)
                       ?? throw new InvalidOperationException(L.F("instance.connector.not_found", c.McVersion, c.Loader));
            await _service.ContentInstaller.InstallAsync(plan, c.ModsDir, null, ct);
            SetStatus(StatusMessage.Success("instance.connector.installed", string.Join(L.T("common.list_separator"), plan.Files.Select(f => f.DisplayName))));
        });
        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task FindMatchingAsync(ModItem? item)
    {
        if (Current is not { } c || item is null) return;
        await RunWorkAsync(async ct =>
        {
            SetStatus(StatusMessage.Info("instance.mods.finding_matching", item.Name));
            var file = await ConnectorInstaller.FindMatchingVersionAsync(item.Mod.Path, c, _service.Modrinth, ct);
            if (file is null)
            {
                Status = L.F(c.Loader is "fabric" or "quilt" && item.Requirements?.Loaders.Contains("fabric") != true
                    ? "instance.mods.match_not_found_forge_unsupported" : "instance.mods.match_not_found",
                    item.Name, c.McVersion, c.Loader ?? L.T("home.version.vanilla"));
                return;
            }
            await _service.ContentInstaller.InstallAsync(new InstallPlan([file], []), c.ModsDir, null, ct);
            // 换下来的旧文件也进回收目录（不弹撤销），以后能在“最近删除”里找回。
            if (Trash(item.Mod.Path, () => { }, toast: false))
                SetStatus(StatusMessage.Success("instance.mods.replaced", item.Name, file.FileName));
        });
        await LoadModsAsync();
    }

    private static async Task DecodeIconAsync(ModItem item, byte[] bytes)
    {
        try
        {
            item.Icon = await Task.Run(() =>
            {
                using var memory = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(memory, 48);
            });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException) { /* 图标格式不对就不显示 */ }
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (Current is { } c) OpenPath(c.ModsDir);
    }

    [RelayCommand]
    private void DeleteMod(ModItem? item)
    {
        if (item is null || !Trash(item.Mod.Path, () => _ = LoadModsAsync())) return;
        Mods.Remove(item);
        ModSummary = L.P("instance.mods.summary", Mods.Count, Mods.Count, Mods.Count(m => m.Enabled));
        RecheckCompat();
    }

    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            SetStatus(StatusMessage.Info("instance.mods.checking_updates"));
            var mods = Mods.Select(m => m.Mod).ToList();
            var updates = await ModManager.CheckUpdatesAsync(mods, _service.Modrinth, _service.CurseForgeIfConfigured, c.McVersion, c.Loader, ct);
            foreach (var item in Mods) item.Update = updates.FirstOrDefault(u => u.LocalPath == item.Mod.Path);
            SetStatus(updates.Count == 0 ? StatusMessage.Success("instance.mods.up_to_date") : new StatusMessage("instance.mods.updates_available", [updates.Count]) { Count = updates.Count });
        });
    }

    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        await RunWorkAsync(async ct =>
        {
            var pending = Mods.Where(m => m.Update is not null).ToList();
            var done = 0;
            foreach (var item in pending)
            {
                SetStatus(StatusMessage.Info("instance.mods.updating", item.Name));
                await ModManager.ApplyUpdateAsync(item.Update!, _service.Downloader, ct);
                Progress = 100.0 * ++done / pending.Count;
            }
            SetStatus(pending.Count == 0 ? StatusMessage.Info("instance.mods.check_first") : new StatusMessage("instance.mods.updated_count", [done], StatusKind.Success) { Count = done });
        });
        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task UpdateModAsync(ModItem? item)
    {
        if (item?.Update is not { } update) return;
        await RunWorkAsync(async ct =>
        {
            await ModManager.ApplyUpdateAsync(update, _service.Downloader, ct);
            SetStatus(StatusMessage.Success("instance.mods.updated", item.Name));
        });
        await LoadModsAsync();
    }

    public void AddFiles(IEnumerable<string> paths, string targetDir)
    {
        System.IO.Directory.CreateDirectory(targetDir);
        var count = 0;
        foreach (var path in paths)
        {
            File.Copy(path, Path.Combine(targetDir, Path.GetFileName(path)), true);
            count++;
        }
        Status = L.P("instance.files.added", count, count);
        _loadedTabs.Clear();
        LoadTab(TabIndex);
    }

    public string? ModsDir => Current?.ModsDir;
    public string? ResourcePacksDir => Current?.ResourcePacksDir;
    public string? ShaderPacksDir => Current?.ShaderPacksDir;
    public string? SavesDir => Current?.SavesDir;

    // ---- 世界 ----

    public ObservableCollection<WorldItem> Worlds { get; } = [];

    /// <summary>1.20 以上才能启动时直接进存档，版本 JSON 里有对应参数就算支持。</summary>
    public bool SupportsQuickPlay
    {
        get
        {
            if (Current is not { } c) return false;
            try
            {
                return _service.Directory.ResolveVersion(c.Id).Arguments?.Game.Any(e => e.ToString().Contains("quickPlaySingleplayer", StringComparison.Ordinal)) == true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                return false;
            }
        }
    }

    private void LoadWorlds()
    {
        DatapackWorld = null;
        Worlds.Clear();
        // 撤销删除世界时实例可能已经被删了、或者选中的是读不了的实例。
        if (Current is not { } c) return;
        foreach (var w in WorldManager.List(c.SavesDir)) Worlds.Add(new WorldItem(w));
    }

    [RelayCommand]
    private void JoinWorld(WorldItem? item)
    {
        if (Current is not { } c || item is null) return;
        _service.RequestLaunch(new LaunchRequest(c.Id, JoinWorld: item.World.FolderName));
        NavigateHome?.Invoke();
    }

    [RelayCommand]
    private async Task BackupWorldAsync(WorldItem? item)
    {
        if (Current is not { } c || item is null) return;
        await RunWorkAsync(async _ =>
        {
            SetStatus(StatusMessage.Info("instance.worlds.backing_up", item.Name));
            var target = await Task.Run(() => WorldManager.Backup(item.World, Path.Combine(c.WorkDir, "backups")));
            SetStatus(StatusMessage.Success("instance.worlds.backed_up", target));
        });
    }

    [RelayCommand]
    private void OpenWorld(WorldItem? item)
    {
        if (item is not null) OpenPath(item.World.Path);
    }

    [RelayCommand]
    private void DeleteWorld(WorldItem? item)
    {
        // 界面上是两步确认（ConfirmButton），到这里已经确认过了。
        if (item is null || !Trash(item.World.Path, LoadWorlds)) return;
        if (DatapackWorld == item) DatapackWorld = null;
        Worlds.Remove(item);
    }

    public void ImportWorld(string zipPath)
    {
        if (Current is not { } c) return;
        try
        {
            System.IO.Directory.CreateDirectory(c.SavesDir);
            var folder = WorldManager.Import(zipPath, c.SavesDir);
            SetStatus(StatusMessage.Success("instance.worlds.imported", folder));
            LoadWorlds();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            SetStatus(StatusMessage.Failure("instance.worlds.import_failed", e, ErrorText.Of(e)));
        }
    }

    // ---- 数据包（属于某个世界）----

    public ObservableCollection<DatapackItem> Datapacks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatapackWorld), nameof(DatapackTitle))]
    public partial WorldItem? DatapackWorld { get; set; }

    public bool HasDatapackWorld => DatapackWorld is not null;
    public string DatapackTitle => DatapackWorld is { } w ? L.F("instance.datapacks.world_title", w.Name) : "";
    public string? DatapacksDir => DatapackWorld is { } w ? DatapackManager.DirectoryOf(w.World.Path) : null;

    /// <summary>主窗口设置：去资源页搜索数据包（版本 id，世界文件夹名）。</summary>
    public Action<string, string>? NavigateToDatapacks { get; set; }

    [RelayCommand]
    private void ShowDatapacks(WorldItem? item)
    {
        DatapackWorld = item;
        LoadDatapacks();
    }

    [RelayCommand]
    private void CloseDatapacks() => DatapackWorld = null;

    private void LoadDatapacks()
    {
        Datapacks.Clear();
        if (DatapackWorld is not { } w) return;
        foreach (var pack in DatapackManager.List(w.World.Path)) Datapacks.Add(new DatapackItem(pack, w.World.Path, s => Status = s));
    }

    [RelayCommand]
    private void DeleteDatapack(DatapackItem? item)
    {
        if (item is null || !Trash(item.Pack.Path, LoadDatapacks)) return;
        Datapacks.Remove(item);
    }

    [RelayCommand]
    private void OpenDatapacksFolder()
    {
        if (DatapacksDir is { } dir) OpenPath(dir);
    }

    [RelayCommand]
    private void BrowseDatapacks()
    {
        if (Current is { } c && DatapackWorld is { } w) NavigateToDatapacks?.Invoke(c.Id, w.World.FolderName);
    }

    public void AddDatapacks(IEnumerable<string> paths)
    {
        if (DatapackWorld is not { } w) return;
        var added = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                DatapackManager.Add(w.World.Path, path);
                added++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                errors.Add(e.Message);
            }
        }
        Status = errors.Count > 0
            ? L.F("instance.datapacks.added_with_errors", L.P("instance.datapacks.added", added, added), string.Join(L.T("common.error_separator"), errors))
            : L.P("instance.datapacks.added", added, added);
        LoadDatapacks();
    }

    // ---- 截图 ----

    public ObservableCollection<ScreenshotItem> Screenshots { get; } = [];

    private void LoadScreenshots() => _ = LoadScreenshotsAsync();

    private async Task LoadScreenshotsAsync()
    {
        if (Current is not { } c) return;
        Screenshots.Clear();
        if (!System.IO.Directory.Exists(c.ScreenshotsDir)) return;
        var files = System.IO.Directory.EnumerateFiles(c.ScreenshotsDir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).Take(200).ToList();
        foreach (var file in files) Screenshots.Add(new ScreenshotItem(file));
        foreach (var item in Screenshots.ToList())
        {
            try
            {
                item.Thumbnail = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(item.Path);
                    return Bitmap.DecodeToWidth(stream, 240);
                });
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NullReferenceException) { }
        }
    }

    [RelayCommand]
    private void OpenScreenshot(ScreenshotItem? item)
    {
        if (item is not null) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenScreenshotsFolder()
    {
        if (Current is { } c) OpenPath(c.ScreenshotsDir);
    }

    [RelayCommand]
    private void DeleteScreenshot(ScreenshotItem? item)
    {
        if (item is null || !Trash(item.Path, LoadScreenshots)) return;
        Screenshots.Remove(item);
    }

    // ---- 资源包、光影 ----

    public ObservableCollection<PackFileItem> ResourcePacks { get; } = [];
    public ObservableCollection<PackFileItem> ShaderPacks { get; } = [];

    private void ReloadPackFiles()
    {
        if (Current is not { } c) return;
        LoadPackFiles(ResourcePacks, c.ResourcePacksDir);
        LoadPackFiles(ShaderPacks, c.ShaderPacksDir);
    }

    private static void LoadPackFiles(ObservableCollection<PackFileItem> target, string dir)
    {
        target.Clear();
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(dir).Order()) target.Add(new PackFileItem(path));
    }

    [RelayCommand]
    private void OpenResourcePacksFolder()
    {
        if (Current is { } c) OpenPath(c.ResourcePacksDir);
    }

    [RelayCommand]
    private void OpenShaderPacksFolder()
    {
        if (Current is { } c) OpenPath(c.ShaderPacksDir);
    }

    [RelayCommand]
    private void DeletePack(PackFileItem? item)
    {
        if (item is null || !Trash(item.Path, ReloadPackFiles)) return;
        ResourcePacks.Remove(item);
        ShaderPacks.Remove(item);
    }

    // ---- 服务器 ----

    public ObservableCollection<ServerItem> Servers { get; } = [];

    [ObservableProperty] public partial string NewServerName { get; set; } = "";
    [ObservableProperty] public partial string NewServerAddress { get; set; } = "";

    private void LoadServers()
    {
        Servers.Clear();
        foreach (var s in ServerList.Read(Current!.WorkDir))
        {
            var item = new ServerItem(s);
            Servers.Add(item);
            _ = PingAsync(item);
        }
    }

    private static async Task PingAsync(ServerItem item)
    {
        var status = await ServerList.PingAsync(item.Address);
        item.StatusText = status.Online
            ? L.F("instance.servers.online", status.Players, status.MaxPlayers, status.VersionName, status.PingMs is { } ms ? $" · {ms} ms" : "")
            : L.F("instance.servers.offline", status.Error);
    }

    [RelayCommand]
    private void AddServer()
    {
        if (Current is not { } c || string.IsNullOrWhiteSpace(NewServerAddress)) return;
        var address = NewServerAddress.Trim();
        var name = string.IsNullOrWhiteSpace(NewServerName) ? address : NewServerName.Trim();
        System.IO.Directory.CreateDirectory(c.WorkDir);
        ServerList.Add(c.WorkDir, new ServerEntry(name, address));
        NewServerName = "";
        NewServerAddress = "";
        LoadServers();
    }

    [RelayCommand]
    private void RemoveServer(ServerItem? item)
    {
        if (Current is not { } c || item is null) return;
        var index = Servers.IndexOf(item);
        var removed = ServerList.Remove(c.WorkDir, item.Address, item.Name, index);
        Servers.Remove(item);
        SetStatus(StatusMessage.Success("trash.deleted", item.Name));
        if (removed is null) return;
        var workDir = c.WorkDir;
        Toasts.Removed(item.Name, () =>
        {
            ServerList.Restore(workDir, removed);
            // 还在看同一个实例时把这一项放回列表原来的位置。
            if (Current?.WorkDir == workDir && !Servers.Contains(item)) Servers.Insert(Math.Clamp(index, 0, Servers.Count), item);
            SetStatus(StatusMessage.Success("trash.restored", item.Name));
        });
    }

    [RelayCommand]
    private void JoinServerNow(ServerItem? item)
    {
        if (Current is not { } c || item is null) return;
        _service.RequestLaunch(new LaunchRequest(c.Id, JoinServer: item.Address));
        NavigateHome?.Invoke();
    }

    [RelayCommand]
    private void RefreshServers()
    {
        if (Current is not null) LoadServers();
    }

    /// <summary>主窗口设置：启动请求发出后切回启动页。</summary>
    public Action? NavigateHome { get; set; }

    // ---- 导出 ----

    public OptionItem[] ExportFormats { get; } = OptionItem.Keys("instance.export.modrinth", "instance.export.curseforge");
    public ObservableCollection<ExportOption> ExportOptions { get; } = [];

    [ObservableProperty] public partial int ExportFormatIndex { get; set; }
    [ObservableProperty] public partial string ExportName { get; set; } = "";
    [ObservableProperty] public partial string ExportVersion { get; set; } = "1.0.0";

    private void LoadExport()
    {
        var c = Current!;
        ExportName = c.Id;
        ExportOptions.Clear();
        foreach (var name in ModpackService.ExportCandidates(c.WorkDir))
        {
            // 默认带上玩家会想分享的内容。
            var defaultOn = name is "mods" or "config" or "resourcepacks" or "shaderpacks" or "options.txt" or "defaultconfigs" or "kubejs";
            ExportOptions.Add(new ExportOption(name, defaultOn));
        }
    }

    public string ExportExtension => ExportFormatIndex == 0 ? "mrpack" : "zip";

    public async Task ExportPackAsync(string path)
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            var include = ExportOptions.Where(o => o.Include).Select(o => o.Name).ToList();
            SetStatus(StatusMessage.Info("instance.export.packing"));
            if (ExportFormatIndex == 0)
                await _service.Modpacks.ExportMrpackAsync(c, path, ExportName, ExportVersion, include, ct);
            else
                await _service.Modpacks.ExportCurseForgeAsync(c, path, ExportName, ExportVersion, Environment.UserName, include, ct);
            SetStatus(StatusMessage.Success("instance.export.saved", path, new FileInfo(path).Length / 1024));
        });
    }

    public async Task RunSnapshotDemoAsync()
    {
        TabIndex = 1;
        await Task.Delay(1500);
    }
}
