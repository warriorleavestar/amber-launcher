using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.App.Services;

/// <summary>界面共用的核心对象。设置里的目录或下载源变了就调用 <see cref="Reload"/>。</summary>
public sealed class LauncherService : IDisposable
{
    private readonly HttpClient _http = Downloader.CreateHttpClient();
    private List<JavaInstall>? _javaCache;

    public AppSettings Settings { get; }
    public GameDirectory Directory { get; private set; } = null!;
    public Downloader Downloader { get; private set; } = null!;
    public VanillaInstaller Installer { get; private set; } = null!;

    /// <summary>已安装版本列表变化（安装完成、切换目录）时触发。</summary>
    public event Action? InstalledVersionsChanged;

    public LauncherService(AppSettings settings)
    {
        Settings = settings;
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
        _javaCache = null;
        NotifyInstalledChanged();
    }

    public void NotifyInstalledChanged() => InstalledVersionsChanged?.Invoke();

    public List<string> InstalledVersions() => Directory.ListInstalled().OrderDescending().ToList();

    public async Task<List<JavaInstall>> GetJavaAsync(bool refresh = false)
    {
        if (_javaCache is null || refresh)
        {
            var root = Directory.Root;
            _javaCache = await Task.Run(() => JavaFinder.FindAll(root));
        }
        return _javaCache;
    }

    public void Dispose() => _http.Dispose();
}
