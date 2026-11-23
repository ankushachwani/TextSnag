using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TextSnag
{
    /// <summary>
    /// An immutable BGRA pixel buffer (top-left origin, 4 bytes per pixel, no row padding).
    /// GDI+ Bitmaps can't be read from several threads at once; this can, so OCR, QR detection and
    /// bullet checks all run in parallel on the same capture.
    /// </summary>
    internal sealed class RawImage
    {
        public readonly int Width, Height;
        public readonly byte[] Pixels;

        public RawImage(int width, int height, byte[] pixels)
        {
            Width = width; Height = height; Pixels = pixels;
        }

        /// <summary>Copies a bitmap (or part of it). Alpha is forced opaque: screen grabs often have alpha 0.</summary>
        public static RawImage FromBitmap(Bitmap bitmap, Rectangle? region = null)
        {
            var r = Rectangle.Intersect(region ?? new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                        new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (r.Width <= 0 || r.Height <= 0) return null;
            var data = bitmap.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = r.Width * 4;
                var pixels = new byte[rowBytes * r.Height];
                for (int y = 0; y < r.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
                }
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return new RawImage(r.Width, r.Height, pixels);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>Loads an image file, flattened onto white (so transparent PNGs read as dark-on-light).</summary>
        public static RawImage Load(string path)
        {
            using (var source = Image.FromFile(path))
            using (var flat = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.White);
                    g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
                }
                return FromBitmap(flat);
            }
        }

        public Bitmap ToBitmap()
        {
            var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = Width * 4;
                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(Pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        public RawImage Crop(Rectangle rect)
        {
            var r = Rectangle.Intersect(rect, new Rectangle(0, 0, Width, Height));
            if (r.Width <= 0 || r.Height <= 0) return null;
            var pixels = new byte[r.Width * r.Height * 4];
            for (int y = 0; y < r.Height; y++)
            {
                Buffer.BlockCopy(Pixels, ((r.Y + y) * Width + r.X) * 4, pixels, y * r.Width * 4, r.Width * 4);
            }
            return new RawImage(r.Width, r.Height, pixels);
        }

        public RawImage Inverted()
        {
            var pixels = new byte[Pixels.Length];
            for (int i = 0; i < Pixels.Length; i += 4)
            {
                pixels[i] = (byte)(255 - Pixels[i]);
                pixels[i + 1] = (byte)(255 - Pixels[i + 1]);
                pixels[i + 2] = (byte)(255 - Pixels[i + 2]);
                pixels[i + 3] = 255;
            }
            return new RawImage(Width, Height, pixels);
        }

        /// <summary>Resized by <paramref name="factor"/>, optionally padded with a solid gray background.</summary>
        public RawImage Scaled(double factor, int pad = 0, byte background = 255)
        {
            int w = Math.Max(1, (int)Math.Round(Width * factor)), h = Math.Max(1, (int)Math.Round(Height * factor));
            using (var source = ToBitmap())
            using (var target = new Bitmap(w + 2 * pad, h + 2 * pad, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(target))
                {
                    g.Clear(Color.FromArgb(background, background, background));
                    g.InterpolationMode = factor > 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
                        g.DrawImage(source, new Rectangle(pad, pad, w, h), 0, 0, Width, Height, GraphicsUnit.Pixel, attributes);
                    }
                }
                return FromBitmap(target);
            }
        }

        /// <summary>Average brightness (0–255) from a sparse grid of pixels.</summary>
        public double MeanBrightness()
        {
            const int samples = 48;
            long sum = 0;
            int n = 0;
            for (int sy = 0; sy < samples; sy++)
            {
                int y = (int)((sy + 0.5) * Height / samples);
                for (int sx = 0; sx < samples; sx++)
                {
                    int x = (int)((sx + 0.5) * Width / samples);
                    sum += Luma(x, y);
                    n++;
                }
            }
            return n == 0 ? 255 : (double)sum / n;
        }

        public int Luma(int x, int y)
        {
            int i = (Math.Min(Math.Max(y, 0), Height - 1) * Width + Math.Min(Math.Max(x, 0), Width - 1)) * 4;
            return (Pixels[i + 2] * 299 + Pixels[i + 1] * 587 + Pixels[i] * 114) / 1000;
        }
    }

    /// <summary>An 8-bit grayscale copy of an image, for quick pixel checks.</summary>
    internal sealed class GrayImage
    {
        public readonly int Width, Height;
        private readonly byte[] pixels;

        public GrayImage(RawImage image)
        {
            Width = image.Width;
            Height = image.Height;
            pixels = new byte[Width * Height];
            for (int i = 0, p = 0; i < pixels.Length; i++, p += 4)
            {
                pixels[i] = (byte)((image.Pixels[p + 2] * 299 + image.Pixels[p + 1] * 587 + image.Pixels[p] * 114) / 1000);
            }
        }

        /// <summary>Top-left origin, clamped to the image.</summary>
        public byte Pixel(int x, int y) =>
            pixels[Math.Min(Math.Max(y, 0), Height - 1) * Width + Math.Min(Math.Max(x, 0), Width - 1)];

        public sealed class Blob
        {
            public int Left, Right, Top, Bottom; // inclusive
            public bool IsolatedOnLeft;          // no other ink between it and the far edge of the search area
        }

        /// <summary>
        /// The run of "ink" columns closest to the right (or left) edge of the area, where ink is
        /// anything clearly different from the area's background. Ranges are inclusive.
        /// </summary>
        public Blob NearestBlob(int col0, int col1, int row0, int row1, bool fromRight)
        {
            int c0 = Math.Max(col0, 0), c1 = Math.Min(col1, Width - 1);
            int r0 = Math.Max(row0, 0), r1 = Math.Min(row1, Height - 1);
            if (c1 - c0 + 1 <= 1 || r1 - r0 + 1 <= 1) return null;

            // Background: the most common brightness in the area.
            var histogram = new int[256];
            for (int y = r0; y <= r1; y++) for (int x = c0; x <= c1; x++) histogram[Pixel(x, y)]++;
            int background = 0;
            for (int v = 1; v < 256; v++) if (histogram[v] > histogram[background]) background = v;
            bool Ink(int x, int y) => Math.Abs(Pixel(x, y) - background) > 70;

            int count = c1 - c0 + 1;
            var inkColumns = new bool[count];
            for (int i = 0; i < count; i++)
            {
                for (int y = r0; y <= r1; y++)
                {
                    if (Ink(c0 + i, y)) { inkColumns[i] = true; break; }
                }
            }

            int step = fromRight ? -1 : 1;
            int first = -1;
            for (int i = fromRight ? count - 1 : 0; i >= 0 && i < count; i += step)
            {
                if (inkColumns[i]) { first = i; break; }
            }
            if (first < 0) return null;
            int last = first;
            for (int i = first + step; i >= 0 && i < count && inkColumns[i]; i += step) last = i;

            int lo = Math.Min(first, last), hi = Math.Max(first, last);
            int x0 = c0 + lo, x1 = c0 + hi;
            int top = -1, bottom = -1;
            for (int y = r0; y <= r1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!Ink(x, y)) continue;
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
            }
            if (top < 0) return null;

            bool others = false;
            if (fromRight) { for (int i = 0; i < lo; i++) others |= inkColumns[i]; }
            else { for (int i = hi + 1; i < count; i++) others |= inkColumns[i]; }
            return new Blob { Left = x0, Right = x1, Top = top, Bottom = bottom, IsolatedOnLeft = !others };
        }
    }
}
