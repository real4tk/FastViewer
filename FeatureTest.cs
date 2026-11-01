// FastViewer: automated check of the 1.7 features (FastViewer.exe <folder\first file> --features <output folder>).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool featureTest, uiTest, galleryTest, controlsTest;
        int preloadOverride;

        // FastViewer.exe <file> --controls <output folder>: two keys per action, taking keys over, mouse buttons.
        void RunControlsTest()
        {
            Directory.CreateDirectory(snapDir);
            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, Action> at = (ms, a) => steps.Add(new KeyValuePair<int, Action>(ms, a));
            Action<string> log = text => testLog.Add(text);
            Func<string, Shortcut> sc = id => shortcuts.First(s => s.Id == id);
            Func<Key, KeyEventArgs> key = k => new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, k) { RoutedEvent = Keyboard.KeyDownEvent };
            Action<string, int, Key> assign = (id, slot, k) => { StartCapture(sc(id), slot); CaptureKey(key(k)); };
            Func<string, string> keys = id => "[" + string.Join(", ", sc(id).Keys) + "]";
            Func<string, Button> nav = name => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == name).Key;

            at(1200, () => { OpenSettings(); nav("Keyboard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            at(1700, () => Snap("1_keyboard"));
            at(1800, () => nav("Mouse").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            at(2200, () => { Snap("2_mouse"); CloseSettings(); });
            at(2300, () =>
            {
                log("actions: " + shortcuts.Count + ", new ones without a key: " + string.Join(", ", shortcuts.Where(s => s.Keys.Count == 0).Select(s => s.Id)));
                assign("strip", 0, Key.P); log("strip after P in box 1: " + keys("strip"));
                assign("next", 1, Key.N); log("next after N in box 2: " + keys("next") + " (expected Right, N)");
                assign("next", 0, Key.N); log("next after N in box 1 too: " + keys("next") + " (expected N once)");
                RemoveKey(sc("next"), 0); log("next after right-click box 1: " + keys("next") + " (expected none)");
                assign("next", 0, Key.Right); assign("next", 1, Key.PageDown); log("next restored: " + keys("next"));
                assign("strip", 1, Key.G); log("strip took G: strip " + keys("strip") + ", grid " + keys("grid") + " (expected grid without G)");
                bool before = showStrip; HandleShortcut(key(Key.P)); log("P toggles the strip: " + (showStrip != before));
                HandleShortcut(key(Key.G)); log("G now toggles the strip, grid open: " + gridOpen + " (expected False)");
                log("saved key lines: " + string.Join("; ", ShortcutLines()));
            });
            at(2600, () =>
            {
                mouseActions["middle"] = "grid";
                log("middle button set to grid, runs: " + RunMouseAction("middle") + ", grid open " + gridOpen);
                CloseGrid();
                mouseActions["back"] = "none";
                int i = index;
                log("back button set to nothing, runs: " + RunMouseAction("back") + ", file unchanged " + (index == i));
                log("saved mouse lines: " + string.Join("; ", MouseLines()));
            });
            at(2800, () => { int v = files.FindIndex(IsVideoFile); if (v >= 0) ShowIndex(v, 1); });
            at(3800, () =>
            {
                StepSpeed(1); log("faster: " + speed);
                StepSpeed(1); StepSpeed(1); log("faster x3 (stops at 2): " + speed);
                StepSpeed(-1); log("slower: " + speed);
                StepSpeed(0); log("normal: " + speed);
            });
            at(4000, () => { OpenSettings(); nav("Keyboard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            at(4400, () => { Snap("3_keyboard_changed"); nav("Mouse").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            at(4800, () => Snap("4_mouse_changed"));
            at(4900, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
            foreach (var s in steps) After(s.Key, s.Value);
        }

        // FastViewer.exe <file> --strip --gallery <output folder>: the thumbnail strip and the folder grid.
        void RunGalleryTest()
        {
            Directory.CreateDirectory(snapDir);
            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, Action> at = (ms, a) => steps.Add(new KeyValuePair<int, Action>(ms, a));
            Action<string> log = text => testLog.Add(text);
            Func<string> name = () => Path.GetFileName(CurrentFile);
            Action<Key> press = key => HandleGridKey(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(this), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            Action stripState = () =>
            {
                var middle = stripTiles.Count == 0 ? null : stripTiles[stripTiles.Count / 2];
                log("strip: " + stripTiles.Count + " tiles, " + stripTiles.Count(t => t.File != null) + " with a file, middle = " +
                    (middle == null || middle.File == null ? "-" : Path.GetFileName(middle.File)) + ", current = " + name() +
                    ", thumbnails cached " + Thumbs.CachedCount);
            };
            string expected = null;

            at(2500, () => { Snap("1_strip"); stripState(); });
            at(2600, () => Go(1));
            at(2700, () => Go(1));
            at(2800, () => Go(1));
            at(3600, () => { Snap("2_strip_after_3_next"); stripState(); });
            at(3700, () => OpenGrid());
            at(4700, () => { Snap("3_grid_medium"); log("grid: " + gridFiles.Count + " files, " + gridCols + " columns, tile " + gridTile + ", tiles made " + gridShown.Count + ", selected " + Path.GetFileName(gridFiles[gridSelected]) + ", current " + name()); });
            at(4800, () => { press(Key.Right); press(Key.Right); press(Key.Down); log("after Right Right Down: selected #" + gridSelected + " " + Path.GetFileName(gridFiles[gridSelected])); });
            at(4900, () => SetGridSize(0));
            at(5600, () => { Snap("4_grid_small"); log("small: " + gridCols + " columns"); });
            at(5700, () => SetGridSize(2));
            at(6400, () => { Snap("5_grid_large"); log("large: " + gridCols + " columns"); });
            at(6500, () => { expected = Path.GetFileName(gridFiles[gridSelected]); press(Key.Enter); log("Enter opened " + name() + " (expected " + expected + "), grid open " + gridOpen); });
            at(6700, () => { ToggleShuffle(); OpenGrid(); log("shuffle on; grid shows folder order: " + gridFiles.SequenceEqual(sortedFiles) + "; next/previous order differs: " + !files.SequenceEqual(sortedFiles)); });
            at(6900, () => { expected = Path.GetFileName(gridFiles[4]); PickFromGrid(4); log("picked " + expected + " from the grid: now on " + name() + ", shuffle still on " + shuffled); });
            at(7000, () => { var next = files[Wrap(index + 1)]; Go(1); log("next after picking goes along the shuffle: " + (CurrentFile == next)); ToggleShuffle(); });
            at(7300, () => Width = 560);
            at(8000, () => { Snap("6_strip_narrow"); stripState(); });
            at(8100, () => { Width = 1440; ToggleFullscreen(); });
            at(9000, () => Snap("7_fullscreen_strip"));
            at(9100, () => OpenGrid());
            at(9800, () => Snap("8_fullscreen_grid"));
            at(9900, () => { CloseGrid(); ToggleFullscreen(); });
            at(10400, () => OpenSettings());
            at(10900, () => { Snap("9_settings_viewing"); CloseSettings(); });
            at(11000, () => { showStrip = false; ApplyStrip(); log("strip off: " + stripTiles.Count + " tiles, strip " + strip.Visibility); });
            at(11400, () => Snap("10_strip_off"));
            at(11500, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
            foreach (var s in steps) After(s.Key, s.Value);
        }

        void RunUiTest()
        {
            Directory.CreateDirectory(snapDir);
            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, Action> at = (ms, a) => steps.Add(new KeyValuePair<int, Action>(ms, a));
            Func<string, Button> nav = name => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == name).Key;
            at(1200, () => OpenSettings());
            at(1600, () => Snap("1_settings_viewing"));
            at(1700, () => nav("Keyboard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            at(2100, () => Snap("2_settings_keyboard"));
            at(2200, () => nav("Videos").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            at(2600, () => { F<ScrollViewer>("SettingsScroll").ScrollToEnd(); });
            at(2900, () => Snap("3_settings_videos_scrolled"));
            at(3000, () => Width = 640);
            at(3500, () => Snap("4_settings_narrow"));
            at(3600, () => { Width = 1440; CloseSettings(); });
            at(4000, () => ToggleFullscreen());
            at(4800, () => Snap("5_fullscreen_bars"));
            at(4900, () => ToggleFullscreen());
            at(5300, () => { testLog.Add("shortcut keys: next=" + KeyOf("next") + ", rotate=" + KeyOf("rotate") + ", loopStop=" + KeyOf("loopStop") + ", settings=" + KeyOf("settings")); });
            at(5400, () => File.WriteAllText(Path.Combine(Path.GetDirectoryName(CurrentFile), "zz_new_download.jpg.tmp"), "x"));
            at(5500, () => File.Copy(CurrentFile, Path.Combine(Path.GetDirectoryName(CurrentFile), "zz_new_download.jpg"), true));
            at(6800, () => testLog.Add("files after a new file appeared: " + string.Join(", ", files.Select(Path.GetFileName))));
            at(6900, () => File.Delete(Path.Combine(Path.GetDirectoryName(CurrentFile), "zz_new_download.jpg")));
            at(8200, () => testLog.Add("files after it was deleted: " + string.Join(", ", files.Select(Path.GetFileName))));
            at(8300, () => { ToggleShuffle(); testLog.Add("shuffled order: " + string.Join(", ", files.Select(Path.GetFileName))); ToggleShuffle(); testLog.Add("back to normal: " + string.Join(", ", files.Select(Path.GetFileName))); });
            at(8600, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
            foreach (var s in steps) After(s.Key, s.Value);
        }

        void RunFeatureTest()
        {
            Directory.CreateDirectory(snapDir);
            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, Action> at = (ms, a) => steps.Add(new KeyValuePair<int, Action>(ms, a));
            Func<string, int> find = name => files.FindIndex(f => Path.GetFileName(f) == name);
            Action<string> log = text => testLog.Add(text);
            Func<string> pos = () => shown == null ? "-" : shown.Video.Position.TotalSeconds.ToString("0.000");

            at(1500, () => { shown.Video.Position = TimeSpan.FromSeconds(30); log("long video moved to 30 s"); });
            at(2000, () => ShowIndex(find("anim.gif"), 1));
            at(2700, () => Snap("1_gif_a"));
            at(2900, () => { Snap("1_gif_b"); log("gif frames: " + (shown.Gif == null ? "none (still)" : shown.Gif.Frames.Length.ToString())); });
            at(3100, () => ShowIndex(find("long.mp4"), -1));
            at(4000, () => { Snap("2_resumed"); log("back on long video, position " + pos() + " (expected about 30)"); });
            at(4100, () => { if (playing) TogglePlay(); });
            at(4300, () => { log("paused at " + pos() + ", frame length " + shown.FrameSeconds.ToString("0.0000")); StepFrame(1); StepFrame(1); StepFrame(1); });
            at(4700, () => { log("after 3 frames forward: " + pos()); StepFrame(-1); });
            at(5000, () => log("after 1 frame back: " + pos()));
            at(5100, () => { shown.Video.Position = TimeSpan.FromSeconds(10); SetLoopStart(); });
            at(5300, () => { shown.Video.Position = TimeSpan.FromSeconds(11.5); SetLoopEnd(); shown.Video.Position = TimeSpan.FromSeconds(10); TogglePlay(); });
            at(7800, () => { Snap("3_loop"); log("2.5 s later in a 10-11.5 s loop, position " + pos() + " (must be 10-11.5)"); });
            at(7900, () => { ClearLoop(); StartTrim(); });
            at(8300, () => { Snap("4_trim"); EndTrim(); });
            at(8500, () => ShowIndex(find("photo.jpg"), 1));
            at(9100, () => Rotate());
            at(9600, () => { SaveRotationToFile(); });
            at(10200, () => { Snap("5_photo_saved"); log("photo rotation left in viewer: " + (rotations.ContainsKey(shown.File) ? rotations[shown.File].ToString() : "0")); });
            at(10300, () =>
            {
                OpenSettings();
                root.UpdateLayout();
                DependencyObject d = F<FrameworkElement>("SettingsList");
                while (d != null && !(d is ScrollViewer)) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                var viewer = (ScrollViewer)d;
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight - 380);
            });
            at(10800, () => Snap("6_settings"));
            at(11000, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.Contains(" ms  ")));
                Close();
            });
            foreach (var s in steps) After(s.Key, s.Value);
        }
    }
}
