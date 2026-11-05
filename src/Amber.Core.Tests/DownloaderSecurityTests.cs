using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public sealed class DownloaderSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-download-security-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "file.bin");
    private string Part => Target + ".part";
    private string State => Part + ".state";

    public DownloaderSecurityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private Downloader Make(HttpClient http, long limit = 1024) => new(http, new SourceChain([new OfficialSource()]))
    {
        RetriesPerSource = 1,
        MaxFileSizeBytes = limit,
        ChunkThreshold = 64,
        MaxChunksPerFile = 2,
        MinChunkSize = 32,
    };

    private DownloadItem Item(long? size = null, byte[]? data = null) => new("https://download.example.test/file", Target,
        data is null ? null : Convert.ToHexStringLower(SHA1.HashData(data)), size);

    private void AssertRejectedFilesRemoved()
    {
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Part));
        Assert.False(File.Exists(State));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, BodyStream body, string? range = null, long? length = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
        if (range is not null) response.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse(range);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public async Task ResponseCannotPreallocateBeyondDeclaredSize(long declared)
    {
        using var body = new BodyStream([1]);
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.PartialContent, body,
            $"bytes 0-{long.MaxValue - 1}/{long.MaxValue}")));

        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(declared)));

        Assert.Equal(0, body.Reads);
        AssertRejectedFilesRemoved();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1025)]
    public async Task InvalidDeclaredSizeDoesNotContactServer(long declared)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("不应请求服务器"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(declared)));
        Assert.Equal(0, handler.Requests);
        AssertRejectedFilesRemoved();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryResponseCannotExceedKnownSize(bool advertised)
    {
        using var body = new BodyStream(new byte[17]);
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.OK, body, length: advertised ? 17 : null)));
        var written = new InlineProgress();

        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(16), written));

        Assert.InRange(written.Maximum, 0, 16);
        if (advertised) Assert.Equal(0, body.Reads);
        AssertRejectedFilesRemoved();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownSizeResponseHasFiniteByteLimit(bool advertised)
    {
        using var body = new BodyStream(new byte[33]);
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.OK, body, length: advertised ? 33 : null)));
        var written = new InlineProgress();

        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http, 32).DownloadOneAsync(Item(), written));

        Assert.InRange(written.Maximum, 0, 32);
        AssertRejectedFilesRemoved();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bytes 1-127/128")]
    [InlineData("bytes 0-126/128")]
    [InlineData("bytes 0-127/*")]
    [InlineData("bytes 0-127/129")]
    [InlineData("items 0-127/128")]
    public async Task InvalidInitialRangeIsRejectedBeforeBody(string? range)
    {
        using var body = new BodyStream(new byte[128]);
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.PartialContent, body, range)));

        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(128)));

        Assert.Equal(0, body.Reads);
        AssertRejectedFilesRemoved();
    }

    [Fact]
    public async Task PartialResponseWithoutRangeRequestIsRejected()
    {
        using var body = new BodyStream(new byte[8]);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Null(request.Headers.Range);
            return Response(HttpStatusCode.PartialContent, body, "bytes 0-7/8");
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(8)));
        Assert.Equal(0, body.Reads);
        AssertRejectedFilesRemoved();
    }

    [Fact]
    public async Task MismatchedRangeContentLengthIsRejectedBeforeAllocation()
    {
        using var body = new BodyStream(new byte[128]);
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.PartialContent, body, "bytes 0-127/128", 129)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(128)));
        Assert.Equal(0, body.Reads);
        AssertRejectedFilesRemoved();
    }

    [Fact]
    public async Task UnknownSizeRangeStreamsWithoutPreallocationAndPreservesHash()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('界', 100));
        using var body = new BodyStream(bytes, beforeRead: () => Assert.Equal(0, new FileInfo(Part).Length));
        var handler = new Handler(request =>
        {
            Assert.Equal("bytes=0-", request.Headers.Range!.ToString());
            return Response(HttpStatusCode.PartialContent, body, $"bytes 0-{bytes.Length - 1}/{bytes.Length}");
        });
        using var http = new HttpClient(handler);

        Assert.Equal(bytes.Length, await Make(http).DownloadOneAsync(Item(data: bytes)));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(Target));
        Assert.False(File.Exists(State));
    }

    [Fact]
    public async Task UnknownSizeWithoutHeadersStreamsWithinLimit()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.OK, new BodyStream(bytes))));
        Assert.Equal(4, await Make(http, 4).DownloadOneAsync(Item(data: bytes)));
        Assert.Equal(bytes, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task EmptyKnownFileIsAccepted()
    {
        using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.OK, new BodyStream([]), length: 0)));
        Assert.Equal(0, await Make(http).DownloadOneAsync(Item(0, [])));
        Assert.Equal(0, new FileInfo(Target).Length);
    }

    [Fact]
    public async Task ServerIgnoringResumeRangeRestartsAndHashesWholeFile()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Part, [99, 99]);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=2-", request.Headers.Range!.ToString());
            return Response(HttpStatusCode.OK, new BodyStream(bytes), length: bytes.Length);
        }));
        Assert.Equal(4, await Make(http).DownloadOneAsync(Item(4, bytes)));
        Assert.Equal(bytes, File.ReadAllBytes(Target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumeUsesRemainingBudgetAndPreservesOriginalPrefix(bool malicious)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Part, bytes[..2]);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=2-", request.Headers.Range!.ToString());
            return Response(HttpStatusCode.PartialContent, new BodyStream(malicious ? [3, 4, 5] : [3, 4]), "bytes 2-3/4");
        }));
        if (malicious)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(4, bytes)));
            AssertRejectedFilesRemoved();
        }
        else
        {
            Assert.Equal(2, await Make(http).DownloadOneAsync(Item(4, bytes)));
            Assert.Equal(bytes, File.ReadAllBytes(Target));
        }
    }

    [Theory]
    [InlineData("bytes 0-3/8", true)]
    [InlineData("bytes 1-4/8", false)]
    [InlineData("bytes 0-3/9", false)]
    [InlineData(null, false)]
    public async Task ChunkResponseCannotCrossRequestedBoundary(string? range, bool oversizedBody)
    {
        var sentinel = Enumerable.Repeat((byte)99, 8).ToArray();
        File.WriteAllBytes(Part, sentinel);
        var state = Downloader.ChunkState.Create(8, 2, 4);
        state.Chunks[1].Done = true;
        state.Save(State);
        using var body = new BodyStream(oversizedBody ? [1, 2, 3, 4, 5] : [1, 2, 3, 4]);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=0-3", request.Headers.Range!.ToString());
            return Response(HttpStatusCode.PartialContent, body, range);
        }));
        var written = new InlineProgress();

        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(8), written));

        Assert.InRange(written.Maximum, 0, 4);
        if (!oversizedBody) Assert.Equal(0, body.Reads);
        AssertRejectedFilesRemoved();
    }

    [Theory]
    [InlineData("{\"Total\":9,\"Chunks\":[{\"Start\":0,\"End\":8,\"Done\":true}]}", 8)]
    [InlineData("{\"Total\":8,\"Chunks\":[{\"Start\":0,\"End\":7,\"Done\":true}]}", 7)]
    [InlineData("{\"Total\":8,\"Chunks\":[{\"Start\":1,\"End\":7}]}", 8)]
    [InlineData("{\"Total\":8,\"Chunks\":[{\"Start\":0,\"End\":3},{\"Start\":3,\"End\":7}]}", 8)]
    [InlineData("{\"Total\":8,\"Chunks\":[{\"Start\":0,\"End\":3},{\"Start\":5,\"End\":7}]}", 8)]
    [InlineData("{\"Total\":8,\"Chunks\":[{\"Start\":0,\"End\":8}]}", 8)]
    [InlineData("{\"Total\":8,\"Chunks\":null}", 8)]
    [InlineData("{\"Total\":8,\"Chunks\":[null]}", 8)]
    [InlineData("null", 8)]
    [InlineData("{", 8)]
    public async Task InvalidSavedStateIsRejectedBeforeNetwork(string state, int partLength)
    {
        File.WriteAllBytes(Part, new byte[partLength]);
        File.WriteAllText(State, state);
        var handler = new Handler(_ => throw new InvalidOperationException("不应请求服务器"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(8)));

        Assert.Equal(0, handler.Requests);
        AssertRejectedFilesRemoved();
    }

    [Fact]
    public async Task OversizedStateIsRejectedWithoutParsing()
    {
        File.WriteAllBytes(Part, new byte[8]);
        File.WriteAllText(State, new string(' ', 64 * 1024 + 1));
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("不应请求服务器")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Make(http).DownloadOneAsync(Item(8)));
        AssertRejectedFilesRemoved();
    }

    [Fact]
    public async Task ValidSavedStateOnlyDownloadsMissingRange()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        File.WriteAllBytes(Part, [1, 2, 3, 4, 0, 0, 0, 0]);
        var state = Downloader.ChunkState.Create(8, 2, 4);
        state.Chunks[0].Done = true;
        state.Save(State);
        var handler = new Handler(request =>
        {
            Assert.Equal("bytes=4-7", request.Headers.Range!.ToString());
            return Response(HttpStatusCode.PartialContent, new BodyStream(data[4..]), "bytes 4-7/8", 4);
        });
        using var http = new HttpClient(handler);

        Assert.Equal(4, await Make(http).DownloadOneAsync(Item(8, data)));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(data, File.ReadAllBytes(Target));
        Assert.False(File.Exists(State));
    }

    private sealed class InlineProgress : IProgress<long>
    {
        public long Maximum { get; private set; }
        public void Report(long value) => Maximum = Math.Max(Maximum, value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1025)]
    public async Task InvalidSizeInBatchIsAggregatedWhileHealthyItemsFinish(long invalidSize)
    {
        var healthy = new byte[] { 1, 2, 3, 4 };
        var handler = new Handler(request =>
        {
            Assert.EndsWith("/healthy", request.RequestUri!.AbsoluteUri);
            return Response(HttpStatusCode.OK, new BodyStream(healthy), length: healthy.Length);
        });
        using var http = new HttpClient(handler);
        var good = new DownloadItem("https://download.example.test/healthy", Target, Size: 4);
        var bad = new DownloadItem("https://download.example.test/bad", Path.Combine(_root, "bad.bin"), Size: invalidSize);
        var progress = new BatchProgress();
        var error = await Assert.ThrowsAsync<DownloadException>(() => Make(http).DownloadAllAsync([good, bad], progress));
        Assert.Equal(bad, Assert.Single(error.Failures).Item);
        Assert.IsType<InvalidDataException>(error.Failures[0].Error);
        Assert.Equal(healthy, File.ReadAllBytes(Target));
        Assert.Equal(1, handler.Requests);
        Assert.Equal((2, 2, 4L), (progress.Last.CompletedFiles, progress.Last.TotalFiles, progress.Last.BytesDownloaded));
        Assert.False(File.Exists(bad.LocalPath));
    }

    private sealed class BatchProgress : IProgress<DownloadProgress>
    {
        public DownloadProgress Last { get; private set; }
        public void Report(DownloadProgress value) => Last = value;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(send(request));
        }
    }

    // 不提供 Length，让测试覆盖没有 Content-Length 的实际流式响应，避免测试框架自动补上长度。
    private sealed class BodyStream(byte[] data, Action? beforeRead = null) : Stream
    {
        private int _position;
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Reads++ == 0) beforeRead?.Invoke();
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
