using Amber.Core.Localization;
namespace Amber.Core.Game;

/// <summary>Maven 坐标 group:artifact:version[:classifier][@ext]。</summary>
public readonly record struct MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static MavenCoordinate Parse(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }
        var parts = name.Split(':');
        if (parts.Length < 3) throw new FormatException(L.F("core.game.maven.invalidcoordinate", name));
        return new MavenCoordinate(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : null, ext);
    }

    public MavenCoordinate WithClassifier(string? classifier) => this with { Classifier = classifier };

    /// <summary>相对 libraries 目录的路径（使用 /）。</summary>
    public string RelativePath
    {
        get
        {
            var file = Classifier is null
                ? $"{Artifact}-{Version}.{Extension}"
                : $"{Artifact}-{Version}-{Classifier}.{Extension}";
            return $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{file}";
        }
    }
}
