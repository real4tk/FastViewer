// FastViewer: keyboard shortcuts you can change (Settings > Keyboard), and shuffle.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FastViewer
{
    partial class ViewerWindow
    {
        class Shortcut
        {
            public string Id, Name;
            public string[] Defaults;
            public List<string> Keys;
            public Action Run;
            public Button Editor;
        }

        List<Shortcut> shortcuts;
        Dictionary<int, Shortcut> shortcutByKey = new Dictionary<int, Shortcut>();
        readonly Dictionary<string, string> savedKeys = new Dictionary<string, string>(); // id -> "Ctrl+R|F11"
        Shortcut capturing; // waiting for the key to assign in Settings

        Shortcut S(string id, string name, Action run, params string[] keys)
        {
            return new Shortcut { Id = id, Name = name, Run = run, Defaults = keys, Keys = keys.ToList() };
        }

        void InitShortcuts()
        {
            shortcuts = new List<Shortcut>
            {
                S("next", "Next file", delegate { Go(1); }, "Right", "PageDown"),
                S("previous", "Previous file", delegate { Go(-1); }, "Left", "PageUp"),
                S("first", "First file", delegate { if (files.Count > 0) ShowIndex(0, 1); }, "Home"),
                S("last", "Last file", delegate { if (files.Count > 0) ShowIndex(files.Count - 1, -1); }, "End"),
                S("shuffle", "Shuffle on/off", ToggleShuffle, "X"),
                S("grid", "All files in folder (grid)", ToggleGrid, "G"),
                S("play", "Play / pause", TogglePlay, "Space", "K"),
                S("skipBack", "Skip back (length in Videos)", delegate { SeekBy(-skipBack); }, "J"),
                S("skipForward", "Skip forward (length in Videos)", delegate { SeekBy(skipForward); }, "L"),
                S("back5", "Back 5 seconds", delegate { SeekBy(-5); }, "Shift+Left"),
                S("forward5", "Forward 5 seconds", delegate { SeekBy(5); }, "Shift+Right"),
                S("frameBack", "Previous frame", delegate { StepFrame(-1); }, "OemComma"),
                S("frameForward", "Next frame", delegate { StepFrame(1); }, "OemPeriod"),
                S("loopA", "Loop from here (A)", SetLoopStart, "OemOpenBrackets"),
                S("loopB", "Loop until here (B)", SetLoopEnd, "OemCloseBrackets"),
                S("loopStop", "Stop A-B loop", ClearLoop, "OemPipe", "OemBackslash"),
                S("repeat", "Repeat video on/off", ToggleLoop, "R"),
                S("volumeUp", "Volume up", delegate { SetVolume(volume + 0.05); }, "Up"),
                S("volumeDown", "Volume down", delegate { SetVolume(volume - 0.05); }, "Down"),
                S("mute", "Mute", ToggleMute, "M"),
                S("trim", "Trim", StartTrim, "Ctrl+T"),
                S("rotate", "Rotate", Rotate, "Ctrl+R"),
                S("zoomIn", "Zoom in", delegate { ZoomBy(1.25, StageCenter()); }, "OemPlus", "Add"),
                S("zoomOut", "Zoom out", delegate { ZoomBy(0.8, StageCenter()); }, "OemMinus", "Subtract"),
                S("fit", "Fit to window", delegate { SetZoom(1, StageCenter()); }, "D0", "NumPad0"),
                S("fullscreen", "Full screen", ToggleFullscreen, "F", "F11"),
                S("mini", "Mini player", ToggleMini, "Ctrl+M"),
                S("slideshow", "Slideshow", ToggleSlideshow, "F5"),
                S("info", "File info", ToggleInfo, "I"),
                S("open", "Open", OpenDialog, "Ctrl+O"),
                S("saveAs", "Save as", SaveAs, "Ctrl+S"),
                S("copy", "Copy", CopyFile, "Ctrl+C"),
                S("delete", "Delete", AskDelete, "Delete"),
                S("settings", "Settings", OpenSettings, "Ctrl+OemComma"),
            };
            foreach (var s in shortcuts)
            {
                string keys;
                if (savedKeys.TryGetValue(s.Id, out keys)) s.Keys = keys.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            RebuildShortcutMap();
        }

        void RebuildShortcutMap()
        {
            shortcutByKey.Clear();
            foreach (var s in shortcuts)
                foreach (var k in s.Keys)
                {
                    int code = Parse(k);
                    if (code != 0 && !shortcutByKey.ContainsKey(code)) shortcutByKey[code] = s;
                }
        }

        IEnumerable<string> ShortcutLines()
        {
            if (shortcuts == null) return savedKeys.Select(p => "key." + p.Key + "=" + p.Value);
            return shortcuts.Where(s => !s.Keys.SequenceEqual(s.Defaults)).Select(s => "key." + s.Id + "=" + string.Join("|", s.Keys));
        }

        // ---- key names: "Ctrl+Shift+R" <-> number (key + modifier bits), and what to show people

        static int Parse(string text)
        {
            int mods = 0;
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "Ctrl") mods |= 1;
                else if (parts[i] == "Shift") mods |= 2;
                else if (parts[i] == "Alt") mods |= 4;
            }
            Key key;
            if (!Enum.TryParse(parts[parts.Length - 1], out key)) return 0;
            return (int)key | (mods << 16);
        }

        static int Code(Key key, ModifierKeys modifiers)
        {
            int mods = ((modifiers & ModifierKeys.Control) != 0 ? 1 : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 2 : 0) |
                       ((modifiers & ModifierKeys.Alt) != 0 ? 4 : 0);
            return (int)key | (mods << 16);
        }

        static string KeyName(Key key, ModifierKeys modifiers)
        {
            string m = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") +
                       ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "");
            return m + key;
        }

        static string Display(string text)
        {
            int plus = text.LastIndexOf('+');
            string mods = plus > 0 && plus < text.Length - 1 ? text.Substring(0, plus + 1) : "";
            string key = text.Substring(mods.Length);
            switch (key)
            {
                case "OemComma": key = ","; break;
                case "OemPeriod": key = "."; break;
                case "OemOpenBrackets": key = "["; break;
                case "OemCloseBrackets": key = "]"; break;
                case "OemPipe": case "OemBackslash": key = "\\"; break;
                case "OemPlus": key = "+"; break;
                case "OemMinus": key = "-"; break;
                case "Add": key = "Num +"; break;
                case "Subtract": key = "Num -"; break;
                case "Next": case "PageDown": key = "Page Down"; break;
                case "Prior": case "PageUp": key = "Page Up"; break;
                case "Return": key = "Enter"; break;
                default:
                    if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key.Substring(1);
                    else if (key.StartsWith("NumPad")) key = "Num " + key.Substring(6);
                    break;
            }
            return mods + key;
        }

        // The first key of an action, for showing in menus ("" if none).
        string KeyOf(string id)
        {
            var s = shortcuts == null ? null : shortcuts.FirstOrDefault(x => x.Id == id);
            return s == null || s.Keys.Count == 0 ? "" : Display(s.Keys[0]);
        }

        // Runs the action for this key; false if the key isn't a shortcut.
        bool HandleShortcut(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            Shortcut s;
            if (!shortcutByKey.TryGetValue(Code(key, Keyboard.Modifiers), out s)) return false;
            s.Run();
            return true;
        }

        // ---- Settings > Keyboard

        void AddShortcutCards()
        {
            var reset = new Button { Style = (Style)Application.Current.Resources["Filled"], Content = "Reset all", MinWidth = 120, Height = 32 };
            reset.Click += delegate
            {
                foreach (var s in shortcuts) s.Keys = s.Defaults.ToList();
                RebuildShortcutMap();
                RefreshShortcutEditors();
                SaveSettings();
            };
            AddCard("", "Keyboard shortcuts",
                "Click a key to change it, then press the new key (with Ctrl, Shift or Alt if you like). Esc cancels. " +
                "A key can only do one thing: giving it to a new action takes it away from the old one.", reset);
            foreach (var s in shortcuts)
            {
                var sc = s;
                sc.Editor = new Button { Style = (Style)Application.Current.Resources["Filled"], MinWidth = 160, Height = 32 };
                sc.Editor.Click += delegate { StartCapture(sc); };
                AddCard("", sc.Name, null, sc.Editor);
            }
            RefreshShortcutEditors();
        }

        void RefreshShortcutEditors()
        {
            foreach (var s in shortcuts)
                if (s.Editor != null)
                    s.Editor.Content = s == capturing ? "Press a key..." : s.Keys.Count == 0 ? "(none)" : string.Join("  or  ", s.Keys.Select(Display));
        }

        void StartCapture(Shortcut s)
        {
            capturing = s;
            RefreshShortcutEditors();
            Focus();
        }

        // While choosing a key in Settings: the next key press becomes the shortcut.
        bool CaptureKey(KeyEventArgs e)
        {
            if (capturing == null) return false;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin) return true; // wait for the real key
            var s = capturing;
            capturing = null;
            if (key != Key.Escape)
            {
                string name = KeyName(key, Keyboard.Modifiers);
                int code = Parse(name);
                string takenFrom = null;
                foreach (var other in shortcuts)
                    if (other != s && other.Keys.RemoveAll(k => Parse(k) == code) > 0) takenFrom = other.Name;
                s.Keys = new List<string> { name };
                RebuildShortcutMap();
                SaveSettings();
                if (takenFrom != null) ShowToast(Display(name) + " was used for \"" + takenFrom + "\"; that action has no key now.", null, null, 6, null);
            }
            RefreshShortcutEditors();
            return true;
        }

        // ---------------------------------------------------------------- shuffle

        bool shuffled;
        List<string> sortedFiles; // the normal order, to go back to when shuffle is switched off

        void ToggleShuffle()
        {
            if (files.Count < 2) return;
            string current = CurrentFile;
            if (!shuffled)
            {
                sortedFiles = new List<string>(files);
                var random = new Random();
                var rest = files.Where(f => f != current).OrderBy(f => random.Next()).ToList();
                files = new List<string> { current };
                files.AddRange(rest);
                shuffled = true;
            }
            else
            {
                var still = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                files = (sortedFiles ?? files).Where(still.Contains).ToList();
                shuffled = false;
            }
            index = Math.Max(0, files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase)));
            ShowIndex(index, 1); // loads the new neighbours
            ShowToast(shuffled ? "Shuffle on: next and previous go through this folder in random order." : "Shuffle off.", null, null, 3, "shuffle");
        }
    }
}
