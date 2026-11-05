using Amber.Core.Multiplayer;
using System.Net;
using System.Security;
using System.Text;

namespace Amber.Core.Tests;

public class PortMappingSecurityTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress LocalAddress = IPAddress.Parse("192.168.1.20");
    private static readonly Uri Location = new("http://192.168.1.1:49152/igd/device.xml");

    [Theory]
    [InlineData("http://127.0.0.1/device.xml")]
    [InlineData("http://127.1/device.xml")]
    [InlineData("http://2130706433/device.xml")]
    [InlineData("http://0x7f000001/device.xml")]
    [InlineData("http://[::ffff:127.0.0.1]/device.xml")]
    [InlineData("http://0.0.0.0/device.xml")]
    [InlineData("http://239.255.255.250/device.xml")]
    [InlineData("http://192.168.1.2/device.xml")]
    [InlineData("http://router.local/device.xml")]
    [InlineData("ftp://192.168.1.1/device.xml")]
    [InlineData("file:///device.xml")]
    [InlineData("http://user@192.168.1.1/device.xml")]
    [InlineData("http://@192.168.1.1/device.xml")]
    [InlineData("http://192.168.1.1/device.xml#fragment")]
    [InlineData("http://192.168.1.1/device.xml#")]
    [InlineData("http://192.168.1.1/device%0d.xml")]
    [InlineData("http://192.168.1.1/device\t.xml")]
    [InlineData("http://192.168.1.1:0/device.xml")]
    public void DiscoveryRejectsUnsafeOrUnrelatedLocations(string location)
    {
        Assert.False(PortMapping.TryDiscoveryLocation(Gateway, new IPEndPoint(Gateway, 1900), Discovery(location), out _));
    }

    [Fact]
    public void DiscoveryRequiresTheSelectedGatewayResponderAndOneLocation()
    {
        Assert.False(PortMapping.TryDiscoveryLocation(Gateway, new IPEndPoint(LocalAddress, 1900), Discovery(Location.ToString()), out _));
        Assert.False(PortMapping.TryDiscoveryLocation(Gateway, new IPEndPoint(Gateway, 1900),
            Discovery(Location.ToString()) + "LOCATION: http://192.168.1.1/another.xml\r\n", out _));
        foreach (var address in new[] { "127.0.0.1", "0.0.0.0", "169.254.1.1", "224.0.0.1", "255.255.255.255" })
        {
            var gateway = IPAddress.Parse(address);
            Assert.False(PortMapping.TryDiscoveryLocation(gateway, new IPEndPoint(gateway, 1900), Discovery($"http://{address}/device.xml"), out _));
        }
    }

    [Theory]
    [InlineData("http://192.168.1.1:49152/igd/device.xml")]
    [InlineData("https://192.168.1.1/igd/device.xml")]
    public void ValidGatewayDiscoveryAndRelativeControlArePreserved(string value)
    {
        Assert.True(PortMapping.TryDiscoveryLocation(Gateway, new IPEndPoint(Gateway, 1900), Discovery(value), out var location));
        Assert.True(PortMapping.TryResolveControl(Gateway, location!, "../control/WANIPConn1", out var control));
        Assert.Equal(new Uri(location!, "../control/WANIPConn1"), control);
    }

    [Theory]
    [InlineData("http://127.0.0.1/control")]
    [InlineData("http://2130706433/control")]
    [InlineData("//192.168.1.2/control")]
    [InlineData("http://router.local:49152/control")]
    [InlineData("http://user@192.168.1.1:49152/control")]
    [InlineData("/control#fragment")]
    [InlineData("/control%0a")]
    [InlineData("\\\\127.0.0.1\\control")]
    public async Task UntrustedControlCausesNoSoapSend(string control)
    {
        var sends = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => PortMapping.MapUpnpAsync(25565, Gateway, LocalAddress, Location,
            _ => { }, default, () => new RecordingHandler(request =>
            {
                sends++;
                Assert.Equal(HttpMethod.Get, request.Method);
                return Ok(Description(control));
            })));
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("http://127.0.0.1/control")]
    [InlineData("http://0x7f000001/control")]
    [InlineData("http://192.168.1.2:49152/control")]
    [InlineData("https://192.168.1.2:49153/control")]
    [InlineData("ftp://192.168.1.1/control")]
    public async Task RequestBoundaryRejectsInvalidDestinationsBeforeSending(string target)
    {
        var sends = 0;
        using var http = new PortMapping.GatewayHttp(Gateway, LocalAddress, Location, new RecordingHandler(_ =>
        {
            sends++;
            return Ok("");
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        await Assert.ThrowsAsync<InvalidDataException>(() => http.SendAsync(request, default));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task RedirectResponsesAreRejectedWithoutASecondRequest()
    {
        using var productionHandler = PortMapping.CreateGatewayHandler(Gateway, LocalAddress);
        Assert.False(productionHandler.AllowAutoRedirect);
        Assert.False(productionHandler.UseProxy);
        Assert.NotNull(productionHandler.ConnectCallback);
        var sends = 0;
        using var http = new PortMapping.GatewayHttp(Gateway, LocalAddress, Location, new RecordingHandler(_ =>
        {
            sends++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/private");
            return response;
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, Location);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(request, default));
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescriptionAndSoapResponsesAreBoundedWithOrWithoutContentLength(bool knownLength)
    {
        using var http = new PortMapping.GatewayHttp(Gateway, LocalAddress, Location, new RecordingHandler(_ =>
        {
            HttpContent content = knownLength
                ? new ByteArrayContent(new byte[PortMapping.MaxUpnpResponseBytes + 1])
                : new StreamContent(new NonSeekableStream(new byte[PortMapping.MaxUpnpResponseBytes + 1]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        using var request = new HttpRequestMessage(knownLength ? HttpMethod.Get : HttpMethod.Post, Location);
        await Assert.ThrowsAsync<InvalidDataException>(() => http.SendAsync(request, default));
    }

    [Fact]
    public async Task ResponseBodyMustFinishWithinTheRequestDeadline()
    {
        using var http = new PortMapping.GatewayHttp(Gateway, LocalAddress, Location,
            new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }),
            TimeSpan.FromMilliseconds(150));
        using var request = new HttpRequestMessage(HttpMethod.Get, Location);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.SendAsync(request, default).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData("../control/WANIPConn1", false)]
    [InlineData("../control/WANIPConn1", true)]
    [InlineData("http://192.168.1.1:49153/control", false)]
    [InlineData("https://192.168.1.1:49152/control", false)]
    [InlineData("https://192.168.1.1/control", false)]
    [InlineData("https://192.168.1.1/control", true)]
    public async Task ValidMapAndCleanupUseTheApprovedGatewayEndpoint(string control, bool redirectCleanup)
    {
        var requests = new List<(HttpMethod Method, Uri Uri, string? Action)>();
        var handlers = 0;
        var result = await PortMapping.MapUpnpAsync(25565, Gateway, LocalAddress, Location, _ => { }, default, () =>
        {
            handlers++;
            return new RecordingHandler(request =>
            {
                var action = request.Headers.TryGetValues("SOAPAction", out var values) ? values.Single() : null;
                requests.Add((request.Method, request.RequestUri!, action));
                if (request.Method == HttpMethod.Get) return Ok(Description(control));
                if (action!.Contains("GetExternalIPAddress")) return Ok("<NewExternalIPAddress>8.8.8.8</NewExternalIPAddress>");
                if (redirectCleanup && action.Contains("DeletePortMapping"))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("http://127.0.0.1/private");
                    return redirect;
                }
                return Ok("<ok/>");
            });
        });
        Assert.NotNull(result);
        Assert.Equal("UPnP", result.Method);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 25565), result.External);
        if (redirectCleanup) await Assert.ThrowsAsync<HttpRequestException>(result.Remove!);
        else await result.Remove!();
        Assert.Equal(2, handlers);
        Assert.Equal(4, requests.Count);
        Assert.Equal(Location, requests[0].Uri);
        Assert.All(requests.Skip(1), request => Assert.Equal(new Uri(Location, control), request.Uri));
        Assert.Contains("AddPortMapping", requests[2].Action);
        Assert.Contains("DeletePortMapping", requests[3].Action);
    }

    [Fact]
    public async Task DeviceDescriptionCannotResolveExternalXmlEntities()
    {
        var sends = 0;
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => PortMapping.MapUpnpAsync(25565, Gateway, LocalAddress, Location,
            _ => { }, default, () => new RecordingHandler(_ =>
            {
                sends++;
                return Ok("<!DOCTYPE root [<!ENTITY external SYSTEM 'http://127.0.0.1/secret'>]><root>&external;</root>");
            })));
        Assert.Equal(1, sends);
    }

    private static string Discovery(string location) => $"HTTP/1.1 200 OK\r\nLOCATION: {location}\r\n\r\n";
    private static string Description(string control) =>
        $"<root xmlns='urn:schemas-upnp-org:device-1-0'><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>{SecurityElement.Escape(control)}</controlURL></service></root>";
    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class StallingStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
