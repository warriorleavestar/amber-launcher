using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Install;
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

public sealed record LoaderChoice(string Label, LoaderKind? Kind);

public sealed partial class DownloadViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private List<ManifestEntry> _all = [];
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _installCts;

    public override string Title => "下载";
    public override string Icon => "⬇";

    public string[] Filters { get; } = ["正式版", "快照", "远古", "全部"];
    public ObservableCollection<RemoteVersionItem> Items { get; } = [];

    public LoaderChoice[] LoaderChoices { get; } =
    [
        new("原版", null),
        new("Fabric", LoaderKind.Fabric),
        new("Quilt", LoaderKind.Quilt),
        new("Forge", LoaderKind.Forge),
        new("NeoForge", LoaderKind.NeoForge),
        new("OptiFine", LoaderKind.OptiFine),
    ];

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "正式版";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>正在配置安装的版本；非空时显示右侧的安装面板。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfiguring))]
    public partial RemoteVersionItem? Target { get; set; }

    public bool IsConfiguring => Target is not null;

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

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedLoaderChanged(LoaderChoice value) => _ = LoadLoaderVersionsAsync();
    partial void OnShowUnstableLoadersChanged(bool value) => _ = LoadLoaderVersionsAsync();
    partial void OnSelectedLoaderVersionChanged(LoaderVersion? value) => UpdateDefaultName();

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
            "远古" => _all.Where(v => v.Type is "old_beta" or "old_alpha"),
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
        LoaderHint = "正在获取版本列表……";
        try
        {
            var versions = await LoaderInstallers.Create(kind, _service.Loaders).ListAsync(target.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var v in versions.Where(v => v.Stable || ShowUnstableLoaders)) LoaderVersions.Add(v);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Note == "推荐") ?? LoaderVersions.FirstOrDefault();
            LoaderHint = versions.Count == 0 ? $"{SelectedLoader.Label} 不支持 {target.Id}"
                : LoaderVersions.Count == 0 ? "只有测试版，勾选“显示测试版”后可选"
                : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            LoaderHint = "获取失败：" + e.Message;
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
            Status = "请先选择加载器版本";
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
                Status = $"正在安装 {chosen.DefaultId}……";
                id = await LoaderInstallers.Create(kind, _service.Loaders).InstallAsync(chosen, CustomName, new Progress<LoaderProgress>(p =>
                {
                    Status = p.Stage;
                    if (p.Percent is { } percent) Progress = percent;
                }), ct);
            }
            else
            {
                id = item.Id;
                Status = $"正在安装 {id}……";
                await _service.Installer.InstallAsync(id, new Progress<InstallProgress>(p =>
                {
                    if (p.Stage != InstallStage.Files || p.Files.TotalFiles == 0) return;
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    var mb = p.Files.BytesDownloaded / 1024.0 / 1024;
                    Status = $"安装 {id}：{p.Files.CompletedFiles}/{p.Files.TotalFiles} 个文件，{mb:F1} MB，{mb / Math.Max(watch.Elapsed.TotalSeconds, 0.1):F1} MB/s";
                }), ct);
                item.Installed = true;
            }
            await Ui.FlushAsync();
            Status = $"{id} 安装完成，用时 {watch.Elapsed.TotalSeconds:F0} 秒";
            Target = null;
            _service.NotifyInstalledChanged();
        }
        catch (OperationCanceledException)
        {
            Status = "已取消安装";
        }
        catch (Exception e)
        {
            Status = $"安装失败：{e.Message}";
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
