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
    /// <summary>
    /// 当前的游戏目录和建在它上面的下载器、安装器。换目录、换下载源时整个换成新的一份（Reload）。
    /// 跨 await 的操作（安装、启动、补全文件）在开始时取一份，全程用它，中途换了目录也不会一半装到旧目录、一半装到新目录。
    /// </summary>
    public GameContext Context { get; private set; } = null!;

    public GameDirectory Directory => Context.Directory;
    public Downloader Downloader => Context.Downloader;
    public VanillaInstaller Installer => Context.Installer;
    public JavaManager Java => Context.Java;
    public LoaderContext Loaders => Context.Loaders;
    public ModrinthProvider Modrinth { get; private set; } = null!;
    public CurseForgeProvider CurseForge { get; private set; } = null!;
    public ContentInstaller ContentInstaller => Context.ContentInstaller;
    public ModpackService Modpacks => Context.Modpacks;

    /// <summary>ctx 的游戏目录还是当前的游戏目录（期间只换了下载源之类的也算）。</summary>
    public bool IsCurrentDirectory(GameContext ctx) =>
        ReferenceEquals(ctx, Context)
        || string.Equals(Path.GetFullPath(ctx.Directory.Root).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Directory.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

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
        var directory = new GameDirectory(Settings.GameDirectory);
        var sources = Settings.DownloadSource switch
        {
            "official" => SourceChain.OfficialFirst,
            "mirror" => SourceChain.MirrorFirst,
            _ => SourceChain.Default(),
        };
        var downloader = new Downloader(_http, sources)
        {
            MaxConcurrency = Math.Clamp(Settings.DownloadConcurrency, 1, 128),
            Hashes = new HashCache(Path.Combine(Settings.GameDirectory, ".amber-hashes")),
        };
        var installer = new VanillaInstaller(downloader, directory);
        var java = new JavaManager(downloader, directory) { AutoDownload = Settings.AutoJavaDownload };
        var loaders = new LoaderContext(downloader, directory, installer, java.ForMajorAsync);
        Modrinth = new ModrinthProvider(_http);
        CurseForge = new CurseForgeProvider(_http, CurseForgeKey,
            string.IsNullOrWhiteSpace(Settings.CurseForgeApiRoot) ? CurseForgeProvider.DefaultApiRoot : Settings.CurseForgeApiRoot.Trim().TrimEnd('/'));
        var contentInstaller = new ContentInstaller(downloader, new Dictionary<ContentSource, IContentProvider>
        {
            [ContentSource.Modrinth] = Modrinth,
            [ContentSource.CurseForge] = CurseForge,
        });
        // 一次换成新的一份：读的人要么拿到全旧的，要么拿到全新的。
        Context = new GameContext(directory, downloader, installer, java, loaders, contentInstaller, new ModpackService(loaders, Modrinth, CurseForge));
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
            System.Diagnostics.Trace.WriteLine("Jay: saving current instance failed: " + e);
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
    public void MarkInstallStarted(string id) => MarkInstallStarted(Context, id);

    /// <summary>在 ctx 的游戏目录里放“安装未完成”标记；ctx 还是当前目录时才通知界面。</summary>
    public void MarkInstallStarted(GameContext ctx, string id)
    {
        try { ctx.Directory.MarkInstallStarted(id); }
        finally { if (IsCurrentDirectory(ctx)) NotifyInstanceStateChanged(id); }
    }

    /// <summary>去掉“安装未完成”标记并通知界面。</summary>
    public void MarkInstallFinished(string id) => MarkInstallFinished(Context, id);

    /// <summary>在 ctx 的游戏目录里去掉“安装未完成”标记；ctx 还是当前目录时才通知界面。</summary>
    public void MarkInstallFinished(GameContext ctx, string id)
    {
        try { ctx.Directory.MarkInstallFinished(id); }
        finally { if (IsCurrentDirectory(ctx)) NotifyInstanceStateChanged(id); }
    }

    /// <summary>
    /// 界面安装加载器：Forge 系先保留新目录，再放“安装未完成”标记；已有实例被拒绝时不改它的状态。
    /// 成功后由界面在全部准备完成时清除标记；失败时安装器只回滚本次拥有的目录。
    /// </summary>
    public Task<string> InstallLoaderAsync(LoaderVersion version, string? customId = null, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default) =>
        InstallLoaderAsync(Context, version, customId, progress, ct);

    /// <summary>在 ctx 的游戏目录里安装加载器：装到一半换了游戏目录，也全程用开始时的目录和安装器。</summary>
    public Task<string> InstallLoaderAsync(GameContext ctx, LoaderVersion version, string? customId = null, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default)
    {
        void Started(string id)
        {
            try { MarkInstallStarted(ctx, id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        var installer = LoaderInstallers.Create(version.Kind, ctx.Loaders);
        if (installer is ForgeLikeInstaller forge)
            return forge.InstallAsync(version, customId, progress, ct, Started);

        var id = string.IsNullOrWhiteSpace(customId) ? version.DefaultId : customId.Trim();
        Started(id);
        return installer.InstallAsync(version, customId, progress, ct);
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
    public InstanceSettings EffectiveSettings(string id) => EffectiveSettings(Context, id);

    public InstanceSettings EffectiveSettings(GameContext ctx, string id)
    {
        var own = InstanceSettings.Load(ctx.Directory, id);
        own.Isolate ??= OtherLauncherSettings.Isolation(ctx.Directory, id);
        return own.Over(Settings.ToGlobalInstanceSettings());
    }

    public string WorkDirFor(string id) => WorkDirFor(Context, id);

    public string WorkDirFor(GameContext ctx, string id) => UsesSharedDir(ctx, id) ? ctx.Directory.Root : ctx.Directory.VersionDir(id);

    /// <summary>没开“按实例分开文件夹”：模组、资源包、存档都在游戏目录下，所有实例共用。</summary>
    public bool UsesSharedDir(string id) => UsesSharedDir(Context, id);

    public bool UsesSharedDir(GameContext ctx, string id) => EffectiveSettings(ctx, id).Isolate != true;

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
        BaseLaunchOptions(Context, id, javaPath, session, sessionJvmArgs);

    public LaunchOptions BaseLaunchOptions(GameContext ctx, string id, string javaPath, GameSession session, IReadOnlyList<string> sessionJvmArgs) =>
        EffectiveSettings(ctx, id).ToLaunchOptions(ctx.Directory, id, javaPath, session, sessionJvmArgs) with { NoTrace = Settings.NoTrace };

    public GameInstance GetInstance(string id) => GetInstance(Context, id);

    public GameInstance GetInstance(GameContext ctx, string id) => GameInstance.Detect(ctx.Directory, id, WorkDirFor(ctx, id));

    /// <summary>实验功能“烘焙”：开了并且找到了加载器原型时返回服务，否则 null。缓存放在数据目录，所有实例共用。</summary>
    public Amber.Core.Bake.BakeService? Bake =>
        Settings.ExperimentalBake && Amber.Core.Bake.BakeToolkit.Find(Settings.BakeLoaderPath) is { } toolkit
            ? new Amber.Core.Bake.BakeService(toolkit, Path.Combine(AppSettings.DataDir, "bake-cache"))
            : null;

    /// <summary>
    /// 启动前的烘焙：实例支持时，输入没变就直接用上次的结果，变了就先烘焙模组和实例。
    /// 返回 null 表示这次按普通方式启动（没开、不支持）；reason 是给日志看的原因。
    /// </summary>
    public Task<(Amber.Core.Bake.BakedLaunch? Launch, string Reason)> PrepareBakeAsync(string id, string javaPath, IProgress<Amber.Core.Bake.BakeEvent>? progress, CancellationToken ct)
        => PrepareBakeAsync(Context, id, javaPath, progress, ct);

    /// <summary>烘焙与本次启动共用固定的游戏目录；中途切换目录不会混用另一个同名实例。</summary>
    public async Task<(Amber.Core.Bake.BakedLaunch? Launch, string Reason)> PrepareBakeAsync(GameContext ctx, string id, string javaPath, IProgress<Amber.Core.Bake.BakeEvent>? progress, CancellationToken ct)
    {
        if (Bake is not { } bake) return (null, Settings.ExperimentalBake ? Amber.Core.Localization.L.T("core.bake.loader_missing") : "");
        var instance = GetInstance(ctx, id);
        var (_, _, supported) = await bake.VersionAsync(javaPath, ct);
        if (!Amber.Core.Bake.BakeService.Supports(instance, supported, out var reason)) return (null, reason);
        var major = ctx.Directory.ResolveVersion(id).JavaVersion?.MajorVersion ?? 21;
        if (bake.Current(ctx.Directory, instance, major) is { } current) return (current, Amber.Core.Localization.L.T("core.bake.reused"));
        await bake.BakeModsAsync(javaPath, ctx.Directory, instance, progress, ct);
        return (await bake.BakeInstanceAsync(javaPath, major, ctx.Directory, instance, progress, ct), Amber.Core.Localization.L.T("core.bake.done"));
    }

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

/// <summary>启动请求。SkipBake：这一次不烘焙（烘焙后游戏没进主界面就崩了，玩家点了“不烘焙再启动”）。</summary>
public sealed record LaunchRequest(string VersionId, string? JoinServer = null, string? JoinWorld = null, bool SkipBake = false)
{
    // 只供本机烘焙重试使用；排队或等待多开确认期间切换目录后，原请求不能落到同名实例。
    internal GameContext? ExpectedContext { get; init; }
}

/// <summary>一个游戏目录，以及建在它上面的下载器、安装器、Java、加载器和资源安装（一起创建，之后不变）。</summary>
public sealed record GameContext(
    GameDirectory Directory,
    Downloader Downloader,
    VanillaInstaller Installer,
    JavaManager Java,
    LoaderContext Loaders,
    ContentInstaller ContentInstaller,
    ModpackService Modpacks);
