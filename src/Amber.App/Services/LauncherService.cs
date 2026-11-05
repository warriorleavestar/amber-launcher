using Amber.Core.Multiplayer;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Launch;
using Amber.Core.Net;

namespace Amber.App.Services;

/// <summary>界面共用的核心对象。设置里的目录或下载源变了就调用 <see cref="Reload"/>。</summary>
public sealed class LauncherService : IDisposable
{
    private readonly HttpClient _http = Downloader.CreateHttpClient();

    public HttpClient Http => _http;
    public AppSettings Settings { get; }
    public AccountService Accounts { get; }
    public GameDirectory Directory { get; private set; } = null!;
    public Downloader Downloader { get; private set; } = null!;
    public VanillaInstaller Installer { get; private set; } = null!;
    public JavaManager Java { get; private set; } = null!;
    public LoaderContext Loaders { get; private set; } = null!;
    public ModrinthProvider Modrinth { get; private set; } = null!;
    public CurseForgeProvider CurseForge { get; private set; } = null!;
    public ContentInstaller ContentInstaller { get; private set; } = null!;
    public ModpackService Modpacks { get; private set; } = null!;

    /// <summary>已安装版本列表变化（安装完成、切换目录）时触发。</summary>
    public event Action? InstalledVersionsChanged;

    /// <summary>账号增删或切换时触发。</summary>
    public event Action? AccountsChanged;

    /// <summary>资源页往实例里装了东西时触发。</summary>
    public event Action? ContentChanged;

    /// <summary>别的页面请求用某种方式启动（进服务器、进存档）。</summary>
    public event Action<LaunchRequest>? LaunchRequested;

    /// <summary>运行中游戏的输出（版本 id，原始行），在游戏输出线程上触发。联机页用它认出“对局域网开放”的端口。</summary>
    public event Action<string, string>? GameOutput;

    /// <summary>联机组件放在数据目录下，和游戏目录无关。</summary>
    public EasyTierInstaller EasyTier => new(Downloader, AppSettings.DataDir);

    public IReadOnlyList<string> MultiplayerPeers =>
        Settings.MultiplayerPeers is { Count: > 0 } peers ? peers : EasyTierRelease.DefaultPeers;

    public LauncherService(AppSettings settings)
    {
        Settings = settings;
        var store = new AccountStore(Path.Combine(AppSettings.DataDir, "accounts.json"));
        store.Load();
        Accounts = new AccountService(_http, store, AppSettings.DataDir, () => Settings.MicrosoftClientId);
        // 老版本只存了玩家名，第一次运行时转成离线账号。
        if (store.Accounts.Count == 0 && !string.IsNullOrWhiteSpace(settings.PlayerName))
            Accounts.AddOffline(settings.PlayerName);
        Reload();
    }

    public void Reload()
    {
        Directory = new GameDirectory(Settings.GameDirectory);
        var sources = Settings.DownloadSource switch
        {
            "official" => SourceChain.OfficialFirst,
            "mirror" => SourceChain.MirrorFirst,
            _ => SourceChain.Default(),
        };
        Downloader = new Downloader(_http, sources)
        {
            MaxConcurrency = Math.Clamp(Settings.DownloadConcurrency, 1, 128),
            Hashes = new HashCache(Path.Combine(Settings.GameDirectory, ".amber-hashes")),
        };
        Installer = new VanillaInstaller(Downloader, Directory);
        Java = new JavaManager(Downloader, Directory) { AutoDownload = Settings.AutoJavaDownload };
        Loaders = new LoaderContext(Downloader, Directory, Installer, Java.ForMajorAsync);
        Modrinth = new ModrinthProvider(_http);
        CurseForge = new CurseForgeProvider(_http, CurseForgeKey,
            string.IsNullOrWhiteSpace(Settings.CurseForgeApiRoot) ? CurseForgeProvider.DefaultApiRoot : Settings.CurseForgeApiRoot.Trim().TrimEnd('/'));
        ContentInstaller = new ContentInstaller(Downloader, new Dictionary<ContentSource, IContentProvider>
        {
            [ContentSource.Modrinth] = Modrinth,
            [ContentSource.CurseForge] = CurseForge,
        });
        Modpacks = new ModpackService(Loaders, Modrinth, CurseForge);
        NotifyInstalledChanged();
    }

    /// <summary>设置在设置页以外被改了（新手卡换了游戏目录或下载源）：设置页跟着刷新显示。</summary>
    public event Action? SettingsChangedElsewhere;

    /// <summary>保存设置并按新设置重建下载器、安装器（新手卡用）。</summary>
    public void SaveAndReload()
    {
        Settings.Save();
        Reload();
        SettingsChangedElsewhere?.Invoke();
    }

    private string? CurseForgeKey() =>
        string.IsNullOrWhiteSpace(Settings.CurseForgeApiKey) ? Environment.GetEnvironmentVariable("AMBER_CF_KEY") : Settings.CurseForgeApiKey;

    /// <summary>有 Key 或自定义接口地址时才用 CurseForge 查更新。</summary>
    public CurseForgeProvider? CurseForgeIfConfigured =>
        !string.IsNullOrWhiteSpace(CurseForgeKey()) || !string.IsNullOrWhiteSpace(Settings.CurseForgeApiRoot) ? CurseForge : null;

    public void NotifyAccountsChanged() => AccountsChanged?.Invoke();
    public void NotifyContentChanged() => ContentChanged?.Invoke();
    public void NotifyInstalledChanged() => InstalledVersionsChanged?.Invoke();
    public void RequestLaunch(LaunchRequest request) => LaunchRequested?.Invoke(request);
    public void NotifyGameOutput(string versionId, string line) => GameOutput?.Invoke(versionId, line);

    public List<string> InstalledVersions() => Directory.ListInstalled().OrderDescending().ToList();

    /// <summary>
    /// 全局唯一的“当前实例”（改版规格 2.3）：启动、实例、联机页都绑定它，资源页默认装到它。
    /// 切换时马上写进 Settings.LastVersion，重启后保持。
    /// </summary>
    public string? CurrentInstance => Settings.LastVersion;

    /// <summary>CurrentInstance 变了（在调用 SetCurrentInstance 的线程上触发）。</summary>
    public event Action? CurrentInstanceChanged;

    public void SetCurrentInstance(string? id)
    {
        if (id is null || id == Settings.LastVersion) return;
        Settings.LastVersion = id;
        // 写不了设置文件（被占用、没权限）：内存里照样切过去，只是下次启动不记得；提醒一声，不拦着。
        try { Settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Amber: saving current instance failed: " + e);
            SettingsSaveFailed?.Invoke(e);
        }
        CurrentInstanceChanged?.Invoke();
    }

    /// <summary>设置文件写不进去（参数：异常）。界面弹个不挡操作的提示。</summary>
    public event Action<Exception>? SettingsSaveFailed;

    /// <summary>实例的状态（安装未完成、已损坏）可能变了：安装开始、完成、取消、失败、修复之后调用。参数：实例 ID。</summary>
    public event Action<string>? InstanceStateChanged;

    public void NotifyInstanceStateChanged(string id) => InstanceStateChanged?.Invoke(id);

    /// <summary>放“安装未完成”标记并通知界面。</summary>
    public void MarkInstallStarted(string id)
    {
        try { Directory.MarkInstallStarted(id); }
        finally { NotifyInstanceStateChanged(id); }
    }

    /// <summary>去掉“安装未完成”标记并通知界面。</summary>
    public void MarkInstallFinished(string id)
    {
        try { Directory.MarkInstallFinished(id); }
        finally { NotifyInstanceStateChanged(id); }
    }

    /// <summary>当前实例还装着就返回它，否则返回 fallback（比如列表第一项）。</summary>
    public string? CurrentOr(IReadOnlyCollection<string> installed, string? fallback) =>
        CurrentInstance is { } id && installed.Contains(id) ? id : fallback;

    /// <summary>实例状态不正常时给版本说明后面加的一句提示（正常时为空串）。</summary>
    public string StateNote(string id) => Directory.StateOf(id) switch
    {
        InstanceState.Incomplete => Amber.Core.Localization.L.T("instance.state.incomplete"),
        InstanceState.Broken => Amber.Core.Localization.L.T("instance.state.broken"),
        _ => "",
    };

    /// <summary>把状态提示接在说明文字后面（另起一行）。</summary>
    public string WithStateNote(string id, string text) => StateNote(id) is { Length: > 0 } note ? text + Environment.NewLine + note : text;

    /// <summary>
    /// 启动时清理回收目录里超过保留天数的条目：游戏目录下的，以及游戏目录在别的卷上时各实例根目录下的。返回清掉的条数。
    /// </summary>
    public int PurgeTrash(DateTimeOffset? now = null)
    {
        var keep = Settings.TrashRetentionDays;
        var removed = TrashService.Purge(Path.Combine(Directory.Root, TrashService.TrashFolder), keep, now);
        foreach (var id in Directory.ListInstalled())
            removed += TrashService.Purge(Path.Combine(Directory.VersionDir(id), TrashService.TrashFolder), keep, now);
        return removed;
    }

    /// <summary>版本设置叠加全局设置后的结果。版本没设隔离时，先看 PCL / HMCL 给这个版本记的隔离设置，再用全局值。</summary>
    public InstanceSettings EffectiveSettings(string id)
    {
        var own = InstanceSettings.Load(Directory, id);
        own.Isolate ??= OtherLauncherSettings.Isolation(Directory, id);
        return own.Over(Settings.ToGlobalInstanceSettings());
    }

    public string WorkDirFor(string id) => UsesSharedDir(id) ? Directory.Root : Directory.VersionDir(id);

    /// <summary>没开“按实例分开文件夹”：模组、资源包、存档都在游戏目录下，所有实例共用。</summary>
    public bool UsesSharedDir(string id) => EffectiveSettings(id).Isolate != true;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _runningDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>记下这个游戏目录里有游戏在运行（启动页的正式启动、实例页的预处理），Dispose 时去掉。</summary>
    public IDisposable MarkRunning(string workDir)
    {
        var key = Path.GetFullPath(workDir);
        _runningDirs.AddOrUpdate(key, 1, (_, n) => n + 1);
        return new RunningMark(() => _runningDirs.AddOrUpdate(key, 0, (_, n) => n - 1));
    }

    public bool IsRunningIn(string workDir) => _runningDirs.TryGetValue(Path.GetFullPath(workDir), out var n) && n > 0;

    private sealed class RunningMark(Action release) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }

    /// <summary>按这个版本的有效设置（版本设置盖在全局设置上）生成启动参数。</summary>
    public LaunchOptions BaseLaunchOptions(string id, string javaPath, GameSession session, IReadOnlyList<string> sessionJvmArgs) =>
        EffectiveSettings(id).ToLaunchOptions(Directory, id, javaPath, session, sessionJvmArgs) with { NoTrace = Settings.NoTrace };

    public GameInstance GetInstance(string id) => GameInstance.Detect(Directory, id, WorkDirFor(id));

    /// <summary>整合包的文件都在版本目录里，这个版本必须固定开启隔离。</summary>
    public void ForceIsolated(string id)
    {
        var settings = InstanceSettings.Load(Directory, id);
        settings.Isolate = true;
        settings.Save(Directory, id);
    }

    /// <summary>进行中的联机会话，退出时一起结束。</summary>
    public IDisposable? ActiveMultiplayer { get; set; }

    /// <summary>联机页开着“仅游戏”转发时，启动游戏要用的转发（否则为 null）。</summary>
    public Amber.Core.Multiplayer.Transport.GameProxy? ActiveGameProxy { get; set; }

    public void Dispose()
    {
        ActiveMultiplayer?.Dispose();
        _http.Dispose();
    }
}

public sealed record LaunchRequest(string VersionId, string? JoinServer = null, string? JoinWorld = null);
