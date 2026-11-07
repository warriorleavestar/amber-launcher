using System.Security.Cryptography;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 一个房间成员自己的密钥：8 字节编号 + 32 字节 psk，房主批准时随机生成，经敲门的加密通道发给这个人。
/// 只在内存里，从不显示、保存或写进日志；房间结束时清零。连房主的游戏通道和聊天时先明文报编号，再用这把 psk 握手。
/// </summary>
public sealed class MemberKey
{
    public const int IdLength = 8;
    public const int PskLength = 32;

    private readonly byte[] _psk;

    public MemberKey(ReadOnlySpan<byte> id, ReadOnlySpan<byte> psk)
    {
        if (id.Length != IdLength) throw new ArgumentException(null, nameof(id));
        if (psk.Length != PskLength) throw new ArgumentException(null, nameof(psk));
        Id = id.ToArray();
        _psk = psk.ToArray();
        IdHex = Convert.ToHexStringLower(Id);
    }

    public static MemberKey Create()
    {
        var id = RandomNumberGenerator.GetBytes(IdLength);
        var psk = RandomNumberGenerator.GetBytes(PskLength);
        try { return new MemberKey(id, psk); }
        finally { CryptographicOperations.ZeroMemory(psk); }
    }

    public byte[] Id { get; }

    /// <summary>编号的十六进制（聊天里的发送者 id）。</summary>
    public string IdHex { get; }

    /// <summary>握手用的 psk 的副本（用完由调用方清零）。</summary>
    internal byte[] CopyPsk() => (byte[])_psk.Clone();

    internal string PskBase64 => Convert.ToBase64String(_psk);

    internal void Zero() => CryptographicOperations.ZeroMemory(_psk);

    public override string ToString() => "MemberKey";
}

/// <summary>房主记着的一个成员：编号、长期公钥、名字（来自敲门）和他的 psk。</summary>
public sealed class MemberEntry
{
    private readonly CancellationTokenSource _revocation = new();

    /// <summary>这把钥匙作废了（踢出、同一个人重新批准换了新钥匙、房间结束）。</summary>
    public bool Revoked => _revocation.IsCancellationRequested;

    /// <summary>作废时取消：游戏通道和聊天里这个成员现有的连接都挂在上面，一作废马上断开。</summary>
    public CancellationToken Revocation => _revocation.Token;

    /// <summary>钥匙清零并通知所有连接断开。在成员表的锁外面调用（回调会关连接）。</summary>
    internal void Revoke()
    {
        Key.Zero();
        try { _revocation.Cancel(); }
        catch (AggregateException) { }
    }

    internal MemberEntry(MemberKey key, byte[] publicKey, string name, bool isHost, PendingInvite? invite = null)
    {
        Invite = invite;
        Key = key;
        PublicKey = (byte[])publicKey.Clone();
        Name = name;
        IsHost = isHost;
    }

    public MemberKey Key { get; }
    public byte[] PublicKey { get; }
    public string Name { get; }
    public bool IsHost { get; }
    /// <summary>这个人是用哪张邀请进来的（踢出时一起作废，免得换个身份拿同一张再进来）。</summary>
    public PendingInvite? Invite { get; }
    public string Id => Key.IdHex;
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);

    /// <summary>对方确认收到了凭据（敲门最后的 ack）。没确认的成员照样能用这把钥匙进来。</summary>
    public bool Confirmed { get; internal set; }

    public override string ToString() => "MemberEntry";
}

/// <summary>这个房间里被踢出、不许再敲门进来的人。</summary>
public sealed record BannedMember(byte[] PublicKey, string Name)
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
}

/// <summary>
/// 房主的成员表：编号 → 公钥、名字、psk。游戏通道和聊天都按编号在这里查 psk，查不到就断开。
/// 同一把公钥再次被批准（重新敲门）时换一把新钥匙，旧的作废。
/// 还记着这个房间的禁入名单（踢出的人）：房间结束就清空，不保存。
/// </summary>
public sealed class MemberTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemberEntry> _byId = new(StringComparer.Ordinal);
    private readonly List<BannedMember> _banned = [];

    public event Action? Changed;

    /// <summary>有成员被移出（踢出，或换网络时没收到新凭据）：游戏通道和聊天要马上断开他现有的连接。</summary>
    public event Action<MemberEntry>? Removed;

    /// <summary>给一个被批准的人发钥匙（房主自己也有一把，用于本机的聊天客户端）。</summary>
    public MemberEntry Issue(ReadOnlySpan<byte> publicKey, string name, bool isHost = false, PendingInvite? invite = null)
    {
        if (publicKey.Length != 32) throw new ArgumentException(null, nameof(publicKey));
        MemberEntry entry;
        var pub = publicKey.ToArray();
        List<MemberEntry> replaced;
        lock (_gate)
        {
            // 房主自己的那把不动（自检里房主和成员可能是同一个身份）。
            replaced = [.. _byId.Values.Where(e => e.IsHost == isHost && e.PublicKey.AsSpan().SequenceEqual(pub))];
            foreach (var old in replaced)
            {
                _byId.Remove(old.Id);
                old.Key.Zero();
            }
            MemberKey key;
            do key = MemberKey.Create();
            while (_byId.ContainsKey(key.IdHex));
            entry = new MemberEntry(key, pub, name, isHost, invite);
            _byId[entry.Id] = entry;
        }
        foreach (var old in replaced)
        {
            old.Revoke();
            Removed?.Invoke(old);
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>房主自己的那把（没有时 null）。</summary>
    public MemberEntry? Host
    {
        get { lock (_gate) return _byId.Values.FirstOrDefault(e => e.IsHost); }
    }

    /// <summary>按长期公钥找（不含房主自己的那把）。</summary>
    public MemberEntry? FindGuest(ReadOnlySpan<byte> publicKey)
    {
        var pub = publicKey.ToArray();
        lock (_gate) return _byId.Values.FirstOrDefault(e => !e.IsHost && e.PublicKey.AsSpan().SequenceEqual(pub));
    }

    /// <summary>移出一个成员：钥匙清零作废，触发 Removed（断开他现有的连接）。</summary>
    public bool Remove(MemberEntry entry)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(entry.Id, out var current) || current != entry) return false;
            _byId.Remove(entry.Id);
            entry.Key.Zero();
        }
        entry.Revoke();
        Removed?.Invoke(entry);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 踢出：记进禁入名单、移出成员表（钥匙作废，现有连接马上断开），并作废他进来用的那张邀请——
    /// 禁入名单按公钥记，换一个身份就能绕过，所以那张邀请也不能再用。
    /// 那张邀请本来还有剩余次数时返回 true（界面提示房主：还要邀请的人请重新发一张）。
    /// </summary>
    public bool Kick(MemberEntry entry, InviteBook? book)
    {
        Ban(entry.PublicKey, entry.Name);
        Remove(entry);
        if (entry.Invite is not { } invite || book is null) return false;
        var hadUses = invite.IsUsable(book.Now);
        book.Revoke(invite);
        return hadUses;
    }

    /// <summary>加进这个房间的禁入名单：以后这把公钥敲门一律不理。</summary>
    public void Ban(ReadOnlySpan<byte> publicKey, string name)
    {
        var pub = publicKey.ToArray();
        lock (_gate)
        {
            if (_banned.Any(b => b.PublicKey.AsSpan().SequenceEqual(pub))) return;
            _banned.Add(new BannedMember(pub, name));
        }
        Changed?.Invoke();
    }

    public void Unban(ReadOnlySpan<byte> publicKey)
    {
        var pub = publicKey.ToArray();
        lock (_gate) _banned.RemoveAll(b => b.PublicKey.AsSpan().SequenceEqual(pub));
        Changed?.Invoke();
    }

    public bool IsBanned(ReadOnlySpan<byte> publicKey)
    {
        var pub = publicKey.ToArray();
        lock (_gate) return _banned.Any(b => b.PublicKey.AsSpan().SequenceEqual(pub));
    }

    public IReadOnlyList<BannedMember> Banned
    {
        get { lock (_gate) return [.. _banned]; }
    }

    /// <summary>按连接时明文报上来的编号找成员。</summary>
    public bool TryGet(ReadOnlySpan<byte> id, out MemberEntry entry)
    {
        entry = null!;
        if (id.Length != MemberKey.IdLength) return false;
        lock (_gate) return _byId.TryGetValue(Convert.ToHexStringLower(id), out entry!);
    }

    public IReadOnlyList<MemberEntry> All
    {
        get { lock (_gate) return [.. _byId.Values]; }
    }

    internal void Confirm(MemberEntry entry)
    {
        entry.Confirmed = true;
        Changed?.Invoke();
    }

    /// <summary>房间结束：所有钥匙清零作废，禁入名单清空。</summary>
    public void Clear()
    {
        List<MemberEntry> all;
        lock (_gate)
        {
            all = [.. _byId.Values];
            foreach (var e in all) e.Key.Zero();
            _byId.Clear();
            _banned.Clear();
        }
        foreach (var e in all)
        {
            e.Revoke();
            Removed?.Invoke(e);
        }
        Changed?.Invoke();
    }
}
