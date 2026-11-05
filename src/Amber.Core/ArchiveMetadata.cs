using System.Buffers;
using System.IO.Compression;
using Amber.Core.Localization;

namespace Amber.Core;

/// <summary>只读展示或导入所需的小块元数据，不按压缩包声明的大小分配内存。</summary>
internal static class ArchiveMetadata
{
    internal const int MaxMetadataBytes = 2 * 1024 * 1024;
    internal const int MaxPackMetadataBytes = 8 * 1024 * 1024;
    internal const int MaxNestedJarBytes = 32 * 1024 * 1024;
    internal const int MaxNestedTotalBytes = 64 * 1024 * 1024;
    internal const int MaxNestedJars = 128;

    /// <summary>一次模组扫描中的所有内嵌包及其元数据共用预算，避免逐项限额被重复利用。</summary>
    internal sealed class Budget(long bytes)
    {
        internal long Remaining { get; private set; } = bytes;

        internal void Check(long count)
        {
            if (count < 0 || count > Remaining) throw TooLarge();
        }

        internal void Consume(int count)
        {
            Check(count);
            Remaining -= count;
        }
    }

    internal static InvalidDataException TooLarge() => new(L.T("core.archive.metadata.toolarge"));

    internal static byte[] ReadBytes(ZipArchiveEntry entry, int maxBytes = MaxMetadataBytes, Budget? budget = null)
    {
        if (entry.Length > maxBytes) throw TooLarge();
        budget?.Check(entry.Length);
        using var stream = entry.Open();
        return ReadBytes(stream, maxBytes, budget);
    }

    internal static byte[] ReadBytes(Stream stream, int maxBytes, Budget? budget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                // 到达上限后只多读一个字节来判断结束，超限数据永远不写进内存缓冲。
                var remaining = Math.Min(maxBytes - memory.Length, budget?.Remaining ?? long.MaxValue);
                var count = (int)Math.Min(buffer.Length, remaining + 1);
                var read = stream.Read(buffer, 0, count);
                if (read == 0) return memory.ToArray();
                if (read > remaining) throw TooLarge();
                budget?.Consume(read);
                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static string ReadText(ZipArchiveEntry entry, int maxBytes = MaxMetadataBytes, Budget? budget = null) =>
        Decode(ReadBytes(entry, maxBytes, budget));

    internal static string ReadFileText(string path, int maxBytes = MaxMetadataBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes) throw TooLarge();
        return Decode(ReadBytes(stream, maxBytes));
    }

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
