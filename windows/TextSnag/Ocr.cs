using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;
using ZXing.Common;

namespace TextSnag
{
    internal sealed class OcrResult
    {
        public string Text;               // formatted, what goes on the clipboard
        public string Raw;                // unformatted lines, for the "Original" quick action
        public List<string> Codes;        // QR / barcode payloads
        public ContentKind Kind;
        public bool IsDirectText;         // true when the text came from the app itself, not OCR

        public bool IsEmpty => Text.Length == 0 && Codes.Count == 0;

        /// <summary>What goes on the clipboard: QR/barcode payloads first, then recognized text.</summary>
        public string Combined => string.Join("\n", Codes.Concat(Text.Length == 0 ? new string[0] : new[] { Text }));
    }

    /// <summary>What one OCR pass returned.</summary>
    internal sealed class OcrPage
    {
        public readonly List<TextLine> Lines = new List<TextLine>(); // one per recognized line
        public readonly List<TextLine> Cells = new List<TextLine>(); // lines split at wide gaps (table cells)

        public int Characters => Lines.Sum(l => l.Text.Count(char.IsLetterOrDigit));
    }

    /// <summary>
    /// Text recognition with Windows' built-in OCR engine (Windows.Media.Ocr), plus QR/barcode
    /// reading. Everything runs on the PC; nothing is uploaded.
    /// </summary>
    internal static class Ocr
    {
        public static volatile bool IsWarm;

        /// <summary>One engine per thread: it isn't documented as safe to share across threads.</summary>
        private static readonly ThreadLocal<OcrEngine> Engines = new ThreadLocal<OcrEngine>(CreateEngine);

        private static OcrEngine CreateEngine()
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null) return engine;
            // None of the user's languages has OCR installed, but maybe another language does.
            var available = OcrEngine.AvailableRecognizerLanguages;
            return available.Count > 0 ? OcrEngine.TryCreateFromLanguage(available[0]) : null;
        }

        /// <summary>False when Windows has no OCR language installed at all.</summary>
        public static bool IsAvailable
        {
            get
            {
                try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
                catch { return false; }
            }
        }

        public static string LanguageName
        {
            get
            {
                try { return Engines.Value?.RecognizerLanguage.DisplayName ?? "none"; }
                catch { return "none"; }
            }
        }

        /// <summary>Synchronous. Call from a background thread.</summary>
        public static OcrResult Recognize(RawImage image, FormatOptions options = null)
        {
            options = options ?? new FormatOptions();
            // QR/barcode detection runs in parallel with text recognition so it adds no time.
            var codesTask = Task.Run(() => DetectCodes(image));

            var page = ReadPage(image);
            var lines = page.Lines.Select(FixMisreadBullet).ToList();
            var kind = ContentKind.Prose;
            var layout = options.DetectTables ? TextFormatter.DetectTable(page.Cells) : null;
            if (layout != null)
            {
                kind = ContentKind.Table(FillEmptyCells(image, layout));
            }
            else if (options.DetectCode && TextFormatter.LooksLikeCode(lines))
            {
                kind = ContentKind.Code;
            }
            else if (options.TidyBullets || options.KeepIndentation)
            {
                lines = RestoreBullets(image, lines);
            }

            List<string> codes;
            try { codes = codesTask.Result; }
            catch (Exception e) { Log.Write("barcode detection failed: " + e.Message); codes = new List<string>(); }

            string text = TextFormatter.Format(lines, kind, options);
            string raw = string.Join("\n", lines.Select(l => l.Text));
            // Don't repeat a QR payload that was also read as text.
            bool duplicate = codes.Contains(text);
            return new OcrResult { Text = duplicate ? "" : text, Raw = duplicate ? "" : raw, Codes = codes, Kind = kind };
        }

        /// <summary>Loads the engine and runs one throwaway pass, so the first real capture is fast.</summary>
        public static void WarmUp()
        {
            try
            {
                Recognize(SampleImage());
            }
            catch (Exception e)
            {
                Log.Write("warm-up failed: " + e);
            }
            IsWarm = true;
        }

        // MARK: - Reading

        /// <summary>
        /// Windows OCR struggles with light text on dark backgrounds (terminals, dark mode), so dark
        /// images are also read inverted, at the same time; the read that found more wins.
        /// </summary>
        public static OcrPage ReadPage(RawImage image)
        {
            if (image.MeanBrightness() >= 100) return ReadOnce(image);
            var normal = Task.Run(() => ReadOnce(image));
            var inverted = ReadOnce(image.Inverted());
            var original = normal.Result;
            bool invertedWins = inverted.Lines.Count > original.Lines.Count
                || (inverted.Lines.Count == original.Lines.Count && inverted.Characters > original.Characters);
            return invertedWins ? inverted : original;
        }

        public static OcrPage ReadOnce(RawImage image)
        {
            var page = new OcrPage();
            var engine = Engines.Value;
            if (engine == null || image == null) return page;

            // Windows OCR reads small screen text much better when it's enlarged, and refuses
            // images larger than MaxImageDimension.
            double max = OcrEngine.MaxImageDimension;
            double scale = (long)image.Width * image.Height <= 2_500_000 ? 2 : 1;
            scale = Math.Min(scale, Math.Min(max / image.Width, max / image.Height));
            var input = Math.Abs(scale - 1) < 0.01 ? image : image.Scaled(scale);

            Windows.Media.Ocr.OcrResult result;
            using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(input.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                                                                    input.Width, input.Height, BitmapAlphaMode.Premultiplied))
            {
                result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            }

            double W = input.Width, H = input.Height;
            foreach (var line in result.Lines)
            {
                var words = line.Words.ToList();
                if (words.Count == 0) continue;
                page.Lines.Add(new TextLine(JoinWords(words), BoxOf(words, W, H)));

                // Split the line where words are far apart: that's how separate table cells look.
                double height = words.Max(w => w.BoundingRect.Height);
                var cell = new List<OcrWord> { words[0] };
                for (int i = 1; i < words.Count; i++)
                {
                    var a = words[i - 1].BoundingRect;
                    var b = words[i].BoundingRect;
                    if (b.X - (a.X + a.Width) > height * 1.1)
                    {
                        page.Cells.Add(new TextLine(JoinWords(cell), BoxOf(cell, W, H)));
                        cell = new List<OcrWord>();
                    }
                    cell.Add(words[i]);
                }
                page.Cells.Add(new TextLine(JoinWords(cell), BoxOf(cell, W, H)));
            }
            return page;
        }

        private static Box BoxOf(List<OcrWord> words, double imageWidth, double imageHeight)
        {
            double left = words.Min(w => w.BoundingRect.X);
            double top = words.Min(w => w.BoundingRect.Y);
            double right = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            double bottom = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            return Box.FromPixels(left, top, right - left, bottom - top, imageWidth, imageHeight);
        }

        /// <summary>
        /// Words joined with spaces, except between Chinese/Japanese characters (Windows OCR returns
        /// each of those as its own "word").
        /// </summary>
        private static string JoinWords(List<OcrWord> words)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i].Text;
                if (i > 0 && !(IsCjk(sb[sb.Length - 1]) && w.Length > 0 && IsCjk(w[0]))) sb.Append(' ');
                sb.Append(w);
            }
            return sb.ToString();
        }

        private static bool IsCjk(char c) =>
            (c >= '぀' && c <= 'ヿ') || (c >= '㐀' && c <= '鿿') || (c >= '豈' && c <= '﫿')
            || (c >= '＀' && c <= '￯') || (c >= '　' && c <= '〿');

        private static List<string> DetectCodes(RawImage image)
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options = new DecodingOptions
                {
                    TryHarder = (long)image.Width * image.Height <= 1_500_000,
                    TryInverted = true,
                },
            };
            var source = new RGBLuminanceSource(image.Pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var results = reader.DecodeMultiple(source);
            var seen = new HashSet<string>();
            return (results ?? new Result[0]).Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t) && seen.Add(t)).ToList();
        }

        // MARK: - Bullets

        /// <summary>
        /// Windows OCR often reads a round bullet "•" as a comma. No line of real text starts with
        /// ", ", so treat it as the bullet it was.
        /// </summary>
        private static TextLine FixMisreadBullet(TextLine line) =>
            line.Text.StartsWith(", ", StringComparison.Ordinal) ? new TextLine("• " + line.Text.Substring(2), line.Box, line.Confidence) : line;

        private static readonly Regex NumberedLine = new Regex(@"^(\d{1,3}|[a-zA-Z])[.)]\s", RegexOptions.Compiled);

        /// <summary>
        /// OCR skips small bullet dots, which also loses a list's nesting. Look just left of where
        /// each line's text starts for a small dot or square; if there is one, it's a bullet, and
        /// the line starts at the bullet.
        /// </summary>
        private static List<TextLine> RestoreBullets(RawImage image, List<TextLine> lines)
        {
            var gray = new GrayImage(image);
            double W = gray.Width, H = gray.Height;
            return lines.Select(line =>
            {
                string s = line.Text.Trim();
                if (TextFormatter.TidyBullet(s) != s || NumberedLine.IsMatch(s)) return line; // already has a marker
                double x0 = line.Box.MinX * W;
                double top = (1 - line.Box.MaxY) * H, bottom = (1 - line.Box.MinY) * H;
                double h = bottom - top;
                if (h <= 6) return line;
                int row0 = (int)(top + 0.12 * h), row1 = (int)(bottom - 0.08 * h);
                int col0 = (int)Math.Max(0, x0 - 2.2 * h), col1 = (int)Math.Max(0, x0 - 0.04 * h);
                if (col1 - col0 + 1 <= 2) return line;
                var blob = gray.NearestBlob(col0, col1, row0, row1, fromRight: true);
                if (blob == null) return line;

                double bw = blob.Right - blob.Left + 1, bh = blob.Bottom - blob.Top + 1;
                double centerY = (blob.Top + blob.Bottom) / 2.0;
                bool looksLikeBullet = bw >= 0.1 * h && bw <= 0.6 * h && bh >= 0.1 * h && bh <= 0.6 * h
                    && bw / bh >= 0.5 && bw / bh <= 2.0
                    && centerY >= top + 0.2 * h && centerY <= bottom - 0.1 * h
                    && blob.IsolatedOnLeft;
                if (!looksLikeBullet) return line;
                double minX = blob.Left / W;
                var box = new Box(minX, line.Box.MinY, line.Box.MaxX - minX, line.Box.Height);
                return new TextLine("• " + s, box, line.Confidence);
            }).ToList();
        }

        // MARK: - Tables

        /// <summary>
        /// OCR often drops a cell holding a single character ("1", "3"). Re-read each empty cell on
        /// its own, cropped and enlarged.
        /// </summary>
        private static List<List<string>> FillEmptyCells(RawImage image, TableLayout layout)
        {
            var rows = layout.Rows.Select(r => r.ToList()).ToList();
            var empty = new List<(int r, int c)>();
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Count; c++)
                    if (rows[r][c].Length == 0) empty.Add((r, c));
            if (empty.Count == 0 || empty.Count > 40) return rows;

            double W = image.Width, H = image.Height;
            var found = new string[empty.Count];
            Parallel.For(0, empty.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var (r, c) = empty[i];
                var col = layout.Columns[c];
                var band = layout.Bands[r];
                // The column's extent between its neighbours, and the row's band, with some margin.
                double left = c > 0 ? (layout.Columns[c - 1].Hi + col.Lo) / 2 : Math.Max(0, col.Lo - 0.02);
                double right = c + 1 < layout.Columns.Count ? (col.Hi + layout.Columns[c + 1].Lo) / 2 : Math.Min(1, col.Hi + 0.02);
                double pad = (band.Hi - band.Lo) * 0.3;
                double x = left * W, y = (1 - band.Hi - pad) * H;
                double w = (right - left) * W, h = (band.Hi - band.Lo + 2 * pad) * H;
                int x0 = (int)Math.Floor(Math.Max(0, x)), y0 = (int)Math.Floor(Math.Max(0, y));
                int x1 = (int)Math.Ceiling(Math.Min(W, x + w)), y1 = (int)Math.Ceiling(Math.Min(H, y + h));
                if (x1 - x0 <= 2 || y1 - y0 <= 2) return;
                var crop = image.Crop(new Rectangle(x0, y0, x1 - x0, y1 - y0));
                if (crop == null) return;
                byte corner = new GrayImage(crop).Pixel(0, 0);
                var big = crop.Scaled(3, pad: 24, background: corner);
                found[i] = string.Join(" ", ReadOnce(big).Lines.Select(l => l.Text)).Trim();
            });
            for (int i = 0; i < empty.Count; i++)
            {
                if (!string.IsNullOrEmpty(found[i])) rows[empty[i].r][empty[i].c] = found[i];
            }
            return rows;
        }

        // MARK: - Test images

        /// <summary>An image with lines of text, used for warm-up and self-tests.</summary>
        public static RawImage SampleImage(IList<string> lines = null, float scale = 1, string font = "Segoe UI",
                                           float fontSize = 28, bool dark = false, IList<int> indents = null)
        {
            lines = lines ?? new[] { "TextSnag warm up", "Hello world 123" };
            float lineHeight = fontSize * 1.6f;
            float width = 900, height = 60 + lines.Count * lineHeight;
            using (var bitmap = new Bitmap((int)(width * scale), (int)(height * scale), PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var typeface = new Font(font, fontSize, GraphicsUnit.Pixel))
                using (var ink = new SolidBrush(dark ? Color.FromArgb(230, 230, 230) : Color.Black))
                {
                    g.ScaleTransform(scale, scale);
                    g.Clear(dark ? Color.FromArgb(26, 26, 26) : Color.White);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var format = (StringFormat)StringFormat.GenericTypographic.Clone();
                    format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    float space = g.MeasureString("    ", typeface, 1000, format).Width / 4;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        float indent = indents != null && i < indents.Count ? indents[i] * space : 0;
                        g.DrawString(lines[i], typeface, ink, 30 + indent, 30 + i * lineHeight, format);
                    }
                }
                return RawImage.FromBitmap(bitmap);
            }
        }
    }
}
