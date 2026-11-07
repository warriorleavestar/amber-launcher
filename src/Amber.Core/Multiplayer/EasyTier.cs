using Amber.Core.Localization;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Amber.Core.Net;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 联机用的 EasyTier（开源组网工具，LGPL-3.0，https://github.com/EasyTier/EasyTier）。
/// Amber 不内置它：第一次用联机时从 GitHub 官方发布页下载固定版本，校验 SHA-256 后解压到数据目录。
/// </summary>
public static class EasyTierRelease
{
    public const string Version = "2.6.4";
    private const string BaseUrl = "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/";
    public const string License = "LGPL-3.0";
    public const string ProjectUrl = "https://github.com/EasyTier/EasyTier";

    public sealed record Asset(string Platform, string Name, string Sha256, long Size, string CoreName, string CliName)
    {
        public string Url => BaseUrl + Name;
    }

    /// <summary>当前系统对应的官方命令行包。哈希和大小来自 EasyTier 的 GitHub Release。</summary>
    public static Asset Current => ForCurrentPlatform();

    internal static Asset For(string os, Architecture arch) => (os, arch) switch
    {
        ("windows", Architecture.X64) => A("windows-x86_64", "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84", 32_657_720, true),
        ("windows", Architecture.Arm64) => A("windows-arm64", "37023f8a3451c9234b17ee2089a03dc344ce90d803b5b359cb6c46682b0549b4", 30_136_892, true),
        ("windows", Architecture.X86) => A("windows-i686", "bf557daeccc5525d95b8a230c339d75554fb52d82d1e050e9a5202c92c02e09e", 30_881_261, true),
        ("macos", Architecture.X64) => A("macos-x86_64", "89fc28a6e6995259d76ce3f11775220e8a21c760e94df91a6a9db30a69b6982e", 33_733_545),
        ("macos", Architecture.Arm64) => A("macos-aarch64", "4be1882d1aa36d31c1d6ba0596f2cf8a097e371f8da124212324b2e0f8df7e4b", 30_174_017),
        ("linux", Architecture.X64) => A("linux-x86_64", "61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6", 25_506_523),
        ("linux", Architecture.Arm64) => A("linux-aarch64", "f533ec25a7ea714e09f645615012200278058525795cc3bb690ff011aec1a70f", 20_487_230),
        _ => throw new PlatformNotSupportedException(L.F("core.mp.component.unsupportedplatform", os, arch)),
    };

    private static Asset ForCurrentPlatform()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";
        return For(os, RuntimeInformation.OSArchitecture);
    }

    private static Asset A(string platform, string sha256, long size, bool exe = false)
    {
        var suffix = exe ? ".exe" : "";
        return new Asset(platform, $"easytier-{platform}-v{Version}.zip", sha256, size, "easytier-core" + suffix, "easytier-cli" + suffix);
    }

    /// <summary>
    /// 默认没有节点：EasyTier 官方文档没有给出固定的公共节点地址（安装脚本里写的 public.easytier.top 已经解析不到），
    /// Amber 也不提供自己的服务器。节点由用户添加：自己用仓库 node 文件夹的脚本搭，或者导入朋友的节点分享码。
    /// </summary>
    public static readonly string[] DefaultPeers = [];
}

public sealed class EasyTierInstaller(Downloader downloader, string dataDir)
{
    private EasyTierRelease.Asset Asset => EasyTierRelease.Current;
    public string InstallDir => Path.Combine(dataDir, "easytier", EasyTierRelease.Version, Asset.Platform);
    public string CorePath => Path.Combine(InstallDir, Asset.CoreName);
    public string CliPath => Path.Combine(InstallDir, Asset.CliName);
    /// <summary>WinDivert64.sys 是完整局域网模式转发游戏局域网广播要用的驱动文件，缺了也重新解压。</summary>
    public bool IsInstalled => File.Exists(CorePath) && File.Exists(CliPath) && (!OperatingSystem.IsWindows() || File.Exists(Path.Combine(InstallDir, "WinDivert64.sys")));

    public async Task EnsureAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled) return;
        var work = Path.Combine(dataDir, "easytier", "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, Asset.Name);
            await downloader.DownloadAllAsync([new DownloadItem(Asset.Url, zip, null, Asset.Size)], progress, ct);
            await using (var stream = File.OpenRead(zip))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (hash != Asset.Sha256)
                    throw new InvalidDataException(L.F("core.mp.component.hashfailed", hash));
            }
            Extract(zip, InstallDir, Asset);
            MakeExecutable(CorePath);
            MakeExecutable(CliPath);
            if (!IsInstalled) throw new InvalidDataException(L.T("core.mp.component.missingexecutable"));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>安装包里文件放在一层子文件夹下，拍平到安装目录。</summary>
    internal static void Extract(string zipPath, string target, EasyTierRelease.Asset asset)
    {
        var temp = target + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var name = Path.GetFileName(entry.FullName);
                if (name.Length == 0 || name.Contains("..")) continue;
                // 只要命令行程序、它们用的库和 WinDivert 驱动文件（完整局域网模式转发广播用）；网页控制台用不到。
                if (name != asset.CoreName && name != asset.CliName && name != "WinDivert64.sys"
                    && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".so", StringComparison.Ordinal)
                    && !name.EndsWith(".dylib", StringComparison.Ordinal)) continue;
                entry.ExtractToFile(Path.Combine(temp, name), true);
            }
        }
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(temp, target);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}

/// <summary>一次联机会话的参数。</summary>
public sealed record EasyTierOptions(
    RoomCode Room,
    bool IsHost,
    IReadOnlyList<string> Peers,
    int RpcPort,
    int? LocalForwardPort = null,
    IReadOnlyList<string>? Listeners = null,
    string? MachineId = null,
    bool Tun = false,
    int? LocalChatPort = null,
    bool AllowExit = false,
    string? ExitNode = null,
    IReadOnlyList<string>? ExtraArgs = null,
    IReadOnlyList<(int Local, int Remote)>? ExtraForwards = null)
{
    /// <summary>封装模式下加入方：本机这个端口经 EasyTier 转发到房主的封装端口（游戏和聊天都走它）。</summary>
    public int? LocalTunnelPort { get; init; }
    /// <summary>邀请房间的加入方：远程控制连房主用的本机端口（不建网卡时经 EasyTier 或封装通道转到房主的 47662）。</summary>
    public int? LocalRemotePort { get; init; }
    /// <summary>允许房间里的人经自己“仅游戏”转发（只开 SOCKS5，不用建网卡）。</summary>
    public bool AllowProxy { get; init; }
    /// <summary>“仅游戏”转发经过的成员（虚拟 IP）。</summary>
    public string? ProxyExit { get; init; }
    /// <summary>本机给游戏用的 SOCKS5 端口。</summary>
    public int? LocalSocksPort { get; init; }
    /// <summary>不建网卡时：本机这个端口经 EasyTier 转发到出口成员的封装 / SOCKS5 端口。</summary>
    public int? LocalExitPort { get; init; }
    /// <summary>直连房间的房主：对加入方提供测速端口。</summary>
    public bool ServesSpeedTest { get; init; }
    /// <summary>直连桥实际使用或正在探测的 IPv4 承载地址；消费出口时须先保护它们的原路由。</summary>
    public IReadOnlyList<string> DirectUnderlayAddresses { get; init; } = [];
    /// <summary>当前有 IPv6 直连承载；现有全局出口门禁只支持明确识别的 IPv4 承载。</summary>
    public bool DirectUnderlayUsesIpv6 { get; init; }
}

public static class EasyTierArgs
{
    /// <summary>房主固定用这个虚拟地址，加入方用同网段的随机地址。</summary>
    public const string HostIp = "10.233.144.1";
    public const string Subnet = "10.233.144.";

    /// <summary>
    /// 两边都用不建虚拟网卡的模式（--no-tun），不需要管理员权限，也不装驱动。
    /// 加入方把本机端口转发到房主虚拟地址上的游戏端口；房主那边由 EasyTier 把发往自己虚拟地址的连接交给本机端口。
    /// 默认不监听公网端口（--no-listener），避免弹防火墙；连不通时数据经中继节点转发。
    /// </summary>
    /// <summary>easytier-core 的环境变量名：房间密钥（2.6.4 的 --network-secret 也认这个环境变量）。</summary>
    public const string SecretVariable = "ET_NETWORK_SECRET";

    /// <summary>消费出口使用独立网卡名，守护只信任本次启动后新出现的 Wintun GUID。</summary>
    public static string DeviceName(EasyTierOptions options) => options.ExitNode is null ? "Amber LAN" : $"Amber LAN-{options.RpcPort}";

    /// <summary>经环境变量传给 easytier-core 的值。</summary>
    public static Dictionary<string, string> Environment(EasyTierOptions o) => new() { [SecretVariable] = o.Room.EasyTierSecret };

    public static List<string> Build(EasyTierOptions o)
    {
        // 房间密钥不放命令行（别的程序能读到进程命令行，开了命令行审计还会记进系统日志），经环境变量 ET_NETWORK_SECRET 传，见 Environment。
        var args = new List<string>
        {
            "--network-name", o.Room.EasyTierNetworkName,
            "--ipv4", o.IsHost ? HostIp : Subnet + JoinerHost(o.MachineId),
            "--rpc-portal", $"127.0.0.1:{o.RpcPort}",
            "--hostname", o.IsHost ? "amber-host" : "amber-guest",
            "--instance-name", $"amber-{(o.IsHost ? "host" : "guest")}-{o.RpcPort}",
            "--multi-thread",
            "--latency-first",
        };
        // 默认不建虚拟网卡；完整局域网模式才建（要管理员权限和 wintun）。
        if (!o.Tun) args.Add("--no-tun");
        // 完整局域网模式下转发本机游戏发出的 UDP 广播，很多游戏靠广播找局域网房间（EasyTier 的这个功能只在 Windows 上、要管理员权限，正好这个模式已经有）。
        else args.AddRange(["--dev-name", DeviceName(o), "--enable-udp-broadcast-relay"]);
        // 出口节点：两边都要用户明确打开。允许别人经自己上网的一方加 --enable-exit-node；
        // 要经别人上网的一方把所有流量导进虚拟网卡，所以必须是完整局域网模式。
        if (o.AllowExit) args.Add("--enable-exit-node");
        if (o.ExitNode is { } exit)
        {
            if (!o.Tun) throw new ArgumentException(L.T("core.mp.component.exitrequireslan"));
            if (!System.Net.IPAddress.TryParse(exit, out var ip) || !exit.StartsWith(Subnet, StringComparison.Ordinal)) throw new ArgumentException(L.F("core.mp.component.invalidexit", exit));
            args.AddRange(["--exit-nodes", ip.ToString()]);
            // --exit-nodes 只选择虚拟网内的下一跳，不会接管系统流量。
            // 两条 /1 优先于物理默认路由；提权守护在启动前保护承载，退出时随本次 TUN 释放。
            _ = ExitNetworkPlan.Create(o);
            args.AddRange(["--manual-routes", "0.0.0.0/1", "128.0.0.0/1"]);
            // DNS 由本次出口租约按接口快照恢复，不继承 EasyTier Magic DNS 的全局环境开关。
            args.AddRange(["--accept-dns", "false"]);
            // 回环桥模式不能让 EasyTier 在未保护的公网地址上另建 P2P 连接。
            if (!(o.ExtraArgs?.Contains("--disable-p2p") ?? false))
                args.Add("--disable-p2p");
        }
        if (o.MachineId is { } id) args.AddRange(["--machine-id", id]);
        foreach (var peer in o.Peers.Where(p => !string.IsNullOrWhiteSpace(p))) args.AddRange(["--peers", peer.Trim()]);
        if (o.Listeners is { Count: > 0 } listeners) foreach (var l in listeners) args.AddRange(["--listeners", l]);
        else args.Add("--no-listener");
        // EasyTier 默认把出站连接绑到物理网卡上，连本机回环地址会报 10049；节点在本机时（自测、或者节点和游戏在同一台电脑）关掉绑定。
        if (o.Peers.Concat(o.Listeners ?? []).Any(IsLoopback)) args.AddRange(["--bind-device", "false"]);
        else if (o.ExitNode is not null) args.AddRange(["--bind-device", "true"]);
        if (o.ExtraArgs is { } extra) args.AddRange(extra);
        // 不建网卡时，EasyTier 会把别的成员发到本机虚拟地址的连接交给本机同一端口——不限制的话，
        // 房间里的人（自己改 EasyTier 参数就行）能连到本机回环上的任何服务，包括 EasyTier 自己的管理端口。
        // 所以只放行这台电脑在这次联机里真正提供的端口；UDP 都不需要。完整局域网模式本来就是要互通，不限制。
        if (!o.Tun)
        {
            // 端口用逗号连在一个参数里（EasyTier 2.6.4 的写法）。
            args.AddRange(["--tcp-whitelist", string.Join(',', ServedPorts(o).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
            args.AddRange(["--udp-whitelist", "1"]);
            args.AddRange(["--rpc-portal-whitelist", "127.0.0.1/32"]);
        }
        if (!o.IsHost && !o.Tun && o.Room.Tunnel.Wraps)
        {
            // 封装模式：游戏和聊天都由 Amber 先封装，再经这一个转发连到房主的封装端口。
            var tunnel = o.LocalTunnelPort ?? throw new ArgumentException(L.T("core.mp.transport.join_port_required"));
            args.AddRange(["--port-forward", $"tcp://127.0.0.1:{tunnel}/{HostIp}:{Transport.TunnelSettings.Port}"]);
        }
        else if (!o.IsHost && !o.Tun)
        {
            var local = o.LocalForwardPort ?? throw new ArgumentException(L.T("core.mp.component.forwardrequired"));
            args.AddRange(["--port-forward", $"tcp://127.0.0.1:{local}/{HostIp}:{o.Room.Port}"]);
            // 房间聊天：本机另一个端口转发到房主的聊天服务。
            if (o.LocalChatPort is { } chat) args.AddRange(["--port-forward", $"tcp://127.0.0.1:{chat}/{HostIp}:{RoomChat.Port}"]);
            // 邀请房间的远程控制：本机另一个端口转发到房主的远程控制服务。
            if (o.LocalRemotePort is { } remote && o.Room.FromInvite) args.AddRange(["--port-forward", $"tcp://127.0.0.1:{remote}/{HostIp}:{RemoteControl.RemoteHub.Port}"]);
        }
        if (!o.Tun && o.ProxyExit is { } proxyExit)
        {
            if (!System.Net.IPAddress.TryParse(proxyExit, out _) || !proxyExit.StartsWith(Subnet, StringComparison.Ordinal)) throw new ArgumentException(L.F("core.mp.exit.invalid_virtual_ip", proxyExit));
            var exitLocal = o.LocalExitPort ?? throw new ArgumentException(L.T("core.mp.transport.exit_port_required"));
            var exitPort = o.Room.Tunnel.Wraps ? Transport.TunnelSettings.Port : Transport.TunnelSettings.PlainSocksPort;
            args.AddRange(["--port-forward", $"tcp://127.0.0.1:{exitLocal}/{proxyExit}:{exitPort}"]);
        }
        if (!o.IsHost)
        {
            foreach (var (local, remote) in o.ExtraForwards ?? []) args.AddRange(["--port-forward", $"tcp://127.0.0.1:{local}/{HostIp}:{remote}"]);
        }
        return args;
    }

    /// <summary>
    /// 这台电脑经 EasyTier 对房间提供的 TCP 端口。房主：封装模式只有封装端口，通用模式是游戏端口和聊天端口；直连房间再加测速端口；
    /// 允许别人借自己转发时加上转发端口。什么都不提供时放一个不会有人听的端口（列表不能空，空的等于不限制）。
    /// </summary>
    public static IReadOnlyList<int> ServedPorts(EasyTierOptions o)
    {
        var ports = new SortedSet<int>();
        if (o.IsHost)
        {
            if (o.Room.Tunnel.Wraps) ports.Add(Transport.TunnelSettings.Port);
            else
            {
                ports.Add(o.Room.Port);
                ports.Add(RoomChat.Port);
                if (o.Room.FromInvite) ports.Add(RemoteControl.RemoteHub.Port);
            }
            if (o.ServesSpeedTest) ports.Add(SpeedTestServer.Port);
        }
        if (o.AllowProxy) ports.Add(o.Room.Tunnel.Wraps ? Transport.TunnelSettings.Port : Transport.TunnelSettings.PlainSocksPort);
        if (ports.Count == 0) ports.Add(1);
        return [.. ports];
    }

    /// <summary>这次会话里本机的虚拟 IP。</summary>
    public static string VirtualIp(EasyTierOptions o) => o.IsHost ? HostIp : Subnet + JoinerHost(o.MachineId);

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");

    /// <summary>加入方地址的最后一段：2–254，按机器标识稳定地选，避免每次变。</summary>
    internal static int JoinerHost(string? machineId)
    {
        var seed = machineId ?? System.Environment.MachineName;
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return 2 + hash[0] % 253;
    }

    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>对端信息（easytier-cli peer 的输出）。</summary>
public sealed record PeerInfo(string Hostname, string Ip, string Cost, double? LatencyMs, string? Tunnel);

/// <summary>一次运行中的联机会话（普通模式或完整局域网模式）。</summary>
public interface IEasyTierSession : IDisposable
{
    EasyTierOptions Options { get; }
    bool HasExited { get; }
    IReadOnlyList<string> RecentOutput { get; }
    Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default);
}

/// <summary>运行中的 easytier-core。Dispose 时结束进程；Windows 上用作业对象保证 Amber 崩溃时它也会被关掉。</summary>
public sealed class EasyTierSession : IEasyTierSession
{
    private readonly Process _process;
    private readonly string _cliPath;
    private readonly int _rpcPort;
    private readonly IDisposable? _job;
    private readonly Queue<string> _output = new();

    public EasyTierOptions Options { get; }
    public bool HasExited => _process.HasExited;
    public event Action<string>? Output;

    private EasyTierSession(Process process, string cliPath, EasyTierOptions options, ProcessJob? job)
    {
        _process = process;
        _cliPath = cliPath;
        _rpcPort = options.RpcPort;
        _job = job;
        Options = options;
    }

    public IReadOnlyList<string> RecentOutput { get { lock (_output) return _output.ToList(); } }

    public static EasyTierSession Start(EasyTierInstaller installer, EasyTierOptions options, string workDir)
    {
        var (process, job) = Launch(installer.CorePath, EasyTierArgs.Build(options), EasyTierArgs.Environment(options), workDir);
        var session = new EasyTierSession(process, installer.CliPath, options, job);
        process.OutputDataReceived += (_, e) => session.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => session.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return session;
    }

    /// <summary>启动 easytier-core（密钥经环境变量传）；Windows 上放进作业对象。输出要调用方开始读。</summary>
    internal static (Process Process, ProcessJob? Job) Launch(string corePath, IEnumerable<string> args, IReadOnlyDictionary<string, string> environment, string workDir)
    {
        Directory.CreateDirectory(workDir);
        EasyTierProcessTracker.Cleanup(workDir, corePath);
        var info = new ProcessStartInfo(corePath)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (name, value) in environment) info.Environment[name] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException(L.T("core.mp.component.startfailed"));
        EasyTierProcessTracker.Track(process, workDir, corePath);
        ProcessJob? job = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                job = new ProcessJob();
                job.Add(process);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (OperatingSystem.IsWindows()) job?.Dispose();
                job = null;
            }
        }
        return (process, job);
    }

    private void OnLine(string? line)
    {
        if (line is null) return;
        lock (_output)
        {
            _output.Enqueue(line);
            if (_output.Count > 200) _output.Dequeue();
        }
        Output?.Invoke(line);
    }

    /// <summary>用 easytier-cli 查对端列表。查询失败返回空列表。</summary>
    public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => QueryPeersAsync(_cliPath, _rpcPort, ct);

    internal static async Task<List<PeerInfo>> QueryPeersAsync(string cliPath, int rpcPort, CancellationToken ct)
    {
        var text = await RunCliAsync(cliPath, ["-p", $"127.0.0.1:{rpcPort}", "-o", "json", "peer"], ct);
        return text is null ? [] : ParsePeers(text);
    }

    internal static List<PeerInfo> ParsePeers(string json)
    {
        var result = new List<PeerInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var array = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("peers", out var p) ? p : default;
            if (array.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in array.EnumerateArray())
            {
                var latency = Str(e, "lat_ms") ?? Str(e, "latency_ms");
                result.Add(new PeerInfo(
                    Str(e, "hostname") ?? "",
                    Str(e, "ipv4") ?? Str(e, "ip") ?? "",
                    Str(e, "cost") ?? "",
                    double.TryParse(latency, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null,
                    Str(e, "tunnel_proto") ?? Str(e, "tunnel")));
            }
        }
        catch (JsonException)
        {
            // 格式不认识就当没有对端。
        }
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static async Task<string?> RunCliAsync(string cliPath, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(cliPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var text = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
            _process.WaitForExit(3000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
        _job?.Dispose();
    }
}

/// <summary>大厅的参数：只为敲门存在的第二个 EasyTier 网络（名字和密钥从邀请码里的大厅密钥派生）。</summary>
/// <param name="ListenUdpPort">房主：在本机回环的这个 UDP 端口上监听（直连桥把还没批准的加入方转发到这里）。不设时不监听。</param>
public sealed record LobbyOptions(string Name, string Secret, bool IsHost, IReadOnlyList<string> Peers, int RpcPort, int? LocalKnockPort = null, int? ListenUdpPort = null)
{
    public override string ToString() => "LobbyOptions";
}

/// <summary>
/// 大厅的 easytier-core 参数。和房间的实例完全分开：另一个网段（10.234.0.0/24，房主固定 .1）、另一个管理端口，不建网卡、不监听公网端口。
/// 房主只放行敲门端口 47660（不建网卡时 EasyTier 把发往房主大厅地址的连接交给本机 127.0.0.1:47660）；加入方什么都不放行，只把本机一个端口转发到房主的敲门端口。
/// </summary>
public static class LobbyArgs
{
    public const string HostIp = "10.234.0.1";
    public const string Subnet = "10.234.0.";

    public static Dictionary<string, string> Environment(LobbyOptions o) => new() { [EasyTierArgs.SecretVariable] = o.Secret };

    public static List<string> Build(LobbyOptions o)
    {
        var args = new List<string>
        {
            "--network-name", o.Name,
            "--ipv4", o.IsHost ? HostIp : Subnet + RandomNumberGenerator.GetInt32(2, 255),
            "--rpc-portal", $"127.0.0.1:{o.RpcPort}",
            "--hostname", o.IsHost ? "jay-lobby-host" : "jay-lobby-guest",
            "--instance-name", $"jay-lobby-{(o.IsHost ? "host" : "guest")}-{o.RpcPort}",
            // 和房间的实例用不同的机器标识，免得在节点上互相顶掉。
            "--machine-id", Guid.NewGuid().ToString(),
            "--no-tun",
            "--latency-first",
        };
        // 直连房间的房主：只在本机回环上监听，给直连桥用；其他情况不监听。
        if (o.IsHost && o.ListenUdpPort is { } listen) args.AddRange(["--listeners", $"udp://127.0.0.1:{listen.ToString(System.Globalization.CultureInfo.InvariantCulture)}"]);
        else args.Add("--no-listener");
        foreach (var peer in o.Peers.Where(p => !string.IsNullOrWhiteSpace(p))) args.AddRange(["--peers", peer.Trim()]);
        if ((o.IsHost && o.ListenUdpPort is not null) || o.Peers.Any(p => Uri.TryCreate(p.Trim(), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost")))
            args.AddRange(["--bind-device", "false"]);
        args.AddRange(["--tcp-whitelist", o.IsHost ? KnockProtocol.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1"]);
        args.AddRange(["--udp-whitelist", "1"]);
        args.AddRange(["--rpc-portal-whitelist", "127.0.0.1/32"]);
        if (!o.IsHost)
        {
            var local = o.LocalKnockPort ?? throw new ArgumentException(L.T("core.mp.component.forwardrequired"));
            args.AddRange(["--port-forward", $"tcp://127.0.0.1:{local}/{HostIp}:{KnockProtocol.Port}"]);
        }
        return args;
    }
}

/// <summary>运行中的大厅 easytier-core。</summary>
public sealed class EasyTierLobby : IDisposable
{
    private readonly Process _process;
    private readonly IDisposable? _job;
    private readonly string _cliPath;
    private readonly Queue<string> _output = new();

    public LobbyOptions Options { get; }
    public bool HasExited => _process.HasExited;
    public event Action<string>? Output;

    private EasyTierLobby(Process process, ProcessJob? job, string cliPath, LobbyOptions options)
    {
        _process = process;
        _job = job;
        _cliPath = cliPath;
        Options = options;
    }

    public IReadOnlyList<string> RecentOutput { get { lock (_output) return _output.ToList(); } }

    public static EasyTierLobby Start(EasyTierInstaller installer, LobbyOptions options, string workDir)
    {
        var (process, job) = EasyTierSession.Launch(installer.CorePath, LobbyArgs.Build(options), LobbyArgs.Environment(options), workDir);
        var lobby = new EasyTierLobby(process, job, installer.CliPath, options);
        process.OutputDataReceived += (_, e) => lobby.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => lobby.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return lobby;
    }

    private void OnLine(string? line)
    {
        if (line is null) return;
        lock (_output)
        {
            _output.Enqueue(line);
            if (_output.Count > 200) _output.Dequeue();
        }
        Output?.Invoke(line);
    }

    public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => EasyTierSession.QueryPeersAsync(_cliPath, Options.RpcPort, ct);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
            _process.WaitForExit(3000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
        _job?.Dispose();
    }
}
