using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Amber.Core.Multiplayer;

/// <summary>路由器上开的一个端口映射。Dispose 时删掉映射。</summary>
public sealed record PortMappingResult(string Method, IPEndPoint External, Func<Task>? Remove);

/// <summary>
/// 请路由器把一个外部 UDP 端口转到本机（UPnP IGD 和 NAT-PMP）。只改路由器上的临时映射（带租期），
/// 不改本机系统设置；结束联机时删掉。路由器关了这个功能、或者路由器自己也在运营商 NAT 后面时没用。
/// </summary>
public static partial class PortMapping
{
    public static async Task<PortMappingResult?> TryMapAsync(int localPort, Action<string> log, CancellationToken ct = default)
    {
        try
        {
            if (await TryUpnpAsync(localPort, log, ct) is { } upnp) return upnp;
        }
        catch (Exception e) when (e is SocketException or HttpRequestException or IOException or System.Xml.XmlException or OperationCanceledException or FormatException)
        {
            if (ct.IsCancellationRequested) throw;
            log(L.T("core.mp.upnp.failed") + e.Message);
        }
        try
        {
            if (await TryNatPmpAsync(localPort, log, ct) is { } pmp) return pmp;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            log(L.T("core.mp.natpmp.failed") + e.Message);
        }
        log(L.T("core.mp.pcp.skipped"));
        return null;
    }

    /// <summary>运营商级 NAT（100.64.0.0/10）和内网地址拿到映射也没用。</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6UniqueLocal && !IPAddress.IsLoopback(address);
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || b[0] >= 224);
    }

    // ---- UPnP IGD ----

    private static async Task<PortMappingResult?> TryUpnpAsync(int localPort, Action<string> log, CancellationToken ct)
    {
        var location = await DiscoverAsync(ct);
        if (location is null)
        {
            log(L.T("core.mp.upnp.notfound"));
            return null;
        }
        log(L.T("core.mp.upnp.gateway") + location.Host);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var xml = XDocument.Parse(await http.GetStringAsync(location, ct));
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        var service = xml.Descendants(ns + "service").FirstOrDefault(s =>
            (string?)s.Element(ns + "serviceType") is { } t && (t.Contains("WANIPConnection") || t.Contains("WANPPPConnection")));
        if (service is null)
        {
            log(L.T("core.mp.upnp.noservice"));
            return null;
        }
        var type = (string)service.Element(ns + "serviceType")!;
        var control = new Uri(location, (string)service.Element(ns + "controlURL")!);
        var localIp = LocalAddressTowards(IPAddress.Parse(location.Host));

        var external = ExtractTag(await SoapAsync(http, control, type, "GetExternalIPAddress", "", ct), "NewExternalIPAddress");
        if (external is null || !IPAddress.TryParse(external, out var externalIp))
        {
            log(L.T("core.mp.upnp.noexternal"));
            return null;
        }
        log(L.F("core.mp.upnp.external", externalIp, IsPublic(externalIp) ? "" : L.T("core.mp.upnp.privateaddress")));
        if (!IsPublic(externalIp)) return null;

        var args = $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{localPort}</NewExternalPort><NewProtocol>UDP</NewProtocol>" +
                   $"<NewInternalPort>{localPort}</NewInternalPort><NewInternalClient>{localIp}</NewInternalClient><NewEnabled>1</NewEnabled>" +
                   $"<NewPortMappingDescription>Amber</NewPortMappingDescription><NewLeaseDuration>7200</NewLeaseDuration>";
        await SoapAsync(http, control, type, "AddPortMapping", args, ct);
        log(L.F("core.mp.upnp.mapped", localPort, localIp, localPort));
        return new PortMappingResult("UPnP", new IPEndPoint(externalIp, localPort), async () =>
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            await SoapAsync(h, control, type, "DeletePortMapping",
                $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{localPort}</NewExternalPort><NewProtocol>UDP</NewProtocol>", CancellationToken.None);
        });
    }

    private static async Task<Uri?> DiscoverAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                var m = LocationPattern().Match(Encoding.ASCII.GetString(r.Buffer));
                if (m.Success && Uri.TryCreate(m.Groups[1].Value.Trim(), UriKind.Absolute, out var uri)) return uri;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?im)^location:\s*(\S+)")]
    private static partial Regex LocationPattern();

    private static async Task<string> SoapAsync(HttpClient http, Uri control, string serviceType, string action, string args, CancellationToken ct)
    {
        var body = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   $"<s:Body><u:{action} xmlns:u=\"{serviceType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(L.F("core.mp.upnp.rejected", action, ExtractTag(text, "errorDescription") ?? response.StatusCode.ToString()));
        return text;
    }

    internal static string? ExtractTag(string xml, string tag)
    {
        var m = Regex.Match(xml, $"<{tag}>([^<]*)</{tag}>");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---- NAT-PMP（RFC 6886）----

    private static async Task<PortMappingResult?> TryNatPmpAsync(int localPort, Action<string> log, CancellationToken ct)
    {
        var gateway = DefaultGateway();
        if (gateway is null)
        {
            log(L.T("core.mp.natpmp.nogateway"));
            return null;
        }
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(gateway, 5351);
        var address = await PmpRequestAsync(udp, target, [0, 0], 12, ct);
        if (address is null)
        {
            log(L.T("core.mp.natpmp.noresponse"));
            return null;
        }
        var externalIp = new IPAddress(address.AsSpan(8, 4).ToArray());
        log(L.F("core.mp.natpmp.external", externalIp));
        if (!IsPublic(externalIp)) return null;
        var mapRequest = new byte[12];
        mapRequest[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(mapRequest.AsSpan(4), (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(mapRequest.AsSpan(6), (ushort)localPort);
        BinaryPrimitives.WriteUInt32BigEndian(mapRequest.AsSpan(8), 7200);
        var mapped = await PmpRequestAsync(udp, target, mapRequest, 16, ct);
        if (mapped is null || BinaryPrimitives.ReadUInt16BigEndian(mapped.AsSpan(2)) != 0) return null;
        var externalPort = BinaryPrimitives.ReadUInt16BigEndian(mapped.AsSpan(10));
        log(L.F("core.mp.natpmp.mapped", externalPort, localPort));
        return new PortMappingResult("NAT-PMP", new IPEndPoint(externalIp, externalPort), async () =>
        {
            using var u = new UdpClient(AddressFamily.InterNetwork);
            var delete = (byte[])mapRequest.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(delete.AsSpan(6), 0);
            BinaryPrimitives.WriteUInt32BigEndian(delete.AsSpan(8), 0);
            await u.SendAsync(delete, target);
        });
    }

    private static async Task<byte[]?> PmpRequestAsync(UdpClient udp, IPEndPoint target, byte[] request, int expected, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(request, target, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
            try
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                if (r.Buffer.Length >= expected) return r.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    internal static IPAddress? DefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    private static IPAddress LocalAddressTowards(IPAddress remote)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(remote, 9);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}
