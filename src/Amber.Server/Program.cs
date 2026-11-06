using System.Globalization;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Net;

namespace Amber.Server;

/// <summary>
/// Jay 服务器版（jay-server）：在 VPS 或局域网里的一台电脑上开一个只能用 Jay 进的 MC 服务器。
/// 同一个程序既跑网关，也管 MC 服务端；管理命令（发邀请、撤销设备）可以在服务运行时另开一个窗口执行。
/// </summary>
public static class Program
{
    private const string Usage = """
        Jay 服务器版

        第一次用：
          jay-server init --host <玩家连的地址> [--port 47670]
              地址可以是 VPS 的公网 IP / 域名，也可以是局域网 IP（比如 192.168.1.20）
          jay-server setup [--version latest|1.21.1] [--memory 2048] [--accept-eula]
              下载官方服务端和合适的 Java；需要同意 Minecraft EULA（https://aka.ms/MinecraftEULA）
          jay-server run [--no-mc]
              启动网关和 MC 服务端。在窗口里直接输入 MC 控制台命令；以 jay 开头的是下面的管理命令

        管理（服务运行时也可以另开窗口执行）：
          jay-server invite <用户名> [--ttl 24h] [--uses 1]   生成邀请码，发给这个玩家
          jay-server list                                     名单和设备
          jay-server invites                                  还没用掉的邀请
          jay-server cancel-invite <编号>
          jay-server revoke <用户名> [<设备码前几位>]          撤销设备（不写设备码就撤销全部）
          jay-server disable <用户名> / enable <用户名>
          jay-server remove <用户名>
          jay-server online-mode on|off                        正版验证（默认 off：离线账号，由网关核对用户名）

        通用选项：--dir <数据目录>（默认是程序旁边的 jay-server-data，也可以用环境变量 JAY_SERVER_DIR）
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = Options.Parse(args);
        var root = options.Get("dir") ?? Environment.GetEnvironmentVariable("JAY_SERVER_DIR") ?? Path.Combine(AppContext.BaseDirectory, "jay-server-data");
        root = Path.GetFullPath(root);
        try
        {
            return options.Command switch
            {
                "init" => Init(root, options),
                "setup" => await SetupAsync(root, options),
                "run" => await RunAsync(root, options),
                null or "help" or "--help" or "-h" => Help(),
                _ => Admin(root, options, Console.Out),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("出错：" + e.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static GateStore Gate(string root)
    {
        var dir = Path.Combine(root, "gate");
        if (!GateStore.IsInitialized(dir)) throw new InvalidOperationException("还没初始化，先运行 jay-server init --host <地址>");
        return new GateStore(dir);
    }

    private static int Init(string root, Options options)
    {
        var host = options.Get("host") ?? throw new ArgumentException("要用 --host 指定玩家连的地址（公网 IP、域名或局域网 IP）");
        var port = options.GetInt("port", 47670);
        var listen = options.GetInt("listen-port", port);
        var store = GateStore.Initialize(Path.Combine(root, "gate"), host, port, listen);
        using var key = store.LoadServerKey();
        Console.WriteLine($"数据目录：{root}");
        Console.WriteLine($"网关地址：{host}:{port}（监听 {listen}）");
        Console.WriteLine($"服务器身份：{MemberIdentity.FingerprintOf(key.PublicKey)}");
        Console.WriteLine("下一步：jay-server setup --accept-eula");
        return 0;
    }

    private static async Task<int> SetupAsync(string root, Options options)
    {
        Gate(root);
        var host = new McServerHost(root);
        var memory = options.GetInt("memory", 2048);
        if (memory is < 512 or > 65536) throw new ArgumentException("--memory 要在 512–65536（MB）之间");
        if (!options.Has("accept-eula") && !host.EulaAccepted)
        {
            Console.Write("运行 MC 服务端需要同意 Minecraft EULA（https://aka.ms/MinecraftEULA）。同意请输入 yes：");
            if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("没有同意 EULA，已取消。");
                return 1;
            }
        }
        using var http = Downloader.CreateHttpClient();
        var downloader = new Downloader(http, SourceChain.Default());
        var settings = await host.SetupAsync(downloader, options.Get("version") ?? "latest", memory, Console.WriteLine);
        host.AcceptEula();
        Console.WriteLine($"MC {settings.Version} 准备好了（Java：{settings.JavaPath}，内存 {settings.MemoryMb} MB，只监听 127.0.0.1:{settings.Port}）。");
        Console.WriteLine("下一步：jay-server run，然后 jay-server invite <用户名>");
        return 0;
    }

    private static async Task<int> RunAsync(string root, Options options)
    {
        var store = Gate(root);
        var config = store.LoadConfig();
        var host = new McServerHost(root);
        var withMc = !options.Has("no-mc");
        if (withMc)
        {
            if (!host.IsSetUp) throw new InvalidOperationException("MC 服务端还没准备好，先运行 jay-server setup");
            if (!host.EulaAccepted) throw new InvalidOperationException("还没同意 Minecraft EULA，先运行 jay-server setup");
            config.BackendHost = "127.0.0.1";
            config.BackendPort = host.LoadSettings().Port;
        }
        using var key = store.LoadServerKey();
        var log = (string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Jay] {line}");
        await using var gate = new GateServer(store, config, key, log);
        gate.Start();
        log($"网关在 {config.ListenAddress} 端口 {config.ListenPort} 上等待玩家；服务器身份 {MemberIdentity.FingerprintOf(key.PublicKey)}");

        McServerHost.RunningServer? mc = null;
        if (withMc)
        {
            mc = host.Start(Console.WriteLine);
            log($"MC 服务端已启动（进程 {mc.Process.Id}）");
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        PosixSignals(stop);

        // 控制台：普通输入交给 MC；jay 开头的是管理命令。没有可交互的控制台（作为服务运行）时只等结束信号。
        var input = Task.Run(async () =>
        {
            if (Console.IsInputRedirected && !options.Has("console")) return;
            while (!stop.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stop.Token);
                if (line is null) return;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line == "stop" || line == "jay stop") { stop.Cancel(); return; }
                if (line.StartsWith("jay ", StringComparison.Ordinal))
                {
                    try { Admin(root, Options.Parse(Split(line[4..])), Console.Out); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or FormatException) { Console.WriteLine("出错：" + e.Message); }
                }
                else mc?.Command(line.TrimStart('/'));
            }
        });

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop.Token);
                if (mc is { HasExited: true })
                {
                    log($"MC 服务端退出了（退出码 {mc.Process.ExitCode}），网关一起停止");
                    stop.Cancel();
                }
            }
        }
        catch (OperationCanceledException) { }

        log("正在关闭……");
        if (mc is not null)
        {
            await mc.StopAsync(TimeSpan.FromSeconds(60));
            mc.Dispose();
        }
        return 0;
    }

    private static void PosixSignals(CancellationTokenSource stop)
    {
        if (OperatingSystem.IsWindows()) return;
        System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });
    }

    internal static int Admin(string root, Options options, TextWriter output)
    {
        var store = Gate(root);
        string Arg(int i, string what) => options.Positional.ElementAtOrDefault(i) ?? throw new ArgumentException($"缺少{what}");
        switch (options.Command)
        {
            case "invite":
            {
                var name = Arg(0, "用户名");
                var ttl = ParseDuration(options.Get("ttl") ?? "24h");
                using var key = store.LoadServerKey();
                var invite = store.CreateInvite(name, ttl, options.GetInt("uses", 1), key.PublicKey);
                output.WriteLine($"给 {name} 的邀请码（{FormatDuration(ttl)}内有效，可用 {options.GetInt("uses", 1)} 次）：");
                output.WriteLine();
                output.WriteLine(invite.Encode());
                output.WriteLine();
                output.WriteLine("让对方在 Jay 启动器里选“加入 Jay 服务器”，粘贴这段码。别发到公开的地方。");
                return 0;
            }
            case "list":
            {
                var state = store.Read();
                if (state.Members.Count == 0) output.WriteLine("名单是空的。用 jay-server invite <用户名> 邀请第一个人。");
                foreach (var m in state.Members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    output.WriteLine($"{m.Name}{(m.Disabled ? "（已停用）" : "")}");
                    foreach (var d in m.Devices)
                        output.WriteLine($"    设备 {d.Fingerprint}  加入于 {d.Added.ToLocalTime():yyyy-MM-dd HH:mm}  最近在线 {(d.LastSeen is { } s ? s.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "从未")}");
                    if (m.Devices.Count == 0) output.WriteLine("    （没有设备）");
                }
                return 0;
            }
            case "invites":
            {
                var invites = store.Read().Invites;
                if (invites.Count == 0) output.WriteLine("没有待用的邀请。");
                foreach (var i in invites)
                    output.WriteLine($"{i.Id}  给 {i.Name}  剩 {i.UsesLeft} 次  到期 {i.Expires.ToLocalTime():yyyy-MM-dd HH:mm}");
                return 0;
            }
            case "cancel-invite":
                output.WriteLine(store.CancelInvite(Arg(0, "邀请编号")) ? "已作废。" : "没有这个邀请。");
                return 0;
            case "revoke":
            {
                var n = store.RevokeDevices(Arg(0, "用户名"), options.Positional.ElementAtOrDefault(1));
                output.WriteLine(n == 0 ? "没找到要撤销的设备。" : $"撤销了 {n} 台设备，它们之后进不来了（已经在线的连接下次重连时生效）。");
                return 0;
            }
            case "disable":
            case "enable":
                output.WriteLine(store.SetDisabled(Arg(0, "用户名"), options.Command == "disable") ? "好了。" : "名单里没有这个人。");
                return 0;
            case "remove":
                output.WriteLine(store.RemoveMember(Arg(0, "用户名")) ? "已从名单移除。" : "名单里没有这个人。");
                return 0;
            case "online-mode":
            {
                var on = Arg(0, "on 或 off") switch { "on" => true, "off" => false, _ => throw new ArgumentException("只能是 on 或 off") };
                var host = new McServerHost(root);
                host.WriteProperties(host.LoadSettings().Port, on);
                output.WriteLine(on ? "已开启正版验证（重启 MC 服务端后生效）。" : "已关闭正版验证：离线账号，由网关核对用户名（重启后生效）。");
                return 0;
            }
            default:
                output.WriteLine($"不认识的命令：{options.Command}");
                output.WriteLine(Usage);
                return 1;
        }
    }

    internal static TimeSpan ParseDuration(string text)
    {
        if (text.Length < 2) throw new FormatException("时长写成 30m、24h 或 7d");
        var number = double.Parse(text[..^1], CultureInfo.InvariantCulture);
        return char.ToLowerInvariant(text[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(number),
            'h' => TimeSpan.FromHours(number),
            'd' => TimeSpan.FromDays(number),
            _ => throw new FormatException("时长写成 30m、24h 或 7d"),
        };
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} 天" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} 小时" : $"{t.TotalMinutes:0} 分钟";

    private static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>最简单的参数解析：第一个词是命令，--name value 是选项，--flag 是开关，其余是位置参数。</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["accept-eula", "no-mc", "console"];
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];

    public static Options Parse(IReadOnlyList<string> args)
    {
        var o = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                if (Flags.Contains(name)) o._values[name] = null;
                else if (i + 1 < args.Count) o._values[name] = args[++i];
                else throw new ArgumentException($"--{name} 后面要跟一个值");
            }
            else if (o.Command is null) o.Command = a;
            else o.Positional.Add(a);
        }
        return o;
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public int GetInt(string name, int fallback) =>
        Get(name) is { } v ? int.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} 要是整数") : fallback;
}
