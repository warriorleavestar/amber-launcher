using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Amber.Core.Content;

/// <summary>一个模组 jar 写明支持的加载器、游戏版本要求和必需依赖。</summary>
public sealed record ModRequirements(
    IReadOnlySet<string> Loaders,
    IReadOnlyList<string> Ids,
    string? MinecraftRange,
    string MinecraftRangeSyntax,
    IReadOnlyList<string> RequiredDeps);

public enum CompatLevel { Ok, ViaConnector, NeedsConnector, WrongLoader, WrongVersion, MissingDeps, Unknown }

public sealed record CompatResult(CompatLevel Level, string Text)
{
    public bool IsProblem => Level is CompatLevel.NeedsConnector or CompatLevel.WrongLoader or CompatLevel.WrongVersion or CompatLevel.MissingDeps;
}

/// <summary>
/// 模组和实例的匹配检查：加载器对不对、游戏版本在不在范围里、必需依赖装没装。
/// Quilt 能直接用 Fabric 模组；Forge / NeoForge 上的 Fabric 模组要靠 Sinytra Connector（不是每个都能用）；
/// Fabric / Quilt 上的 Forge 模组没有可靠的兼容层。
/// </summary>
public static partial class ModCompat
{
    public const string ConnectorProject = "connector";
    public const string ForgifiedFabricApiProject = "forgified-fabric-api";

    /// <summary>这些依赖由加载器自己提供，不算缺失。</summary>
    private static readonly HashSet<string> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "forge", "neoforge", "fml", "javafml", "mixinextras",
    };

    public static ModRequirements? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return ReadZip(zip, nested: true);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读一个 jar 的元数据。nested 为真时，再看 META-INF/jarjar/metadata.json 里列出的内嵌 jar（只看一层）：
    /// Sinytra Connector 这类“外壳”jar 自己没有元数据，真正的模组 id 在内嵌 jar 里。
    /// </summary>
    private static ModRequirements? ReadZip(ZipArchive zip, bool nested)
    {
        {
            var loaders = new HashSet<string>(StringComparer.Ordinal);
            var ids = new List<string>();
            var deps = new List<string>();
            string? mc = null;
            var syntax = "fabric";

            if (ReadJson(zip, "fabric.mod.json") is JsonObject fabric)
            {
                loaders.Add("fabric");
                AddId(ids, fabric["id"]);
                foreach (var p in fabric["provides"] as JsonArray ?? []) AddId(ids, p);
                if (fabric["depends"] is JsonObject depends)
                {
                    foreach (var (key, value) in depends)
                    {
                        if (key == "minecraft") mc = Range(value);
                        else if (!Builtin.Contains(key)) deps.Add(key);
                    }
                }
            }
            if (ReadJson(zip, "quilt.mod.json")?["quilt_loader"] is JsonObject quilt)
            {
                loaders.Add("quilt");
                AddId(ids, quilt["id"]);
                foreach (var d in quilt["depends"] as JsonArray ?? [])
                {
                    var id = d is JsonValue ? Str(d) : Str(d?["id"]);
                    if (id is null) continue;
                    if (id == "minecraft") mc ??= d is JsonObject o ? Range(o["versions"]) : null;
                    else if (!Builtin.Contains(id) && d?["optional"]?.GetValue<bool>() != true) deps.Add(id);
                }
            }
            foreach (var (entry, loader) in new[] { ("META-INF/neoforge.mods.toml", "neoforge"), ("META-INF/mods.toml", "forge") })
            {
                if (zip.GetEntry(entry) is not { } e) continue;
                loaders.Add(loader);
                using var reader = new StreamReader(e.Open());
                var toml = reader.ReadToEnd();
                foreach (Match m in TomlModId().Matches(toml)) if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
                foreach (var dep in TomlDependencies(toml))
                {
                    if (dep.Id == "minecraft") { mc ??= dep.Range; syntax = mc == dep.Range ? "maven" : syntax; }
                    else if (dep.Required && !Builtin.Contains(dep.Id)) deps.Add(dep.Id);
                }
            }
            if (loaders.Count == 0 && zip.GetEntry("mcmod.info") is not null) loaders.Add("forge-legacy");
            if (nested && ReadJson(zip, "META-INF/jarjar/metadata.json")?["jars"] is JsonArray jars)
            {
                var hadOwn = loaders.Count > 0;
                foreach (var jar in jars)
                {
                    if (Str(jar?["path"]) is not { } inner || zip.GetEntry(inner) is not { } entry) continue;
                    try
                    {
                        using var memory = new MemoryStream();
                        using (var s = entry.Open()) s.CopyTo(memory);
                        memory.Position = 0;
                        using var innerZip = new ZipArchive(memory, ZipArchiveMode.Read);
                        if (ReadZip(innerZip, nested: false) is not { } child) continue;
                        foreach (var id in child.Ids) if (!ids.Contains(id)) ids.Add(id);
                        if (!hadOwn) loaders.UnionWith(child.Loaders);
                    }
                    catch (InvalidDataException) { }
                }
            }
            if (loaders.Count == 0) return null;
            return new ModRequirements(loaders, ids.Distinct().ToList(), mc, syntax, deps.Distinct().ToList());
        }
    }

    private static void AddId(List<string> ids, JsonNode? node)
    {
        if (Str(node) is { } id && !ids.Contains(id)) ids.Add(id);
    }

    private static string? Range(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join(" || ", a.Select(x => Str(x)).OfType<string>()),
        _ => null,
    };

    private sealed record TomlDep(string Id, string? Range, bool Required);

    /// <summary>[[dependencies.xxx]] 段：modId、versionRange、mandatory（Forge）或 type（NeoForge）。</summary>
    private static IEnumerable<TomlDep> TomlDependencies(string toml)
    {
        foreach (var section in toml.Split("[[dependencies", StringSplitOptions.None).Skip(1))
        {
            var body = section.Split("\n[[", 2)[0];
            var id = TomlString(body, "modId");
            if (id is null) continue;
            var mandatory = Regex.Match(body, @"(?m)^\s*mandatory\s*=\s*(true|false)").Groups[1].Value;
            var type = TomlString(body, "type");
            var required = type is not null ? type.Equals("required", StringComparison.OrdinalIgnoreCase) : mandatory != "false";
            yield return new TomlDep(id, TomlString(body, "versionRange"), required);
        }
    }

    private static string? TomlString(string text, string key)
    {
        var m = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*[""'](.*?)[""']");
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?m)^\s*modId\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex TomlModId();

    /// <summary>
    /// 检查一个模组能不能在这个实例上用。installedIds 是实例里所有启用模组的 id（含 provides），hasConnector 表示装了 Sinytra Connector。
    /// </summary>
    public static CompatResult Check(ModRequirements? mod, string? instanceLoader, string mcVersion, IReadOnlySet<string> installedIds, bool hasConnector)
    {
        if (mod is null) return new CompatResult(CompatLevel.Unknown, "读不到模组信息");
        var loader = instanceLoader ?? "vanilla";
        var nativeOk = loader switch
        {
            "fabric" => mod.Loaders.Contains("fabric"),
            "quilt" => mod.Loaders.Contains("quilt") || mod.Loaders.Contains("fabric"),
            "forge" => mod.Loaders.Contains("forge") || mod.Loaders.Contains("forge-legacy"),
            // NeoForge 1.20.1 就是 Forge；之后也还认 mods.toml 的老包，但不保证。
            "neoforge" => mod.Loaders.Contains("neoforge") || mod.Loaders.Contains("forge"),
            _ => false,
        };
        var viaConnector = !nativeOk && loader is "forge" or "neoforge" && mod.Loaders.Contains("fabric");

        if (loader == "vanilla") return new CompatResult(CompatLevel.WrongLoader, "这个版本没装加载器，模组不会生效");
        if (!nativeOk && !viaConnector)
        {
            var forgeOnFabric = loader is "fabric" or "quilt" && (mod.Loaders.Contains("forge") || mod.Loaders.Contains("neoforge") || mod.Loaders.Contains("forge-legacy"));
            return new CompatResult(CompatLevel.WrongLoader, forgeOnFabric
                ? $"这是 {LoaderNames(mod)} 模组，Fabric / Quilt 上没有可靠的办法运行 Forge 模组"
                : $"这是 {LoaderNames(mod)} 模组，和这个实例的 {loader} 不匹配");
        }
        if (mod.MinecraftRange is { } range && !VersionMatches(mcVersion, range, mod.MinecraftRangeSyntax))
            return new CompatResult(CompatLevel.WrongVersion, $"需要游戏版本 {range}，这个实例是 {mcVersion}");
        var missing = mod.RequiredDeps.Where(d => !installedIds.Contains(d) && !(viaConnector && d is "fabric-api" or "fabric")).ToList();
        if (missing.Count > 0) return new CompatResult(CompatLevel.MissingDeps, "缺少前置：" + string.Join("、", missing));
        if (viaConnector)
            return hasConnector
                ? new CompatResult(CompatLevel.ViaConnector, "Fabric 模组，经 Sinytra Connector 运行（不是每个都能用）")
                : new CompatResult(CompatLevel.NeedsConnector, "这是 Fabric 模组，要装 Sinytra Connector 才能在 " + loader + " 上运行");
        if (loader == "quilt" && !mod.Loaders.Contains("quilt")) return new CompatResult(CompatLevel.Ok, "Fabric 模组，Quilt 直接支持");
        return new CompatResult(CompatLevel.Ok, "匹配");
    }

    private static string LoaderNames(ModRequirements mod) => string.Join(" / ", mod.Loaders.Select(l => l switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "forge-legacy" => "老 Forge",
        _ => l,
    }));

    // ---------------- 版本范围 ----------------

    /// <summary>fabric 语法：空格分隔是“且”，|| 是“或”，支持 = &gt;= &lt;= &gt; &lt; ~ ^ 和 1.20.x 通配。maven 语法：[1.20,1.21) 这类区间，逗号分隔多个区间是“或”。</summary>
    public static bool VersionMatches(string version, string range, string syntax)
    {
        range = range.Trim();
        if (range is "" or "*") return true;
        try
        {
            return syntax == "maven" ? MavenMatches(version, range) : FabricMatches(version, range);
        }
        catch (FormatException)
        {
            // 写法看不懂就不报错，免得误报。
            return true;
        }
    }

    private static bool FabricMatches(string version, string range) =>
        range.Split("||").Any(alternative => alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(p => FabricPredicate(version, p)));

    private static bool FabricPredicate(string version, string p)
    {
        if (p is "*" or "x") return true;
        string[] ops = [">=", "<=", ">", "<", "=", "~", "^"];
        var op = ops.FirstOrDefault(p.StartsWith) ?? "";
        var target = p[op.Length..].Trim();
        if (target.Contains('x') || target.Contains('X') || target.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = target.TrimEnd('x', 'X', '*').TrimEnd('.');
            return version == prefix || version.StartsWith(prefix + ".", StringComparison.Ordinal);
        }
        var c = Compare(version, target);
        return op switch
        {
            ">=" => c >= 0,
            "<=" => c <= 0,
            ">" => c > 0,
            "<" => c < 0,
            "~" => c >= 0 && SameUpTo(version, target, Math.Max(2, Parts(target).Length - 1)),
            "^" => c >= 0 && SameUpTo(version, target, 1),
            _ => c == 0,
        };
    }

    private static bool MavenMatches(string version, string range)
    {
        foreach (Match m in MavenInterval().Matches(range))
        {
            var (open, low, high, close) = (m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Success ? m.Groups[3].Value.Trim() : null, m.Groups[4].Value);
            if (high is null)
            {
                if (Compare(version, low) == 0) return true;
                continue;
            }
            var lowOk = low.Length == 0 || (open == "[" ? Compare(version, low) >= 0 : Compare(version, low) > 0);
            var highOk = high.Length == 0 || (close == "]" ? Compare(version, high) <= 0 : Compare(version, high) < 0);
            if (lowOk && highOk) return true;
        }
        // 没有方括号的单个版本号，Maven 里表示“推荐这个版本”，不当成硬限制。
        return !range.Contains('[') && !range.Contains('(') || false;
    }

    [GeneratedRegex(@"([\[(])([^,\])]*)(?:,([^\])]*))?([\])])")]
    private static partial Regex MavenInterval();

    private static int[] Parts(string v)
    {
        var core = v.Split('-', '+')[0];
        var parts = core.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out result[i])) throw new FormatException(v);
        return result;
    }

    private static int Compare(string a, string b)
    {
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    private static bool SameUpTo(string a, string b, int count)
    {
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < count; i++)
            if ((i < x.Length ? x[i] : 0) != (i < y.Length ? y[i] : 0)) return false;
        return true;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? ReadJson(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        using var stream = entry.Open();
        return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
}
