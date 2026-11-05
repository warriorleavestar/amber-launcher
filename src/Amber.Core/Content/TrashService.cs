using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Content;

/// <summary>回收目录操作失败的原因（界面按原因显示对应的翻译文字）。</summary>
public enum TrashFailure
{
    /// <summary>要删除的东西已经不在了。</summary>
    SourceMissing,
    /// <summary>找不到能放回收目录的位置。</summary>
    NoTrashLocation,
    /// <summary>不能删除回收目录本身。</summary>
    IsTrashItself,
    /// <summary>回收目录会落在游戏扫描的目录里。</summary>
    TrashInScannedFolder,
    /// <summary>记录里的原位置不在这个回收目录所属的根目录下面，不恢复。</summary>
    OriginalOutsideOwner,
    /// <summary>回收目录里已经没有这一项了。</summary>
    EntryMissing,
    /// <summary>原来所在的文件夹（比如实例）已经不在了：不恢复，免得凭空建出一个看不见的实例文件夹。</summary>
    ParentMissing,
    /// <summary>原位置已经有同名的东西，而这一项不能改名放回（比如实例：文件夹名要和里面的版本文件对得上）。</summary>
    TargetExists,
}

/// <summary>回收目录操作失败（文件被占用这类系统错误照常抛 IOException）。</summary>
public sealed class TrashException(TrashFailure reason, string path) : IOException(reason + ": " + path)
{
    public TrashFailure Reason { get; } = reason;
    public string PathInvolved { get; } = path;
}

/// <summary>回收目录里的一项：原来在哪、什么时候删的、多大，以及它现在放在哪。</summary>
public sealed record TrashEntry(string Id, string TrashRoot, string OriginalPath, DateTimeOffset DeletedAt, long SizeBytes, bool IsDirectory)
{
    /// <summary>这一项在回收目录里的文件夹（里面是 entry.json 和 item/原名）。</summary>
    public string EntryDir => Path.Combine(TrashRoot, Id);

    /// <summary>被删的文件或文件夹现在的位置。</summary>
    public string StoredPath => Path.Combine(EntryDir, TrashService.ItemFolder, Path.GetFileName(OriginalPath));

    public string Name => Path.GetFileName(OriginalPath);
}

/// <summary>
/// 删除改成移进回收目录（不用系统回收站：从回收站撤销基本做不到，而且要走 Shell）。
/// 规则（改版规格 3.9）：
///   • 回收目录是 {游戏目录}/.amber-trash；被删的东西和游戏目录不在同一个卷上时，放在它所属实例的根目录下的 .amber-trash，
///     保证是同卷改名，不会退化成复制。
///   • 回收目录不能在游戏会扫描的目录（mods、saves、resourcepacks…）下面。
///   • 每一项一个文件夹：entry.json 记原路径和时间，item/ 里是原来的文件或文件夹。
///   • 恢复时只恢复到这个回收目录所属的根目录下面（entry.json 被改了也不会把东西搬到别处）。
///   • 过期清理只删回收目录里由这里建的条目文件夹。
/// </summary>
public static class TrashService
{
    public const string TrashFolder = ".amber-trash";
    internal const string ItemFolder = "item";
    private const string EntryFile = "entry.json";

    /// <summary>游戏会扫描的目录名：回收目录不能在这些目录里面。</summary>
    public static readonly IReadOnlySet<string> ScannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "resourcepacks", "shaderpacks", "saves", "screenshots", "datapacks", "config", "versions", "libraries", "assets", "texturepacks", "coremods",
    };

    /// <summary>默认保留天数（设置里可改）。</summary>
    public const int DefaultKeepDays = 7;

    /// <summary>回收目录总量超过这个数时提示用户。</summary>
    public const long WarnTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 选回收目录：和游戏目录同卷就用游戏目录下的；否则沿着被删路径往上，跳过 mods、saves 这类被扫描的目录，用第一个不是它们的上级（通常是实例根目录）。
    /// </summary>
    public static string TrashRootFor(string path, string gameDir)
    {
        var full = Path.GetFullPath(path);
        var game = Path.GetFullPath(gameDir);
        if (SameVolume(full, game)) return Path.Combine(game, TrashFolder);
        var dir = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        while (dir is not null && ScannedFolders.Contains(Path.GetFileName(dir)))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new TrashException(TrashFailure.NoTrashLocation, path);
        return Path.Combine(dir, TrashFolder);
    }

    /// <summary>把文件或文件夹移进回收目录，返回记录。原来的东西不存在时抛 FileNotFoundException；被占用等情况抛 IOException，原文件不动。</summary>
    public static TrashEntry MoveToTrash(string path, string gameDir, DateTimeOffset? now = null)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full)) throw new TrashException(TrashFailure.SourceMissing, full);
        var root = TrashRootFor(full, gameDir);
        if (IsUnder(root, full) || string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
            throw new TrashException(TrashFailure.IsTrashItself, full);
        EnsureNotInScanned(root);

        var when = now ?? DateTimeOffset.Now;
        var size = SizeOf(full);
        var id = when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var entry = new TrashEntry(id, root, full, when, size, isDir);
        var itemDir = Path.Combine(entry.EntryDir, ItemFolder);
        Directory.CreateDirectory(itemDir);
        HideOnWindows(root);
        // 先写记录再移动：移动成功后才算删了；移动失败就把只含记录的条目文件夹收掉，原文件不动。
        WriteEntry(entry);
        try
        {
            if (isDir) Directory.Move(full, entry.StoredPath);
            else File.Move(full, entry.StoredPath);
        }
        catch
        {
            TryRemoveEmptyEntry(entry);
            throw;
        }
        return entry;
    }

    /// <summary>
    /// 恢复到原位置。原位置已经有同名的东西时，在名字后面加“ (1)”“ (2)”……（扩展名保留），返回实际恢复到的路径。
    /// keepName 时不改名，有同名的就抛 TargetExists（实例文件夹改了名，里面的 json 和 jar 就对不上了，会变成打不开的“A (1)”）。
    /// </summary>
    public static string Restore(TrashEntry entry, bool keepName = false)
    {
        var owner = Path.GetDirectoryName(Path.GetFullPath(entry.TrashRoot))!;
        var original = Path.GetFullPath(entry.OriginalPath);
        if (!IsUnder(owner, original)) throw new TrashException(TrashFailure.OriginalOutsideOwner, original);
        var stored = entry.StoredPath;
        var isDir = Directory.Exists(stored);
        if (!isDir && !File.Exists(stored)) throw new TrashException(TrashFailure.EntryMissing, stored);
        // 只补建一层（比如被删空的 saves、mods）；再往上也没了，说明它所在的实例已经被删了。
        var parent = Path.GetDirectoryName(original)!;
        if (!Directory.Exists(parent) && !string.Equals(parent, owner, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(parent) is { } grand && !Directory.Exists(grand))
            throw new TrashException(TrashFailure.ParentMissing, parent);
        if (keepName && (File.Exists(original) || Directory.Exists(original)))
            throw new TrashException(TrashFailure.TargetExists, original);
        Directory.CreateDirectory(parent);
        var target = FreeName(original);
        if (isDir) Directory.Move(stored, target);
        else File.Move(stored, target);
        TryRemoveEmptyEntry(entry);
        return target;
    }

    /// <summary>列出一个回收目录里的条目，新的在前。读不了的条目跳过。</summary>
    public static List<TrashEntry> List(string trashRoot)
    {
        var result = new List<TrashEntry>();
        if (!Directory.Exists(trashRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(trashRoot))
            if (ReadEntry(trashRoot, Path.GetFileName(dir)) is { } e && (File.Exists(e.StoredPath) || Directory.Exists(e.StoredPath)))
                result.Add(e);
        return result.OrderByDescending(e => e.DeletedAt).ToList();
    }

    public static long TotalSize(string trashRoot) => List(trashRoot).Sum(e => e.SizeBytes);

    /// <summary>
    /// 清掉超过保留天数的条目（启动时调用）。只删这里建的条目文件夹（名字和 entry.json 都对得上），返回清掉的条数。
    /// 文件被占用等删不掉的跳过，下次再试。
    /// </summary>
    public static int Purge(string trashRoot, int keepDays, DateTimeOffset? now = null)
    {
        if (keepDays < 0 || !Directory.Exists(trashRoot)) return 0;
        var cutoff = (now ?? DateTimeOffset.Now) - TimeSpan.FromDays(keepDays);
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(trashRoot))
        {
            if (ReadEntry(trashRoot, Path.GetFileName(dir)) is not { } e || e.DeletedAt > cutoff) continue;
            try
            {
                // Directory.Delete 遇到 junction / 符号链接只删链接本身，不会跟进去删目标。
                Directory.Delete(e.EntryDir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>文件或文件夹的总大小（字节）。读不了的子项跳过；不跟进 junction / 符号链接。</summary>
    public static long SizeOf(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(path));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (child is FileInfo f) total += f.Length;
                else if (child is DirectoryInfo d) stack.Push(d);
            }
        }
        return total;
    }

    internal static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>child 在 parent 下面（不含相等），按路径段比较，不会把 C:\Game2 当成 C:\Game 的子目录。</summary>
    internal static bool IsUnder(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(child);
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNotInScanned(string root)
    {
        for (var dir = Path.GetDirectoryName(root); dir is not null; dir = Path.GetDirectoryName(dir))
            if (ScannedFolders.Contains(Path.GetFileName(dir)))
                throw new TrashException(TrashFailure.TrashInScannedFolder, root);
    }

    private static string FreeName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var isFile = File.Exists(path);
        var name = isFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        var ext = isFile ? Path.GetExtension(path) : "";
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void HideOnWindows(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var info = new DirectoryInfo(root);
            if (!info.Attributes.HasFlag(FileAttributes.Hidden)) info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryRemoveEmptyEntry(TrashEntry entry)
    {
        try
        {
            var item = Path.Combine(entry.EntryDir, ItemFolder);
            if (Directory.Exists(item) && !Directory.EnumerateFileSystemEntries(item).Any()) Directory.Delete(item);
            var file = Path.Combine(entry.EntryDir, EntryFile);
            if (File.Exists(file) && !Directory.Exists(item)) File.Delete(file);
            if (Directory.Exists(entry.EntryDir) && !Directory.EnumerateFileSystemEntries(entry.EntryDir).Any()) Directory.Delete(entry.EntryDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteEntry(TrashEntry e)
    {
        using var stream = File.Create(Path.Combine(e.EntryDir, EntryFile));
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteNumber("version", 1);
        w.WriteString("originalPath", e.OriginalPath);
        w.WriteString("deletedAt", e.DeletedAt.ToString("O", CultureInfo.InvariantCulture));
        w.WriteNumber("sizeBytes", e.SizeBytes);
        w.WriteBoolean("isDirectory", e.IsDirectory);
        w.WriteEndObject();
    }

    private static TrashEntry? ReadEntry(string trashRoot, string id)
    {
        var file = Path.Combine(trashRoot, id, EntryFile);
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            var r = doc.RootElement;
            var original = r.GetProperty("originalPath").GetString();
            if (string.IsNullOrEmpty(original) || !DateTimeOffset.TryParse(r.GetProperty("deletedAt").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                return null;
            return new TrashEntry(id, trashRoot, original, at, r.TryGetProperty("sizeBytes", out var s) ? s.GetInt64() : 0,
                r.TryGetProperty("isDirectory", out var d) && d.GetBoolean());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
