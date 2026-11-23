using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TextSnag
{
    /// <summary>A key combination in Win32 terms (what RegisterHotKey wants).</summary>
    internal struct Shortcut : IEquatable<Shortcut>
    {
        public readonly int Key;       // virtual-key code
        public readonly int Modifiers; // MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN

        public Shortcut(int key, int modifiers)
        {
            Key = key;
            Modifiers = modifiers & (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_WIN);
        }

        /// <summary>Ctrl+Alt+5: the Windows spelling of the Mac's ⌥⌘5.</summary>
        public static readonly Shortcut DefaultCapture = new Shortcut((int)Keys.D5, Native.MOD_CONTROL | Native.MOD_ALT);

        /// <summary>Keys that are fine on their own: F1–F24, Print Screen, Pause.</summary>
        public bool IsStandaloneKey => (Key >= (int)Keys.F1 && Key <= (int)Keys.F24) || Key == (int)Keys.PrintScreen || Key == (int)Keys.Pause;

        /// <summary>A global shortcut needs Ctrl, Alt or Win (or a standalone key), or it would hijack normal typing.</summary>
        public bool IsValid => IsStandaloneKey || (Modifiers & (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_WIN)) != 0;

        public string DisplayString
        {
            get
            {
                var parts = new List<string>();
                if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
                if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
                if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
                if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
                parts.Add(KeyName(Key));
                return string.Join("+", parts);
            }
        }

        /// <summary>Stored as "key,modifiers".</summary>
        public string Serialize() => Key + "," + Modifiers;

        public static Shortcut? Parse(string s)
        {
            var parts = (s ?? "").Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int key) && int.TryParse(parts[1], out int mods) && key > 0)
            {
                return new Shortcut(key, mods);
            }
            return null;
        }

        private static string KeyName(int vk)
        {
            if (vk >= (int)Keys.D0 && vk <= (int)Keys.D9) return ((char)vk).ToString();
            if (vk >= (int)Keys.A && vk <= (int)Keys.Z) return ((char)vk).ToString();
            if (vk >= (int)Keys.F1 && vk <= (int)Keys.F24) return "F" + (vk - (int)Keys.F1 + 1);
            if (vk >= (int)Keys.NumPad0 && vk <= (int)Keys.NumPad9) return "Num " + (vk - (int)Keys.NumPad0);
            switch ((Keys)vk)
            {
                case Keys.Space: return "Space";
                case Keys.Enter: return "Enter";
                case Keys.Tab: return "Tab";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Escape: return "Esc";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PgUp";
                case Keys.PageDown: return "PgDn";
                case Keys.PrintScreen: return "PrtScn";
                case Keys.Pause: return "Pause";
                case Keys.Multiply: return "Num *";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Decimal: return "Num .";
                case Keys.Divide: return "Num /";
            }
            // Punctuation keys: the character printed on the key in the current keyboard layout.
            uint ch = Native.MapVirtualKey((uint)vk, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFFFFFF;
            return ch != 0 ? char.ToUpperInvariant((char)ch).ToString() : "Key " + vk;
        }

        public bool Equals(Shortcut other) => Key == other.Key && Modifiers == other.Modifiers;
        public override bool Equals(object obj) => obj is Shortcut other && Equals(other);
        public override int GetHashCode() => Key * 31 + Modifiers;
        public static bool operator ==(Shortcut a, Shortcut b) => a.Equals(b);
        public static bool operator !=(Shortcut a, Shortcut b) => !a.Equals(b);
        public override string ToString() => DisplayString;
    }

    /// <summary>Owns the app's system-wide shortcuts (RegisterHotKey: no keyboard hook, no special permission).</summary>
    internal sealed class HotKeyCenter : NativeWindow
    {
        private static HotKeyCenter shared;
        /// <summary>Create on the UI thread.</summary>
        public static HotKeyCenter Shared => shared ?? (shared = new HotKeyCenter());

        private readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        public HashSet<string> Failed { get; } = new HashSet<string>();

        private HotKeyCenter() => CreateHandle(new CreateParams());

        /// <summary>Registers <paramref name="bindings"/>, replacing whatever was registered.</summary>
        public void Register(IEnumerable<(string name, Shortcut? shortcut, Action action)> bindings)
        {
            UnregisterAll();
            Failed.Clear();
            int id = 0;
            foreach (var (name, shortcut, action) in bindings)
            {
                id++;
                if (!shortcut.HasValue) continue;
                var sc = shortcut.Value;
                if (Native.RegisterHotKey(Handle, id, (uint)(sc.Modifiers | Native.MOD_NOREPEAT), (uint)sc.Key))
                {
                    actions[id] = action;
                }
                else
                {
                    Failed.Add(name); // another app already uses this combination
                }
            }
        }

        /// <summary>While recording a new shortcut, our own shortcuts must not fire.</summary>
        public void UnregisterAll()
        {
            foreach (var id in actions.Keys) Native.UnregisterHotKey(Handle, id);
            actions.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && actions.TryGetValue(m.WParam.ToInt32(), out var action))
            {
                action();
                return;
            }
            base.WndProc(ref m);
        }
    }
}
