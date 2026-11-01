using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Install;
using Amber.Core.Net;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class ProjectItem(ContentProject project) : ObservableObject
{
    public ContentProject Project { get; } = project;
    public string Title => Project.Title;
    public string Summary => Project.Summary;
    public string Meta => $"{(Project.Author is null ? "" : Project.Author + " · ")}{FormatCount(Project.Downloads)} 次下载";

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    private static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:F1} 亿",
        >= 10_000 => $"{n / 10_000.0:F1} 万",
        _ => n.ToString(),
    };
}

public sealed record FileItem(ContentFile File)
{
    public string Name => File.DisplayName;
    public string Meta => $"{ChannelText} · {File.Date.LocalDateTime:yyyy-MM-dd} · {string.Join(", ", File.Loaders)} · {string.Join(", ", File.GameVersions.Take(3))}{(File.GameVersions.Count > 3 ? "…" : "")}";
    private string ChannelText => File.Channel switch { "release" => "正式版", "beta" => "测试版", "alpha" => "早期版", var c => c };
}

/// <summary>资源页：在 Modrinth / CurseForge 搜索并安装模组、资源包、光影、整合包。</summary>
public sealed partial class ContentViewModel : PageViewModel, ISnapshotDemo
{
    private const int PageSize = 20;

    private readonly LauncherService _service;
    private readonly SemaphoreSlim _iconGate = new(6, 6);
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _filesCts;
    private CancellationTokenSource? _workCts;
    private int _total;

    public override string Title => "资源";
    public override string Icon => "❖";

    public string[] TypeNames { get; } = ["模组", "资源包", "光影", "整合包"];
    public string[] SourceNames { get; } = ["Modrinth", "CurseForge"];

    public ObservableCollection<string> Instances { get; } = [];
    public ObservableCollection<ProjectItem> Results { get; } = [];
    public ObservableCollection<FileItem> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModpack))]
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

    [ObservableProperty]
    public partial string Status { get; set; } = "输入关键词搜索，或直接点搜索看热门内容";

    public bool IsModpack => CurrentType == ContentType.Modpack;

    private ContentType CurrentType => (ContentType)TypeIndex;
    private IContentProvider Provider => SourceIndex == 0 ? _service.Modrinth : _service.CurseForge;

    public ContentViewModel(LauncherService service)
    {
        _service = service;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
        RefreshInstances();
    }

    private void RefreshInstances()
    {
        var keep = TargetInstance;
        Instances.Clear();
        foreach (var v in _service.InstalledVersions()) Instances.Add(v);
        TargetInstance = keep is not null && Instances.Contains(keep) ? keep : _service.Settings.LastVersion is { } last && Instances.Contains(last) ? last : Instances.FirstOrDefault();
    }

    partial void OnTypeIndexChanged(int value) => _ = SearchFromStart();
    partial void OnSourceIndexChanged(int value) => _ = SearchFromStart();
    partial void OnSelectedChanged(ProjectItem? value) => _ = LoadFilesAsync();
    partial void OnShowUnstableChanged(bool value) => _ = LoadFilesAsync();
    partial void OnTargetInstanceChanged(string? value) { if (Selected is not null) _ = LoadFilesAsync(); }

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
        Status = "正在搜索……";
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
            Status = result.Total == 0 ? "没有找到结果" : $"共 {result.Total} 个结果，第 {Page + 1} 页";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Status = "搜索失败：" + e.Message;
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
            // 资源包和光影的加载器字段含义不同，只按游戏版本过滤。
            var loader = CurrentType == ContentType.Mod ? instance?.Loader : null;
            var files = await Provider.GetFilesAsync(selected.Project.Id, instance?.McVersion, loader, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var f in files.Where(f => f.IsRelease || ShowUnstable)) Files.Add(new FileItem(f));
            SelectedFile = Files.FirstOrDefault();
            if (IsModpack) NewInstanceName = SafeName(selected.Title);
            if (Files.Count == 0 && files.Count > 0) Status = "这个项目只有测试版，勾选“显示测试版”后可选";
            else if (files.Count == 0) Status = instance is null ? "这个项目没有可下载的版本" : $"没有适用于 {instance.McVersion} {instance.Loader} 的版本";
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Status = "获取版本失败：" + e.Message;
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
            Status = "请先选择要安装到哪个版本";
            return;
        }

        IsBusy = true;
        _workCts = new CancellationTokenSource();
        try
        {
            var target = instance.DirectoryFor(CurrentType);
            var plan = new InstallPlan([file], []);
            if (CurrentType == ContentType.Mod)
            {
                Status = "正在解析依赖……";
                plan = await _service.ContentInstaller.ResolveAsync(file, instance, null, _workCts.Token);
                // 目标文件夹里已经有同名文件的依赖不用再下，下载器会校验并跳过。
            }
            await _service.ContentInstaller.InstallAsync(plan, target, new Progress<DownloadProgress>(p =>
            {
                Progress = 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles);
                Status = $"下载 {p.CompletedFiles}/{p.TotalFiles}：{p.CurrentFile}";
            }), _workCts.Token);

            await Ui.FlushAsync();
            var extra = plan.Files.Count > 1 ? $"（含 {plan.Files.Count - 1} 个依赖）" : "";
            var problems = plan.Problems.Count > 0 ? "。注意：" + string.Join("；", plan.Problems) : "";
            var noLoader = CurrentType == ContentType.Mod && instance.Loader is null ? "。注意：这个版本没装加载器，模组不会生效" : "";
            Status = $"已把 {file.DisplayName}{extra} 安装到 {instance.Id}{problems}{noLoader}";
            _service.NotifyContentChanged();
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception e)
        {
            Status = "安装失败：" + e.Message;
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
            Status = "正在下载整合包……";
            await _service.Downloader.DownloadOneAsync(ContentInstaller.ToItem(file, Path.GetDirectoryName(temp)!), _workCts.Token);
            await ImportCoreAsync(temp, NewInstanceName, _workCts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception e)
        {
            Status = "安装失败：" + e.Message;
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
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception e)
        {
            Status = "导入失败：" + e.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private async Task ImportCoreAsync(string path, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请填写版本名字");
        var id = await _service.Modpacks.ImportAsync(path, name, _service.Directory.VersionDir, new Progress<LoaderProgress>(p =>
        {
            Status = p.Stage;
            if (p.Percent is { } percent) Progress = percent;
        }), ct);
        await Ui.FlushAsync();
        _service.ForceIsolated(id);
        Status = $"整合包已安装为 {id}";
        _service.NotifyInstalledChanged();
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
