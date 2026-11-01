namespace Amber.Core.Content;

public enum ContentType { Mod, ResourcePack, Shader, Modpack }

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
    string? PageUrl);

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

public sealed class MissingApiKeyException() : InvalidOperationException("CurseForge 需要你自己的 API Key，请到「设置」页填写（console.curseforge.com 可以申请）");
