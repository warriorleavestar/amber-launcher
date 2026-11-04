namespace Amber.Core.Content;

/// <summary>
/// 给 Forge / NeoForge 实例装 Sinytra Connector（和它要的 Forgified Fabric API），让 Fabric 模组能跑。
/// 版本按实例的游戏版本和加载器从 Modrinth 上选，依赖按 Connector 自己声明的来。
/// </summary>
public static class ConnectorInstaller
{
    public const string ProjectPage = "https://modrinth.com/mod/connector";

    /// <summary>实例的 mods 里是不是已经有 Connector（按模组 id）。</summary>
    public static bool IsInstalled(IEnumerable<ModRequirements?> mods) =>
        mods.Any(m => m?.Ids.Any(id => id.Equals("connector", StringComparison.OrdinalIgnoreCase)) == true);

    /// <summary>找适合这个实例的 Connector 版本（测试版也要，Connector 目前都是测试版），解析出要装的全部文件。没有适合的版本返回 null。</summary>
    public static async Task<InstallPlan?> PlanAsync(GameInstance instance, IContentProvider modrinth, ContentInstaller installer, ISet<string>? installedProjects, CancellationToken ct = default)
    {
        if (instance.Loader is not ("forge" or "neoforge")) return null;
        var files = await modrinth.GetFilesAsync(ModCompat.ConnectorProject, instance.McVersion, instance.Loader, ct);
        if (files.PickPreferred() is not { } latest) return null;
        return await installer.ResolveAsync(latest, instance, installedProjects, ct);
    }

    /// <summary>Modrinth 上 Fabric API 的项目 id。Forge / NeoForge 上由 Forgified Fabric API 顶替，不装它。</summary>
    public const string FabricApiProjectId = "P7dR8mSH";

    /// <summary>经 Connector 装一个 Fabric 模组：Connector 和它的依赖，加上这个模组按 Fabric 解析出的依赖（Fabric API 除外）。</summary>
    public static async Task<InstallPlan> PlanFabricModAsync(ContentFile fabricFile, GameInstance instance, IContentProvider modrinth, ContentInstaller installer,
        ISet<string>? installedProjects, bool connectorInstalled, CancellationToken ct = default)
    {
        var files = new List<ContentFile>();
        var problems = new List<string>();
        if (!connectorInstalled)
        {
            var connector = await PlanAsync(instance, modrinth, installer, installedProjects, ct)
                            ?? throw new InvalidOperationException($"Modrinth 上没有适合 {instance.McVersion} {instance.Loader} 的 Sinytra Connector，这个 Fabric 模组用不了");
            files.AddRange(connector.Files);
            problems.AddRange(connector.Problems);
        }
        var skip = new HashSet<string>(installedProjects ?? new HashSet<string>(), StringComparer.Ordinal) { FabricApiProjectId };
        foreach (var f in files) skip.Add(f.ProjectId);
        // 前置（比如 Cloth Config）有原生的 Forge / NeoForge 版就用原生版，没有再用 Fabric 版；各自的下一层依赖也按选中的那个版本展开。
        var mod = await installer.ResolveAsync(fabricFile, instance, skip, ct, [instance.Loader, "fabric"]);
        files.AddRange(mod.Files.Where(f => files.All(x => x.ProjectId != f.ProjectId)));
        problems.AddRange(mod.Problems);
        problems.Add("Fabric 模组经 Sinytra Connector 运行，不是每个都能用；进不去游戏时先禁用它");
        return new InstallPlan(files, problems);
    }

    /// <summary>
    /// 给装错的模组找这个实例能用的版本：按 SHA1 在 Modrinth 上认出是哪个项目，再取适合实例游戏版本和加载器的最新版。
    /// 认不出来或者没有合适的版本返回 null。
    /// </summary>
    public static async Task<ContentFile?> FindMatchingVersionAsync(string modPath, GameInstance instance, ModrinthProvider modrinth, CancellationToken ct = default)
    {
        var sha1 = await Amber.Core.Net.Downloader.Sha1Async(modPath, ct);
        var identified = await modrinth.IdentifyBySha1Async([sha1], ct);
        if (!identified.TryGetValue(sha1, out var current)) return null;
        var files = await modrinth.GetFilesAsync(current.ProjectId, instance.McVersion, instance.Loader, ct);
        return files.PickPreferred();
    }

}
