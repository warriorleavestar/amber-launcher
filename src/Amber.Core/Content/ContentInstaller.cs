using Amber.Core.Net;

namespace Amber.Core.Content;

/// <summary>一次安装计划：主文件加上解析出来的必需依赖，还有解析不了的项目说明。</summary>
public sealed record InstallPlan(IReadOnlyList<ContentFile> Files, IReadOnlyList<string> Problems);

/// <summary>解析依赖并把内容下载到实例对应的文件夹。</summary>
public sealed class ContentInstaller(Downloader downloader, IReadOnlyDictionary<ContentSource, IContentProvider> providers)
{
    /// <summary>
    /// 沿必需依赖展开。依赖没有指定版本时，选匹配实例游戏版本和加载器的最新版（正式版优先）。
    /// installedProjects 里的项目（实例已有的模组）不重复安装。
    /// </summary>
    public async Task<InstallPlan> ResolveAsync(ContentFile root, GameInstance instance, ISet<string>? installedProjects = null, CancellationToken ct = default)
    {
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
                    problems.Add($"{file.DisplayName} 和实例里已有的模组 {bad} 不兼容");
                if (dep.Kind != DependencyKind.Required) continue;

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
                        var candidates = await provider.GetFilesAsync(projectId, instance.McVersion, instance.Loader, ct);
                        resolved = candidates.FirstOrDefault(c => c.IsRelease) ?? candidates.FirstOrDefault();
                    }
                    else continue;
                }
                catch (HttpRequestException e)
                {
                    problems.Add($"{file.DisplayName} 的依赖 {dep.ProjectId ?? dep.FileId} 获取失败：{e.Message}");
                    continue;
                }

                if (resolved is null)
                {
                    problems.Add($"{file.DisplayName} 需要的依赖 {dep.ProjectId} 没有适用于 {instance.McVersion} / {instance.Loader} 的版本");
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
        var items = plan.Files.Select(f => ToItem(f, targetDir)).ToList();
        await downloader.DownloadAllAsync(items, progress, ct);
    }

    public static DownloadItem ToItem(ContentFile file, string targetDir) =>
        new(file.Url, Path.Combine(targetDir, SafeFileName(file.FileName)), file.Sha1, file.Size > 0 ? file.Size : null);

    /// <summary>远端给的文件名不能带目录，防止写到目标文件夹之外。</summary>
    public static string SafeFileName(string name)
    {
        var clean = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or "..") throw new InvalidDataException($"不合法的文件名：{name}");
        return clean;
    }
}
