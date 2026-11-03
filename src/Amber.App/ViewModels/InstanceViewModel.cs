using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class ModItem(LocalMod mod) : ObservableObject
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
    public string UpdateText => Update is null ? "" : "可更新到 " + Update.Latest.FileName;

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (value == Mod.Enabled) return;
        try
        {
            var path = ModManager.SetEnabled(Mod, value);
            Mod = Mod with { Path = path, FileName = Path.GetFileName(path), Enabled = value };
            OnPropertyChanged(nameof(Meta));
        }
        catch (IOException)
        {
            // 游戏运行时文件被占用，改不了就恢复勾选状态。
            Dispatcher.UIThread.Post(() => Enabled = Mod.Enabled);
        }
    }

    partial void OnUpdateChanged(ModUpdate? value) => OnPropertyChanged(nameof(UpdateText));
}

public sealed record WorldItem(WorldInfo World)
{
    public string Name => World.Name;
    public string Meta => $"{World.GameMode} · {World.Version ?? "未知版本"} · {World.LastPlayed?.LocalDateTime:yyyy-MM-dd HH:mm} · {World.FolderName}";
}

public sealed partial class DatapackItem(LocalDatapack pack, string worldPath, Action<string> report) : ObservableObject
{
    public LocalDatapack Pack { get; } = pack;
    public string Name => Pack.FileName;
    public string Meta => (Pack.IsFolder ? "文件夹" : "压缩包") + (string.IsNullOrWhiteSpace(Pack.Description) ? "" : " · " + Pack.Description.ReplaceLineEndings(" "));

    [ObservableProperty]
    public partial bool Enabled { get; set; } = pack.Enabled;

    partial void OnEnabledChanged(bool value)
    {
        try
        {
            DatapackManager.SetEnabled(worldPath, Pack.FileName, value);
            report($"已{(value ? "启用" : "禁用")} {Pack.FileName}，下次进这个世界时生效");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            report("修改失败：" + e.Message);
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

public sealed partial class ServerItem(ServerEntry entry) : ObservableObject
{
    public ServerEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Address => Entry.Address;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "正在查询……";
}

public sealed partial class ExportOption(string name, bool include) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool Include { get; set; } = include;
}

public sealed record JavaChoice(string Label, string? Path);

/// <summary>实例页：一个版本自己的设置、模组、世界、截图、资源包、光影、服务器和导出。</summary>
public sealed partial class InstanceViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private readonly HashSet<int> _loadedTabs = [];
    private CancellationTokenSource? _workCts;

    public override string Title => "实例";
    public override string Icon => "M4 4h16v5H4zM4 11h16v4H4zM4 17h16v3H4z";

    public ObservableCollection<string> Instances { get; } = [];

    [ObservableProperty]
    public partial string? SelectedId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstance), nameof(SupportsQuickPlay))]
    public partial GameInstance? Current { get; set; }

    public bool HasInstance => Current is not null;

    [ObservableProperty]
    public partial string InfoText { get; set; } = "";

    [ObservableProperty]
    public partial int TabIndex { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool DeleteArmed { get; set; }

    public InstanceViewModel(LauncherService service)
    {
        _service = service;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
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
        var keep = SelectedId;
        Instances.Clear();
        foreach (var v in _service.InstalledVersions()) Instances.Add(v);
        SelectedId = keep is not null && Instances.Contains(keep) ? keep
            : _service.Settings.LastVersion is { } last && Instances.Contains(last) ? last
            : Instances.FirstOrDefault();
    }

    partial void OnSelectedIdChanged(string? value)
    {
        DeleteArmed = false;
        _loadedTabs.Clear();
        Current = null;
        InfoText = "";
        if (value is null) return;
        try
        {
            Current = _service.GetInstance(value);
            InfoText = $"游戏 {Current.McVersion} · {(Current.Loader is null ? "原版" : $"{Current.Loader} {Current.LoaderVersion}")} · {Current.WorkDir}";
            LoadTab(TabIndex);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            InfoText = "版本文件读取失败：" + e.Message;
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

    /// <summary>删除走回收站，误删还能找回来。</summary>
    private static void Recycle(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (System.IO.Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (System.IO.Directory.Exists(path))
        {
            System.IO.Directory.Delete(path, true);
        }
        else
        {
            File.Delete(path);
        }
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
                    Status = $"校验并补全 {p.Files.CompletedFiles}/{p.Files.TotalFiles}";
                }
            }), ct);
            await Ui.FlushAsync();
            Status = "文件完整";
        });
    }

    /// <summary>删除版本要点两次。</summary>
    [RelayCommand]
    private void DeleteVersion()
    {
        if (Current is not { } c) return;
        if (!DeleteArmed)
        {
            DeleteArmed = true;
            Status = $"再点一次“确认删除”，把 {c.Id} 的版本文件夹（含隔离的存档和模组）放进回收站";
            return;
        }
        DeleteArmed = false;
        try
        {
            Recycle(_service.Directory.VersionDir(c.Id));
            Status = $"已把 {c.Id} 放进回收站";
            _service.NotifyInstalledChanged();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status = "删除失败：" + e.Message;
        }
    }

    public async Task ExportLaunchScriptAsync(string path)
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            var settings = _service.EffectiveSettings(c.Id);
            var version = _service.Directory.ResolveVersion(c.Id);
            var java = !string.IsNullOrEmpty(settings.JavaPath) && File.Exists(settings.JavaPath) ? settings.JavaPath : await _service.Java.ForVersionAsync(version, ct);
            var account = _service.Accounts.Store.Selected;
            var session = account is { Kind: AccountKind.Offline } ? OfflineAccount.Create(account.Name) : OfflineAccount.Create("Player");
            var args = new GameLauncher(_service.Directory).BuildArguments(new LaunchOptions
            {
                VersionId = c.Id,
                JavaPath = java,
                Session = session,
                MaxMemoryMb = settings.MaxMemoryMb ?? 4096,
                WorkingDirectory = c.WorkDir,
                ExtraJvmArgs = ArgumentSplitter.Split(settings.JvmArgs),
                ExtraGameArgs = ArgumentSplitter.Split(settings.GameArgs),
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
            });
            Toolbox.WriteLaunchScript(path, java, c.WorkDir, args, session.AccessToken);
            Status = "启动脚本已导出（离线身份）：" + path;
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
            Status = "已取消";
        }
        catch (OperationCanceledException)
        {
            Status = "失败：网络请求超时，请稍后重试";
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
    private void CancelWork() => _workCts?.Cancel();

    // ---- 设置 ----

    public string[] TriState { get; } = ["沿用全局设置", "开启", "关闭"];
    public string[] MemoryModes { get; } = ["沿用全局设置", "自动", "手动"];
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
            JavaChoices.Add(new JavaChoice("沿用全局设置", null));
            SelectedJava = JavaChoices[0];
        }
        var found = await _service.Java.ScanAsync();
        JavaChoices.Clear();
        JavaChoices.Add(new JavaChoice("沿用全局设置", null));
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
        Status = $"{id} 的设置已保存";
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---- 模组 ----

    public ObservableCollection<ModItem> Mods { get; } = [];

    [ObservableProperty] public partial string ModSummary { get; set; } = "";

    private void LoadMods() => _ = LoadModsAsync();

    private async Task LoadModsAsync()
    {
        if (Current is not { } c) return;
        Mods.Clear();
        var list = await Task.Run(() => new ModManager(c.ModsDir).List());
        foreach (var mod in list)
        {
            var item = new ModItem(mod);
            Mods.Add(item);
            if (mod.Icon is { } icon) _ = DecodeIconAsync(item, icon);
        }
        ModSummary = $"{list.Count} 个模组，启用 {list.Count(m => m.Enabled)} 个";
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
        if (item is null) return;
        try
        {
            Recycle(item.Mod.Path);
            Mods.Remove(item);
            ModSummary = $"{Mods.Count} 个模组，启用 {Mods.Count(m => m.Enabled)} 个";
            Status = $"已把 {item.Name} 放进回收站";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status = "删除失败：" + e.Message;
        }
    }

    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (Current is not { } c) return;
        await RunWorkAsync(async ct =>
        {
            Status = "正在检查更新……";
            var mods = Mods.Select(m => m.Mod).ToList();
            var updates = await ModManager.CheckUpdatesAsync(mods, _service.Modrinth, _service.CurseForgeIfConfigured, c.McVersion, c.Loader, ct);
            foreach (var item in Mods) item.Update = updates.FirstOrDefault(u => u.LocalPath == item.Mod.Path);
            Status = updates.Count == 0 ? "全部是最新版" : $"{updates.Count} 个模组可更新";
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
                Status = $"更新 {item.Name}……";
                await ModManager.ApplyUpdateAsync(item.Update!, _service.Downloader, ct);
                Progress = 100.0 * ++done / pending.Count;
            }
            Status = pending.Count == 0 ? "先点“检查更新”" : $"已更新 {done} 个模组";
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
            Status = $"{item.Name} 已更新";
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
        Status = $"已添加 {count} 个文件";
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
        foreach (var w in WorldManager.List(Current!.SavesDir)) Worlds.Add(new WorldItem(w));
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
            Status = $"正在备份 {item.Name}……";
            var target = await Task.Run(() => WorldManager.Backup(item.World, Path.Combine(c.WorkDir, "backups")));
            Status = "备份完成：" + target;
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
        if (item is null) return;
        try
        {
            Recycle(item.World.Path);
            if (DatapackWorld == item) DatapackWorld = null;
            Worlds.Remove(item);
            Status = $"已把 {item.Name} 放进回收站";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status = "删除失败：" + e.Message;
        }
    }

    public void ImportWorld(string zipPath)
    {
        if (Current is not { } c) return;
        try
        {
            System.IO.Directory.CreateDirectory(c.SavesDir);
            var folder = WorldManager.Import(zipPath, c.SavesDir);
            Status = $"已导入存档 {folder}";
            LoadWorlds();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Status = "导入失败：" + e.Message;
        }
    }

    // ---- 数据包（属于某个世界）----

    public ObservableCollection<DatapackItem> Datapacks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatapackWorld), nameof(DatapackTitle))]
    public partial WorldItem? DatapackWorld { get; set; }

    public bool HasDatapackWorld => DatapackWorld is not null;
    public string DatapackTitle => DatapackWorld is { } w ? $"{w.Name} 的数据包" : "";
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
        if (item is null) return;
        try
        {
            Recycle(item.Pack.Path);
            Datapacks.Remove(item);
            Status = $"已把 {item.Name} 放进回收站";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status = "删除失败：" + e.Message;
        }
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
        Status = $"已添加 {added} 个数据包" + (errors.Count > 0 ? "。" + string.Join("；", errors) : "");
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
        if (item is null) return;
        try
        {
            Recycle(item.Path);
            Screenshots.Remove(item);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status = "删除失败：" + e.Message;
        }
    }

    // ---- 资源包、光影 ----

    public ObservableCollection<PackFileItem> ResourcePacks { get; } = [];
    public ObservableCollection<PackFileItem> ShaderPacks { get; } = [];

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
        if (item is null) return;
        try
        {
            Recycle(item.Path);
            ResourcePacks.Remove(item);
            ShaderPacks.Remove(item);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status = "删除失败：" + e.Message;
        }
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
            ? $"{status.Players}/{status.MaxPlayers} 人在线 · {status.VersionName}{(status.PingMs is { } ms ? $" · {ms} ms" : "")}"
            : "离线：" + status.Error;
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
        ServerList.Remove(c.WorkDir, item.Address);
        Servers.Remove(item);
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

    public string[] ExportFormats { get; } = ["Modrinth（.mrpack）", "CurseForge（.zip）"];
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
            Status = "正在打包……";
            if (ExportFormatIndex == 0)
                await _service.Modpacks.ExportMrpackAsync(c, path, ExportName, ExportVersion, include, ct);
            else
                await _service.Modpacks.ExportCurseForgeAsync(c, path, ExportName, ExportVersion, Environment.UserName, include, ct);
            Status = $"已导出 {path}（{new FileInfo(path).Length / 1024} KB）";
        });
    }

    public async Task RunSnapshotDemoAsync()
    {
        TabIndex = 1;
        await Task.Delay(1500);
    }
}
