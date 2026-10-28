namespace Amber.Core.Game;

/// <summary>一个需要存在于本地的库文件。</summary>
public sealed record LibraryFile(
    string LocalPath,
    string? Url,
    string? Sha1,
    long? Size,
    bool OnClasspath,
    bool Extract,
    IReadOnlyList<string> ExtractExclude);

/// <summary>把版本的 libraries 按当前系统规则展开成具体文件。</summary>
public static class LibraryResolver
{
    private const string DefaultMaven = "https://libraries.minecraft.net/";

    public static List<LibraryFile> Resolve(VersionJson version, GameDirectory dir, RuleEvaluator rules)
    {
        var result = new List<LibraryFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in version.Libraries)
        {
            if (!rules.IsAllowed(lib.Rules)) continue;
            var coord = MavenCoordinate.Parse(lib.Name);
            var exclude = (IReadOnlyList<string>?)lib.Extract?.Exclude ?? [];

            // 旧格式 natives：同一个库额外带一个系统相关的 classifier 包。
            if (lib.Natives is not null)
            {
                if (lib.Natives.TryGetValue(rules.OsName, out var classifier))
                {
                    classifier = classifier.Replace("${arch}", rules.ArchBits);
                    var info = lib.Downloads?.Classifiers?.GetValueOrDefault(classifier);
                    var nativeCoord = coord.WithClassifier(classifier);
                    Add(new LibraryFile(
                        dir.LibraryPath(info?.Path ?? nativeCoord.RelativePath),
                        info?.Url ?? BuildUrl(lib, nativeCoord),
                        info?.Sha1, info?.Size, OnClasspath: false, Extract: true, exclude));
                }
                // 只有 natives、没有 artifact 的库（如 lwjgl-platform）不进 classpath。
                if (lib.Downloads?.Artifact is null) continue;
            }

            var artifact = lib.Downloads?.Artifact;
            // 1.19+ 的 natives 是普通库，classifier 以 natives- 开头；放进 classpath 同时也解压一份。
            var isNewStyleNative = coord.Classifier?.StartsWith("natives-", StringComparison.Ordinal) == true;
            Add(new LibraryFile(
                dir.LibraryPath(artifact?.Path ?? coord.RelativePath),
                artifact?.Url ?? BuildUrl(lib, coord),
                artifact?.Sha1 ?? lib.Sha1,
                artifact?.Size ?? lib.Size,
                OnClasspath: true, Extract: isNewStyleNative, exclude));
        }
        return result;

        void Add(LibraryFile file)
        {
            if (seen.Add(file.LocalPath)) result.Add(file);
        }
    }

    private static string? BuildUrl(Library lib, MavenCoordinate coord)
    {
        // downloads.artifact.url 为空字符串表示“本地文件，不需要下载”（如 Forge 生成的库）。
        if (lib.Downloads?.Artifact is { Url: "" }) return null;
        var baseUrl = string.IsNullOrEmpty(lib.Url) ? DefaultMaven : lib.Url;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return baseUrl + coord.RelativePath;
    }
}
