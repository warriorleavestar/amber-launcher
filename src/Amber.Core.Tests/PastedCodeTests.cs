using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class PastedCodeTests
{
    [Theory]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565", PastedCodeKind.Room)]
    [InlineData("房间码：amb-t2d-K7Q2M9XD-4HZP8RW2N6TC-25565，快来", PastedCodeKind.Room)]
    [InlineData("AMBDIRECT1-eyJhIjoxfQ", PastedCodeKind.DirectInvite)]
    [InlineData("来直连：ambdirect1-eyJhIjoxfQ", PastedCodeKind.DirectInvite)]
    [InlineData("AMBREPLY1-eyJhIjoxfQ", PastedCodeKind.DirectReply)]
    [InlineData("AMBNODE-eyJ1IjpbXX0", PastedCodeKind.NodeShare)]
    [InlineData("", PastedCodeKind.Unknown)]
    [InlineData("   ", PastedCodeKind.Unknown)]
    [InlineData(null, PastedCodeKind.Unknown)]
    [InlineData("AMB-坏掉的码", PastedCodeKind.Unknown)]
    [InlineData("tcp://example.com:11010", PastedCodeKind.Unknown)]
    public void DetectsKindByPrefix(string? text, PastedCodeKind expected) =>
        Assert.Equal(expected, PastedCode.Detect(text));

    [Fact]
    public void BodyThatHappensToContainRoomPrefixIsStillDirect() =>
        // base64url 正文里可能出现 “AMB-…-…-端口” 这样的片段，前缀优先。
        Assert.Equal(PastedCodeKind.DirectInvite, PastedCode.Detect("AMBDIRECT1-xxAMB-K7Q2M9XD-4HZP8RW2N6TC-25565"));

    [Fact]
    public void RealCodesRoundTrip()
    {
        Assert.Equal(PastedCodeKind.Room, PastedCode.Detect(RoomCode.Create(25565).ToString()));
        Assert.Equal(PastedCodeKind.NodeShare, PastedCode.Detect(RelayNodes.Encode(null, ["tcp://example.com:11010"])));
    }
}
