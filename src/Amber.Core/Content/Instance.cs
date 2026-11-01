using Amber.Core.Game;

namespace Amber.Core.Content;

/// <summary>一个可启动版本的概况：游戏版本、加载器、工作目录。</summary>
public sealed record GameInstance(string Id, string McVersion, string? Loader, string? LoaderVersion, string WorkDir)
{
    public string ModsDir => Path.Combine(WorkDir, "mods");
    public string ResourcePacksDir => Path.Combine(WorkDir, "resourcepacks");
    public string ShaderPacksDir => Path.Combine(WorkDir, "shaderpacks");
    public string SavesDir => Path.Combine(WorkDir, "saves");
    public string ScreenshotsDir => Path.Combine(WorkDir, "screenshots");

    public string DirectoryFor(ContentType type) => type switch
    {
        ContentType.Mod => ModsDir,
        ContentType.ResourcePack => ResourcePacksDir,
        ContentType.Shader => ShaderPacksDir,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>从版本 JSON 判断游戏版本和加载器。</summary>
    public static GameInstance Detect(GameDirectory dir, string id, string workDir)
    {
        var mc = id;
        var seen = new HashSet<string>();
        for (var current = id; current is not null && seen.Add(current);)
        {
            var version = dir.ReadVersion(current);
            mc = current;
            current = version.InheritsFrom;
        }

        var resolved = dir.ResolveVersion(id);
        string? loader = null, loaderVersion = null;
        foreach (var lib in resolved.Libraries)
        {
            var parts = lib.Name.Split(':');
            if (parts.Length < 3) continue;
            var key = $"{parts[0]}:{parts[1]}";
            switch (key)
            {
                case "net.fabricmc:fabric-loader":
                    (loader, loaderVersion) = ("fabric", parts[2]);
                    break;
                case "org.quiltmc:quilt-loader":
                    (loader, loaderVersion) = ("quilt", parts[2]);
                    break;
                case "net.neoforged:neoforge" or "net.neoforged:forge" or "net.neoforged.fancymodloader:loader" or "net.neoforged.fancymodloader:earlydisplay":
                    loader ??= "neoforge";
                    break;
                case "net.minecraftforge:forge" or "net.minecraftforge:fmlloader":
                    if (loader != "neoforge") (loader, loaderVersion) = ("forge", parts[2]);
                    break;
                case "optifine:OptiFine":
                    (loader, loaderVersion) = (loader ?? "optifine", parts[2]);
                    break;
            }
        }
        // NeoForge 的版本号写在启动参数里。
        if (loader == "neoforge" && resolved.Arguments?.Game is { } game)
        {
            for (var i = 0; i + 1 < game.Count; i++)
            {
                if (game[i].ValueKind == System.Text.Json.JsonValueKind.String && game[i].GetString() is "--fml.neoForgeVersion" or "--fml.forgeVersion")
                    loaderVersion = game[i + 1].GetString();
            }
        }
        return new GameInstance(id, mc, loader, loaderVersion, workDir);
    }
}
