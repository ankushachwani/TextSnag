// Finds the first link, email address and phone number in captured text, for the "Open Link",
// "Email" and "Call" quick actions. (macOS uses NSDataDetector; Windows has no equivalent, so
// this is a small set of conservative patterns.)
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TextSnag
{
    public static class DataDetector
    {
        private static readonly Regex Email = new Regex(
            @"(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled);

        private const string Tlds =
            "com|org|net|io|dev|app|co|ai|edu|gov|me|info|biz|us|uk|de|fr|es|it|nl|ca|au|in|jp|ly|gg|tv|xyz|so|sh|page|site|blog|news|cloud|tech";

        private static readonly Regex Url = new Regex(
            @"\b(?:https?://|www\.)[^\s<>""]+" +
            // Bare domains ("example.com/docs") only in lower case, so "ASP.NET" isn't a link.
            @"|(?-i:(?<![@\w.-])(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:" + Tlds + @")(?![\w@-])(?:/[^\s<>""]*)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Phone = new Regex(
            @"(?<![\w+])(?:\+\d{1,3}[\s.-]?)?(?:\(\d{1,4}\)[\s.-]?)?\d{2,4}(?:[\s.-]\d{2,4}){1,4}(?![\w])",
            RegexOptions.Compiled);

        /// <summary>
        /// The first web link or email address, as a URL ("https://…" or "mailto:…"), or null.
        /// Links without a scheme get "http://", the way macOS's detector reports them.
        /// </summary>
        public static string FirstLink(string text)
        {
            var url = Url.Match(text);
            var email = Email.Match(text);
            if (email.Success && (!url.Success || email.Index <= url.Index || Overlaps(url, email)))
            {
                return "mailto:" + email.Value;
            }
            if (!url.Success) return null;
            string link = TrimTrailing(url.Value);
            if (!link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                link = "http://" + link;
            }
            return link;
        }

        /// <summary>The first phone number, as digits (with a leading "+" if it had one), or null.</summary>
        public static string FirstPhone(string text)
        {
            foreach (Match m in Phone.Matches(text))
            {
                string s = m.Value.Trim();
                string digits = new string(s.Where(char.IsDigit).ToArray());
                if (digits.Length < 7 || digits.Length > 15) continue;
                bool marked = s.StartsWith("+", StringComparison.Ordinal) || s.Contains("(");
                if (!marked && !s.Any(c => c == ' ' || c == '-' || c == '.')) continue;
                if (LooksLikeDate(s) || LooksLikeVersionOrAddress(s)) continue;
                return (s.StartsWith("+", StringComparison.Ordinal) ? "+" : "") + digits;
            }
            return null;
        }

        private static bool Overlaps(Match a, Match b) => a.Index < b.Index + b.Length && b.Index < a.Index + a.Length;

        private static string TrimTrailing(string link)
        {
            var sb = new StringBuilder(link);
            while (sb.Length > 0)
            {
                char c = sb[sb.Length - 1];
                if (".,;:!?'\"".IndexOf(c) >= 0) { sb.Length--; continue; }
                if (c == ')' && Count(sb, '(') < Count(sb, ')')) { sb.Length--; continue; }
                if (c == ']' && Count(sb, '[') < Count(sb, ']')) { sb.Length--; continue; }
                break;
            }
            return sb.ToString();
        }

        private static int Count(StringBuilder sb, char c)
        {
            int n = 0;
            for (int i = 0; i < sb.Length; i++) if (sb[i] == c) n++;
            return n;
        }

        private static bool LooksLikeDate(string s) =>
            Regex.IsMatch(s, @"^\d{4}[-./]\d{1,2}[-./]\d{1,2}$") || Regex.IsMatch(s, @"^\d{1,2}[-./]\d{1,2}[-./]\d{2,4}$");

        /// <summary>"192.168.10.20", "10.4.22.1": dots only, unless it's the 555.123.4567 shape.</summary>
        private static bool LooksLikeVersionOrAddress(string s) =>
            s.IndexOf('.') >= 0 && s.All(c => char.IsDigit(c) || c == '.') && !Regex.IsMatch(s, @"^\d{3}\.\d{3}\.\d{4}$");
    }
}
