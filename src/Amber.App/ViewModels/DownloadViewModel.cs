using Amber.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>下载页的一行。</summary>
public sealed partial class RemoteVersionItem(ManifestEntry entry, bool installed) : LocalizedObservable
{
    public ManifestEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string TypeText => Entry.Type switch
    {
        "release" => L.T("download.filter.release"),
        "snapshot" => L.T("download.filter.snapshot"),
        "old_beta" => L.T("download.version.old_beta"),
        "old_alpha" => L.T("download.version.old_alpha"),
        var t => t,
    };
    public string Date => Entry.ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");

    [ObservableProperty]
    public partial bool Installed { get; set; } = installed;
}

public sealed record LoaderChoice(string Name, LoaderKind? Kind) : LocalizedRecord
{
    public string Label => Kind is null ? L.T("home.version.vanilla") : Name;
}

public sealed partial class DownloadViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private List<ManifestEntry> _all = [];
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _installCts;

    public override string Title => L.T("download.title");
    public override string Icon => "M11 3h2v9.2l3.6-3.6L18 10l-6 6-6-6 1.400-1.400 3.600 3.600zM5 18h14v2H5z";

    public OptionItem[] Filters { get; } = OptionItem.Keys("download.filter.release", "download.filter.snapshot", "download.filter.old", "common.all");
    public ObservableCollection<RemoteVersionItem> Items { get; } = [];

    public LoaderChoice[] LoaderChoices { get; } =
    [
        new("", null),
        new("Fabric", LoaderKind.Fabric),
        new("Quilt", LoaderKind.Quilt),
        new("Forge", LoaderKind.Forge),
        new("NeoForge", LoaderKind.NeoForge),
        new("OptiFine", LoaderKind.OptiFine),
    ];

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = [];

    private int _filterIndex;
    public OptionItem Filter
    {
        get => Filters[_filterIndex];
        set
        {
            var index = Array.IndexOf(Filters, value);
            if (index >= 0 && SetProperty(ref _filterIndex, index)) ApplyFilter();
        }
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>正在配置安装的版本；非空时显示右侧的安装面板。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfiguring), nameof(InstallTitle))]
    public partial RemoteVersionItem? Target { get; set; }

    public bool IsConfiguring => Target is not null;
    public string InstallTitle => L.F("download.install.title", Target?.Id);

    /// <summary>刚装好的实例；非空时右侧显示成功卡（立即启动、添加模组、打开实例）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstalled), nameof(InstalledTitle), nameof(InstalledHasLoader))]
    public partial string? JustInstalled { get; set; }

    public bool HasInstalled => JustInstalled is not null;
    public string InstalledTitle => L.F("download.installed.title", JustInstalled);

    /// <summary>原版实例装不了模组，成功卡不给“添加模组”。</summary>
    public bool InstalledHasLoader { get; private set; }

    /// <summary>成功卡上的去处（MainViewModel 接上）。</summary>
    public Action<string>? LaunchInstance { get; set; }
    public Action<string>? OpenInstance { get; set; }
    public Action<string>? BrowseModsFor { get; set; }

    [RelayCommand]
    private void LaunchInstalled()
    {
        if (JustInstalled is { } id) LaunchInstance?.Invoke(id);
    }

    [RelayCommand]
    private void OpenInstalled()
    {
        if (JustInstalled is { } id) OpenInstance?.Invoke(id);
    }

    [RelayCommand]
    private void AddModsToInstalled()
    {
        if (JustInstalled is { } id) BrowseModsFor?.Invoke(id);
    }

    [RelayCommand]
    private void CloseInstalled() => JustInstalled = null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLoaderVersion))]
    public partial LoaderChoice SelectedLoader { get; set; }

    public bool NeedsLoaderVersion => SelectedLoader.Kind is not null;

    [ObservableProperty]
    public partial LoaderVersion? SelectedLoaderVersion { get; set; }

    [ObservableProperty]
    public partial bool ShowUnstableLoaders { get; set; }

    [ObservableProperty]
    public partial string LoaderHint { get; set; } = "";

    /// <summary>加载器提示的写法（换语言时重新算）。</summary>
    private Func<string>? _loaderHint;

    private void SetLoaderHint(Func<string>? text)
    {
        _loaderHint = text;
        LoaderHint = text?.Invoke() ?? "";
    }

    protected override void OnLanguageChanged()
    {
        if (_loaderHint is not null) LoaderHint = _loaderHint();
        RelabelForgeNotes();
    }

    /// <summary>正在按新语言换 Forge 备注：重新选中同一个版本时不要改用户填的名字。</summary>
    private bool _relabeling;

    /// <summary>
    /// Forge 的“推荐 / 最新”备注是取列表时按当时的语言写的，换语言时换掉。
    /// 只动 Forge 的行（OptiFine 的备注写的是对应的 Forge 版本，不能改）。
    /// </summary>
    internal void RelabelForgeNotes()
    {
        _relabeling = true;
        try
        {
            for (var i = 0; i < LoaderVersions.Count; i++)
                if (LoaderVersions[i] is { Kind: LoaderKind.Forge, Note: not null } v)
                {
                    var selected = SelectedLoaderVersion == v;
                    LoaderVersions[i] = v with { Note = L.T(v.Recommended ? "core.install.forge.recommended" : "core.install.forge.latest") };
                    if (selected) SelectedLoaderVersion = LoaderVersions[i];
                }
        }
        finally
        {
            _relabeling = false;
        }
    }

    [ObservableProperty]
    public partial string CustomName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    public DownloadViewModel(LauncherService service)
    {
        _service = service;
        SelectedLoader = LoaderChoices[0];
    }

    protected override void OnFirstActivated() => _ = RefreshAsync();

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedLoaderChanged(LoaderChoice value) => _ = LoadLoaderVersionsAsync();
    partial void OnShowUnstableLoadersChanged(bool value) => _ = LoadLoaderVersionsAsync();
    partial void OnSelectedLoaderVersionChanged(LoaderVersion? value)
    {
        if (!_relabeling) UpdateDefaultName();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        SetStatus(StatusMessage.Info("download.status.fetching_versions"));
        try
        {
            var manifest = await _service.Installer.GetManifestAsync();
            _all = manifest.Versions;
            ApplyFilter();
            SetStatus(StatusMessage.Success("download.status.latest", manifest.Latest.GetValueOrDefault("release"), manifest.Latest.GetValueOrDefault("snapshot")));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("download.status.fetch_failed", e, ErrorText.Of(e)));
        }
    }

    private void ApplyFilter()
    {
        var installed = _service.InstalledVersions().ToHashSet();
        IEnumerable<ManifestEntry> list = _filterIndex switch
        {
            0 => _all.Where(v => v.Type == "release"),
            1 => _all.Where(v => v.Type == "snapshot"),
            2 => _all.Where(v => v.Type is "old_beta" or "old_alpha"),
            _ => _all,
        };
        if (!string.IsNullOrWhiteSpace(Search)) list = list.Where(v => v.Id.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
        Items.Clear();
        foreach (var v in list) Items.Add(new RemoteVersionItem(v, installed.Contains(v.Id)));
    }

    [RelayCommand]
    private void Configure(RemoteVersionItem? item)
    {
        Target = item;
        if (item is not null) JustInstalled = null;
        SelectedLoader = LoaderChoices[0];
        UpdateDefaultName();
    }

    [RelayCommand]
    private void CloseConfigure()
    {
        Target = null;
        _listCts?.Cancel();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        _listCts?.Cancel();
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        SetLoaderHint(null);
        UpdateDefaultName();
        if (Target is not { } target || SelectedLoader.Kind is not { } kind) return;

        var cts = _listCts = new CancellationTokenSource();
        SetLoaderHint(() => L.T("download.status.fetching_versions"));
        try
        {
            var versions = await LoaderInstallers.Create(kind, _service.Loaders).ListAsync(target.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var v in versions.Where(v => v.Stable || ShowUnstableLoaders)) LoaderVersions.Add(v);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Recommended) ?? LoaderVersions.FirstOrDefault();
            var loader = SelectedLoader;
            var targetId = target.Id;
            SetLoaderHint(versions.Count == 0 ? () => L.F("download.loader.unsupported", loader.Label, targetId)
                : LoaderVersions.Count == 0 ? () => L.T("download.loader.only_unstable")
                : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            var message = e.Message;
            SetLoaderHint(() => L.F("download.loader.fetch_failed", message));
        }
    }

    private void UpdateDefaultName()
    {
        if (Target is null) return;
        CustomName = SelectedLoader.Kind is null ? Target.Id : SelectedLoaderVersion?.DefaultId ?? "";
    }

    private bool CanInstall() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Target is not { } item) return;
        var loader = SelectedLoader.Kind;
        if (loader is not null && SelectedLoaderVersion is null)
        {
            SetStatus(StatusMessage.Warning("download.status.loader_required"));
            return;
        }

        IsBusy = true;
        var installCts = _installCts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var ct = installCts.Token;
        // 整个安装都用开始时的游戏目录和安装器：装到一半在设置里换了目录，也不会一半装进旧目录、一半装进新目录。
        var ctx = _service.Context;
        var acceptingProgress = true;
        bool AcceptProgress() => acceptingProgress && IsBusy && ReferenceEquals(_installCts, installCts) && !ct.IsCancellationRequested;
        // 加载器的标记由安装入口放置；Forge 系必须先保留新目录，不能在这里提前创建目标。
        var expectedId = loader is null ? item.Id : string.IsNullOrWhiteSpace(CustomName) ? SelectedLoaderVersion!.DefaultId : CustomName.Trim();
        try
        {
            string id;
            if (loader is not null)
            {
                var chosen = SelectedLoaderVersion!;
                SetStatus(StatusMessage.Info("download.status.installing", chosen.DefaultId));
                id = await _service.InstallLoaderAsync(ctx, chosen, CustomName, new Progress<LoaderProgress>(p =>
                {
                    if (!AcceptProgress()) return;
                    Status = p.Stage;
                    if (p.Percent is { } percent) Progress = percent;
                }), ct);
            }
            else
            {
                id = item.Id;
                try { _service.MarkInstallStarted(ctx, id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                SetStatus(StatusMessage.Info("download.status.installing", id));
                await ctx.Installer.InstallAsync(id, new Progress<InstallProgress>(p =>
                {
                    if (!AcceptProgress()) return;
                    if (p.Stage != InstallStage.Files || p.Files.TotalFiles == 0) return;
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    var mb = p.Files.BytesDownloaded / 1024.0 / 1024;
                    SetStatus(StatusMessage.Info("download.status.progress", id, p.Files.CompletedFiles, p.Files.TotalFiles, mb, mb / Math.Max(watch.Elapsed.TotalSeconds, 0.1)));
                }), ct);
                item.Installed = true;
            }
            await Ui.FlushAsync();
            acceptingProgress = false;
            var elapsedSeconds = watch.Elapsed.TotalSeconds;
            Status = L.P("download.status.installed", (long)Math.Round(elapsedSeconds), id, elapsedSeconds);
            Target = null;
            _service.MarkInstallFinished(ctx, id);
            if (id != expectedId) _service.MarkInstallFinished(ctx, expectedId);
            // 装的时候换了游戏目录：装好的实例在旧目录里，不把它设成当前实例，也不提示“打开实例”。
            var sameDirectory = _service.IsCurrentDirectory(ctx);
            _service.NotifyInstalledChanged();
            if (sameDirectory) _service.SetCurrentInstance(id);
            InstalledHasLoader = loader is not null;
            JustInstalled = id;
            // 装的时候切到别的页面了：用 Toast 告诉一声，带“打开实例”。
            if (!IsCurrent && sameDirectory) Toasts.WithAction(L.F("download.installed.title", id), L.T("download.installed.open"), () => OpenInstance?.Invoke(id));
        }
        // 不是玩家点的取消（比如连接超时也抛 TaskCanceledException）按失败显示，不能说成“已取消”。
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            acceptingProgress = false;
            SetStatus(StatusMessage.Warning("download.status.cancelled"));
        }
        catch (Exception e)
        {
            acceptingProgress = false;
            SetStatus(StatusMessage.Failure("common.install_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            // Progress 回调可能还在 UI 队列中；结束后的旧进度不能覆盖本次结局或下一次安装。
            acceptingProgress = false;
            // 刷新失败或取消后的状态；已回滚的目录不再保留标记，其余未完成安装仍显示标注。
            if (_service.IsCurrentDirectory(ctx)) _service.NotifyInstanceStateChanged(expectedId);
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    public async Task RunSnapshotDemoAsync()
    {
        for (var i = 0; i < 100 && Items.Count == 0; i++) await Task.Delay(100);
        Configure(Items.FirstOrDefault(i => i.Id == "1.21.1") ?? Items.FirstOrDefault());
        SelectedLoader = LoaderChoices[1];
        for (var i = 0; i < 100 && LoaderVersions.Count == 0; i++) await Task.Delay(100);
    }
}
