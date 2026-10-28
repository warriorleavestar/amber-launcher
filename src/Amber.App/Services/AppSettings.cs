using System.Text.Json;
using Amber.Core.Game;

namespace Amber.App.Services;

/// <summary>启动器设置，保存在 %APPDATA%\Amber\settings.json。</summary>
public sealed class AppSettings
{
    public string GameDirectory { get; set; } = Amber.Core.Game.GameDirectory.DefaultRoot();

    /// <summary>auto / official / mirror</summary>
    public string DownloadSource { get; set; } = "auto";

    /// <summary>为空表示按版本要求自动选择。</summary>
    public string? JavaPath { get; set; }

    public int MaxMemoryMb { get; set; } = 4096;
    public bool IsolateVersions { get; set; } = true;
    public string PlayerName { get; set; } = "Player";
    public string? LastVersion { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, FilePath, true);
    }
}
