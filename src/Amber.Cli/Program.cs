using Amber.Core.Auth;
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
//   launch <版本> <玩家名> [--mem 4096] [--isolate] [--dry-run]
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
var downloader = new Downloader(http, sources);
var installer = new VanillaInstaller(downloader, dir);
var javaManager = new JavaManager(downloader, dir) { Status = new SyncProgress<string>(Console.WriteLine) };
var loaderContext = new LoaderContext(downloader, dir, installer, javaManager.ForMajorAsync);
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
