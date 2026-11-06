using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;

namespace Amber.Core.Bake;

/// <summary>一个可用的烘焙器（实验）：调用通用加载器原型的 amber.ul.Bake。</summary>
public sealed record BakeToolkit(string Root, IReadOnlyList<string> Classpath, string MappingsDir)
{
    public const string RootVariable = "AMBER_BAKE_LOADER";

    /// <summary>原型目录：要有 build/loader、build/loader-cp.txt、mc/mappings-amber。找不到返回 null。</summary>
    public static BakeToolkit? Find(string? configured)
    {
        var root = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(RootVariable) : configured;
        if (string.IsNullOrWhiteSpace(root)) return null;
        root = Path.GetFullPath(root.Trim());
        var classes = Path.Combine(root, "build", "loader");
        var list = Path.Combine(root, "build", "loader-cp.txt");
        var mappings = Path.Combine(root, "mc", "mappings-amber");
        if (!Directory.Exists(classes) || !File.Exists(list) || !Directory.Exists(mappings)) return null;
        var entries = File.ReadAllText(list).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => Path.GetFullPath(Path.Combine(root, e)));
        return new BakeToolkit(root, [classes, .. entries], mappings);
    }
}

/// <summary>烘焙进度的一行（来自加载器 stdout 的 progress / problem）。</summary>
public sealed record BakeEvent(string Stage, string Message, bool IsProblem, bool IsError);

/// <summary>bake-instance 的结果：启动时代替原版主类和 classpath。</summary>
public sealed record BakedLaunch(string MainClass, IReadOnlyList<string> Classpath, IReadOnlyList<string> JvmArgs, IReadOnlyList<string> GameArgsPrefix, string StateFile, string StateKey);

/// <summary>烘焙失败：消息已经本地化，Problems 是加载器报的依赖问题。</summary>
public sealed class BakeException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// 实验功能“烘焙”：模组下载后预先重映射（bake-mod），实例变化后预先解析和打补丁（bake-instance），
/// 启动时用加载器的 amber.ul.Launcher 读烘焙结果。每个阶段一个 Java 进程，取消时直接结束它。
/// 只支持加载器声明支持的游戏版本，只处理 Fabric / Quilt / 原版实例。
/// </summary>
public sealed class BakeService(BakeToolkit toolkit, string cacheRoot)
{
    public const string StateFolder = ".amber-ul";
    public BakeToolkit Toolkit => toolkit;
    public string CacheRoot => cacheRoot;

    private static readonly string[] SupportedLoaders = ["fabric", "quilt"];

    /// <summary>这个实例能不能烘焙。不能时 reason 是本地化的原因。</summary>
    public static bool Supports(GameInstance instance, IReadOnlyCollection<string> supportedMc, out string reason)
    {
        if (!supportedMc.Contains(instance.McVersion))
        {
            reason = L.F("core.bake.unsupported_version", instance.McVersion, string.Join(L.T("core.mp.format.listseparator"), supportedMc));
            return false;
        }
        if (instance.Loader is { } loader && !SupportedLoaders.Contains(loader))
        {
            reason = L.F("core.bake.unsupported_loader", loader);
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>加载器的版本信息：支持的游戏版本、协议号。</summary>
    public async Task<(string LoaderVersion, int Protocol, List<string> SupportedMc)> VersionAsync(string javaPath, CancellationToken ct)
    {
        var result = await RunAsync(javaPath, "version", null, null, ct);
        var mcs = result["supportedMc"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        return (result["loaderVersion"]?.GetValue<string>() ?? "", result["protocol"]?.GetValue<int>() ?? 0, mcs);
    }

    /// <summary>原版的游戏 jar 和库（不含加载器自己的库：烘焙后由通用加载器代替 Fabric / Quilt）。</summary>
    public static (string GameJar, List<string> Libraries) GameInputs(GameDirectory dir, GameInstance instance)
    {
        var vanilla = dir.ResolveVersion(instance.McVersion);
        var libraries = LibraryResolver.Resolve(vanilla, dir, RuleEvaluator.Current).Where(l => l.OnClasspath).Select(l => l.LocalPath).ToList();
        return (dir.VersionJarPath(vanilla.Jar ?? instance.McVersion), libraries);
    }

    /// <summary>启用的模组 jar（按文件名排序，结果稳定）。</summary>
    public static List<string> ModJars(GameInstance instance) =>
        !Directory.Exists(instance.ModsDir) ? [] :
        Directory.EnumerateFiles(instance.ModsDir, "*.jar").Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>阶段 1：逐个模组烘焙。缓存按内容寻址，没变的模组加载器直接命中。</summary>
    public async Task<int> BakeModsAsync(string javaPath, GameDirectory dir, GameInstance instance, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var (gameJar, libraries) = GameInputs(dir, instance);
        var mods = ModJars(instance);
        for (var i = 0; i < mods.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BakeEvent("bake-mod", L.F("core.bake.mod_progress", i + 1, mods.Count, Path.GetFileName(mods[i])), false, false));
            var request = new JsonObject
            {
                ["schema"] = "amber-ul/bake-mod-request@1",
                ["mcVersion"] = instance.McVersion,
                ["modJar"] = mods[i],
                ["cacheRoot"] = cacheRoot,
                ["mappingsDir"] = toolkit.MappingsDir,
                ["gameJar"] = gameJar,
                ["libraries"] = new JsonArray([.. libraries.Select(l => (JsonNode)l)]),
            };
            await RunAsync(javaPath, "bake-mod", request, progress, ct);
        }
        return mods.Count;
    }

    /// <summary>阶段 2：解析依赖、按模组集打补丁，写 &lt;实例&gt;/.amber-ul/state.json。</summary>
    public async Task<BakedLaunch> BakeInstanceAsync(string javaPath, int javaMajor, GameDirectory dir, GameInstance instance, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var (gameJar, libraries) = GameInputs(dir, instance);
        var mods = ModJars(instance);
        var request = new JsonObject
        {
            ["schema"] = "amber-ul/bake-instance-request@1",
            ["mcVersion"] = instance.McVersion,
            ["side"] = "client",
            ["gameJar"] = gameJar,
            ["libraries"] = new JsonArray([.. libraries.Select(l => (JsonNode)l)]),
            ["mods"] = new JsonArray([.. mods.Select(m => (JsonNode)new JsonObject { ["path"] = m })]),
            ["javaMajor"] = javaMajor,
            ["instanceDir"] = instance.WorkDir,
            ["gameDir"] = instance.WorkDir,
            ["cacheRoot"] = cacheRoot,
            ["mappingsDir"] = toolkit.MappingsDir,
        };
        var result = await RunAsync(javaPath, "bake-instance", request, progress, ct);
        var launch = ParseLaunch(result);
        BakeRecord.Write(instance.WorkDir, new BakeRecord(InputKey(dir, instance, javaMajor), launch));
        return launch;
    }

    internal static BakedLaunch ParseLaunch(JsonObject result)
    {
        var launch = result["launch"]?.AsObject() ?? throw new InvalidDataException(L.T("core.bake.bad_result"));
        static List<string> Strings(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        return new BakedLaunch(
            launch["mainClass"]?.GetValue<string>() ?? throw new InvalidDataException(L.T("core.bake.bad_result")),
            Strings(launch["classpath"]),
            Strings(launch["jvmArgs"]),
            Strings(launch["gameArgsPrefix"]),
            launch["stateFile"]?.GetValue<string>() ?? "",
            result["stateKey"]?.GetValue<string>() ?? "");
    }

    /// <summary>
    /// 实例的烘焙输入指纹：游戏版本、游戏 jar 和库、启用模组、Java 主版本，以及加载器和映射的实际输入。
    /// 游戏文件用大小和修改时间；体积较小的加载器、依赖和映射按内容判断，同目录重新构建也会失效。
    /// 只读 classpath 和当前版本实际使用的映射，不扫描缓存或烘焙产物。
    /// </summary>
    public string InputKey(GameDirectory dir, GameInstance instance, int javaMajor)
    {
        var (gameJar, libraries) = GameInputs(dir, instance);
        var text = new StringBuilder("bake-input-v2|").Append(instance.McVersion).Append('|').Append(javaMajor).Append('|').Append(toolkit.Root);
        AppendStamp(text, gameJar);
        foreach (var library in libraries) AppendStamp(text, library);
        foreach (var mod in ModJars(instance)) AppendStamp(text, mod);
        // 保留 classpath 顺序：同名类或资源按这个顺序加载。类目录里也可能有加载器读取的资源。
        foreach (var entry in toolkit.Classpath)
        {
            text.Append("|classpath:").Append(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    AppendContent(text, file);
            }
            else AppendContent(text, entry);
        }
        // 与原型 ensureMappings 的选择一致；其他 MC 版本的映射和生成的缓存不影响本次输入。
        var merged = Path.Combine(toolkit.MappingsDir, "merged.tiny");
        if (File.Exists(merged)) AppendContent(text, merged);
        else
        {
            AppendContent(text, Path.Combine(toolkit.MappingsDir, $"intermediary-{instance.McVersion}-v2.jar"));
            AppendContent(text, Path.Combine(toolkit.MappingsDir, "client_mappings.txt"));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void AppendStamp(StringBuilder text, string file) => text.Append('|').Append(file).Append(':').Append(Stamp(file));

    private static void AppendContent(StringBuilder text, string file)
    {
        text.Append('|').Append(file).Append(':');
        if (!File.Exists(file))
        {
            text.Append("missing");
            return;
        }
        using var input = File.OpenRead(file);
        text.Append(Convert.ToHexStringLower(SHA256.HashData(input)));
    }

    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    /// <summary>上次的烘焙结果还能不能直接用（输入没变、状态文件还在）。</summary>
    public BakedLaunch? Current(GameDirectory dir, GameInstance instance, int javaMajor)
    {
        var record = BakeRecord.Read(instance.WorkDir);
        if (record is null || !File.Exists(record.Launch.StateFile)) return null;
        return record.Key == InputKey(dir, instance, javaMajor) ? record.Launch : null;
    }

    /// <summary>运行一个阶段，返回 result 对象。stdout 每行一个 JSON；stderr 只留最后几行放进错误信息。</summary>
    internal async Task<JsonObject> RunAsync(string javaPath, string stage, JsonObject? request, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        string? requestFile = null;
        var psi = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(Path.PathSeparator, toolkit.Classpath.Select(p => AsciiPaths.MapArgument(p))));
        psi.ArgumentList.Add("amber.ul.Bake");
        psi.ArgumentList.Add(stage);
        if (request is not null)
        {
            Directory.CreateDirectory(cacheRoot);
            requestFile = Path.Combine(cacheRoot, $"request-{stage}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(requestFile, MapPaths(request).ToJsonString(), new UTF8Encoding(false), ct);
            psi.ArgumentList.Add("--request");
            psi.ArgumentList.Add(AsciiPaths.MapArgument(requestFile));
        }

        var problems = new List<string>();
        var stderr = new Queue<string>();
        JsonObject? result = null;
        string? error = null;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L.T("core.launch.game.javastartfailed"));
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.Enqueue(e.Data);
                if (stderr.Count > 8) stderr.Dequeue();
            }
        };
        process.BeginErrorReadLine();
        try
        {
            using var cancel = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var item = ParseLine(line);
                if (item is null) continue;
                switch (item["type"]?.GetValue<string>())
                {
                    case "progress":
                        if (item["step"]?.GetValue<string>() != "log")
                            progress?.Report(new BakeEvent(stage, item["message"]?.GetValue<string>() ?? "", false, false));
                        break;
                    case "problem":
                        var text = DescribeProblem(item);
                        var isError = item["severity"]?.GetValue<string>() == "error";
                        if (isError) problems.Add(text);
                        progress?.Report(new BakeEvent(stage, text, true, isError));
                        break;
                    case "result":
                        result = item;
                        break;
                    case "error":
                        error = item["message"]?.GetValue<string>();
                        break;
                }
            }
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            if (requestFile is not null) try { File.Delete(requestFile); } catch (IOException) { }
        }
        if (process.ExitCode == 0 && result is not null) return result;
        string tail;
        lock (stderr) tail = string.Join(" / ", stderr);
        var reason = error ?? (problems.Count > 0 ? string.Join(L.T("core.mp.format.listseparator"), problems.Take(3)) : tail);
        throw new BakeException(process.ExitCode == 2 ? L.F("core.bake.dependency_failed", reason) : L.F("core.bake.failed", stage, reason), problems);
    }

    internal static JsonObject? ParseLine(string line)
    {
        if (!line.StartsWith('{')) return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    internal static string DescribeProblem(JsonObject problem)
    {
        var mod = problem["modId"]?.GetValue<string>() ?? problem["file"]?.GetValue<string>() ?? "";
        var message = problem["message"]?.GetValue<string>() ?? problem["code"]?.GetValue<string>() ?? "";
        return mod.Length > 0 ? $"{mod}: {message}" : message;
    }

    /// <summary>请求里的路径都换成 Java 能读的形式（中文路径在非中文系统上改用英文联接）。</summary>
    private static JsonNode MapPaths(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : MapPaths(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(n => n is null ? null : MapPaths(n))]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(AsciiPaths.MapArgument(s)),
        _ => node.DeepClone(),
    };
}

/// <summary>&lt;实例&gt;/.amber-ul/jay-bake.json：上次烘焙的输入指纹和启动参数。</summary>
public sealed record BakeRecord(string Key, BakedLaunch Launch)
{
    public const string FileName = "jay-bake.json";

    public static string PathFor(string workDir) => Path.Combine(workDir, BakeService.StateFolder, FileName);

    public static void Write(string workDir, BakeRecord record)
    {
        var path = PathFor(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JsonObject
        {
            ["key"] = record.Key,
            ["mainClass"] = record.Launch.MainClass,
            ["classpath"] = new JsonArray([.. record.Launch.Classpath.Select(s => (JsonNode)s)]),
            ["jvmArgs"] = new JsonArray([.. record.Launch.JvmArgs.Select(s => (JsonNode)s)]),
            ["gameArgsPrefix"] = new JsonArray([.. record.Launch.GameArgsPrefix.Select(s => (JsonNode)s)]),
            ["stateFile"] = record.Launch.StateFile,
            ["stateKey"] = record.Launch.StateKey,
        };
        var temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    public static BakeRecord? Read(string workDir)
    {
        var path = PathFor(workDir);
        if (!File.Exists(path)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            static List<string> Strings(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            var launch = new BakedLaunch(o["mainClass"]?.GetValue<string>() ?? "", Strings(o["classpath"]), Strings(o["jvmArgs"]),
                Strings(o["gameArgsPrefix"]), o["stateFile"]?.GetValue<string>() ?? "", o["stateKey"]?.GetValue<string>() ?? "");
            return launch.MainClass.Length == 0 ? null : new BakeRecord(o["key"]?.GetValue<string>() ?? "", launch);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }
}
