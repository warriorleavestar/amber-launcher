using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 成员被移出时钥匙清零：查到成员之后、取钥匙之前他刚好被踢，也不能拿一把全零的 psk 去握手或算直连证明。
/// 作废标记在成员表的锁里、清零之前就设上。
/// </summary>
public sealed class MemberKeyRevocationTests
{
    private static readonly byte[] GuestPub = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    [Fact]
    public void ZeroedKeyRefusesToHandOutThePsk()
    {
        var key = MemberKey.Create();
        Assert.False(key.IsZeroed);
        var before = key.CopyPsk();
        Assert.Contains(before, b => b != 0);
        key.Zero();
        Assert.True(key.IsZeroed);
        Assert.Throws<CryptographicException>(() => key.CopyPsk());
        Assert.Throws<CryptographicException>(() => key.PskBase64);
    }

    /// <summary>成员表在锁里只做 MarkRevoked（不触发回调）：这一步之后 Revoked 已经是 true，钥匙同时清零。</summary>
    [Fact]
    public void MarkRevokedFlagsTheEntryAtTheMomentTheKeyIsZeroed()
    {
        var table = new MemberTable();
        var entry = table.Issue(GuestPub, "Alice");
        entry.MarkRevoked();
        Assert.True(entry.Key.IsZeroed);
        Assert.True(entry.Revoked);
        // 断开连接的回调还没跑（那是锁外 Revoke 的事），但谁再来查都已经看到作废。
        Assert.False(entry.Revocation.IsCancellationRequested);
        Assert.True(table.Remove(entry));
        Assert.True(entry.Revocation.IsCancellationRequested);
    }

    [Fact]
    public void ReissueAndClearAlsoRevokeTheOldEntries()
    {
        var table = new MemberTable();
        var first = table.Issue(GuestPub, "Alice");
        var second = table.Issue(GuestPub, "Alice");
        Assert.True(first.Revoked);
        Assert.Throws<CryptographicException>(() => first.Key.CopyPsk());
        table.Clear();
        Assert.True(second.Revoked);
        Assert.Throws<CryptographicException>(() => second.Key.CopyPsk());
    }

    /// <summary>
    /// 房主读到成员编号、查到这个人，然后他被踢了，再接着握手：握手失败（CryptographicException），
    /// 加入方拿不到会话。用的是被踢之前查到的那个对象，模拟“查到之后、取钥匙之前”的那一刻。
    /// </summary>
    [Fact]
    public async Task HandshakeWithAnEntryRevokedAfterLookupFailsClosed()
    {
        var table = new MemberTable();
        var entry = table.Issue(GuestPub, "Alice");
        // 加入方手里的是自己那份钥匙（从敲门回复解出来的），房主清零不影响它。
        var guestKey = new MemberKey(entry.Key.Id, entry.Key.CopyPsk());
        var profile = MemberChannel.ChatProfile(TransportMode.High);

        var (client, server, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var connect = MemberChannel.ConnectAsync(client, guestKey, profile, "chat", []);
            var looked = await MemberChannel.ReadMemberAsync(server, table, CancellationToken.None);
            Assert.Same(entry, looked);
            table.Remove(entry);
            await Assert.ThrowsAsync<CryptographicException>(() => MemberChannel.AcceptMemberAsync(server, looked, profile, "chat", null, CancellationToken.None));
            server.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => connect);
        }
    }

    /// <summary>同样的时刻，直连桥的成员证明：钥匙已清零就算不出证明，不会退化成全零 psk 的 HMAC。</summary>
    [Fact]
    public void AdmitProofWithARevokedEntryFailsClosed()
    {
        var table = new MemberTable();
        var entry = table.Issue(GuestPub, "Alice");
        table.TryGet(entry.Key.Id, out var looked);
        table.Remove(entry);
        Assert.Throws<CryptographicException>(() => DirectKeys.AdmitProof(looked.Key, "session", new byte[16]));
    }

    /// <summary>边取钥匙边移出：每次要么拿到真钥匙，要么抛异常，绝不会拿到全零的。</summary>
    [Fact]
    public async Task ConcurrentRemovalNeverYieldsAZeroPsk()
    {
        for (var round = 0; round < 50; round++)
        {
            var table = new MemberTable();
            var entry = table.Issue(GuestPub, "Alice");
            using var stop = new CancellationTokenSource();
            var reader = Task.Run(() =>
            {
                var copies = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var psk = entry.Key.CopyPsk();
                        Assert.Contains(psk, x => x != 0);
                        copies++;
                    }
                    catch (CryptographicException)
                    {
                        Assert.True(entry.Revoked);
                        return copies;
                    }
                }
                return copies;
            });
            await Task.Yield();
            table.Remove(entry);
            stop.CancelAfter(TimeSpan.FromSeconds(5));
            await reader;
            Assert.True(entry.Revoked);
        }
    }

    [Fact]
    public void LookupRefusesARevokedEntry()
    {
        var table = new MemberTable();
        var entry = table.Issue(GuestPub, "Alice");
        Assert.Same(entry, MemberChannel.Lookup(table, entry.Key.Id));
        entry.MarkRevoked();
        Assert.Throws<CryptographicException>(() => MemberChannel.Lookup(table, entry.Key.Id));
    }

    private static async Task<(NetworkStream Client, NetworkStream Server, TcpClient A, TcpClient B)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var b = await accept;
        listener.Stop();
        return (a.GetStream(), b.GetStream(), a, b);
    }
}
