using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>邀请码 JAY2-：编码、宽松解析、校验位；大厅和敲门的派生值固定不变（对照独立算出的 HKDF 结果）。</summary>
public sealed class InviteTests
{
    private static readonly byte[] L = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
    private static readonly byte[] K = [.. Enumerable.Range(16, 16).Select(i => (byte)i)];
    private const string Plain = "JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-2";
    private const string Wrapped = "JAY2-T2D-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-8";

    [Fact]
    public void EncodesKnownValues()
    {
        Assert.Equal(Plain, new JayInvite(L, K, TunnelSettings.Default).ToString());
        Assert.Equal(Wrapped, new JayInvite(L, K, new TunnelSettings(TransportMode.High, true)).ToString());
    }

    [Fact]
    public void RandomInvitesRoundTrip()
    {
        for (var i = 0; i < 200; i++)
        {
            var mode = (TransportMode)(i % 4);
            var tunnel = mode == TransportMode.General ? TunnelSettings.Default : new TunnelSettings(mode, i % 3 == 0);
            var invite = JayInvite.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), tunnel);
            Assert.True(JayInvite.TryParse(invite.ToString(), out var parsed));
            Assert.Equal(invite.LobbyKey, parsed.LobbyKey);
            Assert.Equal(invite.Token, parsed.Token);
            Assert.Equal(invite.Tunnel, parsed.Tunnel);
        }
    }

    [Theory]
    [InlineData("朋友发来的：" + Plain + " 快来")]
    [InlineData("jay2-00041061050r3gg28a1c60t3gf-0g2491650n2rbhg68t3ce1t7gz-2")]
    [InlineData("JAY2- 0OO41O61O5OR3GG28A1C6OT3GF - 0G2491650N2RBHG68T3CE1T7GZ")]
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ。")]
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-2, thanks")]
    public void ParsesLeniently(string text)
    {
        Assert.True(JayInvite.TryParse(text, out var invite));
        Assert.Equal(L, invite.LobbyKey);
        Assert.Equal(K, invite.Token);
        Assert.False(invite.Tunnel.Wraps);
    }

    [Fact]
    public void ParsesTunnelMarker()
    {
        Assert.True(JayInvite.TryParse(Wrapped.ToLowerInvariant(), out var invite));
        Assert.Equal(new TunnelSettings(TransportMode.High, true), invite.Tunnel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("JAY2-")]
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF")]
    // 校验位不对（抄错了一位）。
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-3")]
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GY-2")]
    // 模式段被改了：校验位对不上。
    [InlineData("JAY2-T3D-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-8")]
    [InlineData("JAY2-T4-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ")]
    // 少一位、多一位、首位超过 7（超过 128 位）、有非法字符。
    [InlineData("JAY2-0004106105R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ")]
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ7")]
    [InlineData("JAY2-80041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ")]
    [InlineData("JAY2-00041061050R3GG28A1C60T3G#-0G2491650N2RBHG68T3CE1T7GZ")]
    // 校验位后面紧跟字母数字：码被改坏了。
    [InlineData("JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-2X")]
    // 旧版本的房间码不是邀请码。
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565")]
    public void RejectsBadInvites(string text) => Assert.False(JayInvite.TryParse(text, out _));

    [Fact]
    public void EveryPositionTypoIsCaughtOrChangesNothing()
    {
        // 改 A 或 B 里任意一位：要么校验位发现，要么（极少数）解析成另一张邀请——绝不会解析成原来那张。
        var caught = 0;
        var positions = 0;
        for (var i = 5; i < Plain.Length - 2; i++)
        {
            if (Plain[i] == '-') continue;
            positions++;
            var replacement = Plain[i] == 'A' ? 'B' : 'A';
            var typo = Plain[..i] + replacement + Plain[(i + 1)..];
            if (!JayInvite.TryParse(typo, out var parsed)) caught++;
            else Assert.False(parsed.LobbyKey.SequenceEqual(L) && parsed.Token.SequenceEqual(K));
        }
        Assert.True(caught >= positions - 2, $"{caught}/{positions}");
    }

    [Fact]
    public void MaskedHidesKeys()
    {
        var masked = new JayInvite(L, K, TunnelSettings.Default).Masked;
        Assert.StartsWith("JAY2-", masked);
        Assert.DoesNotContain("00041061050R3GG28A1C60T3GF", masked);
        Assert.DoesNotContain("0G2491650N2RBHG68T3CE1T7GZ", masked);
    }

    [Fact]
    public void DerivationsAreStable()
    {
        Assert.Equal("jay-l-10b68953d25983cc", LobbyKeys.LobbyName(L));
        Assert.Equal("c05429d71805e369730635b7415c46e35c95f14ab0f2f84a63ab481ab4587dd2", LobbyKeys.LobbySecret(L));
        Assert.Equal("45dd1bfead25af4c", Convert.ToHexStringLower(LobbyKeys.InviteId(K)));
        Assert.Equal("d26c0be6019c48dc094652dc45ef58587870be69d7cca11f4bdf0b989a2cb981", Convert.ToHexStringLower(LobbyKeys.KnockPsk(K)));
    }

    /// <summary>诊断报告里：邀请码整段、大厅和房间的网络名、房间的 256 位密钥都不留。</summary>
    [Fact]
    public void DiagnosticsRedactInvitesAndRoomSecrets()
    {
        var credentials = RoomCredentials.Create(new byte[32], 25565, TunnelSettings.Default) with { Member = MemberKey.Create() };
        var room = credentials.ToRoom();
        var lobby = LobbyKeys.LobbyName(L);
        var text = $"发邀请 {Plain} / {Wrapped.ToLowerInvariant()} 大厅 {lobby} 房间 {room.EasyTierNetworkName} 密钥 {room.EasyTierSecret} " +
            $"network_secret: \"{LobbyKeys.LobbySecret(L)}\"";
        var report = NetDiagnostics.Redact(text, room.Secrets, includePublicIps: true);
        Assert.DoesNotContain("0G2491650N2RBHG68T3CE1T7GZ", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00041061050R3GG28A1C60T3GF", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lobby, report);
        Assert.DoesNotContain(room.EasyTierNetworkName, report);
        Assert.DoesNotContain(room.EasyTierSecret, report);
        Assert.DoesNotContain(LobbyKeys.LobbySecret(L), report);
        Assert.Contains("JAY2-", report);
    }

    [Theory]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565", true)]
    [InlineData("AMBDIRECT1-abc", true)]
    [InlineData(Plain, false)]
    [InlineData("随便写的", false)]
    public void RecognizesOldCodes(string text, bool old) => Assert.Equal(old, JayInvite.LooksLikeOldCode(text));
}
