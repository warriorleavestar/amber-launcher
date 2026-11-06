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
    private readonly Func<int, IContentProvider> _providerForSource;
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

    /// <summary>来源下拉的选项：没配 CurseForge 的 API 密钥（也没填兼容接口）时 CurseForge 置灰（改版规格 4.5）。</summary>
    public SourceOption[] SourceOptions { get; } = [new("Modrinth", true), new("CurseForge", true)];

    /// <summary>能用 CurseForge：有 API 密钥或自定义接口。设置改了之后回到这一页时重新判断。</summary>
    public bool HasCurseForge => _hasCurseForge?.Invoke() ?? true;
    private readonly Func<bool>? _hasCurseForge;

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

    /// <summary>列表里选中的行。翻页时列表清空，这个会变成 null，但右侧详情（Detail）留着。</summary>
    [ObservableProperty]
    public partial ProjectItem? Selected { get; set; }

    /// <summary>右侧详情面板显示的项目：翻页时不收起（#22），换关键词、类型、来源时才收起。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProjectItem? Detail { get; set; }

    public bool HasSelection => Detail is not null;

    /// <summary>选中文件的可选依赖，玩家勾选的一起装（#22）。</summary>
    public ObservableCollection<OptionalDependencyItem> OptionalDependencies { get; } = [];

    public bool HasOptionalDependencies => OptionalDependencies.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRequiresManualDownload))]
    public partial FileItem? SelectedFile { get; set; }

    public bool SelectedRequiresManualDownload => SelectedFile?.File.RequiresManualDownload == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualDownload))]
    public partial string? ManualDownloadUrl { get; set; }

    public bool HasManualDownload => !string.IsNullOrWhiteSpace(ManualDownloadUrl);

    private enum ManualDownloadContext { Resource, ModpackArchive, ModpackImport }
    private ManualDownloadContext _manualDownloadContext;

    /// <summary>整合包导入失败会回滚新目标，不能让玩家往已不存在的实例里补文件。</summary>
    public string ManualDownloadHint => L.T(_manualDownloadContext switch
    {
        ManualDownloadContext.ModpackArchive => "content.curseforge.manual_pack_archive_hint",
        ManualDownloadContext.ModpackImport => "content.curseforge.manual_pack_import_hint",
        _ => "content.curseforge.manual_hint",
    });

    private void SetManualDownloadContext(ManualDownloadContext context)
    {
        if (_manualDownloadContext == context) return;
        _manualDownloadContext = context;
        OnPropertyChanged(nameof(ManualDownloadHint));
    }

    partial void OnManualDownloadUrlChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) SetManualDownloadContext(ManualDownloadContext.Resource);
    }

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
    private IContentProvider Provider => _providerForSource(SourceIndex);

    public ContentViewModel(LauncherService service)
        : this(service, source => source == 0 ? service.Modrinth : service.CurseForge, () => service.CurseForgeIfConfigured is not null) { }

    internal ContentViewModel(LauncherService service, Func<int, IContentProvider> providerForSource, Func<bool>? hasCurseForge = null)
    {
        _service = service;
        _providerForSource = providerForSource;
        _hasCurseForge = hasCurseForge;
        SourceOptions[1].Enabled = HasCurseForge;
        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _slowTimer.Tick += (_, _) => UpdateSlow();
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_service.CurrentInstance is { } id && Instances.Contains(id)) TargetInstance = id;
            OnPropertyChanged(nameof(IsTemporaryTarget));
        });
        RefreshInstances();
    }

    protected override void OnBecameCurrent()
    {
        // 可能刚在设置页填了 CurseForge 的密钥。
        var had = SourceOptions[1].Enabled;
        SourceOptions[1].Enabled = HasCurseForge;
        OnPropertyChanged(nameof(HasCurseForge));
        if (!had && SourceOptions[1].Enabled && SourceIndex == 1) _ = SearchFromStart();
    }

    protected override void OnFirstActivated()
    {
        // 第一次进入资源页就显示列表；已经通过来源切换或快捷入口搜索过时沿用结果。
        if (_searchCts is null) _ = SearchFromStart();
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
    partial void OnSelectedChanged(ProjectItem? value)
    {
        // 翻页清空列表时 Selected 变 null：详情面板不跟着收起。
        if (value is not null) Detail = value;
    }

    partial void OnDetailChanged(ProjectItem? value) => _ = LoadFilesAsync();
    partial void OnShowUnstableChanged(bool value) => _ = LoadFilesAsync();
    /// <summary>空结果里的【显示全部版本】和复选框本身：换了过滤条件就重新搜第一页。</summary>
    partial void OnFilterByInstanceChanged(bool value)
    {
        OnPropertyChanged(nameof(CanShowAllVersions));
        _ = SearchFromStart();
    }
    partial void OnSelectedFileChanged(FileItem? value)
    {
        ManualDownloadUrl = value?.File.ManualDownloadPage;
        SetManualDownloadContext(value is not null && IsModpack ? ManualDownloadContext.ModpackArchive : ManualDownloadContext.Resource);
        LoadOptionalDependencies(value);
        InstallCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(InstallHint));
    }
    partial void OnIsBusyChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();
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
        if (Detail is not null) _ = LoadFilesAsync();
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
    public void BrowseModsFor(string instanceId) => BrowseFor(instanceId, ContentType.Mod);

    /// <summary>
    /// 从别的页面跳来给某个实例找某类内容（实例页的【在线浏览…】、安装成功卡）。
    /// back 不为 null 时页面顶部显示上下文条和【← 返回实例】（改版规格 4.5）。
    /// </summary>
    public void BrowseFor(string instanceId, ContentType type, Action? back = null)
    {
        if (Instances.Contains(instanceId)) TargetInstance = instanceId;
        // 之前搜别的内容留下的关键词对这次没意义，清掉后显示热门。
        Query = "";
        if (TypeIndex != (int)type) TypeIndex = (int)type;
        else _ = SearchFromStart();
        SetReturn(instanceId, type, back);
    }

    /// <summary>从实例页的“下载数据包”跳过来：切到数据包，并选好版本和世界。</summary>
    public void BrowseDatapacksFor(string instanceId, string worldFolder, Action? back = null)
    {
        BrowseFor(instanceId, ContentType.Datapack, back);
        RefreshWorlds();
        TargetWorld = Worlds.FirstOrDefault(w => w.World.FolderName == worldFolder) ?? TargetWorld;
    }

    // ---- 从实例页跳来时的上下文条 ----

    private Action? _back;

    /// <summary>上下文条的文字，比如“正在给 1.21.1-Fabric 找光影”；没有上下文时为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReturn))]
    public partial string? ReturnContext { get; set; }

    public bool HasReturn => ReturnContext is not null;

    private void SetReturn(string instanceId, ContentType type, Action? back)
    {
        _back = back;
        ReturnContext = back is null ? null : L.F("content.context.for_instance", instanceId, TypeNames[(int)type].Label);
    }

    /// <summary>【← 返回实例】：回到实例页（实例和 Tab 都不变），上下文条收起。</summary>
    [RelayCommand]
    private void Return()
    {
        var back = _back;
        _back = null;
        ReturnContext = null;
        back?.Invoke();
    }

    private Task SearchFromStart()
    {
        Page = 0;
        ManualDownloadUrl = null;
        Detail = null;
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

    // ---- 结果列表的三种状态（改版规格 4.5）：加载中（10 秒后提示慢）、空结果、出错 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] public partial bool IsSearching { get; set; }
    /// <summary>已经等了 10 秒以上。</summary>
    [ObservableProperty] public partial bool IsSlow { get; set; }
    [ObservableProperty] public partial string SlowText { get; set; } = "";
    [ObservableProperty] public partial string SearchingText { get; set; } = "";
    /// <summary>这次搜索没有结果时的说明（null 表示不是空结果）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmptyResult), nameof(IsIdle))] public partial string? EmptyText { get; set; }
    public bool IsEmptyResult => EmptyText is not null;
    /// <summary>搜索出错的原因（null 表示没出错）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSearchError), nameof(IsIdle))] public partial string? SearchError { get; set; }
    public bool IsSearchError => SearchError is not null;
    /// <summary>选了 CurseForge 但没配密钥：不发请求，列表里写明去哪填。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] public partial bool NeedsCurseForgeKey { get; set; }
    /// <summary>不在搜、没出错、没结果：显示“搜索结果会显示在这里”。</summary>
    public bool IsIdle => !IsSearching && !IsEmptyResult && !IsSearchError && !NeedsCurseForgeKey && Results.Count == 0;

    private readonly DispatcherTimer _slowTimer;
    private DateTime _searchStarted;
    internal static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(10);

    private string SourceName => SourceNames[Math.Clamp(SourceIndex, 0, SourceNames.Length - 1)];

    /// <summary>另一个来源能不能用（慢速提示里的【切换来源】）。</summary>
    public bool CanSwitchSource => SourceIndex == 1 || HasCurseForge;

    /// <summary>当前是按实例过滤的搜索（空结果时显示【显示全部版本】）。</summary>
    public bool CanShowAllVersions => FilterByInstance && !IsModpack;

    private void UpdateSlow()
    {
        var waited = DateTime.UtcNow - _searchStarted;
        if (!IsSearching || waited < SlowAfter) return;
        IsSlow = true;
        SlowText = SlowMessage(SourceIndex, (int)waited.TotalSeconds);
    }

    /// <summary>超过 10 秒的提示：Modrinth 特别说明国内可能慢。</summary>
    internal static string SlowMessage(int source, int seconds) => source == 0
        ? L.F("content.search.slow_modrinth", seconds)
        : L.F("content.search.slow", "CurseForge", seconds);

    private void SetSearchState(bool searching, string? empty = null, string? error = null, bool needsKey = false)
    {
        EmptyText = empty;
        SearchError = error;
        NeedsCurseForgeKey = needsKey;
        IsSlow = false;
        if (searching)
        {
            _searchStarted = DateTime.UtcNow;
            SearchingText = L.F("content.search.searching_from", SourceName);
            _slowTimer.Start();
        }
        else
        {
            _slowTimer.Stop();
        }
        IsSearching = searching;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanSwitchSource));
        OnPropertyChanged(nameof(CanShowAllVersions));
    }

    /// <summary>慢速提示里的【切换来源】：Modrinth ↔ CurseForge。</summary>
    [RelayCommand]
    private void SwitchSource()
    {
        if (CanSwitchSource) SourceIndex = SourceIndex == 0 ? 1 : 0;
    }

    /// <summary>空结果里的一键操作：取消“只看适用于这个实例”。</summary>
    [RelayCommand]
    private void ShowAllVersions() => FilterByInstance = false;

    /// <summary>主窗口设置：没配 CurseForge 密钥时的【去填写】打开设置页。</summary>
    public Action? NavigateToSettings { get; set; }

    [RelayCommand]
    private void GoToCurseForgeKey() => NavigateToSettings?.Invoke();

    /// <summary>没配密钥时的【改用 Modrinth】。</summary>
    [RelayCommand]
    private void UseModrinth() => SourceIndex = 0;

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Results.Clear();
        Selected = null;
        if (SourceIndex == 1 && !HasCurseForge)
        {
            // 没有密钥的请求一定失败：不联网，直接说明去哪填。
            SetSearchState(false, needsKey: true);
            SetStatus(StatusMessage.Warning("core.content.curseforge.missingapikey"));
            return;
        }
        SetStatus(StatusMessage.Info("content.search.searching"));
        SetSearchState(true);
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
            SetSearchState(false, empty: result.Total == 0 ? EmptyMessage(Query.Trim(), instance is not null) : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!cts.IsCancellationRequested)
            {
                SetStatus(StatusMessage.Failure("content.search.failed", e, ErrorText.Of(e)));
                SetSearchState(false, error: Status);
            }
        }
    }

    /// <summary>空结果的说明：有关键词时建议换英文名；按实例过滤时建议取消过滤（界面上另有一键按钮）。</summary>
    internal static string EmptyMessage(string query, bool filtered) => (query.Length > 0, filtered) switch
    {
        (true, true) => L.F("content.search.empty_query_filtered", query),
        (true, false) => L.F("content.search.empty_query", query),
        (false, true) => L.T("content.search.empty_filtered"),
        _ => L.T("content.search.no_results"),
    };

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
        if (Detail is not { } selected) return;

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
            LoaderNote = null;
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
            // 提示也放在详情面板的安装按钮上方：状态条会被后面的消息覆盖，这条不能被跳过（改版规格 4.5）。
            LoaderNote = loaderNote;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("content.files.fetch_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>详情面板安装按钮上方的提示：需要 Connector、只有 Forge 版这类（null 表示没有）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoaderNote))]
    public partial string? LoaderNote { get; set; }

    public bool HasLoaderNote => LoaderNote is not null;

    /// <summary>安装按钮的说明：没选文件时写明原因（按钮禁用时也显示）。</summary>
    public string InstallHint => SelectedFile is null ? L.T("content.install.pick_file") : L.T("content.install.selected");

    private bool CanInstall() => SelectedFile is not null && !IsBusy;

    private static string SafeName(string title)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
        return title.Trim();
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (Detail?.Project.PageUrl is not { } url) return;
        try
        {
            if (!TryOpenProjectPage(url, start => Process.Start(start)))
                SetStatus(StatusMessage.Error("tools.download.invalid_url"));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(StatusMessage.Failure("content.project.open_failed", e, ErrorText.Of(e)));
        }
    }

    [RelayCommand]
    private void OpenManualDownload()
    {
        try
        {
            if (!TryOpenProjectPage(ManualDownloadUrl, start => Process.Start(start)))
                SetStatus(StatusMessage.Error("tools.download.invalid_url"));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(StatusMessage.Failure("content.project.open_failed", e, ErrorText.Of(e)));
        }
    }

    private void ShowManualDownload(ContentFile file, ManualDownloadContext context = ManualDownloadContext.Resource)
    {
        ManualDownloadUrl = file.ManualDownloadPage;
        SetManualDownloadContext(context);
        var key = context == ManualDownloadContext.ModpackImport
            ? "content.curseforge.pack_import_incomplete" : "core.content.curseforge.manual_download";
        SetStatus(StatusMessage.Warning(key, file.DisplayName, file.ManualDownloadPage ?? ""));
    }

    // 最后一次校验紧贴系统调用，避免新提供方或手工构造的项目绕过入口校验。
    internal static bool TryOpenProjectPage(string? url, Action<ProcessStartInfo> start)
    {
        if (!WebLinks.TryGetHttpUrl(url, out var uri)) return false;
        start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedFile?.File is not { } file) return;
        if (file.RequiresManualDownload)
        {
            ShowManualDownload(file, IsModpack ? ManualDownloadContext.ModpackArchive : ManualDownloadContext.Resource);
            return;
        }
        ManualDownloadUrl = null;
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
        if (BlocksVanillaModInstall(CurrentType, instance))
        {
            // 原版实例不会加载模组：装前拦下，说明要先装加载器（改版规格 1b.4）。
            SetStatus(StatusMessage.Warning("content.install.vanilla_blocked", instance.Id));
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
                    var optional = OptionalDependencies.Where(d => d.Include && d.ProjectId is not null).Select(d => d.ProjectId!).ToHashSet(StringComparer.Ordinal);
                    var optionalFiles = OptionalDependencies.Where(d => d.Include && d.ProjectId is null && d.FileId is not null).Select(d => d.FileId!).ToHashSet(StringComparer.Ordinal);
                    plan = await _service.ContentInstaller.ResolveAsync(file, instance, null, _workCts.Token, optionalProjects: optional, optionalFiles: optionalFiles);
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
        catch (ManualDownloadRequiredException e)
        {
            ShowManualDownload(e.File);
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

    private CancellationTokenSource? _optionalCts;

    /// <summary>列出文件的可选依赖（默认不勾）；名字在后台查，查不到就显示项目 id。经 Connector 装时不列（Connector 规划只装必需的）。</summary>
    private void LoadOptionalDependencies(FileItem? file)
    {
        _optionalCts?.Cancel();
        OptionalDependencies.Clear();
        if (file is not null && !_viaConnector && CurrentType == ContentType.Mod)
        {
            var cts = _optionalCts = new CancellationTokenSource();
            foreach (var dep in OptionalDependencyIds(file.File))
            {
                var item = new OptionalDependencyItem(dep.ProjectId, dep.Source, dep.FileId);
                OptionalDependencies.Add(item);
                _ = LoadOptionalNameAsync(item, cts.Token);
            }
        }
        OnPropertyChanged(nameof(HasOptionalDependencies));
    }

    /// <summary>文件里写明项目或确切文件的可选依赖（去重）。</summary>
    internal static IEnumerable<ContentDependency> OptionalDependencyIds(ContentFile file) =>
        file.Dependencies.Where(d => d.Kind == DependencyKind.Optional && (!string.IsNullOrEmpty(d.ProjectId) || !string.IsNullOrEmpty(d.FileId)))
            .DistinctBy(d => (d.Source, d.ProjectId, FileId: d.ProjectId is null ? d.FileId : null));

    private async Task LoadOptionalNameAsync(OptionalDependencyItem item, CancellationToken ct)
    {
        try
        {
            var provider = item.Source == ContentSource.Modrinth ? _providerForSource(0) : _providerForSource(1);
            var projectId = item.ProjectId;
            if (projectId is null && item.FileId is { } fileId)
            {
                var file = await provider.GetFileAsync("", fileId, ct);
                ct.ThrowIfCancellationRequested();
                projectId = file.ProjectId;
                item.Name = file.DisplayName;
            }
            if (projectId is null) return;
            var project = await provider.GetProjectAsync(projectId, ct);
            if (!ct.IsCancellationRequested) item.Name = project.Title;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or NotSupportedException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            // 名字查不到就显示 id。
        }
    }

    /// <summary>往没有加载器的原版实例里装模组：装了也不会生效，安装前拦下。</summary>
    internal static bool BlocksVanillaModInstall(ContentType type, GameInstance instance) => type == ContentType.Mod && instance.Loader is null;

    private async Task InstallModpackFromFileAsync(ContentFile file)
    {
        IsBusy = true;
        _workCts = new CancellationTokenSource();
        var importStarted = false;
        var temp = Path.Combine(Path.GetTempPath(), "amber-pack-" + Guid.NewGuid().ToString("N"), ContentInstaller.SafeFileName(file.FileName));
        try
        {
            SetStatus(StatusMessage.Info("content.modpack.downloading"));
            await _service.Downloader.DownloadOneAsync(ContentInstaller.ToItem(file, Path.GetDirectoryName(temp)!), _workCts.Token);
            importStarted = true;
            await ImportCoreAsync(temp, NewInstanceName, _workCts.Token);
        }
        catch (ManualDownloadRequiredException e)
        {
            await Ui.FlushAsync();
            ShowManualDownload(e.File, importStarted ? ManualDownloadContext.ModpackImport : ManualDownloadContext.ModpackArchive);
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
        ManualDownloadUrl = null;
        // 快捷导入会先进入资源页，取消首次搜索，避免它迟到后覆盖导入状态。
        _searchCts?.Cancel();
        // 被取消的搜索不会自己收起“正在搜索”：这里收起，不然转圈和慢速提示会一直留着。
        if (IsSearching) SetSearchState(false);
        IsBusy = true;
        _workCts = new CancellationTokenSource();
        try
        {
            var info = ModpackService.Inspect(path);
            await ImportCoreAsync(path, SafeName(info.Name), _workCts.Token);
        }
        catch (ManualDownloadRequiredException e)
        {
            await Ui.FlushAsync();
            ShowManualDownload(e.File, ManualDownloadContext.ModpackImport);
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

/// <summary>来源下拉的一项：Enabled 为 false 时置灰（CurseForge 没配密钥）。</summary>
public sealed partial class SourceOption(string label, bool enabled) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = enabled;

    public override string ToString() => Label;
}

/// <summary>详情面板里的一个可选依赖：勾上就和主文件一起装。</summary>
public sealed partial class OptionalDependencyItem(string? projectId, ContentSource source, string? fileId = null) : ObservableObject
{
    public string? ProjectId { get; } = projectId;
    public string? FileId { get; } = fileId;
    public ContentSource Source { get; } = source;

    [ObservableProperty]
    public partial string Name { get; set; } = projectId ?? fileId ?? "";

    [ObservableProperty]
    public partial bool Include { get; set; }
}
