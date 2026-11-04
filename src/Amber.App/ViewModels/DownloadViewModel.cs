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
public sealed partial class RemoteVersionItem(ManifestEntry entry, bool installed) : ObservableObject
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

public sealed record LoaderChoice(string Name, LoaderKind? Kind)
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

    [ObservableProperty]
    public partial string CustomName { get; set; } = "";

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
        SelectedLoader = LoaderChoices[0];
    }

    protected override void OnFirstActivated() => _ = RefreshAsync();

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedLoaderChanged(LoaderChoice value) => _ = LoadLoaderVersionsAsync();
    partial void OnShowUnstableLoadersChanged(bool value) => _ = LoadLoaderVersionsAsync();
    partial void OnSelectedLoaderVersionChanged(LoaderVersion? value) => UpdateDefaultName();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Status = L.T("download.status.fetching_versions");
        try
        {
            var manifest = await _service.Installer.GetManifestAsync();
            _all = manifest.Versions;
            ApplyFilter();
            Status = L.F("download.status.latest", manifest.Latest.GetValueOrDefault("release"), manifest.Latest.GetValueOrDefault("snapshot"));
        }
        catch (Exception e)
        {
            Status = L.F("download.status.fetch_failed", e.Message);
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
        LoaderHint = "";
        UpdateDefaultName();
        if (Target is not { } target || SelectedLoader.Kind is not { } kind) return;

        var cts = _listCts = new CancellationTokenSource();
        LoaderHint = L.T("download.status.fetching_versions");
        try
        {
            var versions = await LoaderInstallers.Create(kind, _service.Loaders).ListAsync(target.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var v in versions.Where(v => v.Stable || ShowUnstableLoaders)) LoaderVersions.Add(v);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Recommended) ?? LoaderVersions.FirstOrDefault();
            LoaderHint = versions.Count == 0 ? L.F("download.loader.unsupported", SelectedLoader.Label, target.Id)
                : LoaderVersions.Count == 0 ? L.T("download.loader.only_unstable")
                : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            LoaderHint = L.F("download.loader.fetch_failed", e.Message);
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
            Status = L.T("download.status.loader_required");
            return;
        }

        IsBusy = true;
        _installCts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var ct = _installCts.Token;
        try
        {
            string id;
            if (loader is { } kind)
            {
                var chosen = SelectedLoaderVersion!;
                Status = L.F("download.status.installing", chosen.DefaultId);
                id = await LoaderInstallers.Create(kind, _service.Loaders).InstallAsync(chosen, CustomName, new Progress<LoaderProgress>(p =>
                {
                    Status = p.Stage;
                    if (p.Percent is { } percent) Progress = percent;
                }), ct);
            }
            else
            {
                id = item.Id;
                Status = L.F("download.status.installing", id);
                await _service.Installer.InstallAsync(id, new Progress<InstallProgress>(p =>
                {
                    if (p.Stage != InstallStage.Files || p.Files.TotalFiles == 0) return;
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    var mb = p.Files.BytesDownloaded / 1024.0 / 1024;
                    Status = L.F("download.status.progress", id, p.Files.CompletedFiles, p.Files.TotalFiles, mb, mb / Math.Max(watch.Elapsed.TotalSeconds, 0.1));
                }), ct);
                item.Installed = true;
            }
            await Ui.FlushAsync();
            Status = L.F("download.status.installed", id, watch.Elapsed.TotalSeconds);
            Target = null;
            _service.NotifyInstalledChanged();
        }
        catch (OperationCanceledException)
        {
            Status = L.T("download.status.cancelled");
        }
        catch (Exception e)
        {
            Status = L.F("common.install_failed", e.Message);
        }
        finally
        {
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
