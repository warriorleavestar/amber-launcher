using Amber.Core.Localization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Game;

/// <summary>世界里的一个数据包。Id 是游戏在 level.dat 里记的名字（file/文件名）。</summary>
public sealed record LocalDatapack(string FileName, string Path, bool Enabled, string? Description, bool IsFolder)
{
    public string Id => "file/" + FileName;
}

/// <summary>
/// 世界的 datapacks 文件夹。启用和禁用和游戏里的“数据包”界面一样，改的是 level.dat 里的 DataPacks.Enabled / Disabled，
/// 文件本身不动；放进文件夹但两张表里都没有的数据包，游戏下次进世界时会自动启用。
/// </summary>
public static class DatapackManager
{
    public static string DirectoryOf(string worldPath) => System.IO.Path.Combine(worldPath, "datapacks");

    public static List<LocalDatapack> List(string worldPath)
    {
        var dir = DirectoryOf(worldPath);
        if (!Directory.Exists(dir)) return [];
        var (enabled, disabled) = ReadLists(worldPath);
        var result = new List<LocalDatapack>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = System.IO.Path.GetFileName(path);
            var isFolder = Directory.Exists(path);
            if (isFolder ? !File.Exists(System.IO.Path.Combine(path, "pack.mcmeta")) : !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var id = "file/" + name;
            // 两张表都没有 = 游戏还没见过它，进世界时会启用。
            var on = !disabled.Contains(id) || enabled.Contains(id);
            result.Add(new LocalDatapack(name, path, on, ReadDescription(path, isFolder), isFolder));
        }
        return result.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>改 level.dat 启用或禁用。游戏运行中改了会被游戏存档时覆盖，所以要在游戏关闭时用。</summary>
    public static void SetEnabled(string worldPath, string fileName, bool enabled)
    {
        var levelDat = System.IO.Path.Combine(worldPath, "level.dat");
        if (!File.Exists(levelDat)) throw new FileNotFoundException(L.T("core.game.datapack.missinglevel"), levelDat);
        var root = Nbt.ReadFile(levelDat);
        if (root.GetValueOrDefault("Data") is not Dictionary<string, object> data) throw new InvalidDataException(L.T("core.game.datapack.missingdata"));
        if (data.GetValueOrDefault("DataPacks") is not Dictionary<string, object> packs)
        {
            packs = [];
            data["DataPacks"] = packs;
        }
        var on = StringList(packs, "Enabled");
        var off = StringList(packs, "Disabled");
        var id = "file/" + fileName;
        on.Items.RemoveAll(i => i as string == id);
        off.Items.RemoveAll(i => i as string == id);
        (enabled ? on : off).Items.Add(id);

        // 和游戏自己存档一样，先把旧文件留成 level.dat_old。
        File.Copy(levelDat, levelDat + "_old", true);
        Nbt.WriteFile(levelDat, root, gzip: true);
    }

    /// <summary>复制数据包到世界里。文件夹形式的要有 pack.mcmeta，文件要是 zip。</summary>
    public static string Add(string worldPath, string source)
    {
        var dir = DirectoryOf(worldPath);
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(source.TrimEnd('\\', '/')));
        if (Directory.Exists(source))
        {
            if (!File.Exists(System.IO.Path.Combine(source, "pack.mcmeta"))) throw new InvalidDataException(L.T("core.game.datapack.invalidfolder"));
            CopyDirectory(source, target);
        }
        else
        {
            if (!IsDatapackZip(source)) throw new InvalidDataException(L.F("core.game.datapack.invalidarchive", System.IO.Path.GetFileName(source)));
            var name = System.IO.Path.GetFileName(source);
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) target += ".zip";
            File.Copy(source, target, true);
        }
        return target;
    }

    /// <summary>根目录有 pack.mcmeta 和 data/ 才算数据包；只有 assets/ 的是资源包。</summary>
    public static bool IsDatapackZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("pack.mcmeta") is not null && zip.Entries.Any(e => e.FullName.StartsWith("data/", StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static (HashSet<string> Enabled, HashSet<string> Disabled) ReadLists(string worldPath)
    {
        var levelDat = System.IO.Path.Combine(worldPath, "level.dat");
        try
        {
            if (File.Exists(levelDat)
                && Nbt.ReadFile(levelDat).GetValueOrDefault("Data") is Dictionary<string, object> data
                && data.GetValueOrDefault("DataPacks") is Dictionary<string, object> packs)
            {
                return (Strings(packs.GetValueOrDefault("Enabled")), Strings(packs.GetValueOrDefault("Disabled")));
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or IndexOutOfRangeException or ArgumentException)
        {
            // level.dat 读不了就当全部启用，至少能列出来。
        }
        return ([], []);
    }

    private static HashSet<string> Strings(object? list) =>
        list is Nbt.NbtList l ? l.Items.OfType<string>().ToHashSet(StringComparer.Ordinal) : [];

    private static Nbt.NbtList StringList(Dictionary<string, object> parent, string key)
    {
        if (parent.GetValueOrDefault(key) is Nbt.NbtList { ElementType: 8 } existing) return existing;
        if (parent.GetValueOrDefault(key) is Nbt.NbtList { Items.Count: 0 } || !parent.ContainsKey(key))
        {
            var created = new Nbt.NbtList(8, []);
            parent[key] = created;
            return created;
        }
        throw new InvalidDataException(L.F("core.game.datapack.invalidformat", key));
    }

    private static string? ReadDescription(string path, bool isFolder)
    {
        try
        {
            string text;
            if (isFolder)
            {
                text = ArchiveMetadata.ReadFileText(System.IO.Path.Combine(path, "pack.mcmeta"));
            }
            else
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.GetEntry("pack.mcmeta") is not { } entry) return null;
                text = ArchiveMetadata.ReadText(entry);
            }
            var description = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?["pack"]?["description"];
            return PlainText(description)?.Trim();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>描述可以是字符串，也可以是文字组件（对象或数组）。去掉 § 颜色代码。</summary>
    private static string? PlainText(JsonNode? node)
    {
        var text = node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonObject o => (o["text"]?.GetValue<string>() ?? "") + string.Concat((o["extra"] as JsonArray ?? []).Select(PlainText)),
            JsonArray a => string.Concat(a.Select(PlainText)),
            _ => null,
        };
        if (text is null) return null;
        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length) { i++; continue; }
            builder.Append(text[i]);
        }
        return builder.ToString();
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
