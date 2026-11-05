using Amber.Core.Localization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Amber.Core.Content;

/// <summary>某个加载器的元数据里写的要求：游戏版本范围（fabric 或 maven 语法）和必需前置。</summary>
public sealed record LoaderRequirement(string? MinecraftRange, string Syntax, IReadOnlyList<string> RequiredDeps);

/// <summary>
/// 一个模组 jar 写明支持的加载器、模组 id 和各加载器下的要求。
/// 很多模组一个 jar 同时带 Fabric 和 Forge / NeoForge 的元数据，各自的版本范围和前置不同，要分开看。
/// </summary>
public sealed record ModRequirements(
    IReadOnlySet<string> Loaders,
    IReadOnlyList<string> Ids,
    IReadOnlyDictionary<string, LoaderRequirement> PerLoader)
{
    /// <summary>实例用这个加载器时该看哪一份元数据。</summary>
    public LoaderRequirement? For(string loader) => loader switch
    {
        "quilt" => PerLoader.GetValueOrDefault("quilt") ?? PerLoader.GetValueOrDefault("fabric"),
        "neoforge" => PerLoader.GetValueOrDefault("neoforge") ?? PerLoader.GetValueOrDefault("forge"),
        _ => PerLoader.GetValueOrDefault(loader),
    };
}

public enum CompatLevel { Ok, ViaConnector, NeedsConnector, WrongLoader, WrongVersion, MissingDeps, Unknown }

public sealed record CompatResult(CompatLevel Level, string Text)
{
    /// <summary>完全匹配时的说明（模组页不显示它）。</summary>
    public const string MatchText = "匹配";

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
    internal static ModRequirements? ReadZip(ZipArchive zip, bool nested) =>
        ReadZip(zip, nested, new ArchiveMetadata.Budget(ArchiveMetadata.MaxNestedTotalBytes));

    private static ModRequirements? ReadZip(ZipArchive zip, bool nested, ArchiveMetadata.Budget budget)
    {
        var loaders = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        var per = new Dictionary<string, LoaderRequirement>(StringComparer.Ordinal);

        if (ReadJson(zip, "fabric.mod.json", budget) is JsonObject fabric)
        {
            loaders.Add("fabric");
            AddId(ids, fabric["id"]);
            foreach (var p in fabric["provides"] as JsonArray ?? []) AddId(ids, p);
            string? mc = null;
            var deps = new List<string>();
            if (fabric["depends"] is JsonObject depends)
            {
                foreach (var (key, value) in depends)
                {
                    if (key == "minecraft") mc = Range(value);
                    else if (!Builtin.Contains(key)) deps.Add(key);
                }
            }
            per["fabric"] = new LoaderRequirement(mc, "fabric", deps);
        }
        if (ReadJson(zip, "quilt.mod.json", budget)?["quilt_loader"] is JsonObject quilt)
        {
            loaders.Add("quilt");
            AddId(ids, quilt["id"]);
            string? mc = null;
            var deps = new List<string>();
            foreach (var d in quilt["depends"] as JsonArray ?? [])
            {
                var id = d is JsonValue ? Str(d) : Str(d?["id"]);
                if (id is null) continue;
                if (id == "minecraft") mc ??= d is JsonObject o ? Range(o["versions"]) : null;
                else if (!Builtin.Contains(id) && d?["optional"]?.GetValue<bool>() != true) deps.Add(id);
            }
            per["quilt"] = new LoaderRequirement(mc, "fabric", deps);
        }
        foreach (var (entry, loader) in new[] { ("META-INF/neoforge.mods.toml", "neoforge"), ("META-INF/mods.toml", "forge") })
        {
            if (zip.GetEntry(entry) is not { } e) continue;
            loaders.Add(loader);
            var toml = ArchiveMetadata.ReadText(e, budget: budget);
            foreach (Match m in TomlModId().Matches(toml)) if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
            string? mc = null;
            var deps = new List<string>();
            foreach (var dep in TomlDependencies(toml))
            {
                if (dep.Id == "minecraft") mc ??= dep.Range;
                else if (dep.Required && !Builtin.Contains(dep.Id)) deps.Add(dep.Id);
            }
            per[loader] = new LoaderRequirement(mc, "maven", deps.Distinct().ToList());
        }
        if (loaders.Count == 0 && zip.GetEntry("mcmod.info") is not null) loaders.Add("forge-legacy");
        if (nested)
        {
            // 有 metadata.json 就按它列的；老一点的（比如 1.20.1 的 Connector）没有，就看 META-INF/jarjar/ 下所有 jar。
            IEnumerable<string> paths = ReadJson(zip, "META-INF/jarjar/metadata.json", budget)?["jars"] is JsonArray listed
                ? listed.Select(j => Str(j?["path"])).OfType<string>()
                : zip.Entries.Where(e => e.FullName.StartsWith("META-INF/jarjar/", StringComparison.Ordinal) && e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName);
            var hadOwn = loaders.Count > 0;
            var count = 0;
            foreach (var inner in paths.Distinct(StringComparer.Ordinal))
            {
                if (++count > ArchiveMetadata.MaxNestedJars) throw ArchiveMetadata.TooLarge();
                // 只读模组 id，太大的内嵌 jar（几十 MB 的库）跳过，免得整个读进内存。
                if (zip.GetEntry(inner) is not { } entry || entry.Length > ArchiveMetadata.MaxNestedJarBytes) continue;
                budget.Check(entry.Length);
                try
                {
                    using var memory = new MemoryStream(ArchiveMetadata.ReadBytes(entry, ArchiveMetadata.MaxNestedJarBytes, budget), writable: false);
                    using var innerZip = new ZipArchive(memory, ZipArchiveMode.Read);
                    if (ReadZip(innerZip, nested: false, budget) is not { } child) continue;
                    foreach (var id in child.Ids) if (!ids.Contains(id)) ids.Add(id);
                    if (!hadOwn)
                    {
                        loaders.UnionWith(child.Loaders);
                        foreach (var (k, v) in child.PerLoader) per.TryAdd(k, v);
                    }
                }
                catch (InvalidDataException) { }
            }
        }
        if (loaders.Count == 0) return null;
        return new ModRequirements(loaders, ids.Distinct().ToList(), per);
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
        if (mod is null) return new CompatResult(CompatLevel.Unknown, L.T("core.compat.mod.unreadable"));
        var loader = instanceLoader ?? "vanilla";
        var nativeOk = IsNative(mod, loader);
        var viaConnector = RunsViaConnector(mod, loader);

        if (loader == "vanilla") return new CompatResult(CompatLevel.WrongLoader, L.T("core.compat.mod.noloader"));
        if (!nativeOk && !viaConnector)
        {
            var forgeOnFabric = loader is "fabric" or "quilt" && (mod.Loaders.Contains("forge") || mod.Loaders.Contains("neoforge") || mod.Loaders.Contains("forge-legacy"));
            return new CompatResult(CompatLevel.WrongLoader, forgeOnFabric
                ? L.F("core.compat.mod.forgeonfabric", LoaderNames(mod))
                : L.F("core.compat.mod.wrongloader", LoaderNames(mod), loader));
        }
        // 只看实例实际会用的那份元数据（同一个 jar 里 Fabric 和 Forge 的要求可能不同）。
        var req = mod.For(viaConnector ? "fabric" : loader);
        if (req?.MinecraftRange is { } range && !VersionMatches(mcVersion, range, req.Syntax))
            return new CompatResult(CompatLevel.WrongVersion, L.F("core.compat.mod.wrongversion", range, mcVersion));
        var missing = (req?.RequiredDeps ?? []).Where(d => !installedIds.Contains(d) && !(viaConnector && (d is "fabric-api" or "fabric" || HasNativeAlias(d, installedIds)))).ToList();
        if (missing.Count > 0) return new CompatResult(CompatLevel.MissingDeps, L.F("core.compat.mod.missingdependencies", string.Join(L.T("common.list_separator"), missing)));
        if (viaConnector)
            return hasConnector
                ? new CompatResult(CompatLevel.ViaConnector, L.T("core.compat.mod.viaconnector"))
                : new CompatResult(CompatLevel.NeedsConnector, L.F("core.compat.mod.needsconnector", loader));
        if (loader == "quilt" && !mod.Loaders.Contains("quilt")) return new CompatResult(CompatLevel.Ok, L.T("core.compat.mod.quiltsupport"));
        return new CompatResult(CompatLevel.Ok, CompatResult.MatchText);
    }

    /// <summary>
    /// 经 Connector 运行时，Fabric 模组要的前置可以由原生 Forge / NeoForge 版满足，而两边的模组 id 常常只差连字符和下划线
    /// （Fabric 上是 cloth-config，NeoForge 上是 cloth_config）；Connector 自己也按这类别名对应。
    /// </summary>
    internal static bool HasNativeAlias(string fabricId, IReadOnlySet<string> installedIds)
    {
        var key = AliasKey(fabricId);
        return installedIds.Any(id => AliasKey(id) == key);
    }

    private static string AliasKey(string id) => id.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>这个加载器能直接加载这个模组。</summary>
    public static bool IsNative(ModRequirements mod, string? loader) => loader switch
    {
        "fabric" => mod.Loaders.Contains("fabric"),
        "quilt" => mod.Loaders.Contains("quilt") || mod.Loaders.Contains("fabric"),
        "forge" => mod.Loaders.Contains("forge") || mod.Loaders.Contains("forge-legacy"),
        // NeoForge 1.20.1 就是 Forge；之后也还认 mods.toml 的老包，但不保证。
        "neoforge" => mod.Loaders.Contains("neoforge") || mod.Loaders.Contains("forge"),
        _ => false,
    };

    /// <summary>这个 Fabric 模组在 Forge / NeoForge 上要经 Sinytra Connector 运行。模组页和预处理都按这一条判断。</summary>
    public static bool RunsViaConnector(ModRequirements mod, string? loader) =>
        loader is "forge" or "neoforge" && !IsNative(mod, loader) && mod.Loaders.Contains("fabric");

    private static string LoaderNames(ModRequirements mod) => string.Join(" / ", mod.Loaders.Select(l => l switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "forge-legacy" => L.T("core.compat.loader.legacyforge"),
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

    private static JsonNode? ReadJson(ZipArchive zip, string name, ArchiveMetadata.Budget budget)
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        return JsonNode.Parse(ArchiveMetadata.ReadText(entry, budget: budget), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
}
