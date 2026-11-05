using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public sealed class ExitRoutingPlanTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static EasyTierOptions Guest => new(Room, false, ["udp://127.0.0.1:23000"], 10000,
        Tun: true, ExitNode: EasyTierArgs.HostIp) { DirectUnderlayAddresses = ["203.0.113.10", "192.168.1.2", "203.0.113.10"] };

    [Fact]
    public void ExitSelectionRequiresCaptureRoutesAndProtectsDirectUnderlay()
    {
        var plan = Assert.IsType<ExitNetworkPlan>(ExitNetworkPlan.Create(Guest));
        Assert.Equal(["192.168.1.2", "203.0.113.10"], plan.UnderlayAddresses);
        Assert.Equal("Amber LAN-10000", plan.AdapterName);
        var args = EasyTierArgs.Build(Guest);
        var offset = args.IndexOf("--manual-routes");
        Assert.True(offset >= 0);
        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1"], args.Skip(offset + 1).Take(2));
        Assert.Contains("--disable-p2p", args);
        Assert.Contains("--enable-udp-broadcast-relay", args);
        Assert.Equal(plan.AdapterName, args[args.IndexOf("--dev-name") + 1]);
    }

    [Fact]
    public void ProvidingExitAndOrdinaryLanDoNotCaptureLocalInternet()
    {
        foreach (var allow in new[] { false, true })
        {
            var options = Guest with { ExitNode = null, AllowExit = allow };
            Assert.Null(ExitNetworkPlan.Create(options));
            Assert.DoesNotContain("--manual-routes", EasyTierArgs.Build(options));
        }
    }

    [Fact]
    public void NodeModeKeepsNativePhysicalInterfaceBinding()
    {
        var args = EasyTierArgs.Build(Guest with { Peers = ["udp://203.0.113.10:11010"], DirectUnderlayAddresses = [] });
        Assert.Equal("true", args[args.IndexOf("--bind-device") + 1]);
        Assert.Contains("--disable-p2p", args);
        Assert.Contains("--manual-routes", args);
        Assert.Equal(["203.0.113.10"], ExitNetworkPlan.Create(Guest with
            { Peers = ["udp://203.0.113.10:11010"], DirectUnderlayAddresses = [] })!.UnderlayAddresses);
    }

    [Fact]
    public void MixedBridgeAndPublicFallbackFailsBeforeChangingNetwork()
    {
        Assert.Throws<InvalidOperationException>(() => EasyTierArgs.Build(Guest with
            { Peers = ["udp://127.0.0.1:23000", "udp://203.0.113.10:11010"] }));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("224.1.2.3")]
    [InlineData("255.255.255.255")]
    [InlineData("10.233.144.1")]
    [InlineData("example.com")]
    [InlineData("1.2.3.4; whoami")]
    public void InvalidUnderlayIsRejected(string address) =>
        Assert.Throws<ArgumentException>(() => ExitNetworkPlan.Create(Guest with { DirectUnderlayAddresses = [address] }));

    [Fact]
    public void CannotSelectSelfOrUseUnlimitedUnderlayList()
    {
        Assert.Throws<ArgumentException>(() => ExitNetworkPlan.Create(Guest with { ExitNode = EasyTierArgs.VirtualIp(Guest) }));
        Assert.Throws<ArgumentException>(() => ExitNetworkPlan.Create(Guest with
            { DirectUnderlayAddresses = Enumerable.Repeat("203.0.113.10", ExitNetworkPlan.MaxUnderlayAddresses + 1).ToArray() }));
    }

    [Fact]
    public void UntrackedIpv6OrHostnameUnderlayIsRejectedBeforeRouteChanges()
    {
        Assert.Throws<InvalidOperationException>(() => ExitNetworkPlan.Create(Guest with { DirectUnderlayUsesIpv6 = true }));
        Assert.Throws<InvalidOperationException>(() => ExitNetworkPlan.Create(Guest with { Peers = ["udp://relay.example:11010"] }));
        Assert.Throws<InvalidOperationException>(() => ExitNetworkPlan.Create(Guest with { Peers = ["udp://[2001:db8::1]:11010"] }));
    }
}
