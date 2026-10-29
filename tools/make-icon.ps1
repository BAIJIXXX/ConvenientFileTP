# Builds assets\app.ico from a transparent PNG (default: the single *.png in the project root).
# ASCII-only: PowerShell 5.1 reads BOM-less scripts as ANSI.
param(
    [string]$Source = '',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
if (-not (Test-Path -LiteralPath $assets)) { New-Item -ItemType Directory -Force -Path $assets | Out-Null }

if ([string]::IsNullOrWhiteSpace($Source)) {
    $candidates = @(Get-ChildItem -LiteralPath $root -Filter '*.png' -File -ErrorAction SilentlyContinue)
    $fallback = Join-Path $assets 'icon-source.png'
    if ($candidates.Count -eq 1) { $Source = $candidates[0].FullName }
    elseif (Test-Path -LiteralPath $fallback) { $Source = $fallback }
    else { throw 'No source PNG found. Put exactly one *.png in the project root or pass -Source.' }
}
if (-not (Test-Path -LiteralPath $Source)) { throw "Source PNG not found: $Source" }

if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $assets 'app.ico' }

Add-Type -AssemblyName System.Drawing

if (-not ('PngIconBuilder' -as [type])) {
    Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class PngIconBuilder
{
    public static void Build(string pngPath, string icoPath, int[] sizes)
    {
        using (var src = new Bitmap(pngPath))
        using (var argb = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(argb))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }

            int minX = argb.Width, minY = argb.Height, maxX = -1, maxY = -1;
            var rect = new Rectangle(0, 0, argb.Width, argb.Height);
            var data = argb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                for (int y = 0; y < argb.Height; y++)
                {
                    for (int x = 0; x < argb.Width; x++)
                    {
                        if (pixels[y * data.Stride + x * 4 + 3] > 8)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
            finally { argb.UnlockBits(data); }

            if (maxX < 0) throw new InvalidOperationException("Source PNG is fully transparent.");

            int gw = maxX - minX + 1;
            int gh = maxY - minY + 1;
            int side = (int)Math.Ceiling(Math.Max(gw, gh) * 1.04);
            int offX = (side - gw) / 2;
            int offY = (side - gh) / 2;

            using (var master = new Bitmap(side, side, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(master))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(argb, new Rectangle(offX, offY, gw, gh),
                        new Rectangle(minX, minY, gw, gh), GraphicsUnit.Pixel);
                }

                var images = new List<byte[]>();
                var dims = new List<int>();
                foreach (int s in sizes)
                {
                    using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.Transparent);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.DrawImage(master, new Rectangle(0, 0, s, s));
                        }
                        images.Add(s >= 256 ? EncodePng(bmp) : EncodeDib(bmp));
                        dims.Add(s);
                    }
                }
                WriteIco(icoPath, dims, images);
            }
        }
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static byte[] EncodeDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        byte[] pixels = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] raw = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * data.Stride; // DIB rows are bottom-up
                Array.Copy(raw, srcRow, pixels, y * w * 4, w * 4);
            }
        }
        finally { bmp.UnlockBits(data); }

        int maskRow = ((w + 31) / 32) * 4;
        int maskSize = maskRow * h;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40);            // biSize
            bw.Write(w);             // biWidth
            bw.Write(h * 2);         // biHeight (image + mask)
            bw.Write((short)1);      // biPlanes
            bw.Write((short)32);     // biBitCount
            bw.Write(0);             // biCompression = BI_RGB
            bw.Write(w * h * 4);     // biSizeImage
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(pixels);
            bw.Write(new byte[maskSize]);
            bw.Flush();
            return ms.ToArray();
        }
    }

    private static void WriteIco(string path, List<int> dims, List<byte[]> images)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);          // reserved
            bw.Write((short)1);          // type: icon
            bw.Write((short)dims.Count); // image count
            int offset = 6 + 16 * dims.Count;
            for (int i = 0; i < dims.Count; i++)
            {
                int s = dims[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);       // color count
                bw.Write((byte)0);       // reserved
                bw.Write((short)1);      // planes
                bw.Write((short)32);     // bit count
                bw.Write(images[i].Length);
                bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
    }
}
'@
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
[PngIconBuilder]::Build($Source, $Output, $sizes)

$file = Get-Item -LiteralPath $Output
Write-Host ("icon written: {0} ({1} bytes, sizes: {2})" -f $file.FullName, $file.Length, ($sizes -join '/'))
