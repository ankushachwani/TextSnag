using System.Text;

namespace TextSnag
{
    /// <summary>
    /// Windows' "HTML Format" clipboard flavor: UTF-8 HTML behind a small header that gives byte
    /// offsets to the document and the fragment. Word, Excel, Outlook, OneNote and browsers
    /// (Google Docs, Sheets) read tables from it.
    /// </summary>
    public static class ClipboardHtml
    {
        private const string Header =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        private const string Before = "<html><body>\r\n<!--StartFragment-->";
        private const string After = "<!--EndFragment-->\r\n</body>\r\n</html>";

        /// <summary>The bytes to put on the clipboard, including the terminating NUL.</summary>
        public static byte[] Build(string fragment)
        {
            // The charset is given by the format itself, so drop any <meta charset> in the fragment.
            fragment = fragment.Replace("<meta charset=\"utf-8\">", "");
            var utf8 = new UTF8Encoding(false);
            int headerLength = utf8.GetByteCount(string.Format(Header, 0, 0, 0, 0));
            int startHtml = headerLength;
            int startFragment = startHtml + utf8.GetByteCount(Before);
            int endFragment = startFragment + utf8.GetByteCount(fragment);
            int endHtml = endFragment + utf8.GetByteCount(After);
            string document = string.Format(Header, startHtml, endHtml, startFragment, endFragment) + Before + fragment + After;
            return utf8.GetBytes(document + "\0");
        }
    }
}
