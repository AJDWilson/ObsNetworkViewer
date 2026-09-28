# Converts assets\logo.png into the multi-size assets\app.ico embedded in the exe.
# The solid black background is made transparent and the image is cropped to the artwork.
param(
    [string]$Source = "$PSScriptRoot\..\assets\logo.png",
    [string]$Output = "$PSScriptRoot\..\assets\app.ico"
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconMaker
{
    // Dark pixels connected to the image border are background; enclosed dark areas (screens, OBS logo) stay opaque.
    const int BackgroundMaxBrightness = 40;
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Make(string source, string output)
    {
        using (var original = new Bitmap(source))
        using (var keyed = KeyOutBlack(original))
        {
            Rectangle crop = SquareContentBounds(keyed);
            var images = new List<byte[]>();
            foreach (int size in Sizes)
            {
                using (var frame = Resize(keyed, crop, size))
                    images.Add(size >= 256 ? EncodePng(frame) : EncodeDib(frame));
            }
            WriteIco(output, images);
        }
    }

    static Bitmap KeyOutBlack(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
            g.DrawImage(source, 0, 0, source.Width, source.Height);

        int w = result.Width, h = result.Height;
        byte[] px = ReadPixels(result);
        var visited = new bool[w * h];
        var pending = new Stack<int>();
        for (int x = 0; x < w; x++) { pending.Push(x); pending.Push((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { pending.Push(y * w); pending.Push(y * w + w - 1); }

        while (pending.Count > 0)
        {
            int p = pending.Pop();
            if (visited[p])
                continue;
            visited[p] = true;
            int i = p * 4;
            if (Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) > BackgroundMaxBrightness)
                continue;

            px[i + 3] = 0;
            int x = p % w, y = p / w;
            if (x > 0) pending.Push(p - 1);
            if (x < w - 1) pending.Push(p + 1);
            if (y > 0) pending.Push(p - w);
            if (y < h - 1) pending.Push(p + w);
        }
        WritePixels(result, px);
        return result;
    }

    static Rectangle SquareContentBounds(Bitmap bmp)
    {
        byte[] px = ReadPixels(bmp);
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (px[(y * bmp.Width + x) * 4 + 3] > 0)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }

        int side = Math.Max(maxX - minX, maxY - minY) + 1;
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        return new Rectangle(cx - side / 2, cy - side / 2, side, side);
    }

    static Bitmap Resize(Bitmap source, Rectangle crop, int size)
    {
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            attributes.SetWrapMode(WrapMode.Clamp);
            g.DrawImage(source, new Rectangle(0, 0, size, size), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    static byte[] EncodePng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // Classic 32bpp icon frame: BITMAPINFOHEADER, bottom-up BGRA pixels, then a 1bpp AND mask.
    static byte[] EncodeDib(Bitmap bmp)
    {
        int size = bmp.Width;
        byte[] px = ReadPixels(bmp);
        int maskStride = ((size + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(size); w.Write(size * 2);
            w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(size * size * 4 + maskStride * size);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--)
                w.Write(px, y * size * 4, size * 4);
            for (int y = size - 1; y >= 0; y--)
            {
                var row = new byte[maskStride];
                for (int x = 0; x < size; x++)
                    if (px[(y * size + x) * 4 + 3] == 0)
                        row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
            return ms.ToArray();
        }
    }

    static void WriteIco(string path, List<byte[]> images)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                int size = Sizes[i];
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images)
                w.Write(image);
        }
    }

    static byte[] ReadPixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++)
            Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width * 4, bmp.Width * 4);
        bmp.UnlockBits(data);
        return px;
    }

    static void WritePixels(Bitmap bmp, byte[] px)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < bmp.Height; y++)
            Marshal.Copy(px, y * bmp.Width * 4, data.Scan0 + y * data.Stride, bmp.Width * 4);
        bmp.UnlockBits(data);
    }
}
"@

[IconMaker]::Make((Resolve-Path $Source).Path, [IO.Path]::GetFullPath($Output))
Write-Host "Wrote $Output"
