using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TextSnag
{
    internal static class AppIcons
    {
        private static Stream IconStream() => Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.ico");

        /// <summary>The app icon for window title bars.</summary>
        public static Icon Window
        {
            get
            {
                using (var stream = IconStream()) return stream == null ? null : new Icon(stream);
            }
        }

        /// <summary>The app icon at a given pixel size (for the About tab).</summary>
        public static Bitmap Image(int size)
        {
            using (var stream = IconStream())
            {
                if (stream == null) return null;
                using (var icon = new Icon(stream, new Size(size, size))) return icon.ToBitmap();
            }
        }

        /// <summary>
        /// The "Lift" logo for the notification area: a page with one line lifted out, drawn in
        /// white for a dark taskbar or black for a light one (the Mac menu bar icon, same geometry).
        /// </summary>
        public static Icon Tray()
        {
            int size = SystemInformation.SmallIconSize.Width;
            var color = TaskbarIsLight() ? Color.Black : Color.White;
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var faint = new SolidBrush(Color.FromArgb(128, color)))
                using (var solid = new SolidBrush(color))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.ScaleTransform(size / 18f, size / 18f);
                    using (var top = Rounded(3, 2.6f, 10, 1.6f, 0.8f)) g.FillPath(faint, top);
                    using (var bottom = Rounded(3, 13.4f, 8, 1.6f, 0.8f)) g.FillPath(faint, bottom);
                    using (var lifted = Rounded(1.8f, 6.6f, 14.4f, 4.2f, 1.2f)) g.FillPath(solid, lifted);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        private static bool TaskbarIsLight()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Set TEXTSNAG_DEBUG=1 to log timings and recognition fallbacks to %TEMP%\TextSnag.log.</summary>
    internal static class Log
    {
        private static readonly bool Enabled = Environment.GetEnvironmentVariable("TEXTSNAG_DEBUG") != null;
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "TextSnag.log"),
                                       DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
                catch (IOException) { }
            }
        }
    }
}
