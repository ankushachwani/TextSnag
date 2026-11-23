using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TextSnag
{
    /// <summary>
    /// The selection UI: the screen freezes (so a playing video holds still), dims, and you drag a
    /// rectangle over the text. Esc or a right-click cancels. The grab stays in memory; nothing is
    /// written to disk and the clipboard is only touched with the final text.
    /// </summary>
    internal static class ScreenCapture
    {
        /// <summary>
        /// Grabs a rectangle of the screen in physical pixels (virtual-screen coordinates), or null
        /// if the screen can't be read (e.g. the secure desktop is showing).
        /// </summary>
        public static Bitmap GrabBitmap(Rectangle area)
        {
            if (area.Width <= 0 || area.Height <= 0) return null;
            // 32bppRgb: GDI leaves the alpha byte at 0, which this format ignores.
            var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(bitmap))
            {
                IntPtr target = g.GetHdc();
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                try
                {
                    // CAPTUREBLT includes layered windows (tooltips, menus, some video players).
                    ok = Native.BitBlt(target, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT);
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, screen);
                    g.ReleaseHdc(target);
                }
            }
            if (ok) return bitmap;
            bitmap.Dispose();
            return null;
        }

        public static RawImage Grab(Rectangle area)
        {
            using (var bitmap = GrabBitmap(area)) return bitmap == null ? null : RawImage.FromBitmap(bitmap);
        }

        /// <summary>
        /// Shows the selection overlay on every monitor. Calls back on the UI thread with the image
        /// and its screen rectangle, or (null, empty) if the user cancelled.
        /// </summary>
        public static void Select(Action<RawImage, Rectangle> completion)
        {
            var desktop = SystemInformation.VirtualScreen;
            var shot = GrabBitmap(desktop);
            if (shot == null)
            {
                completion(null, Rectangle.Empty);
                return;
            }

            var overlays = new List<SelectionOverlay>();
            bool finished = false;
            void Finish(Rectangle? selection)
            {
                if (finished) return;
                finished = true;
                foreach (var o in overlays) o.Hide();
                RawImage image = null;
                if (selection.HasValue)
                {
                    var s = selection.Value;
                    image = RawImage.FromBitmap(shot, new Rectangle(s.X - desktop.X, s.Y - desktop.Y, s.Width, s.Height));
                }
                // This runs inside an overlay's mouse or key handler: close them once it has returned.
                overlays[0].BeginInvoke(new Action(() =>
                {
                    foreach (var o in overlays) { o.Close(); o.Dispose(); }
                    shot.Dispose();
                    completion(image, selection ?? Rectangle.Empty);
                }));
            }

            foreach (var screen in Screen.AllScreens)
            {
                overlays.Add(new SelectionOverlay(shot, desktop.Location, screen.Bounds, Finish, () => overlays));
            }
            foreach (var o in overlays) o.Show();
            var under = overlays.Find(o => o.Bounds.Contains(Cursor.Position)) ?? overlays[0];
            Native.SetForegroundWindow(under.Handle);
            under.Activate();
        }
    }

    internal sealed class SelectionOverlay : Form
    {
        private readonly Bitmap shot;        // the whole frozen desktop, shared by all overlays
        private readonly Bitmap dimmed;      // this monitor's part, darkened
        private readonly Point offset;       // this monitor's position inside `shot`
        private readonly Action<Rectangle?> finish;
        private readonly Func<List<SelectionOverlay>> siblings;
        private Point? start;
        private Rectangle selection;

        public SelectionOverlay(Bitmap shot, Point desktopOrigin, Rectangle bounds, Action<Rectangle?> finish,
                                Func<List<SelectionOverlay>> siblings)
        {
            this.shot = shot;
            this.finish = finish;
            this.siblings = siblings;
            offset = new Point(bounds.X - desktopOrigin.X, bounds.Y - desktopOrigin.Y);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            Text = "TextSnag";
            Bounds = bounds;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            dimmed = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dimmed))
            using (var shade = new SolidBrush(Color.FromArgb(96, 0, 0, 0)))
            {
                g.DrawImage(shot, new Rectangle(0, 0, bounds.Width, bounds.Height),
                            new Rectangle(offset, bounds.Size), GraphicsUnit.Pixel);
                g.FillRectangle(shade, 0, 0, bounds.Width, bounds.Height);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);
            if (selection.Width > 0 && selection.Height > 0)
            {
                var source = new Rectangle(selection.X + offset.X, selection.Y + offset.Y, selection.Width, selection.Height);
                g.DrawImage(shot, selection, source, GraphicsUnit.Pixel);
                using (var outer = new Pen(Color.FromArgb(140, 0, 0, 0)))
                using (var inner = new Pen(Color.White))
                {
                    var r = selection;
                    g.DrawRectangle(outer, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
                    g.DrawRectangle(inner, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { finish(null); return; }
            if (e.Button != MouseButtons.Left) return;
            start = e.Location;
            Capture = true;
            UpdateSelection(new Rectangle(e.Location, Size.Empty));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!start.HasValue) return;
            var p = new Point(Math.Min(Math.Max(e.X, 0), ClientSize.Width), Math.Min(Math.Max(e.Y, 0), ClientSize.Height));
            var s = start.Value;
            UpdateSelection(Rectangle.FromLTRB(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y), Math.Max(s.X, p.X), Math.Max(s.Y, p.Y)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !start.HasValue) return;
            start = null;
            Capture = false;
            if (selection.Width > 4 && selection.Height > 4)
            {
                var r = selection;
                r.Offset(Bounds.Location); // to screen coordinates
                finish(r);
            }
            else
            {
                UpdateSelection(Rectangle.Empty); // a click without a drag: keep waiting
            }
        }

        private void UpdateSelection(Rectangle next)
        {
            var dirty = Rectangle.Union(selection, next);
            dirty.Inflate(3, 3);
            selection = next;
            Invalidate(dirty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { finish(null); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Switching to another app (Alt+Tab, Win key) cancels; moving to another monitor's overlay doesn't.
            var timer = new Timer { Interval = 150 };
            timer.Tick += (s, a) =>
            {
                timer.Dispose();
                var active = Form.ActiveForm as SelectionOverlay;
                if (active == null || !siblings().Contains(active)) finish(null);
            };
            timer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            // Each overlay covers exactly one monitor in physical pixels; don't let a DPI change resize it.
            if (m.Msg == Native.WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) dimmed.Dispose();
            base.Dispose(disposing);
        }
    }
}
