using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TextSnag
{
    /// <summary>The notification-area app: tray icon and menu, shortcuts, and the capture flow.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Settings settings = Settings.Shared;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Control ui = new Control(); // for getting back onto the UI thread
        private readonly ClipboardService clipboard;
        private SpeechSynthesizer speech;
        private bool busy;
        private Rectangle? lastArea;
        private Stopwatch sinceSelection;

        public TrayApp(EventWaitHandle showSettingsSignal)
        {
            var unused = ui.Handle;
            clipboard = ClipboardService.Shared;
            lastArea = settings.LastArea;

            menu.Opening += (sender, e) =>
            {
                BuildMenu();
                e.Cancel = false;
            };
            BuildMenu();
            tray = new NotifyIcon { Icon = AppIcons.Tray(), ContextMenuStrip = menu, Visible = true };
            UpdateTrayTooltip();
            // A left click captures; a right click opens the menu.
            tray.MouseClick += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left) After(150, () => Perform(ShortcutAction.Capture));
            };
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            RegisterHotKeys();
            settings.ShortcutsChanged += () => ui.BeginInvoke(new Action(RegisterHotKeys));

            Task.Run(() =>
            {
                var t = Stopwatch.StartNew();
                Ocr.WarmUp();
                Log.Write($"warm-up took {t.Elapsed.TotalSeconds:0.000}s ({Ocr.LanguageName})");
            });

            if (!settings.HasLaunchedBefore)
            {
                settings.HasLaunchedBefore = true;
                var combo = settings.GetShortcut(ShortcutAction.Capture)?.DisplayString;
                Toast.Show(combo == null ? "TextSnag is in the notification area. Click its icon to capture text."
                                         : $"TextSnag is in the notification area. Press {combo} to capture text.", Glyph.Info);
            }

            // Opening TextSnag again (e.g. from the Start menu) shows Settings.
            new Thread(() =>
            {
                try
                {
                    while (showSettingsSignal.WaitOne()) ui.BeginInvoke(new Action(SettingsForm.ShowShared));
                }
                catch (Exception) { } // shutting down
            }) { IsBackground = true, Name = "TextSnag show-settings listener" }.Start();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Light/dark taskbar switch: redraw the tray icon in the matching color.
            if (e.Category != UserPreferenceCategory.General) return;
            ui.BeginInvoke(new Action(() =>
            {
                var old = tray.Icon;
                tray.Icon = AppIcons.Tray();
                old?.Dispose();
            }));
        }

        private void UpdateTrayTooltip()
        {
            var combo = settings.GetShortcut(ShortcutAction.Capture)?.DisplayString;
            string text = combo == null ? "TextSnag" : $"TextSnag ({combo} to capture)";
            tray.Text = text.Length > 63 ? "TextSnag" : text; // the notification area allows 63 characters
        }

        private void RegisterHotKeys()
        {
            HotKeyCenter.Shared.Register(ShortcutActions.All.Select(action =>
                (action.Key(), settings.GetShortcut(action), (Action)(() => Perform(action)))));
            UpdateTrayTooltip();
        }

        private void Perform(ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.Capture: StartCapture(false, settings.KeepLineBreaks); break;
                case ShortcutAction.CaptureNoLineBreaks: StartCapture(false, false); break;
                case ShortcutAction.CapturePrevious: StartCapture(true, settings.KeepLineBreaks); break;
                case ShortcutAction.ToggleAdditive:
                    settings.Additive = !settings.Additive;
                    ShowToast(settings.Additive ? "Additive clipboard on" : "Additive clipboard off", Glyph.Add);
                    break;
            }
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread after a delay (e.g. once a menu has faded out).</summary>
        private static void After(int milliseconds, Action action)
        {
            var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, milliseconds) };
            timer.Tick += (sender, e) =>
            {
                timer.Dispose();
                action();
            };
            timer.Start();
        }

        // MARK: Capture

        private void StartCapture(bool previous, bool keepLineBreaks)
        {
            if (busy) return;
            busy = true;
            // The bubble must not end up in the screenshot.
            bool hadToast = Toast.HideNow();
            After(hadToast ? 60 : 1, () =>
            {
                try
                {
                    if (previous && lastArea.HasValue)
                    {
                        var area = lastArea.Value;
                        sinceSelection = Stopwatch.StartNew();
                        Recognize(ScreenCapture.Grab(area), area, keepLineBreaks, cancelled: false);
                    }
                    else
                    {
                        ScreenCapture.Select((image, area) =>
                        {
                            if (image != null)
                            {
                                lastArea = area;
                                settings.LastArea = area;
                                sinceSelection = Stopwatch.StartNew();
                            }
                            Recognize(image, area, keepLineBreaks, cancelled: image == null);
                        });
                    }
                }
                catch (Exception e)
                {
                    Log.Write("capture failed: " + e);
                    busy = false;
                    ShowToast("Couldn’t capture the screen", Glyph.Warning);
                }
            });
        }

        private void Recognize(RawImage image, Rectangle area, bool keepLineBreaks, bool cancelled)
        {
            if (image == null)
            {
                busy = false;
                if (!cancelled) ShowToast("Couldn’t capture the screen", Glyph.Warning);
                return;
            }
            if (!Ocr.IsWarm) ShowToast("Getting ready…", Glyph.Clock);
            var options = settings.FormatOptions(keepLineBreaks);
            bool useDirectText = settings.DirectText;
            var timer = Stopwatch.StartNew();

            Task.Run(() =>
            {
                OcrResult result = null;
                try
                {
                    // Direct Text reads the app's own text while OCR reads the pixels, in parallel.
                    var direct = useDirectText ? Task.Run(() => SafeDirectText(area)) : null;
                    result = Ocr.Recognize(image, options);
                    string directText = direct != null && direct.Wait(150) ? direct.Result : null;

                    // Use the app's text only when it matches what's visible (tables keep their columns).
                    if (directText != null && result.Raw.Length > 0 && !result.Kind.IsTable && DirectText.Agrees(directText, result.Raw))
                    {
                        result.Text = TextFormatter.FormatDirect(directText, result.Text, result.Kind, options);
                        result.IsDirectText = true;
                    }
                }
                catch (Exception e)
                {
                    Log.Write("recognition failed: " + e);
                }
                var elapsed = timer.Elapsed;
                ui.BeginInvoke(new Action(() => Deliver(result, elapsed)));
            });
        }

        private static string SafeDirectText(Rectangle area)
        {
            try { return DirectText.TextIn(area); }
            catch (Exception e)
            {
                Log.Write("direct text failed: " + e.Message);
                return null;
            }
        }

        private void Deliver(OcrResult result, TimeSpan elapsed)
        {
            busy = false;
            if (result == null || result.IsEmpty)
            {
                if (!Ocr.IsAvailable) ShowToast("Windows has no text-recognition language installed", Glyph.Warning);
                else ShowToast("No text found", Glyph.Search);
                return;
            }

            string output = result.Combined;
            if (settings.Additive)
            {
                output = settings.AdditiveBuffer.Length == 0 ? output : settings.AdditiveBuffer + "\n" + output;
                settings.AdditiveBuffer = output;
            }
            // Tables also go on the clipboard as HTML, so they paste as real tables.
            string html = !settings.Additive && result.Kind.IsTable && result.Codes.Count == 0
                ? TextFormatter.HtmlTable(result.Kind.Rows) : null;
            var actions = settings.ShowToast && settings.QuickActions && !settings.Additive ? QuickActions(result) : new List<ToastAction>();
            if (actions.Count == 0) clipboard.Copy(output, html);
            else clipboard.CopyWatchingForPaste(output, html, Toast.Pasted);

            if (sinceSelection != null)
            {
                Log.Write($"selection → clipboard {sinceSelection.Elapsed.TotalSeconds:0.000}s (recognition {elapsed.TotalSeconds:0.000}s, "
                          + result.Kind.Label + (result.IsDirectText ? ", direct text" : "") + ")");
                sinceSelection = null;
            }

            if (settings.PlaySound) PlayCaptureSound();
            if (settings.ShowToast) Toast.Show(Message(result), GlyphFor(result), actions);

            if (settings.OpenLinks)
            {
                foreach (var code in result.Codes)
                {
                    if (Uri.TryCreate(code, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        Open(uri.AbsoluteUri);
                    }
                }
            }
            if (settings.Speak && result.Text.Length > 0) Speak(result.Text);
        }

        private string Message(OcrResult result)
        {
            if (settings.Additive) return "Added text";
            if (result.Codes.Count > 0) return result.Text.Length == 0 ? "Copied QR code" : "Copied text + QR code";
            if (result.IsDirectText) return "Copied exact text";
            switch (result.Kind.Type)
            {
                case ContentType.Code: return "Copied code";
                case ContentType.Table: return "Copied table";
                default: return "Copied text";
            }
        }

        private static string GlyphFor(OcrResult result)
        {
            if (result.Codes.Count > 0) return Glyph.QRCode;
            if (result.IsDirectText) return Glyph.Exact;
            switch (result.Kind.Type)
            {
                case ContentType.Code: return Glyph.Code;
                case ContentType.Table: return Glyph.Table;
                default: return Glyph.Copy;
            }
        }

        // MARK: Quick actions

        /// <summary>One-click follow-ups offered in the bubble, chosen from what was captured.</summary>
        private List<ToastAction> QuickActions(OcrResult result)
        {
            string text = result.Text;
            var actions = new List<ToastAction>();
            Func<string> Copy(string value, string confirmation) => () =>
            {
                clipboard.Copy(value);
                return confirmation;
            };

            if (result.Kind.IsTable)
            {
                actions.Add(new ToastAction("Not a Table", Copy(TextFormatter.PlainRows(result.Kind.Rows), "Copied as plain text")));
            }

            // Links, email addresses and phone numbers found in the text.
            string link = DataDetector.FirstLink(text);
            if (link != null)
            {
                bool isMail = link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
                actions.Add(new ToastAction(isMail ? "Email" : "Open Link", () =>
                {
                    Open(link);
                    return null;
                }));
            }
            string phone = DataDetector.FirstPhone(text);
            if (phone != null)
            {
                actions.Add(new ToastAction("Call", () =>
                {
                    Open("tel:" + phone);
                    return null;
                }));
            }

            if (!result.Kind.IsTable && text.Contains("\n"))
            {
                actions.Add(new ToastAction("One Line", Copy(TextFormatter.OneLine(text), "Copied as one line")));
            }
            var numbers = TextFormatter.Numbers(text);
            if (numbers.Count > 0 && string.Join(" ", numbers) != text.Trim())
            {
                actions.Add(new ToastAction("Numbers", Copy(string.Join("\n", numbers), "Copied numbers only")));
            }
            if (!result.IsDirectText && result.Raw.Length > 0 && result.Raw != text)
            {
                actions.Add(new ToastAction("Original", Copy(result.Raw, "Copied without formatting")));
            }
            return actions.Take(4).ToList();
        }

        private static void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Log.Write("couldn't open " + target + ": " + e.Message);
            }
        }

        private static void PlayCaptureSound()
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Navigation Start.wav");
            try
            {
                if (File.Exists(file))
                {
                    using (var player = new SoundPlayer(file)) player.Play();
                    return;
                }
            }
            catch (Exception) { }
            SystemSounds.Asterisk.Play();
        }

        private void Speak(string text)
        {
            try
            {
                if (speech == null)
                {
                    speech = new SpeechSynthesizer();
                    speech.SetOutputToDefaultAudioDevice();
                }
                speech.SpeakAsyncCancelAll();
                speech.SpeakAsync(text);
            }
            catch (Exception e)
            {
                Log.Write("speech failed: " + e.Message);
            }
        }

        private bool IsSpeaking => speech != null && speech.State == SynthesizerState.Speaking;

        private void ShowToast(string message, string glyph)
        {
            if (settings.ShowToast) Toast.Show(message, glyph);
        }

        // MARK: Menu

        private void BuildMenu()
        {
            foreach (var old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
            menu.Items.Clear();

            menu.Items.Add(Item("Start Capture", () => After(200, () => Perform(ShortcutAction.Capture)), ShortcutAction.Capture));
            var previous = Item("Capture Previous Area", () => After(200, () => Perform(ShortcutAction.CapturePrevious)), ShortcutAction.CapturePrevious);
            previous.Enabled = lastArea.HasValue;
            menu.Items.Add(previous);

            var failed = HotKeyCenter.Shared.Failed;
            if (failed.Count > 0)
            {
                var names = string.Join(", ", ShortcutActions.All.Where(a => failed.Contains(a.Key())).Select(a => a.Title()));
                menu.Items.Add(new ToolStripMenuItem("⚠ Shortcut unavailable: " + names) { Enabled = false });
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Toggle("Keep Line Breaks", settings.KeepLineBreaks, () => settings.KeepLineBreaks = !settings.KeepLineBreaks));
            menu.Items.Add(Toggle("Additive Clipboard", settings.Additive, () => settings.Additive = !settings.Additive));
            if (settings.Additive)
            {
                var clear = Item("Clear Additive Clipboard", () =>
                {
                    settings.AdditiveBuffer = "";
                    ShowToast("Additive clipboard cleared", Glyph.Delete);
                });
                clear.Enabled = settings.AdditiveBuffer.Length > 0;
                menu.Items.Add(clear);
            }
            menu.Items.Add(Toggle("Read Text Aloud", settings.Speak, () => settings.Speak = !settings.Speak));
            if (IsSpeaking) menu.Items.Add(Item("Stop Speaking", () => speech.SpeakAsyncCancelAll()));

            menu.Items.Add(new ToolStripSeparator());
            var settingsItem = Item("Settings…", SettingsForm.ShowShared);
            settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
            menu.Items.Add(settingsItem);
            menu.Items.Add(Item("Quit TextSnag", ExitThread));
        }

        private ToolStripMenuItem Item(string title, Action action, ShortcutAction? shortcutFor = null)
        {
            var item = new ToolStripMenuItem(title);
            item.Click += (sender, e) => action();
            if (shortcutFor.HasValue)
            {
                var sc = settings.GetShortcut(shortcutFor.Value);
                if (sc.HasValue) item.ShortcutKeyDisplayString = sc.Value.DisplayString;
            }
            return item;
        }

        private ToolStripMenuItem Toggle(string title, bool on, Action action)
        {
            var item = Item(title, action);
            item.Checked = on;
            return item;
        }

        protected override void ExitThreadCore()
        {
            // Text that hasn't been pasted yet would vanish with the app: hand it over now.
            clipboard.FlushPending();
            HotKeyCenter.Shared.UnregisterAll();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Toast.HideNow();
            tray.Visible = false;
            tray.Dispose();
            speech?.Dispose();
            base.ExitThreadCore();
        }
    }
}
