using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Auth;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>客户端：连 Jay 服务器网关。</summary>
public static class GateClient
{
    /// <summary>用邀请码入服（连网关、握手、拿到访问密钥）。</summary>
    public static async Task<GateMembership> EnrollAsync(GateInvite invite, MemberIdentity identity, CancellationToken ct = default)
    {
        using var tcp = await ConnectEntryAsync(new GateEndpoint(invite.Host, invite.Port), invite.Rotation, ct);
        await using var stream = tcp.GetStream();
        return await GateProtocol.EnrollAsync(stream, invite, identity.KeyPair, ct);
    }

    /// <summary>
    /// 给 <see cref="TunnelClient"/> 用：每条游戏连接打开一条到网关的已认证加密流。
    /// 隐私+ 的服务器经两个不同节点各开一条腿拼成 <see cref="SplitStream"/>；凑不齐两个能连上的节点就报错，不会退回直连。
    /// </summary>
    /// <param name="onNetwork">网关回了最新的连法（隐私等级、节点列表）时调用，调用方拿去存盘。</param>
    public static Func<CancellationToken, Task<Stream>> Opener(GateMembership membership, MemberIdentity identity, Action<GateNetwork>? onNetwork = null)
    {
        var current = membership;
        void Update(GateNetwork network)
        {
            current = current with { Network = network };
            onNetwork?.Invoke(network);
        }
        return async ct =>
        {
            var m = current;
            if (m.Network is { } plus && plus.Level.IsSplit())
            {
                // 节点被吊销到只剩一个：先经它问一次网关要最新的节点列表（网关会回“要拆分”并附上列表），够两个再走两路。
                if (plus.Relays.Count == 1)
                {
                    try { await (await OpenDirectAsync(m, plus.Relays[0], identity, Update, ct)).DisposeAsync(); }
                    catch (GateRejectedException e) when (e.Status is GateStatus.SplitRequired or GateStatus.CircuitOpen) { }
                    m = current;
                    if (m.Network is not { } updated || !updated.Level.IsSplit())
                        return await OpenDirectAsync(m, m.Network?.Direct ?? new GateEndpoint(m.Host, m.Port), identity, Update, ct);
                }
                return await OpenSplitAsync(m, m.Network!, identity, Update, ct);
            }
            try
            {
                return await OpenDirectAsync(m, m.Network?.Direct ?? new GateEndpoint(m.Host, m.Port), identity, Update, ct);
            }
            catch (GateRejectedException e) when (e.Status == GateStatus.SplitRequired && current.Network is { } changed && changed.Level.IsSplit())
            {
                // 服务器刚切到隐私+：网关回拒绝时已经带上了节点列表，直接改走两路。
                return await OpenSplitAsync(current, changed, identity, Update, ct);
            }
        };
    }

    private static async Task<Stream> OpenDirectAsync(GateMembership m, GateEndpoint target, MemberIdentity identity, Action<GateNetwork> update, CancellationToken ct)
    {
        TcpClient? tcp = null;
        try
        {
            var rotation = target == m.Network?.Direct ? m.Network.DirectRotation : m.Network?.RelayRotation(target);
            tcp = await ConnectEntryAsync(target, rotation, ct);
            Pump.Tune(tcp);
            return await GateProtocol.JoinAsync(new OwnedStream(tcp), m, identity.KeyPair, JoinRequest.Direct, update, ct);
        }
        catch
        {
            tcp?.Dispose();
            throw;
        }
    }

    /// <summary>随机挑两个不同的节点，各做一次完整的进服握手（同一个配对 ID，腿序号 0 / 1）。某个节点连不上就换下一个。</summary>
    internal static async Task<Stream> OpenSplitAsync(GateMembership m, GateNetwork network, MemberIdentity identity, Action<GateNetwork>? update, CancellationToken ct)
    {
        var relays = network.Relays.Distinct().ToArray();
        if (relays.Length < 2) throw new IOException(L.T("core.gate.split.need_two"));
        RandomNumberGenerator.Shuffle(relays.AsSpan());
        var queue = new Queue<GateEndpoint>(relays);
        var queueLock = new Lock();
        var pairId = RandomNumberGenerator.GetBytes(JoinRequest.PairIdLength);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task<SecureStream> LegAsync(byte index)
        {
            Exception? last = null;
            while (true)
            {
                GateEndpoint? relay;
                lock (queueLock) relay = queue.Count > 0 ? queue.Dequeue() : null;
                if (relay is null) throw new IOException(L.T("core.gate.split.need_two"), last);
                TcpClient? tcp = null;
                try
                {
                    tcp = await ConnectEntryAsync(relay, network.RelayRotation(relay), linked.Token);
                    Pump.Tune(tcp);
                    return await GateProtocol.JoinAsync(new OwnedStream(tcp), m, identity.KeyPair, JoinRequest.Leg(index, pairId), update, linked.Token);
                }
                catch (Exception e) when (e is IOException or SocketException && !linked.IsCancellationRequested)
                {
                    tcp?.Dispose();
                    last = e;
                }
                catch
                {
                    tcp?.Dispose();
                    throw;
                }
            }
        }

        var legs = new[] { LegAsync(0), LegAsync(1) };
        try
        {
            await Task.WhenAll(legs.Select(async t =>
            {
                try { await t; }
                catch { await linked.CancelAsync(); throw; }
            }));
        }
        catch
        {
            foreach (var leg in legs)
                if (leg.IsCompletedSuccessfully)
                {
                    leg.Result.Abort();
                    await leg.Result.DisposeAsync();
                }
            // 报最先出错的那个原因（另一条腿多半只是被取消了）。
            var first = legs.Where(l => l.IsFaulted).Select(l => l.Exception!.InnerException!).FirstOrDefault(e => e is not OperationCanceledException);
            if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
            throw;
        }
        return new SplitStream(legs[0].Result, legs[1].Result);
    }

    /// <summary>
    /// 连一个入口。开了端口轮换就按当前窗、上一个窗、下一个窗的顺序试（容忍两边时钟差一个窗），最后再试固定端口（过渡期里还开着）。
    /// 轮换端口连不上多半是被直接拒绝，很快；被防火墙丢包时每个端口最多等几秒。
    /// </summary>
    internal static async Task<TcpClient> ConnectEntryAsync(GateEndpoint endpoint, GateEntryRotation? rotation, CancellationToken ct)
    {
        var ports = rotation is null ? [endpoint.Port] : rotation.CandidatePorts(DateTimeOffset.UtcNow).Append(endpoint.Port).Distinct().ToList();
        Exception? last = null;
        foreach (var port in ports)
        {
            var tcp = new TcpClient();
            try
            {
                await ConnectAsync(tcp, endpoint.Host, port, ct, rotation is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(4));
                return tcp;
            }
            catch (Exception e) when (e is IOException or SocketException && !ct.IsCancellationRequested)
            {
                tcp.Dispose();
                last = e;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(last!);
        throw last!;
    }

    private static async Task ConnectAsync(TcpClient tcp, string host, int port, CancellationToken ct, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(10));
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
/// 客户端保存的 Jay 服务器（数据目录下的 jay-servers.json）。访问密钥加密保存：Windows 用 DPAPI，macOS/Linux 用 AES-GCM + 0600 本机密钥。
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
        /// <summary>隐私等级（basic / plus），网关每次进服都会回最新的。</summary>
        public string Privacy { get; set; } = "basic";
        /// <summary>网关给的直连地址（隐私+ 下没有）。</summary>
        public string? Direct { get; set; }
        /// <summary>节点列表（host:port）。</summary>
        public List<string> Relays { get; set; } = [];
        /// <summary>入口端口轮换：规则（窗口秒数,最小端口,最大端口）、直连入口的种子、每个节点的种子（Base64，和 Relays 一一对应）。没开为 null。</summary>
        public string? Rotation { get; set; }
        public string? DirectSeed { get; set; }
        public List<string>? RelaySeeds { get; set; }
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
                var model = JsonSerializer.Deserialize(File.ReadAllBytes(_path), JayServersJsonContext.Default.FileModel) ?? new FileModel();
                // 旧版非 Windows 直接存 plain:Base64；读到后立即换成加密格式。
                if (!OperatingSystem.IsWindows() && model.Servers.Any(e => e.ProtectedAccessKey.StartsWith("plain:", StringComparison.Ordinal)))
                {
                    var migrated = false;
                    foreach (var entry in model.Servers.Where(e => e.ProtectedAccessKey.StartsWith("plain:", StringComparison.Ordinal)))
                    {
                        try
                        {
                            entry.ProtectedAccessKey = Protect(Unprotect(entry.ProtectedAccessKey));
                            migrated = true;
                        }
                        catch (Exception e) when (e is CryptographicException or FormatException)
                        {
                            // 损坏的单条记录留给选中它时报错，不影响其他服务器加载和迁移。
                        }
                    }
                    if (migrated) Save(model);
                }
                return model.Servers;
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
            Apply(entry, membership.Network);
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

    /// <summary>网关回了新的连法：更新保存的隐私等级和节点列表。返回更新后的条目（找不到返回 null）。</summary>
    public Entry? UpdateNetwork(string id, GateNetwork network)
    {
        lock (_gate)
        {
            var model = new FileModel { Servers = [.. Load()] };
            var entry = model.Servers.FirstOrDefault(e => e.Id == id);
            if (entry is null) return null;
            Apply(entry, network);
            Save(model);
            return entry;
        }
    }

    private static void Apply(Entry entry, GateNetwork? network)
    {
        if (network is null) return;
        entry.Privacy = network.Level switch { PrivacyLevel.PlusPlus => "plusplus", PrivacyLevel.Plus => "plus", _ => "basic" };
        entry.Direct = network.Direct?.ToString();
        entry.Relays = [.. network.Relays.Select(r => r.ToString())];
        entry.Rotation = network.Rotation is { } r ? $"{r.Rule.WindowSeconds},{r.Rule.MinPort},{r.Rule.MaxPort}" : null;
        entry.DirectSeed = network.Rotation?.DirectSeed is { } d ? Convert.ToBase64String(d) : null;
        entry.RelaySeeds = network.Rotation is { } rr ? [.. rr.RelaySeeds.Select(Convert.ToBase64String)] : null;
    }

    private static GateNetworkRotation? LoadRotation(Entry entry)
    {
        if (entry.Rotation?.Split(',') is not [var w, var min, var max] || entry.RelaySeeds is null) return null;
        if (!int.TryParse(w, out var window) || !int.TryParse(min, out var low) || !int.TryParse(max, out var high)) return null;
        var rule = new GateRotation(window, low, high);
        if (!rule.IsValid || entry.RelaySeeds.Count != entry.Relays.Count) return null;
        try
        {
            return new GateNetworkRotation(rule, entry.DirectSeed is null ? null : Convert.FromBase64String(entry.DirectSeed), [.. entry.RelaySeeds.Select(Convert.FromBase64String)]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public GateMembership ToMembership(Entry entry)
    {
        GateNetwork? network = null;
        var level = entry.Privacy switch { "plusplus" => PrivacyLevel.PlusPlus, "plus" => PrivacyLevel.Plus, _ => PrivacyLevel.Basic };
        if (level != PrivacyLevel.Basic || entry.Relays.Count > 0 || entry.Direct is not null)
            network = new GateNetwork(level, GateEndpoint.TryParse(entry.Direct),
                [.. entry.Relays.Select(GateEndpoint.TryParse).OfType<GateEndpoint>()], LoadRotation(entry));
        return new(entry.Host, entry.Port, Convert.FromBase64String(entry.ServerPublicKey), Unprotect(entry.ProtectedAccessKey), entry.Username, network);
    }

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

    private string Protect(byte[] key)
    {
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(LocalSecretKey.LoadOrCreate(_path + ".key", "Jay server"), tag.Length))
            aes.Encrypt(nonce, key, cipher, tag, Entropy);
        return "aesgcm:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private byte[] Unprotect(string stored)
    {
        if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException(L.T("core.gate.store.otherplatform"));
            return ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.CurrentUser);
        }
        if (stored.StartsWith("aesgcm:", StringComparison.Ordinal))
        {
            var all = Convert.FromBase64String(stored[7..]);
            if (all.Length < 29) throw new CryptographicException(L.T("core.gate.store.corrupt"));
            var plain = new byte[all.Length - 28];
            using (var aes = new AesGcm(LocalSecretKey.LoadOrCreate(_path + ".key", "Jay server"), 16))
                aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain, Entropy);
            return plain;
        }
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return Convert.FromBase64String(stored[6..]);
        throw new CryptographicException(L.T("core.gate.store.corrupt"));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(JayServerList.FileModel))]
internal sealed partial class JayServersJsonContext : JsonSerializerContext;
