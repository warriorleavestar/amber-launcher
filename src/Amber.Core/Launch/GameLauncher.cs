using Amber.Core.Localization;
using Amber.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Amber.Core.Auth;
using Amber.Core.Game;

namespace Amber.Core.Launch;

public sealed record LaunchOptions
{
    public required string VersionId { get; init; }
    public required string JavaPath { get; init; }
    public required GameSession Session { get; init; }

    /// <summary>游戏工作目录（存档、模组、配置）。为空时使用 .minecraft 根目录；版本隔离时传 versions/&lt;id&gt;。</summary>
    public string? WorkingDirectory { get; init; }

    public int MaxMemoryMb { get; init; } = 4096;
    public int MinMemoryMb { get; init; } = 256;
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
    public bool Fullscreen { get; init; }

    /// <summary>启动后直接进这个服务器（host 或 host:port）。</summary>
    public string? JoinServer { get; init; }

    /// <summary>启动后直接进这个存档（saves 下的文件夹名）。只有 1.20 以上支持。</summary>
    public string? JoinWorld { get; init; }
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = [];
    public IReadOnlyList<string> ExtraGameArgs { get; init; } = [];
    /// <summary>无痕模式：logs/latest.log 里不写聊天行（见 ChatLogFilter）。</summary>
    public bool NoTrace { get; init; }
    public string LauncherName { get; init; } = "Jay";

    /// <summary>实验功能“烘焙”的结果：设了就用通用加载器的主类和 classpath 启动，原版的其余参数不变。</summary>
    public Amber.Core.Bake.BakedLaunch? Baked { get; init; }
    public string LauncherVersion { get; init; } = "0.1";
}

/// <summary>拼装启动参数、准备 natives 与旧版资源，然后启动游戏进程。</summary>
public sealed class GameLauncher(GameDirectory dir)
{
    /// <summary>请求游戏正常退出，让游戏自己保存世界。</summary>
    public static bool RequestExit(Process game)
    {
        if (game.HasExited) return false;
        if (OperatingSystem.IsMacOS()) return MacGameProcess.RequestExit(game.Id);
        if (OperatingSystem.IsLinux()) return UnixGameProcess.RequestExit(game.Id);
        return game.CloseMainWindow();
    }

    public GameDirectory GameDir => dir;

    public RuleEvaluator BaseRules { get; init; } = RuleEvaluator.Current;

    /// <summary>改用英文名联接启动时，联接背后真正的游戏目录。旧版本的家目录联接要直接指向真目录，不能联接套联接。</summary>
    internal string? RealRoot { get; init; }

    /// <summary>
    /// 无痕模式生成的 log4j2 配置放在这里（每次启动一个子目录，游戏退出后删掉），不写进游戏目录：
    /// 游戏目录可能就是用户自己的 .minecraft。
    /// </summary>
    internal string NoTraceRoot { get; init; } = DefaultNoTraceRoot;

    public static string DefaultNoTraceRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amber", "notrace");

    /// <summary>生成本次启动的完整命令行（不含 java 本身）。需要长期保存的脚本用 WriteLaunchScript，避免引用临时配置。</summary>
    public List<string> BuildArguments(LaunchOptions options, Action<string>? notice = null) => BuildArguments(options, notice, out _);

    /// <summary>导出可长期使用的脚本；无痕配置随脚本保存在“脚本名.amber”目录里，不参与启动时的临时文件清理。</summary>
    public string WriteLaunchScript(string path, LaunchOptions options, Action<string>? notice = null)
    {
        var scriptPath = Path.GetFullPath(path);
        var (launcher, mapped) = ForCurrentCodePage(options, notice);
        var args = launcher.BuildArguments(mapped, notice, out _, scriptPath + ".amber");
        return Toolbox.WriteLaunchScript(scriptPath, mapped.JavaPath, mapped.WorkingDirectory ?? launcher.GameDir.Root,
            args, mapped.Session.AccessToken);
    }

    /// <summary>noTraceDir：这次无痕模式配置放的临时目录（没生成时为 null），用完由调用方删掉。</summary>
    internal List<string> BuildArguments(LaunchOptions options, Action<string>? notice, out string? noTraceDir, string? scriptConfigDir = null)
    {
        noTraceDir = null;
        var version = dir.ResolveVersion(options.VersionId);
        var rules = new RuleEvaluator
        {
            OsName = BaseRules.OsName,
            Arch = BaseRules.Arch,
            OsVersion = BaseRules.OsVersion,
            Features = BuildFeatures(options),
        };

        var workDir = options.WorkingDirectory ?? dir.Root;
        var nativesDir = dir.NativesDir(options.VersionId);
        var libraries = LibraryResolver.Resolve(version, dir, rules);
        var jarId = version.Jar ?? options.VersionId;
        var classpath = libraries.Where(l => l.OnClasspath).Select(l => l.LocalPath).ToList();
        classpath.Add(dir.VersionJarPath(jarId));
        if (options.Baked is { } baked) classpath = baked.Classpath.Select(p => AsciiPaths.MapArgument(p, notice)).ToList();

        var assetsIndexName = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = options.Session.PlayerName,
            ["auth_uuid"] = options.Session.Uuid.ToString("N"),
            ["auth_access_token"] = options.Session.AccessToken,
            ["auth_session"] = options.Session.AccessToken,
            ["auth_xuid"] = options.Session.Xuid ?? "",
            ["clientid"] = options.Session.ClientId,
            ["user_type"] = options.Session.UserType,
            ["user_properties"] = "{}",
            ["version_name"] = options.VersionId,
            ["version_type"] = options.LauncherName,
            ["game_directory"] = workDir,
            ["assets_root"] = dir.AssetsDir,
            ["game_assets"] = LegacyAssetsDir(assetsIndexName, workDir),
            ["assets_index_name"] = assetsIndexName,
            ["natives_directory"] = nativesDir,
            ["library_directory"] = dir.LibrariesDir,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["launcher_name"] = options.LauncherName,
            ["launcher_version"] = options.LauncherVersion,
            ["resolution_width"] = options.WindowWidth?.ToString() ?? "854",
            ["resolution_height"] = options.WindowHeight?.ToString() ?? "480",
            ["quickPlayMultiplayer"] = options.JoinServer ?? "",
            ["quickPlaySingleplayer"] = options.JoinWorld ?? "",
        };

        var args = new List<string>
        {
            $"-Xmx{options.MaxMemoryMb}m",
            $"-Xms{Math.Min(options.MinMemoryMb, options.MaxMemoryMb)}m",
            "-XX:+UseG1GC",
            "-XX:-UseAdaptiveSizePolicy",
            "-XX:-OmitStackTraceInFastThrow",
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
        };

        if (version.Logging?.GetValueOrDefault("client") is { Argument: { } logArg, File: { } logFile })
        {
            var logPath = dir.LoggingConfigPath(logFile.Id);
            if (File.Exists(logPath))
            {
                if (options.NoTrace)
                {
                    noTraceDir = scriptConfigDir ?? NewNoTraceDir();
                    logPath = ChatLogFilter.Prepare(logPath, noTraceDir);
                    notice?.Invoke(L.T("core.launch.notrace.on"));
                }
                args.Add(AsciiPaths.MapArgument(logArg.Replace("${path}", logPath), notice));
            }
            else if (options.NoTrace)
            {
                throw new InvalidOperationException(L.T("core.launch.notrace.config_missing"));
            }
        }
        else if (options.NoTrace)
        {
            // Forge 1.13+、NeoForge 的版本文件把 logging 清空了，游戏用加载器 jar 里自带的 log4j2.xml：取出来改好再指给它。
            noTraceDir = scriptConfigDir ?? NewNoTraceDir();
            var extracted = ChatLogFilter.ExtractFromClasspath(classpath, Path.Combine(noTraceDir, "loader-log4j2.xml"));
            if (extracted is not null)
            {
                args.Add(AsciiPaths.MapArgument("-Dlog4j.configurationFile=" + ChatLogFilter.Prepare(extracted, noTraceDir), notice));
                notice?.Invoke(L.T("core.launch.notrace.on"));
            }
            else if (ChatLogFilter.UsesLog4j(classpath))
            {
                // 有 log4j（会写 latest.log）却找不到配置：不启动，说清楚是哪个加载器。
                throw new InvalidOperationException(L.F("core.launch.notrace.loader_config_missing", ChatLogFilter.LoaderName(version)));
            }
            else
            {
                notice?.Invoke(L.T("core.launch.notrace.unsupported"));
            }
        }

        if (version.Arguments?.Jvm is { Count: > 0 } jvm)
        {
            // Forge 的 ignoreList 里写的是 ${version_name}.jar，意思是“客户端 jar”；
            // 这里的 classpath 直接用原版 jar，所以在 JVM 参数里 version_name 要指向它。
            var jvmVars = new Dictionary<string, string>(vars) { ["version_name"] = jarId, ["primary_jar_name"] = jarId + ".jar" };
            args.AddRange(Expand(jvm, rules).Select(a => Substitute(a, jvmVars)));
        }
        else
        {
            // 1.12.2 及更早没有 jvm 参数，需要自己补上。
            args.Add($"-Djava.library.path={nativesDir}");
            args.Add("-cp");
            args.Add(vars["classpath"]);
        }
        if (options.Baked is { } bakedJvm) args.AddRange(bakedJvm.JvmArgs);
        args.AddRange(options.ExtraJvmArgs);

        if (options.Baked is { } bakedMain)
        {
            args.Add(bakedMain.MainClass);
            args.AddRange(bakedMain.GameArgsPrefix.Select(a => AsciiPaths.MapArgument(a, notice)));
        }
        else
        {
            args.Add(version.MainClass ?? throw new InvalidDataException(L.F("core.launch.game.missingmainclass", options.VersionId)));
        }

        if (version.Arguments?.Game is { Count: > 0 } game)
            args.AddRange(Expand(game, rules).Select(a => Substitute(a, vars)));
        if (version.MinecraftArguments is { } legacy)
            args.AddRange(legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => Substitute(a, vars)));

        if (options.WindowWidth is not null && version.Arguments is null)
            args.AddRange(["--width", vars["resolution_width"], "--height", vars["resolution_height"]]);
        if (options.Fullscreen) args.Add("--fullscreen");

        // 1.20 以上的版本 JSON 里有快速进入参数，由上面的 feature 打开；更老的版本用 --server。
        var hasQuickPlay = version.Arguments?.Game.Any(e => e.ToString().Contains("quickPlayMultiplayer", StringComparison.Ordinal)) == true;
        if (options.JoinServer is { } server && !hasQuickPlay)
        {
            var (host, port) = ServerList.ParseAddress(server);
            args.AddRange(["--server", host, "--port", port.ToString()]);
        }
        args.AddRange(options.ExtraGameArgs);
        return args;
    }

    private static HashSet<string> BuildFeatures(LaunchOptions options)
    {
        var features = new HashSet<string>();
        if (options.WindowWidth is not null) features.Add("has_custom_resolution");
        if (options.JoinServer is not null) features.Add("is_quick_play_multiplayer");
        if (options.JoinWorld is not null) features.Add("is_quick_play_singleplayer");
        return features;
    }

    /// <summary>准备 natives 与旧版资源后启动游戏。路径含系统语言表示不了的字符时改用英文名的联接启动。</summary>
    public Process Launch(LaunchOptions options, Action<string>? onOutput = null)
    {
        var (launcher, mapped) = ForCurrentCodePage(options, onOutput);
        return launcher.LaunchCore(mapped, onOutput);
    }

    /// <summary>
    /// Java 按 ANSI 代码页读自己的路径和命令行参数：路径里有当前系统语言表示不了的字符时，换成指向同一位置的纯英文联接。
    /// 返回用来生成参数和启动的 launcher 和 options（不需要时就是自己和原样的 options）。正式启动、预处理、导出启动脚本都经过这里。
    /// </summary>
    public (GameLauncher Launcher, LaunchOptions Options) ForCurrentCodePage(LaunchOptions options, Action<string>? onOutput = null)
    {
        var root = AsciiPaths.AliasIfNeeded(dir.Root, onOutput);
        var javaHome = Path.GetDirectoryName(Path.GetDirectoryName(options.JavaPath));
        var javaAlias = javaHome is not null && !options.JavaPath.StartsWith(dir.Root, StringComparison.OrdinalIgnoreCase) ? AsciiPaths.AliasIfNeeded(javaHome, onOutput) : null;
        if (root != dir.Root || javaAlias is not null && javaAlias != javaHome)
        {
            string Map(string path)
            {
                path = AsciiPaths.Remap(path, dir.Root, root);
                return javaAlias is not null && javaHome is not null ? AsciiPaths.Remap(path, javaHome, javaAlias) : path;
            }
            var mapped = options with
            {
                JavaPath = Map(options.JavaPath),
                WorkingDirectory = options.WorkingDirectory is { } w ? Map(w) : null,
                ExtraJvmArgs = options.ExtraJvmArgs.Select(a => AsciiPaths.MapArgument(Map(a), onOutput)).ToList(),
                ExtraGameArgs = options.ExtraGameArgs.Select(a => AsciiPaths.MapArgument(Map(a), onOutput)).ToList(),
            };
            return (new GameLauncher(new GameDirectory(root)) { BaseRules = BaseRules, RealRoot = dir.Root, NoTraceRoot = NoTraceRoot }, mapped);
        }
        // 游戏目录和 Java 都是英文路径，参数里也可能带别处的路径（比如外置登录的 -javaagent 指向数据目录）。
        var args = options with
        {
            ExtraJvmArgs = options.ExtraJvmArgs.Select(a => AsciiPaths.MapArgument(a, onOutput)).ToList(),
            ExtraGameArgs = options.ExtraGameArgs.Select(a => AsciiPaths.MapArgument(a, onOutput)).ToList(),
        };
        return (this, args);
    }

    private Process LaunchCore(LaunchOptions options, Action<string>? onOutput)
    {
        var version = dir.ResolveVersion(options.VersionId);
        ExtractNatives(options.VersionId, version);
        var workDir = options.WorkingDirectory ?? dir.Root;
        Directory.CreateDirectory(workDir);
        PrepareLegacyAssets(version, workDir);
        GameLanguage.Prepare(dir, version, workDir);
        if (version.AmberLegacyLibs is { Count: > 0 } fml) Amber.Core.Install.LegacyForge.EnsureLibraries(dir.Root, workDir, fml, onOutput);

        var psi = new ProcessStartInfo(options.JavaPath)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = onOutput is not null,
            RedirectStandardError = onOutput is not null,
            // 启动参数里已让 Java 以 UTF-8 输出，这里必须按 UTF-8 读，否则中文会乱码。
            StandardOutputEncoding = onOutput is not null ? System.Text.Encoding.UTF8 : null,
            StandardErrorEncoding = onOutput is not null ? System.Text.Encoding.UTF8 : null,
            CreateNoWindow = true,
        };
        // 1.6 以前的版本（和 Jay 装的老 Forge）会按系统家目录写文件，先准备指向本实例的隔离家目录。
        var realWorkDir = RealRoot is { } real ? AsciiPaths.Remap(workDir, dir.Root, real) : workDir;
        var legacyHome = IsPreLaunchwrapperHome(version) ? AsciiPaths.LegacyHome(realWorkDir, onOutput) : null;
        string? noTraceDir = null;
        try
        {
            if (legacyHome is not null && !OperatingSystem.IsWindows())
                psi.ArgumentList.Add("-Duser.home=" + legacyHome);
            foreach (var arg in BuildArguments(options, onOutput, out noTraceDir)) psi.ArgumentList.Add(arg);
        }
        catch
        {
            DeleteNoTraceDir(noTraceDir);
            throw;
        }
        if (legacyHome is not null && OperatingSystem.IsWindows())
        {
            psi.Environment["APPDATA"] = legacyHome;
            onOutput?.Invoke(L.F("core.launch.game.legacyappdata", legacyHome));
        }
        else if (legacyHome is not null)
            onOutput?.Invoke(L.F("core.launch.game.legacyhome", legacyHome));

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException(L.T("core.launch.game.javastartfailed"));
        }
        catch
        {
            DeleteNoTraceDir(noTraceDir);
            throw;
        }
        if (noTraceDir is not null)
        {
            // 记下是哪个游戏进程在用：Amber 先退出、崩溃或被结束时，下次启动按这个判断能不能清掉（SweepNoTrace）。
            NoTraceDirs.WriteOwner(noTraceDir, process);
            // 游戏退出后删掉这次生成的配置；被杀毒软件之类占着时过一会儿再试几次。
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = DeleteNoTraceDirWithRetryAsync(noTraceDir);
            if (process.HasExited) _ = DeleteNoTraceDirWithRetryAsync(noTraceDir);
        }
        if (onOutput is not null)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process;
    }

    private string NewNoTraceDir() => Path.Combine(NoTraceRoot, Guid.NewGuid().ToString("N")[..12]);

    /// <summary>只删 Amber 自己在 NoTraceRoot 下建的目录。删掉了（或本来就没有）返回 true。</summary>
    internal bool DeleteNoTraceDir(string? path) =>
        path is null || !NoTraceDirs.IsUnder(NoTraceRoot, path) || NoTraceDirs.TryDelete(path);

    /// <summary>删不掉时按 0.25、0.5、1、2 秒间隔再试；都不行就留给下次启动时的清理。</summary>
    internal async Task<bool> DeleteNoTraceDirWithRetryAsync(string path, int attempts = 5)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (i > 0) await Task.Delay(250 << (i - 1)).ConfigureAwait(false);
            if (DeleteNoTraceDir(path)) return true;
        }
        return false;
    }

    /// <summary>
    /// 启动 Amber 时清掉以前留下的无痕配置目录（Amber 在游戏运行时被关掉、崩溃、注销，或者当时删不掉）。
    /// 还在被运行中的游戏用的、刚建好还没启动游戏的、删不掉的都跳过。返回删掉了几个。
    /// </summary>
    public static int SweepNoTrace(string? root = null) => NoTraceDirs.Sweep(root ?? DefaultNoTraceRoot, DateTime.UtcNow);

    /// <summary>1.6.1（2013-06-25）以前发布的版本，或者 Amber 装的老 Forge。</summary>
    internal static bool IsPreLaunchwrapperHome(VersionJson version) =>
        version.AmberForge is not null
        || DateTimeOffset.TryParse(version.ReleaseTime, out var released) && released < new DateTimeOffset(2013, 6, 25, 0, 0, 0, TimeSpan.Zero);

    internal void ExtractNatives(string versionId, VersionJson version)
    {
        var nativesDir = dir.NativesDir(versionId);
        Directory.CreateDirectory(nativesDir);
        foreach (var lib in LibraryResolver.Resolve(version, dir, BaseRules).Where(l => l.Extract && File.Exists(l.LocalPath)))
        {
            using var zip = ZipFile.OpenRead(lib.LocalPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                if (lib.ExtractExclude.Any(ex => entry.FullName.StartsWith(ex, StringComparison.OrdinalIgnoreCase))) continue;
                // ZipArchiveEntry.Name 只按“/”拆（Windows 打的包里“..\..\x.dll”“C:\x.dll”会原样留着）：自己按两种斜杠取文件名，
                // 带盘符或冒号的条目直接拒绝。
                if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':'))
                    throw new InvalidDataException(L.F("core.game.directory.unsafepath", entry.FullName));
                var name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                if (!IsNativeFile(name)) continue;

                // natives 统一平铺到一个目录，LWJGL 只在 java.library.path 根目录查找。
                var target = GameDirectory.Contained(nativesDir, name);
                if (File.Exists(target) && new FileInfo(target).Length == entry.Length) continue;
                try { entry.ExtractToFile(target, true); }
                catch (IOException) { /* 文件被上一次运行的游戏占用，沿用旧文件 */ }
            }
        }
    }

    private static bool IsNativeFile(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".so", StringComparison.Ordinal)
        || name.EndsWith(".dylib", StringComparison.Ordinal)
        || name.EndsWith(".jnilib", StringComparison.Ordinal);

    private string LegacyAssetsDir(string indexName, string workDir) =>
        indexName is "pre-1.6" ? Path.Combine(workDir, "resources") : Path.Combine(dir.AssetsDir, "virtual", indexName);

    /// <summary>1.7.2 及更早的版本要求资源按原始文件名平铺。</summary>
    internal void PrepareLegacyAssets(VersionJson version, string workDir)
    {
        var id = version.AssetIndex?.Id ?? version.Assets;
        if (id is null) return;
        var indexPath = dir.AssetIndexPath(id);
        if (!File.Exists(indexPath)) return;

        using var stream = File.OpenRead(indexPath);
        var index = JsonSerializer.Deserialize(stream, Json.Options.Info<AssetIndex>());
        if (index is null || !(index.Virtual || index.MapToResources)) return;

        var targetRoot = index.MapToResources ? Path.Combine(workDir, "resources") : LegacyAssetsDir(id, workDir);
        foreach (var (name, obj) in index.Objects)
        {
            var source = dir.AssetObjectPath(obj.Hash);
            // 资源索引里的名字不可信：拼出来必须还在 resources 或 virtual 目录里。
            var target = GameDirectory.Contained(targetRoot, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    /// <summary>展开结构化参数：字符串直接保留，对象按 rules 决定是否加入 value。</summary>
    private static IEnumerable<string> Expand(IEnumerable<JsonElement> elements, RuleEvaluator rules)
    {
        foreach (var element in elements)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                yield return element.GetString()!;
                continue;
            }
            if (element.ValueKind != JsonValueKind.Object) continue;

            var ruleList = element.TryGetProperty("rules", out var r) ? r.Deserialize(Json.Options.Info<List<Rule>>()) : null;
            if (!rules.IsAllowed(ruleList)) continue;
            if (!element.TryGetProperty("value", out var value)) continue;

            if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var v in value.EnumerateArray())
                    if (v.GetString() is { } s) yield return s;
        }
    }

    private static string Substitute(string arg, IReadOnlyDictionary<string, string> vars)
    {
        if (!arg.Contains("${")) return arg;
        foreach (var (key, value) in vars) arg = arg.Replace("${" + key + "}", value);
        return arg;
    }
}
