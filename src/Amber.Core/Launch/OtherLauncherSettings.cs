using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;

namespace Amber.Core.Launch;

/// <summary>
/// 把 Amber 指向 PCL、HMCL 用过的游戏目录时，读它们给每个版本记下的“版本隔离”设置，
/// 不然开了隔离的版本会在共享目录里启动，看不到自己的模组和存档。
/// 只读隔离这一项：内存、Java 路径和 JVM 参数仍以 Amber 的设置为准。
/// </summary>
public static class OtherLauncherSettings
{
    /// <summary>
    /// 这个版本在其他启动器里是否开了版本隔离；不是其他启动器建的版本、或者它跟随全局设置时返回 null。
    /// </summary>
    public static bool? Isolation(GameDirectory dir, string id)
    {
        string versionDir;
        try { versionDir = dir.VersionDir(id); }
        catch (InvalidDataException) { return null; }
        if (!Directory.Exists(versionDir)) return null;

        var pcl = Path.Combine(versionDir, "PCL");
        var hmcl = Path.Combine(versionDir, "hmclversion.cfg");
        var hmclCurrent = Path.Combine(versionDir, ".hmcl", "config", "instance-game-settings.json");
        var hmclFlat = Path.Combine(versionDir, "instance-game-settings.json");
        var hmclNew = File.Exists(hmclCurrent) ? hmclCurrent : File.Exists(hmclFlat) ? hmclFlat : null;
        var fromPcl = PclIsolation(Path.Combine(pcl, "Setup.ini"));
        if (fromPcl is not null) return fromPcl;
        if (hmclNew is not null)
        {
            var (overridden, isolated) = CurrentHmclIsolation(hmclNew, dir.Root, versionDir);
            // 自定义目录无法用 Isolate 表达；也不能再让旧配置或内容推断覆盖它。
            if (overridden) return isolated;
        }
        else
        {
            var fromHmcl = HmclIsolation(hmcl);
            if (fromHmcl is not null) return fromHmcl;
        }

        // 其他启动器建的版本、但没写明（跟随它自己的全局设置）：版本目录里已经有游戏内容，就说明它一直是隔离着玩的。
        if (!Directory.Exists(pcl) && !File.Exists(hmcl) && hmclNew is null) return null;
        string[] content = ["mods", "saves", "config", "resourcepacks"];
        if (content.Any(c => Directory.Exists(Path.Combine(versionDir, c))) || File.Exists(Path.Combine(versionDir, "options.txt"))) return true;
        return null;
    }

    /// <summary>
    /// HMCL 当前的实例设置在 .hmcl/config/instance-game-settings.json。
    /// overrideProperties 包含 runningDirectory 且路径为空时使用版本目录；非空只识别共享根和版本目录。
    /// </summary>
    private static (bool Overridden, bool? Isolated) CurrentHmclIsolation(string path, string gameRoot, string versionDir)
    {
        var overridden = false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj
                || obj["overrideProperties"] is not JsonArray overrides
                || !overrides.Any(v => v is JsonValue value && value.TryGetValue<string>(out var name) && name == "runningDirectory"))
                return (false, null);
            overridden = true;
            if (obj["runningDirectory"] is null) return (true, true);
            if (obj["runningDirectory"] is not JsonValue directory || !directory.TryGetValue<string>(out var text)) return (true, null);
            if (string.IsNullOrWhiteSpace(text)) return (true, true);
            if (!Path.IsPathFullyQualified(text)) return (true, null);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionDir)), comparison)) return (true, true);
            if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)), comparison)) return (true, false);
            return (true, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (overridden, null);
        }
    }

    /// <summary>PCL 的版本设置：versions/X/PCL/Setup.ini，每行“键:值”。新版是 VersionArgumentIndieV2（True/False），旧版是 VersionArgumentIndie（1 隔离，2 不隔离）。</summary>
    internal static bool? PclIsolation(string setupIni)
    {
        if (!File.Exists(setupIni)) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(setupIni))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (values.TryGetValue("VersionArgumentIndieV2", out var v2) && bool.TryParse(v2, out var isolated)) return isolated;
        if (values.TryGetValue("VersionArgumentIndie", out var v1) && int.TryParse(v1, out var mode))
            return mode switch { 1 => true, 2 => false, _ => null };
        return null;
    }

    /// <summary>HMCL 的版本设置：versions/X/hmclversion.cfg 里的 gameDirType，可能是数字也可能是名字（0 / ROOT_FOLDER 共享，1 / VERSION_FOLDER 隔离，2 / CUSTOM 自定义）。</summary>
    internal static bool? HmclIsolation(string cfg)
    {
        if (!File.Exists(cfg)) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(cfg)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (root is not JsonObject obj) return null;
        // usesGlobal 为 true 时这个版本跟随 HMCL 的全局设置，我们不知道那边是什么。
        if (obj["usesGlobal"] is JsonValue g && g.TryGetValue<bool>(out var usesGlobal) && usesGlobal) return null;
        if (obj["gameDirType"] is not JsonValue type) return null;
        if (type.TryGetValue<int>(out var n)) return n switch { 0 => false, 1 => true, _ => null };
        if (type.TryGetValue<string>(out var s)) return s switch { "ROOT_FOLDER" => false, "VERSION_FOLDER" => true, _ => null };
        return null;
    }
}
