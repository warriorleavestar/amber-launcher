using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Content;

/// <summary>一次安装计划：主文件、必需依赖和勾选的可选依赖，还有解析不了的项目说明。</summary>
public sealed record InstallPlan(IReadOnlyList<ContentFile> Files, IReadOnlyList<string> Problems);

/// <summary>解析依赖并把内容下载到实例对应的文件夹。</summary>
public sealed class ContentInstaller(Downloader downloader, IReadOnlyDictionary<ContentSource, IContentProvider> providers)
{
    /// <summary>
    /// 沿必需依赖展开。依赖没有指定版本时，选匹配实例游戏版本和加载器的最新版（正式版优先）。
    /// installedProjects 里的项目（实例已有的模组）不重复安装。
    /// </summary>
    /// <param name="loaderPreference">依赖按这个顺序找版本（比如经 Connector 装 Fabric 模组时先找原生 NeoForge 版，再找 Fabric 版）；为空时只用实例的加载器。</param>
    /// <param name="optionalProjects">玩家勾选的可选依赖（项目 id）：当作必需依赖一起装，其余可选依赖不装。</param>
    /// <param name="optionalFiles">只声明文件 id 的可选依赖：按玩家勾选的确切版本安装。</param>
    public async Task<InstallPlan> ResolveAsync(ContentFile root, GameInstance instance, ISet<string>? installedProjects = null, CancellationToken ct = default,
        IReadOnlyList<string?>? loaderPreference = null, ISet<string>? optionalProjects = null, ISet<string>? optionalFiles = null)
    {
        ct.ThrowIfCancellationRequested();
        var plan = new List<ContentFile>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.ProjectId };
        var queue = new Queue<ContentFile>();
        queue.Enqueue(root);
        plan.Add(root);

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            foreach (var dep in file.Dependencies)
            {
                ct.ThrowIfCancellationRequested();
                if (dep.Kind == DependencyKind.Incompatible && dep.ProjectId is { } bad && installedProjects?.Contains(bad) == true)
                    problems.Add(L.F("core.content.install.incompatible", file.DisplayName, bad));
                var wanted = dep.Kind == DependencyKind.Required
                    || dep.Kind == DependencyKind.Optional && (
                        dep.ProjectId is { } optional && optionalProjects?.Contains(optional) == true
                        || dep.ProjectId is null && dep.FileId is { } optionalFile && optionalFiles?.Contains(optionalFile) == true);
                if (!wanted) continue;

                var provider = providers[dep.Source];
                ContentFile? resolved;
                try
                {
                    if (dep.FileId is { } fileId)
                    {
                        resolved = await provider.GetFileAsync(dep.ProjectId ?? "", fileId, ct);
                    }
                    else if (dep.ProjectId is { } projectId)
                    {
                        if (installedProjects?.Contains(projectId) == true || seen.Contains(projectId)) continue;
                        resolved = null;
                        foreach (var loader in loaderPreference ?? [instance.Loader])
                        {
                            resolved = (await provider.GetFilesAsync(projectId, instance.McVersion, loader, ct)).PickPreferred();
                            if (resolved is not null) break;
                        }
                    }
                    else continue;
                    ct.ThrowIfCancellationRequested();
                }
                catch (HttpRequestException e)
                {
                    ct.ThrowIfCancellationRequested();
                    problems.Add(L.F("core.content.install.dependencyfailed", file.DisplayName, dep.ProjectId ?? dep.FileId, e.Message));
                    continue;
                }

                if (resolved is null)
                {
                    problems.Add(L.F("core.content.install.dependencyunavailable", file.DisplayName, dep.ProjectId ?? dep.FileId, instance.McVersion, instance.Loader));
                    continue;
                }
                if (installedProjects?.Contains(resolved.ProjectId) == true || !seen.Add(resolved.ProjectId)) continue;
                plan.Add(resolved);
                queue.Enqueue(resolved);
            }
        }
        return new InstallPlan(plan, problems);
    }

    public async Task InstallAsync(InstallPlan plan, string targetDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var items = await PrepareDownloadItemsAsync(plan.Files.Select(f => (f, targetDir)), ct);
        await downloader.DownloadAllAsync(items, progress, ct);
    }

    /// <summary>受限文件只能复用用户手动放好的、SHA-1 和可用大小都匹配的文件；先检查整批，避免部分下载。</summary>
    public static async Task<IReadOnlyList<DownloadItem>> PrepareDownloadItemsAsync(
        IEnumerable<(ContentFile File, string TargetDir)> files, CancellationToken ct = default)
    {
        var items = new List<DownloadItem>();
        foreach (var (file, targetDir) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.RequiresManualDownload)
            {
                items.Add(ToItem(file, targetDir));
                continue;
            }
            var path = Path.Combine(targetDir, SafeFileName(file.FileName));
            if (file.Sha1 is { Length: 40 } sha1 && sha1.All(Uri.IsHexDigit) && File.Exists(path)
                && (file.Size <= 0 || new FileInfo(path).Length == file.Size)
                && string.Equals(await Downloader.Sha1Async(path, ct), sha1, StringComparison.OrdinalIgnoreCase))
                continue;
            throw new ManualDownloadRequiredException(file);
        }
        return items;
    }

    public static DownloadItem ToItem(ContentFile file, string targetDir)
    {
        if (file.RequiresManualDownload) throw new ManualDownloadRequiredException(file);
        return new(file.Url, Path.Combine(targetDir, SafeFileName(file.FileName)), file.Sha1, file.Size > 0 ? file.Size : null);
    }

    /// <summary>远端给的文件名不能带目录，防止写到目标文件夹之外。</summary>
    public static string SafeFileName(string name)
    {
        var clean = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or "..") throw new InvalidDataException(L.F("core.content.install.invalidfilename", name));
        return clean;
    }
}
