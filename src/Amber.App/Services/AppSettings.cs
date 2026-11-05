using Amber.Core;
using System.Text.Json;
using Amber.Core.Launch;

namespace Amber.App.Services;

/// <summary>启动器设置，保存在 %APPDATA%\Amber\settings.json。</summary>
public sealed class AppSettings
{
    public string GameDirectory { get; set; } = Amber.Core.Game.GameDirectory.DefaultRoot();

    /// <summary>auto / official / mirror</summary>
    public string DownloadSource { get; set; } = "auto";

    /// <summary>同时下载的文件数。</summary>
    public int DownloadConcurrency { get; set; } = 32;

    /// <summary>为空表示按版本要求自动选择。</summary>
    public string? JavaPath { get; set; }

    public bool AutoJavaDownload { get; set; } = true;
    public bool AutoMemory { get; set; } = true;
    public int MaxMemoryMb { get; set; } = 4096;
    public bool IsolateVersions { get; set; } = true;
    public string PlayerName { get; set; } = "Player";
    public string? LastVersion { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool Fullscreen { get; set; }
    public string? JvmArgs { get; set; }
    public string? GameArgs { get; set; }
    public string? PreLaunchCommand { get; set; }
    public string? PostExitCommand { get; set; }

    /// <summary>可选：覆盖微软应用 ID。留空时读环境变量 AMBER_MS_CLIENT_ID，再没有就用内置默认值。</summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>用户自己申请的 CurseForge API Key，留空时读环境变量 AMBER_CF_KEY。</summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>CurseForge 接口地址。留空用官方。可以指向用户信任的代理或镜像。</summary>
    public string? CurseForgeApiRoot { get; set; }

    /// <summary>更新检查地址。留空就不检查，启动器不会为此联网。</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>system / light / dark</summary>
    public string Theme { get; set; } = "system";

    /// <summary>界面语言：zh-Hans / zh-Hant / en / ja；null 表示跟随系统。</summary>
    public string? Language { get; set; }

    /// <summary>启动器窗口上次的位置和大小（逻辑像素，按当时那块屏幕记）。</summary>
    public WindowPlacement? LauncherWindow { get; set; }

    /// <summary>联机用的 EasyTier 中继节点，空表示用 EasyTier 社区的公共节点。</summary>
    public List<string>? MultiplayerPeers { get; set; }

    /// <summary>最近玩过的实例 id，最近的在前（启动页的“最近游玩”）。</summary>
    public List<string>? RecentInstances { get; set; }

    /// <summary>侧边栏手动收成图标栏。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>减少动画（页面切换、抽屉滑入这些都不做过渡）。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>启动游戏后启动器怎么办：minimize（最小化，默认）/ keep（保持）/ tray（隐藏到托盘）。</summary>
    public string AfterLaunch { get; set; } = "minimize";

    /// <summary>打开启动器时检查一次更新（只在设置导航上显示提示，不自动下载）。</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>删除的模组、存档等在回收目录里保留几天，过期自动清掉。</summary>
    public int TrashRetentionDays { get; set; } = 7;

    /// <summary>实例的显示名和状态，按实例 id（文件夹名）记。显示名和文件夹名分开，改名只改显示名。</summary>
    public Dictionary<string, InstanceMeta>? Instances { get; set; }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = AmberJson.WithReflectionFallback(AppJsonContext.Default),
    };

    /// <summary>Amber 自己的数据目录。单元测试用 AMBER_DATA_DIR 指到临时目录，免得读写真实的设置和账号。</summary>
    public static string DataDir => Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber");
    public static string FilePath => Path.Combine(DataDir, "settings.json");

    public InstanceSettings ToGlobalInstanceSettings() => new()
    {
        Isolate = IsolateVersions,
        JavaPath = JavaPath,
        AutoMemory = AutoMemory,
        MaxMemoryMb = MaxMemoryMb,
        JvmArgs = JvmArgs,
        GameArgs = GameArgs,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        Fullscreen = Fullscreen,
        PreLaunchCommand = PreLaunchCommand,
        PostExitCommand = PostExitCommand,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // 设置文件损坏时回到默认值，原文件保留一份方便排查。
            File.Copy(FilePath, FilePath + ".broken", true);
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions.Info<AppSettings>()));
        File.Move(temp, FilePath, true);
    }
}

/// <summary>实例在启动器里的附加信息（不写进游戏的版本文件）。</summary>
public sealed class InstanceMeta
{
    /// <summary>界面上显示的名字；null 时按游戏版本和加载器生成。</summary>
    public string? DisplayName { get; set; }

    /// <summary>null 表示正常；incomplete（安装未完成）/ broken（已损坏）。</summary>
    public string? State { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>测试用：比较新旧两种读写方式。</summary>
internal static class AppSettingsJson
{
    internal static JsonSerializerOptions Options => AppSettings.JsonOptions;
    internal static System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver Generated => AppJsonContext.Default;
}

/// <summary>窗口位置（物理像素）和大小（逻辑像素），以及是否最大化。</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
