using System.Buffers.Binary;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 隐私+ 的两路拆分（2-of-2）：每块数据拆成随机垫片 r（走 A 腿）和 d⊕r（走 B 腿），只拿到一条腿的人什么也还原不出来。
/// 两条腿各自已经是端到端认证加密的 <see cref="SecureStream"/>（各做过一次完整的进服握手），所以这里不再另加 MAC。
/// 块格式：[4 字节序号][2 字节长度][数据]。收方核对两边序号和长度一致才异或；对不上、或者只有一条腿结束，都按出错断开。
/// </summary>
public sealed class SplitStream : Stream, IAuthenticatedStream
{
    public const int MaxChunk = 16 * 1024;
    private const int HeaderLength = 6;
    private readonly SecureStream _a;
    private readonly SecureStream _b;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private uint _sendSeq;
    private uint _receiveSeq;
    private byte[] _readBuffer = [];
    private int _readOffset;
    private bool _aborted;
    private bool _ended;

    /// <param name="a">序号 0 的腿（发垫片）。</param>
    /// <param name="b">序号 1 的腿（发异或后的数据）。</param>
    public SplitStream(SecureStream a, SecureStream b)
    {
        _a = a;
        _b = b;
    }

    public bool ClosedCleanly => _a.ClosedCleanly && _b.ClosedCleanly;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => FlushAsync().GetAwaiter().GetResult();
    public override async Task FlushAsync(CancellationToken ct) => await Task.WhenAll(_a.FlushAsync(ct), _b.FlushAsync(ct));
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            while (!buffer.IsEmpty)
            {
                var n = Math.Min(buffer.Length, MaxChunk);
                var pad = new byte[HeaderLength + n];
                var mixed = new byte[HeaderLength + n];
                BinaryPrimitives.WriteUInt32BigEndian(pad, _sendSeq);
                BinaryPrimitives.WriteUInt16BigEndian(pad.AsSpan(4), (ushort)n);
                pad.AsSpan(0, HeaderLength).CopyTo(mixed);
                RandomNumberGenerator.Fill(pad.AsSpan(HeaderLength));
                Xor(buffer.Span[..n], pad.AsSpan(HeaderLength), mixed.AsSpan(HeaderLength));
                _sendSeq++;
                await Task.WhenAll(_a.WriteAsync(pad, ct).AsTask(), _b.WriteAsync(mixed, ct).AsTask());
                buffer = buffer[n..];
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_readOffset >= _readBuffer.Length)
        {
            if (_ended) return 0;
            var chunk = await ReadChunkAsync(ct);
            if (chunk is null)
            {
                _ended = true;
                return 0;
            }
            _readBuffer = chunk;
            _readOffset = 0;
        }
        var count = Math.Min(buffer.Length, _readBuffer.Length - _readOffset);
        _readBuffer.AsSpan(_readOffset, count).CopyTo(buffer.Span);
        _readOffset += count;
        return count;
    }

    private async Task<byte[]?> ReadChunkAsync(CancellationToken ct)
    {
        var headerA = new byte[HeaderLength];
        var headerB = new byte[HeaderLength];
        var gotA = SecureStream.ReadExactAsync(_a, headerA, ct);
        var gotB = SecureStream.ReadExactAsync(_b, headerB, ct);
        await WhenBothOrOneEndsAsync(gotA, gotB);
        var (a, b) = (await gotA, await gotB);
        if (!a && !b) return null;
        // 只有一条腿结束：不能当成正常结束（另一条腿可能被人截断了）。
        if (a != b) throw new IOException(L.T("core.gate.split.leg_lost"));
        if (!headerA.AsSpan().SequenceEqual(headerB) || BinaryPrimitives.ReadUInt32BigEndian(headerA) != _receiveSeq)
            throw new CryptographicException(L.T("core.gate.split.mismatch"));
        _receiveSeq++;
        var length = BinaryPrimitives.ReadUInt16BigEndian(headerA.AsSpan(4));
        if (length is 0 or > MaxChunk) throw new CryptographicException(L.T("core.gate.split.mismatch"));
        var pad = new byte[length];
        var mixed = new byte[length];
        var bodyA = SecureStream.ReadExactAsync(_a, pad, ct);
        var bodyB = SecureStream.ReadExactAsync(_b, mixed, ct);
        await WhenBothOrOneEndsAsync(bodyA, bodyB);
        if (!await bodyA | !await bodyB) throw new IOException(L.T("core.gate.split.leg_lost"));
        Xor(pad, mixed, pad);
        return pad;
    }

    /// <summary>
    /// 一条腿结束或出错时，另一条腿多半还在等数据（节点断了，对面不会再发）：给它一点时间（正常结束时两边的结束记录差不多同时到），
    /// 还没动静就两条一起断开，免得卡住。
    /// </summary>
    private async Task WhenBothOrOneEndsAsync(Task<bool> x, Task<bool> y)
    {
        var first = await Task.WhenAny(x, y);
        if (first.IsCompletedSuccessfully && first.Result) return;
        var other = ReferenceEquals(first, x) ? y : x;
        if (await Task.WhenAny(other, Task.Delay(EndGrace)) == other) return;
        Abort();
        try { await other; } catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException or OperationCanceledException or System.Net.Sockets.SocketException) { }
        throw new IOException(L.T("core.gate.split.leg_lost"));
    }

    private static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(3);

    private static void Xor(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<byte> output)
    {
        for (var i = 0; i < output.Length; i++) output[i] = (byte)(x[i] ^ y[i]);
    }

    public void Abort()
    {
        _aborted = true;
        _a.Abort();
        _b.Abort();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_aborted)
        {
            _a.Abort();
            _b.Abort();
        }
        await Task.WhenAll(_a.DisposeAsync().AsTask(), _b.DisposeAsync().AsTask());
        _writeLock.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = DisposeAsync().AsTask();
        base.Dispose(disposing);
    }
}
