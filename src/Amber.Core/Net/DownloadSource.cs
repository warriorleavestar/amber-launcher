namespace Amber.Core.Net;

/// <summary>把官方地址改写成某个镜像的地址。返回 null 表示该镜像不提供这个文件。</summary>
public interface IDownloadSource
{
    string Name { get; }
    string? Rewrite(string officialUrl);
}

/// <summary>官方源：原样返回。</summary>
public sealed class OfficialSource : IDownloadSource
{
    public string Name => "官方";
    public string? Rewrite(string officialUrl) => officialUrl;
}

/// <summary>按主机名前缀替换的镜像，BMCLAPI 及其兼容镜像都属于这一类。</summary>
public sealed class PrefixMirrorSource(string name, IReadOnlyDictionary<string, string> prefixMap) : IDownloadSource
{
    public string Name { get; } = name;

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
    });
}

/// <summary>按优先级排列的一组下载源，为每个官方地址给出候选地址列表。</summary>
public sealed class SourceChain(IReadOnlyList<IDownloadSource> sources)
{
    public IReadOnlyList<IDownloadSource> Sources { get; } = sources;

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
    public static SourceChain Default()
    {
        var tz = TimeZoneInfo.Local.Id;
        var china = tz is "China Standard Time" or "Asia/Shanghai" or "Asia/Chongqing" or "Asia/Urumqi";
        return china ? MirrorFirst : OfficialFirst;
    }
}
