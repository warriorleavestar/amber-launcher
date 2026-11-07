using Amber.Core.Localization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>网关的名单和未用完的邀请。只存设备公钥和用户名，不存 IP、MAC 之类的东西。</summary>
public sealed class GateState
{
    public int Version { get; set; } = 1;
    /// <summary>进服握手的 psk（Base64，32 字节）。只发给入服成功的设备。</summary>
    public string AccessKey { get; set; } = "";
    public int MaxDevicesPerMember { get; set; } = 2;
    public List<GateMember> Members { get; set; } = [];
    public List<GatePendingInvite> Invites { get; set; } = [];
    /// <summary>隐私等级。放在 state.json 里，运行中的网关改了马上生效。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PrivacyLevel>))]
    public PrivacyLevel Privacy { get; set; } = PrivacyLevel.Basic;
    /// <summary>节点（中继）。隐私+ 下客户端经其中两个连进来，网关只认这些节点的来源 IP。</summary>
    public List<GateRelayEntry> Relays { get; set; } = [];
    /// <summary>隐私++：至少要有几个节点在发心跳才不熔断。</summary>
    public int MinHealthyRelays { get; set; } = 2;
    /// <summary>隐私++：多久收不到心跳算节点不可用（秒）。</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 15;
    /// <summary>纪元号：吊销节点时加一，运行中的网关看到它变了就断开全部连接（大家重新握手，换新密钥）。</summary>
    public int Epoch { get; set; }
    /// <summary>入口端口轮换（TOTP）。null 表示没开过。</summary>
    public GateRotationState? Rotation { get; set; }

    public GateMember? Member(string name) => Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class GateMember
{
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
    public List<GateDevice> Devices { get; set; } = [];
}

public sealed class GateDevice
{
    /// <summary>设备公钥（Base64）。</summary>
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset Added { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
}

public sealed class GateRelayEntry
{
    /// <summary>玩家连的地址（host:port），会发给客户端。</summary>
    public string Address { get; set; } = "";
    /// <summary>网关看到的这个节点的来源 IP（节点在局域网、或者和网关在同一台机器时要填）；空的就把 Address 的主机名解析成 IP。</summary>
    public string? Source { get; set; }
    /// <summary>心跳通道的 psk（Base64，从节点码派生）。没发过节点码的节点没有心跳，隐私++ 下算不健康。</summary>
    public string? HeartbeatPsk { get; set; }
    /// <summary>节点第一次连心跳通道时记下的公钥（Base64）。之后只认这一把。</summary>
    public string? PublicKey { get; set; }
}

public sealed class GateRotationState
{
    public bool Enabled { get; set; }
    public int WindowSeconds { get; set; } = GateRotation.Default.WindowSeconds;
    public int MinPort { get; set; } = GateRotation.Default.MinPort;
    public int MaxPort { get; set; } = GateRotation.Default.MaxPort;
    /// <summary>主种子（Base64，32 字节）。各个入口的种子从它派生。换了它，所有人保存的轮换信息都失效。</summary>
    public string Seed { get; set; } = "";
    /// <summary>刚开轮换时固定端口再开放到这个时间：这段时间里进过服的老玩家会拿到轮换信息，之后固定端口关掉。</summary>
    public DateTimeOffset? FixedUntil { get; set; }
}

/// <summary>运行中的网关每 5 秒写一次的状态（health.json），给 jay-server status 看。</summary>
public sealed class GateHealth
{
    public DateTimeOffset Updated { get; set; }
    public bool BreakerOpen { get; set; }
    public int Epoch { get; set; }
    public int Online { get; set; }
    public List<Relay> Relays { get; set; } = [];

    public sealed class Relay
    {
        public string Address { get; set; } = "";
        public DateTimeOffset? LastHeartbeat { get; set; }
        public bool Connected { get; set; }
    }
}

public sealed class GatePendingInvite
{
    public string Id { get; set; } = "";
    /// <summary>从令牌派生的 psk（Base64）。令牌本身不落盘。</summary>
    public string Psk { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expires { get; set; }
    public int UsesLeft { get; set; }
}

/// <summary>网关的设置（gate.json）。</summary>
public sealed class GateConfig
{
    /// <summary>网关监听的地址和端口。</summary>
    public string ListenAddress { get; set; } = "::";
    public int ListenPort { get; set; } = 47670;
    /// <summary>MC 服务端（只监听本机）。</summary>
    public string BackendHost { get; set; } = "127.0.0.1";
    public int BackendPort { get; set; } = 25565;
    /// <summary>写进邀请码的公网地址和端口（玩家连的地址）。</summary>
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = 47670;
    public int MaxConnections { get; set; } = 512;
    /// <summary>同一个 IP 同时在握手中的连接数上限。</summary>
    public int MaxHandshakesPerIp { get; set; } = 8;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GateState))]
[JsonSerializable(typeof(GateConfig))]
[JsonSerializable(typeof(GateHealth))]
internal sealed partial class GateJsonContext : JsonSerializerContext;

public enum RedeemOutcome { Ok, InviteGone, DeviceLimit, Disabled }

/// <summary>
/// 网关数据目录（默认 /var/lib/jay-gate）：server.key（网关长期私钥，0600）、state.json（名单和邀请）、gate.json（设置）。
/// 守护进程和管理命令会同时改 state.json：每次修改都先拿 state.lock 的独占锁，重新读一遍，改完原子替换（写临时文件再改名），不会互相覆盖。
/// </summary>
public sealed class GateStore
{
    public const string KeyFile = "server.key";
    public const string StateFile = "state.json";
    public const string ConfigFile = "gate.json";
    private const string LockFile = "state.lock";
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromHours(1);

    private readonly string _dir;
    private readonly Lock _gate = new();
    private (DateTime Stamp, long Length, GateState State)? _cache;

    public GateStore(string dir) => _dir = dir;

    public string Directory => _dir;
    private string StatePath => Path.Combine(_dir, StateFile);

    public static bool IsInitialized(string dir) => File.Exists(Path.Combine(dir, KeyFile)) && File.Exists(Path.Combine(dir, StateFile));

    /// <summary>第一次部署：生成网关长期密钥和访问密钥。已经有了就不动（不会把已有成员踢掉）。</summary>
    public static GateStore Initialize(string dir, string publicHost, int publicPort, int listenPort)
    {
        if (!GateInvite.IsValidHost(publicHost)) throw new ArgumentException(L.T("core.gate.admin.badhost"));
        System.IO.Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var store = new GateStore(dir);
        var keyPath = Path.Combine(dir, KeyFile);
        if (!File.Exists(keyPath))
        {
            using var key = X25519KeyPair.Generate();
            var raw = key.ExportPrivateKey();
            try { WritePrivate(keyPath, raw, FileMode.CreateNew); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        var configPath = Path.Combine(dir, ConfigFile);
        var config = File.Exists(configPath) ? store.LoadConfig() : new GateConfig();
        config.PublicHost = publicHost;
        config.PublicPort = publicPort;
        config.ListenPort = listenPort;
        store.SaveConfig(config);
        if (!File.Exists(store.StatePath))
            store.Mutate(s =>
            {
                s.AccessKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                return 0;
            });
        return store;
    }

    public X25519KeyPair LoadServerKey()
    {
        var raw = File.ReadAllBytes(Path.Combine(_dir, KeyFile));
        try { return X25519KeyPair.FromPrivateKey(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    public GateConfig LoadConfig() =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_dir, ConfigFile)), GateJsonContext.Default.GateConfig) ?? new GateConfig();

    public void SaveConfig(GateConfig config) =>
        WriteAtomic(Path.Combine(_dir, ConfigFile), JsonSerializer.SerializeToUtf8Bytes(config, GateJsonContext.Default.GateConfig));

    /// <summary>读当前状态（文件改过才重新读）。返回的对象别改：要改用 <see cref="Mutate{T}"/>。</summary>
    public GateState Read()
    {
        lock (_gate)
        {
            var info = new FileInfo(StatePath);
            if (!info.Exists) return new GateState();
            if (_cache is { } c && c.Stamp == info.LastWriteTimeUtc && c.Length == info.Length) return c.State;
            var state = Load();
            _cache = (info.LastWriteTimeUtc, info.Length, state);
            return state;
        }
    }

    /// <summary>加锁、重新读、修改、原子写回。</summary>
    public T Mutate<T>(Func<GateState, T> change)
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            var state = File.Exists(StatePath) ? Load() : new GateState();
            var result = change(state);
            var now = DateTimeOffset.UtcNow;
            state.Invites.RemoveAll(i => i.Expires <= now || i.UsesLeft <= 0);
            WriteAtomic(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, GateJsonContext.Default.GateState));
            _cache = null;
            return result;
        }
    }

    public byte[] AccessKey() => Convert.FromBase64String(Read().AccessKey);

    /// <summary>还能用的邀请（id, psk）。</summary>
    public IReadOnlyList<(string Id, byte[] Psk)> ActiveInvites()
    {
        var now = DateTimeOffset.UtcNow;
        return Read().Invites.Where(i => i.Expires > now && i.UsesLeft > 0).Select(i => (i.Id, Convert.FromBase64String(i.Psk))).ToList();
    }

    /// <summary>生成一份邀请。令牌只出现在返回的邀请码里，落盘的是派生出的 psk。</summary>
    public GateInvite CreateInvite(string name, TimeSpan ttl, int uses, byte[] serverPublicKey)
    {
        if (!GateInvite.IsValidUsername(name)) throw new ArgumentException(L.T("core.gate.admin.badname"));
        if (uses < 1 || uses > 100) throw new ArgumentException(L.T("core.gate.admin.baduses"));
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(30)) throw new ArgumentException(L.T("core.gate.admin.badttl"));
        var config = LoadConfig();
        var keyHash = GateProtocol.ServerKeyHash(serverPublicKey);
        var token = RandomNumberGenerator.GetBytes(GateInvite.TokenLength);
        var psk = GateProtocol.EnrollPsk(token, keyHash);
        var now = DateTimeOffset.UtcNow;
        Mutate(s =>
        {
            s.Invites.Add(new GatePendingInvite
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
                Psk = Convert.ToBase64String(psk),
                Name = name,
                Created = now,
                Expires = now + ttl,
                UsesLeft = uses,
            });
            return 0;
        });
        CryptographicOperations.ZeroMemory(psk);
        // 隐私+：邀请码里写随机一个节点，不写源站地址。
        var state = Read();
        if (state.Privacy.IsSplit() && state.Relays.Count > 0
            && GateEndpoint.TryParse(state.Relays[RandomNumberGenerator.GetInt32(state.Relays.Count)].Address) is { } relay)
            return new GateInvite(relay.Host, relay.Port, keyHash, token, name, EntryRotation(relay.ToString(), state));
        return new GateInvite(config.PublicHost, config.PublicPort, keyHash, token, name, EntryRotation(GateRotation.DirectEntry, state));
    }

    /// <summary>用邀请登记设备。设备已经在这个名下：照样算成功（重装后重新申请也走这条）。</summary>
    public (RedeemOutcome Outcome, string? Name) Redeem(string inviteId, byte[] devicePublicKey) => Mutate(s =>
    {
        var now = DateTimeOffset.UtcNow;
        var invite = s.Invites.FirstOrDefault(i => i.Id == inviteId && i.Expires > now && i.UsesLeft > 0);
        if (invite is null) return (RedeemOutcome.InviteGone, (string?)null);
        var member = s.Member(invite.Name);
        if (member is { Disabled: true }) return (RedeemOutcome.Disabled, null);
        if (member is null)
        {
            member = new GateMember { Name = invite.Name };
            s.Members.Add(member);
        }
        var key = Convert.ToBase64String(devicePublicKey);
        if (!member.Devices.Any(d => d.PublicKey == key))
        {
            if (member.Devices.Count >= s.MaxDevicesPerMember) return (RedeemOutcome.DeviceLimit, null);
            member.Devices.Add(new GateDevice { PublicKey = key, Fingerprint = MemberIdentity.FingerprintOf(devicePublicKey), Added = now });
        }
        invite.UsesLeft--;
        return (RedeemOutcome.Ok, (string?)member.Name);
    });

    /// <summary>这台设备可以用哪些用户名登录（没停用的成员里绑定了它的）。</summary>
    public IReadOnlyList<string> AllowedNames(byte[] devicePublicKey)
    {
        var key = Convert.ToBase64String(devicePublicKey);
        return Read().Members.Where(m => !m.Disabled && m.Devices.Any(d => d.PublicKey == key)).Select(m => m.Name).ToList();
    }

    /// <summary>记下最后在线时间（一小时精度，免得每次连接都写盘）。</summary>
    public void Touch(string name, byte[] devicePublicKey)
    {
        var key = Convert.ToBase64String(devicePublicKey);
        var now = DateTimeOffset.UtcNow;
        var device = Read().Member(name)?.Devices.FirstOrDefault(d => d.PublicKey == key);
        if (device is null || device.LastSeen is { } seen && now - seen < LastSeenResolution) return;
        Mutate(s =>
        {
            var d = s.Member(name)?.Devices.FirstOrDefault(x => x.PublicKey == key);
            if (d is not null) d.LastSeen = now;
            return 0;
        });
    }

    /// <summary>撤销设备：给了指纹前缀只撤那一台，没给就撤这个用户名下的全部。返回撤了几台。</summary>
    public int RevokeDevices(string name, string? fingerprintPrefix) => Mutate(s =>
    {
        var member = s.Member(name);
        if (member is null) return 0;
        var prefix = fingerprintPrefix?.Replace("-", "").ToUpperInvariant();
        return member.Devices.RemoveAll(d => prefix is null || d.Fingerprint.Replace("-", "").StartsWith(prefix, StringComparison.Ordinal));
    });

    public bool SetDisabled(string name, bool disabled) => Mutate(s =>
    {
        var member = s.Member(name);
        if (member is null) return false;
        member.Disabled = disabled;
        return true;
    });

    public bool RemoveMember(string name) => Mutate(s => s.Members.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) > 0);

    public bool CancelInvite(string id) => Mutate(s => s.Invites.RemoveAll(i => i.Id == id) > 0);

    /// <summary>告诉客户端的连法。隐私+ 下不带直连地址（源站地址不外传）。</summary>
    /// <summary>开着的轮换：规则和主种子；没开返回 null。</summary>
    public (GateRotation Rule, byte[] Master)? Rotation(GateState? state = null)
    {
        var r = (state ?? Read()).Rotation;
        if (r is not { Enabled: true } || string.IsNullOrEmpty(r.Seed)) return null;
        var rule = new GateRotation(r.WindowSeconds, r.MinPort, r.MaxPort);
        return rule.IsValid ? (rule, Convert.FromBase64String(r.Seed)) : null;
    }

    /// <summary>固定端口现在还要不要开（没开轮换，或者还在过渡期里）。</summary>
    public static bool FixedPortWanted(GateState state) =>
        state.Rotation is not { Enabled: true } || state.Rotation.FixedUntil is { } until && DateTimeOffset.UtcNow < until;

    /// <summary>开轮换。主种子只在第一次开时生成（或者明确要求换新）。grace 内固定端口照开，让老玩家进一次服拿到轮换信息。</summary>
    public void EnableRotation(GateRotation rule, TimeSpan grace, bool newSeed = false)
    {
        if (!rule.IsValid) throw new ArgumentException(L.T("core.gate.admin.bad_rotation"));
        Mutate(s =>
        {
            var wasOn = s.Rotation is { Enabled: true };
            s.Rotation ??= new GateRotationState();
            if (newSeed || string.IsNullOrEmpty(s.Rotation.Seed)) s.Rotation.Seed = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GateRotation.SeedLength));
            s.Rotation.Enabled = true;
            s.Rotation.WindowSeconds = rule.WindowSeconds;
            s.Rotation.MinPort = rule.MinPort;
            s.Rotation.MaxPort = rule.MaxPort;
            if (!wasOn || newSeed) s.Rotation.FixedUntil = grace > TimeSpan.Zero ? DateTimeOffset.UtcNow + grace : null;
            return 0;
        });
    }

    public void DisableRotation() => Mutate(s =>
    {
        if (s.Rotation is not null) s.Rotation.Enabled = false;
        return 0;
    });

    /// <summary>某个入口（"direct" 或节点地址）现在的轮换信息。</summary>
    public GateEntryRotation? EntryRotation(string entry, GateState? state = null) =>
        Rotation(state) is { } r ? new GateEntryRotation(r.Rule, GateRotation.EntrySeed(r.Master, entry)) : null;

    public GateNetwork Network(GateConfig config)
    {
        var state = Read();
        // 普通模式不发节点地址（玩家用不上，少暴露一点）。隐私++ 只发有节点码（会发心跳）的节点。
        // 不按当前是否健康过滤：客户端要留着完整列表，节点恢复后才连得回来。
        var relays = !state.Privacy.IsSplit() ? [] : state.Relays.Where(r => state.Privacy != PrivacyLevel.PlusPlus || r.HeartbeatPsk is not null).Select(r => GateEndpoint.TryParse(r.Address)).OfType<GateEndpoint>().ToList();
        var direct = state.Privacy.IsSplit() || !GateInvite.IsValidHost(config.PublicHost) ? null : new GateEndpoint(config.PublicHost, config.PublicPort);
        GateNetworkRotation? rotation = null;
        if (Rotation(state) is { } r)
            rotation = new GateNetworkRotation(r.Rule, direct is null ? null : GateRotation.EntrySeed(r.Master, GateRotation.DirectEntry),
                [.. relays.Select(e => GateRotation.EntrySeed(r.Master, e.ToString()))]);
        return new GateNetwork(state.Privacy, direct, relays, rotation);
    }

    /// <summary>切换隐私等级。隐私+ 至少要两个节点。</summary>
    public void SetPrivacy(PrivacyLevel level) => Mutate(s =>
    {
        if (level.IsSplit() && s.Relays.Count < 2) throw new ArgumentException(L.T("core.gate.admin.plus_needs_relays"));
        if (level == PrivacyLevel.PlusPlus && s.Relays.Count(r => r.HeartbeatPsk is not null) < s.MinHealthyRelays)
            throw new ArgumentException(L.F("core.gate.admin.plusplus_needs_codes", s.MinHealthyRelays));
        s.Privacy = level;
        return 0;
    });

    /// <summary>加节点（同一地址已有就更新来源 IP）。</summary>
    public void AddRelay(GateEndpoint address, string? source)
    {
        if (source is not null && !System.Net.IPAddress.TryParse(source, out _)) throw new ArgumentException(L.T("core.gate.admin.badsource"));
        Mutate(s =>
        {
            var text = address.ToString();
            var entry = s.Relays.FirstOrDefault(r => string.Equals(r.Address, text, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                if (s.Relays.Count >= GateNetwork.MaxRelays) throw new ArgumentException(L.T("core.gate.admin.too_many_relays"));
                s.Relays.Add(entry = new GateRelayEntry { Address = text });
            }
            entry.Source = source;
            return 0;
        });
    }

    /// <summary>删节点。隐私+ 下删完不到两个就不让删。</summary>
    public bool RemoveRelay(string address) => Mutate(s =>
    {
        var parsed = GateEndpoint.TryParse(address)?.ToString() ?? address.Trim();
        var index = s.Relays.FindIndex(r => string.Equals(r.Address, parsed, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (s.Privacy.IsSplit() && s.Relays.Count <= 2) throw new ArgumentException(L.T("core.gate.admin.plus_needs_relays"));
        s.Relays.RemoveAt(index);
        return true;
    });

    /// <summary>
    /// 吊销节点（发现它不可信时用）：从列表删掉、心跳 psk 一起作废，纪元号加一。运行中的网关会马上断开全部连接，大家重新握手换新密钥，
    /// 之后客户端拿到的节点列表里不再有它。和 <see cref="RemoveRelay"/> 不同，剩下不到两个节点也照删（隐私+/++ 下会暂停，直到补上节点）。
    /// </summary>
    public bool RevokeRelay(string address) => Mutate(s =>
    {
        var parsed = GateEndpoint.TryParse(address)?.ToString() ?? address.Trim();
        if (s.Relays.RemoveAll(r => string.Equals(r.Address, parsed, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        s.Epoch++;
        return true;
    });

    /// <summary>给节点发（或重发）节点码：生成新的节点密钥，旧码和记下的节点公钥一起作废。</summary>
    public GateRelayCode IssueRelayCode(string address, byte[] serverPublicKey)
    {
        var parsed = GateEndpoint.TryParse(address)?.ToString() ?? address.Trim();
        var keyHash = GateProtocol.ServerKeyHash(serverPublicKey);
        var secret = RandomNumberGenerator.GetBytes(GateRelayCode.SecretLength);
        var psk = GateProtocol.RelayPsk(secret, keyHash);
        var found = Mutate(s =>
        {
            var entry = s.Relays.FirstOrDefault(r => string.Equals(r.Address, parsed, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            entry.HeartbeatPsk = Convert.ToBase64String(psk);
            entry.PublicKey = null;
            return true;
        });
        CryptographicOperations.ZeroMemory(psk);
        if (!found) throw new ArgumentException(L.T("core.gate.admin.no_such_relay"));
        return new GateRelayCode(keyHash, secret);
    }

    /// <summary>心跳通道能认的节点（地址, psk）。</summary>
    public IReadOnlyList<(string Id, byte[] Psk)> RelayPsks() =>
        Read().Relays.Where(r => r.HeartbeatPsk is not null).Select(r => (r.Address, Convert.FromBase64String(r.HeartbeatPsk!))).ToList();

    /// <summary>核对（第一次就记下）节点公钥。对不上返回 false。</summary>
    public bool CheckRelayKey(string address, byte[] publicKey)
    {
        var key = Convert.ToBase64String(publicKey);
        var entry = Read().Relays.FirstOrDefault(r => r.Address == address);
        if (entry is null) return false;
        if (entry.PublicKey is not null) return entry.PublicKey == key;
        return Mutate(s =>
        {
            var e = s.Relays.FirstOrDefault(r => r.Address == address);
            if (e is null) return false;
            e.PublicKey ??= key;
            return e.PublicKey == key;
        });
    }

    public void WriteHealth(GateHealth health) =>
        WriteAtomic(Path.Combine(_dir, GateServer.HealthFile), JsonSerializer.SerializeToUtf8Bytes(health, GateJsonContext.Default.GateHealth));

    public GateHealth? ReadHealth()
    {
        var path = Path.Combine(_dir, GateServer.HealthFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(path), GateJsonContext.Default.GateHealth); }
        catch (JsonException) { return null; }
    }

    private GateState Load() =>
        JsonSerializer.Deserialize(File.ReadAllBytes(StatePath), GateJsonContext.Default.GateState) ?? new GateState();

    private FileStream AcquireFileLock()
    {
        var path = Path.Combine(_dir, LockFile);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(path, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var temp = path + ".tmp";
        WritePrivate(temp, data, FileMode.Create);
        File.Move(temp, path, true);
    }

    private static void WritePrivate(string path, byte[] data, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
