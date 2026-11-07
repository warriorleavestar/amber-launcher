using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 在本机进程里走一遍邀请加入（敲门、批准、游戏通道、聊天）、再踢人换网络，把所有日志和诊断报告收起来，
/// 检查里面没有房间凭据、成员钥匙、大厅密钥、邀请码和直连码。
/// </summary>
public sealed class SecretsStayOutOfLogsTests
{
    [Fact]
    public async Task FullInviteJoinAndKickLogsNoSecrets()
    {
        var captured = new List<string>();
        void Capture(string line)
        {
            lock (captured) captured.Add(line);
        }
        var diag = new DiagnosticLog();
        diag.Added += e => Capture(e.ToString());

        using var host = X25519KeyPair.Generate();
        using var guest = X25519KeyPair.Generate();
        var settings = new TunnelSettings(TransportMode.High, false);
        var credentials = RoomCredentials.Create(host.PublicKey.ToArray(), 25565, settings);
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var book = new InviteBook();
        var pending = book.Create(2, TimeSpan.FromMinutes(10), false, settings);
        var invite = pending.Invite;
        var lobbyKey = invite.LobbyKey;

        await using var knock = new KnockServer(book, host, () => credentials, _ => false, (_, _) => Task.FromResult(true), IPAddress.Loopback, 0,
            log: line => diag.Add("knock", line), members: table);
        await using var tunnelServer = new TunnelServer(hostRoom, settings, 0, _ => async (stream, ct) =>
        {
            var buffer = new byte[64];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                await stream.FlushAsync(ct);
            }
        }, log: line => diag.Add("tunnel", line));
        await using var hub = new ChatHub(hostRoom, 0);

        // 敲门、批准、拿到凭据。
        KnockOutcome outcome;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, knock.Port);
            outcome = await KnockClient.KnockAsync(client.GetStream(), invite, guest, "Steve", "0.6.0", decisionTimeout: TimeSpan.FromSeconds(5));
        }
        Assert.True(outcome.Approved);
        var guestRoom = outcome.Credentials!.ToRoom();
        var entry = table.FindGuest(guest.PublicKey)!;
        var psk = entry.Key.CopyPsk();

        // 游戏通道和聊天。
        await using (var tunnel = await TunnelClient.Wrapped("127.0.0.1", tunnelServer.Port, guestRoom, settings, TunnelService.Game)(default))
        {
            await tunnel.WriteAsync("hi"u8.ToArray());
            await tunnel.FlushAsync();
            await tunnel.ReadExactlyAsync(new byte[2]);
        }
        await using var chat = new ChatClient(guestRoom, "Steve");
        var kicked = false;
        chat.Kicked += () => kicked = true;
        chat.Received += m => Capture(m.ToString() ?? "");
        await chat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));

        // 直连码：同一张邀请包进 JAY2D-，回复码 JAY2R-。
        var direct = new DirectCode(false, "0123456789abcdef", NatKind.Cone, [new Candidate(PathKind.Punch, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000))], invite);
        var reply = new DirectCode(true, "0123456789abcdef", NatKind.Cone, [new Candidate(PathKind.Punch, new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40001))]);

        // 踢人：通知、移出、作废邀请，再换网络。
        // 被踢的人回了确认才断开：他那边一定已经收到通知。
        Assert.True(await hub.NotifyKickedAsync(entry, TimeSpan.FromSeconds(10)));
        Assert.True(await WaitAsync(() => kicked));
        Assert.True(table.Kick(entry, book));
        Assert.True(await WaitAsync(() => hub.MemberCount == 0));
        var next = credentials.Rotate();
        await hub.SendRekeyAsync(next.ToRekey(), TimeSpan.FromSeconds(2));
        // 被踢的人再连：失败的错误消息也收起来。
        var again = await Record.ExceptionAsync(() => TunnelClient.Wrapped("127.0.0.1", tunnelServer.Port, guestRoom, settings, TunnelService.Game)(default));
        if (again is not null) Capture(again.Message);

        // 这些对象打进日志时（字符串插值、ToString）也不能带出秘密。
        foreach (var o in new object[] { credentials, next, next.ToRekey(), hostRoom, entry, entry.Key, outcome.Credentials, direct, reply, pending })
            Capture(o.ToString() ?? "");
        // 有人把码原样写进了日志：诊断报告要遮掉（房间密钥按房间的 Secrets 遮）。
        diag.Add("pasted", $"invite {invite} direct {direct.Encode()} reply {reply.Encode()}");
        var report = NetDiagnostics.BuildReport("test", diag.Snapshot(), null, [.. hostRoom.Secrets, .. next.ToRekey() is var r ? new[] { r.Network, r.Secret } : []], includePublicIps: false);

        string[] secrets =
        [
            credentials.Network, credentials.Secret, next.Network, next.Secret,
            Convert.ToBase64String(psk), Convert.ToHexString(psk),
            invite.ToString(), Convert.ToHexString(lobbyKey), Convert.ToHexString(invite.Token),
            LobbyKeys.LobbySecret(lobbyKey), LobbyKeys.LobbyName(lobbyKey),
            direct.Encode(), direct.Encode()[6..], reply.Encode()[6..],
        ];
        // 日志里本来就不该写出来的（除了故意写进去的那一行“pasted”）。
        var logged = string.Join("\n", captured.Where(l => !l.Contains("[pasted]", StringComparison.Ordinal)));
        // 真的收到了各处的日志（不是空着就算通过），故意写进去的那一行也进了报告。
        Assert.Contains(captured, l => l.Contains("[knock]", StringComparison.Ordinal));
        Assert.Contains("[pasted]", report, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, logged, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, report, StringComparison.OrdinalIgnoreCase);
        }
        CryptographicOperations.ZeroMemory(psk);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
