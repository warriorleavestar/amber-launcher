using Amber.Core.Localization;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Game;

public sealed record WorldInfo(string FolderName, string Path, string Name, string GameMode, DateTimeOffset? LastPlayed, string? Version, long? SizeBytes = null);

/// <summary>saves 文件夹：列出世界、备份、从压缩包导入。</summary>
public static class WorldManager
{
    public static List<WorldInfo> List(string savesDir)
    {
        if (!Directory.Exists(savesDir)) return [];
        var result = new List<WorldInfo>();
        foreach (var folder in Directory.EnumerateDirectories(savesDir))
        {
            var levelDat = Path.Combine(folder, "level.dat");
            if (!File.Exists(levelDat)) continue;
            result.Add(Read(folder, levelDat));
        }
        return result.OrderByDescending(w => w.LastPlayed).ToList();
    }

    private static WorldInfo Read(string folder, string levelDat)
    {
        var folderName = Path.GetFileName(folder);
        try
        {
            var root = Nbt.ReadFile(levelDat);
            if (root.GetValueOrDefault("Data") is not Dictionary<string, object> data)
                return new WorldInfo(folderName, folder, folderName, L.T("core.game.world.unknownmode"), null, null);
            var last = data.GetValueOrDefault("LastPlayed") is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
            var version = data.GetValueOrDefault("Version") is Dictionary<string, object> v ? v.GetValueOrDefault("Name") as string : null;
            var hardcore = data.GetValueOrDefault("hardcore") is byte and not (byte)0;
            var mode = hardcore ? L.T("core.game.world.hardcore") : (data.GetValueOrDefault("GameType") as int? ?? 0) switch
            {
                0 => L.T("core.game.world.survival"),
                1 => L.T("core.game.world.creative"),
                2 => L.T("core.game.world.adventure"),
                3 => L.T("core.game.world.spectator"),
                _ => L.T("core.game.world.unknownmode"),
            };
            return new WorldInfo(folderName, folder, data.GetValueOrDefault("LevelName") as string ?? folderName, mode, last, version);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            return new WorldInfo(folderName, folder, L.F("core.game.world.corruptlevel", folderName), L.T("core.game.world.unknownmode"), null, null);
        }
    }

    /// <summary>打包备份到 saves 旁边的 backups 文件夹。返回备份文件路径。</summary>
    public static string Backup(WorldInfo world, string backupsDir)
    {
        Directory.CreateDirectory(backupsDir);
        var target = Path.Combine(backupsDir, $"{world.FolderName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        // session.lock 被运行中的游戏占用，不需要备份。
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(world.Path, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "session.lock") continue;
            var name = world.FolderName + "/" + Path.GetRelativePath(world.Path, file).Replace('\\', '/');
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            input.CopyTo(output);
        }
        return target;
    }

    /// <summary>从压缩包导入世界（压缩包里某层有 level.dat 即可）。返回新文件夹名。</summary>
    public static string Import(string zipPath, string savesDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        // 先统一归档路径的两种分隔符，并在任何写入前验证全部路径。
        var entries = zip.Entries.Select(e => (Entry: e, Name: ImportPath(e.FullName))).ToList();
        var level = entries.Where(e => e.Name.Split('/').Last() == "level.dat").OrderBy(e => e.Name.Length).FirstOrDefault();
        if (level.Entry is null) throw new InvalidDataException(L.T("core.game.world.missinglevel"));
        var prefix = level.Name[..^"level.dat".Length];
        var baseName = prefix.TrimEnd('/').Split('/').LastOrDefault(s => s.Length > 0) ?? Path.GetFileNameWithoutExtension(zipPath);
        GameDirectory.CheckId(baseName);
        var folder = baseName;
        for (var i = 2; Directory.Exists(GameDirectory.Contained(savesDir, folder)) || File.Exists(GameDirectory.Contained(savesDir, folder)); i++)
            folder = $"{baseName} ({i})";

        var target = GameDirectory.Contained(savesDir, folder);
        var files = entries.Where(e => e.Name.StartsWith(prefix, StringComparison.Ordinal) && !e.Name.EndsWith('/'))
            .Select(e => (e.Entry, Path: GameDirectory.Contained(target, e.Name[prefix.Length..]))).ToList();
        foreach (var (entry, path) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
        return folder;
    }

    private static string ImportPath(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException(L.F("core.game.world.unsafepath", name));
        foreach (var part in normalized.TrimEnd('/').Split('/')) GameDirectory.CheckId(part);
        return normalized;
    }
}

public sealed record ServerEntry(string Name, string Address);

public sealed record ServerStatus(bool Online, string? Motd, string? VersionName, int? Players, int? MaxPlayers, long? PingMs, string? Error);

/// <summary>servers.dat：游戏的多人服务器列表。保留游戏写入的其他字段。</summary>
public static class ServerList
{
    internal const int MaxStatusPacketBytes = 1024 * 1024;
    public static List<ServerEntry> Read(string workDir)
    {
        var path = Path.Combine(workDir, "servers.dat");
        if (!File.Exists(path)) return [];
        try
        {
            var root = Nbt.ReadFile(path);
            if (root.GetValueOrDefault("servers") is not Nbt.NbtList list) return [];
            return list.Items.OfType<Dictionary<string, object>>()
                .Select(s => new ServerEntry(s.GetValueOrDefault("name") as string ?? "Minecraft Server", s.GetValueOrDefault("ip") as string ?? ""))
                .Where(s => s.Address.Length > 0).ToList();
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return [];
        }
    }

    public static void Add(string workDir, ServerEntry server)
    {
        var (root, list) = Load(workDir);
        list.Items.RemoveAll(i => i is Dictionary<string, object> d && (d.GetValueOrDefault("ip") as string) == server.Address);
        list.Items.Add(new Dictionary<string, object> { ["name"] = server.Name, ["ip"] = server.Address, ["hidden"] = (byte)0 });
        Nbt.WriteFile(Path.Combine(workDir, "servers.dat"), root);
    }

    public static void Remove(string workDir, string address)
    {
        var (root, list) = Load(workDir);
        list.Items.RemoveAll(i => i is Dictionary<string, object> d && (d.GetValueOrDefault("ip") as string) == address);
        Nbt.WriteFile(Path.Combine(workDir, "servers.dat"), root);
    }

    private static (Dictionary<string, object> Root, Nbt.NbtList List) Load(string workDir)
    {
        var path = Path.Combine(workDir, "servers.dat");
        var root = File.Exists(path) ? Nbt.ReadFile(path) : [];
        if (root.GetValueOrDefault("servers") is not Nbt.NbtList list)
        {
            list = new Nbt.NbtList(10, []);
            root["servers"] = list;
        }
        return (root, list);
    }

    /// <summary>host 或 host:port 拆开。默认端口 25565，IPv6 要写成 [addr]:port。</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            var host = address[1..close];
            return (host, close + 2 < address.Length && int.TryParse(address[(close + 2)..], out var p6) ? p6 : 25565);
        }
        var colon = address.LastIndexOf(':');
        return colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var port)
            ? (address[..colon], port)
            : (address, 25565);
    }

    /// <summary>服务器列表 Ping（1.7+ 协议的状态查询）。超时或失败返回离线状态，不抛异常。</summary>
    public static async Task<ServerStatus> PingAsync(string address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            var (host, port) = ParseAddress(address);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, linked.Token);
            await using var stream = tcp.GetStream();

            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, 767);
            var hostBytes = Encoding.UTF8.GetBytes(host);
            WriteVarInt(handshake, hostBytes.Length);
            handshake.Write(hostBytes);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)port);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), linked.Token);
            await WritePacketAsync(stream, [0x00], linked.Token);

            var json = await ReadStatusPacketAsync(stream, linked.Token);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ping = new byte[9];
            ping[0] = 0x01;
            await WritePacketAsync(stream, ping, linked.Token);
            long? ms = null;
            try
            {
                var pong = await ReadPacketAsync(stream, 9, linked.Token);
                if (!pong.AsSpan().SequenceEqual(ping)) throw InvalidStatus();
                ms = watch.ElapsedMilliseconds;
            }
            catch (Exception e) when (e is EndOfStreamException or IOException) { /* 有的服务器不回 pong */ }

            return new ServerStatus(true, MotdText(json?["description"]), json?["version"]?["name"]?.GetValue<string>(),
                json?["players"]?["online"]?.GetValue<int>(), json?["players"]?["max"]?.GetValue<int>(), ms, null);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or JsonException or EndOfStreamException or InvalidDataException or InvalidOperationException or FormatException)
        {
            if (ct.IsCancellationRequested) throw;
            return new ServerStatus(false, null, null, null, null, null, e is OperationCanceledException ? L.T("core.mp.server.timeout") : e.Message);
        }
    }

    // 状态包很小；先限制完整帧，再在帧内验证 ID 和字符串长度，绝不按内层声明另行分配。
    internal static async Task<JsonObject> ReadStatusPacketAsync(Stream stream, CancellationToken ct = default)
    {
        var frame = await ReadPacketAsync(stream, MaxStatusPacketBytes, ct);
        using var packet = new MemoryStream(frame, writable: false);
        if (await ReadVarIntAsync(packet, ct) != 0) throw InvalidStatus();
        var length = await ReadVarIntAsync(packet, ct);
        if (length <= 0 || length != packet.Length - packet.Position) throw InvalidStatus();
        return JsonNode.Parse(frame.AsSpan((int)packet.Position, length)) as JsonObject ?? throw InvalidStatus();
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        if (length <= 0 || length > maxBytes) throw InvalidStatus();
        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, ct);
        return frame;
    }

    private static InvalidDataException InvalidStatus() => new(L.T("core.mp.server.invalidstatus"));

    private static string? MotdText(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return s;
            case JsonObject o:
                var builder = new StringBuilder(o["text"]?.GetValue<string>() ?? "");
                foreach (var extra in o["extra"] as JsonArray ?? [])
                    builder.Append(MotdText(extra));
                return builder.ToString();
            default:
                return null;
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);
        await stream.WriteAsync(packet.ToArray(), ct);
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var buffer = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, ct);
            if (shift == 28 && (buffer[0] & 0xf0) != 0) throw InvalidStatus();
            result |= (buffer[0] & 0x7f) << shift;
            if ((buffer[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException(L.T("core.mp.server.varinttoolong"));
    }
}
