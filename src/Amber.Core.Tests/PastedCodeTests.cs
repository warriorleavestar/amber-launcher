using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class PastedCodeTests
{
    [Theory]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565", PastedCodeKind.Room)]
    [InlineData("房间码：amb-t2d-K7Q2M9XD-4HZP8RW2N6TC-25565，快来", PastedCodeKind.Room)]
    [InlineData("JAY2D-eyJ2IjoxfQ", PastedCodeKind.DirectInvite)]
    [InlineData("来直连：jay2d-eyJ2IjoxfQ", PastedCodeKind.DirectInvite)]
    [InlineData("JAY2R-eyJ2IjoxfQ", PastedCodeKind.DirectReply)]
    [InlineData("AMBDIRECT1-eyJhIjoxfQ", PastedCodeKind.OldDirect)]
    [InlineData("来直连：ambdirect1-eyJhIjoxfQ", PastedCodeKind.OldDirect)]
    [InlineData("AMBREPLY1-eyJhIjoxfQ", PastedCodeKind.OldDirect)]
    [InlineData("AMBNODE-eyJ1IjpbXX0", PastedCodeKind.NodeShare)]
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-2", PastedCodeKind.Invite)]
    [InlineData("邀请你：jay2-t2d-00041061050r3gg28a1c60t3gf-0g2491650n2rbhg68t3ce1t7gz-8 快来", PastedCodeKind.Invite)]
    [InlineData("", PastedCodeKind.Unknown)]
    [InlineData("   ", PastedCodeKind.Unknown)]
    [InlineData(null, PastedCodeKind.Unknown)]
    [InlineData("AMB-坏掉的码", PastedCodeKind.Unknown)]
    [InlineData("tcp://example.com:11010", PastedCodeKind.Unknown)]
    public void DetectsKindByPrefix(string? text, PastedCodeKind expected) =>
        Assert.Equal(expected, PastedCode.Detect(text));

    [Fact]
    public void BodyThatHappensToContainRoomPrefixIsStillDirect()
    {
        // base64url 正文里可能出现 “AMB-…-…-端口” 这样的片段，前缀优先。
        Assert.Equal(PastedCodeKind.DirectInvite, PastedCode.Detect("JAY2D-xxAMB-K7Q2M9XD-4HZP8RW2N6TC-25565"));
        Assert.Equal(PastedCodeKind.OldDirect, PastedCode.Detect("AMBDIRECT1-xxAMB-K7Q2M9XD-4HZP8RW2N6TC-25565"));
    }

    [Fact]
    public void RealCodesRoundTrip()
    {
        Assert.Equal(PastedCodeKind.Room, PastedCode.Detect(RoomCode.Create(25565).ToString()));
        Assert.Equal(PastedCodeKind.NodeShare, PastedCode.Detect(RelayNodes.Encode(null, ["tcp://example.com:11010"])));
        var invite = JayInvite.Create(new byte[16], new Amber.Core.Multiplayer.Transport.TunnelSettings(Amber.Core.Multiplayer.Transport.TransportMode.High, true));
        Assert.Equal(PastedCodeKind.Invite, PastedCode.Detect(invite.ToString()));
    }
}
