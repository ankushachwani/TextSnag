// A line-for-line port of macos/Sources/TextSnag/TextFormatter.swift. Keep the two in sync: the same
// recognized lines should produce the same clipboard text on macOS and Windows.
//
// Plain C# with no Windows dependencies, so it builds into the app (.NET Framework 4.8) and
// into the cross-platform tests (windows/Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TextSnag
{
    /// <summary>User-controlled cleanup applied to recognized text. Every step is optional.</summary>
    public sealed class FormatOptions
    {
        public bool KeepLineBreaks = true;
        public bool KeepIndentation = true;
        public bool TidyBullets = true;
        public bool FixPunctuation = true;
        public bool StraightQuotes = false;
        public bool DetectCode = true;
        public bool DetectTables = true;

        public FormatOptions Clone() => (FormatOptions)MemberwiseClone();
    }

    /// <summary>
    /// A normalized 0–1 rectangle with its origin at the bottom left: the convention Apple's Vision
    /// uses, kept here so the layout rules read exactly like the macOS version.
    /// </summary>
    public struct Box
    {
        public readonly double X, Y, Width, Height;

        public Box(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public double MinX => X;
        public double MaxX => X + Width;
        public double MinY => Y;
        public double MaxY => Y + Height;
        public double MidY => Y + Height / 2;

        /// <summary>From pixel coordinates with a top-left origin (what Windows OCR reports).</summary>
        public static Box FromPixels(double left, double top, double width, double height, double imageWidth, double imageHeight) =>
            new Box(left / imageWidth, 1 - (top + height) / imageHeight, width / imageWidth, height / imageHeight);
    }

    /// <summary>One line (or table cell) as the recognizer saw it.</summary>
    public sealed class TextLine
    {
        public readonly string Text;
        public readonly Box Box;
        public readonly float Confidence;

        public TextLine(string text, Box box, float confidence = 1)
        {
            Text = text; Box = box; Confidence = confidence;
        }

        public override string ToString() => Text;
    }

    public enum ContentType { Prose, Code, Table }

    public sealed class ContentKind
    {
        public readonly ContentType Type;
        public readonly List<List<string>> Rows; // tables only

        private ContentKind(ContentType type, List<List<string>> rows)
        {
            Type = type; Rows = rows;
        }

        public static readonly ContentKind Prose = new ContentKind(ContentType.Prose, null);
        public static readonly ContentKind Code = new ContentKind(ContentType.Code, null);
        public static ContentKind Table(List<List<string>> rows) => new ContentKind(ContentType.Table, rows);

        public bool IsTable => Type == ContentType.Table;

        public string Label
        {
            get
            {
                switch (Type)
                {
                    case ContentType.Code: return "code";
                    case ContentType.Table: return "table";
                    default: return "text";
                }
            }
        }
    }

    /// <summary>A closed range of normalized coordinates.</summary>
    public struct Extent
    {
        public readonly double Lo, Hi;
        public Extent(double lo, double hi) { Lo = lo; Hi = hi; }
    }

    /// <summary>A detected table: cell text plus where its columns and rows are (normalized).</summary>
    public sealed class TableLayout
    {
        public readonly List<List<string>> Rows;
        public readonly List<Extent> Columns;
        public readonly List<Extent> Bands; // each row's vertical extent

        public TableLayout(List<List<string>> rows, List<Extent> columns, List<Extent> bands)
        {
            Rows = rows; Columns = columns; Bands = bands;
        }
    }

    public static class TextFormatter
    {
        // MARK: - Detection

        public static List<List<string>> Table(IList<TextLine> lines) => DetectTable(lines)?.Rows;

        /// <summary>Looks for rows of separate cells whose columns line up.</summary>
        public static TableLayout DetectTable(IList<TextLine> lines)
        {
            if (lines.Count < 4) return null;

            // Group cells into rows by vertical overlap, top row first.
            var rows = new List<List<TextLine>>();
            foreach (var line in lines.OrderByDescending(l => l.Box.MidY))
            {
                if (rows.Count > 0)
                {
                    var reference = rows[rows.Count - 1][0];
                    if (Math.Abs(reference.Box.MidY - line.Box.MidY) < Math.Min(reference.Box.Height, line.Box.Height) * 0.6)
                    {
                        rows[rows.Count - 1].Add(line);
                        continue;
                    }
                }
                rows.Add(new List<TextLine> { line });
            }
            rows = rows.Select(r => r.OrderBy(c => c.Box.MinX).ToList()).ToList();

            var multi = rows.Where(r => r.Count >= 2).ToList();
            if (multi.Count < 3 || (double)multi.Count / rows.Count < 0.6) return null;

            // Tables have short cells; two-column articles have long ones.
            var words = multi.SelectMany(r => r).Select(c => WordCount(c.Text)).OrderBy(n => n).ToList();
            if (words[words.Count / 2] > 6) return null;

            // Columns: merge the horizontal extents of cells that overlap across rows.
            var columns = new List<Extent>();
            foreach (var cell in multi.SelectMany(r => r).OrderBy(c => c.Box.MinX))
            {
                if (columns.Count > 0 && cell.Box.MinX <= columns[columns.Count - 1].Hi)
                {
                    var last = columns[columns.Count - 1];
                    columns[columns.Count - 1] = new Extent(last.Lo, Math.Max(last.Hi, cell.Box.MaxX));
                }
                else
                {
                    columns.Add(new Extent(cell.Box.MinX, cell.Box.MaxX));
                }
            }
            if (columns.Count < 2 || columns.Count > 12) return null;

            int ColumnFor(TextLine cell)
            {
                int best = 0;
                double bestOverlap = -1;
                for (int i = 0; i < columns.Count; i++)
                {
                    double overlap = Math.Max(0, Math.Min(columns[i].Hi, cell.Box.MaxX) - Math.Max(columns[i].Lo, cell.Box.MinX));
                    if (overlap > bestOverlap) { best = i; bestOverlap = overlap; }
                }
                return best;
            }

            var table = new List<List<string>>();
            foreach (var row in rows)
            {
                var cells = Enumerable.Repeat("", columns.Count).ToList();
                foreach (var cell in row)
                {
                    int i = ColumnFor(cell);
                    cells[i] = cells[i].Length == 0 ? cell.Text : cells[i] + " " + cell.Text;
                }
                table.Add(cells);
            }
            int used = Enumerable.Range(0, columns.Count).Count(i => table.Count(r => r[i].Length > 0) >= 2);
            if (used < 2) return null;
            var bands = rows.Select(r => new Extent(r.Min(c => c.Box.MinY), r.Max(c => c.Box.MaxY))).ToList();
            return new TableLayout(table, columns, bands);
        }

        private static int WordCount(string s) => s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

        private static readonly Regex CodeSymbols = new Regex(@"[{}\[\]();<>=|&]|=>|->|::|//|\+\+|!=", RegexOptions.Compiled);
        private static readonly Regex CodeWords = new Regex(
            @"\b(func|def|let|var|const|return|import|from|class|struct|enum|if|else|elif|for|while|switch|case|public|private|static|void|int|string|fn|print|println|console|echo|sudo|npm|pip|git|brew|cd|ls|export|async|await|true|false|null|nil|None|self|this)\b",
            RegexOptions.Compiled);

        /// <summary>True when most lines look like source code or shell commands.</summary>
        public static bool LooksLikeCode(IList<TextLine> lines)
        {
            if (lines.Count == 0) return false;
            int hits = 0;
            foreach (var line in lines)
            {
                string s = line.Text.Trim();
                int symbols = CodeSymbols.Matches(s).Count;
                int words = CodeWords.Matches(s).Count;
                bool shape = s.EndsWith(";", StringComparison.Ordinal) || s.EndsWith("{", StringComparison.Ordinal) || s == "}"
                    || s.StartsWith("$ ", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal)
                    || s.StartsWith("#!", StringComparison.Ordinal) || s.StartsWith("</", StringComparison.Ordinal)
                    || (s.StartsWith("#", StringComparison.Ordinal) && words > 0);
                if (shape || symbols >= 2 || (symbols >= 1 && words >= 1)) hits++;
            }
            double ratio = (double)hits / lines.Count;
            return lines.Count == 1 ? hits == 1 : ratio >= 0.4;
        }

        // MARK: - Formatting

        public static string Format(IList<TextLine> lines, ContentKind kind, FormatOptions options)
        {
            switch (kind.Type)
            {
                case ContentType.Table:
                    return TextTable(kind.Rows.Select(r => r.Select(FixLookAlikes).ToList()).ToList());
                case ContentType.Code:
                    return FixLookAlikes(FormatCode(lines));
                default:
                    return FixLookAlikes(FormatProse(lines, options));
            }
        }

        /// <summary>Code: keep every line and its indentation; never smart quotes.</summary>
        private static string FormatCode(IList<TextLine> lines)
        {
            double charWidth = EstimatedCharWidth(lines);
            double left = lines.Count == 0 ? 0 : lines.Min(l => l.Box.MinX);
            return string.Join("\n", lines.Select(line =>
            {
                int spaces = charWidth > 0 ? (int)Math.Round((line.Box.MinX - left) / charWidth, MidpointRounding.AwayFromZero) : 0;
                return new string(' ', Math.Max(0, spaces)) + CodeClean(line.Text);
            }));
        }

        /// <summary>Bullet styles by nesting level, the way most apps draw nested lists.</summary>
        public static readonly string[] LevelBullets = { "•", "◦", "▪" };
        public const string IndentUnit = "    ";

        private static string FormatProse(IList<TextLine> lines, FormatOptions options)
        {
            double charWidth = Math.Max(EstimatedCharWidth(lines), 0.004);
            double left = lines.Count == 0 ? 0 : lines.Min(l => l.Box.MinX);
            var texts = lines.Select(l => options.TidyBullets ? TidyBullet(l.Text) : l.Text).ToList();
            var isItem = texts.Select(StartsListItem).ToList();
            var offsets = lines.Select(l => l.Box.MinX - left).ToList();

            // Nesting levels: group the left edges of list items (or of all lines when there's no
            // list) into steps. Offsets past 40% of the width are a second column, not indentation.
            bool hasList = isItem.Contains(true);
            var steps = new List<double>();
            foreach (var v in offsets.Where((_, i) => !hasList || isItem[i]).Where(v => v < 0.4).OrderBy(v => v))
            {
                if (steps.Count == 0 || v - steps[steps.Count - 1] > charWidth * 1.5) steps.Add(v);
            }
            int Level(double offset)
            {
                if (offset >= 0.4) return 0;
                for (int k = steps.Count - 1; k >= 0; k--)
                {
                    if (steps[k] <= offset + charWidth * 0.75) return k;
                }
                return 0;
            }

            var output = new List<string>();
            string lastItemIndent = null;
            TextLine previous = null;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var prev = previous;
                previous = line;
                string s = texts[i];
                string indent = "";
                if (isItem[i])
                {
                    int lvl = Level(offsets[i]);
                    if (options.KeepIndentation) indent = Repeat(IndentUnit, lvl);
                    // OCR reads ◦ and ▪ as "•"; restore the style from the nesting level.
                    if (options.TidyBullets && s.StartsWith("• ", StringComparison.Ordinal))
                    {
                        s = LevelBullets[lvl % LevelBullets.Length] + s.Substring(1);
                    }
                    lastItemIndent = indent;
                }
                else if (options.KeepIndentation)
                {
                    if (hasList && lastItemIndent != null && offsets[i] > charWidth)
                    {
                        indent = lastItemIndent + "  "; // a list item's wrapped line, under its text
                    }
                    else if (!hasList)
                    {
                        indent = Repeat(IndentUnit, Level(offsets[i]));
                    }
                }

                if (prev == null || output.Count == 0) { output.Add(indent + s); continue; }

                if (options.KeepLineBreaks || isItem[i])
                {
                    output.Add(indent + s);
                    continue;
                }
                // Paragraph mode: join lines, but a visible gap or a jump back up starts a new paragraph.
                double gap = prev.Box.MinY - line.Box.MaxY;
                int last = output.Count - 1;
                if (gap > prev.Box.Height * 0.8 || line.Box.MaxY > prev.Box.MaxY)
                {
                    lastItemIndent = null;
                    output.Add("");
                    output.Add(s);
                }
                else if (output[last].EndsWith("-", StringComparison.Ordinal) && s.Length > 0 && char.IsLower(s[0]))
                {
                    output[last] = output[last].Substring(0, output[last].Length - 1) + s; // re-join a hyphenated word
                }
                else
                {
                    output[last] += " " + s;
                }
            }

            string text = string.Join("\n", output);
            if (options.FixPunctuation) text = FixPunctuation(text);
            if (options.StraightQuotes) text = StraightenQuotes(text);
            return text;
        }

        /// <summary>Average width of one character, from lines long enough to measure.</summary>
        private static double EstimatedCharWidth(IList<TextLine> lines)
        {
            var widths = lines.Where(l => l.Text.Length >= 4).Select(l => l.Box.Width / l.Text.Length).OrderBy(w => w).ToList();
            return widths.Count == 0 ? 0 : widths[widths.Count / 2];
        }

        /// <summary>
        /// Text read straight from an app (Direct Text) is already exact, so only layout is applied.
        /// Apps don't report indentation, and list markers often arrive on a line of their own, so
        /// markers are re-attached and the indentation and bullet style are borrowed from the OCR
        /// read of the same area when the lines match up.
        /// </summary>
        public static string FormatDirect(string text, string ocrText, ContentKind kind, FormatOptions options)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (kind.Type == ContentType.Code)
            {
                return string.Join("\n", text.Split('\n').Select(CodeClean));
            }

            var lines = new List<string>();
            string marker = null;
            foreach (var raw in text.Split('\n'))
            {
                string t = raw.Trim();
                if (IsMarkerOnly(t)) { marker = t; continue; }
                if (marker != null && t.Length > 0) { lines.Add(marker + " " + t); marker = null; }
                else lines.Add(t);
            }
            if (options.TidyBullets) lines = lines.Select(TidyBullet).ToList();

            var ocrLines = ocrText.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            var appLines = lines.Where(l => l.Length > 0).ToList();
            if (options.KeepLineBreaks && ocrLines.Count == appLines.Count
                && appLines.Zip(ocrLines, (a, b) => Similarity(a, b) >= 0.5).All(ok => ok))
            {
                int j = 0;
                lines = lines.Select(line =>
                {
                    if (line.Length == 0) return line;
                    string ocr = ocrLines[j++];
                    string indent = options.KeepIndentation ? new string(' ', ocr.Length - ocr.TrimStart(' ').Length) : "";
                    string ocrBody = ocr.TrimStart(' ');
                    string ocrMarker = BulletPrefix(ocrBody);
                    if (ocrMarker == null) return indent + line;
                    string own = BulletPrefix(line);
                    string body = own != null ? line.Substring(own.Length) : line;
                    return indent + ocrMarker + body;
                }).ToList();
            }

            string result;
            if (options.KeepLineBreaks)
            {
                result = string.Join("\n", lines);
            }
            else
            {
                // Join wrapped lines; blank lines and list items still start a new line.
                result = "";
                foreach (var line in lines)
                {
                    if (result.Length == 0) { result = line; continue; }
                    if (line.Length == 0)
                    {
                        if (!result.EndsWith("\n\n", StringComparison.Ordinal)) result += "\n\n";
                        continue;
                    }
                    bool endsWithBreak = result.EndsWith("\n", StringComparison.Ordinal);
                    if (endsWithBreak || StartsListItem(line)) result += (endsWithBreak ? "" : "\n") + line;
                    else result += " " + line;
                }
            }
            if (options.StraightQuotes) result = StraightenQuotes(result);
            return result;
        }

        /// <summary>"•", "◦", "1.", "a)" on a line by itself.</summary>
        private static bool IsMarkerOnly(string s) =>
            Regex.IsMatch(s, @"^([•◦▪●○■□‣⁃∙·\-*–]|\d{1,3}[.)]|[a-zA-Z][.)])$");

        /// <summary>The bullet and its space at the start of a line ("• ", "◦ "), if any.</summary>
        private static string BulletPrefix(string s)
        {
            if (s.Length < 2 || s[1] != ' ') return null;
            char first = s[0];
            if (!BulletGlyphs.Contains(first) && !LevelBullets.Contains(first.ToString())) return null;
            return first + " ";
        }

        /// <summary>Letter-pair (Dice) similarity between two strings, 0–1.</summary>
        public static double Similarity(string a, string b)
        {
            Dictionary<string, int> Pairs(string s)
            {
                var chars = s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
                var counts = new Dictionary<string, int>();
                for (int i = 0; i + 1 < chars.Length; i++)
                {
                    string key = new string(new[] { chars[i], chars[i + 1] });
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
                return counts;
            }
            var x = Pairs(a);
            var y = Pairs(b);
            int total = x.Values.Sum() + y.Values.Sum();
            if (total == 0) return a.ToLowerInvariant() == b.ToLowerInvariant() ? 1 : 0;
            int shared = 0;
            foreach (var pair in x)
            {
                if (y.TryGetValue(pair.Key, out int other)) shared += Math.Min(pair.Value, other);
            }
            return 2.0 * shared / total;
        }

        // MARK: - Look-alike letters

        /// <summary>Cyrillic and Greek letters that look identical to Latin ones.</summary>
        private static readonly Dictionary<char, char> Homoglyphs = new Dictionary<char, char>
        {
            ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's',
            ['ԁ'] = 'd', ['һ'] = 'h', ['ӏ'] = 'l', ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O',
            ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X', ['І'] = 'I', ['Ј'] = 'J', ['Ѕ'] = 'S', ['ο'] = 'o', ['ν'] = 'v', ['Ο'] = 'O',
            ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N', ['Ρ'] = 'P',
            ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X',
        };

        /// <summary>
        /// OCR sometimes returns a Cyrillic "і" inside "Hi". In text that's mostly Latin, swap
        /// look-alike letters back, so words and code stay searchable and runnable. Real Russian or
        /// Greek text (mostly non-Latin) is left alone.
        /// </summary>
        public static string FixLookAlikes(string text)
        {
            int letters = 0, latin = 0;
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (c < 128) latin++;
            }
            if (letters == 0 || (double)latin / letters < 0.6) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) sb.Append(Homoglyphs.TryGetValue(c, out char swap) ? swap : c);
            return sb.ToString();
        }

        // MARK: - Small text fixes

        private static readonly HashSet<char> BulletGlyphs = new HashSet<char>
        {
            '•', '●', '○', '◦', '▪', '▫', '■', '□', '‣', '⁃', '∙', '·', '◆', '◇', '►', '▸', '➢', '➤', '✓', '✔', '–', '—', '*', '-',
        };

        /// <summary>Any bullet style at the start of a line becomes "• " (◦ and ▪ keep their style).</summary>
        public static string TidyBullet(string s)
        {
            string trimmed = s.TrimStart(' ');
            if (trimmed.Length == 0 || !BulletGlyphs.Contains(trimmed[0])) return s;
            char first = trimmed[0];
            string rest = trimmed.Substring(1);
            // "-" and "*" only count as bullets when followed by a space ("-5" and "*args" are not).
            if ((first == '-' || first == '*' || first == '–' || first == '—') && !(rest.Length > 0 && rest[0] == ' ')) return s;
            string text = rest.TrimStart(' ');
            if (text.Length == 0) return s;
            string style = first == '◦' || first == '○' ? "◦" : (first == '▪' || first == '■' ? "▪" : "•");
            return style + " " + text;
        }

        private static readonly Regex NumberedItem = new Regex(@"^\s*(\d{1,3}|[a-zA-Z])[.)]\s", RegexOptions.Compiled);

        public static bool StartsListItem(string s)
        {
            if (s.StartsWith("• ", StringComparison.Ordinal) || s.StartsWith("◦ ", StringComparison.Ordinal)
                || s.StartsWith("▪ ", StringComparison.Ordinal)) return true;
            return NumberedItem.IsMatch(s);
        }

        private static readonly (Regex pattern, string replacement)[] PunctuationRules =
        {
            (new Regex(@"(?<=\S)[ \t]+([,.;:!?%)\]}])", RegexOptions.Compiled), "$1"), // "word ," → "word,"
            (new Regex(@"([(\[{])[ \t]+(?=\S)", RegexOptions.Compiled), "$1"),          // "( word" → "(word"
            (new Regex(@"(?<=[a-zA-Z]),(?=[a-zA-Z])", RegexOptions.Compiled), ", "),    // "one,two" → "one, two" (1,000 untouched)
            (new Regex(@"(?<=\S) {2,}(?=\S)", RegexOptions.Compiled), " "),             // doubled spaces inside a line
            (new Regex(@"\.\s?\.\s?\.(?!\.)", RegexOptions.Compiled), "…"),             // ". . ." → "…"
        };

        public static string FixPunctuation(string text)
        {
            foreach (var (pattern, replacement) in PunctuationRules) text = pattern.Replace(text, replacement);
            return text;
        }

        public static string StraightenQuotes(string text)
        {
            foreach (var c in new[] { "“", "”", "„", "‟", "″" }) text = text.Replace(c, "\"");
            foreach (var c in new[] { "‘", "’", "‚", "‛", "′" }) text = text.Replace(c, "'");
            return text;
        }

        /// <summary>Code characters OCR sometimes returns in "pretty" or full-width form.</summary>
        public static string CodeClean(string s)
        {
            var sb = new StringBuilder(StraightenQuotes(s));
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                // Full-width ASCII (！ through ～) → normal ASCII; ideographic space → space.
                if (c >= '！' && c <= '～') sb[i] = (char)(c - 0xFEE0);
                else if (c == '　') sb[i] = ' ';
            }
            string t = sb.ToString();
            var swaps = new[]
            {
                ("…", "..."), ("–", "-"), ("—", "--"), ("×", "x"), ("÷", "/"),
                ("≠", "!="), ("≤", "<="), ("≥", ">="), ("→", "->"), ("⇒", "=>"),
            };
            foreach (var (a, b) in swaps) t = t.Replace(a, b);
            return t;
        }

        // MARK: - Quick-action transforms

        public static string OneLine(string text) =>
            string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        private static readonly Regex NumberPattern = new Regex(@"[-+]?\$?\d[\d,]*(?:\.\d+)?%?", RegexOptions.Compiled);

        public static List<string> Numbers(string text) =>
            NumberPattern.Matches(text).Cast<Match>().Select(m => m.Value).ToList();

        /// <summary>
        /// A table drawn with text, for places that only take plain text (Teams, Slack…): padded
        /// columns with "|" between them. It's also a valid Markdown table, so Notion, Obsidian,
        /// GitHub and other Markdown apps show it as a real table.
        /// </summary>
        public static string TextTable(List<List<string>> rows)
        {
            if (rows.Count == 0 || rows[0].Count == 0) return "";
            var header = rows[0];
            var widths = Enumerable.Range(0, header.Count)
                .Select(c => Math.Max(3, rows.Max(r => c < r.Count ? r[c].Length : 0)))
                .ToList();
            string Line(List<string> cells) =>
                "| " + string.Join(" | ", widths.Select((w, c) => (c < cells.Count ? cells[c] : "").Replace("|", "/").PadRight(w))) + " |";
            string rule = "| " + string.Join(" | ", widths.Select(w => new string('-', w))) + " |";
            return string.Join("\n", new[] { Line(header), rule }.Concat(rows.Skip(1).Select(Line)));
        }

        /// <summary>The same table as HTML, which Word, Excel, Outlook, OneNote, Google Docs and Sheets paste as a real table.</summary>
        public static string HtmlTable(List<List<string>> rows)
        {
            string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            const string cellStyle = "border:1px solid #bbb;padding:4px 8px;text-align:left";
            var html = new StringBuilder("<meta charset=\"utf-8\"><table style=\"border-collapse:collapse\">");
            for (int r = 0; r < rows.Count; r++)
            {
                string tag = r == 0 ? "th" : "td";
                html.Append("<tr>");
                foreach (var cell in rows[r]) html.Append($"<{tag} style=\"{cellStyle}\">{Esc(cell)}</{tag}>");
                html.Append("</tr>");
            }
            return html.Append("</table>").ToString();
        }

        /// <summary>"Not a table": each row as an ordinary line of text.</summary>
        public static string PlainRows(List<List<string>> rows) =>
            string.Join("\n", rows.Select(r => string.Join(" ", r.Where(c => c.Length > 0))));

        public static string MarkdownTable(List<List<string>> rows)
        {
            if (rows.Count == 0) return "";
            string Line(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|"))) + " |";
            return string.Join("\n", new[] { Line(rows[0]), Line(rows[0].Select(_ => "---")) }.Concat(rows.Skip(1).Select(Line)));
        }

        private static string Repeat(string s, int count) => count <= 0 ? "" : string.Concat(Enumerable.Repeat(s, count));
    }
}
