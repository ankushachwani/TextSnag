using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TextSnag
{
    /// <summary>Things a global shortcut can trigger.</summary>
    internal enum ShortcutAction { Capture, CaptureNoLineBreaks, CapturePrevious, ToggleAdditive }

    internal static class ShortcutActions
    {
        public static readonly ShortcutAction[] All =
            { ShortcutAction.Capture, ShortcutAction.CaptureNoLineBreaks, ShortcutAction.CapturePrevious, ShortcutAction.ToggleAdditive };

        public static string Title(this ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.Capture: return "Start Capture";
                case ShortcutAction.CaptureNoLineBreaks: return "Capture Without Line Breaks";
                case ShortcutAction.CapturePrevious: return "Capture Previous Area";
                default: return "Toggle Additive Clipboard";
            }
        }

        /// <summary>The name used in the registry (the same keys as the Mac app's defaults).</summary>
        public static string Key(this ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.Capture: return "capture";
                case ShortcutAction.CaptureNoLineBreaks: return "captureNoLineBreaks";
                case ShortcutAction.CapturePrevious: return "capturePrevious";
                default: return "toggleAdditive";
            }
        }

        public static Shortcut? DefaultShortcut(this ShortcutAction action) =>
            action == ShortcutAction.Capture ? Shortcut.DefaultCapture : (Shortcut?)null;
    }

    /// <summary>All user settings, persisted in the registry under HKEY_CURRENT_USER\Software\TextSnag.</summary>
    internal sealed class Settings
    {
        public static readonly Settings Shared = new Settings();

        private const string KeyPath = @"Software\TextSnag";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Any setting changed (so open windows and menus can refresh).</summary>
        public event Action Changed;
        /// <summary>A shortcut changed, so the hot keys get re-registered.</summary>
        public event Action ShortcutsChanged;

        private readonly Dictionary<string, bool> bools = new Dictionary<string, bool>();
        private readonly Dictionary<ShortcutAction, Shortcut?> shortcuts = new Dictionary<ShortcutAction, Shortcut?>();
        private string additiveBuffer;

        private Settings()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                additiveBuffer = key?.GetValue("additiveBuffer") as string ?? "";
                foreach (var action in ShortcutActions.All)
                {
                    var stored = key?.GetValue("shortcut." + action.Key()) as string;
                    shortcuts[action] = stored == null ? action.DefaultShortcut() : Shortcut.Parse(stored);
                }
            }
        }

        // Formatting
        public bool KeepLineBreaks { get => Bool("keepLineBreaks", true); set => SetBool("keepLineBreaks", value); }
        public bool KeepIndentation { get => Bool("keepIndentation", true); set => SetBool("keepIndentation", value); }
        public bool TidyBullets { get => Bool("tidyBullets", true); set => SetBool("tidyBullets", value); }
        public bool FixPunctuation { get => Bool("fixPunctuation", true); set => SetBool("fixPunctuation", value); }
        public bool StraightQuotes { get => Bool("straightQuotes", false); set => SetBool("straightQuotes", value); }
        public bool DetectCode { get => Bool("detectCode", true); set => SetBool("detectCode", value); }
        public bool DetectTables { get => Bool("detectTables", true); set => SetBool("detectTables", value); }
        public bool DirectText { get => Bool("directText", true); set => SetBool("directText", value); }

        // Clipboard & after capture
        public bool Additive
        {
            get => Bool("additiveClipboard", false);
            set
            {
                if (!value) additiveBuffer = "";
                if (!value) Write("additiveBuffer", "");
                SetBool("additiveClipboard", value);
            }
        }

        public string AdditiveBuffer
        {
            get => additiveBuffer;
            set
            {
                additiveBuffer = value ?? "";
                Write("additiveBuffer", additiveBuffer);
                Changed?.Invoke();
            }
        }

        public bool QuickActions { get => Bool("quickActions", true); set => SetBool("quickActions", value); }
        public bool Speak { get => Bool("speakAfterCapture", false); set => SetBool("speakAfterCapture", value); }
        public bool OpenLinks { get => Bool("openQRLinks", false); set => SetBool("openQRLinks", value); }
        public bool ShowToast { get => Bool("showToast", true); set => SetBool("showToast", value); }
        public bool PlaySound { get => Bool("playSound", false); set => SetBool("playSound", value); }

        // App state
        public bool HasLaunchedBefore { get => Bool("hasLaunchedBefore", false); set => SetBool("hasLaunchedBefore", value); }

        public int SettingsTab
        {
            get => Read("settingsTab") is int i ? i : 0;
            set => Write("settingsTab", value);
        }

        /// <summary>The last selection, for "Capture Previous Area" (kept across restarts).</summary>
        public Rectangle? LastArea
        {
            get
            {
                var parts = (Read("lastArea") as string ?? "").Split(',');
                if (parts.Length == 4 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)
                    && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h) && w > 1 && h > 1)
                {
                    return new Rectangle(x, y, w, h);
                }
                return null;
            }
            set => Write("lastArea", value.HasValue ? $"{value.Value.X},{value.Value.Y},{value.Value.Width},{value.Value.Height}" : "");
        }

        public FormatOptions FormatOptions(bool? keepLineBreaks = null) => new FormatOptions
        {
            KeepLineBreaks = keepLineBreaks ?? KeepLineBreaks,
            KeepIndentation = KeepIndentation,
            TidyBullets = TidyBullets,
            FixPunctuation = FixPunctuation,
            StraightQuotes = StraightQuotes,
            DetectCode = DetectCode,
            DetectTables = DetectTables,
        };

        public Shortcut? GetShortcut(ShortcutAction action) => shortcuts.TryGetValue(action, out var sc) ? sc : null;

        public void SetShortcut(Shortcut? shortcut, ShortcutAction action)
        {
            // One combination can only do one thing.
            if (shortcut.HasValue)
            {
                foreach (var other in ShortcutActions.All)
                {
                    if (other != action && shortcuts[other] == shortcut)
                    {
                        shortcuts[other] = null;
                        Write("shortcut." + other.Key(), "");
                    }
                }
            }
            shortcuts[action] = shortcut;
            Write("shortcut." + action.Key(), shortcut?.Serialize() ?? "");
            RaiseShortcutsChanged();
            Changed?.Invoke();
        }

        public void RaiseShortcutsChanged() => ShortcutsChanged?.Invoke();

        /// <summary>Starts TextSnag when you sign in to Windows (the per-user Run key; no admin needed).</summary>
        public bool LaunchAtLogin
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    var value = key?.GetValue("TextSnag") as string;
                    return value != null && value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (value) key.SetValue("TextSnag", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("TextSnag", false);
                }
                Changed?.Invoke();
            }
        }

        // MARK: Storage

        private bool Bool(string name, bool defaultValue)
        {
            if (bools.TryGetValue(name, out bool cached)) return cached;
            bool value = Read(name) is int i ? i != 0 : defaultValue;
            bools[name] = value;
            return value;
        }

        private void SetBool(string name, bool value)
        {
            bools[name] = value;
            Write(name, value ? 1 : 0);
            Changed?.Invoke();
        }

        private static object Read(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath)) return key?.GetValue(name);
            }
            catch (Exception e)
            {
                Log.Write("settings read failed: " + e.Message);
                return null;
            }
        }

        private static void Write(string name, object value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (value is int i) key.SetValue(name, i, RegistryValueKind.DWord);
                    else key.SetValue(name, value?.ToString() ?? "", RegistryValueKind.String);
                }
            }
            catch (Exception e)
            {
                Log.Write("settings write failed: " + e.Message);
            }
        }
    }
}
