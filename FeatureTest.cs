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
        bool featureTest, uiTest, galleryTest, controlsTest, tweaksTest, memoryTest, stallTest, looksTest, volumeTest, dimTest, syncTest, toastTest;
        int preloadOverride;

        // FastViewer.exe <file> --toast <output folder>: the pop-up message ("Update now") must be clickable in every
        // look and in full screen, not covered by a floating top bar. Checks what really sits under its buttons.
        void RunToastTest()
        {
            Directory.CreateDirectory(snapDir);
            Action<string> log = text => testLog.Add(text);
            Func<FrameworkElement, bool> reachable = el =>
            {
                var p = el.TranslatePoint(new Point(el.ActualWidth / 2, el.ActualHeight / 2), root);
                var hit = root.InputHitTest(p) as DependencyObject;
                while (hit != null && hit != el) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit) ?? LogicalTreeHelper.GetParent(hit);
                return hit == el;
            };
            int t = 1500;
            foreach (var look in new[] { "Photos", "Clean", "Glass", "Midnight" })
                foreach (var full in new[] { false, true })
                {
                    var id = look;
                    var f = full;
                    After(t += 200, () =>
                    {
                        lookId = id; lookMode = "Dark"; ApplyLook();
                        if (f != fullscreen) ToggleFullscreen();
                        ShowToast("FastViewer 1.15.1 is available.", "Update now", delegate { }, 0, "update");
                    });
                    After(t += 600, () =>
                    {
                        var toast = F<FrameworkElement>("Toast");
                        double top = toast.TranslatePoint(new Point(0, 0), root).Y;
                        log(string.Format("{0,-8} {1,-11}: message at y {2:0} (window caption {3:0}), \"Update now\" clickable {4}, close clickable {5}",
                            id, f ? "full screen" : "window", top, chrome.CaptionHeight, reachable(F<FrameworkElement>("ToastBtn")), reachable(F<FrameworkElement>("ToastClose"))));
                        Snap(id + (f ? "_fullscreen" : "_window"));
                    });
                }
            After(t += 200, () =>
            {
                if (fullscreen) ToggleFullscreen();
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ") && !l.StartsWith("look ")));
                Close();
            });
        }

        // FastViewer.exe <video> --settings <scratch .ini> --sync <output folder>: another FastViewer window (played by
        // this test, writing the scratch file) changes settings; this window must take them over when it comes to the
        // front, and closing must keep them (plus this window's own volume).
        void RunSyncTest()
        {
            Directory.CreateDirectory(snapDir);
            Action<string> log = text => testLog.Add(text);
            Func<string> state = () => "dim " + (dimVideo ? "on" : "off") + ", scrim " + scrim.Visibility + ", look " + lookId + " (" + Looks.Current.Name + ")" +
                ", Copy button " + F<FrameworkElement>("CopyBtn").Visibility + ", next keys [" + string.Join(", ", shortcuts.First(s => s.Id == "next").Keys) +
                "], middle button " + mouseActions["middle"];
            Action<string[]> otherWindowSaves = lines =>
            {
                var all = File.ReadAllLines(SettingsFile).Where(l => !lines.Any(n => l.StartsWith(n.Split('=')[0] + "="))).Concat(lines).ToArray();
                File.WriteAllLines(SettingsFile, all);
                File.SetLastWriteTimeUtc(SettingsFile, DateTime.UtcNow.AddSeconds(2));
            };
            int t = 1800;
            After(t, () => { if (playing) TogglePlay(); log("start: " + state()); });
            After(t += 200, () =>
            {
                otherWindowSaves(new[] { "dim=0", "look=Midnight", "hide=CopyBtn", "key.next=N", "mouse.middle=grid" });
                log("other window saved: dim off, Midnight, Copy hidden, next = N, middle = grid");
                log("before coming to the front: " + state());
                SyncSettings(); // what happens when this window is activated
                log("after coming to the front: " + state());
            });
            After(t += 600, () => Snap("1_after_sync"));
            After(t += 100, () =>
            {
                int before = settingsRefresh.Count;
                volume = 0.33; // this window's own change (saved only when it closes)
                otherWindowSaves(new[] { "dim=1", "look=Clean" });
                log("other window saved again: dim on, Clean; this window's volume set to 0.33, then it closes");
                SaveSettings(true);
                log("file after closing: " + string.Join(" | ", File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("dim=") || l.StartsWith("look=") || l.StartsWith("volume=") || l.StartsWith("hide="))));
            });
            After(t += 200, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
        }

        // FastViewer.exe <video> --dim <output folder>: with "Dim video" off, does the video get darker while the
        // controls show? Every look, controls shown and hidden (and dim on for comparison), saved as pictures.
        void RunDimTest()
        {
            Directory.CreateDirectory(snapDir);
            if (settingsOverride)
            {
                After(1500, () =>
                {
                    dimVideo = false;
                    SaveSettings();
                    testLog.Add("saved to " + SettingsFile + ": " + (File.Exists(SettingsFile) ? string.Join(" | ", File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("dim=") || l.StartsWith("look="))) : "NOT WRITTEN"));
                    File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.Contains(" ms  ")));
                    Close();
                });
                return;
            }
            Action<string> log = text => testLog.Add(text);
            Action<bool> controls = show =>
            {
                overlay.BeginAnimation(OpacityProperty, null);
                overlay.Opacity = show ? 1 : 0;
                overlayVisible = show;
                foreach (var name in new[] { "TopBar", "BottomBar" })
                {
                    var bar = F<FrameworkElement>(name);
                    bar.BeginAnimation(OpacityProperty, null);
                    if (Floating) bar.Opacity = show ? 1 : 0;
                }
            };
            int t = 1800;
            After(t, () => { if (playing) TogglePlay(); });
            foreach (var look in new[] { "Photos", "Clean", "Glass", "Midnight" })
                foreach (var dim in new[] { false, true })
                {
                    var id = look;
                    var d = dim;
                    After(t += 200, () => { lookId = id; lookMode = "Dark"; ApplyLook(); dimVideo = d; UpdateScrim(); controls(true); });
                    After(t += 500, () => { Snap(id + "_dim" + (d ? "On" : "Off") + "_controls"); log(id + " dim " + (d ? "on" : "off") + ": scrim " + scrim.Visibility + ", shown slot opacity " + (shown == null ? "-" : shown.Host.Opacity.ToString("0.00"))); });
                    After(t += 100, () => controls(false));
                    After(t += 500, () => Snap(id + "_dim" + (d ? "On" : "Off") + "_none"));
                }
            After(t += 200, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
        }

        // FastViewer.exe <video> --volume <output folder>: the volume slider on hover. The mouse moving in and out is
        // simulated (the real pointer isn't touched) and the slider isn't shown, only what it would do is logged.
        // The video is paused first, so unmuting stays silent.
        void RunVolumeTest()
        {
            Directory.CreateDirectory(snapDir);
            Action<string> log = text => testLog.Add(text);
            var panel = (FrameworkElement)volPopup.Child;
            Action<UIElement, RoutedEvent> mouse = (el, ev) => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = ev });
            int t = 1500;
            After(t, () => { if (playing) TogglePlay(); log("paused: " + !playing + ", muted at start: " + muted); });
            After(t += 100, () => mouse(volBtn, Mouse.MouseEnterEvent));
            After(t += 80, () => log("pointer on the speaker 80 ms: slider open " + VolumeSliderOpen + " (expected False, waits a moment)"));
            After(t += 170, () => log("pointer on the speaker 250 ms: slider open " + VolumeSliderOpen + " (expected True)"));
            After(t += 50, () => mouse(volBtn, Mouse.MouseLeaveEvent));
            After(t += 250, () => log("left 250 ms ago: slider open " + VolumeSliderOpen + " (expected True, a short grace)"));
            After(t += 500, () => log("left 750 ms ago: slider open " + VolumeSliderOpen + " (expected False)"));
            After(t += 100, () => mouse(volBtn, Mouse.MouseEnterEvent));
            After(t += 300, () => { mouse(volBtn, Mouse.MouseLeaveEvent); mouse(panel, Mouse.MouseEnterEvent); });
            After(t += 900, () => log("moved from the speaker onto the slider, 900 ms: slider open " + VolumeSliderOpen + " (expected True)"));
            After(t += 50, () => mouse(panel, Mouse.MouseLeaveEvent));
            After(t += 800, () => log("left the slider: open " + VolumeSliderOpen + " (expected False)"));
            After(t += 100, () =>
            {
                bool before = muted;
                volBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                log("click on the speaker: muted " + before + " -> " + muted + ", slider open " + VolumeSliderOpen);
                volBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                log("click again: muted " + muted);
                muted = true;
                ApplyAudio();
            });
            Action<int> wheel = delta => volBtn.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent });
            After(t += 100, () =>
            {
                double v0 = volume; wheel(120); double v1 = volume;
                log(string.Format("wheel up on the speaker, slider closed: volume {0:0.00} -> {1:0.00}", v0, v1));
                muted = true; ApplyAudio();
            });
            After(t += 100, () => mouse(volBtn, Mouse.MouseEnterEvent));
            After(t += 300, () =>
            {
                double v0 = volume; wheel(-120); double v1 = volume;
                log(string.Format("wheel down on the speaker, slider open {0}: volume {1:0.00} -> {2:0.00}", VolumeSliderOpen, v0, v1));
                muted = true; ApplyAudio();
            });
            After(t += 100, () => mouse(volBtn, Mouse.MouseEnterEvent));
            After(t += 300, () => { log("open again: " + VolumeSliderOpen); int photo = files.FindIndex(f => !IsVideoFile(f)); if (photo >= 0) ShowIndex(photo, 1); });
            After(t += 800, () => log("switched to a photo (no video bar): slider open " + VolumeSliderOpen + " (expected False)"));
            After(t += 100, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
        }

        // FastViewer.exe <video> --looks <output folder>: every look, dark and light, on the viewer and on
        // Settings > Appearance; the Buttons page and the presets. Liquid Glass is shown over a stand-in desktop.
        void RunLooksTest()
        {
            Directory.CreateDirectory(snapDir);
            Action<string> log = text => testLog.Add(text);
            Func<string, Button> nav = name => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == name).Key;
            Action<string> page = name => nav(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            int t = 1800;
            After(t, () => { if (playing) TogglePlay(); });
            foreach (var look in Looks.All)
                foreach (var mode in new[] { "Dark", "Light" })
                {
                    var id = look.Id;
                    var m = mode;
                    After(t += 300, () => { lookId = id; lookMode = m; ApplyLook(); log(id + " " + m + ": light=" + Looks.Light + ", floating bars=" + Floating + ", see-through=" + seeThrough); });
                    After(t += 700, () => Snap(id + "_" + m.ToLowerInvariant() + "_1_viewer"));
                    After(t += 100, () => { OpenSettings(); page("Appearance"); });
                    After(t += 800, () => { Snap(id + "_" + m.ToLowerInvariant() + "_2_appearance"); CloseSettings(); });
                }
            After(t += 300, () => { lookId = "Photos"; lookMode = "Dark"; ApplyLook(); OpenSettings(); page("Buttons"); });
            After(t += 700, () => Snap("buttons_1_top"));
            After(t += 100, () => F<ScrollViewer>("SettingsScroll").ScrollToVerticalOffset(560));
            After(t += 500, () => Snap("buttons_2_more"));
            After(t += 100, () => { F<ScrollViewer>("SettingsScroll").ScrollToEnd(); });
            After(t += 500, () => { Snap("buttons_3_end"); CloseSettings(); });
            After(t += 200, () => { ApplyPreset(1); log("Essentials: hidden " + string.Join(", ", hiddenButtons)); });
            After(t += 600, () => Snap("preset_essentials_photos"));
            After(t += 100, () =>
            {
                ApplyPreset(2);
                log("Minimal: hidden " + hiddenButtons.Count + " buttons, middle button " + centerButton + ", bottom bar row " + F<RowDefinition>("BottomControlsRow").Height.Value);
                log("\"...\" menu, Hidden buttons: " + string.Join(", ", HiddenButtonItems(false).OfType<MenuItem>().Select(i => i.Header)));
                log("video menu, Hidden buttons: " + string.Join(", ", HiddenButtonItems(true).OfType<MenuItem>().Select(i => i.Header)));
            });
            After(t += 600, () => Snap("preset_minimal_photos"));
            After(t += 100, () => { lookId = "Clean"; ApplyLook(); });
            After(t += 700, () => Snap("preset_minimal_clean"));
            After(t += 100, () => { lookId = "Glass"; ApplyLook(); ApplyPreset(0); OpenSettings(); page("Buttons"); });
            After(t += 900, () => Snap("glass_settings_lens"));
            After(t += 100, () =>
            {
                CloseSettings();
                log("hidden players in Glass: " + string.Join(", ", slots.Select(s => s.Host.Opacity.ToString("0.00"))) + "; cover " + cover.Visibility);
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
        }
        volatile string stallStep = "opening";

        // FastViewer.exe <file> --stall <output folder>: plays, seeks, pauses, switches and "hovers" over heavy videos,
        // while a watchdog notes every moment the window stops responding (> 0.3 s) and the code it was stuck in.
        void RunStallTest()
        {
            Directory.CreateDirectory(snapDir);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long beat = 0, lastTick = 0;
            var ui = System.Threading.Thread.CurrentThread;
            var stalls = new List<string>();
            bool done = false;
            var heart = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
            heart.Tick += delegate
            {
                long now = clock.ElapsedMilliseconds;
                if (lastTick > 0 && now - lastTick > 300) lock (stalls) stalls.Add(string.Format("{0,6} ms: not responding for {1} ms (during: {2})", now, now - lastTick, stallStep));
                lastTick = now;
                System.Threading.Interlocked.Exchange(ref beat, now);
            };
            heart.Start();
            var watchdog = new System.Threading.Thread(delegate()
            {
                long reported = -1;
                while (!done)
                {
                    System.Threading.Thread.Sleep(50);
                    long last = System.Threading.Interlocked.Read(ref beat);
                    if (last == 0 || clock.ElapsedMilliseconds - last < 300 || reported == last) continue;
                    reported = last;
                    string trace;
#pragma warning disable 618 // reading another thread's stack: test only
                    try
                    {
                        ui.Suspend();
                        try { trace = new System.Diagnostics.StackTrace(ui, false).ToString(); }
                        finally { ui.Resume(); }
                    }
                    catch (Exception ex) { trace = "(no stack: " + ex.Message + ")"; }
#pragma warning restore 618
                    var lines = trace.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(14);
                    lock (stalls) stalls.Add("  stuck in (during: " + stallStep + "):\n      " + string.Join("\n      ", lines));
                }
            }) { IsBackground = true };
            watchdog.Start();

            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, string, Action> at = (ms, name, a) => steps.Add(new KeyValuePair<int, Action>(ms, () => { stallStep = name; a(); }));
            var random = new Random(1);
            for (int ms = 1500; ms < 29000; ms += 250) at(ms, null, () => ShowOverlay()); // the mouse moving over the video
            at(2500, "skip forward 5 s", () => SeekBy(5));
            at(3200, "skip back 3 s", () => SeekBy(-3));
            for (int k = 0; k < 12; k++) at(3800 + k * 80, "dragging the time bar", () => seek.Value = random.NextDouble() * seek.Maximum);
            at(5200, "pause", () => TogglePlay());
            at(5700, "play again", () => TogglePlay());
            at(6300, "pause", () => TogglePlay());
            at(6600, "frame by frame", () => { StepFrame(1); StepFrame(1); StepFrame(-1); });
            at(7200, "play again", () => TogglePlay());
            at(8200, "next (4K)", () => Go(1));
            at(10000, "next (long 1080p60)", () => Go(1));
            for (int k = 0; k < 10; k++) at(11500 + k * 100, "dragging the time bar (long)", () => seek.Value = random.NextDouble() * seek.Maximum);
            at(13000, "skip forward 30 s", () => SeekBy(30));
            at(13600, "skip back 10 s", () => SeekBy(-10));
            at(14200, "pause", () => TogglePlay());
            at(14600, "play again", () => TogglePlay());
            for (int k = 0; k < 8; k++) { int dir = k % 2 == 0 ? -1 : 1; at(15500 + k * 150, "switching quickly", () => Go(dir)); }
            for (int k = 0; k < 6; k++) at(17500 + k * 400, "switching", () => Go(-1));
            at(21000, "play to the end region", () => { if (shown != null && shown.IsVideo && shown.Video.NaturalDuration.HasTimeSpan) shown.Video.Position = shown.Video.NaturalDuration.TimeSpan - TimeSpan.FromSeconds(1.5); });
            at(24000, "watching", () => { });
            at(29000, "done", () =>
            {
                done = true;
                heart.Stop();
                var log = new List<string>
                {
                    "stalls over 0.3 s: " + stalls.Count(l => l.Contains("not responding")) +
                    ", longest " + stalls.Where(l => l.Contains("not responding")).Select(l => int.Parse(System.Text.RegularExpressions.Regex.Match(l, @"for (\d+) ms").Groups[1].Value)).DefaultIfEmpty(0).Max() + " ms" +
                    ", stopped grabbing frames: " + slowGrabs
                };
                lock (stalls) log.AddRange(stalls);
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), log);
                Close();
            });
            foreach (var s in steps) { var a = s.Value; After(s.Key, () => { var keep = stallStep; a(); if (stallStep == null) stallStep = keep; }); }
        }

        // FastViewer.exe <file> --memory <output folder>: memory while going through the folder, after zooming
        // into a photo, and after letting go of everything (what the background mode does).
        void RunMemoryTest()
        {
            Directory.CreateDirectory(snapDir);
            Action<string> log = text => testLog.Add(text);
            var me = System.Diagnostics.Process.GetCurrentProcess();
            double peakPrivate = 0, peakWorking = 0;
            Func<string> mem = () =>
            {
                me.Refresh();
                return string.Format("private {0:0} MB, working set {1:0} MB, managed {2:0} MB",
                    me.PrivateMemorySize64 / 1048576.0, me.WorkingSet64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0);
            };
            var sampler = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            sampler.Tick += delegate
            {
                me.Refresh();
                peakPrivate = Math.Max(peakPrivate, me.PrivateMemorySize64 / 1048576.0);
                peakWorking = Math.Max(peakWorking, me.WorkingSet64 / 1048576.0);
            };
            Action collect = () => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); };

            int t = 1500;
            After(t, () => { log("first file open: " + mem()); sampler.Start(); });
            for (int i = 0; i < 24; i++) After(t += 400, () => Go(1)); // once through the whole folder and a bit
            After(t += 1500, () =>
            {
                sampler.Stop();
                log(string.Format("going through {0} files: peak private {1:0} MB, peak working set {2:0} MB", files.Count, peakPrivate, peakWorking));
                log("after: " + mem());
                int photo = files.FindIndex(f => !IsVideoFile(f));
                ShowIndex(photo, 1);
            });
            After(t += 800, () =>
            {
                var b = shown.Picture.Source as System.Windows.Media.Imaging.BitmapSource;
                log("photo on screen: " + (b == null ? "-" : b.PixelWidth + " x " + b.PixelHeight) + " pixels in memory, photo is " + shown.FullWidth + " x " + shown.FullHeight);
                SetZoom(4, StageCenter());
            });
            After(t += 1500, () =>
            {
                var b = shown.Picture.Source as System.Windows.Media.Imaging.BitmapSource;
                log("zoomed to 400%: " + (b == null ? "-" : b.PixelWidth + " x " + b.PixelHeight) + " pixels in memory; " + mem());
                SetZoom(1, StageCenter());
                LetGoOfFiles();
                collect();
                log("let go of everything, right away: " + mem());
            });
            After(t += 1500, () => { collect(); log("let go of everything, 1.5 s later: " + mem()); });
            After(t += 300, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
        }

        // FastViewer.exe <file> --tweaks <output folder>: middle play button, side arrows, feedback, grid names.
        // The video is paused before anything touches the volume, so the test stays silent.
        void RunTweaksTest()
        {
            Directory.CreateDirectory(snapDir);
            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, Action> at = (ms, a) => steps.Add(new KeyValuePair<int, Action>(ms, a));
            Action<string> log = text => testLog.Add(text);
            Func<string> toast = () => F<FrameworkElement>("Toast").Visibility == Visibility.Visible ? F<TextBlock>("ToastText").Text : "(none)";
            Func<string, Button> nav = name => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == name).Key;

            at(800, () => { int v = files.FindIndex(IsVideoFile); if (v >= 0) ShowIndex(v, 1); });
            at(2000, () => { centerButton = 1; UpdateControls(); log("only when paused, playing=" + playing + ": button " + centerPlayBtn.Visibility); Snap("1_playing_no_button"); });
            at(2200, () => { if (playing) TogglePlay(); log("only when paused, paused: button " + centerPlayBtn.Visibility); });
            at(2600, () => Snap("2_paused_play_button"));
            at(2700, () => { centerButton = 2; UpdateControls(); log("hide, paused: button " + centerPlayBtn.Visibility); hiddenButtons.Add("Arrows"); UpdateControls(); log("arrows off: " + F<FrameworkElement>("PrevBtn").Visibility + "/" + F<FrameworkElement>("NextBtn").Visibility); });
            at(3100, () => Snap("3_no_button_no_arrows"));
            at(3200, () =>
            {
                overlayVisible = false; // as if the controls had faded
                SeekBy(10); log("skip with controls hidden: " + toast());
                HideToast(null); overlayVisible = true; SeekBy(10); log("skip with controls showing: " + toast());
                log("paused before volume: " + !playing);
                SetVolume(0.45); log("volume key: " + toast());
                ToggleMute(); log("mute key: " + toast());
                muted = true; ApplyAudio(); log("test muted again: " + muted);
                ShowToast("Continuing where you left off", "Start over", delegate { }, 4, "resume");
                SetVolume(0.5); log("volume while a message with a button shows: " + toast() + " (expected the message stays)");
                muted = true; ApplyAudio(); HideToast(null);
            });
            at(3500, () => { centerButton = 0; hiddenButtons.Remove("Arrows"); UpdateControls(); OpenGrid(); gridNames = true; UpdateGridNamesButton(); LayoutGrid(true); });
            at(4300, () => { Snap("4_grid_names"); log("grid names: row " + GridRow + " for tile " + gridTile + ", tiles " + gridShown.Count); });
            at(4400, () => { gridNames = false; UpdateGridNamesButton(); LayoutGrid(true); log("names off: row " + GridRow); CloseGrid(); });
            at(4600, () => { OpenSettings(); nav("Videos").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            at(5000, () => { F<ScrollViewer>("SettingsScroll").ScrollToVerticalOffset(260); });
            at(5300, () => { Snap("5_settings_videos"); nav("Viewing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            at(5700, () => { F<ScrollViewer>("SettingsScroll").ScrollToVerticalOffset(250); });
            at(6000, () => { Snap("6_settings_viewing"); CloseSettings(); });
            at(6100, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog.Where(l => !l.StartsWith("snap ") && !l.StartsWith("reveal") && !l.StartsWith("live") && !l.Contains(" ms  ")));
                Close();
            });
            foreach (var s in steps) After(s.Key, s.Value);
        }

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
