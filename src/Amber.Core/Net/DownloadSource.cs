using Amber.Core.Localization;
namespace Amber.Core.Net;

/// <summary>把官方地址改写成某个镜像的地址。返回 null 表示该镜像不提供这个文件。</summary>
public interface IDownloadSource
{
    string Name { get; }
    string? Rewrite(string officialUrl);

    /// <summary>这个源对某主机的连接数上限；null 表示不限制。</summary>
    int? HostLimit(string host) => null;
}

/// <summary>官方源：原样返回。</summary>
public sealed class OfficialSource : IDownloadSource
{
    public string Name => L.T("core.net.source.official");
    public string? Rewrite(string officialUrl) => officialUrl;
}

/// <summary>按主机名前缀替换的镜像，BMCLAPI 及其兼容镜像都属于这一类。</summary>
public sealed class PrefixMirrorSource(string name, IReadOnlyDictionary<string, string> prefixMap, int? maxConnections = null) : IDownloadSource
{
    public string Name { get; } = name;

    public int? HostLimit(string host) =>
        maxConnections is not null && prefixMap.Values.Any(to => Uri.TryCreate(to, UriKind.Absolute, out var uri) && uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase))
            ? maxConnections
            : null;

    /// <summary>防止并发设置和分块把连接数放大。没查到公布的上限，取实测够快的值（装 26.3：32 连接 121 秒，16 连接 391 秒），真被限流时靠 429 处理。</summary>
    public const int BmclapiMaxConnections = 32;

    public string? Rewrite(string officialUrl)
    {
        foreach (var (from, to) in prefixMap)
        {
            if (officialUrl.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + officialUrl[from.Length..];
        }
        return null;
    }

    /// <summary>BMCLAPI 公开文档中的地址映射。</summary>
    public static PrefixMirrorSource Bmclapi(string root = "https://bmclapi2.bangbang93.com") => new("BMCLAPI", new Dictionary<string, string>
    {
        ["https://piston-meta.mojang.com/"] = root + "/",
        ["https://launchermeta.mojang.com/"] = root + "/",
        ["https://piston-data.mojang.com/"] = root + "/",
        ["https://launcher.mojang.com/"] = root + "/",
        ["https://resources.download.minecraft.net/"] = root + "/assets/",
        ["https://libraries.minecraft.net/"] = root + "/maven/",
        ["https://maven.minecraftforge.net/"] = root + "/maven/",
        ["https://files.minecraftforge.net/maven/"] = root + "/maven/",
        ["https://maven.neoforged.net/releases/"] = root + "/maven/",
        ["https://maven.quiltmc.org/repository/release/"] = root + "/maven/",
        ["https://maven.fabricmc.net/"] = root + "/maven/",
        ["https://meta.fabricmc.net/"] = root + "/fabric-meta/",
    }, BmclapiMaxConnections);
}

/// <summary>按优先级排列的一组下载源，为每个官方地址给出候选地址列表。</summary>
public sealed class SourceChain(IReadOnlyList<IDownloadSource> sources)
{
    public IReadOnlyList<IDownloadSource> Sources { get; } = sources;

    public int? HostLimit(string host) => Sources.Select(s => s.HostLimit(host)).Min();

    public IReadOnlyList<string> Candidates(string officialUrl)
    {
        var list = new List<string>();
        foreach (var source in Sources)
        {
            var url = source.Rewrite(officialUrl);
            if (url is not null && !list.Contains(url)) list.Add(url);
        }
        // 无论如何都保留官方地址兜底。
        if (!list.Contains(officialUrl)) list.Add(officialUrl);
        return list;
    }

    public static SourceChain OfficialFirst { get; } = new([new OfficialSource(), PrefixMirrorSource.Bmclapi()]);
    public static SourceChain MirrorFirst { get; } = new([PrefixMirrorSource.Bmclapi(), new OfficialSource()]);

    /// <summary>
    /// 默认顺序：系统时区为中国时优先镜像，否则优先官方。
    /// 只用于挑默认值，不联网查 IP，也不据此禁用任何功能。
    /// </summary>
    public static SourceChain Default() => PrefersMirrors() ? MirrorFirst : OfficialFirst;

    /// <summary>系统时区是中国时默认先用镜像。只用于排顺序，不联网查 IP。</summary>
    public static bool PrefersMirrors()
    {
        var tz = TimeZoneInfo.Local.Id;
        return tz is "China Standard Time" or "Asia/Shanghai" or "Asia/Chongqing" or "Asia/Urumqi";
    }
}
