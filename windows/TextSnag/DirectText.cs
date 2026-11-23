using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace TextSnag
{
    /// <summary>
    /// Direct Text: when the selected area is part of a real app window (a dialog, a settings page,
    /// a web page that blocks selection), read the app's own text through Windows UI Automation
    /// instead of reading pixels. It's exact, with no recognition mistakes.
    ///
    /// It's only used when it agrees with what OCR saw in the same area, because apps can expose
    /// hidden or off-screen text that isn't in your selection. Unlike macOS, Windows needs no
    /// permission for this; apps running as administrator can't be read unless TextSnag is too.
    /// </summary>
    internal static class DirectText
    {
        private static readonly int OwnProcessId = Process.GetCurrentProcess().Id;

        /// <summary>Text inside <paramref name="area"/> (physical screen pixels), or null. Call from a background thread.</summary>
        public static string TextIn(System.Drawing.Rectangle area, double budgetSeconds = 0.25)
        {
            if (area.Width <= 6 || area.Height <= 6) return null;
            var deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
            var rect = new Rect(area.X, area.Y, area.Width, area.Height);

            // Hit-test a grid of points across the selection and collect the distinct elements.
            var elements = new List<AutomationElement>();
            var seen = new HashSet<string>();
            int rows = Math.Max(2, Math.Min(24, area.Height / 10));
            int cols = area.Width > 240 ? 4 : 2;
            for (int r = 0; r < rows && DateTime.UtcNow < deadline; r++)
            {
                for (int c = 0; c < cols && DateTime.UtcNow < deadline; c++)
                {
                    var p = new Point(rect.X + rect.Width * (c + 0.5) / cols, rect.Y + rect.Height * (r + 0.5) / rows);
                    try
                    {
                        var element = AutomationElement.FromPoint(p);
                        if (element == null || element.Current.ProcessId == OwnProcessId) continue;
                        if (seen.Add(string.Join(".", element.GetRuntimeId()))) elements.Add(element);
                    }
                    catch (Exception) { } // the element went away, or the app isn't responding
                }
            }

            // Read each element: text controls by character range, labels by value/name.
            var pieces = new List<(Rect frame, string text)>();
            var seenText = new HashSet<string>();
            foreach (var element in elements)
            {
                if (DateTime.UtcNow > deadline) break;
                try
                {
                    var frame = element.Current.BoundingRectangle;
                    if (frame.IsEmpty || frame.Width <= 0 || frame.Height <= 0) continue;
                    string s = TextInRange(element, frame, rect) ?? LabelText(element, frame, rect);
                    if (s == null) continue;
                    string t = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                    if (t.Length > 0 && seenText.Add(t)) pieces.Add((frame, t));
                }
                catch (Exception) { }
            }
            if (pieces.Count == 0) return null;

            // Reading order: top to bottom, then left to right within a row. (Insertion sort: the
            // "same row" test isn't transitive, which List.Sort doesn't allow.)
            for (int i = 1; i < pieces.Count; i++)
            {
                var item = pieces[i];
                int j = i - 1;
                while (j >= 0 && ReadsAfter(pieces[j].frame, item.frame)) { pieces[j + 1] = pieces[j]; j--; }
                pieces[j + 1] = item;
            }
            return string.Join("\n", pieces.Select(p => p.text));
        }

        private static bool ReadsAfter(Rect a, Rect b) =>
            Math.Abs(a.Top - b.Top) > Math.Min(a.Height, b.Height) * 0.5 ? a.Top > b.Top : a.Left > b.Left;

        /// <summary>
        /// True when the app's text and the OCR text describe the same words, allowing for some OCR
        /// mistakes. Guards against hidden, off-screen, or partially selected text.
        /// </summary>
        public static bool Agrees(string direct, string ocr) =>
            // Letter-pair similarity rather than whole words: one misread letter ("nurnber") barely
            // matters, but extra hidden text clearly does.
            TextFormatter.Similarity(direct, ocr) >= 0.8;

        /// <summary>Text controls, documents and web pages: the characters between the first and last positions in the selection.</summary>
        private static string TextInRange(AutomationElement element, Rect frame, Rect selection)
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return null;
            var text = (TextPattern)pattern;
            var inside = Rect.Intersect(frame, selection);
            if (inside.IsEmpty || inside.Width < 4 || inside.Height < 4) return null;

            const double inset = 2;
            var start = text.RangeFromPoint(new Point(inside.Left + inset, inside.Top + inset));
            var end = text.RangeFromPoint(new Point(inside.Right - inset, inside.Bottom - inset));
            end.ExpandToEnclosingUnit(TextUnit.Character);
            if (start.CompareEndpoints(TextPatternRangeEndpoint.Start, end, TextPatternRangeEndpoint.Start) > 0) return null;
            var span = start.Clone();
            span.MoveEndpointByRange(TextPatternRangeEndpoint.End, end, TextPatternRangeEndpoint.End);
            string s = span.GetText(200_000);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>Labels, buttons, cells, static web text: their value or name, if they sit inside the selection.</summary>
        private static string LabelText(AutomationElement element, Rect frame, Rect selection)
        {
            var inside = Rect.Intersect(frame, selection);
            if (inside.IsEmpty || inside.Width * inside.Height / (frame.Width * frame.Height) <= 0.5) return null;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
            {
                string value = ((ValuePattern)pattern).Current.Value;
                if (!string.IsNullOrEmpty(value)) return value;
            }
            string name = element.Current.Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }
}
