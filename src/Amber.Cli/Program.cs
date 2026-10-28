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
        case "launch":
        {
            var id = argList.ElementAtOrDefault(1) ?? throw new ArgumentException("缺少版本号");
            var player = argList.ElementAtOrDefault(2) ?? "Player";
            var mem = int.Parse(TakeOption(argList, "--mem") ?? "4096");
            var isolate = argList.Remove("--isolate");
            var dryRun = argList.Remove("--dry-run");

            await installer.CompleteAsync(id, null, cts.Token);
            var version = dir.ResolveVersion(id);
            var required = version.JavaVersion?.MajorVersion ?? 8;
            var java = JavaFinder.Pick(JavaFinder.FindAll(dir.Root), required)
                       ?? throw new InvalidOperationException($"没有找到 Java {required} 或更高版本");
            Console.WriteLine($"使用 {java}");

            var options = new LaunchOptions
            {
                VersionId = id,
                JavaPath = java.JavaPath,
                Session = OfflineAccount.Create(player),
                MaxMemoryMb = mem,
                WorkingDirectory = isolate ? dir.VersionDir(id) : null,
            };
            var launcher = new GameLauncher(dir);
            if (dryRun)
            {
                Console.WriteLine(string.Join('\n', launcher.BuildArguments(options)));
                break;
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
