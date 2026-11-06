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
    internal MemberEntry(MemberKey key, byte[] publicKey, string name, bool isHost)
    {
        Key = key;
        PublicKey = (byte[])publicKey.Clone();
        Name = name;
        IsHost = isHost;
    }

    public MemberKey Key { get; }
    public byte[] PublicKey { get; }
    public string Name { get; }
    public bool IsHost { get; }
    public string Id => Key.IdHex;
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);

    /// <summary>对方确认收到了凭据（敲门最后的 ack）。没确认的成员照样能用这把钥匙进来。</summary>
    public bool Confirmed { get; internal set; }

    public override string ToString() => "MemberEntry";
}

/// <summary>
/// 房主的成员表：编号 → 公钥、名字、psk。游戏通道和聊天都按编号在这里查 psk，查不到就断开。
/// 同一把公钥再次被批准（重新敲门）时换一把新钥匙，旧的作废。
/// </summary>
public sealed class MemberTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemberEntry> _byId = new(StringComparer.Ordinal);

    public event Action? Changed;

    /// <summary>给一个被批准的人发钥匙（房主自己也有一把，用于本机的聊天客户端）。</summary>
    public MemberEntry Issue(ReadOnlySpan<byte> publicKey, string name, bool isHost = false)
    {
        if (publicKey.Length != 32) throw new ArgumentException(null, nameof(publicKey));
        MemberEntry entry;
        var pub = publicKey.ToArray();
        lock (_gate)
        {
            foreach (var old in _byId.Values.Where(e => e.PublicKey.AsSpan().SequenceEqual(pub)).ToList())
            {
                _byId.Remove(old.Id);
                old.Key.Zero();
            }
            MemberKey key;
            do key = MemberKey.Create();
            while (_byId.ContainsKey(key.IdHex));
            entry = new MemberEntry(key, pub, name, isHost);
            _byId[entry.Id] = entry;
        }
        Changed?.Invoke();
        return entry;
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

    /// <summary>房间结束：所有钥匙清零作废。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var e in _byId.Values) e.Key.Zero();
            _byId.Clear();
        }
        Changed?.Invoke();
    }
}
