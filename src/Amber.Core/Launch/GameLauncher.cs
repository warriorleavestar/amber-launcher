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
    public string LauncherName { get; init; } = "Amber";
    public string LauncherVersion { get; init; } = "0.1";
}

/// <summary>拼装启动参数、准备 natives 与旧版资源，然后启动游戏进程。</summary>
public sealed class GameLauncher(GameDirectory dir)
{
    public GameDirectory GameDir => dir;

    public RuleEvaluator BaseRules { get; init; } = RuleEvaluator.Current;

    /// <summary>改用英文名联接启动时，联接背后真正的游戏目录。旧版本的家目录联接要直接指向真目录，不能联接套联接。</summary>
    internal string? RealRoot { get; init; }

    /// <summary>生成完整命令行（不含 java 本身），可用于导出启动脚本。</summary>
    public List<string> BuildArguments(LaunchOptions options)
    {
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
            if (File.Exists(logPath)) args.Add(logArg.Replace("${path}", logPath));
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
        args.AddRange(options.ExtraJvmArgs);

        args.Add(version.MainClass ?? throw new InvalidDataException($"{options.VersionId} 没有 mainClass"));

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
            return (new GameLauncher(new GameDirectory(root)) { BaseRules = BaseRules, RealRoot = dir.Root }, mapped);
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
        foreach (var arg in BuildArguments(options)) psi.ArgumentList.Add(arg);
        // 1.6 以前的版本（和 Amber 装的老 Forge）把 %APPDATA%\.minecraft 当家目录，改成指向这个实例。
        var realWorkDir = RealRoot is { } real ? AsciiPaths.Remap(workDir, dir.Root, real) : workDir;
        if (IsPreLaunchwrapperHome(version) && AsciiPaths.LegacyHome(realWorkDir, onOutput) is { } home)
        {
            psi.Environment["APPDATA"] = home;
            onOutput?.Invoke($"[Amber] 旧版本：这次游戏的 APPDATA 指向 {home}（其中 .minecraft 就是实例目录），不会写到你自己的 .minecraft");
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Java 进程");
        if (onOutput is not null)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process;
    }

    /// <summary>1.6.1（2013-06-25）以前发布的版本，或者 Amber 装的老 Forge。</summary>
    internal static bool IsPreLaunchwrapperHome(VersionJson version) =>
        version.AmberForge is not null
        || DateTimeOffset.TryParse(version.ReleaseTime, out var released) && released < new DateTimeOffset(2013, 6, 25, 0, 0, 0, TimeSpan.Zero);

    private void ExtractNatives(string versionId, VersionJson version)
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
                if (!IsNativeFile(entry.Name)) continue;

                // natives 统一平铺到一个目录，LWJGL 只在 java.library.path 根目录查找。
                var target = Path.Combine(nativesDir, entry.Name);
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
    private void PrepareLegacyAssets(VersionJson version, string workDir)
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
            var target = Path.Combine(targetRoot, name.Replace('/', Path.DirectorySeparatorChar));
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
