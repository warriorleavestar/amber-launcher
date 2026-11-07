using System.Globalization;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Transport;
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

        隐私+（玩家经两个不同节点各走一半，源站地址不外传）：
          jay-server relay --origin <网关地址:端口> [--listen 47671] [--code JAYR1-...]
              在节点电脑上运行（VPS 或局域网电脑都行）：只把连接原样转给网关，不存玩家的任何密钥
              带上节点码时还会给网关发心跳（隐私++ 要用）
          jay-server relays add <节点地址:端口> [--source <网关看到的节点 IP>]
              加节点并打印它的节点码。节点在局域网里、或者和网关在同一台机器上时，--source 填网关这边看到的它的 IP
          jay-server relays code <节点地址:端口>              重发节点码（旧码作废；节点重装后也用这个）
          jay-server relays remove <节点地址:端口> / relays list
          jay-server relays revoke <节点地址:端口>             吊销不可信的节点：马上断开所有人，重新握手换新密钥
          jay-server privacy basic|plus|plusplus               plus 至少要两个节点；plusplus 还要节点发心跳，不够就熔断
          jay-server status                                    节点心跳、熔断状态、在线人数（服务运行时）

        入口端口轮换（像验证器 App 的动态码：入口端口每隔一段时间按种子换一个，扫描器找不到，泄露的旧地址自动过期）：
          jay-server rotate on [--window 10m] [--range 20000-45000] [--grace 3d] [--new-seed]
              普通模式轮换网关端口，隐私+/++ 轮换节点端口（节点要带节点码运行）。
              grace 期间固定端口照开，老玩家进一次服就会拿到轮换信息；之后固定端口关闭
          jay-server rotate off / rotate status

        通用选项：--dir <数据目录>（默认是程序旁边的 jay-server-data，也可以用环境变量 JAY_SERVER_DIR）
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var options = Options.Parse(args);
            if (options.WantsHelp) return Help();
            var root = options.Get("dir") ?? Environment.GetEnvironmentVariable("JAY_SERVER_DIR") ?? Path.Combine(AppContext.BaseDirectory, "jay-server-data");
            root = Path.GetFullPath(root);
            return options.Command switch
            {
                "init" => Init(root, options),
                "setup" => await SetupAsync(root, options),
                "run" => await RunAsync(root, options),
                "relay" => await RelayAsync(options),
                null => Help(),
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
        var state = store.Read();
        log(state.Privacy.IsSplit()
            ? $"网关在端口 {gate.Port} 上等待玩家（{(state.Privacy == PrivacyLevel.PlusPlus ? "隐私++" : "隐私+")}：只收 {state.Relays.Count} 个节点转来的拆分连接）；服务器身份 {MemberIdentity.FingerprintOf(key.PublicKey)}"
            : $"网关在端口 {gate.Port} 上等待玩家（玩家连 {config.PublicHost}:{config.PublicPort}）；服务器身份 {MemberIdentity.FingerprintOf(key.PublicKey)}");

        McServerHost.RunningServer? mc = null;
        if (withMc)
        {
            mc = host.Start(Console.WriteLine);
            log($"MC 服务端已启动（进程 {mc.Process.Id}）");
        }
        gate.BreakerChanged += (open, _) =>
        {
            // 熔断时先存盘（不停服），万一之后有人去停服务端也不会坏档。
            if (open) mc?.Command("save-all");
        };

        // 不 Dispose：退出时 ProcessExit 还可能来取消它。
        var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var sigterm = OperatingSystem.IsWindows() ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Stop(); });

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

    /// <summary>节点：不需要初始化网关数据目录，只要知道源站地址。</summary>
    private static async Task<int> RelayAsync(Options options)
    {
        var origin = GateEndpoint.TryParse(options.Get("origin")) ?? throw new ArgumentException("要用 --origin <网关地址:端口> 指定源站");
        var listen = options.GetInt("listen", 47671);
        var log = (string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Jay 节点] {line}");
        GateRelayCode? code = null;
        X25519KeyPair? key = null;
        if (options.Get("code") is { } text)
        {
            code = GateRelayCode.TryParse(text) ?? throw new ArgumentException("节点码不对（应该是 JAYR1- 开头的一串）");
            var dir = options.Get("dir") ?? Environment.GetEnvironmentVariable("JAY_RELAY_DIR") ?? Path.Combine(AppContext.BaseDirectory, "jay-relay-data");
            key = GateRelay.LoadOrCreateKey(Path.Combine(Path.GetFullPath(dir), "relay.key"));
        }
        using var _ = key;
        await using var relay = new GateRelay(options.Get("bind") ?? "::", listen, origin.Host, origin.Port, log: log, code: code, key: key);
        relay.Start();
        log(code is null
            ? $"节点在端口 {relay.Port} 上运行，转给 {origin}（没带节点码：不发心跳，隐私++ 下不算健康节点）"
            : $"节点在端口 {relay.Port} 上运行，转给 {origin}，节点身份 {MemberIdentity.FingerprintOf(key!.PublicKey)}，每 5 秒发一次心跳");
        var stop = new CancellationTokenSource();
        void Stop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        using var sigterm = OperatingSystem.IsWindows() ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Stop(); });
        try { await Task.Delay(Timeout.Infinite, stop.Token); }
        catch (OperationCanceledException) { }
        log("正在关闭……");
        return 0;
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
                output.WriteLine(n == 0 ? "没找到要撤销的设备。" : $"撤销了 {n} 台设备，它们之后进不来了（网关在运行时，几秒内会断开它们在线的连接）。");
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
            case "privacy":
            {
                var level = Arg(0, "basic、plus 或 plusplus") switch
                {
                    "basic" => PrivacyLevel.Basic,
                    "plus" => PrivacyLevel.Plus,
                    "plusplus" or "plus+" or "++" => PrivacyLevel.PlusPlus,
                    _ => throw new ArgumentException("只能是 basic、plus 或 plusplus"),
                };
                store.SetPrivacy(level);
                output.WriteLine(level switch
                {
                    PrivacyLevel.Plus => "已切到隐私+：玩家经两个节点进服，网关只收节点转来的连接（新邀请码里写的是节点地址）。别忘了用防火墙只放行节点访问网关端口。",
                    PrivacyLevel.PlusPlus => "已切到隐私++：在隐私+ 之上，节点要持续发心跳。发心跳的节点少于两个时熔断：断开所有人、暂停进服（服务端只存盘不停），节点恢复后自动可用。",
                    _ => "已切回普通模式：玩家直连网关。",
                });
                return 0;
            }
            case "relays":
            {
                switch (Arg(0, "add、code、remove、revoke 或 list"))
                {
                    case "add":
                    {
                        var address = GateEndpoint.TryParse(Arg(1, "节点地址")) ?? throw new ArgumentException("节点地址要写成 主机:端口");
                        store.AddRelay(address, options.Get("source"));
                        output.WriteLine($"已加节点 {address}。");
                        WriteRelayCode(store, address.ToString(), output);
                        break;
                    }
                    case "code":
                    {
                        var address = GateEndpoint.TryParse(Arg(1, "节点地址")) ?? throw new ArgumentException("节点地址要写成 主机:端口");
                        WriteRelayCode(store, address.ToString(), output);
                        output.WriteLine("旧的节点码已经作废，节点要换上新码重新运行。");
                        break;
                    }
                    case "revoke":
                        output.WriteLine(store.RevokeRelay(Arg(1, "节点地址"))
                            ? "已吊销：运行中的网关会马上断开所有人，大家重新握手换新密钥，之后不会再经过这个节点。记得在防火墙里也去掉它。"
                            : "没有这个节点。");
                        break;
                    case "remove":
                        output.WriteLine(store.RemoveRelay(Arg(1, "节点地址")) ? "已删除。" : "没有这个节点。");
                        break;
                    case "list":
                    {
                        var state = store.Read();
                        output.WriteLine($"隐私等级：{PrivacyName(state.Privacy)}");
                        if (state.Relays.Count == 0) output.WriteLine("还没有节点。");
                        foreach (var r in state.Relays)
                            output.WriteLine($"  {r.Address}{(r.Source is null ? "" : $"  （来源 IP {r.Source}）")}"
                                + (r.HeartbeatPsk is null ? "  没有节点码" : r.PublicKey is null ? "  节点码已发，节点还没连上" : $"  节点身份 {MemberIdentity.FingerprintOf(Convert.FromBase64String(r.PublicKey))}"));
                        break;
                    }
                    default:
                        throw new ArgumentException("只能是 add、code、remove、revoke 或 list");
                }
                return 0;
            }
            case "rotate":
            {
                switch (Arg(0, "on、off 或 status"))
                {
                    case "on":
                    {
                        var range = (options.Get("range") ?? $"{GateRotation.Default.MinPort}-{GateRotation.Default.MaxPort}").Split('-');
                        if (range.Length != 2 || !int.TryParse(range[0], out var min) || !int.TryParse(range[1], out var max)) throw new ArgumentException("--range 写成 20000-45000");
                        var window = ParseDuration(options.Get("window") ?? "10m");
                        var grace = ParseDuration(options.Get("grace") ?? "3d");
                        store.EnableRotation(new GateRotation((int)window.TotalSeconds, min, max), grace, options.Has("new-seed"));
                        output.WriteLine($"已开启入口端口轮换：每 {FormatDuration(window)}换一次，端口在 {min}–{max} 之间。");
                        output.WriteLine("防火墙 / 安全组要放行这个端口范围（网关在普通模式下，节点在隐私+/++ 下）。之后发的邀请码里带着轮换信息。");
                        if (store.Read().Rotation?.FixedUntil is { } until)
                            output.WriteLine($"固定端口开放到 {until.ToLocalTime():yyyy-MM-dd HH:mm}，这之前进过服的玩家会自动改用轮换端口；之后没进过服的老玩家要重新发邀请。");
                        return 0;
                    }
                    case "off":
                        store.DisableRotation();
                        output.WriteLine("已关闭轮换，回到固定端口。");
                        return 0;
                    case "status":
                    {
                        var rotation = store.Rotation();
                        if (rotation is not { } r)
                        {
                            output.WriteLine("没开轮换。");
                            return 0;
                        }
                        var config = store.LoadConfig();
                        var now = DateTimeOffset.UtcNow;
                        output.WriteLine($"轮换中：每 {FormatDuration(TimeSpan.FromSeconds(r.Rule.WindowSeconds))}换一次，端口 {r.Rule.MinPort}–{r.Rule.MaxPort}");
                        output.WriteLine($"  网关直连：现在 {r.Rule.PortAt(GateRotation.EntrySeed(r.Master, GateRotation.DirectEntry), now)}（固定端口 {config.ListenPort}）");
                        foreach (var relay in store.Read().Relays)
                            output.WriteLine($"  节点 {relay.Address}：现在 {r.Rule.PortAt(GateRotation.EntrySeed(r.Master, relay.Address), now)}");
                        return 0;
                    }
                    default:
                        throw new ArgumentException("只能是 on、off 或 status");
                }
            }
            case "status":
            {
                var state = store.Read();
                output.WriteLine($"隐私等级：{PrivacyName(state.Privacy)}");
                var health = store.ReadHealth();
                if (health is null || DateTimeOffset.UtcNow - health.Updated > TimeSpan.FromSeconds(30))
                {
                    output.WriteLine("网关没在运行（或者 30 秒内没更新状态）。");
                    return 0;
                }
                output.WriteLine($"在线 {health.Online} 人；{(health.BreakerOpen ? "熔断中（暂停进服）" : "正常")}；纪元 {health.Epoch}");
                foreach (var r in health.Relays)
                {
                    var seen = r.LastHeartbeat is { } t ? $"{(DateTimeOffset.UtcNow - t).TotalSeconds:0} 秒前" : "从未";
                    output.WriteLine($"  {r.Address}  心跳通道{(r.Connected ? "已连" : "未连")}  最近心跳 {seen}");
                }
                return 0;
            }
            default:
                output.WriteLine($"不认识的命令：{options.Command}");
                output.WriteLine(Usage);
                return 1;
        }
    }

    private static string PrivacyName(PrivacyLevel level) => level switch
    {
        PrivacyLevel.PlusPlus => "隐私++",
        PrivacyLevel.Plus => "隐私+",
        _ => "普通",
    };

    private static void WriteRelayCode(GateStore store, string address, TextWriter output)
    {
        using var key = store.LoadServerKey();
        var code = store.IssueRelayCode(address, key.PublicKey);
        output.WriteLine("节点码（隐私++ 的心跳要用，别外传）：");
        output.WriteLine();
        output.WriteLine(code.Encode());
        output.WriteLine();
        output.WriteLine($"在节点电脑上运行：jay-server relay --origin <网关地址>:<网关端口> --listen {GateEndpoint.TryParse(address)?.Port} --code <上面这段>");
    }

    internal static TimeSpan ParseDuration(string text)
    {
        if (text == "0") return TimeSpan.Zero;
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
    private static readonly HashSet<string> Flags = ["accept-eula", "no-mc", "console", "new-seed"];
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];

    /// <summary>help、-h、--help、/?（放在哪里都行）：显示用法，不执行命令。</summary>
    public bool WantsHelp { get; private set; }

    public static Options Parse(IReadOnlyList<string> args)
    {
        var o = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h" or "/?" || (a == "help" && o.Command is null))
            {
                o.WantsHelp = true;
                continue;
            }
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
