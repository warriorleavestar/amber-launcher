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

    /// <summary>联机用的 EasyTier 中继节点，空表示用 EasyTier 社区的公共节点。</summary>
    public List<string>? MultiplayerPeers { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = AmberJson.WithReflectionFallback(AppJsonContext.Default),
    };

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber");
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

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
