using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

/// <summary>房主信任的人：公钥和当时的名字。</summary>
public sealed record TrustedMember(byte[] PublicKey, string Name)
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
}

/// <summary>
/// 房主的信任列表（trusted.json）：只存公钥和名字。列表里的人用任何一张有效邀请敲门都直接批准。
/// 文件坏了当作空列表（不崩），下次保存时覆盖。
/// </summary>
public sealed class TrustedMembers(string path)
{
    private readonly Lock _gate = new();
    private List<TrustedMember>? _members;

    public event Action? Changed;

    public IReadOnlyList<TrustedMember> All
    {
        get { lock (_gate) return [.. Load()]; }
    }

    public bool IsTrusted(ReadOnlySpan<byte> publicKey)
    {
        lock (_gate)
        {
            foreach (var m in Load())
                if (CryptographicOperations.FixedTimeEquals(m.PublicKey, publicKey)) return true;
            return false;
        }
    }

    public void Add(byte[] publicKey, string name)
    {
        if (publicKey.Length != 32) throw new ArgumentException(null, nameof(publicKey));
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(m => m.PublicKey.AsSpan().SequenceEqual(publicKey));
            list.Add(new TrustedMember((byte[])publicKey.Clone(), KnockProtocol.SanitizeName(name)));
            Save(list);
        }
        Changed?.Invoke();
    }

    public void Remove(byte[] publicKey)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.RemoveAll(m => m.PublicKey.AsSpan().SequenceEqual(publicKey)) == 0) return;
            Save(list);
        }
        Changed?.Invoke();
    }

    private List<TrustedMember> Load()
    {
        if (_members is not null) return _members;
        _members = [];
        try
        {
            if (!File.Exists(path)) return _members;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array) return _members;
            foreach (var item in array)
            {
                if (item?["pub"]?.GetValue<string>() is not { Length: 64 } hex || !hex.All(char.IsAsciiHexDigit)) continue;
                _members.Add(new TrustedMember(Convert.FromHexString(hex), KnockProtocol.SanitizeName(item["name"]?.GetValue<string>())));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            _members.Clear();
        }
        return _members;
    }

    private void Save(List<TrustedMember> list)
    {
        _members = list;
        var array = new JsonArray();
        foreach (var m in list) array.Add(new JsonObject { ["pub"] = Convert.ToHexStringLower(m.PublicKey), ["name"] = m.Name });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
