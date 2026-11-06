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
        return new GateInvite(config.PublicHost, config.PublicPort, keyHash, token, name);
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
