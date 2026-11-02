// FastViewer: bug-hunt test modes (FastViewer.exe <file> --hunt <output folder> --case <name> [--count n] [--every ms] [--pattern p]).
// Cases: torture (very fast switching with checks of what's on screen), edge (odd files), churn (files changing in the
// open folder; give it a scratch folder), big (thousands of files), dump (what was loaded from --settings), keys,
// dialog, rotate (scratch folder), loop, sizes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool huntTest;
        string huntCase = "torture", huntPattern = "mixed";
        int huntCount, huntEvery;

        [DllImport("user32.dll")]
        static extern int GetGuiResources(IntPtr process, int flags);

        void RunHunt()
        {
            Directory.CreateDirectory(snapDir);
            testLog.Clear();
            switch (huntCase)
            {
                case "torture": HuntTorture(); break;
                case "edge": HuntEdge(); break;
                case "churn": HuntChurn(); break;
                case "big": HuntBig(); break;
                case "dump": HuntDump(); break;
                case "keys": HuntKeys(); break;
                case "dialog": HuntDialog(); break;
                case "rotate": HuntRotate(); break;
                case "loop": HuntLoop(); break;
                case "sizes": HuntSizes(); break;
                case "ghost": HuntGhost(); break;
                case "positions": HuntPositions(); break;
                case "saveclose": HuntSaveClose(); break;
                default: HuntLog("unknown case " + huntCase); HuntDone(); break;
            }
        }

        readonly List<string> huntLog = new List<string>();
        void HuntLog(string text) { huntLog.Add(text); }

        void HuntDone()
        {
            File.WriteAllLines(Path.Combine(snapDir, "log.txt"), huntLog);
            File.WriteAllLines(Path.Combine(snapDir, "events.txt"), testLog);
            Close();
        }

        string HuntResources()
        {
            var me = Process.GetCurrentProcess();
            return string.Format("private {0:0} MB, working set {1:0} MB, handles {2}, GDI {3}, USER {4}, threads {5}",
                me.PrivateMemorySize64 / 1048576.0, me.WorkingSet64 / 1048576.0, me.HandleCount,
                GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1), me.Threads.Count);
        }

        string HuntSlot(Slot s)
        {
            if (s == null) return "(none)";
            return string.Format("{0} [{1}{2}{3}{4}{5}]", s.File == null ? "-" : Path.GetFileName(s.File),
                s.IsVideo ? "video" : "photo", s.Ready ? " ready" : " loading", s.Failed ? " FAILED" : "",
                s.IsVideo ? " " + s.Video.NaturalVideoWidth + "x" + s.Video.NaturalVideoHeight + " at " + s.Video.Position.TotalSeconds.ToString("0.00") + "s" : "",
                s.IsVideo && s.Poster.Visibility == Visibility.Visible ? " poster" : "") + (s.Failed && s.Error != null ? " error: " + s.Error.Replace("\n", " ") : "");
        }

        // What's really on screen in the picture area: average brightness and spread (0-255) from the window's pixels.
        void HuntPixels(out double mean, out double spread)
        {
            double d = Dpi();
            var p = stage.TranslatePoint(new Point(0, 0), this);
            int x0 = (int)(p.X * d), y0 = (int)(p.Y * d), w = (int)(stage.ActualWidth * d), h = (int)(stage.ActualHeight * d);
            int ww = (int)Math.Round(ActualWidth * d), wh = (int)Math.Round(ActualHeight * d);
            mean = spread = -1;
            if (w < 10 || h < 10) return;
            using (var bmp = new System.Drawing.Bitmap(ww, wh))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(new WindowInteropHelper(this).Handle, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
                double sum = 0, sum2 = 0;
                int n = 0;
                // the middle half of the picture area (away from the bars and arrows)
                for (int y = y0 + h / 4; y < y0 + h * 3 / 4 && y < wh; y += 6)
                    for (int x = x0 + w / 4; x < x0 + w * 3 / 4 && x < ww; x += 6)
                    {
                        var c = bmp.GetPixel(x, y);
                        double l = 0.3 * c.R + 0.59 * c.G + 0.11 * c.B;
                        sum += l; sum2 += l * l; n++;
                    }
                if (n == 0) return;
                mean = sum / n;
                spread = Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
            }
        }

        // ---------------------------------------------------------------- torture: thousands of fast switches

        void HuntTorture()
        {
            int count = huntCount > 0 ? huntCount : 1000, every = huntEvery > 0 ? huntEvery : 80;
            var rnd = new Random(7);
            int n = 0, lagTicks = 0, blankTicks = 0, stuck = 0, wrong = 0, black = 0, hiddenMoving = 0, notPlaying = 0, checks = 0;
            long maxLagMs = 0;
            var lagWatch = Stopwatch.StartNew();
            Slot lastWanted = null;
            HuntLog(string.Format("torture: {0} switches every {1} ms, pattern {2}, preload {3}, {4} files, autoplay {5}", count, every, huntPattern, preloadAhead, files.Count, autoplay));
            HuntLog("start: " + HuntResources());
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(every) };
            Action verify = null;
            timer.Tick += delegate
            {
                // How it looks right before the next press.
                if (wanted != shown)
                {
                    lagTicks++;
                    if (wanted != lastWanted) { lastWanted = wanted; lagWatch.Restart(); }
                    maxLagMs = Math.Max(maxLagMs, lagWatch.ElapsedMilliseconds);
                }
                var s = shown;
                bool content = s != null && (s.Failed || (!s.IsVideo && s.Picture.Source != null) ||
                               (s.IsVideo && (s.Poster.Source != null && s.Poster.Visibility == Visibility.Visible || s.Video.NaturalVideoWidth > 0)));
                if (!content)
                {
                    blankTicks++;
                    if (blankTicks <= 20) HuntLog("  nothing to show at switch " + n + ": shown " + HuntSlot(s));
                }

                int step;
                switch (huntPattern)
                {
                    case "forward": step = 1; break;
                    case "pingpong": step = n % 2 == 0 ? 1 : -1; break;
                    default:
                        int r = rnd.Next(12);
                        step = r < 7 ? 1 : r < 9 ? -1 : r < 10 ? 2 : r < 11 ? -3 : 0;
                        break;
                }
                if (step == 0) ShowIndex(rnd.Next(files.Count), 1); // a jump (thumbnail click)
                else Go(step);
                n++;
                if (n % 200 == 0) HuntLog(string.Format("after {0} switches: {1}", n, HuntResources()));
                if (n % 50 == 0 || n >= count)
                {
                    timer.Stop();
                    After(1500, verify);
                }
            };
            var positions = new Dictionary<Slot, double>();
            verify = delegate
            {
                checks++;
                var s = shown;
                string expect = files.Count > 0 ? files[index] : null;
                if (wanted != shown)
                {
                    stuck++;
                    HuntLog("  STUCK after " + n + ": wanted " + HuntSlot(wanted) + ", still showing " + HuntSlot(s));
                }
                if (s == null || !string.Equals(s.File, expect, StringComparison.OrdinalIgnoreCase) || titleText.Text != Path.GetFileName(expect))
                {
                    wrong++;
                    HuntLog("  WRONG FILE after " + n + ": list says " + Path.GetFileName(expect) + ", title '" + titleText.Text + "', showing " + HuntSlot(s));
                }
                double mean, spread;
                HuntPixels(out mean, out spread);
                if (s != null && !s.Failed && mean >= 0 && mean < 12 && spread < 4)
                {
                    black++;
                    HuntLog(string.Format("  BLACK picture after {0}: brightness {1:0.0}, spread {2:0.0}, showing {3}", n, mean, spread, HuntSlot(s)));
                    if (black <= 3) Snap("black_" + n);
                }
                positions.Clear();
                foreach (var x in slots) if (x.IsVideo && x.File != null) positions[x] = x.Video.Position.TotalSeconds;
                After(500, delegate
                {
                    foreach (var p in positions)
                    {
                        double now = p.Key.Video.Position.TotalSeconds;
                        if (p.Key == shown)
                        {
                            var d = shown.Video.NaturalDuration;
                            bool atEnd = d.HasTimeSpan && now >= d.TimeSpan.TotalSeconds - 0.6;
                            if (autoplay && !shown.Failed && playing && Math.Abs(now - p.Value) < 0.05 && !atEnd)
                            {
                                notPlaying++;
                                HuntLog("  SHOWN VIDEO NOT MOVING after " + n + ": " + HuntSlot(shown));
                            }
                        }
                        else if (Math.Abs(now - p.Value) > 0.05)
                        {
                            hiddenMoving++;
                            HuntLog("  HIDDEN VIDEO PLAYING after " + n + ": " + HuntSlot(p.Key) + " (muted " + p.Key.Video.IsMuted + ")");
                        }
                    }
                    if (n < count) { timer.Start(); return; }
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    HuntLog("end (after GC): " + HuntResources());
                    HuntLog(string.Format("summary: {0} switches, {1} checks; stuck {2}, wrong file {3}, black picture {4}, shown video not moving {5}, hidden video playing {6}, " +
                        "presses with the old file still up {7} ({8:0.0}%), longest wait {9} ms, presses with nothing to show {10}, stopped grabbing frames {11}",
                        n, checks, stuck, wrong, black, notPlaying, hiddenMoving, lagTicks, 100.0 * lagTicks / Math.Max(1, n), maxLagMs, blankTicks, slowGrabs));
                    HuntDone();
                });
            };
            After(1500, timer.Start);
        }

        // ---------------------------------------------------------------- edge: every file of an odd folder

        void HuntEdge()
        {
            int every = huntEvery > 0 ? huntEvery : 2500;
            int t = 1500;
            HuntLog("folder: " + (files.Count > 0 ? Path.GetDirectoryName(files[0]) : "-") + ", " + files.Count + " files, message '" + message.Text.Replace("\n", " / ") + "'");
            HuntLog("files: " + string.Join(" | ", files.Select(Path.GetFileName)));
            for (int i = 0; i < files.Count; i++)
            {
                int k = i;
                After(t, () => { switchWatch.Restart(); ShowIndex(k, 1); });
                After(t += every, () =>
                {
                    var s = shown;
                    double mean, spread;
                    HuntPixels(out mean, out spread);
                    var pic = s == null ? null : s.Picture.Source as System.Windows.Media.Imaging.BitmapSource;
                    HuntLog(string.Format("{0}\n    wanted==shown {1}; shown {2}; title '{3}'; message '{4}'; picture {5}; full {6}x{7}; brightness {8:0} spread {9:0}; duration {10}",
                        Path.GetFileName(files[k]), wanted == shown, HuntSlot(s), titleText.Text, message.Text.Replace("\n", " / "),
                        pic == null ? "-" : pic.PixelWidth + "x" + pic.PixelHeight, s == null ? 0 : s.FullWidth, s == null ? 0 : s.FullHeight, mean, spread,
                        s != null && s.IsVideo && s.Video.NaturalDuration.HasTimeSpan ? s.Video.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0") + " s" : "-"));
                    if (wanted != shown) HuntLog("    STILL LOADING: " + HuntSlot(wanted));
                    if (playing) TogglePlay();
                });
            }
            After(t += 300, () => { HuntLog("end: " + HuntResources()); HuntDone(); });
        }

        // ---------------------------------------------------------------- churn: the open folder changes

        void HuntChurn()
        {
            string folder = Path.GetDirectoryName(files[index]);
            string spare = Path.Combine(snapDir, "spare.jpg");
            File.Copy(files.First(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)), spare, true);
            Action<string> state = what => HuntLog(string.Format("{0}: {1} files, index {2}, current {3}, title '{4}', shown {5}, message '{6}'",
                what, files.Count, index, CurrentFile == null ? "-" : Path.GetFileName(CurrentFile), titleText.Text, HuntSlot(shown), message.Text.Replace("\n", " / ")));
            int t = 1500;
            After(t, () => { ShowIndex(2, 1); });
            After(t += 800, () => state("start on the 3rd file"));
            After(t += 100, () => File.Copy(spare, Path.Combine(folder, "00 new first.jpg")));
            After(t += 1500, () => state("a new file appeared at the start"));
            string renamedTo = null;
            After(t += 100, () => { var cur = CurrentFile; renamedTo = Path.Combine(folder, "zz renamed " + Path.GetFileName(cur)); File.Move(cur, renamedTo); });
            After(t += 1500, () => state("the current file was renamed to " + Path.GetFileName(renamedTo)));
            After(t += 100, () => { var cur = CurrentFile; try { File.Delete(cur); HuntLog("deleted " + Path.GetFileName(cur) + " from outside"); } catch (Exception ex) { HuntLog("couldn't delete the current file from outside: " + ex.Message); } });
            After(t += 1500, () => state("the current file was deleted from outside"));
            After(t += 100, () =>
            {
                var cur = CurrentFile;
                if (cur != null && !IsVideoFile(cur))
                {
                    File.Copy(spare, cur, true); // edited and saved in place (like Paint)
                    File.SetLastWriteTime(cur, DateTime.Now);
                }
            });
            After(t += 1500, () => { state("the current photo was changed in place"); HuntLog("  picture in memory still the old one: " + (shown != null && shown.Picture.Source != null)); });
            var stallWatch = new Stopwatch();
            After(t += 100, () =>
            {
                var w = Stopwatch.StartNew();
                for (int i = 0; i < 400; i++) File.Copy(spare, Path.Combine(folder, string.Format("bulk {0:D3}.jpg", i)));
                HuntLog("copied 400 files in " + w.ElapsedMilliseconds + " ms");
            });
            After(t += 2500, () => state("400 files were added"));
            After(t += 100, () => { ToggleShuffle(); state("shuffle on"); for (int i = 0; i < 5; i++) File.Copy(spare, Path.Combine(folder, "shuffled new " + i + ".jpg")); });
            After(t += 1500, () => { state("5 files added while shuffled"); ToggleShuffle(); });
            After(t += 300, () =>
            {
                var w = Stopwatch.StartNew();
                foreach (var f in Directory.GetFiles(folder)) try { File.Delete(f); } catch (Exception ex) { HuntLog("couldn't delete " + Path.GetFileName(f) + ": " + ex.Message); }
                HuntLog("deleted everything from outside in " + w.ElapsedMilliseconds + " ms");
            });
            After(t += 2000, () => { state("the folder is empty"); Snap("churn_empty"); });
            After(t += 100, () => { Go(1); state("pressed next in the empty folder"); });
            After(t += 100, () => File.Copy(spare, Path.Combine(folder, "back again.jpg")));
            After(t += 1500, () => state("one file came back"));
            After(t += 100, () => { try { Directory.Delete(folder, true); HuntLog("folder deleted"); } catch (Exception ex) { HuntLog("couldn't delete the folder: " + ex.Message); } });
            After(t += 1500, () => { state("the folder itself was deleted"); Go(1); state("pressed next"); });
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- big: thousands of files

        void HuntBig()
        {
            string first = files.Count > 0 ? files[0] : null;
            int t = 1500;
            for (int o = 0; o < 4; o++)
            {
                int ord = o;
                After(t += 400, () =>
                {
                    order = ord;
                    var w = Stopwatch.StartNew();
                    OpenPath(first);
                    HuntLog(string.Format("order {0}: opening the folder ({1} files) took {2} ms", ord, files.Count, w.ElapsedMilliseconds));
                    w.Restart();
                    RescanFolder();
                    HuntLog(string.Format("order {0}: re-reading it after a change took {1} ms", ord, w.ElapsedMilliseconds));
                });
            }
            After(t += 3000, () =>
            {
                order = 0;
                var w = Stopwatch.StartNew();
                ToggleShuffle();
                HuntLog("shuffle on: " + w.ElapsedMilliseconds + " ms");
                w.Restart();
                RescanFolder();
                HuntLog("re-reading while shuffled (nothing new): " + w.ElapsedMilliseconds + " ms");
            });
            string added = null;
            After(t += 500, () =>
            {
                added = Path.Combine(Path.GetDirectoryName(first), "zzz added.jpg");
                File.Copy(first, added, true);
                var w = Stopwatch.StartNew();
                RescanFolder();
                HuntLog("re-reading while shuffled (one new file): " + w.ElapsedMilliseconds + " ms");
                ToggleShuffle();
            });
            After(t += 500, () =>
            {
                File.Delete(added);
                var w = Stopwatch.StartNew();
                ToggleGrid();
                HuntLog("grid open: " + w.ElapsedMilliseconds + " ms");
            });
            After(t += 4000, () => { Snap("big_grid"); HuntLog("grid after 4 s: " + HuntResources()); ToggleGrid(); });
            After(t += 500, () =>
            {
                var w = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) Go(1);
                HuntLog("200 switches in a row (no waiting): " + w.ElapsedMilliseconds + " ms");
            });
            After(t += 2000, HuntDone);
        }

        // ---------------------------------------------------------------- dump: what was loaded (use with --settings)

        void HuntDump()
        {
            After(1200, () =>
            {
                HuntLog("loaded: volume " + volume + ", muted " + muted + ", loop " + loop + ", filter " + filter + ", order " + order + ", preload " + preloadAhead +
                        ", slideSeconds " + slideSeconds + ", hideDelay " + hideDelay + ", skip " + skipBack + "/" + skipForward + ", look " + lookId + "/" + lookMode +
                        ", autoplay " + autoplay + ", dim " + dimVideo + ", strip " + showStrip + ", gridSize " + gridSize + ", centerPlay " + centerButton +
                        ", keepReady " + keepReady + ", files " + files.Count + ", slideTimer " + slideTimer.Interval.TotalMilliseconds + " ms");
                HuntLog("keys: next=[" + string.Join(",", shortcuts.First(s => s.Id == "next").Keys) + "] rotate=[" + string.Join(",", shortcuts.First(s => s.Id == "rotate").Keys) +
                        "] mouse middle=" + mouseActions["middle"]);
                HuntLog("settings page cards: " + (settingsList == null ? -1 : settingsList.Children.Count));
                try { OpenSettings(); HuntLog("settings opened"); CloseSettings(); }
                catch (Exception ex) { HuntLog("OPENING SETTINGS FAILED: " + ex.GetType().Name + ": " + ex.Message); }
                try { ToggleSlideshow(); HuntLog("slideshow started"); }
                catch (Exception ex) { HuntLog("SLIDESHOW FAILED: " + ex.GetType().Name + ": " + ex.Message); }
            });
            After(3200, () => { HuntLog("after 2 s of slideshow: on file " + index + " of " + files.Count); StopSlideshow(); });
            After(3400, HuntDone);
        }

        // ---------------------------------------------------------------- keys

        void PressKey(Key key)
        {
            var source = PresentationSource.FromVisual(this);
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            OnKey(this, e);
        }

        void HuntKeys()
        {
            Func<string, string> keys = id => "[" + string.Join(",", shortcuts.First(s => s.Id == id).Keys) + "]";
            int t = 1500;
            After(t, () =>
            {
                HuntLog("start: index " + index + ", next " + keys("next") + ", rotate " + keys("rotate"));
                OpenSettings();
                StartCapture(shortcuts.First(s => s.Id == "rotate"), 1); // click the 2nd key box of Rotate...
                CloseSettings();                                          // ...then leave Settings with the Back button / mouse back button
                HuntLog("clicked Rotate's key box, then left Settings without pressing a key (capturing " + (capturing != null) + ")");
                int before = index;
                PressKey(Key.Right); // going to the next file
                HuntLog("pressed Right to go to the next file: index " + before + " -> " + index + ", next " + keys("next") + ", rotate " + keys("rotate") +
                        ", message '" + F<TextBlock>("ToastText").Text + "'");
                PressKey(Key.Right);
                HuntLog("pressed Right again: index " + index + ", rotation of this file " + (rotations.ContainsKey(CurrentFile) ? rotations[CurrentFile] : 0));
            });
            After(t += 800, () =>
            {
                foreach (var s in shortcuts) s.Keys = s.Defaults.ToList();
                RebuildShortcutMap();
                // the same key saved for two actions (settings.ini edited by hand, or an older version)
                savedKeys.Clear();
                savedKeys["rotate"] = "Right";
                ApplySavedKeys();
                int before = index;
                PressKey(Key.Right);
                HuntLog("Right saved for Rotate while Next still has it by default: next " + keys("next") + ", rotate " + keys("rotate") +
                        "; pressing Right: index " + before + " -> " + index + ", rotated " + (rotations.ContainsKey(CurrentFile) ? rotations[CurrentFile] : 0));
                savedKeys["rotate"] = "Ctrl+Banana|Ctrl+R";
                ApplySavedKeys();
                HuntLog("nonsense key name saved: rotate " + keys("rotate") + " (shows as '" + KeyOf("rotate") + "')");
            });
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- delete dialog

        void HuntDialog()
        {
            int t = 1500;
            After(t, () =>
            {
                ShowIndex(0, 1);
            });
            After(t += 800, () =>
            {
                AskDelete();
                HuntLog("delete dialog: '" + dialogText.Text + "', current " + Path.GetFileName(CurrentFile));
                stage.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
                HuntLog("mouse wheel while the dialog is open: dialog still '" + dialogText.Text + "' (visible " + dialog.Visibility + "), but OK would now delete " + Path.GetFileName(CurrentFile));
                RunMouseAction("forward");
                HuntLog("mouse forward button: OK would now delete " + Path.GetFileName(CurrentFile));
                dialog.Visibility = Visibility.Collapsed;
            });
            After(t += 500, () =>
            {
                slideSeconds = 2;
                slideTimer.Interval = TimeSpan.FromSeconds(2);
                int photo = files.FindIndex(f => !IsVideoFile(f));
                ShowIndex(photo, 1);
            });
            After(t += 600, () =>
            {
                ToggleSlideshow();
                AskDelete();
                HuntLog("slideshow running, delete dialog: '" + dialogText.Text + "'");
            });
            After(t += 4500, () =>
            {
                HuntLog("4.5 s later: dialog still '" + dialogText.Text + "' (visible " + dialog.Visibility + "), OK would delete " + Path.GetFileName(CurrentFile));
                dialog.Visibility = Visibility.Collapsed;
                StopSlideshow();
            });
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- rotate and save (scratch folder only!)

        static string HuntHash(string f)
        {
            try { using (var md5 = System.Security.Cryptography.MD5.Create()) using (var fs = File.OpenRead(f)) return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").Substring(0, 10); }
            catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
        }

        void HuntRotate()
        {
            saveRotation = true;
            string folder = Path.GetDirectoryName(files[index]);
            Func<string, int> at = name => files.FindIndex(f => Path.GetFileName(f) == name);
            Action<string> toast = what => HuntLog(what + ": message '" + (F<FrameworkElement>("Toast").Visibility == Visibility.Visible ? F<TextBlock>("ToastText").Text : "") + "'");
            int t = 1500;
            var hashes = new Dictionary<string, string>();
            After(t, () => { foreach (var f in files) hashes[f] = HuntHash(f); ShowIndex(at("quick.jpg"), 1); });
            After(t += 700, () => { Rotate(); });
            After(t += 400, () => { Go(1); HuntLog("rotated quick.jpg, then pressed next 0.4 s later (before the save)"); });
            After(t += 2000, () =>
            {
                var f = Path.Combine(folder, "quick.jpg");
                toast("2 s later");
                HuntLog("quick.jpg changed on disk: " + (HuntHash(f) != hashes[f]) + ", FastViewer still shows it turned: " + rotations.ContainsKey(f));
            });
            foreach (var name in new[] { "plain.jpg", "readonly.jpg", "readonly.png", "picture.png" })
            {
                var n = name;
                After(t += 300, () => ShowIndex(at(n), 1));
                After(t += 700, () => Rotate());
                After(t += 2200, () =>
                {
                    var f = Path.Combine(folder, n);
                    toast(n + " rotated and saved");
                    HuntLog("  changed on disk: " + (HuntHash(f) != hashes[f]) + ", left-over temp file: " + File.Exists(f + ".fastviewer-tmp") +
                            ", files in folder now: " + string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));
                });
            }
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- A-B loop with A and B at the same spot

        void HuntLoop()
        {
            int t = 1500;
            After(t, () => { if (playing) TogglePlay(); shown.Video.Position = TimeSpan.FromSeconds(2); });
            After(t += 500, () =>
            {
                SetLoopStart();
                SetLoopEnd();
                HuntLog("A and B set at the same moment: A " + loopA + ", B " + loopB + ", message '" + F<TextBlock>("ToastText").Text + "'");
                TogglePlay();
            });
            var seen = new List<string>();
            for (int k = 1; k <= 6; k++) After(t += 250, () => seen.Add(shown.Video.Position.TotalSeconds.ToString("0.00")));
            After(t += 100, () =>
            {
                HuntLog("position while playing: " + string.Join(", ", seen) + " (playing " + playing + ")");
                TogglePlay();
                ClearLoop();
                shown.Video.Position = TimeSpan.FromSeconds(1);
                SetLoopEnd(); // B without A first
                HuntLog("B pressed without A: A " + loopA + ", B " + loopB);
                ClearLoop();
            });
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- ghost: a video skipped while it starts

        // Sound is "on" here (not muted) but at volume 0, so nothing is heard; the log says what a real window would do.
        void HuntGhost()
        {
            int t = 1500;
            Slot ghost = null;
            for (int round = 0; round < 6; round++)
            {
                int r = round;
                After(t += 200, () =>
                {
                    muted = false;
                    volume = 0;
                    // a photo, with a video next to it that has no first-frame picture yet (heavy video, slow PC, or just not made yet)
                    int photo = files.FindIndex(f => !IsVideoFile(f) && files.Count > files.IndexOf(f) + 1 && IsVideoFile(files[files.IndexOf(f) + 1]));
                    ShowIndex(photo + r * 0, 1);
                });
                After(t += 1500, () =>
                {
                    var next = slots.FirstOrDefault(s => string.Equals(s.File, files[Wrap(index + 1)], StringComparison.OrdinalIgnoreCase));
                    if (next != null) { next.Poster.Source = null; next.Poster.Visibility = Visibility.Collapsed; }
                    Go(1); // to the video: it starts playing behind the photo until it draws
                    ghost = wanted;
                    HuntLog("round " + r + ": pressed next to " + HuntSlot(ghost) + ", playing now " + playing + ", revealing it " + (revealing == ghost));
                });
                After(t += 30, () =>
                {
                    Go(-1); // changed your mind: back to the photo, which is still on screen
                    HuntLog("  30 ms later pressed previous: " + Path.GetFileName(files[index]) + " (ready " + (wanted != null && wanted.Ready) + ")");
                });
                After(t += 2500, () =>
                {
                    double a = ghost.Video.Position.TotalSeconds;
                    After(400, () =>
                    {
                        double b = ghost.Video.Position.TotalSeconds;
                        HuntLog(string.Format("  2.5 s later: skipped video {0}: still playing {1} ({2:0.00}s -> {3:0.00}s), muted {4}, volume {5}, on screen {6}; now showing {7}",
                            Path.GetFileName(ghost.File), b - a > 0.1, a, b, ghost.Video.IsMuted, ghost.Video.Volume, ghost == shown, HuntSlot(shown)));
                    });
                });
                t += 500;
            }
            After(t += 600, HuntDone);
        }

        // ---------------------------------------------------------------- resume places with several windows (--positions <scratch file>)

        void HuntPositions()
        {
            Func<string> file = () => string.Join(" | ", File.ReadAllLines(PositionsFile).Select(l => l.Substring(0, l.IndexOf('|') + 1) + Path.GetFileName(l.Substring(l.IndexOf('|') + 1))));
            int t = 1500;
            After(t, () =>
            {
                // the scratch file starts with: old.mp4 at 100 s (oldest) ... then 299 others
                LoadPositions();
                HuntLog("start: " + positions.Count + " places, first lines " + string.Join(" | ", File.ReadAllLines(PositionsFile).Take(2)));
                if (playing) TogglePlay();
                shown.Video.Position = TimeSpan.FromSeconds(40);
                ShowIndex(Wrap(index + 1), 1); // leaving the long video at 40 s: remembered
            });
            After(t += 1000, () =>
            {
                // meanwhile another FastViewer window saves a place of its own
                File.AppendAllLines(PositionsFile, new[] { "77.0|C:\\other window\\theirs.mp4" });
                File.SetLastWriteTimeUtc(PositionsFile, DateTime.UtcNow.AddSeconds(2));
                positionChanges.Add(new KeyValuePair<string, double>(positions.Keys.First(k => k.EndsWith("old.mp4")), 100)); // watched old.mp4 again
                SavePositions(); // this window closes
                var lines = File.ReadAllLines(PositionsFile);
                HuntLog("after saving: " + lines.Length + " lines; other window's place kept: " + lines.Any(l => l.EndsWith("theirs.mp4")) +
                        "; old.mp4 moved to the end (most recent): " + lines.Skip(lines.Length - 3).Any(l => l.EndsWith("old.mp4")) +
                        "; this video at 40 s: " + lines.Any(l => l.StartsWith("40.") && l.EndsWith("long.mp4")));
                HuntLog("last 3 lines: " + string.Join(" | ", lines.Skip(lines.Length - 3)));
            });
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- closing while a video is saved

        void HuntSaveClose()
        {
            After(1500, () =>
            {
                if (playing) TogglePlay();
                string target = Path.Combine(snapDir, "copy.mp4");
                RunConversion(files[index], target, null, null, "Making compatible copy");
                HuntLog("started saving " + Path.GetFileName(files[index]) + " to copy.mp4, then closed the window right away");
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), huntLog);
                Close();
                HuntLog("after Close: window visible " + IsVisible + ", still saving " + converting);
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), huntLog);
            });
        }

        // ---------------------------------------------------------------- odd window sizes

        void HuntSizes()
        {
            var sizes = new[] { new Size(MinWidth, MinHeight), new Size(MinWidth, 1000), new Size(1800, MinHeight), new Size(640, 480), new Size(2600, 1500) };
            int t = 1500;
            for (int i = 0; i < sizes.Length; i++)
            {
                var sz = sizes[i];
                int k = i;
                After(t += 300, () => { Width = sz.Width; Height = sz.Height; ShowOverlay(); });
                After(t += 600, () =>
                {
                    HuntLog(string.Format("asked {0:0}x{1:0}, got {2:0}x{3:0}; title width {4:0}; stage {5:0}x{6:0}", sz.Width, sz.Height, ActualWidth, ActualHeight,
                        titleText.ActualWidth, stage.ActualWidth, stage.ActualHeight));
                    Snap("size_" + k + "_" + (int)sz.Width + "x" + (int)sz.Height);
                });
            }
            After(t += 300, () => { ToggleMini(); });
            After(t += 800, () => { HuntLog(string.Format("mini: {0:0}x{1:0} at {2:0},{3:0}", ActualWidth, ActualHeight, Left, Top)); Snap("size_mini"); ToggleMini(); });
            After(t += 800, () => { ToggleFullscreen(); ToggleMini(); });
            After(t += 800, () => { HuntLog(string.Format("full screen, then mini: {0:0}x{1:0}, fullscreen {2}, mini {3}", ActualWidth, ActualHeight, fullscreen, mini)); Snap("size_full_then_mini"); ToggleMini(); });
            After(t += 800, () => { HuntLog(string.Format("back: {0:0}x{1:0}, fullscreen {2}, mini {3}, state {4}", ActualWidth, ActualHeight, fullscreen, mini, WindowState)); Snap("size_back"); if (fullscreen) ToggleFullscreen(); });
            After(t += 500, HuntDone);
        }
    }
}
