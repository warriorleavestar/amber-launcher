using Amber.Core.Localization;
namespace Amber.Core.Content;

public enum ContentType { Mod, ResourcePack, Shader, Modpack, Datapack }

public enum ContentSource { Modrinth, CurseForge }

public enum DependencyKind { Required, Optional, Incompatible, Embedded }

public sealed record ContentProject(
    ContentSource Source,
    string Id,
    string Title,
    string Summary,
    string? Author,
    string? IconUrl,
    long Downloads,
    string? PageUrl)
{
    /// <summary>CurseForge 作者的第三方分发设置；未设置时保持 API 的默认行为。</summary>
    public bool? AllowDistribution { get; init; }
}

public sealed record ContentDependency(ContentSource Source, string? ProjectId, string? FileId, DependencyKind Kind);

/// <summary>项目的一个可下载版本。</summary>
public sealed record ContentFile(
    ContentSource Source,
    string ProjectId,
    string FileId,
    string DisplayName,
    string FileName,
    string Url,
    string? Sha1,
    long Size,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string Channel,
    DateTimeOffset Date,
    IReadOnlyList<ContentDependency> Dependencies)
{
    public bool IsRelease => Channel == "release";

    /// <summary>API 没有提供下载地址时，只能在作者的项目页面手动下载。</summary>
    public bool RequiresManualDownload => Source == ContentSource.CurseForge && string.IsNullOrWhiteSpace(Url);
    public string? ManualDownloadPage { get; init; }
}

public static class ContentFileExtensions
{
    /// <summary>挑一个版本：有正式版用最新的正式版，没有就用最新的。列表已按时间从新到旧排好。</summary>
    public static ContentFile? PickPreferred(this IEnumerable<ContentFile> files) =>
        files.FirstOrDefault(f => f.IsRelease) ?? files.FirstOrDefault();
}

public sealed record SearchResult(IReadOnlyList<ContentProject> Items, int Total);

public sealed record SearchQuery(ContentType Type, string Text, string? GameVersion, string? Loader, int Offset = 0, int Limit = 20);

public interface IContentProvider
{
    ContentSource Source { get; }
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
    Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default);

    /// <summary>项目的版本，最新的在前。gameVersion / loader 为空时不过滤。</summary>
    Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default);
    Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default);
}

/// <summary>按 SHA1 或指纹识别本地文件得到的结果。</summary>
public sealed record IdentifiedFile(string LocalPath, ContentFile File);

/// <summary>可更新的模组：Current 是本地文件，Latest 是远端的新版本。</summary>
public sealed record ModUpdate(string LocalPath, ContentFile Latest);

public sealed class MissingApiKeyException() : InvalidOperationException(L.T("core.content.curseforge.missingapikey"));

public sealed class ManualDownloadRequiredException(ContentFile file)
    : InvalidOperationException(L.F("core.content.curseforge.manual_download", file.DisplayName, file.ManualDownloadPage ?? ""))
{
    public ContentFile File { get; } = file;
}
