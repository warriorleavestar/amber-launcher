using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>最后一张邀请用完、那个人还在等批准时房主又发了新邀请：旧大厅结束后要按新的大厅密钥重开，新邀请才能用。</summary>
public sealed class HostLobbyRekeyTests
{
    private sealed class FakeLobby(List<LobbyOptions> started, LobbyOptions options) : IDisposable
    {
        public void Dispose() { lock (started) started.Remove(options); }
    }

    [Fact]
    public async Task NewInviteWhileLastKnockWaitsGetsWorkingLobby()
    {
        var running = new List<LobbyOptions>();
        using var host = X25519KeyPair.Generate();
        using var guest = X25519KeyPair.Generate();
        var credentials = RoomCredentials.Create(host.PublicKey, 25565, TunnelSettings.Default);
        var decision = new TaskCompletionSource<bool>();
        var asked = new TaskCompletionSource();
        KnockServer? knock = null;
        await using var lobby = new HostLobby(o => { lock (running) running.Add(o); return new FakeLobby(running, o); }, () => ["tcp://127.0.0.1:1"],
            book => knock = new KnockServer(book, host, () => credentials, _ => false, (_, _) => { asked.TrySetResult(); return decision.Task; }, IPAddress.Loopback, 0));
        var first = lobby.Book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        for (var i = 0; i < 100 && !lobby.IsOpen; i++) await Task.Delay(50);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, knock!.Port);
        var knocking = KnockClient.KnockAsync(client.GetStream(), first.Invite, guest, "A", "1");
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 房主在等批准的时候又发了一张邀请。
        var second = lobby.Book.Create(1, TimeSpan.FromMinutes(10), false, TunnelSettings.Default);
        decision.SetResult(true);
        Assert.True((await knocking).Approved);
        for (var i = 0; i < 100; i++)
        {
            lock (running) if (running.Count == 1 && running[0].Name == LobbyKeys.LobbyName(second.Invite.LobbyKey)) return;
            await Task.Delay(50);
        }
        lock (running) Assert.Fail($"running={running.Count} names=[{string.Join(",", running.Select(r => r.Name))}] want {LobbyKeys.LobbyName(second.Invite.LobbyKey)}");
    }
}
