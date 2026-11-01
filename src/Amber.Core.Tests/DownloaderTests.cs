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

    /// <summary>这个文件第一次请求只发一半就断开。</summary>
    public HashSet<string> DropOnce { get; } = [];

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
        try
        {
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

    private Downloader Make(SourceChain? chain = null, HashCache? cache = null) => new(_http, chain ?? new SourceChain([new OfficialSource()]))
    {
        RetriesPerSource = 2,
        ChunkThreshold = 1024 * 1024,
        MinChunkSize = 256 * 1024,
        StallTimeout = TimeSpan.FromSeconds(2),
        Hashes = cache,
    };

    private string Local(string name) => Path.Combine(_root, name);

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
