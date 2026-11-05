using Amber.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Localization;
using Amber.Core.Net;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class ProjectItem(ContentProject project) : LocalizedObservable
{
    public ContentProject Project { get; } = project;
    public string Title => Project.Title;
    public string Summary => Project.Summary;
    public string Meta => L.P("content.project.downloads", Project.Downloads, (Project.Author is null ? "" : Project.Author + " · "), FormatCount(Project.Downloads));

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    /// <summary>下载数：中文和日文按“万 / 亿”，英文按 K / M / B。按当前界面语言的区域写数字。</summary>
    internal static string FormatCount(long n) => L.Language == "en"
        ? Compact(n, 1_000, [("K", 1_000), ("M", 1_000_000), ("B", 1_000_000_000)], (unit, v) => v.ToString("0.#", L.Culture) + unit)
        : Compact(n, 10_000, [("content.count.ten_thousand", 10_000), ("content.count.hundred_million", 100_000_000)], (key, v) => L.F(key, v));

    /// <summary>
    /// 选单位：先按能用的最大单位四舍五入到一位小数，结果到了下一级（比如 999,960 → “1000.0K”）就换成下一级的单位。
    /// </summary>
    private static string Compact(long n, long first, (string Unit, long Size)[] units, Func<string, double, string> format)
    {
        if (n < first) return n.ToString("N0", L.Culture);
        var index = units.Length - 1;
        while (index > 0 && n < units[index].Size) index--;
        var value = Math.Round(n / (double)units[index].Size, 1);
        if (index + 1 < units.Length && value * units[index].Size >= units[index + 1].Size)
        {
            index++;
            value = Math.Round(n / (double)units[index].Size, 1);
        }
        return format(units[index].Unit, value);
    }
}

public sealed record FileItem(ContentFile File) : LocalizedRecord
{
    public string Name => File.DisplayName;
    public string Meta => $"{ChannelText} · {File.Date.LocalDateTime:yyyy-MM-dd} · {string.Join(", ", File.Loaders)} · {string.Join(", ", File.GameVersions.Take(3))}{(File.GameVersions.Count > 3 ? "…" : "")}";
    private string ChannelText => File.Channel switch { "release" => L.T("download.filter.release"), "beta" => L.T("content.channel.beta"), "alpha" => L.T("content.channel.alpha"), var c => c };
}

public sealed record WorldChoice(WorldInfo World) : LocalizedRecord
{
    public string Label => World.Name == World.FolderName ? World.Name : L.F("content.world.label", World.Name, World.FolderName);
}

/// <summary>资源页：在 Modrinth / CurseForge 搜索并安装模组、资源包、光影、整合包、数据包。</summary>
public sealed partial class ContentViewModel : PageViewModel, ISnapshotDemo
{
    private const int PageSize = 20;

    private readonly LauncherService _service;
    private readonly SemaphoreSlim _iconGate = new(6, 6);
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _filesCts;
    private CancellationTokenSource? _workCts;
    private int _total;

    /// <summary>当前选中的项目是经 Sinytra Connector 装的 Fabric 模组。</summary>
    private bool _viaConnector;

    public override string Title => L.T("content.title");
    public override string Icon => "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z";

    public OptionItem[] TypeNames { get; } = OptionItem.Keys("instance.mods.title", "instance.resourcepacks.title", "instance.shaderpacks.title", "content.type.modpack", "instance.datapacks.title");
    public string[] SourceNames { get; } = ["Modrinth", "CurseForge"];

    public ObservableCollection<string> Instances { get; } = [];
    public ObservableCollection<ProjectItem> Results { get; } = [];
    public ObservableCollection<FileItem> Files { get; } = [];
    public ObservableCollection<WorldChoice> Worlds { get; } = [];

    /// <summary>数据包装到哪个世界。</summary>
    [ObservableProperty]
    public partial WorldChoice? TargetWorld { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModpack), nameof(IsDatapack))]
    public partial int TypeIndex { get; set; }

    [ObservableProperty]
    public partial int SourceIndex { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string? TargetInstance { get; set; }

    [ObservableProperty]
    public partial bool FilterByInstance { get; set; } = true;

    [ObservableProperty]
    public partial int Page { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProjectItem? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial FileItem? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string NewInstanceName { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowUnstable { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    protected override StatusMessage? DefaultStatus => StatusMessage.Info("content.search.hint");

    public bool IsModpack => CurrentType == ContentType.Modpack;
    public bool IsDatapack => CurrentType == ContentType.Datapack;

    private ContentType CurrentType => (ContentType)TypeIndex;
    private IContentProvider Provider => SourceIndex == 0 ? _service.Modrinth : _service.CurseForge;

    public ContentViewModel(LauncherService service)
    {
        _service = service;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_service.CurrentInstance is { } id && Instances.Contains(id)) TargetInstance = id;
            OnPropertyChanged(nameof(IsTemporaryTarget));
        });
        RefreshInstances();
    }

    private void RefreshInstances()
    {
        // 资源页“装到”跟着当前实例；手动换成别的只算这一次（不改当前实例），切换当前实例时回到它。
        var keep = IsTemporaryTarget ? TargetInstance : null;
        Instances.Clear();
        foreach (var v in _service.InstalledVersions()) Instances.Add(v);
        TargetInstance = keep is not null && Instances.Contains(keep) ? keep : _service.CurrentOr(Instances, Instances.FirstOrDefault());
    }

    partial void OnTypeIndexChanged(int value)
    {
        if (value < 0) return;
        OnPropertyChanged(nameof(IsSharedTarget));
        if (IsDatapack) RefreshWorlds();
        _ = SearchFromStart();
    }
    partial void OnSourceIndexChanged(int value)
    {
        if (value >= 0) _ = SearchFromStart();
    }
    partial void OnSelectedChanged(ProjectItem? value) => _ = LoadFilesAsync();
    partial void OnShowUnstableChanged(bool value) => _ = LoadFilesAsync();
    /// <summary>“装到”临时换成了不是当前实例的那个（显示“仅本次”）。</summary>
    public bool IsTemporaryTarget => TargetInstance is { } t && _service.CurrentInstance is { } c && t != c && Instances.Contains(c);

    /// <summary>
    /// 版本隔离关闭时模组、资源包、光影装进共享目录（所有实例共用），“装到”的实例只用来筛选兼容的游戏版本和加载器。
    /// </summary>
    public bool IsSharedTarget => TargetInstance is { } t && CurrentType is ContentType.Mod or ContentType.ResourcePack or ContentType.Shader
        && _service.UsesSharedDir(t);

    partial void OnTargetInstanceChanged(string? value)
    {
        OnPropertyChanged(nameof(IsTemporaryTarget));
        OnPropertyChanged(nameof(IsSharedTarget));
        if (IsDatapack) RefreshWorlds();
        if (Selected is not null) _ = LoadFilesAsync();
    }

    private void RefreshWorlds()
    {
        var keep = TargetWorld?.World.FolderName;
        Worlds.Clear();
        try
        {
            if (CurrentInstance() is { } instance)
                foreach (var w in WorldManager.List(instance.SavesDir)) Worlds.Add(new WorldChoice(w));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            // 版本文件坏了就没有世界可选。
        }
        TargetWorld = Worlds.FirstOrDefault(w => w.World.FolderName == keep) ?? Worlds.FirstOrDefault();
    }

    /// <summary>从别的页面跳来给某个实例找模组（比如安装成功卡上的“添加模组”）。</summary>
    public void BrowseModsFor(string instanceId)
    {
        if (Instances.Contains(instanceId)) TargetInstance = instanceId;
        Query = "";
        if (TypeIndex != (int)ContentType.Mod) TypeIndex = (int)ContentType.Mod;
        else _ = SearchFromStart();
    }

    /// <summary>从实例页的“下载数据包”跳过来：切到数据包，并选好版本和世界。</summary>
    public void BrowseDatapacksFor(string instanceId, string worldFolder)
    {
        if (Instances.Contains(instanceId)) TargetInstance = instanceId;
        // 之前搜模组留下的关键词对数据包没意义，清掉后显示热门数据包。
        Query = "";
        if (TypeIndex != (int)ContentType.Datapack) TypeIndex = (int)ContentType.Datapack;
        else _ = SearchFromStart();
        RefreshWorlds();
        TargetWorld = Worlds.FirstOrDefault(w => w.World.FolderName == worldFolder) ?? TargetWorld;
    }

    private Task SearchFromStart()
    {
        Page = 0;
        return SearchAsync();
    }

    [RelayCommand]
    private Task Search() => SearchFromStart();

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if ((Page + 1) * PageSize >= _total) return;
        Page++;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (Page == 0) return;
        Page--;
        await SearchAsync();
    }

    private GameInstance? CurrentInstance() =>
        TargetInstance is { } id ? _service.GetInstance(id) : null;

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        SetStatus(StatusMessage.Info("content.search.searching"));
        Results.Clear();
        Selected = null;
        try
        {
            var instance = FilterByInstance && !IsModpack ? CurrentInstance() : null;
            var result = await Provider.SearchAsync(new SearchQuery(CurrentType, Query.Trim(), instance?.McVersion, instance?.Loader, Page * PageSize, PageSize), cts.Token);
            if (cts.IsCancellationRequested) return;
            _total = result.Total;
            foreach (var p in result.Items)
            {
                var item = new ProjectItem(p);
                Results.Add(item);
                _ = LoadIconAsync(item);
            }
            Status = result.Total == 0 ? L.T("content.search.no_results") : L.P("content.search.results", result.Total, result.Total, Page + 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("content.search.failed", e, ErrorText.Of(e)));
        }
    }

    private async Task LoadIconAsync(ProjectItem item)
    {
        if (item.Project.IconUrl is not { } url) return;
        await _iconGate.WaitAsync();
        try
        {
            var bytes = await _service.Http.GetByteArrayAsync(url);
            item.Icon = await Task.Run(() =>
            {
                using var memory = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(memory, 64);
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            // 图标加载失败只是少张图。
        }
        finally
        {
            _iconGate.Release();
        }
    }

    private async Task LoadFilesAsync()
    {
        _filesCts?.Cancel();
        Files.Clear();
        SelectedFile = null;
        if (Selected is not { } selected) return;

        var cts = _filesCts = new CancellationTokenSource();
        try
        {
            var instance = IsModpack || !FilterByInstance ? null : CurrentInstance();
            // 资源包和光影的加载器字段含义不同，只按游戏版本过滤。数据包在 Modrinth 上的加载器写作 datapack，
            // 同一个项目常常还有模组版，要按它挑出数据包文件。
            var loader = CurrentType switch
            {
                ContentType.Mod => instance?.Loader,
                ContentType.Datapack => "datapack",
                _ => null,
            };
            var files = await Provider.GetFilesAsync(selected.Project.Id, instance?.McVersion, loader, cts.Token);
            if (cts.IsCancellationRequested) return;
            _viaConnector = false;
            string? loaderNote = null;
            if (files.Count == 0 && CurrentType == ContentType.Mod && instance is { Loader: "forge" or "neoforge" } && SourceIndex == 0)
            {
                // 只有 Fabric 版：可以经 Sinytra Connector 用。
                var fabric = await Provider.GetFilesAsync(selected.Project.Id, instance.McVersion, "fabric", cts.Token);
                if (cts.IsCancellationRequested) return;
                if (fabric.Count > 0)
                {
                    files = fabric;
                    _viaConnector = true;
                    loaderNote = L.F("content.loader.connector_hint", instance.Loader);
                }
            }
            else if (files.Count == 0 && CurrentType == ContentType.Mod && instance is { Loader: "fabric" or "quilt" })
            {
                var forge = await Provider.GetFilesAsync(selected.Project.Id, instance.McVersion, null, cts.Token);
                if (forge.Count > 0 && forge.All(f => f.Loaders.All(l => l is "forge" or "neoforge")))
                    loaderNote = L.T("content.loader.forge_only");
            }
            foreach (var f in files.Where(f => f.IsRelease || ShowUnstable)) Files.Add(new FileItem(f));
            SelectedFile = Files.FirstOrDefault();
            if (IsModpack) NewInstanceName = SafeName(selected.Title);
            if (Files.Count == 0 && files.Count > 0) SetStatus(StatusMessage.Warning("content.files.only_unstable"));
            else if (files.Count == 0) Status = loaderNote ?? (instance is null ? L.T("content.files.empty") : L.F("content.files.no_matching", instance.McVersion, instance.Loader));
            else if (loaderNote is not null) Status = loaderNote;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("content.files.fetch_failed", e, ErrorText.Of(e)));
        }
    }

    private static string SafeName(string title)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
        return title.Trim();
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (Selected?.Project.PageUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (SelectedFile?.File is not { } file) return;
        if (IsModpack)
        {
            await InstallModpackFromFileAsync(file);
            return;
        }
        if (CurrentInstance() is not { } instance)
        {
            SetStatus(StatusMessage.Warning("content.install.instance_required"));
            return;
        }
        var world = TargetWorld;
        if (IsDatapack && world is null)
        {
            SetStatus(StatusMessage.Warning("content.install.world_required"));
            return;
        }

        IsBusy = true;
        _workCts = new CancellationTokenSource();
        try
        {
            var target = IsDatapack ? DatapackManager.DirectoryOf(world!.World.Path) : instance.DirectoryFor(CurrentType);
            var plan = new InstallPlan([file], []);
            if (CurrentType == ContentType.Mod)
            {
                SetStatus(StatusMessage.Info("content.install.resolving"));
                if (_viaConnector)
                {
                    var hasConnector = ConnectorInstaller.IsInstalled(new ModManager(instance.ModsDir).List().Where(m => m.Enabled).Select(m => m.Requirements));
                    plan = await ConnectorInstaller.PlanFabricModAsync(file, instance, _service.Modrinth, _service.ContentInstaller, null, hasConnector, _workCts.Token);
                }
                else
                {
                    plan = await _service.ContentInstaller.ResolveAsync(file, instance, null, _workCts.Token);
                }
                // 目标文件夹里已经有同名文件的依赖不用再下，下载器会校验并跳过。
            }
            await _service.ContentInstaller.InstallAsync(plan, target, new Progress<DownloadProgress>(p =>
            {
                Progress = 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles);
                SetStatus(StatusMessage.Info("content.install.progress", p.CompletedFiles, p.TotalFiles, p.CurrentFile));
            }), _workCts.Token);

            await Ui.FlushAsync();
            var extra = plan.Files.Count > 1 ? L.P("content.install.dependencies", plan.Files.Count - 1, plan.Files.Count - 1) : "";
            var problems = plan.Problems.Count > 0 ? L.F("content.install.problems", string.Join(L.T("common.list_separator"), plan.Problems)) : "";
            var noLoader = CurrentType == ContentType.Mod && instance.Loader is null ? L.T("content.install.no_loader") : "";
            var where = IsDatapack ? L.F("content.install.world_target", instance.Id, world!.Label) : instance.Id;
            // 带“注意：”的（依赖有问题、实例没装加载器）用警告色，不当成单纯的成功。
            var kind = problems.Length > 0 || noLoader.Length > 0 ? StatusKind.Warning : StatusKind.Success;
            SetStatus(new StatusMessage("content.install.completed", [file.DisplayName, extra, where, problems, noLoader], kind));
            _service.NotifyContentChanged();
        }
        catch (OperationCanceledException) when (_workCts?.IsCancellationRequested == true)
        {
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("common.install_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private async Task InstallModpackFromFileAsync(ContentFile file)
    {
        IsBusy = true;
        _workCts = new CancellationTokenSource();
        var temp = Path.Combine(Path.GetTempPath(), "amber-pack-" + Guid.NewGuid().ToString("N"), ContentInstaller.SafeFileName(file.FileName));
        try
        {
            SetStatus(StatusMessage.Info("content.modpack.downloading"));
            await _service.Downloader.DownloadOneAsync(ContentInstaller.ToItem(file, Path.GetDirectoryName(temp)!), _workCts.Token);
            await ImportCoreAsync(temp, NewInstanceName, _workCts.Token);
        }
        catch (OperationCanceledException) when (_workCts?.IsCancellationRequested == true)
        {
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("common.install_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            try { Directory.Delete(Path.GetDirectoryName(temp)!, true); } catch { /* 临时文件 */ }
        }
    }

    /// <summary>导入本地整合包文件（由界面的文件选择器调用）。</summary>
    public async Task ImportLocalPackAsync(string path)
    {
        IsBusy = true;
        _workCts = new CancellationTokenSource();
        try
        {
            var info = ModpackService.Inspect(path);
            await ImportCoreAsync(path, SafeName(info.Name), _workCts.Token);
        }
        catch (OperationCanceledException) when (_workCts?.IsCancellationRequested == true)
        {
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("instance.worlds.import_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private async Task ImportCoreAsync(string path, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(L.T("content.modpack.name_required"));
        var skipped = new List<string>();
        var id = await _service.Modpacks.ImportAsync(path, name, _service.Directory.VersionDir, new Progress<LoaderProgress>(p =>
        {
            Status = p.Stage;
            if (p.Percent is { } percent) Progress = percent;
        }), ct, onSkip: file => { lock (skipped) skipped.Add(file); });
        await Ui.FlushAsync();
        _service.ForceIsolated(id);
        // 跳过的文件在进度里一闪而过，完成时用可随语言切换的状态再汇总一次。
        lock (skipped)
            SetStatus(skipped.Count == 0
                ? StatusMessage.Success("content.modpack.installed", id)
                : StatusMessage.Warning("content.modpack.installed_skipped", id, skipped.Count, string.Join(L.T("core.mp.format.listseparator"), skipped.Take(5)) + (skipped.Count > 5 ? "…" : "")));
        _service.NotifyInstalledChanged();
        // 新装的整合包直接成为当前实例（改版规格 2.3）。
        _service.SetCurrentInstance(id);
    }

    [RelayCommand]
    private void Cancel() => _workCts?.Cancel();


    public async Task RunSnapshotDemoAsync()
    {
        Query = "sodium";
        await SearchFromStart();
        Selected = Results.FirstOrDefault();
        for (var i = 0; i < 60 && Files.Count == 0; i++) await Task.Delay(100);
        await Task.Delay(1500);
    }
}
