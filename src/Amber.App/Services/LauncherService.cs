using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.App.Services;

/// <summary>界面共用的核心对象。设置里的目录或下载源变了就调用 <see cref="Reload"/>。</summary>
public sealed class LauncherService : IDisposable
{
    private readonly HttpClient _http = Downloader.CreateHttpClient();

    public AppSettings Settings { get; }
    public AccountService Accounts { get; }
    public GameDirectory Directory { get; private set; } = null!;
    public Downloader Downloader { get; private set; } = null!;
    public VanillaInstaller Installer { get; private set; } = null!;
    public JavaManager Java { get; private set; } = null!;
    public LoaderContext Loaders { get; private set; } = null!;

    /// <summary>已安装版本列表变化（安装完成、切换目录）时触发。</summary>
    public event Action? InstalledVersionsChanged;

    /// <summary>账号增删或切换时触发。</summary>
    public event Action? AccountsChanged;

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
        Downloader = new Downloader(_http, sources);
        Installer = new VanillaInstaller(Downloader, Directory);
        Java = new JavaManager(Downloader, Directory);
        Loaders = new LoaderContext(Downloader, Directory, Installer, Java.ForMajorAsync);
        NotifyInstalledChanged();
    }

    public void NotifyAccountsChanged() => AccountsChanged?.Invoke();

    public void NotifyInstalledChanged() => InstalledVersionsChanged?.Invoke();

    public List<string> InstalledVersions() => Directory.ListInstalled().OrderDescending().ToList();

    public void Dispose() => _http.Dispose();
}
