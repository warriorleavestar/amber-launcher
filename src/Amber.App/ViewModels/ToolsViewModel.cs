using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Java;
using Amber.Core.Launch;
using Amber.Core.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class JunkItem(JunkGroup group) : ObservableObject
{
    public JunkGroup Group { get; } = group;
    public string Name => Group.Name;
    public string Description => Group.Description;
    public string Size => Group.Bytes >= 1024 * 1024 ? $"{Group.Bytes / 1024.0 / 1024:F1} MB" : $"{Group.Bytes / 1024.0:F0} KB";

    [ObservableProperty]
    public partial bool Selected { get; set; } = group.Bytes > 0;
}

/// <summary>工具箱：清理垃圾、下载任意文件、下载 Java、检查更新。</summary>
public sealed partial class ToolsViewModel(LauncherService service) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "工具箱";
    public override string Icon => "✦";

    public ObservableCollection<JunkItem> Junk { get; } = [];
    public int[] JavaMajors { get; } = [8, 17, 21, 25];

    [ObservableProperty] public partial string JunkStatus { get; set; } = "点“扫描”看看能清理多少";
    [ObservableProperty] public partial string DownloadUrl { get; set; } = "";
    [ObservableProperty] public partial string DownloadFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] public partial string DownloadSha1 { get; set; } = "";
    [ObservableProperty] public partial int SelectedJavaMajor { get; set; } = 21;
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial string? UpdateUrl { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    [RelayCommand]
    private async Task ScanJunkAsync()
    {
        JunkStatus = "正在扫描……";
        var dir = service.Directory;
        var groups = await Task.Run(() => Toolbox.ScanJunk(dir));
        Junk.Clear();
        foreach (var g in groups) Junk.Add(new JunkItem(g));
        JunkStatus = $"共 {groups.Sum(g => g.Bytes) / 1024.0 / 1024:F1} MB 可以清理。存档、模组和配置不在清理范围内。";
    }

    [RelayCommand]
    private async Task CleanJunkAsync()
    {
        var selected = Junk.Where(j => j.Selected).Select(j => j.Group).ToList();
        if (selected.Count == 0) return;
        var freed = await Task.Run(() => Toolbox.Clean(selected));
        JunkStatus = $"清理了 {freed / 1024.0 / 1024:F1} MB";
        await ScanJunkAsync();
        JunkStatus = $"清理了 {freed / 1024.0 / 1024:F1} MB，游戏运行中的文件会被跳过";
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception e)
        {
            Status = "失败：" + e.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private Task DownloadFileAsync() => RunAsync(async ct =>
    {
        if (!Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("请填写以 http 或 https 开头的下载地址");
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
        var target = Path.Combine(DownloadFolder.Trim(), name);
        Status = $"正在下载 {name}……";
        var item = new DownloadItem(uri.ToString(), target, string.IsNullOrWhiteSpace(DownloadSha1) ? null : DownloadSha1.Trim());
        await service.Downloader.DownloadOneAsync(item, new Progress<long>(bytes =>
        {
            Status = $"正在下载 {name}：{bytes / 1024.0 / 1024:F1} MB";
        }), ct);
        await Ui.FlushAsync();
        Status = "已下载到 " + target;
    });

    [RelayCommand]
    private Task InstallJavaAsync() => RunAsync(async ct =>
    {
        var major = SelectedJavaMajor;
        Status = $"正在下载 Java {major}……";
        var path = await new JavaRuntimeInstaller(service.Downloader, service.Directory.Root).InstallAsync(null, major, new Progress<DownloadProgress>(p =>
        {
            Progress = 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles);
            Status = $"下载 Java {major}：{p.CompletedFiles}/{p.TotalFiles}";
        }), ct);
        await service.Java.ScanAsync(true);
        await Ui.FlushAsync();
        Status = "已安装：" + path;
    });

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateUrl = null;
        var feed = service.Settings.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed))
        {
            UpdateStatus = $"当前版本 {UpdateChecker.CurrentVersion}。没有配置更新地址，所以不会联网检查（可以在设置页填写）。";
            return;
        }
        try
        {
            var update = await new UpdateChecker(service.Http).CheckAsync(feed, UpdateChecker.CurrentVersion);
            if (update is null)
            {
                UpdateStatus = $"当前版本 {UpdateChecker.CurrentVersion} 已是最新";
            }
            else
            {
                UpdateStatus = $"有新版本 {update.Version}（当前 {UpdateChecker.CurrentVersion}）。Amber 不会自动下载或替换，需要的话自己去更新。";
                UpdateUrl = update.Url;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            UpdateStatus = "检查失败：" + e.Message;
        }
    }

    [RelayCommand]
    private void OpenUpdateUrl()
    {
        if (UpdateUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
