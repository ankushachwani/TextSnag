// Tests for the shared formatting core. No test framework needed: `dotnet run --project windows/Tests`.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TextSnag;

internal static class Program
{
    private static int failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  — " + detail)}");
        if (!ok) failures++;
    }

    private static void Equal(string name, string expected, string actual) =>
        Check(name, expected == actual, $"expected {Show(expected)}, got {Show(actual)}");

    private static string Show(string s) => s == null ? "null" : "\"" + s.Replace("\n", "⏎").Replace("\t", "⇥") + "\"";

    private const double CharWidth = 0.012, LineHeight = 0.05;

    /// <summary>A recognized line, placed by its left edge and top (normalized, top-down like a screen).</summary>
    private static TextLine Line(string text, double left, double top, double height = LineHeight) =>
        new TextLine(text, new Box(left, 1 - top - height, text.Length * CharWidth, height));

    /// <summary>Lines stacked from the top, one per row, indented by whole characters.</summary>
    private static List<TextLine> Stack(IList<string> texts, IList<int> indents = null, double spacing = 0.07)
    {
        var lines = new List<TextLine>();
        for (int i = 0; i < texts.Count; i++)
        {
            int indent = indents != null && i < indents.Count ? indents[i] : 0;
            lines.Add(Line(texts[i], 0.05 + indent * CharWidth, 0.05 + i * spacing));
        }
        return lines;
    }

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var defaults = new FormatOptions();
        var noBreaks = new FormatOptions { KeepLineBreaks = false };

        Console.WriteLine("— Text");
        var prose = Stack(new[] { "The quick brown fox jumps", "over the lazy dog." });
        prose.Add(Line("Second paragraph here.", 0.05, 0.05 + 2 * 0.07 + 0.08));
        Equal("keeps line breaks", "The quick brown fox jumps\nover the lazy dog.\nSecond paragraph here.",
              TextFormatter.Format(prose, ContentKind.Prose, defaults));
        Equal("merges lines into paragraphs", "The quick brown fox jumps over the lazy dog.\n\nSecond paragraph here.",
              TextFormatter.Format(prose, ContentKind.Prose, noBreaks));
        Equal("re-joins hyphenated words", "an example text",
              TextFormatter.Format(Stack(new[] { "an exam-", "ple text" }), ContentKind.Prose, noBreaks));
        Check("ordinary text is not code", !TextFormatter.LooksLikeCode(prose));
        Check("ordinary text is not a table", TextFormatter.DetectTable(prose) == null);

        Console.WriteLine("\n— Lists");
        var list = Stack(new[] { "• Fruit", "• Apples and pears", "• Bananas", "• Vegetables", "• Carrots", "• Organic only",
                                 "1. First step", "2. Second step" },
                         new[] { 0, 4, 4, 0, 4, 8, 0, 0 });
        Equal("bullets, styles and nesting kept",
              "• Fruit\n    ◦ Apples and pears\n    ◦ Bananas\n• Vegetables\n    ◦ Carrots\n        ▪ Organic only\n1. First step\n2. Second step",
              TextFormatter.Format(list, ContentKind.Prose, defaults));
        Equal("nesting dropped when indentation is off",
              "• Fruit\n◦ Apples and pears",
              TextFormatter.Format(list.Take(2).ToList(), ContentKind.Prose, new FormatOptions { KeepIndentation = false }));
        var wrapped = new List<TextLine> { Line("• A list item that wraps", 0.05, 0.05), Line("onto a second line", 0.05 + 2 * CharWidth, 0.12) };
        Equal("wrapped list item lines up under its text", "• A list item that wraps\n  onto a second line",
              TextFormatter.Format(wrapped, ContentKind.Prose, defaults));

        Console.WriteLine("\n— Code");
        var code = Stack(new[] { "func greet(name: String) {", "if name.isEmpty {", "return", "}", "print(\"Hi, \\(name)!\")", "}" },
                         new[] { 0, 4, 8, 4, 4, 0 });
        Check("detected as code", TextFormatter.LooksLikeCode(code));
        Equal("indentation kept",
              "func greet(name: String) {\n    if name.isEmpty {\n        return\n    }\n    print(\"Hi, \\(name)!\")\n}",
              TextFormatter.Format(code, ContentKind.Code, defaults));
        Check("a shell command is code", TextFormatter.LooksLikeCode(Stack(new[] { "$ npm install --save-dev typescript" })));

        Console.WriteLine("\n— Tables");
        var cells = new List<TextLine>();
        string[][] rows =
        {
            new[] { "Name", "Qty", "Price" }, new[] { "Apples", "3", "$1.20" }, new[] { "Bananas", "12", "$0.50" },
            new[] { "Cherries", "1", "$4.00" },
        };
        double[] columnX = { 0.05, 0.42, 0.7 };
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < 3; c++)
                cells.Add(Line(rows[r][c], columnX[c], 0.1 + r * 0.1));
        var layout = TextFormatter.DetectTable(cells);
        Check("detected as table", layout != null);
        if (layout != null)
        {
            Check("rows and columns", layout.Rows.Count == 4 && layout.Rows.All(r => r.Count == 3)
                  && layout.Rows[2][0] == "Bananas" && layout.Rows[3][1] == "1");
            string text = TextFormatter.Format(cells, ContentKind.Table(layout.Rows), defaults);
            Check("plain text drawn as a table", text.StartsWith("| Name     | Qty | Price |\n| -------- | --- | ----- |\n| Apples   | 3   | $1.20 |"), Show(text));
            Check("HTML table for Word, Excel, Docs…",
                  TextFormatter.HtmlTable(layout.Rows).Contains("<th style=\"border:1px solid #bbb;padding:4px 8px;text-align:left\">Name</th>"));
            Equal("Not a Table action", "Name Qty Price\nApples 3 $1.20\nBananas 12 $0.50\nCherries 1 $4.00", TextFormatter.PlainRows(layout.Rows));
            Equal("Markdown table", "| Name | Qty | Price |\n| --- | --- | --- |", string.Join("\n", TextFormatter.MarkdownTable(layout.Rows).Split('\n').Take(2)));
        }
        var empty = cells.Where(l => l.Text != "3").ToList();
        Check("a missing cell stays empty", TextFormatter.DetectTable(empty)?.Rows[1][1] == "");
        var article = new List<TextLine>();
        for (int r = 0; r < 4; r++)
        {
            article.Add(Line("A long sentence in the first column of an article here", 0.02, 0.1 + r * 0.07));
            article.Add(Line("and another long sentence in the second column too", 0.52, 0.1 + r * 0.07));
        }
        Check("two-column article is not a table", TextFormatter.DetectTable(article) == null);

        Console.WriteLine("\n— Direct Text");
        Equal("keeps bullets and indentation from OCR", "• Fruit\n    ◦ Apples and pears\n    ◦ Bananas",
              TextFormatter.FormatDirect("•\nFruit\n•\nApples and pears\n•\nBananas", "• Fruit\n    ◦ Apples and pears\n    ◦ Bananas",
                                         ContentKind.Prose, defaults));
        Equal("Windows line endings", "One\nTwo", TextFormatter.FormatDirect("One\r\nTwo", "One\nTwo", ContentKind.Prose, defaults));
        Equal("joins wrapped lines when line breaks are off", "One two three\n• Item",
              TextFormatter.FormatDirect("One two\nthree\n• Item", "", ContentKind.Prose, noBreaks));
        Check("accepts app text that matches the screen",
              TextFormatter.Similarity("Are you sure you want to delete “Report.pdf”?", "Are you sure you want to delete \"Report.pdf\"?") >= 0.8);
        Check("tolerates OCR mistakes", TextFormatter.Similarity("Serial number: C02XK1ZTJGH5", "Serial nurnber: C02XK1ZTJGH5") >= 0.8);
        Check("rejects hidden extra text",
              TextFormatter.Similarity("Visible line\nHidden tooltip text with many more words here", "Visible line") < 0.8);

        Console.WriteLine("\n— Formatting rules");
        Check("tidy bullets", new[] { "● one", "▪ two", "- three", "* four", "•five", "◦ six" }.Select(TextFormatter.TidyBullet)
              .SequenceEqual(new[] { "• one", "▪ two", "• three", "• four", "• five", "◦ six" }));
        Check("hyphens and numbers are not bullets", TextFormatter.TidyBullet("-5 degrees") == "-5 degrees" && TextFormatter.TidyBullet("*args") == "*args");
        Equal("fix punctuation spacing", "Hello, world (yes) one, two 1,000 done…",
              TextFormatter.FixPunctuation("Hello , world ( yes ) one,two 1,000  done . . ."));
        Equal("look-alike letters", "print(\"Hi\") Hello", TextFormatter.FixLookAlikes("print(\"Hі\") Неllo"));
        Equal("real Cyrillic left alone", "Привет мир", TextFormatter.FixLookAlikes("Привет мир"));
        Equal("straight quotes", "\"Hi\" it's", TextFormatter.StraightenQuotes("“Hi” it’s"));
        Equal("code cleanup", "x=1 -> \"a\" -- b", TextFormatter.CodeClean("ｘ＝１ → “a” — b"));

        Console.WriteLine("\n— Quick actions");
        Equal("one line", "a b c d", TextFormatter.OneLine("a\nb  c\n\nd"));
        Check("numbers only", TextFormatter.Numbers("Total: $1,200.50 for 3 items (15%)").SequenceEqual(new[] { "$1,200.50", "3", "15%" }));
        Equal("link", "https://example.com/a", DataDetector.FirstLink("Visit https://example.com/a."));
        Equal("www link", "http://www.example.org", DataDetector.FirstLink("go to www.example.org today"));
        Equal("bare domain", "http://example.com/docs", DataDetector.FirstLink("see example.com/docs for more"));
        Equal("email", "mailto:jane.doe@example.com", DataDetector.FirstLink("write to jane.doe@example.com, thanks"));
        Equal("no link in file names", null, DataDetector.FirstLink("open report.pdf and ASP.NET notes"));
        Equal("phone", "+15551234567", DataDetector.FirstPhone("Call +1 (555) 123-4567 today"));
        Equal("US phone", "5551234567", DataDetector.FirstPhone("Phone: 555-123-4567"));
        Equal("dates are not phones", null, DataDetector.FirstPhone("Due 2024-11-23"));
        Equal("addresses are not phones", null, DataDetector.FirstPhone("Server 192.168.10.20"));
        Equal("prices are not phones", null, DataDetector.FirstPhone("Total: $1,200.50"));

        Console.WriteLine("\n— Clipboard");
        var html = Encoding.UTF8.GetString(ClipboardHtml.Build("<table><tr><td>é</td></tr></table>"));
        int Offset(string name)
        {
            int at = html.IndexOf(name + ":", StringComparison.Ordinal) + name.Length + 1;
            return int.Parse(html.Substring(at, 10));
        }
        var bytes = Encoding.UTF8.GetBytes(html);
        string fragment = Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment"));
        Equal("HTML clipboard offsets point at the fragment", "<table><tr><td>é</td></tr></table>", fragment);
        Check("HTML clipboard document bounds", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 6) == "<html>"
              && Encoding.UTF8.GetString(bytes, Offset("EndHTML") - 7, 7) == "</html>");

        Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
