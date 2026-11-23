using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace TextSnag
{
    /// <summary>A plain Windows settings window: tabs, no scrolling, sized to fit the tallest tab.</summary>
    internal sealed class SettingsForm : Form
    {
        private static SettingsForm shared;

        public static void ShowShared()
        {
            if (shared == null || shared.IsDisposed) shared = new SettingsForm();
            if (shared.WindowState == FormWindowState.Minimized) shared.WindowState = FormWindowState.Normal;
            shared.Show();
            shared.Activate();
            Native.SetForegroundWindow(shared.Handle);
        }

        private readonly Settings settings = Settings.Shared;
        private readonly float s;
        private readonly int labelWidth, contentWidth;
        private readonly List<Action> refreshers = new List<Action>();
        private bool syncing;

        private SettingsForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            s = DeviceDpi / 96f;
            labelWidth = Px(120);
            contentWidth = Px(340);

            Text = "TextSnag Settings";
            Icon = AppIcons.Window;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(Px(12), Px(4)) };
            var pages = new[]
            {
                Page("General", GeneralPane()),
                Page("Formatting", FormattingPane()),
                Page("Shortcuts", ShortcutsPane()),
                Page("About", AboutPane()),
            };
            foreach (var (page, _) in pages) tabs.TabPages.Add(page);
            Controls.Add(tabs);

            // Size the window to the tallest pane.
            ClientSize = new Size(labelWidth + contentWidth + Px(80), Px(600));
            tabs.PerformLayout();
            int header = tabs.Height - tabs.DisplayRectangle.Height;
            int tallest = pages.Max(p => p.Item2.GetPreferredSize(new Size(tabs.DisplayRectangle.Width, 0)).Height);
            ClientSize = new Size(ClientSize.Width, header + tallest + Px(14 + 10 + 8)); // + the page padding

            tabs.SelectedIndex = Math.Min(Math.Max(settings.SettingsTab, 0), tabs.TabCount - 1);
            tabs.SelectedIndexChanged += (sender, e) => settings.SettingsTab = tabs.SelectedIndex;

            settings.Changed += Sync;
            FormClosed += (sender, e) => settings.Changed -= Sync;
            Sync();
        }

        private int Px(float value) => (int)Math.Round(value * s);

        private void Sync()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(Sync)); return; }
            syncing = true;
            foreach (var refresh in refreshers) refresh();
            syncing = false;
        }

        private (TabPage, Control) Page(string title, Control content)
        {
            var page = new TabPage(title) { UseVisualStyleBackColor = true, Padding = new Padding(Px(16), Px(14), Px(16), Px(10)) };
            content.Dock = DockStyle.Top;
            page.Controls.Add(content);
            return (page, content);
        }

        // MARK: Panes

        private Control GeneralPane()
        {
            var grid = Grid();
            var clear = new Button { Text = "Clear Additive Clipboard", AutoSize = true, Margin = new Padding(Px(18), Px(2), 0, Px(4)) };
            clear.Click += (sender, e) => settings.AdditiveBuffer = "";
            refreshers.Add(() =>
            {
                clear.Visible = settings.Additive;
                clear.Enabled = settings.AdditiveBuffer.Length > 0;
            });
            Row(grid, "Clipboard",
                Check("Additive clipboard", () => settings.Additive, v => settings.Additive = v),
                Hint("Each capture is added after the previous ones."),
                clear);
            Divider(grid);

            var quickActions = Check("Quick actions in the bubble", () => settings.QuickActions, v => settings.QuickActions = v);
            refreshers.Add(() => quickActions.Enabled = settings.ShowToast);
            Row(grid, "After capture",
                Check("Show “Copied” bubble", () => settings.ShowToast, v => settings.ShowToast = v),
                quickActions,
                Hint("One line, numbers only, not a table, open link, email, call…"),
                Check("Play sound", () => settings.PlaySound, v => settings.PlaySound = v),
                Check("Read text aloud", () => settings.Speak, v => settings.Speak = v),
                Check("Open links from QR codes", () => settings.OpenLinks, v => settings.OpenLinks = v));
            Divider(grid);

            Row(grid, "Startup",
                Check("Start TextSnag when you sign in", () => settings.LaunchAtLogin, v => settings.LaunchAtLogin = v));
            return grid;
        }

        private Control FormattingPane()
        {
            var grid = Grid();
            Row(grid, "Direct Text",
                Check("Copy the app’s own text when possible", () => settings.DirectText, v => settings.DirectText = v),
                Hint("Exact characters, no recognition mistakes, for dialogs, settings and web pages. Images and video still use OCR."));
            Divider(grid);
            Row(grid, "Detect",
                Check("Code", () => settings.DetectCode, v => settings.DetectCode = v),
                Hint("Keeps indentation, fixes quotes, handles dark terminals."),
                Check("Tables", () => settings.DetectTables, v => settings.DetectTables = v),
                Hint("Pastes as a real table in Word, Excel, Outlook, OneNote, Google Docs and Sheets, and as a neat text table in Teams or Slack. Turn off to always get plain lines."));
            Divider(grid);
            Row(grid, "Text",
                Check("Keep line breaks", () => settings.KeepLineBreaks, v => settings.KeepLineBreaks = v),
                Hint("When off, lines are joined into paragraphs."),
                Check("Keep indentation", () => settings.KeepIndentation, v => settings.KeepIndentation = v),
                Check("Tidy bullets", () => settings.TidyBullets, v => settings.TidyBullets = v),
                Hint("Bullets are kept and follow their nesting level (•, ◦, ▪). Odd styles like ● or * become •."),
                Check("Fix punctuation spacing", () => settings.FixPunctuation, v => settings.FixPunctuation = v),
                Hint("“word ,word” becomes “word, word”."),
                Check("Use straight quotes", () => settings.StraightQuotes, v => settings.StraightQuotes = v));
            return grid;
        }

        private Control ShortcutsPane()
        {
            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(190)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var action in ShortcutActions.All)
            {
                var label = new Label
                {
                    Text = action.Title() + ":",
                    AutoSize = false,
                    Width = Px(185),
                    Height = Px(30),
                    TextAlign = ContentAlignment.MiddleRight,
                    Margin = new Padding(0, Px(3), Px(4), Px(3)),
                };
                var recorder = new ShortcutButton { Width = Px(170), Height = Px(30), Margin = new Padding(0, Px(3), Px(4), Px(3)) };
                recorder.Changed += sc => settings.SetShortcut(sc, action);
                var remove = new Button
                {
                    Text = "✕",
                    Width = Px(30),
                    Height = Px(30),
                    FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(0, Px(3), 0, Px(3)),
                    TabStop = false,
                };
                remove.FlatAppearance.BorderSize = 0;
                new ToolTip().SetToolTip(remove, "Remove shortcut");
                remove.Click += (sender, e) => settings.SetShortcut(null, action);
                refreshers.Add(() =>
                {
                    if (!recorder.Recording) recorder.Shortcut = settings.GetShortcut(action);
                    remove.Visible = settings.GetShortcut(action).HasValue;
                });
                grid.Controls.Add(label);
                grid.Controls.Add(recorder);
                grid.Controls.Add(remove);
            }

            var failedNote = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(0, Px(8), 0, 0) };
            refreshers.Add(() =>
            {
                var failed = HotKeyCenter.Shared.Failed;
                var names = ShortcutActions.All.Where(a => failed.Contains(a.Key())).Select(a => a.Title()).ToList();
                failedNote.Text = names.Count == 0 ? "" : "Another app is already using the shortcut for: " + string.Join(", ", names);
                failedNote.Visible = names.Count > 0;
                failedNote.MaximumSize = new Size(labelWidth + contentWidth, 0);
            });
            grid.Controls.Add(failedNote);
            grid.SetColumnSpan(failedNote, 3);

            var hint = Hint("Click a box and press the keys you want. Shortcuts need Ctrl, Alt or Win (or an F-key or Print Screen). Esc cancels, Backspace removes.");
            hint.Margin = new Padding(0, Px(10), 0, 0);
            hint.MaximumSize = new Size(labelWidth + contentWidth, 0);
            grid.Controls.Add(hint);
            grid.SetColumnSpan(hint, 3);
            return grid;
        }

        private Control AboutPane()
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Padding = new Padding(0, Px(12), 0, Px(12)),
            };
            int width = labelWidth + contentWidth;
            var icon = new PictureBox
            {
                Image = AppIcons.Image(Px(72)),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Size = new Size(width, Px(80)),
            };
            var name = new Label
            {
                Text = "TextSnag",
                Font = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(width, Px(34)),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            var version = Application.ProductVersion.Split('+')[0];
            var versionLabel = new Label
            {
                Text = "Version " + version,
                ForeColor = SystemColors.GrayText,
                AutoSize = false,
                Size = new Size(width, Px(22)),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            var about = new Label
            {
                Text = "Free, open-source screen text capture for Windows.\nEverything runs on your PC. Nothing is uploaded.\n\nText recognition language: " + Ocr.LanguageName,
                AutoSize = false,
                Size = new Size(width, Px(84)),
                TextAlign = ContentAlignment.TopCenter,
                Margin = new Padding(0, Px(8), 0, 0),
            };
            panel.Controls.AddRange(new Control[] { icon, name, versionLabel, about });
            return panel;
        }

        // MARK: Building blocks

        /// <summary>A right-aligned label column next to controls.</summary>
        private TableLayoutPanel Grid()
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, contentWidth));
            return grid;
        }

        private void Row(TableLayoutPanel grid, string title, params Control[] content)
        {
            var label = new Label
            {
                Text = title + ":",
                AutoSize = false,
                Width = labelWidth - Px(8),
                Height = Px(22),
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Margin = new Padding(0, Px(1), Px(8), 0),
            };
            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            stack.Controls.AddRange(content);
            grid.Controls.Add(label);
            grid.Controls.Add(stack);
        }

        private void Divider(TableLayoutPanel grid)
        {
            var line = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                AutoSize = false,
                Height = 2,
                Width = labelWidth + contentWidth,
                Margin = new Padding(0, Px(8), 0, Px(8)),
            };
            grid.Controls.Add(line);
            grid.SetColumnSpan(line, 2);
        }

        private CheckBox Check(string text, Func<bool> get, Action<bool> set)
        {
            var box = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, Px(2), 0, Px(2)) };
            box.CheckedChanged += (sender, e) =>
            {
                if (!syncing) set(box.Checked);
            };
            refreshers.Add(() => box.Checked = get());
            return box;
        }

        private Label Hint(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(contentWidth - Px(18), 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(Px(18), 0, 0, Px(6)),
        };
    }

    /// <summary>A button that records the next key combination pressed.</summary>
    internal sealed class ShortcutButton : Button
    {
        private Shortcut? shortcut;
        public event Action<Shortcut?> Changed;
        public bool Recording { get; private set; }

        public ShortcutButton()
        {
            UpdateTitle();
        }

        public Shortcut? Shortcut
        {
            get => shortcut;
            set
            {
                shortcut = value;
                UpdateTitle();
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Recording) Stop(); else Start();
        }

        private void Start()
        {
            Recording = true;
            HotKeyCenter.Shared.UnregisterAll(); // so pressing a current shortcut gets recorded, not run
            UpdateTitle();
            Focus();
        }

        private void Stop()
        {
            if (!Recording) return;
            Recording = false;
            UpdateTitle();
            Settings.Shared.RaiseShortcutsChanged(); // re-register hot keys
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Stop();
        }

        protected override bool IsInputKey(Keys keyData) => Recording || base.IsInputKey(keyData);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Recording) return base.ProcessCmdKey(ref msg, keyData);
            var key = keyData & Keys.KeyCode;
            var modifiers = keyData & Keys.Modifiers;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin
                || key == Keys.LControlKey || key == Keys.RControlKey || key == Keys.LShiftKey || key == Keys.RShiftKey
                || key == Keys.LMenu || key == Keys.RMenu)
            {
                return true; // wait for the actual key
            }
            bool win = (Native.GetKeyState((int)Keys.LWin) & 0x8000) != 0 || (Native.GetKeyState((int)Keys.RWin) & 0x8000) != 0;
            if (modifiers == Keys.None && !win)
            {
                if (key == Keys.Escape) { Stop(); return true; }
                if (key == Keys.Back || key == Keys.Delete)
                {
                    Shortcut = null;
                    Changed?.Invoke(null);
                    Stop();
                    return true;
                }
            }
            int mods = 0;
            if ((modifiers & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
            if ((modifiers & Keys.Alt) != 0) mods |= Native.MOD_ALT;
            if ((modifiers & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
            if (win) mods |= Native.MOD_WIN;
            var sc = new Shortcut((int)key, mods);
            if (!sc.IsValid)
            {
                SystemSounds.Beep.Play();
                return true;
            }
            Shortcut = sc;
            Changed?.Invoke(sc);
            Stop();
            return true;
        }

        private void UpdateTitle()
        {
            Text = Recording ? "Press keys…" : (shortcut?.DisplayString ?? "Record Shortcut");
        }
    }
}
