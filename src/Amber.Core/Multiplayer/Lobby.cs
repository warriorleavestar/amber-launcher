using System.Net;
using System.Net.Sockets;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 房主的大厅：只在至少有一张还能用的邀请时运行（第二个 EasyTier 实例 + 敲门服务）；邀请都用完、过期或撤销后马上关掉，并丢掉大厅密钥。
/// </summary>
public sealed class HostLobby : IAsyncDisposable
{
    private readonly Func<LobbyOptions, IDisposable> _startLobby;
    private readonly Func<IReadOnlyList<string>> _peers;
    private readonly Func<InviteBook, KnockServer> _startKnock;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _timer;
    private IDisposable? _lobby;
    /// <summary>当前大厅是按哪个网络名开的（邀请簿换了大厅密钥时用来发现旧大厅已经没用了）。</summary>
    private string? _openName;
    private KnockServer? _knock;
    private bool _disposed;

    public InviteBook Book { get; }

    /// <summary>大厅运行状态变了（开了 / 关了）。</summary>
    public event Action? StateChanged;
    /// <summary>批准了一个人、发了他的钥匙（发出凭据前就触发，对方不确认也算进了房间）。</summary>
    public event Action<KnockRequest, MemberEntry>? Issued;
    /// <summary>对方确认收到了凭据。</summary>
    public event Action<KnockRequest>? Admitted;
    /// <summary>失败的敲门太多，暂停 60 秒。</summary>
    public event Action? Paused;

    public bool IsOpen => _lobby is not null;

    /// <param name="startLobby">启动大厅的 EasyTier（测试里可以换掉）。</param>
    /// <param name="startKnock">在本机开敲门服务（测试里换端口）。</param>
    public HostLobby(Func<LobbyOptions, IDisposable> startLobby, Func<IReadOnlyList<string>> peers, Func<InviteBook, KnockServer> startKnock,
        Action<string>? log = null, InviteBook? book = null)
    {
        _startLobby = startLobby;
        _peers = peers;
        _startKnock = startKnock;
        _log = log;
        Book = book ?? new InviteBook();
        Book.Changed += () => _ = UpdateQuietlyAsync();
        _timer = new Timer(_ => Book.Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>用真的 EasyTier 和 47660 端口的房主大厅。</summary>
    public static HostLobby Create(EasyTierInstaller installer, string workDir, Func<IReadOnlyList<string>> peers, X25519KeyPair identity,
        Func<RoomCredentials> credentials, Func<byte[], bool> isTrusted, Func<KnockRequest, CancellationToken, Task<bool>> approve, MemberTable members, Action<string>? log = null)
    {
        HostLobby? lobby = null;
        lobby = new HostLobby(
            options => EasyTierLobby.Start(installer, options, workDir),
            peers,
            // 不建网卡时 EasyTier 把发往房主大厅地址 10.234.0.1:47660 的连接交给本机 127.0.0.1:47660。
            book => new KnockServer(book, identity, credentials, isTrusted, approve, IPAddress.Loopback, KnockProtocol.Port, log, members: members),
            log);
        return lobby;
    }

    /// <summary>按邀请簿的状态开关大厅。</summary>
    public async Task UpdateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            var pending = Book.HasPending;
            var key = Book.Lobby;
            // 最后一次用掉时，那个人可能还在等房主批准、还没收到凭据（连接就走在大厅里）：等进行中的敲门都结束再关。
            // 大厅密钥换了也要关掉重开：上一张邀请用完、还有人在等批准时又发了新邀请，邀请簿已经换了新的大厅密钥，旧大厅里没人能用新邀请。
            if ((_lobby is not null || _knock is not null) && (_knock?.Active ?? 0) == 0 && (!pending || key?.Name != _openName))
            {
                await CloseCoreAsync();
                _log?.Invoke(L.T("core.mp.lobby.closed"));
                StateChanged?.Invoke();
            }
            if (pending && _lobby is null && key is { } lobby)
            {
                try
                {
                    _knock = _startKnock(Book);
                    _knock.Issued += (r, m) => Issued?.Invoke(r, m);
                    _knock.Admitted += r => Admitted?.Invoke(r);
                    _knock.Paused += () => Paused?.Invoke();
                    _knock.SessionEnded += () => _ = UpdateQuietlyAsync();
                    _lobby = _startLobby(new LobbyOptions(lobby.Name, lobby.Secret, true, _peers(), EasyTierArgs.FreeTcpPort()));
                    _openName = lobby.Name;
                    _log?.Invoke(L.T("core.mp.lobby.opened"));
                }
                catch (Exception e) when (e is SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _log?.Invoke(L.F("core.mp.lobby.failed", e.Message));
                    await CloseCoreAsync();
                    throw;
                }
                StateChanged?.Invoke();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>邀请簿变化时在后台更新；开不起来的原因已经记进日志，状态由 IsOpen 反映。</summary>
    private async Task UpdateQuietlyAsync()
    {
        try { await UpdateAsync(); }
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
    }

    private async Task CloseCoreAsync()
    {
        _lobby?.Dispose();
        _lobby = null;
        _openName = null;
        if (_knock is { } knock)
        {
            _knock = null;
            await knock.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await _timer.DisposeAsync();
            await CloseCoreAsync();
            Book.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>加入方：进大厅、等看到房主、敲门，拿到结果后马上离开大厅。</summary>
public static class LobbyJoin
{
    /// <summary>进大厅后最多等这么久看到房主。</summary>
    public static readonly TimeSpan FindHostTimeout = TimeSpan.FromSeconds(60);

    public static async Task<KnockOutcome> KnockAsync(EasyTierInstaller installer, string workDir, IReadOnlyList<string> peers, JayInvite invite,
        X25519KeyPair me, string name, string version, Action<string>? log, Action? hostFound, CancellationToken ct)
    {
        var localPort = EasyTierArgs.FreeTcpPort();
        var options = new LobbyOptions(LobbyKeys.LobbyName(invite.LobbyKey), LobbyKeys.LobbySecret(invite.LobbyKey), false, peers, EasyTierArgs.FreeTcpPort(), localPort);
        using var lobby = EasyTierLobby.Start(installer, options, workDir);
        if (log is not null) lobby.Output += line => log(line);
        // 先等大厅里看到房主再敲：没连上时本机转发端口会接了又断，白白算作一次失败。
        using (var find = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            find.CancelAfter(FindHostTimeout);
            while (true)
            {
                if (lobby.HasExited) throw new IOException(L.F("core.mp.lobby.exited", lobby.RecentOutput.LastOrDefault() ?? ""));
                var peersNow = await lobby.PeersAsync(find.Token);
                if (peersNow.Any(p => p.Ip == LobbyArgs.HostIp && p.Cost != "Local")) break;
                try { await Task.Delay(1000, find.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(L.T("core.mp.lobby.nohost")); }
            }
        }
        hostFound?.Invoke();
        // EasyTier 刚看到对方时转发偶尔还没就绪：连接被立刻断开（握手还没开始）就再试几次。
        for (var attempt = 0; ; attempt++)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, localPort, ct);
                return await KnockClient.KnockAsync(client.GetStream(), invite, me, name, version, ct);
            }
            catch (Exception e) when (attempt < 2 && e is IOException or SocketException && !ct.IsCancellationRequested)
            {
                log?.Invoke(L.F("core.mp.knock.failed", e.Message));
                await Task.Delay(2000, ct);
            }
        }
    }
}
