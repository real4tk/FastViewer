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
using System.Windows.Media;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool huntTest;
        Action<ContextMenu, Button> huntMenuHook; // tests: a menu is drawn inside the window instead of popping up on the screen
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
                case "overlap": HuntOverlap(); break;
                case "audit": HuntAudit(); break;
                case "ux": HuntUx(); break;
                case "motion": huntMotion = true; HuntMotion(); break;
                case "updatemsg":
                    After(1500, () => CheckForUpdates(true));
                    After(2500, () => HuntLog("message after checking: '" + F<TextBlock>("ToastText").Text + "' (shown " + (F<FrameworkElement>("Toast").Visibility == Visibility.Visible) + ")"));
                    After(7000, () => { HuntLog("message after the quiet retry: '" + F<TextBlock>("ToastText").Text + "'"); HuntDone(); });
                    break;
                case "saveclose": HuntSaveClose(); break;
                case "trim": HuntTrim(); break;
                case "rates": HuntRates(); break;
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

        // ---------------------------------------------------------------- refresh rates: the same animation at 60-500 Hz

        // Plays each kind of animation with the frame timing of a 60, 144, 240 and 500 Hz screen (frames are
        // simulated, so no such screen is needed) and compares: how long it takes, where it is at the same
        // moments, the biggest single step, and where it ends.
        void HuntRates()
        {
            After(1000, () =>
            {
                HuntLog("this screen: " + Motion.ScreenRate + " Hz; WPF animation clock limit " +
                    System.Windows.Media.Animation.Timeline.GetDesiredFrameRate(new System.Windows.Media.Animation.DoubleAnimation()) + " fps");
                var kinds = new[] { "decelerate 250 ms (Photos)", "spring 280 ms (Liquid Glass)", "calm 220 ms (Midnight)", "follow 200 ms (zoom, wheel)", "momentum (pan)" };
                foreach (var kind in kinds)
                {
                    var lines = new List<string>();
                    double[] reference = null;
                    foreach (int hz in new[] { 60, 144, 240, 500 })
                    {
                        double dt = 1.0 / hz, t = 0, value = 0, biggest = 0;
                        var at = new double[5]; // value at 50, 100, 150, 200, 250 ms
                        var probe = new Border();
                        Func<bool> busy;
                        if (kind.StartsWith("follow"))
                        {
                            double x = 0;
                            Motion.Run("rateTest", step => { x += (1 - x) * Motion.Follow(step, 200); value = x; return 1 - x > 0.001; });
                            busy = () => Motion.Running("rateTest");
                        }
                        else if (kind.StartsWith("momentum"))
                        {
                            double x = 0, v = 1000;
                            Motion.Run("rateTest", step => { double slow = Math.Exp(-step / 0.16); x += v * 0.16 * (1 - slow); v *= slow; value = x / 160; return v > 20; });
                            busy = () => Motion.Running("rateTest");
                        }
                        else
                        {
                            System.Windows.Media.Animation.IEasingFunction ease = kind.StartsWith("spring") ? (System.Windows.Media.Animation.IEasingFunction)new SpringEase()
                                : kind.StartsWith("calm") ? (System.Windows.Media.Animation.IEasingFunction)new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                                : new BezierEase(0, 0, 0, 1);
                            double ms = kind.StartsWith("spring") ? 280 : kind.StartsWith("calm") ? 220 : 250;
                            Motion.FromTo(probe, OpacityProperty, 0, 1, ms, ease);
                            busy = () => Motion.Busy(probe);
                        }
                        int frames = 0;
                        while (busy() && frames < 5000)
                        {
                            double before = kind.StartsWith("follow") || kind.StartsWith("momentum") ? value : probe.Opacity;
                            Motion.SimulateFrame(dt);
                            t += dt;
                            frames++;
                            double now = kind.StartsWith("follow") || kind.StartsWith("momentum") ? value : probe.Opacity;
                            biggest = Math.Max(biggest, Math.Abs(now - before));
                            for (int k = 0; k < 5; k++) if (Math.Abs(t - (k + 1) * 0.05) < dt / 2 + 1e-9) at[k] = now;
                        }
                        double end = kind.StartsWith("follow") || kind.StartsWith("momentum") ? value : probe.Opacity;
                        for (int k = 0; k < 5; k++) if ((k + 1) * 0.05 > t + dt / 2) at[k] = end; // already finished by then
                        double worst = 0;
                        if (reference == null) reference = at;
                        else for (int k = 0; k < 5; k++) worst = Math.Max(worst, Math.Abs(at[k] - reference[k]));
                        lines.Add(string.Format("  {0,3} Hz: {1,4} frames, done after {2,4:0} ms, ends at {3:0.000}, biggest step {4:0.000}, at 50/100/150/200/250 ms: {5}{6}",
                            hz, frames, t * 1000, end, biggest, string.Join(" ", at.Select(a => a.ToString("0.000"))),
                            hz == 60 ? "" : string.Format("  (differs from 60 Hz by at most {0:0.000})", worst)));
                    }
                    HuntLog(kind);
                    foreach (var l in lines) HuntLog(l);
                }
                After(300, HuntDone);
            });
        }

        // ---------------------------------------------------------------- trim bar (Trim.cs)

        void HuntTrim()
        {
            Func<string> state = () => string.Format("A {0:0.000}, B {1:0.000}, video at {2:0.000}, active {3}, playing {4}, text '{5}', hint '{6}'",
                loopA.HasValue ? loopA.Value.TotalSeconds : -1, loopB.HasValue ? loopB.Value.TotalSeconds : -1,
                shown.Video.Position.TotalSeconds, trimActive, playing, F<TextBlock>("TrimText").Text, F<TextBlock>("TrimHint").Text);
            Action<char, double> drag = (which, seconds) =>
            {
                // as the mouse would: take hold, move there, let go
                trimDrag = which;
                trimGrab = 0;
                TrimDragTo(TrimX(seconds));
                trimDrag = '\0';
                TrimSeek(trimHeadAt, true);
                PlaceTrim();
            };
            int t = 1500;
            After(t, () =>
            {
                if (playing) TogglePlay();
                StartTrim();
                HuntLog("trim open: " + state() + "; control bar visible " + transport.IsVisible + ", trim bar " + F<FrameworkElement>("TrimBar").ActualWidth.ToString("0") + " wide");
            });
            After(t += 3000, () =>
            {
                HuntLog("filmstrip: " + (filmFrames == null ? "none" : filmFrames.Count(f => f != null) + " of " + filmFrames.Length + " pictures, " +
                    (filmFrames.FirstOrDefault(f => f != null) == null ? "" : filmFrames.First(f => f != null).PixelWidth + "x" + filmFrames.First(f => f != null).PixelHeight)));
                Snap("trim_open");
                drag('A', 3);
                HuntLog("dragged start to 3 s: " + state());
                drag('B', 6);
                HuntLog("dragged end to 6 s: " + state());
                drag('A', 9);
                HuntLog("dragged start past the end (to 9 s): " + state());
                drag('A', 2);
                drag('P', 0.5);
                HuntLog("clicked the strip before the start (0.5 s): " + state());
            });
            After(t += 700, () =>
            {
                Snap("trim_dragged");
                PressKey(Key.Right); PressKey(Key.Right); PressKey(Key.Right);
                HuntLog("right x3 (start, a frame is " + shown.FrameSeconds.ToString("0.0000") + " s): " + state());
                PressKey(Key.Tab);
                PressKey(Key.Left);
                HuntLog("Tab, left: " + state());
                NudgeTrim(1, true);
                HuntLog("Shift+right: " + state());
                shown.Video.Position = TimeSpan.FromSeconds(4);
                PressKey(Key.OemOpenBrackets);
                HuntLog("[ at 4 s: " + state());
                PressKey(Key.OemCloseBrackets);
                HuntLog("] at the same moment (can't make it shorter than " + TrimShortest + " s): " + state());
                drag('B', 5.5);
                PressKey(Key.PageDown);
                HuntLog("PageDown (next file) while trimming: still trimming " + trimming + ", file " + Path.GetFileName(shown.File));
                PressKey(Key.Space);
                HuntLog("Space: " + state());
            });
            var seen = new List<string>();
            for (int k = 0; k < 10; k++) After(t += 250, () => seen.Add(shown.Video.Position.TotalSeconds.ToString("0.00")));
            After(t += 50, () =>
            {
                HuntLog("playing the part (" + loopA.Value.TotalSeconds.ToString("0.00") + "-" + loopB.Value.TotalSeconds.ToString("0.00") + "): " + string.Join(", ", seen));
                Snap("trim_playing");
                PressKey(Key.Space);
                HuntLog("Space again: " + state());
                Width = 470;
            });
            After(t += 700, () =>
            {
                HuntLog("narrow window: trim bar " + F<FrameworkElement>("TrimBar").ActualWidth.ToString("0") + " wide, hint shown " + F<FrameworkElement>("TrimHint").IsVisible +
                    ", play words shown " + F<FrameworkElement>("TrimPlayText").IsVisible);
                Snap("trim_narrow");
                Width = 1100;
            });
            After(t += 700, () =>
            {
                double a = loopA.Value.TotalSeconds, b = loopB.Value.TotalSeconds;
                string target = Path.Combine(snapDir, "clip.mp4");
                HuntLog(string.Format("saving {0:0.000}-{1:0.000} ({2:0.000} s) to clip.mp4", a, b, b - a));
                RunConversion(shown.File, target, loopA, loopB, "Saving clip");
                EndTrim();
                HuntLog("after leaving: trimming " + trimming + ", loop " + loopA.HasValue + ", control bar visible " + transport.IsVisible);
            });
            Action waitSaved = null;
            waitSaved = () =>
            {
                if (converting) { After(300, waitSaved); return; }
                HuntLog("saved: message '" + F<TextBlock>("ToastText").Text + "'");
                StartTrim();
                HuntLog("trim again (same video): filmstrip ready at once " + (filmFrames != null && filmFrames.All(f => f != null)) + "; " + state());
                PressKey(Key.Escape);
                HuntLog("Esc: trimming " + trimming + ", full screen " + fullscreen);
                After(400, HuntDone);
            };
            After(t += 500, waitSaved);
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

        // ---------------------------------------------------------------- Settings under the top bar

        // Where an element is in the window, or Rect.Empty when it isn't showing.
        Rect HuntBox(FrameworkElement el)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.Opacity <= 0.01) return Rect.Empty;
            for (DependencyObject p = el; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            {
                var fe = p as UIElement;
                if (fe != null && fe.Opacity <= 0.01) return Rect.Empty;
            }
            return el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }

        // Every visible button and text of the top bar that sits on Settings' Back button or title.
        string HuntTopBarOverlaps()
        {
            var hits = new List<string>();
            var under = new[] { "SettingsBackBtn", "SettingsTitle" }.Select(n => new { Name = n, Box = HuntBox(F<FrameworkElement>(n)) }).ToList();
            var topBar = F<Panel>("TopBar");
            var parts = new List<FrameworkElement>();
            Action<DependencyObject> walk = null;
            walk = d =>
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                    if (c is Button || (c is TextBlock && ((TextBlock)c).Text.Length > 0 && ((FrameworkElement)c).Name == "TitleText")) parts.Add((FrameworkElement)c);
                    else walk(c);
                }
            };
            walk(topBar);
            bool barOnTop = Panel.GetZIndex(topBar) >= Panel.GetZIndex(F<FrameworkElement>("SettingsPage"));
            foreach (var p in parts)
            {
                var box = HuntBox(p);
                if (box.IsEmpty) continue;
                foreach (var u in under)
                    if (!u.Box.IsEmpty && box.IntersectsWith(u.Box))
                        hits.Add((string.IsNullOrEmpty(p.Name) ? p.GetType().Name : p.Name) + " on " + u.Name + (barOnTop ? "" : " (hidden behind it)"));
            }
            return hits.Count == 0 ? "no overlap" : "OVERLAP: " + string.Join(", ", hits);
        }

        void HuntOverlap()
        {
            int t = 1500;
            foreach (var look in new[] { "Photos", "Clean" })
                foreach (var full in new[] { false, true })
                    foreach (var w in full ? new double[] { 0 } : new double[] { 380, 520, 700, 860, 1100, 1440 })
                    {
                        string id = look; bool f = full; double width = w;
                        After(t += 150, () =>
                        {
                            lookId = id; lookMode = "Dark"; ApplyLook();
                            if (f != fullscreen) ToggleFullscreen();
                            if (!f) { Width = width; Height = 700; }
                            if (!settingsOpen) OpenSettings();
                            ShowOverlay();
                        });
                        After(t += 450, () =>
                        {
                            HuntLog(string.Format("{0,-6} {1,-11} {2,5:0} wide: {3}", id, f ? "full screen" : "window", ActualWidth, HuntTopBarOverlaps()));
                            if (width == 520 || width == 1440 || f) Snap("overlap_" + id + (f ? "_full" : "_" + width));
                        });
                    }
            After(t += 200, () => { if (settingsOpen) CloseSettings(); if (fullscreen) ToggleFullscreen(); HuntDone(); });
        }

        // ---------------------------------------------------------------- UI audit: every screen, look, size and scale

        // The visible buttons, sliders, switches and texts (texts inside buttons count as the button).
        List<FrameworkElement> HuntLeaves()
        {
            var list = new List<FrameworkElement>();
            Action<DependencyObject> walk = null;
            walk = d =>
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i) as FrameworkElement;
                    if (c == null || !c.IsVisible) continue;
                    if (c is Button || c is System.Windows.Controls.Primitives.ToggleButton || c is Slider || c is MenuItem) { list.Add(c); continue; }
                    var tb = c as TextBlock;
                    if (tb != null) { if (!string.IsNullOrWhiteSpace(tb.Text)) list.Add(tb); continue; }
                    walk(c);
                }
            };
            walk(root);
            return list;
        }

        // The page or bar (child of the window's main grid) an element belongs to.
        FrameworkElement HuntLayer(DependencyObject d)
        {
            while (d != null)
            {
                var p = System.Windows.Media.VisualTreeHelper.GetParent(d);
                if (p == root) return d as FrameworkElement;
                d = p;
            }
            return null;
        }

        // Hidden under a page that's drawn on top of it (Settings, the grid, the Delete dialog).
        bool HuntCovered(FrameworkElement el, Rect box)
        {
            var layer = HuntLayer(el);
            int mine = layer == null ? -1 : root.Children.IndexOf(layer);
            foreach (var name in new[] { "GridPage", "SettingsPage", "Dialog" })
            {
                var page = F<FrameworkElement>(name);
                if (page == layer || !page.IsVisible) continue;
                int theirs = root.Children.IndexOf(page);
                bool above = Panel.GetZIndex(page) > (layer == null ? 0 : Panel.GetZIndex(layer)) ||
                             (Panel.GetZIndex(page) == (layer == null ? 0 : Panel.GetZIndex(layer)) && theirs > mine);
                if (above && HuntBox(page).Contains(new Point(box.X + box.Width / 2, box.Y + box.Height / 2))) return true;
            }
            return false;
        }

        static bool HuntInside(DependencyObject child, DependencyObject parent)
        {
            for (var d = child; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d)) if (d == parent) return true;
            return false;
        }

        static string HuntName(FrameworkElement el)
        {
            if (!string.IsNullOrEmpty(el.Name)) return el.Name;
            var tb = el as TextBlock;
            if (tb != null) return "\"" + (tb.Text.Length > 30 ? tb.Text.Substring(0, 30) + "..." : tb.Text) + "\"";
            var b = el as ContentControl;
            if (b != null && b.Content is string) return el.GetType().Name + " \"" + b.Content + "\"";
            var mi = el as MenuItem;
            if (mi != null && mi.Header is string) return "menu \"" + mi.Header + "\"";
            var tip = el.ToolTip as string;
            return el.GetType().Name + (tip != null ? " (" + tip + ")" : "");
        }

        // Overlapping controls, text that doesn't fit, controls cut off by the window edge.
        List<string> HuntLayoutProblems()
        {
            var problems = new List<string>();
            var leaves = HuntLeaves().Select(e => new { El = e, Box = HuntBox(e) }).Where(x => !x.Box.IsEmpty && !HuntCovered(x.El, x.Box)).ToList();
            var win = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            double dpi = Dpi();
            for (int i = 0; i < leaves.Count; i++)
            {
                var a = leaves[i];
                if (a.Box.Right > win.Right + 1 || a.Box.Bottom > win.Bottom + 1 || a.Box.Left < -1 || a.Box.Top < -1)
                {
                    // inside a scrolling list it's just scrolled off
                    bool scrolled = false;
                    for (var d = (DependencyObject)a.El; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d)) if (d is ScrollViewer) { scrolled = true; break; }
                    if (!scrolled) problems.Add("cut off by the window edge: " + HuntName(a.El));
                }
                var tb = a.El as TextBlock;
                if (tb != null && tb.Inlines.Count <= 1)
                {
                    var ft = new System.Windows.Media.FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, tb.FlowDirection,
                        new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dpi);
                    if (tb.TextWrapping == TextWrapping.NoWrap && ft.WidthIncludingTrailingWhitespace > tb.ActualWidth + 1.5)
                        problems.Add((tb.TextTrimming == TextTrimming.None ? "text cut off: " : "text shortened with ...: ") + HuntName(tb) +
                                     string.Format(" (needs {0:0}, has {1:0})", ft.WidthIncludingTrailingWhitespace, tb.ActualWidth));
                }
                for (int j = i + 1; j < leaves.Count; j++)
                {
                    var b = leaves[j];
                    if (HuntInside(a.El, b.El) || HuntInside(b.El, a.El)) continue;
                    var x = Rect.Intersect(a.Box, b.Box);
                    if (x.IsEmpty || x.Width * x.Height < 6) continue;
                    if (a.El is MenuItem && b.El is MenuItem) continue;
                    problems.Add("overlap: " + HuntName(a.El) + " / " + HuntName(b.El) + string.Format(" ({0:0}x{1:0} px)", x.Width, x.Height));
                }
            }
            return problems;
        }

        static double HuntLum(Color c)
        {
            Func<double, double> ch = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }

        static Color HuntOver(Color top, Color under)
        {
            double a = top.A / 255.0;
            return Color.FromRgb((byte)(top.R * a + under.R * (1 - a)), (byte)(top.G * a + under.G * (1 - a)), (byte)(top.B * a + under.B * (1 - a)));
        }

        // Contrast of the look's text colours on its backgrounds (4.5 is the usual minimum for normal text, 3 for large text and icons).
        void HuntContrast(string label)
        {
            Func<string, Color?> col = k => { var b = Application.Current.Resources[k] as SolidColorBrush; return b == null ? (Color?)null : b.Color; };
            var window = col("WindowBg") ?? Colors.Black;
            var pairs = new[]
            {
                new[] { "TextMain", "PageBg" }, new[] { "TextSecondary", "PageBg" }, new[] { "TextSecondary", "CardBg" }, new[] { "TextDim", "CardBg" },
                new[] { "TextDim", "MenuBg" }, new[] { "TextMain", "MenuBg" }, new[] { "ControlText", "TopBarBg" }, new[] { "TextSecondary", "BottomBarBg" },
                new[] { "TextMain", "FloatBg" }, new[] { "AccentText", "Accent" }, new[] { "ControlText", "ControlFill" }, new[] { "TextMain", "TipBg" },
                new[] { "TextMain", "ArrowBg" }, new[] { "TextSecondary", "StageBg" }
            };
            var parts = new List<string>();
            foreach (var p in pairs)
            {
                var fg = col(p[0]); var bg = col(p[1]);
                if (fg == null || bg == null) continue;
                // see-through backgrounds: over the window colour, and for things over a picture also over white
                var bgs = new List<Color> { HuntOver(bg.Value, window) };
                if (p[1] == "FloatBg" || p[1] == "ArrowBg" || p[1] == "TopBarBg") bgs.Add(HuntOver(bg.Value, Colors.White));
                double worst = bgs.Select(b => { var f = HuntOver(fg.Value, b); double l1 = HuntLum(f), l2 = HuntLum(b); return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05); }).Min();
                parts.Add(string.Format("{0} on {1} {2:0.0}{3}", p[0], p[1], worst, worst < 3 ? " LOW" : worst < 4.5 ? " (large text only)" : ""));
            }
            HuntLog("contrast " + label + ": " + string.Join("; ", parts));
        }

        // Buttons a screen reader can't name, and what Tab reaches.
        void HuntAccessibility(string where)
        {
            var unnamed = new List<string>();
            int buttons = 0, focusable = 0;
            Action<DependencyObject> walk = null;
            walk = d =>
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                    var el = c as Control;
                    if (el != null && el.IsVisible && (el is Button || el is CheckBox || el is Slider))
                    {
                        buttons++;
                        if (el.Focusable && el.IsTabStop) focusable++;
                        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(el);
                        string name = peer == null ? "" : peer.GetName();
                        if (string.IsNullOrWhiteSpace(name)) unnamed.Add(HuntName(el));
                        continue;
                    }
                    walk(c);
                }
            };
            walk(root);
            HuntLog(string.Format("screen reader, {0}: {1} controls, {2} reachable with Tab, {3} without a name: {4}", where, buttons, focusable, unnamed.Count, string.Join(", ", unnamed.Distinct())));
            var order = new List<string>();
            Keyboard.Focus(this);
            for (int k = 0; k < 12; k++)
            {
                var before = Keyboard.FocusedElement;
                var from = Keyboard.FocusedElement as UIElement ?? this;
                from.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                var now = Keyboard.FocusedElement as FrameworkElement;
                if (now == null || now == before) break;
                order.Add(HuntName(now));
            }
            HuntLog("Tab, " + where + ": " + (order.Count == 0 ? "reaches nothing" : string.Join(" > ", order)));
        }

        void HuntAudit()
        {
            // --pattern picks the size set: "sizes" (every size, main states), "scales" (125%, 150%), else "full" (every state at medium size)
            var audit = huntPattern == "mixed" ? "full" : huntPattern;
            if (audit == "contrast") // every look, dark and light
            {
                After(1500, () =>
                {
                    foreach (var lk in new[] { "Photos", "Clean", "Glass", "Midnight" })
                        foreach (var mode in new[] { "Dark", "Light" })
                        {
                            lookId = lk; lookMode = mode; ApplyLook();
                            HuntContrast(lk + " " + mode.ToLowerInvariant());
                        }
                    HuntDone();
                });
                return;
            }
            var looks = new[] { "Photos", "Clean" };
            int photo = files.FindIndex(f => Path.GetFileName(f).StartsWith("01")), video = files.FindIndex(f => Path.GetFileName(f).StartsWith("02")),
                gif = files.FindIndex(f => f.EndsWith(".gif")), broken = files.FindIndex(f => Path.GetFileName(f).StartsWith("04")),
                longName = files.FindIndex(f => Path.GetFileName(f).StartsWith("05"));
            var steps = new List<KeyValuePair<string, Action>>(); // state name, set-up
            Action reset = () =>
            {
                huntMenuHook = null;
                if (huntMenuHost != null) { root.Children.Remove(huntMenuHost); huntMenuHost = null; }
                dialog.Visibility = Visibility.Collapsed;
                HideToast(null);
                if (trimming) EndTrim();
                if (settingsOpen) CloseSettings();
                CloseGrid();
                if (infoOpen) ToggleInfo();
                if (mini) ToggleMini();
                if (slideshow) StopSlideshow();
                if (fullscreen) ToggleFullscreen();
                if (showStrip) { showStrip = false; ApplyStrip(); }
            };
            Action<int> show = i => { if (i >= 0) ShowIndex(i, 1); };
            Action pause = () => { if (playing) TogglePlay(); };
            Action<string, Action> state = (n, a) => steps.Add(new KeyValuePair<string, Action>(n, a));
            Action<string, string> menuState = (n, which) => state(n, () =>
            {
                huntMenuHook = (menu, button) =>
                {
                    // A ContextMenu can't sit in the window: its items move to a Menu drawn the same way (Theme.xaml).
                    var look = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                        "<ControlTemplate TargetType='Menu' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                        "<Border Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuEdge}' BorderThickness='1' " +
                        "CornerRadius='{DynamicResource Radius.Float}' Padding='4' MinWidth='220'><StackPanel IsItemsHost='True'/></Border></ControlTemplate>");
                    var copy = new Menu { Template = look, FontSize = 14 };
                    copy.SetResourceReference(Control.ForegroundProperty, "TextMain");
                    copy.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
                    var items = menu.Items.Cast<object>().ToList();
                    menu.Items.Clear();
                    foreach (var it in items) copy.Items.Add(it);
                    var host = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = copy };
                    Grid.SetRowSpan(host, 3);
                    Panel.SetZIndex(host, 100);
                    var p = button.TranslatePoint(new Point(0, 0), root);
                    root.Children.Add(host);
                    host.UpdateLayout();
                    double x = Math.Max(0, Math.Min(p.X, root.ActualWidth - host.ActualWidth)), y = which == "up" ? p.Y - host.ActualHeight - 4 : p.Y + button.ActualHeight + 4;
                    host.Margin = new Thickness(x, Math.Max(0, y), 0, 0);
                    huntMenuHost = host;
                };
            });

            state("photo", () => show(photo));
            state("photo_controls_hidden", () => { show(photo); });
            state("video_playing", () => show(video));
            state("video_paused", () => { show(video); });
            state("gif", () => show(gif));
            if (audit == "full")
            {
                state("info_pane", () => { show(photo); ToggleInfo(); });
                state("strip", () => { show(photo); showStrip = true; ApplyStrip(); });
                state("grid", () => { show(photo); OpenGrid(); });
                state("grid_names", () => { show(photo); gridNames = true; OpenGrid(); });
                state("broken_file", () => show(broken));
                state("long_name", () => show(longName));
                state("delete_dialog", () => { show(photo); AskDelete(); });
                state("toast_update", () => { show(photo); ShowToast("FastViewer 1.16.0 is available.", "Update now", delegate { }, 0, "update"); });
                state("toast_resume", () => { show(video); ShowToast("Continuing where you left off (02:31)", "Start over", delegate { }, 0, "resume"); });
                state("trim", () => { show(video); StartTrim(); });
                state("full_photo", () => { show(photo); ToggleFullscreen(); });
                state("full_video", () => { show(video); ToggleFullscreen(); });
                state("slideshow", () => { show(photo); ToggleSlideshow(); });
                state("mini_video", () => { show(video); ToggleMini(); });
                state("mini_photo", () => { show(photo); ToggleMini(); });
                menuState("menu_more", "down");
                menuState("menu_video", "up");
                menuState("menu_zoom", "up");
                foreach (var page in settingsPages.Select(p => ((TextBlock)p.Value.Children[0]).Text).ToList())
                {
                    var pg = page;
                    state("settings_" + pg.Replace(" ", ""), () =>
                    {
                        show(photo);
                        OpenSettings();
                        settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == pg).Key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                }
                state("empty", () => { LetGoOfFiles(); });
            }
            else
            {
                state("settings_Viewing", () =>
                {
                    show(photo); OpenSettings();
                    settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Viewing").Key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                state("grid", () => { show(photo); OpenGrid(); });
                state("delete_dialog", () => { show(photo); AskDelete(); });
                state("toast_update", () => { show(photo); ShowToast("FastViewer 1.16.0 is available.", "Update now", delegate { }, 0, "update"); });
                state("full_video", () => { show(video); ToggleFullscreen(); });
            }

            var profiles = audit == "sizes"
                ? new[] { "380x260", "640x440", "1100x720", "1600x1000", "max" }
                : audit == "scales" ? new[] { "1100x720@1.25", "1000x640@1.5" } : new[] { "1100x720" };
            string folder = files.Count > 0 ? files[0] : null;
            int t = 1500;
            foreach (var look in looks)
                foreach (var prof in profiles)
                {
                    string lk = look, pf = prof;
                    After(t += 100, () =>
                    {
                        reset();
                        lookId = lk; lookMode = "Dark"; ApplyLook();
                        double scale = pf.Contains("@") ? double.Parse(pf.Split('@')[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
                        root.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
                        if (pf == "max") WindowState = WindowState.Maximized;
                        else
                        {
                            WindowState = WindowState.Normal;
                            var wh = pf.Split('@')[0].Split('x');
                            Width = double.Parse(wh[0]) * scale; Height = double.Parse(wh[1]) * scale;
                        }
                        HuntContrast(lk + " dark");
                    });
                    if (pf == profiles[0])
                    {
                        After(t += 400, () => { show(photo); HuntAccessibility(lk + " viewer"); OpenSettings(); });
                        After(t += 500, () => { HuntAccessibility(lk + " Settings"); CloseSettings(); lookMode = "Light"; ApplyLook(); HuntContrast(lk + " light"); lookMode = "Dark"; ApplyLook(); });
                    }
                    foreach (var st in steps)
                    {
                        var s = st;
                        After(t += 150, () =>
                        {
                            reset();
                            if (files.Count == 0 && folder != null) OpenPath(folder);
                            s.Value();
                            if (s.Key == "menu_video") show(video);
                            if (s.Key == "menu_more" || s.Key == "menu_zoom") show(photo);
                            ShowOverlay();
                        });
                        After(t += s.Key.StartsWith("video") || s.Key == "menu_video" || s.Key.Contains("gif") || s.Key.StartsWith("full_video") || s.Key.StartsWith("mini_video") ? 1600 : 900, () =>
                        {
                            if (s.Key == "video_paused") pause();
                            if (s.Key == "photo_controls_hidden") { idleTimer.Stop(); HideOverlay(); }
                            if (s.Key == "menu_more") ShowMoreMenu();
                            if (s.Key == "menu_video") { pause(); ShowOverlay(); ShowVideoMenu(); }
                            if (s.Key == "menu_zoom") ShowZoomMenu();
                        });
                        After(t += s.Key == "photo_controls_hidden" ? 700 : 250, () =>
                        {
                            string name = lk + "_" + pf.Replace("@", "_at_") + "_" + s.Key;
                            var problems = HuntLayoutProblems();
                            HuntLog(name + ": " + (problems.Count == 0 ? "ok" : string.Join(" | ", problems.Distinct())));
                            Snap(name);
                        });
                    }
                }
            After(t += 300, () => { reset(); root.LayoutTransform = Transform.Identity; WindowState = WindowState.Normal; HuntDone(); });
        }
        Border huntMenuHost;

        // ---------------------------------------------------------------- UX fixes that pictures can't show

        void HuntUx()
        {
            Func<string, string> tip = n => (F<FrameworkElement>(n).ToolTip as string) ?? "-";
            Func<string> menuKeys = () =>
            {
                string r = "";
                huntMenuHook = (menu, button) => { r = string.Join(", ", menu.Items.OfType<MenuItem>().Where(m => m.InputGestureText.Length > 0).Select(m => m.Header + "=" + m.InputGestureText)); menu.Items.Clear(); };
                ShowVideoMenu();
                huntMenuHook = null;
                return r;
            };
            int t = 1500;
            int video = files.FindIndex(IsVideoFile);
            After(t, () =>
            {
                HuntLog("tooltips: next '" + tip("NextBtn") + "', delete '" + tip("DeleteBtn") + "', repeat '" + tip("LoopBtn") + "', back '" + tip("BackBtn") + "', slideshow '" + tip("SlideshowBtn") + "'");
                HuntLog("title tooltip: '" + (titleText.ToolTip as string) + "'");
                if (video >= 0) ShowIndex(video, 1);
            });
            After(t += 1500, () =>
            {
                if (playing) TogglePlay();
                HuntLog("video menu keys: " + menuKeys());
                // rebind: Next gets N, Repeat gets Q, Trim loses its key
                OpenSettings();
                StartCapture(shortcuts.First(s => s.Id == "next"), 0); CaptureKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.N) { RoutedEvent = Keyboard.KeyDownEvent });
                StartCapture(shortcuts.First(s => s.Id == "repeat"), 0); CaptureKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Q) { RoutedEvent = Keyboard.KeyDownEvent });
                RemoveKey(shortcuts.First(s => s.Id == "trim"), 0);
                StartCapture(shortcuts.First(s => s.Id == "mute"), 1);
                bool tabTaken = CaptureKey(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent });
                HuntLog("Tab while a key box waits: taken as a shortcut " + tabTaken + ", mute keys [" + string.Join(",", shortcuts.First(s => s.Id == "mute").Keys) + "]");
                CloseSettings();
                HuntLog("after rebinding: next '" + tip("NextBtn") + "', repeat '" + tip("LoopBtn") + "'; video menu keys: " + menuKeys());
                foreach (var s in shortcuts) s.Keys = s.Defaults.ToList();
                RebuildShortcutMap();
                HuntLog("file buttons in Settings: " + string.Join(", ", new[] { "DeleteBtn", "CopyBtn", "RotateBtn", "OpenWithBtn" }.Select(n => { OpenSettings(); bool on = F<Button>(n).IsEnabled; CloseSettings(); return n + (on ? " on" : " off"); })));
            });
            // Preload now and from another window (that used to crash the next switch)
            After(t += 500, () =>
            {
                HuntLog("players: " + slots.Length + " (preload " + preloadAhead + ")");
                SetPreload(3);
                HuntLog("Preload set to 3: " + slots.Length + " players");
            });
            for (int k = 0; k < 15; k++) After(t += 120, () => Go(1));
            After(t += 1500, () =>
            {
                HuntLog("15 switches with preload 3: showing " + HuntSlot(shown) + ", ready players " + slots.Count(x => x.Ready));
                SetPreload(1);
                HuntLog("Preload set to 1: " + slots.Length + " players");
                preloadAhead = 3; // what another window's saved setting does (SyncSettings loads it)
                if (slots.Length != 2 * preloadAhead + 2) SetPreload(preloadAhead);
                HuntLog("another window set Preload to 3: " + slots.Length + " players");
            });
            for (int k = 0; k < 15; k++) After(t += 120, () => Go(1));
            After(t += 1500, () =>
            {
                HuntLog("15 more switches: showing " + HuntSlot(shown) + " (no crash)");
                ToggleSlideshow();
                HuntLog("slideshow on: button '" + tip("SlideshowBtn") + "', message '" + F<TextBlock>("ToastText").Text + "', min/max buttons " + F<FrameworkElement>("MinBtn").Visibility + "/" + F<FrameworkElement>("MaxBtn").Visibility);
                StopSlideshow();
                if (fullscreen) ToggleFullscreen();
                HuntLog("slideshow off: button '" + tip("SlideshowBtn") + "', min/max " + F<FrameworkElement>("MinBtn").Visibility + "/" + F<FrameworkElement>("MaxBtn").Visibility);
                miniVolume = false; ToggleMini();
                HuntLog("mini player, volume button off (default): " + F<FrameworkElement>("MiniVolBtn").Visibility);
                miniVolume = true; UpdateControls();
                double v0 = volume; F<Button>("MiniVolBtn").RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                bool m0 = muted; F<Button>("MiniVolBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HuntLog("volume button on: " + F<FrameworkElement>("MiniVolBtn").Visibility + ", wheel up: volume " + v0.ToString("0.00") + " -> " + volume.ToString("0.00") + ", click: muted " + m0 + " -> " + muted + ", name '" + System.Windows.Automation.AutomationProperties.GetName(F<Button>("MiniVolBtn")) + "'");
                ToggleMute(); ToggleMini(); miniVolume = false;
                StartTrim();
                HuntLog("trim: '" + F<TextBlock>("TrimText").Text + "'");
                EndTrim();
            });
            After(t += 300, HuntDone);
        }

        // ---------------------------------------------------------------- motion: frame times of every animation, and idle CPU

        bool huntMotion; // lets the window crossfade and fade-through run in this test (other tests skip them)

        void HuntMotion()
        {
            var me = Process.GetCurrentProcess();
            Func<double> cpu = () => { me.Refresh(); return me.TotalProcessorTime.TotalMilliseconds; };
            var frames = new List<double>();
            TimeSpan last = TimeSpan.Zero;
            EventHandler onFrame = (o, e) =>
            {
                var now = ((RenderingEventArgs)e).RenderingTime;
                if (!Motion.AnyRunning) { last = TimeSpan.Zero; return; } // only while something is moving
                if (now == last) return;
                if (last != TimeSpan.Zero) frames.Add((now - last).TotalMilliseconds);
                last = now;
            };
            string segment = null;
            Action<string> begin = name => { segment = name; frames.Clear(); last = TimeSpan.Zero; CompositionTarget.Rendering += onFrame; };
            Action end = () =>
            {
                CompositionTarget.Rendering -= onFrame;
                if (frames.Count < 3) { HuntLog(string.Format("{0,-34} (no frames)", segment)); return; }
                var s = frames.OrderBy(x => x).ToList();
                double med = s[s.Count / 2], p95 = s[(int)(s.Count * 0.95)], max = s[s.Count - 1];
                int dropped = frames.Count(x => x > med * 1.7);
                HuntLog(string.Format("{0,-34} {1,4} frames  median {2,5:0.0} ms  p95 {3,5:0.0}  max {4,6:0.0}  long frames {5}", segment, frames.Count, med, p95, max, dropped));
            };
            int video = files.FindIndex(IsVideoFile), photo = files.FindIndex(f => !IsVideoFile(f) && !f.EndsWith(".gif"));
            var steps = new List<KeyValuePair<string, Action>>();
            Action<string, Action> step = (n, a) => steps.Add(new KeyValuePair<string, Action>(n, a));
            step("open Settings", () => OpenSettings());
            step("switch Settings pages", () => { foreach (var p in settingsPages.Take(3)) p.Key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            step("close Settings", () => CloseSettings());
            step("message drops in", () => ShowToast("FastViewer 9.9.9 is available.", "Update now", delegate { }, 0, "update"));
            step("message leaves", () => HideToast(null));
            step("delete dialog opens", () => AskDelete());
            step("delete dialog closes", () => HideDeleteDialog());
            step("zoom in (3 wheel notches)", () => { ZoomBy(1.25, StageCenter()); ZoomBy(1.25, StageCenter()); ZoomBy(1.25, StageCenter()); });
            step("pan glide after a drag", () =>
            {
                var s = shown;
                var v = new Vector(900, 300);
                Motion.Run("pan", dt => { double slow = Math.Exp(-dt / 0.16); s.Pan.X += v.X * 0.16 * (1 - slow); s.Pan.Y += v.Y * 0.16 * (1 - slow); ClampPan(s); v *= slow; return v.Length > 20; });
            });
            step("fit to window", () => SetZoom(1, StageCenter()));
            step("rotate", () => Rotate());
            step("rotate back (3 more)", () => { Rotate(); Rotate(); Rotate(); });
            step("controls fade out", () => { idleTimer.Stop(); HideOverlay(); });
            step("controls fade in", () => ShowOverlay());
            step("grid opens (photo into its tile)", () => OpenGrid());
            step("grid size change", () => SetGridSize(gridSize == 1 ? 2 : 1));
            step("grid selection glides", () => { SelectGridTile(Math.Min(gridFiles.Count - 1, gridSelected + 1)); });
            step("pick from grid (grows from tile)", () => PickFromGrid(gridSelected));
            step("strip appears", () => { showStrip = true; ApplyStrip(); });
            step("strip glides with 5 switches", () => { for (int k = 0; k < 5; k++) Go(1); });
            step("info pane slides in", () => { if (!infoOpen) ToggleInfo(); });
            step("info pane closes", () => { if (infoOpen) ToggleInfo(); });
            step("look change (window crossfade)", () => { CrossfadeWindow(250); lookId = lookId == "Photos" ? "Clean" : "Photos"; ApplyLook(); });
            step("full screen (fade through)", () => ToggleFullscreenSmoothly());
            step("leave full screen", () => ToggleFullscreenSmoothly());
            step("video: play/pause morph", () => { ShowIndex(Math.Max(0, video), 1); });
            step("pause (button dips, icon morphs)", () => { if (!playing) TogglePlay(); TogglePlay(); });
            step("volume slider opens", () => { volumeTestOpen = false; var keep = snapDir; SetVolumeSlider(true); });

            int t = 2000;
            double idleBefore = 0, idleAfter = 0, c0 = 0;
            After(t, () => { if (photo >= 0) ShowIndex(photo, 1); });
            After(t += 1500, () => { c0 = cpu(); });
            After(t += 5000, () => { idleBefore = (cpu() - c0) / 5.0; HuntLog(string.Format("screen {4} Hz, animations at {5} fps; motion level {0}, look {1}; idle before: {2:0.0} ms CPU per second, movers running {3}", Motion.Level, lookId, idleBefore, Motion.AnyRunning, Motion.ScreenRate, System.Windows.Media.Animation.Timeline.GetDesiredFrameRate(new System.Windows.Media.Animation.DoubleAnimation()))); });
            foreach (var s in steps)
            {
                var st = s;
                After(t += 100, () => { begin(st.Key); try { st.Value(); } catch (Exception ex) { HuntLog("  FAILED " + st.Key + ": " + ex.Message); } });
                After(t += 700, end);
            }
            After(t += 300, () => { SetVolumeSlider(false); if (showStrip) { showStrip = false; ApplyStrip(); } if (playing) TogglePlay(); if (photo >= 0) ShowIndex(photo, 1); });
            After(t += 1500, () => { HideOverlay(true); c0 = cpu(); });
            After(t += 5000, () =>
            {
                idleAfter = (cpu() - c0) / 5.0;
                HuntLog(string.Format("idle after: {0:0.0} ms CPU per second (before {1:0.0}), movers still running {2}, frame hook attached {3}",
                    idleAfter, idleBefore, Motion.AnyRunning, Motion.AnyRunning));
                HuntDone();
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
