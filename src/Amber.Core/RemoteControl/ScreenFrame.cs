using System.IO.Compression;

namespace Amber.Core.RemoteControl;

/// <summary>一张 BGRA 画面（每行 Width×4 字节，没有行尾填充）。</summary>
public sealed class ScreenFrame
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = [];
    public int Stride => Width * 4;

    public ScreenFrame() { }

    public ScreenFrame(int width, int height) => Resize(width, height);

    /// <summary>改尺寸（内容清空）。尺寸没变时什么都不做。</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height && Pixels.Length == width * height * 4) return;
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public void CopyFrom(ScreenFrame other)
    {
        Resize(other.Width, other.Height);
        other.Pixels.CopyTo(Pixels, 0);
    }
}

/// <summary>
/// 块比对：画面按 64×64 切块，和上一次发出去的画面比，只给出变了的块。尺寸变了（或第一张）时全部算变。
/// 比较用整行内存比较，每块最多比 64 行。
/// </summary>
public sealed class TileDiffer
{
    private readonly ScreenFrame _last = new();
    private bool _hasLast;

    public int TileSize { get; }

    public TileDiffer(int tileSize = RemoteWire.TileSize) => TileSize = tileSize;

    /// <summary>下一张全部重发（比如控制方要求刷新）。</summary>
    public void Reset() => _hasLast = false;

    /// <summary>找出变了的块，并把这张画面记成“上一次”。</summary>
    public List<(int X, int Y, int Width, int Height)> Diff(ScreenFrame frame)
    {
        var changed = new List<(int, int, int, int)>();
        var full = !_hasLast || _last.Width != frame.Width || _last.Height != frame.Height;
        var stride = frame.Stride;
        for (var ty = 0; ty < frame.Height; ty += TileSize)
        {
            var th = Math.Min(TileSize, frame.Height - ty);
            for (var tx = 0; tx < frame.Width; tx += TileSize)
            {
                var tw = Math.Min(TileSize, frame.Width - tx);
                if (full || !Same(frame.Pixels, _last.Pixels, stride, tx, ty, tw, th)) changed.Add((tx, ty, tw, th));
            }
        }
        _last.CopyFrom(frame);
        _hasLast = true;
        return changed;
    }

    private static bool Same(byte[] a, byte[] b, int stride, int x, int y, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            var offset = (y + row) * stride + x * 4;
            if (!a.AsSpan(offset, w * 4).SequenceEqual(b.AsSpan(offset, w * 4))) return false;
        }
        return true;
    }
}

/// <summary>画面缩放和坐标换算。</summary>
public static class RemoteGeometry
{
    /// <summary>超过这个尺寸的桌面按整数倍缩小后再发（4K 屏发一半，省带宽）。</summary>
    public const int PreferredMaxWidth = 2560;
    public const int PreferredMaxHeight = 1600;

    /// <summary>缩小倍数（1 表示不缩）。保证结果不超过协议的最大边长。</summary>
    public static int ScaleFor(int width, int height)
    {
        var s = Math.Max(1, Math.Max((width + PreferredMaxWidth - 1) / PreferredMaxWidth, (height + PreferredMaxHeight - 1) / PreferredMaxHeight));
        while (width / s > RemoteWire.MaxDimension || height / s > RemoteWire.MaxDimension) s++;
        return s;
    }

    /// <summary>按 s×s 取平均缩小（s=1 时直接复制）。</summary>
    public static void Downscale(ScreenFrame source, ScreenFrame target, int s)
    {
        if (s <= 1)
        {
            target.CopyFrom(source);
            return;
        }
        int w = Math.Max(1, source.Width / s), h = Math.Max(1, source.Height / s);
        target.Resize(w, h);
        var src = source.Pixels;
        var dst = target.Pixels;
        var srcStride = source.Stride;
        var n = s * s;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int b = 0, g = 0, r = 0;
                for (var dy = 0; dy < s; dy++)
                {
                    var o = (y * s + dy) * srcStride + x * s * 4;
                    for (var dx = 0; dx < s; dx++, o += 4)
                    {
                        b += src[o];
                        g += src[o + 1];
                        r += src[o + 2];
                    }
                }
                var d = (y * w + x) * 4;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n);
                dst[d + 3] = 255;
            }
        }
    }

    /// <summary>画面像素 → 桌面坐标（被控方收到鼠标时用）。超出画面的夹到边上。</summary>
    public static (int X, int Y) FrameToDesktop(ScreenBounds bounds, int frameWidth, int frameHeight, int x, int y)
    {
        x = Math.Clamp(x, 0, Math.Max(0, frameWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, frameHeight - 1));
        // 按比例换算（不只是整数倍：截屏后桌面尺寸可能变了）。取像素中心。
        var dx = bounds.X + (int)((x + 0.5) * bounds.Width / frameWidth);
        var dy = bounds.Y + (int)((y + 0.5) * bounds.Height / frameHeight);
        return (Math.Clamp(dx, bounds.X, bounds.X + bounds.Width - 1), Math.Clamp(dy, bounds.Y, bounds.Y + bounds.Height - 1));
    }

    /// <summary>桌面坐标 → 画面像素（被控方发指针位置时用）。</summary>
    public static (int X, int Y) DesktopToFrame(ScreenBounds bounds, int frameWidth, int frameHeight, int x, int y) =>
        ((int)((long)(x - bounds.X) * frameWidth / Math.Max(1, bounds.Width)), (int)((long)(y - bounds.Y) * frameHeight / Math.Max(1, bounds.Height)));

    /// <summary>
    /// 查看窗口里的点 → 画面像素。画面按比例缩放居中（Uniform），四周可能有黑边；落在黑边上返回 null。
    /// </summary>
    public static (int X, int Y)? ViewToFrame(double viewWidth, double viewHeight, int frameWidth, int frameHeight, double px, double py)
    {
        if (viewWidth <= 0 || viewHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) return null;
        var scale = Math.Min(viewWidth / frameWidth, viewHeight / frameHeight);
        var offsetX = (viewWidth - frameWidth * scale) / 2;
        var offsetY = (viewHeight - frameHeight * scale) / 2;
        var fx = (px - offsetX) / scale;
        var fy = (py - offsetY) / scale;
        if (fx < 0 || fy < 0 || fx >= frameWidth || fy >= frameHeight) return null;
        return ((int)fx, (int)fy);
    }

    /// <summary>
    /// SendInput 的绝对坐标（0–65535 映射到整个虚拟桌面）。
    /// </summary>
    public static (int X, int Y) ToAbsolute(ScreenBounds virtualDesktop, int x, int y)
    {
        int Norm(int v, int origin, int size) => size <= 1 ? 0 : (int)Math.Round((v - origin) * 65535.0 / (size - 1));
        return (Math.Clamp(Norm(x, virtualDesktop.X, virtualDesktop.Width), 0, 65535), Math.Clamp(Norm(y, virtualDesktop.Y, virtualDesktop.Height), 0, 65535));
    }
}

/// <summary>无损编码（Deflate 压缩原始 BGRA）。测试用，也是没有图像库时的退路。</summary>
public sealed class RawTileCodec : ITileCodec
{
    public byte Id => 1;

    public byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            for (var y = 0; y < height; y++) deflate.Write(bgra.Slice(y * stride, width * 4));
        return ms.ToArray();
    }

    public bool Decode(ReadOnlySpan<byte> data, Span<byte> bgra, int width, int height, int stride)
    {
        var row = new byte[width * 4];
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            for (var y = 0; y < height; y++)
            {
                deflate.ReadExactly(row);
                row.CopyTo(bgra.Slice(y * stride, width * 4));
            }
            // 多出来的数据也算不对。
            return deflate.ReadByte() < 0;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }
}

/// <summary>把一块像素从整张画面里拷出来（编码时要连续的内存）。</summary>
internal static class TileCopy
{
    public static byte[] Extract(ScreenFrame frame, int x, int y, int w, int h)
    {
        var tile = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            frame.Pixels.AsSpan((y + row) * frame.Stride + x * 4, w * 4).CopyTo(tile.AsSpan(row * w * 4));
        return tile;
    }
}
