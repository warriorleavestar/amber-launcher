using Amber.Core.Localization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 网关守护进程的核心：在公网端口上接受 Jay 客户端，握手、核对名单和用户名，然后转给本机的 MC 服务端。
/// 认不出的连接（扫描器、psk 不对）一个字节都不回就断开。
/// </summary>
public sealed class GateServer : IAsyncDisposable
{
    private readonly GateStore _store;
    private readonly GateConfig _config;
    private readonly X25519KeyPair _serverKey;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<IPAddress, int> _handshaking = new();
    private Task? _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Online;
    public int Rejected;

    public GateServer(GateStore store, GateConfig config, X25519KeyPair serverKey, Action<string> log)
    {
        _store = store;
        _config = config;
        _serverKey = serverKey;
        _log = log;
        _slots = new SemaphoreSlim(config.MaxConnections);
        var address = IPAddress.Parse(config.ListenAddress);
        try
        {
            _listener = new TcpListener(address, config.ListenPort);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) _listener.Server.DualMode = true;
        }
        catch (SocketException) when (address.Equals(IPAddress.IPv6Any))
        {
            // 有的 VPS / 容器关掉了 IPv6：退回只听 IPv4。
            _listener = new TcpListener(IPAddress.Any, config.ListenPort);
        }
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            if (!_slots.Wait(0))
            {
                Pump.Abort(client);
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, ct); }
                finally { _slots.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6() ?? IPAddress.IPv6None;
            Pump.Tune(client);
            var state = _store.Read();
            var plus = state.Privacy == PrivacyLevel.Plus;
            var fromRelay = state.Relays.Count > 0 && RelaySources(state).Contains(remote);
            // 节点转来的连接共用节点的 IP，不按 IP 限握手数（总数仍受 MaxConnections 限制）。
            if (fromRelay) remote = IPAddress.None;
            else if (_handshaking.AddOrUpdate(remote, 1, (_, n) => n + 1) > _config.MaxHandshakesPerIp)
            {
                Release(remote);
                Pump.Abort(client);
                return;
            }
            Stream stream = new OwnedStream(client);
            SecureStream? secure = null;
            Stream? session = null;
            PendingLeg? partner = null;
            string? name = null;
            byte[]? device = null;
            McLogin.Result? login = null;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(GateProtocol.HandshakeTimeout);
                    var accepted = await GateProtocol.AcceptAsync(stream, _serverKey, _store.AccessKey(), _store.ActiveInvites(), timeout.Token);
                    if (accepted is null)
                    {
                        Interlocked.Increment(ref Rejected);
                        Pump.Abort(client);
                        return;
                    }
                    secure = accepted.Stream;
                    device = accepted.DevicePublicKey;
                    var fingerprint = MemberIdentity.FingerprintOf(device);
                    var network = accepted.Version >= 2 ? _store.Network(_config) : null;
                    if (accepted.IsEnroll)
                    {
                        var (outcome, bound) = _store.Redeem(accepted.InviteId!, device);
                        var status = outcome switch
                        {
                            RedeemOutcome.Ok => GateStatus.Ok,
                            RedeemOutcome.DeviceLimit => GateStatus.DeviceLimit,
                            RedeemOutcome.Disabled => GateStatus.Disabled,
                            _ => GateStatus.NotMember,
                        };
                        await GateProtocol.SendEnrollResultAsync(secure, status, status == GateStatus.Ok ? _store.AccessKey() : null, bound, timeout.Token, network);
                        await secure.CloseAsync(timeout.Token);
                        _log(status == GateStatus.Ok ? L.F("core.gate.log.enrolled", bound!, fingerprint) : L.F("core.gate.log.enroll_refused", outcome, fingerprint));
                        return;
                    }
                    var names = _store.AllowedNames(device);
                    if (names.Count == 0)
                    {
                        await GateProtocol.SendJoinResultAsync(secure, GateStatus.NotMember, timeout.Token, network);
                        await secure.CloseAsync(timeout.Token);
                        _log(L.F("core.gate.log.not_member", fingerprint));
                        return;
                    }
                    var split = accepted.Request is { Kind: JoinKind.SplitLeg };
                    // 隐私+：只收经节点转来的拆分连接。旧版客户端（v1）和直连都回“要拆分”，v2 客户端顺便拿到节点列表。
                    if (plus && !(split && fromRelay))
                    {
                        await GateProtocol.SendJoinResultAsync(secure, GateStatus.SplitRequired, timeout.Token, network);
                        await secure.CloseAsync(timeout.Token);
                        _log(L.F("core.gate.log.split_required", fingerprint));
                        return;
                    }
                    await GateProtocol.SendJoinResultAsync(secure, GateStatus.Ok, timeout.Token, network);
                    if (split)
                    {
                        Release(remote);
                        remote = IPAddress.None;
                        var leg = new PendingLeg(device, accepted.Request!.Index, secure);
                        var key = Convert.ToHexString(accepted.Request.PairId);
                        if (await PairAsync(key, leg, timeout.Token) is not { } other)
                        {
                            // 先到的腿：交给后到的那条去跑，自己等整条连接结束（占着连接名额）。
                            secure = null;
                            await leg.Done.Task;
                            return;
                        }
                        partner = other;
                        if (!CryptographicOperations.FixedTimeEquals(other.Device, device) || other.Index == leg.Index)
                        {
                            other.Stream.Abort();
                            throw new CryptographicException(L.T("core.gate.split.mismatch"));
                        }
                        session = leg.Index == 0 ? new SplitStream(secure, other.Stream) : new SplitStream(other.Stream, secure);
                        secure = null;
                    }
                    else
                    {
                        session = secure;
                        secure = null;
                    }
                    login = await McLogin.ReadAsync(session, timeout.Token);
                    if (login.Intent != McLogin.Intent.Status)
                    {
                        name = names.FirstOrDefault(n => string.Equals(n, login.Username, StringComparison.OrdinalIgnoreCase));
                        if (name is null)
                        {
                            await WriteLoginDisconnectAsync(session, L.F("core.gate.mc.name_not_bound", login.Username!), timeout.Token);
                            await session.FlushAsync(timeout.Token);
                            _log(L.F("core.gate.log.wrong_name", fingerprint, login.Username!, string.Join(", ", names)));
                            return;
                        }
                    }
                }
                Release(remote);
                remote = IPAddress.None;
                using var backend = new TcpClient();
                await backend.ConnectAsync(_config.BackendHost, _config.BackendPort, ct);
                Pump.Tune(backend);
                var backendStream = backend.GetStream();
                await backendStream.WriteAsync(login!.Raw, ct);
                if (name is not null)
                {
                    _store.Touch(name, device!);
                    Interlocked.Increment(ref Online);
                    _log(L.F(partner is null ? "core.gate.log.joined" : "core.gate.log.joined_split", name));
                }
                try
                {
                    await Pump.RunAsync(session!, backendStream, ct, () => Pump.Abort(backend));
                }
                finally
                {
                    if (name is not null)
                    {
                        Interlocked.Decrement(ref Online);
                        _log(L.F("core.gate.log.left", name));
                    }
                }
                session = null;
            }
            catch (Exception e) when (e is CryptographicException or IOException or SocketException or EndOfStreamException or OperationCanceledException or InvalidDataException)
            {
                secure?.Abort();
                (session as IAuthenticatedStream)?.Abort();
                Pump.Abort(client);
                if (device is not null) _log(L.F("core.gate.log.error", MemberIdentity.FingerprintOf(device), e.Message));
            }
            finally
            {
                if (!Equals(remote, IPAddress.None)) Release(remote);
                if (secure is not null) await secure.DisposeAsync();
                if (session is not null) await session.DisposeAsync();
                partner?.Done.TrySetResult();
            }
        }
    }

    /// <summary>拆分连接里等着配对的一条腿。</summary>
    private sealed class PendingLeg(byte[] device, byte index, SecureStream stream)
    {
        public byte[] Device { get; } = device;
        public byte Index { get; } = index;
        public SecureStream Stream { get; } = stream;
        public TaskCompletionSource<bool> Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, PendingLeg> _legs = new();
    public static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 配对：后到的腿拿走先到的那条并返回它；先到的腿登记后等着被拿走，被拿走返回 null。
    /// 10 秒没等到另一条腿就抛超时（连接断开）。
    /// </summary>
    private async Task<PendingLeg?> PairAsync(string key, PendingLeg leg, CancellationToken ct)
    {
        while (true)
        {
            if (_legs.TryRemove(key, out var other))
            {
                if (other.Claimed.TrySetResult(true)) return other;
                continue;
            }
            if (_legs.Count >= _config.MaxConnections) throw new IOException(L.T("core.gate.split.timeout"));
            if (_legs.TryAdd(key, leg)) break;
        }
        try
        {
            await leg.Claimed.Task.WaitAsync(PairTimeout, ct);
            return null;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            _legs.TryRemove(new KeyValuePair<string, PendingLeg>(key, leg));
            // 刚好在超时的同时被另一条腿拿走了：照样交出去。
            if (!leg.Claimed.TrySetResult(false) && leg.Claimed.Task.Result) return null;
            throw new IOException(L.T("core.gate.split.timeout"), e);
        }
    }

    private (GateState State, DateTime Resolved, HashSet<IPAddress> Addresses)? _relaySources;

    /// <summary>节点的来源 IP（填了 Source 用 Source，没填就解析地址里的主机名；一分钟重新解析一次）。</summary>
    private HashSet<IPAddress> RelaySources(GateState state)
    {
        var cached = _relaySources;
        if (cached is { } c && ReferenceEquals(c.State, state) && DateTime.UtcNow - c.Resolved < TimeSpan.FromMinutes(1)) return c.Addresses;
        var set = new HashSet<IPAddress>();
        foreach (var relay in state.Relays)
        {
            if (relay.Source is { } source && IPAddress.TryParse(source, out var ip))
            {
                set.Add(ip.MapToIPv6());
                continue;
            }
            if (GateEndpoint.TryParse(relay.Address) is not { } endpoint) continue;
            try
            {
                foreach (var a in Dns.GetHostAddresses(endpoint.Host)) set.Add(a.MapToIPv6());
            }
            catch (SocketException)
            {
                // 解析不了就先不认这个节点，下次再试。
            }
        }
        _relaySources = (state, DateTime.UtcNow, set);
        return set;
    }

    private void Release(IPAddress remote)
    {
        if (_handshaking.AddOrUpdate(remote, 0, (_, n) => n - 1) <= 0) _handshaking.TryRemove(new KeyValuePair<IPAddress, int>(remote, 0));
    }

    /// <summary>登录阶段的 Disconnect 包（id 0x00，原因是 JSON 文本组件，没压缩）：MC 客户端会把原因显示出来。</summary>
    internal static async Task WriteLoginDisconnectAsync(Stream stream, string reason, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject { ["text"] = reason }.ToJsonString());
        var body = new List<byte>();
        WriteVarInt(body, 0x00);
        WriteVarInt(body, json.Length);
        body.AddRange(json);
        var packet = new List<byte>();
        WriteVarInt(packet, body.Count);
        packet.AddRange(body);
        await stream.WriteAsync(packet.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static void WriteVarInt(List<byte> output, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            output.Add(b);
        } while (v != 0);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
