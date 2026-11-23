using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TextSnag
{
    /// <summary>A button in the bubble. <see cref="Run"/> returns the confirmation to show, or null to just close it.</summary>
    internal sealed class ToastAction
    {
        public readonly string Title;
        public readonly Func<string> Run;

        public ToastAction(string title, Func<string> run)
        {
            Title = title; Run = run;
        }
    }

    /// <summary>Icons from Windows' own icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on 10).</summary>
    internal static class Glyph
    {
        public const string Copy = "", Check = "", Code = "", Table = "", QRCode = "",
                            Exact = "", Search = "", Add = "", Delete = "", Info = "",
                            Clock = "", Warning = "";
    }

    /// <summary>
    /// The small bubble at the top of the screen ("Copied text"), optionally with quick-action
    /// buttons. Fades out on its own; stays while the pointer is over it.
    /// </summary>
    internal static class Toast
    {
        /// <summary>How long the bubble stays when it has quick actions (unless you paste first).</summary>
        public const int ActionsLifetimeMs = 3000;

        private static ToastWindow current;

        public static void Show(string message, string glyph = Glyph.Copy, IList<ToastAction> actions = null)
        {
            current?.CloseNow();
            current = new ToastWindow(message, glyph, actions ?? new ToastAction[0]);
            current.Present();
        }

        /// <summary>Called when the captured text is pasted somewhere: close the bubble.</summary>
        public static void Pasted()
        {
            if (current != null && current.HasActions) current.FadeOut();
        }

        /// <summary>Removes the bubble immediately (before a screen grab). True if one was showing.</summary>
        public static bool HideNow()
        {
            if (current == null) return false;
            bool wasShowing = !current.IsDisposed && current.Visible;
            current.CloseNow();
            current = null;
            return wasShowing;
        }

        internal static void Closed(ToastWindow window)
        {
            if (current == window) current = null;
        }
    }

    internal sealed class ToastWindow : Form
    {
        private readonly string message, glyph;
        private readonly IList<ToastAction> actions;
        private readonly Timer hideTimer = new Timer(), fadeTimer = new Timer { Interval = 16 }, watchTimer = new Timer { Interval = 500 };
        private readonly List<RectangleF> pills = new List<RectangleF>();
        private Bitmap frame;
        private DateTime deadline;
        private DateTime fadeStart;
        private int hover = -1;
        private bool fading;
        private float s = 1; // DPI scale
        private RectangleF bubble, iconRect, textRect;
        private float dividerX;

        public bool HasActions => actions.Count > 0;

        public ToastWindow(string message, string glyph, IList<ToastAction> actions)
        {
            this.message = message;
            this.glyph = glyph;
            this.actions = actions;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Text = "TextSnag";
            hideTimer.Tick += (sender, e) => { hideTimer.Stop(); FadeOut(); };
            fadeTimer.Tick += (sender, e) => FadeStep();
            watchTimer.Tick += (sender, e) =>
            {
                // Copying something else closes the bubble (its buttons would act on the old capture).
                if (!ClipboardService.Shared.OwnsClipboard) FadeOut();
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                if (actions == null || actions.Count == 0) cp.ExStyle |= Native.WS_EX_TRANSPARENT; // clicks pass through
                return cp;
            }
        }

        public void Present()
        {
            var cursor = Cursor.Position;
            s = Native.DpiAt(cursor) / 96f;
            var size = LayoutBubble();
            var area = Screen.FromPoint(cursor).WorkingArea;
            float shadow = Shadow;
            Bounds = new Rectangle((int)(area.Left + (area.Width - bubble.Width) / 2 - shadow),
                                   (int)(area.Top + 12 * s - shadow + 2 * s), size.Width, size.Height);
            frame = Draw();
            CreateHandle();
            Commit(255);
            Show();

            if (actions.Count == 0)
            {
                ScheduleHide(1400);
            }
            else
            {
                // Quick actions stay 3 s, or until you paste or copy something else, whichever comes
                // first. Hovering keeps them open.
                deadline = DateTime.UtcNow.AddMilliseconds(Toast.ActionsLifetimeMs);
                ScheduleHide(Toast.ActionsLifetimeMs);
                watchTimer.Start();
            }
        }

        public void FadeOut()
        {
            if (fading || IsDisposed) return;
            fading = true;
            hideTimer.Stop();
            watchTimer.Stop();
            fadeStart = DateTime.UtcNow;
            fadeTimer.Start();
        }

        public void CloseNow()
        {
            hideTimer.Stop();
            fadeTimer.Stop();
            watchTimer.Stop();
            if (!IsDisposed)
            {
                Close();
                Dispose();
            }
        }

        private void FadeStep()
        {
            double t = (DateTime.UtcNow - fadeStart).TotalMilliseconds / 250;
            if (t >= 1)
            {
                CloseNow();
                Toast.Closed(this);
                return;
            }
            Commit((byte)(255 * (1 - t)));
        }

        private void ScheduleHide(int ms)
        {
            hideTimer.Stop();
            hideTimer.Interval = Math.Max(1, ms);
            hideTimer.Start();
        }

        // MARK: Mouse

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE; // clicking a button must not steal focus from your app
                return;
            }
            if (m.Msg == Native.WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (actions.Count > 0 && !fading) hideTimer.Stop();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(-1);
            if (actions.Count > 0 && !fading)
            {
                ScheduleHide((int)Math.Max(1000, (deadline - DateTime.UtcNow).TotalMilliseconds));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (actions.Count > 0 && !fading) hideTimer.Stop();
            SetHover(pills.FindIndex(p => p.Contains(e.Location)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || fading) return;
            int index = pills.FindIndex(p => p.Contains(e.Location));
            if (index < 0) return;
            var action = actions[index];
            // Run it after this mouse message is done: it replaces (and disposes) this window.
            BeginInvoke(new Action(() =>
            {
                string confirmation = action.Run();
                if (confirmation != null) Toast.Show(confirmation, Glyph.Check);
                else FadeOut();
            }));
        }

        private void SetHover(int index)
        {
            if (index == hover || IsDisposed) return;
            hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            frame?.Dispose();
            frame = Draw();
            Commit(255);
        }

        // MARK: Drawing

        private float Shadow => 14 * s;

        private static bool LightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return !(key?.GetValue("AppsUseLightTheme") is int v) || v != 0;
                }
            }
            catch { return true; }
        }

        private static string iconFamily;
        private static string IconFamily
        {
            get
            {
                if (iconFamily != null) return iconFamily;
                using (var fonts = new InstalledFontCollection())
                {
                    var names = new HashSet<string>(fonts.Families.Select(f => f.Name));
                    iconFamily = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
                        : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "";
                }
                return iconFamily;
            }
        }

        private Font TextFont() => new Font("Segoe UI Semibold", 13 * s, GraphicsUnit.Pixel);
        private Font PillFont() => new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel);

        private Size LayoutBubble()
        {
            float height = 38 * s, pillHeight = 24 * s;
            float x = 14 * s;
            bool hasIcon = IconFamily.Length > 0;
            using (var bitmap = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bitmap))
            using (var textFont = TextFont())
            using (var pillFont = PillFont())
            {
                if (hasIcon)
                {
                    iconRect = new RectangleF(x, 0, 16 * s, height);
                    x += 16 * s + 8 * s;
                }
                float textWidth = Math.Min(g.MeasureString(message, textFont, 10000, StringFormat.GenericTypographic).Width + 2 * s, 360 * s);
                textRect = new RectangleF(x, 0, textWidth, height);
                x += textWidth;
                pills.Clear();
                if (actions.Count > 0)
                {
                    x += 12 * s;
                    dividerX = x;
                    x += 1 + 8 * s;
                    foreach (var action in actions)
                    {
                        float w = (float)Math.Ceiling(g.MeasureString(action.Title, pillFont, 1000, StringFormat.GenericTypographic).Width + 22 * s);
                        pills.Add(new RectangleF(x, (height - pillHeight) / 2, w, pillHeight));
                        x += w + 6 * s;
                    }
                    x += 2 * s; // 8 from the last pill to the edge
                }
                else
                {
                    x += 16 * s;
                }
            }
            float shadow = Shadow;
            bubble = new RectangleF(shadow, shadow - 2 * s, (float)Math.Ceiling(x), height);
            // Everything above was measured from the bubble's corner; move it into window coordinates.
            iconRect.Offset(bubble.X, bubble.Y);
            textRect.Offset(bubble.X, bubble.Y);
            dividerX += bubble.X;
            for (int i = 0; i < pills.Count; i++)
            {
                var p = pills[i];
                p.Offset(bubble.X, bubble.Y);
                pills[i] = p;
            }
            return new Size((int)Math.Ceiling(bubble.Width + 2 * shadow), (int)Math.Ceiling(bubble.Height + 2 * shadow));
        }

        private Bitmap Draw()
        {
            bool light = LightTheme();
            var background = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44);
            var ink = light ? Color.FromArgb(26, 26, 26) : Color.White;
            var size = Size;
            var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                // Soft shadow, a little below the bubble.
                float shadow = Shadow;
                for (float i = shadow; i >= 1; i -= 1)
                {
                    var r = bubble;
                    r.Inflate(i, i);
                    r.Offset(0, 3 * s);
                    float strength = 1 - i / shadow;
                    using (var brush = new SolidBrush(Color.FromArgb((int)(10 * strength * strength) + 1, 0, 0, 0)))
                    using (var path = Rounded(r, bubble.Height / 2 + i))
                    {
                        g.FillPath(brush, path);
                    }
                }

                using (var fill = new SolidBrush(background))
                using (var border = new Pen(Color.FromArgb(light ? 24 : 40, light ? Color.Black : Color.White), 1))
                using (var path = Rounded(bubble, Math.Min(14 * s, bubble.Height / 2)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using (var brush = new SolidBrush(ink))
                {
                    if (IconFamily.Length > 0)
                    {
                        using (var iconFont = new Font(IconFamily, 14 * s, GraphicsUnit.Pixel))
                        {
                            g.DrawString(glyph, iconFont, brush, iconRect, center);
                        }
                    }
                    using (var textFont = TextFont())
                    using (var format = new StringFormat(StringFormatFlags.NoWrap)
                    {
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                    })
                    {
                        g.DrawString(message, textFont, brush, textRect, format);
                    }

                    if (actions.Count > 0)
                    {
                        using (var divider = new Pen(Color.FromArgb(50, ink), 1))
                        {
                            float mid = bubble.Y + bubble.Height / 2;
                            g.DrawLine(divider, dividerX, mid - 8 * s, dividerX, mid + 8 * s);
                        }
                        using (var pillFont = PillFont())
                        {
                            for (int i = 0; i < pills.Count; i++)
                            {
                                using (var pillFill = new SolidBrush(Color.FromArgb(i == hover ? 46 : 22, ink)))
                                using (var path = Rounded(pills[i], pills[i].Height / 2))
                                {
                                    g.FillPath(pillFill, path);
                                }
                                g.DrawString(actions[i].Title, pillFont, brush, pills[i], center);
                            }
                        }
                    }
                }
            }
            return bitmap;
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Puts the drawn frame on screen with per-pixel transparency.</summary>
        private void Commit(byte opacity)
        {
            if (frame == null || !IsHandleCreated) return;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr memory = Native.CreateCompatibleDC(screen);
            IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                bitmap = frame.GetHbitmap(Color.FromArgb(0));
                previous = Native.SelectObject(memory, bitmap);
                var size = new Native.SIZE(frame.Width, frame.Height);
                var source = new Native.POINT(0, 0);
                var position = new Native.POINT(Left, Top);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = opacity,
                    AlphaFormat = Native.AC_SRC_ALPHA,
                };
                Native.UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
                if (bitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memory, previous);
                    Native.DeleteObject(bitmap);
                }
                Native.DeleteDC(memory);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hideTimer.Dispose();
                fadeTimer.Dispose();
                watchTimer.Dispose();
                frame?.Dispose();
                frame = null;
                Toast.Closed(this);
            }
            base.Dispose(disposing);
        }
    }
}
