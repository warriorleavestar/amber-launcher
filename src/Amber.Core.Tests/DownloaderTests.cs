using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>本机真实 HTTP 服务器，按需模拟范围请求、中断、错误和卡住。</summary>
internal sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, byte[]> _files = [];
    public int Port { get; }
    public string Root => $"http://127.0.0.1:{Port}/";

    public bool SupportRanges { get; set; } = true;
    public int RangeRequests;
    public int Requests;
    public HashSet<string> Fail500 { get; } = [];
    public HashSet<string> Hang { get; } = [];

    /// <summary>像 forgecdn 一样：带 Range 就回 404。</summary>
    public HashSet<string> RejectRange { get; } = [];

    /// <summary>这个文件第一次请求只发一半就断开。</summary>
    public HashSet<string> DropOnce { get; } = [];

    /// <summary>每个请求先等这么久，用来观察同时在处理的请求数。</summary>
    public int DelayMs { get; set; }
    public int PeakActive;
    private int _active;

    /// <summary>接下来这么多个请求回 FailStatus（可带 Retry-After），之后恢复正常。</summary>
    public int FailNext;
    public int FailStatus { get; set; } = 429;
    public string? RetryAfter { get; set; }

    /// <summary>FailNext 只对分块请求（Range 带结束位置）生效，探测请求放行。</summary>
    public bool FailChunksOnly { get; set; }

    public byte[] CorruptFor(string path) => _files[path].Select(b => (byte)(b ^ 0xFF)).ToArray();

    public TestServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add(Root);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Add(string path, byte[] data)
    {
        _files[path] = data;
        return Root + path;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath.TrimStart('/');
        Interlocked.Increment(ref Requests);
        // 并发数只在“收到请求 → 开始回数据之前”这段里算：客户端读完最后一个字节就会发下一个请求，
        // 那时这边的 Close() 可能还没返回，按整个处理过程算会多数出一个其实不存在的并发。
        var active = Interlocked.Increment(ref _active);
        var counted = true;
        int peak;
        while (active > (peak = Volatile.Read(ref PeakActive))) Interlocked.CompareExchange(ref PeakActive, active, peak);
        try
        {
            if (DelayMs > 0) await Task.Delay(DelayMs);
            Interlocked.Decrement(ref _active);
            counted = false;
            var isChunk = c.Request.Headers["Range"] is { } r && !r.EndsWith('-');
            if ((!FailChunksOnly || isChunk) && Volatile.Read(ref FailNext) > 0 && Interlocked.Decrement(ref FailNext) >= 0)
            {
                c.Response.StatusCode = FailStatus;
                if (RetryAfter is not null) c.Response.Headers["Retry-After"] = RetryAfter;
                c.Response.Close();
                return;
            }
            if (Fail500.Contains(path))
            {
                c.Response.StatusCode = 500;
                c.Response.Close();
                return;
            }
            if (!_files.TryGetValue(path, out var data))
            {
                c.Response.StatusCode = 404;
                c.Response.Close();
                return;
            }
            if (RejectRange.Contains(path) && c.Request.Headers["Range"] is not null)
            {
                c.Response.StatusCode = 404;
                c.Response.Close();
                return;
            }
            if (Hang.Contains(path))
            {
                c.Response.ContentLength64 = data.Length;
                await c.Response.OutputStream.WriteAsync(data.AsMemory(0, 16));
                await c.Response.OutputStream.FlushAsync();
                await Task.Delay(Timeout.Infinite, _cts.Token);
                return;
            }

            long start = 0, end = data.Length - 1;
            var range = c.Request.Headers["Range"];
            var partial = false;
            if (SupportRanges && range is not null)
            {
                var spec = range["bytes=".Length..].Split('-');
                start = long.Parse(spec[0]);
                if (spec[1].Length > 0) end = long.Parse(spec[1]);
                partial = true;
                Interlocked.Increment(ref RangeRequests);
                c.Response.StatusCode = 206;
                c.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
            }
            var length = end - start + 1;
            c.Response.ContentLength64 = length;
            var send = (int)length;
            lock (DropOnce)
            {
                if (DropOnce.Remove(path))
                    send = (int)(length / 2);
            }
            await c.Response.OutputStream.WriteAsync(data.AsMemory((int)start, send));
            if (send != length)
            {
                c.Response.Abort();
                return;
            }
            c.Response.Close();
            _ = partial;
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            if (counted) Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
    }
}

public class DownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-dl-" + Guid.NewGuid().ToString("N"));
    private readonly TestServer _server = new();
    private readonly HttpClient _http = Downloader.CreateHttpClient();

    public DownloaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    private static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        new Random(size).NextBytes(data);
        return data;
    }

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private Downloader Make(SourceChain? chain = null, HashCache? cache = null, TimeSpan? stall = null) => new(_http, chain ?? new SourceChain([new OfficialSource()]))
    {
        RetriesPerSource = 2,
        ChunkThreshold = 1024 * 1024,
        MinChunkSize = 256 * 1024,
        StallTimeout = stall ?? TimeSpan.FromSeconds(2),
        Hashes = cache,
    };

    private string Local(string name) => Path.Combine(_root, name);

    private SourceChain MirrorOf(TestServer server, int? limit = null) =>
        new([new PrefixMirrorSource("测试", new Dictionary<string, string> { [_server.Root] = server.Root }, limit)]);

    [Fact]
    public async Task MirrorConnectionLimit_IsRespected_EvenForChunks()
    {
        _server.DelayMs = 30;
        var big = RandomBytes(4 * 1024 * 1024);
        var items = new List<DownloadItem> { new(_server.Add("big.bin", big), Local("big.bin"), Sha1(big), big.Length) };
        for (var i = 0; i < 20; i++)
        {
            var data = RandomBytes(1000 + i);
            items.Add(new(_server.Add($"s{i}", data), Local($"s{i}"), Sha1(data), data.Length));
        }
        // 服务器按“收到请求到开始回数据之前”算并发。客户端只有在这段时间里放弃一个请求（卡住超时）再重试，
        // 才会让服务器同时数到被放弃的那个和新的那个——繁忙机器上 30 ms 的延迟可能被拖过 2 秒的卡住超时，
        // 这个测试就偶尔多数出一个。这里不测卡住超时：给一个远大于任何调度延迟的期限，客户端不会放弃请求，
        // 服务器数到的就严格等于客户端占着的名额。
        var downloader = Make(MirrorOf(_server, 3), stall: TimeSpan.FromMinutes(5));

        await downloader.DownloadAllAsync(items);

        Assert.InRange(_server.PeakActive, 1, 3);
        Assert.Equal(big, await File.ReadAllBytesAsync(Local("big.bin")));
    }

    [Fact]
    public async Task Bmclapi_DeclaresLimit_OfficialDoesNot()
    {
        var chain = SourceChain.OfficialFirst;
        Assert.Equal(PrefixMirrorSource.BmclapiMaxConnections, chain.HostLimit("bmclapi2.bangbang93.com"));
        Assert.Null(chain.HostLimit("piston-data.mojang.com"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Throttled_SwitchesToNextSource_WithoutWaiting()
    {
        using var official = new TestServer();
        var data = RandomBytes(5000);
        official.Add("a.bin", data);
        official.Add("b.bin", data);
        _server.Add("a.bin", data);
        _server.Add("b.bin", data);
        _server.FailNext = 1000;
        _server.RetryAfter = "60";
        var chain = new SourceChain([new PrefixMirrorSource("镜像", new Dictionary<string, string> { [official.Root] = _server.Root }), new OfficialSource()]);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Make(chain).DownloadAllAsync([
            new(official.Root + "a.bin", Local("a.bin"), Sha1(data), data.Length),
        ]);
        var mirrorHits = _server.Requests;
        await Make(chain).DownloadOneAsync(new(official.Root + "b.bin", Local("b.bin"), Sha1(data), data.Length));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"不该等限流结束，实际 {watch.Elapsed}");
        Assert.Equal(1, mirrorHits);
        Assert.Equal(data, await File.ReadAllBytesAsync(Local("a.bin")));
    }

    [Fact]
    public async Task EverySourceFailing_ReportsEachSource_NotJustTheThrottledMirror()
    {
        // 官方源连不上（这里是 404），最后一个源（镜像）又限流：报错里两边的原因都要有。
        using var mirror = new TestServer();
        var data = RandomBytes(5000);
        mirror.Add("a.bin", data);
        mirror.FailNext = 1000;
        mirror.RetryAfter = "0";
        var chain = new SourceChain([new OfficialSource(), new PrefixMirrorSource("镜像", new Dictionary<string, string> { [_server.Root] = mirror.Root })]);

        var e = await Assert.ThrowsAsync<IOException>(() => Make(chain).DownloadOneAsync(new(_server.Root + "a.bin", Local("a.bin"), Sha1(data), data.Length)));

        Assert.Contains(new Uri(_server.Root).Authority, e.Message);
        Assert.Contains(new Uri(mirror.Root).Authority, e.Message);
        Assert.Contains("a.bin", e.Message);
    }

    [Fact]
    public async Task Throttled_OnlySource_WaitsForRetryAfter()
    {
        var data = RandomBytes(5000);
        var url = _server.Add("a.bin", data);
        _server.FailNext = 1;
        _server.RetryAfter = "1";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Make().DownloadOneAsync(new(url, Local("a.bin"), Sha1(data), data.Length));

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), $"应该等 Retry-After，实际 {watch.Elapsed}");
        Assert.Equal(data, await File.ReadAllBytesAsync(Local("a.bin")));
    }

    [Fact]
    public async Task ServerError_DuringResume_KeepsProgress()
    {
        var data = RandomBytes(100_000);
        var url = _server.Add("a.bin", data);
        var part = Local("a.bin") + ".part";
        await File.WriteAllBytesAsync(part, data[..60_000]);
        _server.FailNext = 2;
        _server.FailStatus = 500;
        var downloader = new Downloader(_http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 2 };

        await Assert.ThrowsAnyAsync<Exception>(() => downloader.DownloadOneAsync(new(url, Local("a.bin"), Sha1(data), data.Length)));
        Assert.Equal(60_000, new FileInfo(part).Length);

        var written = await downloader.DownloadOneAsync(new(url, Local("a.bin"), Sha1(data), data.Length));

        Assert.Equal(40_000, written);
        Assert.Equal(data, await File.ReadAllBytesAsync(Local("a.bin")));
    }

    [Fact]
    public async Task ServerError_DuringChunks_KeepsState()
    {
        var data = RandomBytes(4 * 1024 * 1024);
        var url = _server.Add("big.bin", data);
        var downloader = Make();
        var item = new DownloadItem(url, Local("big.bin"), Sha1(data), data.Length);
        _server.FailStatus = 503;
        _server.FailChunksOnly = true;
        _server.FailNext = 100000;

        await Assert.ThrowsAnyAsync<Exception>(() => downloader.DownloadOneAsync(item));
        Assert.True(File.Exists(Local("big.bin") + ".part.state"), "5xx 不该扔掉分块进度");

        _server.FailNext = 0;
        await downloader.DownloadOneAsync(item);
        Assert.Equal(data, await File.ReadAllBytesAsync(Local("big.bin")));
    }

    [Fact]
    public async Task Cancel_StillSavesHashCache()
    {
        var good = RandomBytes(3000);
        await File.WriteAllBytesAsync(Local("good.bin"), good);
        var hang = RandomBytes(3001);
        var hangUrl = _server.Add("hang.bin", hang);
        _server.Hang.Add("hang.bin");
        var cachePath = Local("hashes");
        var downloader = Make(cache: new HashCache(cachePath));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var download = downloader.DownloadAllAsync([
            new(_server.Root + "good.bin", Local("good.bin"), Sha1(good), good.Length),
            new(hangUrl, Local("hang.bin"), Sha1(hang), hang.Length),
        ], null, cts.Token);
        // 等待进入下载阶段再取消，避免繁忙机器在校验 good.bin 前就耗尽固定的 500 ms。
        while (Volatile.Read(ref _server.Requests) == 0 && !download.IsCompleted)
            await Task.Delay(10, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.Contains("good.bin", await File.ReadAllTextAsync(cachePath));
    }

    [Fact]
    public async Task StreamedHash_StillRejectsCorruptData()
    {
        var data = RandomBytes(5000);
        var url = _server.Add("a.bin", data);
        var wrong = Sha1(RandomBytes(5001));

        await Assert.ThrowsAnyAsync<Exception>(() => Make().DownloadOneAsync(new(url, Local("a.bin"), wrong, data.Length)));

        Assert.False(File.Exists(Local("a.bin")));
    }

    [Fact]
    public async Task LargeFile_IsChunked_AndIntact()
    {
        var data = RandomBytes(3 * 1024 * 1024);
        var url = _server.Add("big.bin", data);
        var written = await Make().DownloadOneAsync(new DownloadItem(url, Local("big.bin"), Sha1(data), data.Length));
        Assert.Equal(data.Length, written);
        Assert.True(File.ReadAllBytes(Local("big.bin")).SequenceEqual(data));
        Assert.True(_server.RangeRequests >= 8, $"应该分块，实际 {_server.RangeRequests} 次范围请求");
        Assert.False(File.Exists(Local("big.bin.part")));
        Assert.False(File.Exists(Local("big.bin.part.state")));
    }

    [Fact]
    public async Task ServerWithoutRanges_StillWorks()
    {
        _server.SupportRanges = false;
        var data = RandomBytes(3 * 1024 * 1024);
        var url = _server.Add("big.bin", data);
        await Make().DownloadOneAsync(new DownloadItem(url, Local("big.bin"), Sha1(data)));
        Assert.True(File.ReadAllBytes(Local("big.bin")).SequenceEqual(data));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerThatRejectsRange_FallsBackToPlainGet(bool sizeKnown)
    {
        var data = RandomBytes(3 * 1024 * 1024);
        var url = _server.Add("cdn.bin", data);
        _server.RejectRange.Add("cdn.bin");
        await Make().DownloadOneAsync(new DownloadItem(url, Local("cdn.bin"), Sha1(data), sizeKnown ? data.Length : null));
        Assert.True(File.ReadAllBytes(Local("cdn.bin")).SequenceEqual(data));
    }

    [Fact]
    public async Task StaleProgress_OnServerThatRejectsRange_RestartsCleanly()
    {
        var data = RandomBytes(100_000);
        var url = _server.Add("cdn2.bin", data);
        _server.RejectRange.Add("cdn2.bin");
        File.WriteAllBytes(Local("cdn2.bin.part"), data[..50_000]);
        await Make().DownloadOneAsync(new DownloadItem(url, Local("cdn2.bin"), Sha1(data), data.Length));
        Assert.True(File.ReadAllBytes(Local("cdn2.bin")).SequenceEqual(data));
    }

    [Fact]
    public async Task SmallKnownFile_DoesNotUseRange()
    {
        var data = RandomBytes(10_000);
        await Make().DownloadOneAsync(new DownloadItem(_server.Add("t.bin", data), Local("t.bin"), Sha1(data), data.Length));
        Assert.Equal(0, _server.RangeRequests);
    }

    [Fact]
    public async Task SmallFile_ResumesFromPartialFile()
    {
        var data = RandomBytes(200_000);
        var url = _server.Add("s.bin", data);
        File.WriteAllBytes(Local("s.bin.part"), data[..150_000]);
        var written = await Make().DownloadOneAsync(new DownloadItem(url, Local("s.bin"), Sha1(data), data.Length));
        Assert.Equal(50_000, written);
        Assert.True(File.ReadAllBytes(Local("s.bin")).SequenceEqual(data));
    }

    [Fact]
    public async Task ChunkedDownload_ResumesFromState_OnlyFetchesMissing()
    {
        var data = RandomBytes(2 * 1024 * 1024);
        var url = _server.Add("r.bin", data);
        // 模拟上次下了前一半分块后中断。
        var state = Downloader.ChunkState.Create(data.Length, 8, 256 * 1024);
        var half = state.Chunks.Count / 2;
        var part = new byte[data.Length];
        for (var i = 0; i < half; i++)
        {
            var c = state.Chunks[i];
            Array.Copy(data, c.Start, part, c.Start, c.End - c.Start + 1);
            c.Done = true;
        }
        File.WriteAllBytes(Local("r.bin.part"), part);
        state.Save(Local("r.bin.part.state"));

        var written = await Make().DownloadOneAsync(new DownloadItem(url, Local("r.bin"), Sha1(data), data.Length));
        Assert.True(written < data.Length, $"应该只下缺的部分，实际下了 {written}");
        Assert.Equal(state.Chunks.Count - half, _server.RangeRequests);
        Assert.True(File.ReadAllBytes(Local("r.bin")).SequenceEqual(data));
    }

    [Fact]
    public async Task DroppedConnection_IsRetried()
    {
        var data = RandomBytes(100_000);
        var url = _server.Add("drop.bin", data);
        lock (_server.DropOnce) _server.DropOnce.Add("drop.bin");
        await Make().DownloadOneAsync(new DownloadItem(url, Local("drop.bin"), Sha1(data), data.Length));
        Assert.True(File.ReadAllBytes(Local("drop.bin")).SequenceEqual(data));
    }

    [Fact]
    public async Task BadHashFromMirror_FallsBackToOfficial()
    {
        var good = RandomBytes(50_000);
        var bad = RandomBytes(50_001)[..50_000];
        var officialUrl = _server.Add("official/f.bin", good);
        _server.Add("mirror/f.bin", bad);
        var mirror = new PrefixMirrorSource("m", new Dictionary<string, string> { [_server.Root + "official/"] = _server.Root + "mirror/" });
        var chain = new SourceChain([mirror, new OfficialSource()]);

        await Make(chain).DownloadOneAsync(new DownloadItem(officialUrl, Local("f.bin"), Sha1(good), good.Length));
        Assert.True(File.ReadAllBytes(Local("f.bin")).SequenceEqual(good));
    }

    [Fact]
    public async Task ServerError_AndStall_FallBackToNextSource()
    {
        var data = RandomBytes(40_000);
        var officialUrl = _server.Add("official/a.bin", data);
        _server.Add("mirror/a.bin", data);
        _server.Fail500.Add("mirror/a.bin");
        var mirror = new PrefixMirrorSource("m", new Dictionary<string, string> { [_server.Root + "official/"] = _server.Root + "mirror/" });
        await Make(new SourceChain([mirror, new OfficialSource()])).DownloadOneAsync(new DownloadItem(officialUrl, Local("a.bin"), Sha1(data)));
        Assert.True(File.Exists(Local("a.bin")));

        var officialUrl2 = _server.Add("official/b.bin", data);
        _server.Add("mirror/b.bin", data);
        _server.Hang.Add("mirror/b.bin");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Make(new SourceChain([mirror, new OfficialSource()])).DownloadOneAsync(new DownloadItem(officialUrl2, Local("b.bin"), Sha1(data)));
        Assert.True(File.ReadAllBytes(Local("b.bin")).SequenceEqual(data));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "卡住的源应该很快被放弃");
    }

    [Fact]
    public async Task HashFailure_WithoutGoodSource_Throws_AndLeavesNothing()
    {
        var data = RandomBytes(10_000);
        var url = _server.Add("x.bin", data);
        await Assert.ThrowsAsync<InvalidDataException>(() => Make().DownloadOneAsync(new DownloadItem(url, Local("x.bin"), new string('0', 40))));
        Assert.False(File.Exists(Local("x.bin")));
        Assert.False(File.Exists(Local("x.bin.part")));
    }

    [Fact]
    public async Task DownloadAll_ReportsBytesAndFailures()
    {
        var a = RandomBytes(30_000);
        var items = new List<DownloadItem>
        {
            new(_server.Add("1.bin", a), Local("1.bin"), Sha1(a)),
            new(_server.Add("2.bin", a), Local("2.bin"), Sha1(a)),
            new(_server.Root + "missing.bin", Local("3.bin")),
        };
        var last = default(DownloadProgress);
        var e = await Assert.ThrowsAsync<DownloadException>(() => Make().DownloadAllAsync(items, new Sync(p => last = p)));
        Assert.Single(e.Failures);
        Assert.Equal((3, 3), (last.CompletedFiles, last.TotalFiles));
        Assert.Equal(60_000, last.BytesDownloaded);
    }

    [Fact]
    public async Task HashCache_SkipsRehash_ButDetectsChanges()
    {
        var data = RandomBytes(20_000);
        var url = _server.Add("c.bin", data);
        var cachePath = Local("hashes.txt");
        var item = new DownloadItem(url, Local("c.bin"), Sha1(data), data.Length);

        await Make(cache: new HashCache(cachePath)).DownloadOneAsync(item);
        Assert.Contains(Sha1(data), File.ReadAllText(cachePath));

        // 新进程读回缓存：不发请求就判定有效
        var requests = _server.Requests;
        Assert.Equal(0, await Make(cache: new HashCache(cachePath)).DownloadOneAsync(item));
        Assert.Equal(requests, _server.Requests);

        // 内容被改（同样大小，时间变了）：重新校验发现不对，重新下载
        var tampered = (byte[])data.Clone();
        tampered[0] ^= 0xFF;
        File.WriteAllBytes(Local("c.bin"), tampered);
        File.SetLastWriteTimeUtc(Local("c.bin"), DateTime.UtcNow.AddMinutes(1));
        await Make(cache: new HashCache(cachePath)).DownloadOneAsync(item);
        Assert.True(File.ReadAllBytes(Local("c.bin")).SequenceEqual(data));
    }

    private sealed class Sync(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => action(value);
    }
}
