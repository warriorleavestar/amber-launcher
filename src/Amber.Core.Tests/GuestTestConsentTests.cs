using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>R2：房主的全条件测试只在加入方用户同意后执行，完整局域网和出口要分别同意，旧的、重复、乱序的步骤不认，测完交回原来的设置。</summary>
public sealed class GuestTestConsentTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static readonly EasyTierOptions Original = new(Room, false, ["udp://127.0.0.1:1"], 15000);
    private const string Run = "0123456789abcdef";

    [Fact]
    public void NothingRunsWithoutConsent()
    {
        var c = new GuestTestConsent();
        Assert.False(c.AllowsElevate(Run));
        Assert.Equal("not-consented", c.BeginStep(Run, 1, false, false));

        Assert.True(c.Request(new TestRequest(Run, true)));
        // 请求了但用户还没点同意。
        Assert.Equal("not-consented", c.BeginStep(Run, 1, false, false));
        Assert.Null(c.Answer(false, true, true, Original));
        Assert.Equal("not-consented", c.BeginStep(Run, 1, false, false));
        Assert.Null(c.ActiveRun);
    }

    [Fact]
    public void FullLanAndExitNeedTheirOwnConsent()
    {
        var c = new GuestTestConsent();
        c.Request(new TestRequest(Run, true));
        Assert.Equal((Run, false, false), c.Answer(true, false, true, Original));
        Assert.False(c.AllowsElevate(Run));
        Assert.Equal("full-lan-not-allowed", c.BeginStep(Run, 1, true, false));
        Assert.Equal("exit-not-allowed", c.BeginStep(Run, 1, false, true));
        Assert.Null(c.BeginStep(Run, 1, false, false));

        var d = new GuestTestConsent();
        d.Request(new TestRequest(Run, true));
        Assert.Equal((Run, true, false), d.Answer(true, true, false, Original));
        Assert.True(d.AllowsElevate(Run));
        Assert.Equal("exit-not-allowed", d.BeginStep(Run, 1, true, true));

        // 房主没请求完整局域网：用户勾了也不算。
        var e = new GuestTestConsent();
        e.Request(new TestRequest(Run, false));
        Assert.Equal((Run, false, false), e.Answer(true, true, true, Original));
    }

    [Fact]
    public void StaleDuplicateAndOtherRunStepsAreRejected()
    {
        var c = new GuestTestConsent();
        c.Request(new TestRequest(Run, false));
        c.Answer(true, false, false, Original);
        Assert.Equal("not-consented", c.BeginStep("ffffffffffffffff", 1, false, false));
        Assert.Equal("not-consented", c.BeginStep(null, 1, false, false));
        Assert.Null(c.BeginStep(Run, 2, false, false));
        Assert.Equal("busy", c.BeginStep(Run, 3, false, false));
        c.EndStep(Run);
        Assert.Equal("stale", c.BeginStep(Run, 2, false, false));
        Assert.Equal("stale", c.BeginStep(Run, 1, false, false));
        Assert.Null(c.BeginStep(Run, 3, false, false));
        // 已经在测时不接受新的请求。
        Assert.False(c.Request(new TestRequest("aaaaaaaaaaaaaaaa", false)));
    }

    [Fact]
    public void EndReturnsOriginalAndClearsConsent()
    {
        var c = new GuestTestConsent();
        c.Request(new TestRequest(Run, false));
        c.Answer(true, false, false, Original);
        Assert.False(c.End("ffffffffffffffff", out _));
        Assert.True(c.End(Run, out var original));
        Assert.Same(Original, original);
        Assert.Equal("not-consented", c.BeginStep(Run, 1, false, false));
        Assert.Null(c.ActiveRun);
        Assert.Equal(Run, c.RestoringRun);
        Assert.False(c.Request(new TestRequest("aaaaaaaaaaaaaaaa", false)));
        Assert.False(c.End(Run, out _));
        c.CompleteEnd("ffffffffffffffff");
        Assert.Equal(Run, c.RestoringRun);
        c.CompleteEnd(Run);
        Assert.Null(c.RestoringRun);
        Assert.True(c.Request(new TestRequest("aaaaaaaaaaaaaaaa", false)));
    }

    [Fact]
    public void IdleConsentExpires()
    {
        var now = DateTime.UtcNow;
        var c = new GuestTestConsent(() => now);
        c.Request(new TestRequest(Run, false));
        c.Answer(true, false, false, Original);
        Assert.False(c.IsIdle(out _));
        now += GuestTestConsent.IdleLimit + TimeSpan.FromSeconds(1);
        Assert.True(c.IsIdle(out var run));
        Assert.Equal(Run, run);
        Assert.Equal("not-consented", c.BeginStep(Run, 1, false, false));
    }

    [Fact]
    public void BadStepMessagesAreIgnored()
    {
        Assert.Null(DirectSession.ParseStep(new JsonObject { ["i"] = "1", ["route"] = "Direct", ["path"] = "Lan" }));
        Assert.Null(DirectSession.ParseStep(new JsonObject { ["i"] = 1, ["route"] = 7 }));
        Assert.Null(DirectSession.ParseStep(new JsonObject { ["i"] = 1, ["route"] = "99" }));
        Assert.Null(DirectSession.ParseStep(new JsonObject { ["i"] = 1, ["route"] = "Direct" }));
        Assert.Null(DirectSession.ParseStep(new JsonObject { ["i"] = 1, ["route"] = "Direct", ["path"] = "Nope" }));
        var step = DirectSession.ParseStep(new JsonObject { ["i"] = 2, ["route"] = "Direct", ["path"] = "Lan", ["fullLan"] = "yes", ["exit"] = true });
        Assert.Equal(new MatrixStep(2, RouteKind.Direct, PathKind.Lan, false, true), step);
    }
}
