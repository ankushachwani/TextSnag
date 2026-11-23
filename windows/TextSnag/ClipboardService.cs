using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TextSnag
{
    /// <summary>
    /// Owns TextSnag's clipboard writes. It can put text on the clipboard with "delayed rendering":
    /// Windows asks TextSnag for the text at the moment another app pastes it. That's how TextSnag
    /// knows you pasted (to close the bubble), with no extra permission or keyboard hook.
    /// </summary>
    internal sealed class ClipboardService : NativeWindow
    {
        private static ClipboardService shared;
        /// <summary>Create on the UI thread.</summary>
        public static ClipboardService Shared => shared ?? (shared = new ClipboardService());

        private readonly uint htmlFormat;
        private string pendingText;
        private byte[] pendingHtml;
        private Action onPaste;
        private DateTime copiedAt;

        public ClipboardService()
        {
            htmlFormat = Native.RegisterClipboardFormat("HTML Format");
            CreateHandle(new CreateParams());
        }

        /// <summary>True while the clipboard still holds what TextSnag last put there.</summary>
        public bool OwnsClipboard => Handle != IntPtr.Zero && Native.GetClipboardOwner() == Handle;

        /// <summary>Text that has been copied but not yet handed to any app.</summary>
        public bool HasPending => pendingText != null;

        /// <summary>Puts text (and optionally an HTML version) on the clipboard right away.</summary>
        public bool Copy(string text, string html = null)
        {
            if (!Open()) return false;
            try
            {
                Native.EmptyClipboard(); // also clears any pending delayed text (WM_DESTROYCLIPBOARD)
                SetData(Native.CF_UNICODETEXT, UnicodeBytes(text));
                if (html != null) SetData(htmlFormat, ClipboardHtml.Build(html));
                return true;
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        /// <summary>
        /// Promises the text to the clipboard without handing it over yet. When an app pastes,
        /// Windows asks for it and <paramref name="pasted"/> runs (unless that happens within the
        /// first moment: clipboard history and clipboard managers read new clips right away).
        /// </summary>
        public bool CopyWatchingForPaste(string text, string html, Action pasted)
        {
            if (!Open()) return false;
            try
            {
                Native.EmptyClipboard();
                pendingText = text;
                pendingHtml = html == null ? null : ClipboardHtml.Build(html);
                onPaste = pasted;
                copiedAt = DateTime.UtcNow;
                Native.SetClipboardData(Native.CF_UNICODETEXT, IntPtr.Zero);
                if (pendingHtml != null) Native.SetClipboardData(htmlFormat, IntPtr.Zero);
                return true;
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        /// <summary>Text that hasn't been pasted yet would vanish with the app: hand it over now.</summary>
        public void FlushPending()
        {
            if (pendingText == null || !OwnsClipboard || !Open()) return;
            try
            {
                if (Native.GetClipboardOwner() == Handle) RenderAll();
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_RENDERFORMAT:
                    // The clipboard is already open on behalf of the app that's pasting.
                    Render((uint)m.WParam.ToInt64());
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_RENDERALLFORMATS:
                    FlushPending();
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_DESTROYCLIPBOARD:
                    // Someone else (or a new copy of ours) replaced the clipboard.
                    pendingText = null;
                    pendingHtml = null;
                    onPaste = null;
                    break;
            }
            base.WndProc(ref m);
        }

        private void RenderAll()
        {
            Render(Native.CF_UNICODETEXT);
            if (pendingHtml != null) Render(htmlFormat);
        }

        private void Render(uint format)
        {
            if (pendingText == null) return;
            byte[] data = format == htmlFormat ? pendingHtml : format == Native.CF_UNICODETEXT ? UnicodeBytes(pendingText) : null;
            if (data == null) return;
            SetData(format, data);
            if (onPaste != null && (DateTime.UtcNow - copiedAt).TotalSeconds > 0.8)
            {
                var callback = onPaste;
                onPaste = null;
                var context = SynchronizationContext.Current;
                if (context != null) context.Post(_ => callback(), null);
                else callback();
            }
        }

        private bool Open()
        {
            // Another app may have the clipboard open for a moment.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (Native.OpenClipboard(Handle)) return true;
                Thread.Sleep(15);
            }
            Log.Write("couldn't open the clipboard");
            return false;
        }

        /// <summary>UTF-16 with Windows line endings and a terminating NUL.</summary>
        private static byte[] UnicodeBytes(string text)
        {
            string crlf = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return Encoding.Unicode.GetBytes(crlf + "\0");
        }

        private static void SetData(uint format, byte[] data)
        {
            IntPtr memory = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (memory == IntPtr.Zero) return;
            IntPtr target = Native.GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                Native.GlobalFree(memory);
                return;
            }
            Marshal.Copy(data, 0, target, data.Length);
            Native.GlobalUnlock(memory);
            // On success the clipboard owns the memory; otherwise it's still ours to free.
            if (Native.SetClipboardData(format, memory) == IntPtr.Zero) Native.GlobalFree(memory);
        }
    }
}
