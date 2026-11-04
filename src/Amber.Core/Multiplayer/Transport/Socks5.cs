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

    /// <summary>出口成员：处理一条已经接上的 SOCKS5 连接。</summary>
    public static async Task ServeAsync(Stream client, CancellationToken ct, Action<string>? log = null, Func<IPAddress, bool>? allow = null)
    {
        allow ??= IsPublic;
        var head = new byte[2];
        if (!await SecureStream.ReadExactAsync(client, head, ct) || head[0] != Version) return;
        var methods = new byte[head[1]];
        if (!await SecureStream.ReadExactAsync(client, methods, ct)) return;
        if (!methods.Contains((byte)0))
        {
            await client.WriteAsync(new byte[] { Version, 0xFF }, ct);
            return;
        }
        await client.WriteAsync(new byte[] { Version, 0 }, ct);
        await client.FlushAsync(ct);

        var request = new byte[4];
        if (!await SecureStream.ReadExactAsync(client, request, ct)) return;
        if (request[0] != Version || request[1] != 1)
        {
            await ReplyAsync(client, 7, ct);   // 只支持 CONNECT
            return;
        }
        var (host, port) = await ReadAddressAsync(client, request[3], ct);
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
            log?.Invoke($"[转发] 拒绝连接 {host}:{port}（本机、局域网或保留地址）");
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
        log?.Invoke($"[转发] {host}:{port}");
        await Pump.RunAsync(client, remote.GetStream(), ct);
    }

    /// <summary>加入方：在流上发 CONNECT host:port，成功后这个流就是到目标的连接。</summary>
    public static async Task ConnectAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { Version, 1, 0 }, ct);
        await stream.FlushAsync(ct);
        var choice = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, choice, ct) || choice[1] != 0) throw new IOException("出口不接受这个代理请求");
        var request = new List<byte> { Version, 1, 0 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255) throw new ArgumentException("主机名太长");
            request.Add(3);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }
        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        await stream.WriteAsync(request.ToArray(), ct);
        await stream.FlushAsync(ct);
        var reply = new byte[4];
        if (!await SecureStream.ReadExactAsync(stream, reply, ct)) throw new IOException("出口没有回应");
        await ReadAddressAsync(stream, reply[3], ct);
        if (reply[1] != 0) throw new IOException(reply[1] switch
        {
            2 => $"出口拒绝连接 {host}（本机、局域网或保留地址不能经别人转发）",
            4 => $"出口那边找不到 {host}",
            5 => $"{host}:{port} 拒绝连接",
            _ => $"出口连不上 {host}:{port}（代码 {reply[1]}）",
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
                throw new InvalidDataException("SOCKS5 地址类型不认识");
        }
        var port = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, port, ct)) throw new EndOfStreamException();
        return (host, BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    /// <summary>公网地址才允许经出口连：排除本机、私有网段、链路本地、CGNAT、组播、保留地址和 EasyTier 的虚拟网段。</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || b[0] == 0
                     || b[0] == 172 && b[1] >= 16 && b[1] < 32
                     || b[0] == 192 && b[1] == 168
                     || b[0] == 169 && b[1] == 254
                     || b[0] == 100 && b[1] >= 64 && b[1] < 128
                     || b[0] >= 224
                     || b[0] == 192 && b[1] == 0 && b[2] == 0
                     || b[0] == 198 && (b[1] == 18 || b[1] == 19));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }
        return false;
    }
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
        if (reader.VarInt() != 0x00) throw new InvalidDataException("第一个包不是 MC 握手包");
        var protocol = reader.VarInt();
        var address = reader.String(255);
        reader.UShort();
        var next = reader.VarInt();
        // Forge 客户端会在地址后面接 "\0FML3\0" 这类标记，服务端靠它认出 Forge，要原样保留。
        var marker = address.IndexOf('\0') is var i and >= 0 ? address[i..] : "";
        return new McDisguise.PacketWriter().VarInt(0x00).VarInt(protocol).String(realHost + marker).UShort((ushort)realPort).VarInt(next).ToPacket();
    }

    /// <summary>给 TunnelClient 用：先做 SOCKS5 CONNECT，再把改写过的握手包发过去，之后原样转发。</summary>
    public static Func<Stream, Stream, CancellationToken, Task<Stream>> ViaSocks(string host, int port) => async (game, remote, ct) =>
    {
        await Socks5.ConnectAsync(remote, host, port, ct);
        var first = await RewriteFirstPacketAsync(game, host, port, ct);
        await remote.WriteAsync(first, ct);
        await remote.FlushAsync(ct);
        return game;
    };
}
