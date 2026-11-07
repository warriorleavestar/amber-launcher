using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>客户端：连 Jay 服务器网关。</summary>
public static class GateClient
{
    /// <summary>用邀请码入服（连网关、握手、拿到访问密钥）。</summary>
    public static async Task<GateMembership> EnrollAsync(GateInvite invite, MemberIdentity identity, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        await ConnectAsync(tcp, invite.Host, invite.Port, ct);
        await using var stream = tcp.GetStream();
        return await GateProtocol.EnrollAsync(stream, invite, identity.KeyPair, ct);
    }

    /// <summary>给 <see cref="TunnelClient"/> 用：每条游戏连接打开一条到网关的已认证加密流。</summary>
    public static Func<CancellationToken, Task<Stream>> Opener(GateMembership membership, MemberIdentity identity) => async ct =>
    {
        var tcp = new TcpClient();
        try
        {
            await ConnectAsync(tcp, membership.Host, membership.Port, ct);
            Pump.Tune(tcp);
            return await GateProtocol.JoinAsync(new OwnedStream(tcp), membership, identity.KeyPair, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    };

    private static async Task ConnectAsync(TcpClient tcp, string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(L.T("core.gate.connect.timeout"));
        }
    }
}

/// <summary>
/// 客户端保存的 Jay 服务器（数据目录下的 jay-servers.json）。访问密钥加密保存：Windows 用 DPAPI（当前用户），其他系统靠文件只给当前用户读（0600）。
/// 访问密钥单独泄露也进不了服：还要这台设备的身份私钥。
/// </summary>
public sealed class JayServerList
{
    public const string FileName = "jay-servers.json";
    private static readonly byte[] Entropy = "Jay.Gate.AccessKey"u8.ToArray();

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string ServerPublicKey { get; set; } = "";
        public string ProtectedAccessKey { get; set; } = "";
        public string Username { get; set; } = "";
        public DateTimeOffset Added { get; set; }
    }

    public sealed class FileModel
    {
        public List<Entry> Servers { get; set; } = [];
    }

    private readonly string _path;
    private readonly Lock _gate = new();

    public JayServerList(string dataDirectory) => _path = Path.Combine(dataDirectory, FileName);

    public IReadOnlyList<Entry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(_path), JayServersJsonContext.Default.FileModel)?.Servers ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>保存入服结果。同一个网关 + 用户名已经有了就替换（重新申请）。</summary>
    public Entry Add(GateMembership membership, string? displayName = null)
    {
        lock (_gate)
        {
            var model = new FileModel { Servers = [.. Load()] };
            var serverKey = Convert.ToBase64String(membership.ServerPublicKey);
            model.Servers.RemoveAll(e => e.ServerPublicKey == serverKey && string.Equals(e.Username, membership.Username, StringComparison.OrdinalIgnoreCase));
            var entry = new Entry
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? membership.Host : displayName.Trim(),
                Host = membership.Host,
                Port = membership.Port,
                ServerPublicKey = serverKey,
                ProtectedAccessKey = Protect(membership.AccessKey),
                Username = membership.Username,
                Added = DateTimeOffset.UtcNow,
            };
            model.Servers.Add(entry);
            Save(model);
            return entry;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var model = new FileModel { Servers = [.. Load()] };
            if (model.Servers.RemoveAll(e => e.Id == id) > 0) Save(model);
        }
    }

    public static GateMembership ToMembership(Entry entry) =>
        new(entry.Host, entry.Port, Convert.FromBase64String(entry.ServerPublicKey), Unprotect(entry.ProtectedAccessKey), entry.Username);

    private void Save(FileModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(model, JayServersJsonContext.Default.FileModel));
            stream.Flush(true);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, true);
    }

    private static string Protect(byte[] key) => OperatingSystem.IsWindows()
        ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser))
        : "plain:" + Convert.ToBase64String(key);

    private static byte[] Unprotect(string stored)
    {
        if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException(L.T("core.gate.store.otherplatform"));
            return ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.CurrentUser);
        }
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return Convert.FromBase64String(stored[6..]);
        throw new CryptographicException(L.T("core.gate.store.corrupt"));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(JayServerList.FileModel))]
internal sealed partial class JayServersJsonContext : JsonSerializerContext;
