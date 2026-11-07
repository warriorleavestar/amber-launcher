using System.Runtime.InteropServices;
using Amber.Core.RemoteControl;
using SkiaSharp;

namespace Amber.App.Services;

/// <summary>远程控制画面块的 JPEG 编码（Avalonia 自带的 Skia，不另加依赖）。只在内存里编解码，不落盘。</summary>
public sealed class SkiaTileCodec : ITileCodec
{
    public byte Id => 2;

    public byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var copy = bgra[..(stride * (height - 1) + width * 4)].ToArray();
        var handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), stride);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(Math.Clamp(quality, 1, 100), SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
            return data?.ToArray() ?? [];
        }
        finally
        {
            handle.Free();
        }
    }

    public bool Decode(ReadOnlySpan<byte> data, Span<byte> bgra, int width, int height, int stride)
    {
        if (data.Length == 0) return false;
        try
        {
            using var bitmap = SKBitmap.Decode(data.ToArray(), new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            if (bitmap is null || bitmap.Width != width || bitmap.Height != height || bitmap.ColorType != SKColorType.Bgra8888) return false;
            var pixels = bitmap.GetPixelSpan();
            var rowBytes = bitmap.RowBytes;
            for (var y = 0; y < height; y++) pixels.Slice(y * rowBytes, width * 4).CopyTo(bgra.Slice(y * stride, width * 4));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
