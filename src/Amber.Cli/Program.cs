using Amber.Core;
using Amber.Core.Multiplayer;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Java;
using Amber.Core.Launch;
using Amber.Core.Net;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 用法：
//   amber [--dir <.minecraft>] [--source official|mirror] <命令>
//   list-remote [release|snapshot|all]   列出可安装版本
//   list                                  列出已安装版本
//   install <版本>                        安装 / 补全版本
//   java                                  列出本机 Java
//   loaders <fabric|quilt|forge|neoforge|optifine> <游戏版本>   列出加载器版本
//   install-loader <加载器> <游戏版本> [加载器版本] [--id 名字]    安装加载器（默认装最新稳定版）
//   java-install <主版本>                 下载 Mojang Java 运行时
//   search <modrinth|curseforge> <mod|resourcepack|shader|modpack> <关键词> [--mc 版本] [--loader 加载器]
//   add <来源> <项目 id> <版本名> [--type mod] [--isolate]    连同依赖安装到实例
//   mods <版本名> [--isolate]             列出实例里的模组
//   check-updates <版本名> [--isolate]    检查模组更新
//   update-mods <版本名> [--isolate]      更新所有能更新的模组
//   import <整合包文件> <新版本名>         导入 mrpack 或 CurseForge 整合包（总是版本隔离）
//   export <版本名> <输出文件> [--format mrpack|curseforge] [--isolate]
//   CurseForge：环境变量 AMBER_CF_KEY 提供 Key，AMBER_CF_ROOT 可以换接口地址
//   launch <版本> <玩家名> [--mem 4096] [--isolate] [--dry-run]
//   lan-setup                                下载并校验联机组件 EasyTier
//   lan-nat [--stun host:port]... [--map]    查网络类型（STUN、IPv6、路由器端口映射）
//   lan-direct-host <游戏端口> [--sim] [--debug] [--node url]... [--reply-file 文件] [--seconds N]
//   lan-direct-join <邀请码> [--sim] [--debug] [--node url]... [--seconds N]   直连房间，不用联机节点
//   lan-host <本机游戏端口> [--peer url]... [--listen url]... [--seconds N]   开房间，打印房间码
//   lan-join <房间码> [--peer url]... [--listen url]... [--seconds N]         加入房间，打印本机转发地址
//   （--peer 不写时用 EasyTier 社区公共节点；联机组件放在 %APPDATA%\Amber\easytier）
var argList = args.ToList();
var root = TakeOption(argList, "--dir") ?? GameDirectory.DefaultRoot();
var sources = TakeOption(argList, "--source") switch
{
    "official" => SourceChain.OfficialFirst,
    "mirror" => SourceChain.MirrorFirst,
    _ => SourceChain.Default(),
};

if (argList.Count == 0)
{
    Console.WriteLine("命令：list-remote | list | install <版本> | java | launch <版本> <玩家名> [--mem MB] [--isolate] [--dry-run]");
    return 1;
}

var dir = new GameDirectory(root);
using var http = Downloader.CreateHttpClient();
var downloader = new Downloader(http, sources) { Hashes = new HashCache(Path.Combine(dir.Root, ".amber-hashes")) };
var installer = new VanillaInstaller(downloader, dir);
var javaManager = new JavaManager(downloader, dir) { Status = new SyncProgress<string>(Console.WriteLine) };
var loaderContext = new LoaderContext(downloader, dir, installer, javaManager.ForMajorAsync);
var modrinth = new ModrinthProvider(http);
var curseForge = new CurseForgeProvider(http, () => Environment.GetEnvironmentVariable("AMBER_CF_KEY"),
    Environment.GetEnvironmentVariable("AMBER_CF_ROOT") ?? CurseForgeProvider.DefaultApiRoot);
var providers = new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = modrinth, [ContentSource.CurseForge] = curseForge };
var contentInstaller = new ContentInstaller(downloader, providers);
var modpacks = new ModpackService(loaderContext, modrinth, curseForge);
GameInstance Instance(string id, bool isolate) => GameInstance.Detect(dir, id, isolate ? dir.VersionDir(id) : dir.Root);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    switch (argList[0])
    {
        case "list-remote":
        {
            var filter = argList.ElementAtOrDefault(1) ?? "release";
            var manifest = await installer.GetManifestAsync(cts.Token);
            foreach (var v in manifest.Versions.Where(v => filter == "all" || v.Type == filter).Take(40))
                Console.WriteLine($"{v.Id,-24}{v.Type,-10}{v.ReleaseTime:yyyy-MM-dd}");
            Console.WriteLine($"最新正式版 {manifest.Latest.GetValueOrDefault("release")}，最新快照 {manifest.Latest.GetValueOrDefault("snapshot")}");
            break;
        }
        case "list":
            foreach (var id in dir.ListInstalled()) Console.WriteLine(id);
            break;
        case "install":
        {
            var id = argList.ElementAtOrDefault(1) ?? throw new ArgumentException("缺少版本号");
            Console.WriteLine($"安装 {id} 到 {dir.Root}（下载源：{string.Join(" → ", sources.Sources.Select(s => s.Name))}）");
            var lastPrint = DateTime.MinValue;
            await installer.InstallAsync(id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage != InstallStage.Files) { Console.WriteLine($"[{p.Stage}]"); return; }
                if ((DateTime.Now - lastPrint).TotalMilliseconds < 500 && p.Files.CompletedFiles < p.Files.TotalFiles) return;
                lastPrint = DateTime.Now;
                Console.WriteLine($"  {p.Files.CompletedFiles}/{p.Files.TotalFiles} 个文件，{p.Files.BytesDownloaded / 1024.0 / 1024:F1} MB");
            }), cts.Token);
            Console.WriteLine("完成");
            break;
        }
        case "java":
            foreach (var j in JavaFinder.FindAll(dir.Root)) Console.WriteLine(j);
            break;
        case "loaders":
        {
            var loader = LoaderInstallers.Create(Enum.Parse<LoaderKind>(argList[1], true), loaderContext);
            foreach (var v in (await loader.ListAsync(argList[2], cts.Token)).Take(30))
                Console.WriteLine($"{v.Version,-40}{(v.Stable ? "" : "测试版")} {v.Note}");
            break;
        }
        case "install-loader":
        {
            var customId = TakeOption(argList, "--id");
            var loader = LoaderInstallers.Create(Enum.Parse<LoaderKind>(argList[1], true), loaderContext);
            var versions = await loader.ListAsync(argList[2], cts.Token);
            var wanted = argList.ElementAtOrDefault(3);
            var chosen = (wanted is not null ? versions.FirstOrDefault(v => v.Version == wanted) : versions.FirstOrDefault(v => v.Stable) ?? versions.FirstOrDefault())
                         ?? throw new ArgumentException($"{argList[2]} 没有可用的 {argList[1]} {wanted}");
            var lastStage = "";
            var installed = await loader.InstallAsync(chosen, customId, new SyncProgress<LoaderProgress>(p =>
            {
                var head = p.Stage.Split(' ')[0];
                if (head != lastStage) Console.WriteLine(p.Stage);
                lastStage = head;
            }), cts.Token);
            Console.WriteLine($"已安装 {installed}");
            break;
        }
        case "search":
        {
            var mc = TakeOption(argList, "--mc");
            var loader = TakeOption(argList, "--loader");
            var provider = providers[Enum.Parse<ContentSource>(argList[1], true)];
            var result = await provider.SearchAsync(new SearchQuery(Enum.Parse<ContentType>(argList[2], true), argList.ElementAtOrDefault(3) ?? "", mc, loader, 0, 8), cts.Token);
            Console.WriteLine($"共 {result.Total} 个结果");
            foreach (var p in result.Items) Console.WriteLine($"{p.Id,-12}{p.Title,-34}{p.Downloads,12}  {p.Author}");
            if (result.Items.FirstOrDefault() is { } first)
            {
                var files = await provider.GetFilesAsync(first.Id, mc, loader, cts.Token);
                Console.WriteLine($"{first.Title} 有 {files.Count} 个匹配的版本");
                foreach (var f in files.Take(3)) Console.WriteLine($"  {f.DisplayName} | {f.FileName} | {f.Channel} | sha1 {f.Sha1} | {f.Size} 字节 | 依赖 {f.Dependencies.Count} | {f.Url}");
            }
            break;
        }
        case "add":
        {
            var type = Enum.Parse<ContentType>(TakeOption(argList, "--type") ?? "mod", true);
            var isolate = argList.Remove("--isolate");
            var provider = providers[Enum.Parse<ContentSource>(argList[1], true)];
            var instance = Instance(argList[3], isolate);
            // --via-connector：给 Forge / NeoForge 实例装 Fabric 模组，连同 Sinytra Connector 和 Forgified Fabric API。
            var viaConnector = argList.Remove("--via-connector");
            var loaderFilter = type == ContentType.Mod ? (viaConnector ? "fabric" : instance.Loader) : null;
            var files = await provider.GetFilesAsync(argList[2], instance.McVersion, loaderFilter, cts.Token);
            var file = files.PickPreferred()
                       ?? throw new InvalidOperationException($"没有适用于 {instance.McVersion} / {loaderFilter} 的版本");
            var hasConnector = ConnectorInstaller.IsInstalled(new ModManager(instance.ModsDir).List().Where(m => m.Enabled).Select(m => m.Requirements));
            var plan = viaConnector
                ? await ConnectorInstaller.PlanFabricModAsync(file, instance, modrinth, contentInstaller, null, hasConnector, cts.Token)
                : await contentInstaller.ResolveAsync(file, instance, null, cts.Token);
            foreach (var p in plan.Problems) Console.WriteLine("问题：" + p);
            foreach (var f in plan.Files) Console.WriteLine("安装 " + f.FileName);
            await contentInstaller.InstallAsync(plan, instance.DirectoryFor(type), null, cts.Token);
            Console.WriteLine($"已安装到 {instance.DirectoryFor(type)}");
            break;
        }
        case "mods":
        {
            var isolate = argList.Remove("--isolate");
            var instance = Instance(argList[1], isolate);
            Console.WriteLine($"{instance.Id}：游戏 {instance.McVersion}，加载器 {instance.Loader} {instance.LoaderVersion}");
            // --compat：同时做兼容检查；--quiet 只打印汇总和用时（测性能用）。
            var compat = argList.Remove("--compat");
            var quiet = argList.Remove("--quiet");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var list = new ModManager(instance.ModsDir).List();
            var listMs = watch.ElapsedMilliseconds;
            // --twice：同一进程里再扫一遍，看缓存命中后的用时。
            if (argList.Remove("--twice"))
            {
                var again = System.Diagnostics.Stopwatch.StartNew();
                new ModManager(instance.ModsDir).List();
                Console.WriteLine($"SCAN-AGAIN {again.ElapsedMilliseconds} ms");
            }
            // 和模组页一样只按启用的模组算前置和 Connector，禁用的不检查。
            var enabled = list.Where(m => m.Enabled).ToList();
            var ids = new HashSet<string>(enabled.SelectMany(m => m.Requirements?.Ids ?? []), StringComparer.OrdinalIgnoreCase);
            var connector = ConnectorInstaller.IsInstalled(enabled.Select(m => m.Requirements));
            var results = list.Select(m => compat && m.Enabled ? ModCompat.Check(m.Requirements, instance.Loader, instance.McVersion, ids, connector) : null).ToList();
            var compatMs = watch.ElapsedMilliseconds - listMs;
            var problems = results.Count(r => r?.IsProblem == true);
            if (!quiet)
                foreach (var (m, result) in list.Zip(results))
                    Console.WriteLine($"{(m.Enabled ? "  " : "× ")}{m.Name} | {m.Version} | {m.ModId} | {m.Authors} | {(m.Icon is null ? "无图标" : "有图标")}{(result is null ? "" : " | " + result.Text)}");
            Console.WriteLine($"SCAN {list.Count} 个模组：读元数据 {listMs} ms，兼容检查 {compatMs} ms，问题 {problems} 个");
            break;
        }
        case "prewarm":
        {
            // Connector 预处理：列出还没转换的 Fabric 模组，启动一次游戏转换完就结束。
            var isolate = argList.Remove("--isolate");
            var instance = Instance(argList[1], isolate);
            var pending = ConnectorWarmup.Pending(instance.WorkDir, new ModManager(instance.ModsDir).List(), instance.Loader);
            Console.WriteLine($"PENDING {pending.Count}：{string.Join("、", pending.Select(m => m.FileName))}");
            if (pending.Count == 0) break;
            await installer.CompleteAsync(instance.Id, null, cts.Token);
            // 和启动器一样按这个版本的设置启动（命令行没有全局设置，只用版本自己的）。
            var settings = InstanceSettings.Load(dir, instance.Id);
            if (isolate) settings.Isolate = true;
            var java = !string.IsNullOrEmpty(settings.JavaPath) && File.Exists(settings.JavaPath)
                ? settings.JavaPath
                : await javaManager.ForVersionAsync(dir.ResolveVersion(instance.Id), cts.Token);
            var options = settings.ToLaunchOptions(dir, instance.Id, java, OfflineAccount.Create("Player"), []);
            var result = await ConnectorWarmup.RunAsync(new GameLauncher(dir), options, new SyncProgress<string>(Console.WriteLine), TimeSpan.FromMinutes(15), cts.Token);
            Console.WriteLine($"WARMUP {(result.Done ? "DONE" : "FAIL")} {result.Detail}");
            var left = ConnectorWarmup.Pending(instance.WorkDir, new ModManager(instance.ModsDir).List(), instance.Loader);
            Console.WriteLine($"PENDING-AFTER {left.Count}");
            break;
        }
        case "transport-bench":
        {
            // 各封装模式在本机回环上的开销：握手时间、64 字节往返、单向吞吐。不经过 EasyTier，只量封装本身。
            foreach (var (mode, disguise) in new[] { (Amber.Core.Multiplayer.Transport.TransportMode.General, false), (Amber.Core.Multiplayer.Transport.TransportMode.Minecraft, false),
                         (Amber.Core.Multiplayer.Transport.TransportMode.High, false), (Amber.Core.Multiplayer.Transport.TransportMode.High, true),
                         (Amber.Core.Multiplayer.Transport.TransportMode.Extreme, false), (Amber.Core.Multiplayer.Transport.TransportMode.Extreme, true) })
            {
                if (mode == Amber.Core.Multiplayer.Transport.TransportMode.Extreme && !Amber.Core.Multiplayer.Transport.NoiseHandshake.HybridSupported)
                {
                    Console.WriteLine("Extreme：这台电脑不支持 ML-KEM，跳过");
                    continue;
                }
                Console.WriteLine(await TransportBench.RunAsync(mode, disguise, cts.Token));
            }
            break;
        }
        case "account-check":
        {
            // 检查选中的微软账号能不能正常进服务器，只打印不涉密的结果。--force-refresh：在内存里强制走一次刷新令牌（不写回文件）。
            var force = argList.Remove("--force-refresh");
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber");
            var store = new AccountStore(Path.Combine(dataDir, "accounts.json"));
            store.Load();
            var account = store.Selected;
            if (account is not { Kind: AccountKind.Microsoft })
            {
                Console.WriteLine($"选中的账号不是微软账号（{account?.Kind.ToString() ?? "没有账号"}）");
                break;
            }
            var auth = new MicrosoftAuth(http, MicrosoftAuth.DefaultClientId);
            Console.WriteLine($"账号 {account.Name}，令牌到期 {account.AccessTokenExpires:u}，已存访问令牌 {account.AccessToken is { Length: > 0 }}，已存刷新令牌 {account.RefreshToken is { Length: > 0 }}");
            if (force)
            {
                var before = account.AccessToken;
                account.AccessTokenExpires = DateTimeOffset.UtcNow.AddMinutes(-1);
                await auth.RefreshAsync(account, cts.Token);
                Console.WriteLine($"刷新：成功，得到新的访问令牌 {account.AccessToken != before}，新的到期时间 {account.AccessTokenExpires:u}（没有写回文件）");
            }
            else await auth.RefreshAsync(account, cts.Token);
            using (var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile"))
            {
                profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", account.AccessToken);
                using var profile = await http.SendAsync(profileRequest, cts.Token);
                var body = System.Text.Json.Nodes.JsonNode.Parse(await profile.Content.ReadAsStringAsync(cts.Token));
                Console.WriteLine($"档案接口：HTTP {(int)profile.StatusCode}，名字 {body?["name"]}，UUID 一致 {body?["id"]?.GetValue<string>() == account.Uuid.ToString("N")}，皮肤 {body?["skins"]?.AsArray().Count ?? 0} 个");
            }
            // 和进服务器时一样：客户端向会话服务器登记一个随机的服务器 id，再像服务器那样查询 hasJoined。
            var serverId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
            var joinBody = new System.Text.Json.Nodes.JsonObject { ["accessToken"] = account.AccessToken, ["selectedProfile"] = account.Uuid.ToString("N"), ["serverId"] = serverId };
            using (var join = await http.PostAsync("https://sessionserver.mojang.com/session/minecraft/join", AmberJson.Body(joinBody), cts.Token))
                Console.WriteLine($"会话登记（join）：HTTP {(int)join.StatusCode}{(join.IsSuccessStatusCode ? "" : " " + await join.Content.ReadAsStringAsync(cts.Token))}");
            using (var joined = await http.GetAsync($"https://sessionserver.mojang.com/session/minecraft/hasJoined?username={Uri.EscapeDataString(account.Name)}&serverId={serverId}", cts.Token))
                Console.WriteLine($"服务器校验（hasJoined）：HTTP {(int)joined.StatusCode}{(joined.StatusCode == System.Net.HttpStatusCode.OK ? "，通过" : "，没通过")}");
            break;
        }
        case "check-updates" or "update-mods":
        {
            var isolate = argList.Remove("--isolate");
            var instance = Instance(argList[1], isolate);
            var hasCf = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AMBER_CF_KEY")) || Environment.GetEnvironmentVariable("AMBER_CF_ROOT") is not null;
            var updates = await ModManager.CheckUpdatesAsync(new ModManager(instance.ModsDir).List(), modrinth, hasCf ? curseForge : null, instance.McVersion, instance.Loader, cts.Token);
            Console.WriteLine($"{updates.Count} 个可更新");
            foreach (var u in updates) Console.WriteLine($"  {Path.GetFileName(u.LocalPath)} → {u.Latest.FileName}");
            if (argList[0] == "update-mods")
            {
                foreach (var u in updates) await ModManager.ApplyUpdateAsync(u, downloader, cts.Token);
                Console.WriteLine("已更新");
            }
            break;
        }
        case "import":
        {
            Console.WriteLine(ModpackService.Inspect(argList[1]));
            var lastStage = "";
            var installed = await modpacks.ImportAsync(argList[1], argList[2], dir.VersionDir, new SyncProgress<LoaderProgress>(p =>
            {
                var head = p.Stage.Split(' ')[0];
                if (head != lastStage) Console.WriteLine(p.Stage);
                lastStage = head;
            }), cts.Token);
            Console.WriteLine($"已导入 {installed}");
            break;
        }
        case "export":
        {
            var format = TakeOption(argList, "--format") ?? "mrpack";
            var isolate = argList.Remove("--isolate");
            var instance = Instance(argList[1], isolate);
            var include = ModpackService.ExportCandidates(instance.WorkDir);
            Console.WriteLine("包含：" + string.Join(", ", include));
            if (format == "curseforge") await modpacks.ExportCurseForgeAsync(instance, argList[2], instance.Id, "1.0.0", Environment.UserName, include, cts.Token);
            else await modpacks.ExportMrpackAsync(instance, argList[2], instance.Id, "1.0.0", include, cts.Token);
            Console.WriteLine($"已导出 {argList[2]}（{new FileInfo(argList[2]).Length / 1024} KB）");
            break;
        }
        case "lan-setup":
        {
            var et = new EasyTierInstaller(downloader, AmberDataDir());
            await et.EnsureAsync(null, cts.Token);
            Console.WriteLine($"EasyTier {EasyTierRelease.Version} 就绪：{et.CorePath}");
            break;
        }
        case "lan-direct-host" or "lan-direct-join":
        {
            // 直连房间（不用联机节点）。--sim 本机模拟（只用 127.0.0.1），--debug 打印详细日志，
            // --node 打不通时退回的节点，--reply-file 房主从这个文件读加入方的回复码。
            var isHost = argList[0] == "lan-direct-host";
            var sim = argList.Remove("--sim");
            var debug = argList.Remove("--debug");
            var nodes = TakeAll(argList, "--node");
            var stunList = TakeAll(argList, "--stun");
            var replyFile = TakeOption(argList, "--reply-file");
            var seconds = int.Parse(TakeOption(argList, "--seconds") ?? "0");
            var et = new EasyTierInstaller(downloader, AmberDataDir());
            if (!et.IsInstalled) throw new InvalidOperationException("先运行 lan-setup");
            // 直连房间也是邀请房间：房主开大厅、发一张自动批准的邀请；加入方经直连桥敲门拿到房间凭据。
            // 这里的联机身份每次新生成，不保存。
            using var me = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
            DirectCode? invite = null;
            RoomCode? room = null;
            MemberTable? members = null;
            RoomCredentials? credentials = null;
            InviteBook? book = null;
            byte[] bridgeKey;
            if (isHost)
            {
                credentials = RoomCredentials.Create(me.PublicKey, int.Parse(argList[1]), Amber.Core.Multiplayer.Transport.TunnelSettings.Default);
                members = new MemberTable();
                room = credentials.ToHostRoom(members, "cli");
                book = new InviteBook { KeepLobbyKey = true };
                var lobbyKey = book.LobbyKeyCopy();
                bridgeKey = DirectKeys.Bridge(lobbyKey);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(lobbyKey);
            }
            else
            {
                if (!DirectCode.TryParse(argList[1], out invite, out var error) || invite.Invite is null) throw new ArgumentException(error ?? "邀请码不对");
                bridgeKey = DirectKeys.Bridge(invite.Invite.LobbyKey);
            }
            var log = new DiagnosticLog();
            log.Added += e => { if (debug || e.Category is not "EasyTier") Console.WriteLine("  log| " + NetDiagnostics.Redact(e.ToString(), room is { } r ? [.. r.Secrets] : [], true)); };
            var options = new DirectOptions(stunList.Count > 0 ? stunList : Stun.DefaultServers, !sim, nodes, false, false, debug, sim);
            await using var direct = new DirectSession(et, Path.Combine(AmberDataDir(), "easytier"), room, isHost, options, log) { BridgeKey = bridgeKey };
            var code = await direct.PrepareAsync(cts.Token);
            await using var lobby = isHost
                ? HostLobby.Create(et, Path.Combine(AmberDataDir(), "easytier", "lobby"), () => nodes, me, () => credentials!, _ => false, (_, _) => Task.FromResult(true),
                    members!, l => Console.WriteLine("  lobby| " + l), book, direct.LobbyUdpPort)
                : null;
            if (lobby is not null)
            {
                var pending = lobby.Book.Create(1, TimeSpan.FromMinutes(30), true, credentials!.Tunnel);
                await lobby.UpdateAsync();
                Console.WriteLine("INVITE " + direct.InviteCode(pending.Invite).Encode());
            }
            else
            {
                Console.WriteLine("REPLY " + (code with { Session = invite!.Session }).Encode());
                var joined = await direct.JoinAsync(invite, async (lobbyPeers, ct) =>
                {
                    var outcome = await LobbyJoin.KnockAsync(et, Path.Combine(AmberDataDir(), "easytier", "lobby-join"), lobbyPeers, invite.Invite!, me, "cli",
                        UpdateChecker.CurrentVersion, l => Console.WriteLine("  lobby| " + l), null, ct);
                    return outcome.Credentials?.ToRoom();
                }, cts.Token);
                if (!joined) throw new InvalidOperationException("房主没有批准");
                Console.WriteLine($"LOCAL 127.0.0.1:{direct.LocalGamePort}");
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            var replied = false;
            try
            {
                while (true)
                {
                    await Task.Delay(2000, stop.Token);
                    if (isHost && !replied && replyFile is not null && File.Exists(replyFile) && DirectCode.TryParse(File.ReadAllText(replyFile), out var reply, out _))
                    {
                        replied = true;
                        _ = direct.AddReplyAsync(reply, stop.Token);
                    }
                    var peers = direct.EasyTier is { } s ? await s.PeersAsync(stop.Token) : [];
                    Console.WriteLine("PATH " + direct.DescribePath(peers));
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                Console.WriteLine("时间到，结束");
            }
            break;
        }
        case "lan-nat":
        {
            // 查网络类型：STUN 外部地址、NAT 类型、IPv6、路由器端口映射（--map 时才试，结束时删掉）。
            var servers = TakeAll(argList, "--stun");
            var tryMap = argList.Remove("--map");
            using var socket = DirectBridge.CreateSocket();
            var gathered = await DirectGatherer.GatherAsync(socket, servers.Count > 0 ? servers : Stun.DefaultServers, tryMap, Console.WriteLine, cts.Token);
            Console.WriteLine($"NAT {gathered.Nat}：{DirectText.Nat(gathered.Nat)}");
            foreach (var c in gathered.Candidates) Console.WriteLine("CANDIDATE " + c);
            if (gathered.Mapping?.Remove is { } remove) await remove();
            break;
        }
        case "lan-host" or "lan-join":
        {
            var host = argList[0] == "lan-host";
            var peers = TakeAll(argList, "--peer");
            var listen = TakeAll(argList, "--listen");
            var seconds = int.Parse(TakeOption(argList, "--seconds") ?? "0");
            // 封装模式：--transport 0..3（通用 / MC 专用 / 高加密 / 极端加密），--disguise 伪装成 MC。
            var transportMode = TakeOption(argList, "--transport") is { } tm
                ? int.TryParse(tm, out var tmValue) && tmValue is >= 0 and <= 3 ? (Amber.Core.Multiplayer.Transport.TransportMode)tmValue : throw new ArgumentException("--transport 只能是 0–3")
                : Amber.Core.Multiplayer.Transport.TransportMode.General;
            var disguise = argList.Remove("--disguise");
            if (transportMode == Amber.Core.Multiplayer.Transport.TransportMode.Extreme && !Amber.Core.Multiplayer.Transport.NoiseHandshake.HybridSupported)
                throw new PlatformNotSupportedException("极端加密需要系统支持 ML-KEM（较新的 Windows 11），这台电脑不支持");
            var allowProxy = argList.Remove("--allow-proxy");
            var proxyExit = TakeOption(argList, "--proxy-exit");
            // 测试用：--extra-forward 本机端口:房主端口，模拟有人自己加转发去连房主的别的端口（应该被白名单挡住）。
            var extraForward = TakeOption(argList, "--extra-forward") is { } ef ? (int.Parse(ef.Split(':')[0]), int.Parse(ef.Split(':')[1])) : ((int, int)?)null;
            // --echo：房主在游戏端口上开一个回显服务代替游戏；--probe N：加入方连上后测 N 次往返延迟和一次吞吐。
            var echo = argList.Remove("--echo");
            var probe = int.Parse(TakeOption(argList, "--probe") ?? "0");
            RoomCode room;
            int? local = null;
            if (host)
            {
                room = RoomCode.Create(int.Parse(argList[1])) with { Tunnel = new Amber.Core.Multiplayer.Transport.TunnelSettings(transportMode, disguise) };
            }
            else
            {
                if (!RoomCode.TryParse(argList[1], out room)) throw new ArgumentException("房间码格式不对");
                if (room.Tunnel.Mode == Amber.Core.Multiplayer.Transport.TransportMode.Extreme && !Amber.Core.Multiplayer.Transport.NoiseHandshake.HybridSupported)
                    throw new PlatformNotSupportedException("这个房间用的是极端加密，需要系统支持 ML-KEM，这台电脑不支持");
                local = EasyTierArgs.FreeTcpPort();
            }
            var et = new EasyTierInstaller(downloader, AmberDataDir());
            if (!et.IsInstalled) throw new InvalidOperationException("先运行 lan-setup");
            // --peer none：不连任何节点（本机测试时房主只开监听）。
            List<string> effectivePeers = peers.Contains("none") ? [] : peers.Count > 0 ? peers : EasyTierRelease.DefaultPeers.ToList();
            var options = new EasyTierOptions(room, host, effectivePeers, EasyTierArgs.FreeTcpPort(), local,
                listen.Count > 0 ? listen : null, Guid.NewGuid().ToString(), LocalChatPort: host ? null : EasyTierArgs.FreeTcpPort())
            {
                LocalTunnelPort = !host && room.Tunnel.Wraps ? EasyTierArgs.FreeTcpPort() : null,
                AllowProxy = allowProxy,
                ProxyExit = proxyExit,
                LocalSocksPort = proxyExit is null ? null : EasyTierArgs.FreeTcpPort(),
                LocalExitPort = proxyExit is null ? null : EasyTierArgs.FreeTcpPort(),
            };
            if (extraForward is { } xf) options = options with { ExtraForwards = [xf] };
            using var echoServer = host && echo ? EchoServer.Start(room.Port) : null;
            using var session = EasyTierSession.Start(et, options, Path.Combine(AmberDataDir(), "easytier", "run-cli-" + options.RpcPort));
            await using var transport = Amber.Core.Multiplayer.Transport.RoomTransport.Start(options, l => Console.WriteLine("  tunnel| " + l));
            Console.WriteLine($"TRANSPORT {room.Tunnel.Mode} disguise={room.Tunnel.Disguise} server={transport.Server?.Port.ToString() ?? "-"} socks={transport.Socks?.Port.ToString() ?? "-"}");
            if (Environment.GetEnvironmentVariable("AMBER_ET_VERBOSE") == "1") session.Output += l => Console.WriteLine("  et| " + l);
            Console.WriteLine($"RPC {options.RpcPort}");
            Console.WriteLine($"CLI {et.CliPath}");
            Console.WriteLine($"ARGS {string.Join(' ', EasyTierArgs.Build(options).Select(x => room.Secrets.Contains(x) ? "<secret>" : x))}");
            Console.WriteLine(host ? $"ROOM {room}" : $"LOCAL 127.0.0.1:{local}");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                while (!session.HasExited)
                {
                    await Task.Delay(3000, stop.Token);
                    var list = await session.PeersAsync(stop.Token);
                    Console.WriteLine($"PEERS {list.Count}: " + string.Join("; ", list.Select(p => $"{p.Hostname} {p.Ip} {p.Cost} {p.LatencyMs}ms {p.Tunnel}")));
                    if (probe > 0 && local is { } probePort && list.Count > 0)
                    {
                        try
                        {
                            Console.WriteLine("PROBE " + await EchoServer.ProbeAsync(probePort, probe, stop.Token));
                            probe = 0;
                        }
                        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
                        {
                            Console.WriteLine("PROBE 还连不上：" + e.Message);
                        }
                    }
                    if (transport.Socks is { } sx) Console.WriteLine($"SOCKS 127.0.0.1:{sx.Port} 连接 {sx.Connections} 失败 {sx.Failures}");
                    if (transport.Server is { } sv) Console.WriteLine($"TUNNEL-SERVER 接受 {sv.Accepted} 拒绝 {sv.Rejected}");
                }
                Console.WriteLine("easytier-core 退出了：");
                foreach (var l in session.RecentOutput.TakeLast(20)) Console.WriteLine("  " + l);
                return 3;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                Console.WriteLine("时间到，结束");
            }
            break;
        }
        case "worlds":
        {
            foreach (var w in WorldManager.List(Path.Combine(argList[1], "saves")))
                Console.WriteLine($"{w.Name} | {w.FolderName} | {w.GameMode} | {w.Version} | {w.LastPlayed}");
            break;
        }
        case "ping":
        {
            var status = await ServerList.PingAsync(argList[1], null, cts.Token);
            Console.WriteLine(status);
            break;
        }
        case "junk":
        {
            foreach (var g in Toolbox.ScanJunk(dir)) Console.WriteLine($"{g.Name}: {g.Paths.Count} 项，{g.Bytes / 1024} KB");
            break;
        }
        case "analyze":
        {
            var analysis = CrashAnalyzer.Analyze(File.ReadAllText(argList[1]));
            Console.WriteLine(analysis.Summary);
            foreach (var m in analysis.SuspectMods) Console.WriteLine("疑似模组：" + m);
            break;
        }
        case "java-install":
            Console.WriteLine(await javaManager.ForMajorAsync(int.Parse(argList[1]), cts.Token));
            break;
        case "launch":
        {
            var mem = int.Parse(TakeOption(argList, "--mem") ?? "4096");
            var id = argList.ElementAtOrDefault(1) ?? throw new ArgumentException("缺少版本号");
            var player = argList.ElementAtOrDefault(2) ?? "Player";
            var isolate = argList.Remove("--isolate");
            var dryRun = argList.Remove("--dry-run");
            var smoke = TakeOption(argList, "--smoke") is { } smokeText ? int.Parse(smokeText) : (int?)null;
            var authlib = TakeOption(argList, "--authlib");
            var joinServer = TakeOption(argList, "--server");
            var joinWorld = TakeOption(argList, "--world");
            var extraJvm = TakeOption(argList, "--jvm");
            // --bake <加载器目录>：实验功能，先用通用加载器烘焙模组和实例，再用烘焙结果启动。
            var bakeLoader = TakeOption(argList, "--bake");

            await installer.CompleteAsync(id, null, cts.Token);
            var version = dir.ResolveVersion(id);
            var javaPath = await javaManager.ForVersionAsync(version, cts.Token);
            Console.WriteLine($"使用 {javaPath}");

            var options = new LaunchOptions
            {
                VersionId = id,
                JavaPath = javaPath,
                Session = OfflineAccount.Create(player),
                MaxMemoryMb = mem,
                WorkingDirectory = isolate ? dir.VersionDir(id) : null,
                JoinServer = joinServer,
                JoinWorld = joinWorld,
                ExtraJvmArgs = ArgumentSplitter.Split(extraJvm),
            };
            if (authlib is not null)
            {
                // 检查外置登录用：账号仍是离线的，只验证 authlib-injector 能否随游戏加载。
                var external = new ExternalAuth(http);
                var api = await external.ResolveApiRootAsync(authlib, cts.Token);
                var jar = await new AuthlibInjector(http, Path.Combine(dir.Root, "authlib-injector")).EnsureJarAsync(cts.Token);
                options = options with { ExtraJvmArgs = AuthlibInjector.JvmArgs(jar, api, await external.GetMetadataAsync(api, cts.Token)) };
                Console.WriteLine($"authlib-injector：{jar} → {api}");
            }
            if (bakeLoader is not null)
            {
                var toolkit = Amber.Core.Bake.BakeToolkit.Find(bakeLoader) ?? throw new DirectoryNotFoundException(bakeLoader);
                var bake = new Amber.Core.Bake.BakeService(toolkit, Path.Combine(dir.Root, ".amber-bake-cache"));
                var instance = Instance(id, isolate);
                var (_, _, supported) = await bake.VersionAsync(javaPath, cts.Token);
                if (!Amber.Core.Bake.BakeService.Supports(instance, supported, out var reason)) throw new InvalidOperationException(reason);
                var major = version.JavaVersion?.MajorVersion ?? 21;
                var bakeWatch = System.Diagnostics.Stopwatch.StartNew();
                var events = new SyncProgress<Amber.Core.Bake.BakeEvent>(e => Console.WriteLine("BAKE " + e.Message));
                var baked = bake.Current(dir, instance, major);
                if (baked is null)
                {
                    await bake.BakeModsAsync(javaPath, dir, instance, events, cts.Token);
                    baked = await bake.BakeInstanceAsync(javaPath, major, dir, instance, events, cts.Token);
                }
                Console.WriteLine($"BAKE READY {bakeWatch.Elapsed.TotalSeconds:F1}s {baked.StateFile}");
                options = options with { Baked = baked };
            }
            var launcher = new GameLauncher(dir);
            if (dryRun)
            {
                Console.WriteLine(string.Join('\n', launcher.BuildArguments(options)));
                break;
            }
            if (smoke is not null)
            {
                // 冒烟测试：等到游戏进入主界面（声音引擎启动）或超时，然后关掉游戏。
                var reached = new TaskCompletionSource<string>();
                var tail = new Queue<string>();
                var smokeParser = new GameLogParser();
                var game = launcher.Launch(options, line =>
                {
                    lock (tail)
                    {
                        var parsed = smokeParser.Feed(line);
                        if (parsed is null) return;
                        tail.Enqueue(parsed.Message);
                        if (tail.Count > 25) tail.Dequeue();
                        if (parsed.Message.Contains("Sound engine started") || parsed.Message.Contains("OpenAL initialized") || parsed.Message.Contains("SoundSystem Author"))
                            reached.TrySetResult(parsed.Message);
                    }
                });
                var finished = await Task.WhenAny(reached.Task, game.WaitForExitAsync(cts.Token), Task.Delay(TimeSpan.FromSeconds(smoke.Value), cts.Token));
                var ok = finished == reached.Task;
                // 再留几秒，确认不是一进主界面就崩。
                if (ok) await Task.WhenAny(game.WaitForExitAsync(cts.Token), Task.Delay(4000, cts.Token));
                ok = ok && !game.HasExited;
                var exitedByItself = game.HasExited;
                if (!game.HasExited) game.Kill(true);
                Console.WriteLine(ok ? $"SMOKE PASS {id}" : $"SMOKE FAIL {id}（{(game.HasExited ? "退出代码 " + game.ExitCode : "超时")}）");
                if (!ok) lock (tail) foreach (var l in tail) Console.WriteLine("  " + l);
                return ok ? 0 : 2;
            }
            var process = launcher.Launch(options, Console.WriteLine);
            await process.WaitForExitAsync(cts.Token);
            Console.WriteLine($"游戏已退出，代码 {process.ExitCode}");
            return process.ExitCode;
        }
        default:
            Console.WriteLine($"未知命令：{argList[0]}");
            return 1;
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("已取消");
    return 130;
}
catch (Exception e)
{
    Console.Error.WriteLine($"错误：{e.Message}");
    return 1;
}

static string AmberDataDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber");

static List<string> TakeAll(List<string> list, string name)
{
    var result = new List<string>();
    while (TakeOption(list, name) is { } value) result.Add(value);
    return result;
}

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}


/// <summary>测试用的回显服务（代替游戏）和延迟 / 吞吐测量。</summary>
internal sealed class EchoServer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private EchoServer(int port)
    {
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        _listener.Start();
        _ = LoopAsync();
    }

    public static EchoServer Start(int port) => new(port);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    c.NoDelay = true;
                    try { await c.GetStream().CopyToAsync(c.GetStream(), _cts.Token); } catch (Exception) { }
                }
            });
        }
    }

    /// <summary>经本机端口连到回显服务：count 次 64 字节往返的中位数 / p95，再测 8 MB 的往返吞吐。</summary>
    public static async Task<string> ProbeAsync(int port, int count, CancellationToken ct)
    {
        using var c = new System.Net.Sockets.TcpClient();
        await c.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
        c.NoDelay = true;
        var s = c.GetStream();
        var buf = new byte[64];
        var rtts = new List<double>();
        for (var i = 0; i < count; i++)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            await s.WriteAsync(buf, ct);
            var read = 0;
            while (read < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(read), ct);
                if (n == 0) throw new IOException("连接被关闭");
                read += n;
            }
            rtts.Add(w.Elapsed.TotalMilliseconds);
        }
        rtts.Sort();
        var data = new byte[8 * 1024 * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var writer = Task.Run(async () => { await s.WriteAsync(data, ct); });
        var got = new byte[data.Length];
        var total = 0;
        while (total < got.Length)
        {
            var n = await s.ReadAsync(got.AsMemory(total), ct);
            if (n == 0) throw new IOException("连接被关闭");
            total += n;
        }
        await writer;
        var seconds = watch.Elapsed.TotalSeconds;
        var ok = data.AsSpan().SequenceEqual(got);
        return $"往返中位数 {rtts[rtts.Count / 2]:F2} ms p95 {rtts[(int)(rtts.Count * 0.95)]:F2} ms；8 MB 往返 {seconds:F2} s（{8 / seconds:F1} MB/s）数据一致 {ok}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}


internal static class TransportBench
{
    public static async Task<string> RunAsync(Amber.Core.Multiplayer.Transport.TransportMode mode, bool disguise, CancellationToken ct)
    {
        var room = RoomCode.Create(25565);
        var handshakes = new List<double>();
        Stream? client = null, server = null;
        var tcps = new List<System.Net.Sockets.TcpClient>();
        for (var i = 0; i < 20; i++)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var a = new System.Net.Sockets.TcpClient { NoDelay = true };
            var acceptTask = listener.AcceptTcpClientAsync(ct);
            await a.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, ct);
            var b = await acceptTask;
            b.NoDelay = true;
            listener.Stop();
            tcps.Add(a);
            tcps.Add(b);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (mode == Amber.Core.Multiplayer.Transport.TransportMode.General)
            {
                client = a.GetStream();
                server = b.GetStream();
            }
            else
            {
                var accept = Amber.Core.Multiplayer.Transport.TunnelHandshake.AcceptAsync(b.GetStream(), room, mode, disguise, ct);
                client = await Amber.Core.Multiplayer.Transport.TunnelHandshake.ConnectAsync(a.GetStream(), room, mode, disguise, Amber.Core.Multiplayer.Transport.TunnelService.Game, ct);
                server = (await accept)!.Value.Stream;
            }
            handshakes.Add(watch.Elapsed.TotalMilliseconds);
        }
        // 回显：服务端把收到的原样发回。
        var echo = Task.Run(async () =>
        {
            var buf = new byte[65536];
            int n;
            try { while ((n = await server!.ReadAsync(buf, ct)) > 0) { await server.WriteAsync(buf.AsMemory(0, n), ct); await server.FlushAsync(ct); } } catch (Exception) { }
        }, ct);
        var msg = new byte[64];
        var rtts = new List<double>();
        for (var i = 0; i < 2000; i++)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            await client!.WriteAsync(msg, ct);
            await client.FlushAsync(ct);
            var read = 0;
            while (read < 64) read += await client.ReadAsync(msg.AsMemory(read), ct);
            rtts.Add(w.Elapsed.TotalMicroseconds);
        }
        rtts.Sort();
        var data = new byte[64 * 1024];
        var total = 64L * 1024 * 1024;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reader = Task.Run(async () =>
        {
            var got = 0L;
            var buf = new byte[65536];
            while (got < total) got += await client!.ReadAsync(buf, ct);
        }, ct);
        for (long sent = 0; sent < total; sent += data.Length) await client!.WriteAsync(data, ct);
        await reader;
        var mbps = 64 / sw.Elapsed.TotalSeconds;
        handshakes.Sort();
        foreach (var t in tcps) t.Dispose();
        var name = $"{mode}{(disguise ? "+伪装" : "")}";
        return $"{name,-16} 握手中位数 {handshakes[10]:F2} ms | 64 B 往返中位数 {rtts[1000]:F0} µs p99 {rtts[1980]:F0} µs | 64 MB 回显吞吐 {mbps:F0} MB/s";
    }
}
