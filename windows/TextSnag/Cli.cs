using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ZXing;
using ZXing.Common;

namespace TextSnag
{
    /// <summary>Non-UI entry points used for testing. Run them piped (`TextSnag.exe --selftest | Out-Host`) so the shell waits.</summary>
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            switch (args[0])
            {
                case "--ocr":
                {
                    if (args.Length < 2 || !File.Exists(args[1]))
                    {
                        Console.Error.WriteLine("usage: TextSnag --ocr <image> [--no-line-breaks] [--raw]");
                        return 2;
                    }
                    Ocr.WarmUp();
                    var image = RawImage.Load(args[1]);
                    var timer = Stopwatch.StartNew();
                    var options = new FormatOptions { KeepLineBreaks = !args.Contains("--no-line-breaks") };
                    var result = Ocr.Recognize(image, options);
                    Console.WriteLine(args.Contains("--raw") ? result.Raw : result.Combined);
                    Console.Error.WriteLine($"({result.Kind.Label}, recognized in {timer.Elapsed.TotalSeconds:0.000}s, language: {Ocr.LanguageName})");
                    return result.IsEmpty ? 1 : 0;
                }
                case "--lines":
                {
                    // Debug: what the OCR engine returned, line by line, with left edges.
                    if (args.Length < 2 || !File.Exists(args[1])) return 2;
                    var page = Ocr.ReadPage(RawImage.Load(args[1]));
                    foreach (var l in page.Lines) Console.WriteLine($"x={l.Box.MinX:0.000} w={l.Box.Width:0.000} h={l.Box.Height:0.000}  {l.Text}");
                    Console.WriteLine("-- cells --");
                    foreach (var l in page.Cells) Console.WriteLine($"x={l.Box.MinX:0.000} y={l.Box.MinY:0.000}  {l.Text}");
                    return 0;
                }
                case "--selftest":
                    return SelfTest();
                case "--paste-test":
                    return PasteTest();
                case "--capture-test":
                {
                    // End to end without the mouse: grab a screen area, read it, report size and
                    // timing only (the text itself isn't printed).
                    Ocr.WarmUp();
                    var timer = Stopwatch.StartNew();
                    var area = Rectangle.Intersect(new Rectangle(0, 0, 900, 600), Screen.PrimaryScreen.Bounds);
                    var image = ScreenCapture.Grab(area);
                    double grab = timer.Elapsed.TotalSeconds;
                    int chars = image == null ? 0 : Ocr.Recognize(image).Combined.Length;
                    Console.WriteLine($"{(chars > 0 ? "PASS" : "FAIL")}  captured screen area: {chars} characters, grab {grab:0.000}s, total {timer.Elapsed.TotalSeconds:0.000}s");
                    return chars > 0 ? 0 : 1;
                }
                default:
                    Console.Error.WriteLine("usage: TextSnag [--ocr <image> [--no-line-breaks] [--raw] | --lines <image> | --selftest | --paste-test | --capture-test]");
                    return 2;
            }
        }

        private static int failures;

        private static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length == 0 ? "" : "  \u2014 " + detail)}");
            if (!ok) failures++;
        }

        private static string Show(string s) => s.Replace("\n", "\u23CE").Replace("\t", "\u21E5");

        private static int SelfTest()
        {
            failures = 0;
            var t = Stopwatch.StartNew();
            Ocr.WarmUp();
            Console.WriteLine($"      warm-up {t.Elapsed.TotalSeconds:0.00}s, OCR language: {Ocr.LanguageName}");
            if (!Ocr.IsAvailable)
            {
                Check("Windows has an OCR language installed", false,
                      "add one in Settings > Time & language > Language & region, or: Get-WindowsCapability -Online -Name 'Language.OCR*'");
            }

            // Prose
            Console.WriteLine("\n\u2014 Text");
            var img = Ocr.SampleImage(new[] { "The quick brown fox jumps", "over the lazy dog.", "", "Second paragraph here." });
            t = Stopwatch.StartNew();
            var kept = Ocr.Recognize(img);
            double elapsed = t.Elapsed.TotalSeconds;
            Check("recognizes text", kept.Text.Contains("quick brown fox jumps") && kept.Text.Contains("Second paragraph"), Show(kept.Text));
            Check("keeps line breaks", kept.Text.Split('\n').Count(l => l.Length > 0) == 3);
            Check("recognition under 0.5s", elapsed < 0.5, $"{elapsed:0.000}s");
            Check("classified as text", kept.Kind.Type == ContentType.Prose, kept.Kind.Label);

            var merged = Ocr.Recognize(img, new FormatOptions { KeepLineBreaks = false });
            Check("merges lines into paragraphs",
                  merged.Text.Contains("jumps over the lazy dog.") && merged.Text.Contains("\n\nSecond paragraph"), Show(merged.Text));

            // Code
            Console.WriteLine("\n\u2014 Code");
            var codeLines = new[] { "func greet(name: String) {", "if name.isEmpty {", "return", "}", "print(\"Hi, \\(name)!\")", "}" };
            var codeIndents = new[] { 0, 4, 8, 4, 4, 0 };
            foreach (bool dark in new[] { false, true })
            {
                var code = Ocr.Recognize(Ocr.SampleImage(codeLines, 2, "Consolas", 24, dark, codeIndents));
                var indents = code.Text.Split('\n').Select(l => l.Length - l.TrimStart(' ').Length).ToArray();
                string name = dark ? "dark terminal" : "light editor";
                Check($"{name}: detected as code", code.Kind.Type == ContentType.Code, code.Kind.Label);
                Check($"{name}: braces and parentheses intact",
                      code.Text.Contains("func greet(name: String) {") && code.Text.Contains("print(\"Hi, \\(name)!\")"), Show(code.Text));
                Check($"{name}: indentation kept", indents.SequenceEqual(codeIndents), string.Join(",", indents));
            }

            // Tables
            Console.WriteLine("\n\u2014 Tables");
            var table = Ocr.Recognize(RenderTable(new[]
            {
                new[] { "Name", "Qty", "Price" }, new[] { "Apples", "3", "$1.20" }, new[] { "Bananas", "12", "$0.50" },
                new[] { "Cherries", "1", "$4.00" },
            }));
            if (table.Kind.IsTable)
            {
                var rows = table.Kind.Rows;
                Check("detected as table", true, $"{rows.Count} rows \u00D7 {rows[0].Count} columns");
                Check("rows and columns", rows.Count == 4 && rows.All(r => r.Count == 3) && rows[2][0] == "Bananas" && rows[3][1] == "1", Show(table.Text));
                Check("plain text drawn as a table", table.Text.StartsWith("| Name     | Qty | Price |\n| -------- | --- | ----- |", StringComparison.Ordinal), Show(table.Text));
                Check("Not a Table action", TextFormatter.PlainRows(rows).StartsWith("Name Qty Price\nApples 3 $1.20", StringComparison.Ordinal),
                      Show(TextFormatter.PlainRows(rows)));
            }
            else
            {
                Check("detected as table", false, $"{table.Kind.Label}: {Show(table.Text)}");
            }
            var paragraph = Ocr.Recognize(Ocr.SampleImage(new[]
            {
                "A normal paragraph of text that wraps", "onto a second line and then", "a third, with no columns at all.",
            }));
            Check("ordinary text is not mistaken for a table", paragraph.Kind.Type == ContentType.Prose, paragraph.Kind.Label);

            // Lists
            Console.WriteLine("\n\u2014 Lists");
            var listLines = new[] { "\u2022 Fruit", "\u25E6 Apples and pears", "\u25E6 Bananas", "\u2022 Vegetables", "\u25E6 Carrots", "\u25AA Organic only",
                                    "1. First step", "2. Second step" };
            var listIndents = new[] { 0, 4, 4, 0, 4, 8, 0, 0 };
            string expectedList = "\u2022 Fruit\n    \u25E6 Apples and pears\n    \u25E6 Bananas\n\u2022 Vegetables\n    \u25E6 Carrots\n        \u25AA Organic only\n1. First step\n2. Second step";
            var list = Ocr.Recognize(Ocr.SampleImage(listLines, 2, indents: listIndents));
            Check("bullets, styles and nesting kept", list.Text == expectedList, Show(list.Text));

            // QR
            Console.WriteLine("\n\u2014 QR codes");
            var qr = Ocr.Recognize(RenderQR("https://example.com/textsnag"));
            Check("reads QR code", qr.Codes.SequenceEqual(new[] { "https://example.com/textsnag" }), string.Join(", ", qr.Codes));

            // App
            Console.WriteLine("\n\u2014 App");
            Check("default shortcut is Ctrl+Alt+5", Shortcut.DefaultCapture.DisplayString == "Ctrl+Alt+5", Shortcut.DefaultCapture.DisplayString);
            Check("shortcut round-trips through settings", Shortcut.Parse(Shortcut.DefaultCapture.Serialize()) == Shortcut.DefaultCapture);
            using (var hotkeys = new Form())
            {
                // Registering the default shortcut tells us whether another app already owns it.
                bool free = Native.RegisterHotKey(hotkeys.Handle, 99, (uint)(Shortcut.DefaultCapture.Modifiers | Native.MOD_NOREPEAT),
                                                  (uint)Shortcut.DefaultCapture.Key);
                if (free) Native.UnregisterHotKey(hotkeys.Handle, 99);
                Check("Ctrl+Alt+5 is available", free, free ? "" : "another app has registered it");
            }

            Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Copies through the paste-detecting clipboard, then pastes from another process
        /// (PowerShell's Get-Clipboard) and checks TextSnag noticed. Restores the clipboard text afterwards.
        /// </summary>
        private static int PasteTest()
        {
            string saved = null;
            try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { }

            const string sample = "TextSnag paste test\n    \u25E6 indented";
            bool pasted = false;
            var clipboard = new ClipboardService();
            clipboard.CopyWatchingForPaste(sample, null, () => pasted = true);
            Pump(1000); // a real paste comes after a moment

            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            string got = "";
            using (var reader = Process.Start(info))
            {
                // Read on another thread: this thread has to keep answering the clipboard's request.
                var output = new Thread(() => got = reader.StandardOutput.ReadToEnd());
                output.Start();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!reader.HasExited && DateTime.UtcNow < deadline) Pump(20);
                output.Join(2000);
                Pump(200);
            }
            got = got.TrimEnd('\r', '\n').Replace("\r\n", "\n");

            try
            {
                if (saved != null) Clipboard.SetText(saved); else Clipboard.Clear();
            }
            catch { }
            clipboard.DestroyHandle();

            bool intact = got == sample;
            bool ok = pasted && intact;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  paste detected: {pasted}, pasted text intact: {intact}{(intact ? "" : "  \u2014 got " + Show(got))}");
            return ok ? 0 : 1;
        }

        private static void Pump(int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        private static RawImage RenderTable(string[][] rows)
        {
            const float scale = 2;
            float[] columnX = { 40, 330, 560 };
            const float rowHeight = 52;
            var size = new SizeF(800, rows.Length * rowHeight + 60);
            using (var bitmap = new Bitmap((int)(size.Width * scale), (int)(size.Height * scale), PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var regular = new Font("Segoe UI", 24, GraphicsUnit.Pixel))
                using (var bold = new Font("Segoe UI", 24, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var rule = new SolidBrush(Color.FromArgb(217, 217, 217)))
                {
                    g.ScaleTransform(scale, scale);
                    g.Clear(Color.White);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    for (int r = 0; r < rows.Length; r++)
                    {
                        float y = 30 + r * rowHeight;
                        for (int c = 0; c < rows[r].Length; c++)
                        {
                            g.DrawString(rows[r][c], r == 0 ? bold : regular, Brushes.Black, columnX[c], y, StringFormat.GenericTypographic);
                        }
                        g.FillRectangle(rule, 30, y + rowHeight - 12, size.Width - 60, 1);
                    }
                }
                return RawImage.FromBitmap(bitmap);
            }
        }

        private static RawImage RenderQR(string payload)
        {
            var writer = new BarcodeWriterGeneric
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new EncodingOptions { Width = 0, Height = 0, Margin = 0 },
            };
            BitMatrix matrix = writer.Encode(payload);
            const int moduleSize = 10, pad = 40;
            int w = matrix.Width * moduleSize + 2 * pad, h = matrix.Height * moduleSize + 2 * pad;
            var pixels = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int mx = (x - pad) / moduleSize, my = (y - pad) / moduleSize;
                    bool black = x >= pad && y >= pad && mx < matrix.Width && my < matrix.Height && matrix[mx, my];
                    int i = (y * w + x) * 4;
                    byte v = black ? (byte)0 : (byte)255;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                    pixels[i + 3] = 255;
                }
            }
            return new RawImage(w, h, pixels);
        }
    }
}
