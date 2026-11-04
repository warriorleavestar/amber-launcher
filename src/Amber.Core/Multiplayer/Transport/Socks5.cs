using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// “仅游戏”转发用的最小 SOCKS5（RFC 1928）：只支持 CONNECT、不认证（只在房间的封装通道或 EasyTier 里可达）。
/// 出口成员这边：替房间里的人连外网，拒绝连本机、局域网、虚拟网段这些地址，别人借你上网时碰不到你家里的设备。
/// </summary>
public static class Socks5
{
    private const byte Version = 5;

    /// <summary>握手（问候和请求）最多等这么久：不让人先开好一堆空闲连接，等网络变了再发请求。</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>出口成员：处理一条已经接上的 SOCKS5 连接。</summary>
    /// <param name="ready">出口准备好了没有（查到了自己的公网 IP）。没有时读完请求就回“出口没准备好”，不解析也不连接。</param>
    /// <param name="ensureReady">收到请求之后、解析和连接之前调用（出口在这里确认公网 IP 还是新的）；返回 false 时同样回“出口没准备好”。</param>
    public static async Task ServeAsync(Stream client, CancellationToken ct, Action<string>? log = null, Func<IPAddress, bool>? allow = null, Func<int, bool>? allowPort = null, bool ready = true,
        Func<CancellationToken, Task<bool>>? ensureReady = null, TimeSpan? handshakeTimeout = null)
    {
        allowPort ??= ExitPolicy.AllowsPort;
        allow ??= IsPublic;
        string host;
        int port;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(handshakeTimeout ?? HandshakeTimeout);
            try
            {
                if (await ReadRequestAsync(client, deadline.Token) is not { } request) return;
                (host, port) = request;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log?.Invoke(L.T("core.mp.socks.handshake_timeout"));
                return;
            }
        }
        if (!ready || ensureReady is not null && !await ensureReady(ct))
        {
            log?.Invoke(L.T("core.mp.socks.public_ip_unconfirmed"));
            await ReplyAsync(client, 3, ct);
            return;
        }
        if (!allowPort(port))
        {
            log?.Invoke(L.T("core.mp.socks.mail_port_blocked"));
            await ReplyAsync(client, 2, ct);
            return;
        }
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            await ReplyAsync(client, 4, ct);   // 找不到主机
            return;
        }
        var targets = addresses.Where(allow).ToArray();
        if (targets.Length == 0)
        {
            // 只有 IPv6 地址（里面也没嵌 IPv4）：单独回“地址类型不支持”，加入方能看到“出口只转 IPv4”。
            if (addresses.Length > 0 && addresses.All(a => ExitPolicy.Normalize(a).AddressFamily == AddressFamily.InterNetworkV6))
            {
                log?.Invoke(L.T("core.mp.socks.ipv6_target_blocked"));
                await ReplyAsync(client, 8, ct);
                return;
            }
            log?.Invoke(L.T("core.mp.socks.private_target_blocked"));
            await ReplyAsync(client, 2, ct);   // 规则不允许
            return;
        }
        using var remote = new TcpClient(targets[0].AddressFamily);
        try
        {
            await remote.ConnectAsync(targets, port, ct);
        }
        catch (SocketException)
        {
            await ReplyAsync(client, 5, ct);   // 连接被拒绝
            return;
        }
        Pump.Tune(remote);
        await ReplyAsync(client, 0, ct);
        // 只记有连接经过，不记别人连去了哪里（导出诊断报告时也不会带出成员的访问记录）。
        log?.Invoke(L.T("core.mp.socks.connection_forwarded"));
        await Pump.RunAsync(client, remote.GetStream(), ct, () => Pump.Abort(remote));
    }

    /// <summary>读问候和 CONNECT 请求；对方断开或请求不对时返回 null（需要回复的已经回过了）。</summary>
    private static async Task<(string Host, int Port)?> ReadRequestAsync(Stream client, CancellationToken ct)
    {
        var head = new byte[2];
        if (!await SecureStream.ReadExactAsync(client, head, ct) || head[0] != Version) return null;
        var methods = new byte[head[1]];
        if (!await SecureStream.ReadExactAsync(client, methods, ct)) return null;
        if (!methods.Contains((byte)0))
        {
            await client.WriteAsync(new byte[] { Version, 0xFF }, ct);
            return null;
        }
        await client.WriteAsync(new byte[] { Version, 0 }, ct);
        await client.FlushAsync(ct);

        var request = new byte[4];
        if (!await SecureStream.ReadExactAsync(client, request, ct)) return null;
        if (request[0] != Version || request[1] != 1)
        {
            await ReplyAsync(client, 7, ct);   // 只支持 CONNECT
            return null;
        }
        return await ReadAddressAsync(client, request[3], ct);
    }

    /// <summary>加入方：在流上发 CONNECT host:port，成功后这个流就是到目标的连接。</summary>
    public static async Task ConnectAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { Version, 1, 0 }, ct);
        await stream.FlushAsync(ct);
        var choice = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, choice, ct) || choice[1] != 0) throw new IOException(L.T("core.mp.socks.request_rejected"));
        var request = new List<byte> { Version, 1, 0 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255) throw new ArgumentException(L.T("core.mp.socks.hostname_too_long"));
            request.Add(3);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }
        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        await stream.WriteAsync(request.ToArray(), ct);
        await stream.FlushAsync(ct);
        var reply = new byte[4];
        if (!await SecureStream.ReadExactAsync(stream, reply, ct)) throw new IOException(L.T("core.mp.socks.no_reply"));
        await ReadAddressAsync(stream, reply[3], ct);
        if (reply[1] != 0) throw new IOException(reply[1] switch
        {
            2 => L.F("core.mp.socks.destination_rejected", host, port),
            4 => L.F("core.mp.socks.host_not_found", host),
            8 => L.F("core.mp.socks.ipv6_only", host),
            3 => L.T("core.mp.socks.exit_not_ready"),
            5 => L.F("core.mp.socks.connection_refused", host, port),
            _ => L.F("core.mp.socks.connection_failed", host, port, reply[1]),
        });
    }

    private static async Task ReplyAsync(Stream stream, byte code, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { Version, code, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<(string Host, int Port)> ReadAddressAsync(Stream stream, byte type, CancellationToken ct)
    {
        byte[] address;
        string host;
        switch (type)
        {
            case 1:
                address = new byte[4];
                if (!await SecureStream.ReadExactAsync(stream, address, ct)) throw new EndOfStreamException();
                host = new IPAddress(address).ToString();
                break;
            case 4:
                address = new byte[16];
                if (!await SecureStream.ReadExactAsync(stream, address, ct)) throw new EndOfStreamException();
                host = new IPAddress(address).ToString();
                break;
            case 3:
                var length = new byte[1];
                if (!await SecureStream.ReadExactAsync(stream, length, ct)) throw new EndOfStreamException();
                address = new byte[length[0]];
                if (!await SecureStream.ReadExactAsync(stream, address, ct)) throw new EndOfStreamException();
                host = Encoding.ASCII.GetString(address);
                break;
            default:
                throw new InvalidDataException(L.T("core.mp.socks.unknown_address_type"));
        }
        var port = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, port, ct)) throw new EndOfStreamException();
        return (host, BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    /// <summary>按地址本身判断能不能经出口连（保留和私有网段、嵌着私有 IPv4 的 IPv6 写法都不行）。出口实际用 <see cref="ExitPolicy"/>，还会排除本机自己的地址和网段。</summary>
    public static bool IsPublic(IPAddress address) => ExitPolicy.IsGloballyRoutable(address);

}

/// <summary>
/// 经出口进服务器时，游戏连的是本机转发端口，握手包里写的服务器地址就成了 127.0.0.1。
/// 有些服务器按这个地址区分子服或者拒绝不认识的地址，所以把第一个包（明文的 Handshake）里的地址和端口改回真实的。
/// </summary>
public static class McHandshakeRewriter
{
    public static async Task<byte[]> RewriteFirstPacketAsync(Stream game, string realHost, int realPort, CancellationToken ct)
    {
        var packet = await McDisguise.ReadPacketAsync(game, ct);
        var reader = new McDisguise.PacketReader(packet);
        if (reader.VarInt() != 0x00) throw new InvalidDataException(L.T("core.mp.socks.first_packet_not_mc_handshake"));
        var protocol = reader.VarInt();
        var address = reader.String(255);
        reader.UShort();
        var next = reader.VarInt();
        // Forge 客户端会在地址后面接 "\0FML3\0" 这类标记，服务端靠它认出 Forge，要原样保留。
        var marker = address.IndexOf('\0') is var i and >= 0 ? address[i..] : "";
        return new McDisguise.PacketWriter().VarInt(0x00).VarInt(protocol).String(realHost + marker).UShort((ushort)realPort).VarInt(next).ToPacket();
    }

    /// <summary>给 TunnelClient 用：先做 SOCKS5 CONNECT，再把改写过的握手包发过去，之后原样转发。</summary>
    /// <param name="handshakeHost">握手包里写的地址（用户写的名字，经 SRV 查到别的主机时也写原来的名字）；默认和 host 一样。</param>
    public static Func<Stream, Stream, CancellationToken, Task<Stream>> ViaSocks(string host, int port, string? handshakeHost = null, int? handshakePort = null) => async (game, remote, ct) =>
    {
        await Socks5.ConnectAsync(remote, host, port, ct);
        var first = await RewriteFirstPacketAsync(game, handshakeHost ?? host, handshakePort ?? port, ct);
        await remote.WriteAsync(first, ct);
        await remote.FlushAsync(ct);
        return game;
    };
}
