using System.Net;
using System.Security.Cryptography;

namespace Amber.Core.Net;

/// <summary>一个下载项：官方地址 + 本地路径 + 可选校验信息。</summary>
public sealed record DownloadItem(string Url, string LocalPath, string? Sha1 = null, long? Size = null);

public readonly record struct DownloadProgress(int CompletedFiles, int TotalFiles, long BytesDownloaded, string? CurrentFile);

public sealed class DownloadException(string message, IReadOnlyList<(DownloadItem Item, Exception Error)> failures)
    : Exception(message)
{
    public IReadOnlyList<(DownloadItem Item, Exception Error)> Failures { get; } = failures;
}

/// <summary>并发下载器：已存在且校验通过的文件跳过；按候选源依次重试；SHA1 不符视为失败并换源。</summary>
public sealed class Downloader
{
    private readonly HttpClient _http;
    private readonly SourceChain _sources;

    public int MaxConcurrency { get; init; } = 32;
    public int RetriesPerSource { get; init; } = 2;

    public Downloader(HttpClient http, SourceChain sources)
    {
        _http = http;
        _sources = sources;
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Amber/{typeof(Downloader).Assembly.GetName().Version?.ToString(3) ?? "0.1"}");
        return http;
    }

    /// <summary>按候选源获取一段文本（版本清单、版本 JSON 等小文件）。</summary>
    public async Task<string> GetStringAsync(string officialUrl, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in _sources.Candidates(officialUrl))
        {
            try
            {
                return await _http.GetStringAsync(url, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw new HttpRequestException($"所有下载源都失败了：{officialUrl}", last);
    }

    public async Task DownloadAllAsync(IReadOnlyCollection<DownloadItem> items, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var total = items.Count;
        var completed = 0;
        long bytes = 0;
        var failures = new List<(DownloadItem, Exception)>();

        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (item, token) =>
        {
            try
            {
                var downloaded = await DownloadOneAsync(item, token);
                Interlocked.Add(ref bytes, downloaded);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                lock (failures) failures.Add((item, e));
            }
            var done = Interlocked.Increment(ref completed);
            progress?.Report(new DownloadProgress(done, total, Interlocked.Read(ref bytes), Path.GetFileName(item.LocalPath)));
        });

        if (failures.Count > 0)
            throw new DownloadException($"{failures.Count} 个文件下载失败，例如 {failures[0].Item1.Url}：{failures[0].Item2.Message}", failures);
    }

    /// <summary>返回实际下载的字节数（跳过则为 0）。</summary>
    public async Task<long> DownloadOneAsync(DownloadItem item, CancellationToken ct = default)
    {
        if (await IsValidAsync(item, ct)) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);

        Exception? last = null;
        foreach (var url in _sources.Candidates(item.Url))
        {
            for (var attempt = 0; attempt < RetriesPerSource; attempt++)
            {
                var temp = item.LocalPath + ".part";
                try
                {
                    long written;
                    using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var input = await response.Content.ReadAsStreamAsync(ct);
                        await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        await input.CopyToAsync(output, ct);
                        written = output.Length;
                    }

                    if (item.Sha1 is not null)
                    {
                        var actual = await Sha1Async(temp, ct);
                        if (!actual.Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"SHA1 不匹配（期望 {item.Sha1}，实际 {actual}）");
                    }
                    File.Move(temp, item.LocalPath, true);
                    return written;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    last = e;
                    TryDelete(temp);
                    // 404 换源重试没意义，直接换下一个源。
                    if (e is HttpRequestException { StatusCode: HttpStatusCode.NotFound }) break;
                }
            }
        }
        throw last ?? new IOException($"下载失败：{item.Url}");
    }

    private static async Task<bool> IsValidAsync(DownloadItem item, CancellationToken ct)
    {
        var info = new FileInfo(item.LocalPath);
        if (!info.Exists) return false;
        if (item.Size is { } size && info.Length != size) return false;
        if (item.Sha1 is null) return info.Length > 0;
        return (await Sha1Async(item.LocalPath, ct)).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> Sha1Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 临时文件删不掉不影响结果 */ }
    }
}
