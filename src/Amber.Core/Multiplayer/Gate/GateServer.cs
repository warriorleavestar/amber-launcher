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
    private EntryListeners? _listeners;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<IPAddress, int> _handshaking = new();
    private Task? _monitor;

    /// <summary>固定端口（Start 之后才有）。</summary>
    public int Port => _listeners?.FixedPort ?? throw new InvalidOperationException();
    /// <summary>现在开着的轮换端口。</summary>
    public IReadOnlyList<int> RotatingPorts => _listeners?.RotatingPorts ?? [];
    public bool FixedPortOpen => _listeners?.FixedOpen ?? false;
    public int Online;
    public int Rejected;

    public GateServer(GateStore store, GateConfig config, X25519KeyPair serverKey, Action<string> log)
    {
        _store = store;
        _config = config;
        _serverKey = serverKey;
        _log = log;
        _slots = new SemaphoreSlim(config.MaxConnections);
    }

    public void Start()
    {
        _listeners = new EntryListeners(_config.ListenAddress, _config.ListenPort, OnAccepted, line => _log(L.F("core.gate.log.listen_failed", line)));
        _epoch = _store.Read().Epoch;
        SyncListeners(_store.Read());
        _monitor = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    private void OnAccepted(TcpClient client)
    {
        if (_cts.IsCancellationRequested || !_slots.Wait(0))
        {
            Pump.Abort(client);
            client.Dispose();
            return;
        }
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await HandleAsync(client, ct); }
            finally { _slots.Release(); }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 入口端口轮换：普通模式下开了轮换就关掉固定端口，只听当前前后三个时间窗的端口。
    /// 隐私+/++ 下玩家连的是节点，网关的固定端口留给节点转发（用防火墙只放行节点），轮换交给节点去做。
    /// 从隐私+/++ 降回普通模式时，节点还在转发到固定端口：配着节点就让固定端口一直开着，但过渡期后只收节点转来的连接（见 <see cref="FixedPortClosedTo"/>）。
    /// 这样还拿着节点列表的老玩家照样经节点进一次服，顺便拿到新的地址簿，之后改走网关的轮换端口。
    /// </summary>
    private void SyncListeners(GateState state)
    {
        if (_listeners is null) return;
        var rotation = _store.Rotation(state);
        if (rotation is not null && !state.Privacy.IsSplit())
        {
            var seed = GateRotation.EntrySeed(rotation.Value.Master, GateRotation.DirectEntry);
            _listeners.SetRotating([.. rotation.Value.Rule.CandidatePorts(seed, DateTimeOffset.UtcNow)]);
            _listeners.SetFixedOpen(GateStore.FixedPortWanted(state) || state.Relays.Count > 0);
        }
        else
        {
            _listeners.SetFixedOpen(true);
            _listeners.SetRotating([]);
        }
    }

    /// <summary>普通模式开着轮换、过渡期也过了：固定端口只为节点开着，别的来源连到固定端口一律直接断开。</summary>
    private bool FixedPortClosedTo(TcpClient client, GateState state, bool fromRelay) =>
        !fromRelay && !state.Privacy.IsSplit() && _store.Rotation(state) is not null && !GateStore.FixedPortWanted(state)
        && _listeners is { } listeners && (client.Client.LocalEndPoint as IPEndPoint)?.Port == listeners.FixedPort;

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6() ?? IPAddress.IPv6None;
            Pump.Tune(client);
            var state = _store.Read();
            var plus = state.Privacy.IsSplit();
            var fromRelay = state.Relays.Count > 0 && RelaySources(state).Contains(remote);
            if (FixedPortClosedTo(client, state, fromRelay))
            {
                Interlocked.Increment(ref Rejected);
                Pump.Abort(client);
                return;
            }
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
                    var accepted = await GateProtocol.AcceptAsync(stream, _serverKey, _store.AccessKey(), _store.ActiveInvites(), timeout.Token, _store.RelayPsks());
                    if (accepted is null)
                    {
                        Interlocked.Increment(ref Rejected);
                        Pump.Abort(client);
                        return;
                    }
                    secure = accepted.Stream;
                    device = accepted.DevicePublicKey;
                    var fingerprint = MemberIdentity.FingerprintOf(device);
                    if (accepted.RelayId is { } relayId)
                    {
                        Release(remote);
                        remote = IPAddress.None;
                        var channel = secure;
                        secure = null;
                        await HandleRelayAsync(relayId, channel, device, ct);
                        return;
                    }
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
                    if (BreakerOpen)
                    {
                        await GateProtocol.SendJoinResultAsync(secure, GateStatus.CircuitOpen, timeout.Token, network);
                        await secure.CloseAsync(timeout.Token);
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
                // 配对和读登录包期间刚好熔断了：不再接进服务端。
                if (BreakerOpen) throw new IOException(GateRejectedException.Describe(GateStatus.CircuitOpen));
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
                var sessionId = Interlocked.Increment(ref _sessionIds);
                var live = session!;
                _sessions[sessionId] = new LiveSession(device, name, () =>
                {
                    (live as IAuthenticatedStream)?.Abort();
                    Pump.Abort(backend);
                    try { backend.Close(); } catch (ObjectDisposedException) { }
                });
                try
                {
                    if (_epoch != _store.Read().Epoch || BreakerOpen) throw new IOException(GateRejectedException.Describe(GateStatus.CircuitOpen));
                    await Pump.RunAsync(live, backendStream, ct, () => Pump.Abort(backend));
                }
                finally
                {
                    _sessions.TryRemove(sessionId, out _);
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

    // ---------------- 隐私++：节点心跳和熔断 ----------------

    /// <summary>一条进了服务端的游戏连接：哪台设备、用哪个名字（状态查询没有名字），以及怎么把它断开。</summary>
    private sealed record LiveSession(byte[]? Device, string? Name, Action Abort);

    private readonly ConcurrentDictionary<long, LiveSession> _sessions = new();
    private long _sessionIds;
    private readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();
    private readonly ConcurrentDictionary<string, SecureStream> _relayChannels = new();
    private volatile bool _breakerOpen;
    private int _epoch;
    private DateTime _healthySince = DateTime.MinValue;
    private HashSet<string> _healthyLast = [];

    /// <summary>隐私++ 熔断中：断开了全部游戏连接，暂停接受新连接。</summary>
    public bool BreakerOpen => _breakerOpen;
    public int ActiveSessions => _sessions.Count;
    /// <summary>熔断状态变了（true = 熔断，false = 恢复）。第二个参数是原因（哪些节点不可用）。运行程序拿去执行 save-all。</summary>
    public event Action<bool, string>? BreakerChanged;
    /// <summary>熔断后要连续健康多久才恢复（防止节点时好时坏反复打扰玩家）。</summary>
    public TimeSpan RecoverDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>不为 null 时覆盖 state.json 里的心跳超时（测试用）。</summary>
    public TimeSpan? HeartbeatTimeoutOverride { get; set; }
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>运行状态写到数据目录的 health.json，给 jay-server status 看。</summary>
    public const string HealthFile = "health.json";

    /// <summary>节点的心跳通道：核对（第一次记下）节点公钥，回 Ok，然后一直收心跳。</summary>
    private async Task HandleRelayAsync(string relayId, SecureStream channel, byte[] relayKey, CancellationToken ct)
    {
        await using (channel)
        {
            try
            {
                if (!_store.CheckRelayKey(relayId, relayKey))
                {
                    await GateProtocol.WriteRecordAsync(channel, [(byte)GateStatus.NotMember], ct);
                    await channel.CloseAsync(ct);
                    _log(L.F("core.gate.log.relay_wrong_key", relayId, MemberIdentity.FingerprintOf(relayKey)));
                    return;
                }
                await GateProtocol.WriteRecordAsync(channel, [(byte)GateStatus.Ok], ct);
                if (_relayChannels.TryRemove(relayId, out var old)) old.Abort();
                _pushed.TryRemove(relayId, out _);
                _relayChannels[relayId] = channel;
                await PushRotationAsync(relayId, channel, _store.Read(), ct);
                ulong last = 0;
                var timeout = HeartbeatTimeout(_store.Read());
                while (!ct.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(timeout * 2);
                    var (counter, time) = GateProtocol.ParseHeartbeat(await GateProtocol.ReadRecordAsync(channel, wait.Token));
                    // 通道本身防重放；序号再保证不倒退，时间差太远（节点时钟坏了）就不算数。
                    if (counter <= last) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
                    last = counter;
                    if ((DateTimeOffset.UtcNow - time).Duration() > TimeSpan.FromMinutes(5)) continue;
                    _heartbeats[relayId] = DateTime.UtcNow;
                }
            }
            catch (Exception e) when (e is CryptographicException or IOException or SocketException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
            {
                channel.Abort();
            }
            finally
            {
                _relayChannels.TryRemove(new KeyValuePair<string, SecureStream>(relayId, channel));
            }
        }
    }

    private readonly ConcurrentDictionary<string, byte[]> _pushed = new();

    /// <summary>节点的入口设置：[1][固定端口开不开][有没有轮换][规则 7 字节][这个节点的种子]。变了才重发。</summary>
    internal static byte[] RelaySettings(bool fixedOpen, GateEntryRotation? rotation)
    {
        var output = new List<byte> { 1, fixedOpen ? (byte)1 : (byte)0, rotation is null ? (byte)0 : (byte)1 };
        if (rotation is not null)
        {
            rotation.Rotation.Write(output);
            output.AddRange(rotation.Seed);
        }
        return [.. output];
    }

    private async Task PushRotationAsync(string relayId, SecureStream channel, GateState state, CancellationToken ct)
    {
        var record = RelaySettings(GateStore.FixedPortWanted(state), _store.EntryRotation(relayId, state));
        if (_pushed.TryGetValue(relayId, out var last) && last.AsSpan().SequenceEqual(record)) return;
        _pushed[relayId] = record;
        await GateProtocol.WriteRecordAsync(channel, record, ct);
    }

    private void PushRotation(GateState state)
    {
        foreach (var (id, channel) in _relayChannels)
            _ = Task.Run(async () =>
            {
                try { await PushRotationAsync(id, channel, state, _cts.Token); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or System.Net.Sockets.SocketException) { channel.Abort(); }
            });
    }

    private TimeSpan HeartbeatTimeout(GateState state) => HeartbeatTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(3, state.HeartbeatTimeoutSeconds));

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                var state = _store.Read();
                Check(state);
                SyncListeners(state);
                PushRotation(state);
                if (tick++ % 5 == 0) WriteHealth();
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // 状态文件正被管理命令改写：下一秒再看。
            }
        }
    }

    /// <summary>每秒看一次：纪元号变了（吊销了节点）就全员断开；隐私++ 下健康节点不够就熔断，连续健康一段时间后恢复。</summary>
    internal void Check(GateState state)
    {
        var now = DateTime.UtcNow;
        // 被吊销或换了节点码的节点：它的心跳通道立刻断掉。
        var known = state.Relays.Where(r => r.HeartbeatPsk is not null).Select(r => r.Address).ToHashSet();
        foreach (var (id, channel) in _relayChannels)
            if (!known.Contains(id) || state.Relays.First(r => r.Address == id).PublicKey is null)
            {
                channel.Abort();
                _relayChannels.TryRemove(new KeyValuePair<string, SecureStream>(id, channel));
                _heartbeats.TryRemove(id, out _);
            }
        if (state.Epoch != _epoch)
        {
            _epoch = state.Epoch;
            var n = AbortAll();
            _log(L.F("core.gate.log.epoch", _epoch, n));
        }
        // 撤销、停用或移出名单的设备：已经在线的连接也马上断开，不等它下次重连。
        foreach (var (id, live) in _sessions)
            if (live is { Device: { } device, Name: { } name } && !StillAllowed(state, name, device) && _sessions.TryRemove(id, out _))
            {
                live.Abort();
                _log(L.F("core.gate.log.revoked_dropped", name, MemberIdentity.FingerprintOf(device)));
            }
        var timeout = HeartbeatTimeout(state);
        var healthy = known.Where(id => _heartbeats.TryGetValue(id, out var t) && now - t < timeout).ToHashSet();
        foreach (var id in healthy.Except(_healthyLast)) _log(L.F("core.gate.log.relay_up", id));
        foreach (var id in _healthyLast.Except(healthy)) _log(L.F("core.gate.log.relay_down", id));
        _healthyLast = healthy;

        var enough = healthy.Count >= Math.Max(2, state.MinHealthyRelays);
        if (state.Privacy != PrivacyLevel.PlusPlus || enough)
        {
            if (_healthySince == DateTime.MinValue) _healthySince = now;
        }
        else _healthySince = DateTime.MinValue;

        if (!_breakerOpen && state.Privacy == PrivacyLevel.PlusPlus && !enough)
        {
            _breakerOpen = true;
            var down = string.Join(", ", known.Except(healthy).DefaultIfEmpty("-"));
            var n = AbortAll();
            _log(L.F("core.gate.log.breaker_open", down, n));
            WriteHealth();
            BreakerChanged?.Invoke(true, down);
        }
        else if (_breakerOpen && (state.Privacy != PrivacyLevel.PlusPlus || _healthySince != DateTime.MinValue && now - _healthySince >= RecoverDelay))
        {
            _breakerOpen = false;
            _log(L.T("core.gate.log.breaker_closed"));
            WriteHealth();
            BreakerChanged?.Invoke(false, "");
        }
    }

    private static bool StillAllowed(GateState state, string name, byte[] device)
    {
        var key = Convert.ToBase64String(device);
        return state.Members.Any(m => !m.Disabled && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) && m.Devices.Any(d => d.PublicKey == key));
    }

    /// <summary>断开全部游戏连接（不发正常结束：对面按出错处理，重新握手时是新密钥）。</summary>
    private int AbortAll()
    {
        var n = 0;
        foreach (var (id, abort) in _sessions)
            if (_sessions.TryRemove(id, out _))
            {
                abort.Abort();
                n++;
            }
        return n;
    }

    private void WriteHealth()
    {
        var now = DateTime.UtcNow;
        var health = new GateHealth
        {
            Updated = DateTimeOffset.UtcNow,
            BreakerOpen = _breakerOpen,
            Epoch = _epoch,
            Online = Online,
            Relays = _store.Read().Relays.Select(r => new GateHealth.Relay
            {
                Address = r.Address,
                LastHeartbeat = _heartbeats.TryGetValue(r.Address, out var t) ? new DateTimeOffset(t, TimeSpan.Zero) : null,
                Connected = _relayChannels.ContainsKey(r.Address),
            }).ToList(),
        };
        _store.WriteHealth(health);
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
        if (_listeners is not null) await _listeners.DisposeAsync();
        if (_monitor is not null)
            try { await _monitor; } catch (OperationCanceledException) { }
        foreach (var channel in _relayChannels.Values) channel.Abort();
        _cts.Dispose();
    }
}
