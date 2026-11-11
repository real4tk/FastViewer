// FastViewer: keyboard shortcuts you can change (Settings > Keyboard, two keys per action), what the
// mouse buttons do (Settings > Mouse), and shuffle. Esc always means "go back" and can't be changed,
// so you can never get stuck in full screen.

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
            public Button[] Editors; // the two key boxes in Settings
        }

        const int KeysPerAction = 2;
        List<Shortcut> shortcuts;
        Dictionary<int, Shortcut> shortcutByKey = new Dictionary<int, Shortcut>();
        readonly Dictionary<string, string> savedKeys = new Dictionary<string, string>(); // id -> "Ctrl+R|F11"
        Shortcut capturing; // waiting for the key to assign in Settings
        int capturingSlot;  // ...into its first or second key box

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
                S("strip", "Thumbnail strip on/off", ToggleStrip),
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
                S("speedDown", "Slower", delegate { StepSpeed(-1); }),
                S("speedUp", "Faster", delegate { StepSpeed(1); }),
                S("speedNormal", "Normal speed", delegate { StepSpeed(0); }),
                S("trim", "Trim", StartTrim, "Ctrl+T"),
                S("crop", "Crop", ToggleCrop, "C"),
                S("saveFrame", "Save a frame", SaveFrame),
                S("copyFrame", "Copy frame", CopyFrame, "Ctrl+Shift+C"),
                S("compatible", "Make compatible copy", MakeCompatibleCopy),
                S("subtitles", "Subtitles on/off", ToggleSubtitles, "S"),
                S("autoCaptions", "Make captions automatically", MakeCaptions),
                S("toJpg", "Convert to JPG", delegate { ConvertPhoto(".jpg"); }),
                S("toPng", "Convert to PNG", delegate { ConvertPhoto(".png"); }),
                S("rotate", "Rotate right", Rotate, "Ctrl+R"),
                S("rotateLeft", "Rotate left", RotateLeft, "Ctrl+Shift+R"),
                S("saveRotation", "Save rotation to file", SaveRotationToFile),
                S("zoomIn", "Zoom in", delegate { ZoomBy(1.25, StageCenter()); }, "OemPlus", "Add"),
                S("zoomOut", "Zoom out", delegate { ZoomBy(0.8, StageCenter()); }, "OemMinus", "Subtract"),
                S("fit", "Fit to window", delegate { SetZoom(1, StageCenter()); }, "D0", "NumPad0"),
                S("actualSize", "Actual size (100%)", delegate { ZoomToPercent(100); }),
                S("fullscreen", "Full screen", ToggleFullscreenSmoothly, "F", "F11"),
                S("mini", "Mini player", ToggleMiniSmoothly, "Ctrl+M"),
                S("pin", "Keep on top on/off", TogglePin),
                S("slideshow", "Slideshow", ToggleSlideshow, "F5"),
                S("info", "File info", ToggleInfo, "I"),
                S("open", "Open", OpenDialog, "Ctrl+O"),
                S("saveAs", "Save as", SaveAs, "Ctrl+S"),
                S("copy", "Copy", CopyFile, "Ctrl+C"),
                S("copyPath", "Copy file path", CopyPath),
                S("showInFolder", "Show in folder", ShowInFolder),
                S("openWith", "Open with", OpenWith),
                S("delete", "Delete", AskDelete, "Delete"),
                S("settings", "Settings", OpenSettings, "Ctrl+OemComma"),
                S("close", "Close window", Close),
            };
            ApplySavedKeys();
        }

        // The keys from settings.ini (actions without a saved line keep their default keys).
        void ApplySavedKeys()
        {
            foreach (var s in shortcuts)
            {
                string keys;
                s.Keys = savedKeys.TryGetValue(s.Id, out keys)
                    ? keys.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Take(KeysPerAction).ToList()
                    : s.Defaults.ToList();
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
            UpdateShortcutHints();
        }

        // Button -> what it does and its action: the tooltip shows the action's key as it is now (keys can be
        // changed in Settings), and a screen reader reads the words.
        static readonly string[,] ButtonHints =
        {
            { "OpenBtn", "Open", "open" }, { "DeleteBtn", "Delete", "delete" }, { "CopyBtn", "Copy", "copy" },
            { "RotateBtn", "Rotate", "rotate" }, { "MoreBtn", "See more", "" }, { "MiniBtn", "Mini player: just the video, always on top", "mini" },
            { "OpenWithBtn", "Open with another app", "openWith" }, { "MinBtn", "Minimize", "" }, { "MaxBtn", "Maximize", "" },
            { "CloseBtn", "Close", "close" }, { "PrevBtn", "Previous", "previous" }, { "NextBtn", "Next", "next" },
            { "PlayBtn", "Play / Pause", "play" }, { "MuteBtn", "Mute", "mute" }, { "LoopBtn", "Repeat", "repeat" },
            { "VideoMoreBtn", "More video options", "" }, { "GridBtn", "All files in this folder", "grid" },
            { "InfoBtn", "File info", "info" }, { "FitBtn", "Fit to window", "fit" }, { "ZoomBtn", "Zoom", "" },
            { "ZoomOutBtn", "Zoom out", "zoomOut" }, { "ZoomInBtn", "Zoom in", "zoomIn" }, { "FullBtn", "Full screen", "fullscreen" },
            { "InfoCloseBtn", "Close file info", "info" }, { "MiniExitBtn", "Back to the full window", "mini" }, { "MiniVolBtn", "Mute (scroll for volume)", "mute" }, { "MiniCloseBtn", "Close", "close" },
            { "SettingsBackBtn", "Back (Esc)", "" }, { "GridBackBtn", "Back (Esc)", "" }, { "ToastClose", "Close message", "" },
            { "TrimPlayBtn", "Play the part between the handles", "play" }, { "TrimSaveBtn", "Save the part between the handles as a new video (Enter)", "" }, { "TrimCancelBtn", "Leave without saving (Esc)", "" },
            { "TrimGifBtn", "Save the part between the handles as a GIF", "" }, { "TrimShareBtn", "Make a small copy for Discord and copy it, ready to paste", "" },
            { "CropSaveBtn", "Keep only the part in the frame; the original goes to the Recycle Bin (Enter)", "" }, { "CropCopyBtn", "Save the part in the frame as a new file", "" }, { "CropCancelBtn", "Leave without cropping (Esc)", "" },
            { "CropRatioBtn", "Hold a shape while dragging", "" }, { "CropResetBtn", "Back to the whole picture", "" },
            { "SubBtn", "Subtitles", "subtitles" },
            { "GridSizeBtn", "Thumbnail size (Ctrl + mouse wheel)", "" }
        };

        static string WithKey(string text, string key) { return key.Length == 0 ? text : text + " (" + key + ")"; }

        void UpdateShortcutHints()
        {
            if (root == null) return;
            for (int i = 0; i < ButtonHints.GetLength(0); i++)
            {
                var b = F<Button>(ButtonHints[i, 0]);
                if (b == null) continue;
                string text = ButtonHints[i, 1], id = ButtonHints[i, 2];
                if (b.Name == "MaxBtn" && WindowState == WindowState.Maximized) text = "Restore";
                b.ToolTip = id.Length == 0 ? text : WithKey(text, KeyOf(id));
                System.Windows.Automation.AutomationProperties.SetName(b, text.Replace(" (Esc)", ""));
            }
            var vol = F<Button>("VolBtn");
            vol.ToolTip = WithKey("Mute", KeyOf("mute")) + ". Point at it for the volume slider";
            System.Windows.Automation.AutomationProperties.SetName(vol, "Mute");
            SetSlideshowButton();
            UpdateSkipButtons();
            UpdateRotateButton();
            centerPlayBtn.ToolTip = WithKey(playing ? "Pause" : "Play", KeyOf("play"));
            System.Windows.Automation.AutomationProperties.SetName(centerPlayBtn, playing ? "Pause" : "Play");
            foreach (var s in new[] { new[] { "Seek", "Position in the video" }, new[] { "Vol", "Volume" }, new[] { "ZoomSlider", "Zoom" } })
                System.Windows.Automation.AutomationProperties.SetName(F<FrameworkElement>(s[0]), s[1]);
            if (files.Count == 0) message.Text = EmptyMessage();
        }

        // What an empty window says (also names the key that opens a file, if it has one).
        string EmptyMessage()
        {
            string key = KeyOf("open");
            return "Drop a photo, video or folder here\nor " + (key.Length > 0 ? "press " + key : "click Open") + " to open one";
        }

        static readonly Dictionary<string, string> ActionIcons = new Dictionary<string, string>
        {
            { "next", "" }, { "previous", "" }, { "first", "" }, { "last", "" }, { "shuffle", "" },
            { "grid", "" }, { "strip", "" }, { "play", "" }, { "skipBack", "" }, { "skipForward", "" },
            { "back5", "" }, { "forward5", "" }, { "frameBack", "" }, { "frameForward", "" },
            { "loopA", "\uE892" }, { "loopB", "\uE893" }, { "loopStop", "" }, { "repeat", "" }, { "volumeUp", "" },
            { "volumeDown", "" }, { "mute", "" }, { "speedDown", "" }, { "speedUp", "" }, { "speedNormal", "" },
            { "trim", "" }, { "crop", "" }, { "saveFrame", "" }, { "compatible", "\uE895" }, { "rotate", "" }, { "saveRotation", "" },
            { "zoomIn", "" }, { "zoomOut", "" }, { "fit", "" }, { "actualSize", "" }, { "fullscreen", "" },
            { "mini", "" }, { "pin", "" }, { "slideshow", "" }, { "info", "" }, { "open", "" },
            { "saveAs", "" }, { "copy", "" }, { "copyPath", "" }, { "showInFolder", "" }, { "openWith", "" },
            { "subtitles", "\uE7F0" }, { "autoCaptions", "\uE720" }, { "toJpg", "\uE8AB" }, { "toPng", "\uE8AB" },
            { "rotateLeft", "\uE7AD" },
            { "copyFrame", "\uEB9F" }, { "delete", "" }, { "settings", "" }, { "close", "" }
        };

        IEnumerable<string> ShortcutLines()
        {
            if (shortcuts == null) return savedKeys.Select(p => "key." + p.Key + "=" + p.Value);
            return shortcuts.Where(s => !s.Keys.Select(Parse).SequenceEqual(s.Defaults.Select(Parse))) // PageDown and Next are the same key
                            .Select(s => "key." + s.Id + "=" + string.Join("|", s.Keys));
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
            string typed = KeyChar(key);
            if (typed != null) return mods + typed; // punctuation keys: the character this keyboard layout puts there
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint code, uint mapType);

        // "Oem3", "OemComma"...: the character on that key with the keyboard layout in use (` on a US keyboard,
        // @ on a Dutch one), or null for other keys.
        static string KeyChar(string key)
        {
            Key k;
            if (!key.StartsWith("Oem") || key == "OemPlus" || !Enum.TryParse(key, out k)) return null; // (OemPlus: "+", the zoom key, though it types "=")
            try
            {
                uint c = MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(k), 2) & 0xFFFF; // MAPVK_VK_TO_CHAR (dead keys too)
                if (c < 32) return null;
                return ((char)c).ToString().ToUpperInvariant();
            }
            catch { return null; }
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
            AddCard("\uE765", "Keyboard shortcuts",
                "Every action can have two keys. Click a box, then press the new key (with Ctrl, Shift or Alt if you like); " +
                "Esc cancels. Right-click a box to remove that key. A key can only do one thing: giving it to a new action " +
                "takes it away from the old one. Esc itself always goes back (leaves full screen, the mini player, the grid " +
                "or Settings), so it can't be changed.", reset);
            foreach (var s in shortcuts)
            {
                var sc = s;
                var boxes = new StackPanel { Orientation = Orientation.Horizontal };
                sc.Editors = new Button[KeysPerAction];
                for (int k = 0; k < KeysPerAction; k++)
                {
                    int slot = k;
                    var b = new Button
                    {
                        Style = (Style)Application.Current.Resources["Filled"], MinWidth = 130, Height = 32,
                        Margin = new Thickness(k == 0 ? 0 : 8, 0, 0, 0), ToolTip = "Click to change; right-click (or Delete) to remove"
                    };
                    b.SetResourceReference(BorderBrushProperty, "OutlineEdge"); // an outline in every look (some looks draw buttons without one), so a key box reads as a box
                    b.Click += delegate { StartCapture(sc, slot); };
                    b.MouseRightButtonUp += delegate(object o, MouseButtonEventArgs e) { RemoveKey(sc, slot); e.Handled = true; };
                    b.KeyDown += delegate(object o, KeyEventArgs e) // the keyboard's way to remove a key
                    {
                        if (capturing == null && (e.Key == Key.Delete || e.Key == Key.Back)) { RemoveKey(sc, slot); e.Handled = true; }
                    };
                    System.Windows.Automation.AutomationProperties.SetName(b, sc.Name + ", key " + (slot + 1));
                    sc.Editors[k] = b;
                    boxes.Children.Add(b);
                }
                string icon;
                var keyCard = AddCard(ActionIcons.TryGetValue(sc.Id, out icon) ? icon : "", sc.Name, null, boxes);
                if (sc.Id == "rotateLeft") FlipCardIcon(keyCard);
            }
            RefreshShortcutEditors();
        }

        void RefreshShortcutEditors()
        {
            foreach (var s in shortcuts)
            {
                if (s.Editors == null) continue;
                for (int k = 0; k < s.Editors.Length; k++)
                {
                    // A new key goes into the first free box, so an empty box always comes after the set ones.
                    bool waiting = s == capturing && Math.Min(capturingSlot, s.Keys.Count) == k;
                    s.Editors[k].Content = waiting ? "Press a key..." : k < s.Keys.Count ? Display(s.Keys[k]) : "+"; // an empty box: just a quiet plus (the page had "Not set" some thirty times)
                    s.Editors[k].SetResourceReference(ForegroundProperty, waiting || k < s.Keys.Count ? "TextMain" : "TextDim");
                    s.Editors[k].ToolTip = k < s.Keys.Count ? "Click to change; right-click (or Delete) to remove" : "Click, then press a key to add one";
                }
            }
        }

        void StartCapture(Shortcut s, int slot)
        {
            capturing = s;
            capturingSlot = slot;
            RefreshShortcutEditors();
            Focus();
        }

        void RemoveKey(Shortcut s, int slot)
        {
            if (capturing != null)
            {
                capturing = null;
                RefreshShortcutEditors();
            }
            if (slot >= s.Keys.Count) return;
            s.Keys.RemoveAt(slot);
            RebuildShortcutMap();
            RefreshShortcutEditors();
            SaveSettings();
        }

        // While choosing a key in Settings: the next key press becomes the shortcut.
        bool CaptureKey(KeyEventArgs e)
        {
            if (capturing == null) return false;
            if (!settingsOpen) { capturing = null; return false; } // only while Settings shows the key box
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab) // Tab moves on through Settings (keyboard use), it isn't taken as a shortcut
            {
                capturing = null;
                RefreshShortcutEditors();
                return false;
            }
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
                int slot = Math.Min(capturingSlot, s.Keys.Count);
                if (slot < s.Keys.Count) s.Keys[slot] = name;
                else s.Keys.Add(name);
                for (int k = s.Keys.Count - 1; k >= 0; k--) // the same key in both boxes: keep one
                    if (k != slot && Parse(s.Keys[k]) == code) s.Keys.RemoveAt(k);
                RebuildShortcutMap();
                SaveSettings();
                if (takenFrom != null) ShowToast(Display(name) + " was used for \"" + takenFrom + "\"; that action has no key now.", null, null, 6, null);
            }
            RefreshShortcutEditors();
            return true;
        }

        // ---------------------------------------------------------------- actions that only had a menu item

        void CopyPath()
        {
            if (CurrentFile == null) return;
            try { Clipboard.SetText(CurrentFile); }
            catch { }
        }

        void ToggleStrip()
        {
            showStrip = !showStrip;
            ApplyStrip();
            SaveSettings();
            ShowToast(showStrip ? "Thumbnail strip on." : "Thumbnail strip off.", null, null, 2, "strip");
        }

        // Every speed FastViewer offers; Settings > Videos > "Speeds in the menu" picks which ones are shown (1x always is).
        static readonly double[] AllSpeeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };
        static readonly double[] DefaultSpeeds = { 0.5, 1, 1.5, 2 };
        readonly HashSet<double> shownSpeeds = new HashSet<double>(DefaultSpeeds);

        double[] Speeds { get { return AllSpeeds.Where(x => x == 1 || shownSpeeds.Contains(x)).ToArray(); } }

        static string SpeedText(double s) { return s.ToString(CultureInfo.InvariantCulture) + "x"; }

        void LoadSpeeds(string v)
        {
            shownSpeeds.Clear();
            foreach (var part in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double d;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && AllSpeeds.Contains(d)) shownSpeeds.Add(d);
            }
            shownSpeeds.Add(1);
        }

        string SpeedsLine() { return "speeds=" + string.Join(",", Speeds.Select(x => x.ToString(CultureInfo.InvariantCulture))); }

        // -1 slower, 1 faster, 0 back to normal (the same steps as the video "..." menu).
        void StepSpeed(int direction)
        {
            if (shown == null || !shown.IsVideo) return;
            var list = Speeds;
            double s = direction == 0 ? 1
                : direction > 0 ? list.Where(x => x > speed + 0.01).DefaultIfEmpty(speed).First()
                : list.Where(x => x < speed - 0.01).DefaultIfEmpty(speed).Last();
            SetSpeed(s);
            ShowToast("Speed " + SpeedText(s), null, null, 2, "speed");
        }

        // ---------------------------------------------------------------- mouse buttons (Settings > Mouse)

        // Button -> shortcut id, "none" for nothing. A click on a video can only play/pause or do nothing:
        // anything else would also run on the first click of every double-click.
        readonly Dictionary<string, string> mouseActions = new Dictionary<string, string>
        {
            { "back", "previous" }, { "forward", "next" }, { "middle", "none" }, { "double", "fullscreen" }, { "click", "play" }
        };
        static readonly string[] MouseChoices =
        {
            "none", "previous", "next", "play", "skipBack", "skipForward", "mute", "fullscreen", "mini", "grid",
            "rotate", "fit", "info", "shuffle", "slideshow", "pin", "showInFolder", "openWith", "close"
        };

        void ResetMouseActions()
        {
            mouseActions["back"] = "previous";
            mouseActions["forward"] = "next";
            mouseActions["middle"] = "none";
            mouseActions["double"] = "fullscreen";
            mouseActions["click"] = "play";
        }

        bool RunMouseAction(string button)
        {
            string id;
            if (shortcuts == null || !mouseActions.TryGetValue(button, out id) || id == "none") return false;
            var s = shortcuts.FirstOrDefault(x => x.Id == id);
            if (s == null) return false;
            s.Run();
            return true;
        }

        IEnumerable<string> MouseLines()
        {
            return mouseActions.Select(p => "mouse." + p.Key + "=" + p.Value);
        }

        void AddMouseCards()
        {
            AddCard("", "Mouse wheel",
                "What the mouse wheel does when viewing a file. Ctrl + mouse wheel always zooms.",
                Dropdown(new[] { "View next or previous items", "Zoom in or out" },
                    () => wheelZooms ? 1 : 0, i => wheelZooms = i == 1));
            MouseCard("", "back", "Back button", "The side button nearest you (on most mice). In the grid and in Settings it always goes back.");
            MouseCard("", "forward", "Forward button", "The other side button.");
            MouseCard("", "middle", "Middle button", "Pressing the mouse wheel down.");
            MouseCard("", "double", "Double-click the picture", "In the mini player, \"Full screen\" takes you back to the normal window.");
            AddCard("", "Click a video",
                "A single click on a playing or paused video.",
                Dropdown(new[] { "Play / pause", "Nothing" },
                    () => mouseActions["click"] == "none" ? 1 : 0, i => mouseActions["click"] = i == 0 ? "play" : "none"));
        }

        void MouseCard(string glyph, string button, string title, string description)
        {
            var names = MouseChoices.Select(id => id == "none" ? "Nothing" : shortcuts.First(s => s.Id == id).Name).ToArray();
            AddCard(glyph, title, description, Dropdown(names,
                () => Math.Max(0, Array.IndexOf(MouseChoices, mouseActions[button])),
                i => mouseActions[button] = MouseChoices[i]));
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
