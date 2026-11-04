using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Amber.Core.Multiplayer;

/// <summary>网络类型：好打洞（锥形 NAT 或者有公网地址）、难打洞（对称 NAT，每个目标换一个外部端口）、查不出来。</summary>
public enum NatKind { Unknown, Open, Cone, Symmetric }

public sealed record StunResult(IPEndPoint? Mapped, NatKind Kind, string Detail);

/// <summary>
/// 最小的 STUN 客户端（RFC 5389 Binding）：问公共 STUN 服务器“我在外面看起来是哪个地址和端口”。
/// STUN 服务器只回答这个问题，不转发任何游戏数据。用同一个套接字问两台服务器，外部端口不同就是对称 NAT。
/// </summary>
public static class Stun
{
    /// <summary>默认 STUN 服务器：国内能用的两个加一个国外的。用户可以在联机页改，也可以清空。</summary>
    public static readonly string[] DefaultServers = ["stun.miwifi.com:3478", "stun.chat.bilibili.com:3478", "stun.cloudflare.com:3478"];

    private const uint MagicCookie = 0x2112A442;

    internal static byte[] BuildRequest(byte[] transactionId)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        transactionId.CopyTo(packet, 8);
        return packet;
    }

    /// <summary>解析 Binding 成功响应里的映射地址（优先 XOR-MAPPED-ADDRESS）。不是对应请求的响应返回 null。</summary>
    internal static IPEndPoint? ParseResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transactionId)
    {
        if (data.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(data) != 0x0101) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie || !data.Slice(8, 12).SequenceEqual(transactionId)) return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var attrs = data.Slice(20, Math.Min(length, data.Length - 20));
        IPEndPoint? plain = null;
        while (attrs.Length >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(attrs);
            var len = BinaryPrimitives.ReadUInt16BigEndian(attrs[2..]);
            if (attrs.Length < 4 + len) break;
            var value = attrs.Slice(4, len);
            if (type is 0x0020 or 0x8020 && value.Length >= 8)
            {
                var port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (MagicCookie >> 16));
                if (value[1] == 0x01)
                {
                    var raw = BinaryPrimitives.ReadUInt32BigEndian(value[4..]) ^ MagicCookie;
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, raw);
                    return new IPEndPoint(new IPAddress(bytes), port);
                }
                if (value[1] == 0x02 && value.Length >= 20)
                {
                    var bytes = value.Slice(4, 16).ToArray();
                    var key = new byte[16];
                    BinaryPrimitives.WriteUInt32BigEndian(key, MagicCookie);
                    transactionId.CopyTo(key.AsSpan(4));
                    for (var i = 0; i < 16; i++) bytes[i] ^= key[i];
                    return new IPEndPoint(new IPAddress(bytes), port);
                }
            }
            else if (type == 0x0001 && value.Length >= 8 && value[1] == 0x01)
            {
                plain = new IPEndPoint(new IPAddress(value.Slice(4, 4).ToArray()), BinaryPrimitives.ReadUInt16BigEndian(value[2..]));
            }
            attrs = attrs[(4 + ((len + 3) & ~3))..];
        }
        return plain;
    }

    /// <summary>用给定的套接字问一台服务器。超时或失败返回 null。</summary>
    public static async Task<IPEndPoint?> QueryAsync(Socket socket, string server, TimeSpan timeout, CancellationToken ct = default)
    {
        IPEndPoint target;
        try
        {
            target = await ResolveAsync(server, ct);
        }
        catch (Exception e) when (e is SocketException or FormatException or ArgumentException)
        {
            return null;
        }
        var id = RandomNumberGenerator.GetBytes(12);
        var request = BuildRequest(id);
        var buffer = new byte[1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await socket.SendToAsync(request, SocketFlags.None, MapTo(socket, target), cts.Token);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                wait.CancelAfter(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds / 3));
                try
                {
                    while (true)
                    {
                        var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), wait.Token);
                        if (ParseResponse(buffer.AsSpan(0, r.ReceivedBytes), id) is { } mapped) return Normalize(mapped);
                    }
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    // 这一轮没回，重发。
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException)
        {
            if (ct.IsCancellationRequested) throw;
        }
        return null;
    }

    /// <summary>问多台服务器，判断 NAT 类型。外部端口都一样是锥形（或者有公网地址），不一样是对称。</summary>
    public static async Task<StunResult> DetectAsync(Socket socket, IReadOnlyList<string> servers, CancellationToken ct = default)
    {
        var answers = new List<(string Server, IPEndPoint Mapped)>();
        foreach (var server in servers.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (await QueryAsync(socket, server.Trim(), TimeSpan.FromSeconds(2.5), ct) is { } mapped) answers.Add((server, mapped));
            if (answers.Count >= 2) break;
        }
        if (answers.Count == 0) return new StunResult(null, NatKind.Unknown, servers.Count == 0 ? "没有设置 STUN 服务器" : "STUN 服务器都没有回应（可能 UDP 被拦）");
        var first = answers[0].Mapped;
        var local = socket.LocalEndPoint as IPEndPoint;
        if (local is not null && LocalAddresses().Contains(first.Address))
            return new StunResult(first, NatKind.Open, "本机就有公网地址");
        if (answers.Count < 2) return new StunResult(first, NatKind.Unknown, "只有一台 STUN 服务器回应，判断不了 NAT 类型");
        return answers.Select(a => a.Mapped.Port).Distinct().Count() == 1
            ? new StunResult(first, NatKind.Cone, "锥形 NAT，比较好打洞")
            : new StunResult(first, NatKind.Symmetric, "对称 NAT（严格），不容易打洞");
    }

    internal static async Task<IPEndPoint> ResolveAsync(string server, CancellationToken ct)
    {
        var colon = server.LastIndexOf(':');
        var host = colon > 0 ? server[..colon] : server;
        var port = colon > 0 ? int.Parse(server[(colon + 1)..]) : 3478;
        var addresses = await Dns.GetHostAddressesAsync(host.Trim('[', ']'), ct);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(v4, port);
    }

    /// <summary>双栈套接字要把 IPv4 地址写成映射形式。</summary>
    internal static IPEndPoint MapTo(Socket socket, IPEndPoint target) =>
        socket.AddressFamily == AddressFamily.InterNetworkV6 && target.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(target.Address.MapToIPv6(), target.Port)
            : target;

    internal static IPEndPoint Normalize(IPEndPoint endPoint) =>
        endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;

    internal static HashSet<IPAddress> LocalAddresses()
    {
        var set = new HashSet<IPAddress>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var u in nic.GetIPProperties().UnicastAddresses) set.Add(u.Address);
        return set;
    }
}
