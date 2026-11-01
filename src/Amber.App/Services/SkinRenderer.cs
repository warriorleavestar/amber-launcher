using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Amber.App.Services;

/// <summary>把皮肤贴图渲染成正面头像和全身图（只用像素拼接，放大时不插值）。</summary>
public static class SkinRenderer
{
    private readonly record struct Part(int X, int Y, int W, int H, int ToX, int ToY, bool Mirror = false);

    public static (Bitmap Head, Bitmap Body)? Render(string skinPath, bool slim)
    {
        try
        {
            using var source = new Bitmap(skinPath);
            var width = source.PixelSize.Width;
            var height = source.PixelSize.Height;
            if (width < 64 || width % 64 != 0 || (height != width && height != width / 2)) return null;

            var scale = width / 64;
            var pixels = new byte[width * height * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { source.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
            finally { handle.Free(); }
            var swap = source.Format == PixelFormat.Rgba8888;

            var legacy = height == width / 2;
            var armWidth = slim ? 3 : 4;
            var head = Compose(pixels, width, scale, swap, 8, 8, 6,
            [
                new(8, 8, 8, 8, 0, 0),
                new(40, 8, 8, 8, 0, 0),
            ]);

            List<Part> body =
            [
                new(8, 8, 8, 8, 4, 0),
                new(20, 20, 8, 12, 4, 8),
                new(44, 20, armWidth, 12, 4 - armWidth, 8),
                legacy ? new(44, 20, armWidth, 12, 12, 8, Mirror: true) : new(36, 52, armWidth, 12, 12, 8),
                new(4, 20, 4, 12, 4, 20),
                legacy ? new(4, 20, 4, 12, 8, 20, Mirror: true) : new(20, 52, 4, 12, 8, 20),
                new(40, 8, 8, 8, 4, 0),
            ];
            if (!legacy)
            {
                body.AddRange(
                [
                    new(20, 36, 8, 12, 4, 8),
                    new(44, 36, armWidth, 12, 4 - armWidth, 8),
                    new(52, 52, armWidth, 12, 12, 8),
                    new(4, 36, 4, 12, 4, 20),
                    new(4, 52, 4, 12, 8, 20),
                ]);
            }
            return (head, Compose(pixels, width, scale, swap, 16, 32, 6, body));
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static WriteableBitmap Compose(byte[] source, int sourceWidth, int scale, bool swap, int w, int h, int zoom, IReadOnlyList<Part> parts)
    {
        var target = new byte[w * h * 4];
        foreach (var part in parts)
        {
            for (var y = 0; y < part.H; y++)
            for (var x = 0; x < part.W; x++)
            {
                var srcX = (part.Mirror ? part.X + part.W - 1 - x : part.X + x) * scale;
                var srcY = (part.Y + y) * scale;
                var s = (srcY * sourceWidth + srcX) * 4;
                if (source[s + 3] < 128) continue;
                var d = ((part.ToY + y) * w + part.ToX + x) * 4;
                target[d] = source[swap ? s + 2 : s];
                target[d + 1] = source[s + 1];
                target[d + 2] = source[swap ? s : s + 2];
                target[d + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(new PixelSize(w * zoom, h * zoom), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var row = new byte[w * zoom * 4];
        for (var y = 0; y < h * zoom; y++)
        {
            for (var x = 0; x < w * zoom; x++)
                Array.Copy(target, (y / zoom * w + x / zoom) * 4, row, x * 4, 4);
            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}
