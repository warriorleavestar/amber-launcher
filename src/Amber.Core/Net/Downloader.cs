using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Amber.Core.Net;

/// <summary>一个下载项：官方地址 + 本地路径 + 可选校验信息。</summary>
public sealed record DownloadItem(string Url, string LocalPath, string? Sha1 = null, long? Size = null);

/// <summary>BytesDownloaded 是实时累计的字节数，CurrentFile 可能为空。</summary>
public readonly record struct DownloadProgress(int CompletedFiles, int TotalFiles, long BytesDownloaded, string? CurrentFile);

public sealed class DownloadException(string message, IReadOnlyList<(DownloadItem Item, Exception Error)> failures)
    : Exception(message)
{
    public IReadOnlyList<(DownloadItem Item, Exception Error)> Failures { get; } = failures;
}

/// <summary>
/// 并发下载器。
/// - 已存在且校验过的文件直接跳过（见 <see cref="HashCache"/>）。
/// - 大文件按范围分块并发；中断后保留进度，下次接着下。
/// - 按候选源依次尝试；SHA1 不符、卡住不动都算失败并换源；连续失败的源本次会话排到后面。
/// - 遵守镜像的连接数上限；被限流（429 或带 Retry-After 的 503）时先换源，只剩这一个源就等它恢复。
/// </summary>
public sealed class Downloader
{
    // 缓冲区从池里取：几千个小文件每个都新建 128 KB 的数组会落到大对象堆，频繁触发完整 GC。
    private const int CopyBufferSize = 128 * 1024;
    private static readonly TimeSpan DefaultThrottleWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxThrottleWait = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly SourceChain _sources;
    private readonly Dictionary<string, int> _hostFailures = [];
    private readonly Dictionary<string, DateTime> _hostCooldowns = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new();

    public int MaxConcurrency { get; init; } = 32;
    public int RetriesPerSource { get; init; } = 2;

    /// <summary>超过这个大小且服务器支持范围请求时分块下载。</summary>
    public long ChunkThreshold { get; init; } = 16 * 1024 * 1024;
    public int MaxChunksPerFile { get; init; } = 8;
    public long MinChunkSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>这么久没收到数据就算卡住。</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>小请求（清单、JSON）单个源的超时。</summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>同一个主机连续失败这么多次后，本次会话把它排到最后。</summary>
    public int HostFailureLimit { get; init; } = 6;

    public HashCache? Hashes { get; init; }

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
        // 超时由调用方按请求控制，大文件不能有总时长限制。
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Amber/{typeof(Downloader).Assembly.GetName().Version?.ToString(3) ?? "0.1"}");
        return http;
    }

    private IReadOnlyList<string> OrderedCandidates(string officialUrl)
    {
        var list = _sources.Candidates(officialUrl);
        lock (_hostFailures)
        {
            if (_hostFailures.Count == 0 && _hostCooldowns.Count == 0) return list;
            // 稳定排序：失败过多或正在限流的主机整体挪到后面，其余保持优先级。
            var now = DateTime.UtcNow;
            return list.OrderBy(u =>
            {
                var host = HostOf(u);
                var cooling = _hostCooldowns.TryGetValue(host, out var until) && until > now;
                return cooling || _hostFailures.GetValueOrDefault(host) >= HostFailureLimit ? 1 : 0;
            }).ToList();
        }
    }

    /// <summary>占用一个到该主机的连接名额，主机限流中就先等它恢复。</summary>
    private async Task<HostSlot> EnterHostAsync(string url, CancellationToken ct)
    {
        var host = HostOf(url);
        while (true)
        {
            TimeSpan wait;
            lock (_hostFailures)
            {
                if (!_hostCooldowns.TryGetValue(host, out var until)) break;
                wait = until - DateTime.UtcNow;
                if (wait <= TimeSpan.Zero)
                {
                    _hostCooldowns.Remove(host);
                    break;
                }
            }
            await Task.Delay(wait, ct);
        }
        var gate = _hostGates.GetOrAdd(host, h => new SemaphoreSlim(_sources.HostLimit(h) ?? int.MaxValue));
        await gate.WaitAsync(ct);
        return new HostSlot(gate);
    }

    private sealed class HostSlot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    /// <summary>响应表示被限流时记下恢复时间并抛出；其他响应不处理。</summary>
    private void ThrowIfThrottled(string url, HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (response.StatusCode != HttpStatusCode.TooManyRequests && !(response.StatusCode == HttpStatusCode.ServiceUnavailable && retryAfter is not null)) return;

        var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : DefaultThrottleWait);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > MaxThrottleWait) wait = MaxThrottleWait;
        lock (_hostFailures)
        {
            var until = DateTime.UtcNow + wait;
            var host = HostOf(url);
            if (!_hostCooldowns.TryGetValue(host, out var current) || current < until) _hostCooldowns[host] = until;
        }
        throw new HostThrottledException($"{HostOf(url)} 限流了（HTTP {(int)response.StatusCode}）");
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    private void NoteFailure(string url)
    {
        lock (_hostFailures) _hostFailures[HostOf(url)] = _hostFailures.GetValueOrDefault(HostOf(url)) + 1;
    }

    private void NoteSuccess(string url)
    {
        lock (_hostFailures) _hostFailures.Remove(HostOf(url));
    }

    /// <summary>按候选源获取一段文本（版本清单、版本 JSON 等小文件）。</summary>
    public async Task<string> GetStringAsync(string officialUrl, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in OrderedCandidates(officialUrl))
        {
            using var slot = await EnterHostAsync(url, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MetadataTimeout);
            try
            {
                using var response = await _http.GetAsync(url, timeout.Token);
                ThrowIfThrottled(url, response);
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                NoteSuccess(url);
                return text;
            }
            catch (HostThrottledException e)
            {
                last = e;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = e;
                NoteFailure(url);
            }
        }
        throw new HttpRequestException($"所有下载源都失败了：{officialUrl}", last);
    }

    public async Task DownloadAllAsync(IReadOnlyCollection<DownloadItem> items, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var total = items.Count;
        var completed = 0;
        long bytes = 0;
        var lastReport = Stopwatch.StartNew();
        var failures = new List<(DownloadItem, Exception)>();

        void Report(string? file, bool force)
        {
            if (progress is null) return;
            lock (lastReport)
            {
                if (!force && lastReport.ElapsedMilliseconds < 80) return;
                lastReport.Restart();
            }
            progress.Report(new DownloadProgress(Volatile.Read(ref completed), total, Interlocked.Read(ref bytes), file));
        }

        try
        {
            // 先校验，再下载：校验只吃 CPU 和磁盘，不该占网络并发名额。
            var pending = new List<DownloadItem>();
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = ct }, async (item, token) =>
            {
                if (await IsValidAsync(item, token))
                {
                    Interlocked.Increment(ref completed);
                    Report(null, false);
                }
                else
                {
                    lock (pending) pending.Add(item);
                }
            });
            Hashes?.Save();

            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (item, token) =>
            {
                try
                {
                    await DownloadCoreAsync(item, delta =>
                    {
                        Interlocked.Add(ref bytes, delta);
                        Report(null, false);
                    }, token);
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    lock (failures) failures.Add((item, e));
                }
                Interlocked.Increment(ref completed);
                Report(Path.GetFileName(item.LocalPath), false);
            });
        }
        finally
        {
            // 中途取消也要保存，不然已经校验过的文件下次要重新算哈希。
            Hashes?.Save();
        }
        Report(null, true);

        if (failures.Count > 0)
            throw new DownloadException($"{failures.Count} 个文件下载失败，例如 {failures[0].Item1.Url}：{failures[0].Item2.Message}", failures);
    }

    /// <summary>返回实际下载的字节数（跳过则为 0）。</summary>
    public Task<long> DownloadOneAsync(DownloadItem item, CancellationToken ct = default) => DownloadOneAsync(item, null, ct);

    /// <summary>bytes 收到的是累计已下载的字节数。</summary>
    public async Task<long> DownloadOneAsync(DownloadItem item, IProgress<long>? bytes, CancellationToken ct = default)
    {
        if (await IsValidAsync(item, ct)) return 0;
        long total = 0;
        var result = await DownloadCoreAsync(item, delta =>
        {
            var now = Interlocked.Add(ref total, delta);
            bytes?.Report(now);
        }, ct);
        Hashes?.Save();
        return result;
    }

    private async Task<long> DownloadCoreAsync(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
        var temp = item.LocalPath + ".part";
        var state = temp + ".state";

        Exception? last = null;
        var candidates = OrderedCandidates(item.Url);
        foreach (var url in candidates)
        {
            for (var attempt = 0; attempt < RetriesPerSource; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt + Random.Shared.Next(200)), ct);
                try
                {
                    var (written, streamedSha1) = await FetchAsync(url, item, temp, state, onBytes, ct);
                    await VerifyAsync(item, temp, streamedSha1, ct);
                    File.Move(temp, item.LocalPath, true);
                    TryDelete(state);
                    if (item.Sha1 is not null) Hashes?.Remember(item.LocalPath, item.Sha1);
                    NoteSuccess(url);
                    return written;
                }
                catch (HostThrottledException e)
                {
                    last = e;
                    // 还有别的源就不等了；已有进度留着，换源后接着下。
                    if (url != candidates[^1]) break;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    last = e;
                    NoteFailure(url);
                    // 校验失败说明这份数据是坏的；换的源不支持分块时旧断点也接不上。
                    if (e is InvalidDataException or RangeNotSupportedException)
                    {
                        TryDelete(temp);
                        TryDelete(state);
                    }
                    // 404 在这个源上重试没意义，直接换下一个源。
                    if (e is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden }) break;
                }
            }
        }
        throw last ?? new IOException($"下载失败：{item.Url}");
    }

    /// <summary>streamedSha1 是下载时顺带算好的；有它就不用把刚写完的文件再读一遍。</summary>
    private static async Task VerifyAsync(DownloadItem item, string temp, string? streamedSha1, CancellationToken ct)
    {
        var info = new FileInfo(temp);
        if (!info.Exists) throw new InvalidDataException("下载结果不存在");
        if (item.Size is { } size && info.Length != size)
            throw new InvalidDataException($"文件大小不对（期望 {size}，实际 {info.Length}）");
        if (item.Sha1 is not null)
        {
            var actual = streamedSha1 ?? await Sha1Async(temp, ct);
            if (!actual.Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA1 不匹配（期望 {item.Sha1}，实际 {actual}）");
        }
    }

    /// <summary>把 url 的内容写到 temp。能分块就分块，已有进度就接着下。返回本次写入的字节数，整个文件一次下完时还带上它的 SHA1。</summary>
    private async Task<(long Written, string? Sha1)> FetchAsync(string url, DownloadItem item, string temp, string state, Action<long> onBytes, CancellationToken ct)
    {
        var existingChunks = ChunkState.Load(state);
        if (existingChunks is not null && File.Exists(temp))
            return (await FetchChunkedAsync(url, temp, state, existingChunks, onBytes, ct), null);
        TryDelete(state);

        var resumeFrom = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (item.Size is { } knownSize && resumeFrom > knownSize) resumeFrom = 0;
        if (resumeFrom > 0 && item.Size == resumeFrom) return (0, null);

        // 名额要在开始计时之前拿到，排队不算卡住。
        using var slot = await EnterHostAsync(url, ct);

        // 已知的小文件不用探测分块，普通请求最省事。其余的带 Range，从响应判断服务器支不支持。
        var useRange = resumeFrom > 0 || item.Size is null || item.Size >= ChunkThreshold;
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(StallTimeout);
        var response = await SendGetAsync(url, useRange ? resumeFrom : null, headerTimeout.Token);
        try
        {
            ThrowIfThrottled(url, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        // 有的 CDN 收到 Range 直接报错（forgecdn 返回 404），不能因此判定文件不存在，去掉 Range 再试一次。
        // 只看 4xx：服务器临时出错时已有的进度要留着。
        if (useRange && IsClientError(response.StatusCode))
        {
            response.Dispose();
            resumeFrom = 0;
            TryDelete(temp);
            response = await SendGetAsync(url, null, headerTimeout.Token);
        }
        using var _ = response;
        response.EnsureSuccessStatusCode();

        var partial = response.StatusCode == HttpStatusCode.PartialContent;
        var totalSize = partial ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
        if (partial && resumeFrom == 0 && totalSize >= ChunkThreshold && totalSize is { } chunkTotal)
        {
            response.Dispose();
            slot.Dispose();
            var chunks = ChunkState.Create(chunkTotal, MaxChunksPerFile, MinChunkSize);
            await using (var handle = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                handle.SetLength(chunkTotal);
            chunks.Save(state);
            return (await FetchChunkedAsync(url, temp, state, chunks, onBytes, ct), null);
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var append = partial && resumeFrom > 0;
        await using var output = new FileStream(temp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 0, true);
        // 续传时前半段不经过这里，算不出整个文件的哈希，留给校验时读文件。
        using var hash = item.Sha1 is not null && !append ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        var written = await CopyWithStallCheckAsync(input, (buffer, token) =>
        {
            hash?.AppendData(buffer.Span);
            return output.WriteAsync(buffer, token);
        }, onBytes, ct);
        if (response.Content.Headers.ContentLength is { } expected && written != expected)
            throw new IOException($"连接提前结束（收到 {written}，应有 {expected}）");
        return (written, hash is null ? null : Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool IsClientError(HttpStatusCode status) => (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout;

    private async Task<HttpResponseMessage> SendGetAsync(string url, long? rangeFrom, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (rangeFrom is { } from) request.Headers.Range = new RangeHeaderValue(from, null);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<long> FetchChunkedAsync(string url, string temp, string statePath, ChunkState state, Action<long> onBytes, CancellationToken ct)
    {
        using var handle = File.OpenHandle(temp, FileMode.Open, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        long written = 0;
        var pending = state.Chunks.Where(c => !c.Done).ToList();
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = MaxChunksPerFile, CancellationToken = ct }, async (chunk, token) =>
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                long received = 0;
                try
                {
                    using var slot = await EnterHostAsync(url, token);
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(chunk.Start, chunk.End);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(StallTimeout);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    ThrowIfThrottled(url, response);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        // 200 或 4xx 才说明不支持分块；5xx 只是临时出错，不能扔掉进度。
                        if (response.IsSuccessStatusCode || IsClientError(response.StatusCode)) throw new RangeNotSupportedException();
                        response.EnsureSuccessStatusCode();
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    var position = chunk.Start;
                    await CopyWithStallCheckAsync(input, async (buffer, t) =>
                    {
                        await RandomAccess.WriteAsync(handle, buffer, position, t);
                        position += buffer.Length;
                    }, delta =>
                    {
                        received += delta;
                        onBytes(delta);
                    }, token);
                    if (position != chunk.End + 1) throw new IOException("分块提前结束");

                    Interlocked.Add(ref written, received);
                    lock (state)
                    {
                        chunk.Done = true;
                        state.Save(statePath);
                    }
                    return;
                }
                catch (RangeNotSupportedException)
                {
                    throw;
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    last = e;
                    onBytes(-received);
                    if (attempt < 2) await Task.Delay(300 * (attempt + 1), token);
                }
                catch
                {
                    // 被取消的分块没写完，下次要重下，进度里不能算进去。
                    onBytes(-received);
                    throw;
                }
            }
            throw last!;
        });
        return written;
    }

    /// <summary>复制数据；超过 StallTimeout 没收到新数据就中断，换源而不是一直等。</summary>
    private async Task<long> CopyWithStallCheckAsync(Stream input, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, Action<long> onBytes, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"下载卡住了：{StallTimeout.TotalSeconds:F0} 秒没有收到数据");
                }
                if (read == 0) return total;
                await write(buffer.AsMemory(0, read), ct);
                total += read;
                onBytes(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<bool> IsValidAsync(DownloadItem item, CancellationToken ct)
    {
        var info = new FileInfo(item.LocalPath);
        if (!info.Exists) return false;
        if (item.Size is { } size && info.Length != size) return false;
        if (item.Sha1 is null) return info.Length > 0;
        if (Hashes?.IsKnownGood(info, item.Sha1) == true) return true;
        if (!(await Sha1Async(item.LocalPath, ct)).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
        Hashes?.Remember(item.LocalPath, item.Sha1);
        return true;
    }

    public static async Task<string> Sha1Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 临时文件删不掉不影响结果 */ }
    }

    private sealed class RangeNotSupportedException() : IOException("这个下载源不支持分块");

    private sealed class HostThrottledException(string message) : IOException(message);

    /// <summary>分块进度，存在 .part.state 里，断点续传时读回来。</summary>
    internal sealed class ChunkState
    {
        public long Total { get; set; }
        public List<Chunk> Chunks { get; set; } = [];

        public sealed class Chunk
        {
            public long Start { get; set; }
            public long End { get; set; }
            public bool Done { get; set; }
        }

        public static ChunkState Create(long total, int maxChunks, long minChunkSize)
        {
            var count = (int)Math.Clamp(total / minChunkSize, 1, maxChunks);
            var size = total / count;
            var state = new ChunkState { Total = total };
            for (var i = 0; i < count; i++)
            {
                var start = i * size;
                state.Chunks.Add(new Chunk { Start = start, End = i == count - 1 ? total - 1 : start + size - 1 });
            }
            return state;
        }

        public static ChunkState? Load(string path)
        {
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<ChunkState>(File.ReadAllText(path)); }
            catch (JsonException) { return null; }
        }

        public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}

/// <summary>
/// 记住校验通过的文件（路径 + 大小 + 修改时间 + SHA1）。文件没变就不用重新算哈希，
/// 启动时几千个资源文件的检查从几十秒降到不到一秒。文件被改动后修改时间变了，会重新校验。
/// </summary>
public sealed class HashCache(string filePath)
{
    private readonly Dictionary<string, (long Length, long Ticks, string Sha1)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _loaded;
    private bool _dirty;

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(filePath)) return;
            try
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 4 && long.TryParse(parts[1], out var length) && long.TryParse(parts[2], out var ticks))
                        _entries[parts[0]] = (length, ticks, parts[3]);
                }
            }
            catch (IOException) { /* 缓存读不了就重新校验 */ }
        }
    }

    public bool IsKnownGood(FileInfo info, string sha1)
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _entries.TryGetValue(info.FullName, out var e)
                   && e.Length == info.Length
                   && e.Ticks == info.LastWriteTimeUtc.Ticks
                   && e.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Remember(string path, string sha1)
    {
        EnsureLoaded();
        var info = new FileInfo(path);
        if (!info.Exists) return;
        lock (_gate)
        {
            _entries[info.FullName] = (info.Length, info.LastWriteTimeUtc.Ticks, sha1);
            _dirty = true;
        }
    }

    public void Save()
    {
        if (!_loaded) return;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var temp = filePath + ".tmp";
                File.WriteAllLines(temp, _entries.Select(e => $"{e.Key}\t{e.Value.Length}\t{e.Value.Ticks}\t{e.Value.Sha1}"));
                File.Move(temp, filePath, true);
            }
            catch (IOException) { /* 缓存写不了下次重新校验 */ }
        }
    }
}
