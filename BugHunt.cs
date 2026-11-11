// FastViewer: bug-hunt test modes (FastViewer.exe <file> --hunt <output folder> --case <name> [--count n] [--every ms] [--pattern p]).
// Cases: torture (very fast switching with checks of what's on screen), edge (odd files), churn (files changing in the
// open folder; give it a scratch folder), big (thousands of files), dump (what was loaded from --settings), keys,
// dialog, rotate (scratch folder), loop, sizes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                case "toggles": HuntToggles(); break;
                case "scrub": HuntScrub(); break;
                case "custom": HuntCustom(); break;
                case "squish": HuntSquish(); break;
                case "squishall": HuntSquishAll(); break;
                case "skipicons": HuntSkipIcons(); break;
                case "preloadghost": HuntPreloadGhost(); break;
                case "showcase": HuntShowcase(); break;
                case "wheel": HuntWheel(); break;
                case "tips": HuntTips(); break;
                case "music": HuntMusic(); break;
                case "cover": HuntCover(); break;
                case "vturn": HuntVideoTurn(); break;
                case "share": HuntShare(); break;
                case "crop": HuntCrop(); break;
                case "tracks": HuntTracks(); break;
                case "gridsearch": HuntGridSearch(); break;
                case "menus": HuntMenus(); break;
                case "switches": HuntSwitches(); break;
                case "taskbar": HuntTaskbar(); break;
                case "away": HuntAway(); break;
                case "convert": HuntConvert(); break;
                case "rotleft": HuntRotateLeft(); break;
                case "subs": HuntSubs(); break;
                case "addons": HuntAddons(); break;
                case "captions": HuntCaptions(); break;
                case "captionsall": HuntCaptionsAll(); break;
                case "burn": HuntBurn(); break;
                case "videomenu": HuntVideoMenu(); break;
                case "smooth": HuntSmooth(); break;
                case "rotsave": HuntRotSave(); break;
                case "showmigrate": HuntShowMigrate(); break;
                case "minivol":
                    After(1500, () => { if (playing) TogglePlay(); miniVolume = true; ToggleMini(); });
                    After(2500, () => { ShowOverlay(); SetVolume(0.45); });
                    After(2900, () => { Snap("mini_volume_level"); HuntLog("mini player " + mini + ", " + ActualWidth.ToString("0") + "x" + ActualHeight.ToString("0") + ", level '" + F<TextBlock>("MiniVolText").Text + "', top message " + (toastKind ?? "none")); HuntDone(); });
                    break;
                case "miniclear": // folder: a see-through PNG first, then an ordinary photo and a video (wraps around)
                    After(1500, () => { if (playing) TogglePlay(); ToggleMini(); });
                    After(2500, () => { HuntLog(MiniClearState("see-through png")); Go(1); });
                    After(3500, () => { HuntLog(MiniClearState("next file")); Go(1); });
                    After(4500, () => { HuntLog(MiniClearState("video") + ", video drawn " + (shown.IsVideo && shown.Video.NaturalVideoWidth > 0)); Go(1); });
                    After(5500, () => { HuntLog(MiniClearState("back to png")); Go(-1); });
                    After(6500, () => { HuntLog(MiniClearState("video again") + ", video drawn " + (shown.IsVideo && shown.Video.NaturalVideoWidth > 0)); Go(1); });
                    After(7500, () => { HuntLog(MiniClearState("png again")); ToggleMini(); });
                    After(8500, () => { HuntLog(MiniClearState("left mini")); ToggleFullscreen(); });
                    After(9500, () => { HuntLog("full screen: frame " + chrome.GlassFrameThickness.Left + ", square region " + rounded + (chrome.GlassFrameThickness.Left == 0 && rounded ? "" : " FAIL")); ToggleFullscreen(); });
                    After(10500, () => { HuntLog("normal window: frame " + chrome.GlassFrameThickness.Left + ", region " + rounded + (chrome.GlassFrameThickness.Left == (seeThrough ? -1 : 1) && rounded == seeThrough ? "" : " FAIL")); HuntDone(); });
                    break;
                default: After(500, () => { HuntLog("unknown case " + huntCase); HuntDone(); }); break; // (not while the window is still being set up)
            }
        }

        readonly List<string> huntLog = new List<string>();
        void HuntLog(string text) { huntLog.Add(text); }

        // The mini player's see-through state, and how clear it really is: the window drawn over white and over
        // black, measured outside the test picture's circle (anything faint there shows as a grey box on screen).
        string MiniClearState(string step)
        {
            int w = Math.Max(1, (int)ActualWidth), h = Math.Max(1, (int)ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var overlay = F<FrameworkElement>("Overlay"); double was = overlay.Opacity;
            overlay.Opacity = 0; UpdateLayout(); // the mini player's own buttons and arrows are meant to show
            rtb.Render(this);
            overlay.Opacity = was;
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            int outside = 0, tinted = 0, darkest = 255, brightest = 0;
            double r = 0.27 * w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = x - w / 2.0, dy = y - h / 2.0;
                    if (dx * dx + dy * dy < r * r) continue; // the picture itself
        
                    outside++;
                    int i = (y * w + x) * 4, a = px[i + 3];
                    int overWhite = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) + 255 - a; // premultiplied
                    int overBlack = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                    if (overWhite < 255 || overBlack > 0) tinted++;
                    darkest = Math.Min(darkest, overWhite);
                    brightest = Math.Max(brightest, overBlack);
                }
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            return step + ": " + Path.GetFileName(shown.File) + ", mini " + mini + ", miniClear " + miniClear +
                ", stage " + stage.Background + ", cover " + cover.Visibility + ", window bg " + Background + ", frame " + chrome.GlassFrameThickness.Left +
                ", composition " + src.CompositionTarget.BackgroundColor + ", region " + rounded + ", " + w + "x" + h +
                ", around the picture: " + (100.0 * tinted / Math.Max(1, outside)).ToString("0.0") + "% not clear, over white darkest " + darkest +
                ", over black brightest " + brightest + (miniClear && tinted > 0 ? " FAIL" : "") + (miniClear && tinted > 0 ? Painters(this, 1.0, "") : "");
        }

        // Every visible element that draws something (a background, picture, video or shape), with its opacity
        // after all its parents' opacities: what can leave a faint box behind a see-through picture.
        string Painters(DependencyObject o, double opacity, string path)
        {
            var fe = o as FrameworkElement;
            if (fe != null)
            {
                if (fe.Visibility != Visibility.Visible) return "";
                opacity *= fe.Opacity;
                if (opacity <= 0 || fe.ActualWidth < 1 || fe.ActualHeight < 1) return "";
            }
            string found = "";
            Brush b = o is Panel ? ((Panel)o).Background : o is Border ? ((Border)o).Background : o is Control ? ((Control)o).Background
                : o is System.Windows.Shapes.Shape ? ((System.Windows.Shapes.Shape)o).Fill : null;
            var sc = b as SolidColorBrush;
            bool paints = (b != null && (sc == null || sc.Color.A > 0)) || (o is Image && ((Image)o).Source != null) || (o is MediaElement && ((MediaElement)o).Source != null);
            if (paints && fe != null)
                found += "\n   " + o.GetType().Name + " " + fe.Name + (b != null ? " " + b : "") + " opacity " + opacity.ToString("0.###") + " " +
                    fe.ActualWidth.ToString("0") + "x" + fe.ActualHeight.ToString("0") + " in " + path;
            string here = fe != null && fe.Name != "" ? fe.Name : (fe != null ? o.GetType().Name : "");
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
                found += Painters(VisualTreeHelper.GetChild(o, i), opacity, here == "" ? path : path + "/" + here);
            return found;
        }

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
                bool content = s != null && (s.Failed || ((!s.IsVideo || s.IsAudio) && s.Picture.Source != null) ||
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

        // ---------------------------------------------------------------- the custom look (CustomLook.cs); use with --settings <scratch.ini>

        void HuntCustom()
        {
            var r = Application.Current.Resources;
            Func<string, string> res = k => { var v = r[k]; var b = v as SolidColorBrush; return b != null ? ColorText(b.Color) : v is CornerRadius ? ((CornerRadius)v).TopLeft.ToString() : v == null ? "-" : v.ToString(); };
            Func<StackPanel> page = () => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Custom look").Value;
            int t = 1500;
            After(t, () =>
            {
                HuntLog("start: look " + lookId + ", custom values " + custom.Count + ", base " + customBase);
                OpenSettings();
                ShowSettingsPage(page());
            });
            After(t += 600, () => Snap("custom_page_top"));
            After(t += 200, () =>
            {
                lookMode = "Dark"; ApplyLook();
                SetCustomColors(new[] { "Accent" }, Color.FromRgb(0xE8, 0x11, 0x23), false, true);
                HuntLog("accent red: look " + lookId + ", Accent " + res("Accent") + ", text on it " + res("AccentText") + ", saved " + custom["Dark.Accent"]);
                SetCustomColors(new[] { "Accent" }, Color.FromRgb(0xFF, 0xD6, 0x00), false, true);
                HuntLog("accent yellow: text on it " + res("AccentText") + " (black reads better)");
                SetCustomColors(new[] { "WindowBg", "StageBg", "CoverBg", "StripBg" }, Color.FromRgb(0x10, 0x18, 0x30), false, true);
                SetCustomColors(new[] { "PageBg" }, Color.FromRgb(0x14, 0x1C, 0x36), false, true);
                SetCustomColors(new[] { "CardBg", "MenuBg", "TipBg", "FloatBg", "DialogBg" }, Color.FromRgb(0x1E, 0x28, 0x4A), false, true);
                HuntLog("background " + res("StageBg") + ", pages " + res("PageBg") + ", cards " + res("CardBg") + ", menus " + res("MenuBg"));
                SetCustomValue("roundness", "0");
                SetCustomValue("font", "Georgia");
                SetCustomValue("titleWeight", "Bold");
                SetCustomValue("motionStyle", "springy");
                SetCustomValue("motionSpeed", "1.4");
                SetCustomValue("icons", "lines");
            });
            After(t += 300, () =>
            {
                HuntLog("roundness 0: control bar corners " + res("Radius.Float") + ", cards " + res("Radius.Card") + "; font " + res("Font.Ui") + ", titles " + res("Weight.Title"));
                HuntLog("motion springy x1.4: style " + Motion.Style + ", normal " + Motion.Normal + " ms, press " + Motion.Press + "; icons " + (Looks.Current.Get("Look.Icons")));
                Snap("custom_dark_square_georgia");
                SetCustomValue("roundness", "20");
                SetCustomValue("bars", "floating");
            });
            After(t += 300, () =>
            {
                HuntLog("roundness 20: control bar " + res("Radius.Float") + ", cards " + res("Radius.Card") + ", dialog footer " + r["Radius.DialogFooter"] + "; bars float " + barsFloat);
                CloseSettings();
            });
            After(t += 500, () => Snap("custom_viewer_dark"));
            After(t += 200, () =>
            {
                OpenSettings(); ShowSettingsPage(page());
                lookMode = "Light"; ApplyLook();
                HuntLog("light: accent " + res("Accent") + " (the dark red/yellow is dark-only), page " + res("PageBg"));
                SetCustomColors(new[] { "PageBg" }, Color.FromRgb(0xFF, 0xF4, 0xE0), true, true);
                SetCustomColors(new[] { "TextMain", "ControlText", "ControlHoverText" }, Color.FromRgb(0xFF, 0xF0, 0xD0), true, true);
            });
            After(t += 700, () =>
            {
                HuntLog("light text almost the page colour: readability warning shown " + (readabilityCard.Visibility == Visibility.Visible) + ": '" + readabilityText.Text + "'");
                Snap("custom_light_warning");
                SetCustomColors(new[] { "TextMain", "ControlText", "ControlHoverText" }, Color.FromRgb(0x20, 0x18, 0x10), true, true);
            });
            After(t += 700, () =>
            {
                HuntLog("dark text again: warning shown " + (readabilityCard.Visibility == Visibility.Visible));
                lookMode = "Dark"; ApplyLook();
                HuntLog("back to dark: accent " + res("Accent") + ", page " + res("PageBg"));
                customName = "Night Sky";
                SaveSettings();
                var lines = File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("custom") || l.StartsWith("look=")).ToList();
                HuntLog("saved: " + lines.Count + " lines, e.g. " + string.Join(" | ", lines.Take(5)));
                int before = custom.Count;
                custom.Clear(); customName = "x";
                LoadSettings(false, true);
                HuntLog("loaded again: " + custom.Count + " values (had " + before + "), name '" + customName + "', base " + customBase);
                // export, start over, import, undo
                string file = Path.Combine(snapDir, "Night Sky.fvlook");
                File.WriteAllLines(file, LookFileLines());
                HuntLog("export file: " + File.ReadAllLines(file).Length + " lines, first '" + File.ReadAllLines(file)[0] + "'");
                StartCustomOver();
                HuntLog("start over: " + custom.Count + " values, accent " + res("Accent") + ", name '" + customName + "'");
                ImportLook(file);
                HuntLog("imported: " + custom.Count + " values, name '" + customName + "', accent " + res("Accent") + ", look " + lookId + ", message '" + F<TextBlock>("ToastText").Text + "'");
                toastAction();
                HuntLog("Undo: " + custom.Count + " values, name '" + customName + "'");
                ImportLook(file);
                string bad = Path.Combine(snapDir, "bad.fvlook");
                File.WriteAllText(bad, "hello\nDark.Accent=#FF0000");
                ImportLook(bad);
                HuntLog("not a look file: message '" + F<TextBlock>("ToastText").Text + "', still " + custom.Count + " values");
                string odd = Path.Combine(snapDir, "odd.fvlook");
                File.WriteAllLines(odd, new[] { "FastViewer look", "name=Odd", "base=Banana", "Dark.Accent=#00FF00", "Dark.Nonsense=#FFFFFF", "roundness=abc", "font=Verdana", "Light.TextMain=notacolour" });
                ImportLook(odd);
                HuntLog("odd file: '" + F<TextBlock>("ToastText").Text + "', base " + customBase + ", values " + string.Join(", ", custom.Keys));
                ImportLook(file);
                HuntLog("after last import: name '" + customName + "'");
                ShowSettingsPage(settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Appearance").Value);
                HuntLog("after showing Appearance: name '" + customName + "'");
            });
            After(t += 800, () =>
            {
                var card = lookCards.Children.OfType<Button>().FirstOrDefault(c => (string)c.Tag == CustomId);
                HuntLog("name now '" + customName + "', custom look name '" + Looks.Custom.Name + "'");
                HuntLog("Appearance: " + lookCards.Children.Count + " look cards, custom card '" + (card == null ? "missing" : (string)card.ToolTip) + "', chosen " + lookId);
                Snap("custom_appearance_cards");
                ShowSettingsPage(page());
                var scroll = F<ScrollViewer>("SettingsScroll");
                scroll.ScrollToVerticalOffset(700);
            });
            After(t += 500, () => Snap("custom_page_middle"));
            After(t += 100, () => { var scroll = F<ScrollViewer>("SettingsScroll"); scroll.ScrollToEnd(); });
            After(t += 500, () => Snap("custom_page_end"));
            After(t += 100, () =>
            {
                var showAll = FindAll<Button>(page()).First(b => b.Content as string == "Show all colours");
                showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HuntLog("all colours: " + FindAll<Button>(page()).Count(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "").EndsWith(" colour")) + " colour buttons on the page");
                var pick = FindAll<Button>(page()).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Accent colour");
                pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HuntLog("clicking a colour in a test: '" + testLog.LastOrDefault() + "'");
                F<ScrollViewer>("SettingsScroll").ScrollToVerticalOffset(1500);
            });
            After(t += 500, () => Snap("custom_all_colours"));
            After(t += 100, () =>
            {
                // the colour maths: every colour survives the trip through hue/shade and back
                int worst = 0;
                var rnd = new Random(3);
                for (int i = 0; i < 2000; i++)
                {
                    var c = Color.FromArgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                    double h, s, v;
                    ToHsv(c, out h, out s, out v);
                    var back = FromHsv(h, s, v, c.A / 255.0);
                    worst = Math.Max(worst, Math.Max(Math.Abs(c.R - back.R), Math.Max(Math.Abs(c.G - back.G), Math.Max(Math.Abs(c.B - back.B), Math.Abs(c.A - back.A)))));
                }
                HuntLog("2000 random colours to hue/shade and back: largest difference " + worst + " (of 255)");
                F<ScrollViewer>("SettingsScroll").ScrollToTop();
                huntPickerInWindow = true;
            });
            After(t += 400, () =>
            {
                var pick = FindAll<Button>(page()).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Accent colour");
                pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            After(t += 500, () =>
            {
                Snap("custom_picker");
                root.Children.Remove(huntPicker);
                huntPickerInWindow = false;
            });
            After(t += 100, () =>
            {
                lookId = "Photos"; ApplyLook();
                HuntLog("Photos again: accent " + res("Accent") + ", corners " + res("Radius.Float") + ", font " + res("Font.Ui") + ", bars float " + barsFloat);
                HuntDone();
            });
        }

        // Settings > Viewing > Show: old settings lines become the new choice; each choice lets the right files through.
        void HuntShowMigrate()
        {
            After(800, () =>
            {
                foreach (var lines in new[] { new string[0], new[] { "filter=0" }, new[] { "filter=1" }, new[] { "filter=2" }, new[] { "filter=0", "music=0" }, new[] { "filter=0", "music=1" }, new[] { "filter=1", "music=0" }, new[] { "show=4" } })
                {
                    filter = 0;
                    foreach (var l in lines) { var kv = l.Split('='); LoadOption(kv[0], kv[1]); }
                    HuntLog("[" + string.Join(", ", lines) + "] -> " + ShowChoices[filter]);
                }
                var names = new[] { "a.jpg", "b.mp4", "c.mp3", "d.flac", "e.txt" };
                for (filter = 0; filter < ShowChoices.Length; filter++)
                    HuntLog(ShowChoices[filter] + ": " + string.Join(" ", names.Where(PassesFilter)));
                filter = 3;
                HuntLog("saved line: " + OptionLines().First(l => l.StartsWith("show=")) + ", old lines written: " + OptionLines().Count(l => l.StartsWith("filter=") || l.StartsWith("music=")));
                HuntDone();
            });
        }
        // Rotate with "Also save it to the file": JPEGs with and without an orientation tag, a sideways phone JPEG (tag 6),
        // a PNG and a BMP. Each is turned once, saved, then "reopened" (decoded fresh, and opened again in the window)
        // and checked: size turned, the red corner where it should be, the date, Windows' thumbnail. Scratch folder only!
        void HuntRotSave()
        {
            string dir = Path.GetDirectoryName(files[0]);
            // Make the tagged JPEGs from plain.jpg (400 x 200, red square top-left).
            Action<string, ushort> tagged = (name, o) =>
            {
                BitmapSource src;
                using (var fs = File.OpenRead(Path.Combine(dir, "plain.jpg"))) src = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (o == 6) src = new TransformedBitmap(src, new RotateTransform(270)); // stored sideways, the tag turns it back up
                var meta = new BitmapMetadata("jpg");
                meta.SetQuery("/app1/ifd/{ushort=274}", o);
                var enc = new JpegBitmapEncoder { QualityLevel = 95 };
                enc.Frames.Add(BitmapFrame.Create(src, null, meta, null));
                using (var fs = File.Create(Path.Combine(dir, name))) enc.Save(fs);
            };
            Func<BitmapSource, string> corners = b =>
            {
                var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
                Func<int, int, string> at = (x, y) => { var px = new byte[4]; c.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0); return px[2] > 150 && px[0] < 100 ? "red" : px[0] > 150 ? "blue" : "rgb(" + px[2] + "," + px[1] + "," + px[0] + ")"; };
                int w = c.PixelWidth, h = c.PixelHeight, m = Math.Max(2, Math.Min(w, h) / 20);
                return w + "x" + h + " TL " + at(m, m) + " TR " + at(w - m, m) + " BR " + at(w - m, h - m) + " BL " + at(m, h - m);
            };
            Func<string, string> onDisk = f =>
            {
                try
                {
                    int fw, fh;
                    var b = DecodeImage(f, 4000, out fw, out fh);
                    object tag = null;
                    try { using (var fs = File.OpenRead(f)) { var m = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Metadata as BitmapMetadata; tag = m == null ? null : m.GetQuery("System.Photo.Orientation"); } } catch { }
                    return "as shown " + corners(b) + ", orientation tag " + (tag ?? "none") + ", modified " + File.GetLastWriteTime(f).ToString("HH:mm:ss.fff") + ", Windows thumbnail " + corners(ThumbsWide(f)) + ", Explorer's cache 96: " + (Thumbs.FromCache(f, 96) == null ? "none" : corners(Thumbs.FromCache(f, 96))) + " 256: " + (Thumbs.FromCache(f, 256) == null ? "none" : corners(Thumbs.FromCache(f, 256)));
                }
                catch (Exception ex) { return "error " + ex.Message; }
            };
            // Like a phone photo: 4000 x 3000, stored sideways (tag 6) with the small preview phones put inside the JPEG.
            Action phone = () =>
            {
                BitmapSource src;
                using (var fs = File.OpenRead(Path.Combine(dir, "plain.jpg"))) src = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var big = new TransformedBitmap(src, new ScaleTransform(10, 15)); // 4000 x 3000
                var stored = new TransformedBitmap(big, new RotateTransform(270));
                var preview = new TransformedBitmap(stored, new ScaleTransform(0.04, 0.04)); // 120 x 160, stored sideways too
                var meta = new BitmapMetadata("jpg");
                meta.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
                var enc = new JpegBitmapEncoder { QualityLevel = 90 };
                enc.Frames.Add(BitmapFrame.Create(stored, BitmapFrame.Create(preview), meta, null));
                using (var fs = File.Create(Path.Combine(dir, "phone.jpg"))) enc.Save(fs);
            };
            var names = new[] { "phone.jpg", "plain.jpg", "tag1.jpg", "tag6.jpg", "photo.png", "photo.bmp" };
            After(800, () =>
            {
                tagged("tag1.jpg", 1); tagged("tag6.jpg", 6); phone();
                File.SetLastWriteTime(Path.Combine(dir, "tag1.jpg"), DateTime.Now.AddHours(-1));
                saveRotation = true;
                OpenPath(files[0]);
            });
            int t = 2500;
            foreach (var n in names)
            {
                string name = n;
                After(t, () =>
                {
                    ShowIndex(files.FindIndex(f => Path.GetFileName(f) == name), 1);
                    HuntLog(name + " before: " + onDisk(Path.Combine(dir, name)));
                });
                After(t + 700, () => { Thumbs.TestSkipRebuild = name == "plain.jpg" || name == "photo.png"; toastKind = null; Rotate(); });
                After(t + 3000, () =>
                {
                    HuntLog("  message: " + F<TextBlock>("ToastText").Text + ", still waiting to save " + rotationsToSave.Count + ", turn kept in memory " + rotations.ContainsKey(shown.File));
                    if (Thumbs.lastRebuild != null) Thumbs.lastRebuild.Join(5000);
                    HuntLog("  after" + (Thumbs.TestSkipRebuild ? " (cache NOT rebuilt, like before the fix)" : "") + ": " + onDisk(Path.Combine(dir, name)));
                });
                t += 3500;
            }
            After(t, () =>
            {
                // reopen everything as a new window would: forget turns in memory, open the folder again
                rotations.Clear();
                OpenPath(Path.Combine(dir, names[0]));
            });
            int t2 = t + 1000;
            foreach (var n in names)
            {
                string name = n;
                After(t2, () => ShowIndex(files.FindIndex(f => Path.GetFileName(f) == name), 1));
                After(t2 + 600, () => HuntLog("reopened " + name + ": window shows " + corners(ShownPixels()) + " (turned on screen " + shown.Rotation.Angle + ")"));
                t2 += 900;
            }
            After(t2 + 300, () => { Snap("rotsave_last"); HuntDone(); });
        }

        // Windows' thumbnail as it is (not cropped square).
        static BitmapSource ThumbsWide(string f) { return Thumbs.MakeWide(f, 256); }

        BitmapSource ShownPixels()
        {
            var b = shown.Picture.Source as BitmapSource;
            if (b == null) return null;
            double a = shown.Rotation.Angle;
            return a == 0 ? b : new TransformedBitmap(b, new RotateTransform(a));
        }
        // Taskbar thumbnail buttons: their state on a photo, a video and music, and what a click does (as Windows would click).
        void HuntTaskbar()
        {
            Func<string> state = () => "prev " + (thumbPrev.IsEnabled ? "on" : "off") + ", play/pause " + (thumbPlay.Visibility == Visibility.Visible ? "'" + thumbPlay.Description + "'" : "hidden") +
                ", next " + (thumbNext.IsEnabled ? "on" : "off") + " | shown " + Path.GetFileName(shown.File) + " playing " + playing;
            Action<System.Windows.Shell.ThumbButtonInfo> click = b => typeof(System.Windows.Shell.ThumbButtonInfo).GetMethod("InvokeClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(b, null);
            Action<string> go = name => ShowIndex(files.FindIndex(f => Path.GetFileName(f) == name), 1);
            After(1500, () => { go("photo.jpg"); });
            After(2000, () => { HuntLog("photo: " + state()); Snap("taskbar_photo"); go("clip.mp4"); });
            After(3000, () => { HuntLog("video: " + state()); click(thumbPlay); });
            After(3300, () => { HuntLog("after clicking pause: " + state()); click(thumbPlay); });
            After(3600, () => { HuntLog("after clicking play: " + state()); TogglePlay(); });
            After(3900, () => { HuntLog("after Space-like pause in the window: " + state()); go("song.mp3"); });
            After(4900, () => { HuntLog("music: " + state()); click(thumbPlay); });
            After(5200, () => { HuntLog("music after clicking pause: " + state()); string before = Path.GetFileName(shown.File); click(thumbNext); HuntLog("next from " + before); });
            After(5800, () => { HuntLog("  now: " + state()); click(thumbPrev); });
            After(6400, () => { HuntLog("after previous: " + state()); OpenSettings(); });
            After(6800, () => { HuntLog("Settings open: " + state()); HuntDone(); });
        }
        // 1.20: Change cover (MP3). Folder: song.mp3 (ID3 2.3 with a cover), v24.mp3 (ID3 2.4, no cover), bare.mp3 (no tag),
        // locked.mp3; pictures in a "pics" subfolder: red.png, blue.jpg. Use a scratch copy: the files are changed!
        // --case vturn (scratch folder of MP4s with a red square top-left; use --rotations <scratch file>): plain.mp4 is
        // turned without saving (remembered only), a90/a180/a270.mp4 are turned and saved into the file. Run twice:
        // the second run's "at start" lines show what was remembered or written.
        void HuntVideoTurn()
        {
            string dir = Path.GetDirectoryName(files[0]);
            Func<BitmapSource, string> red = b =>
            {
                if (b == null) return "none";
                var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
                int w = c.PixelWidth, h = c.PixelHeight; var px = new byte[w * h * 4];
                c.CopyPixels(px, w * 4, 0);
                double sx = 0, sy = 0, n = 0;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; if (px[i + 2] > 180 && px[i + 1] < 80 && px[i] < 80) { sx += x; sy += y; n++; } }
                return n == 0 ? w + "x" + h + " no red" : w + "x" + h + " red " + (sy / n < h / 2.0 ? "top" : "bottom") + "-" + (sx / n < w / 2.0 ? "left" : "right");
            };
            Action<string> go = name => ShowIndex(files.FindIndex(f => Path.GetFileName(f) == name), 1);
            var names = new[] { "plain.mp4", "a90.mp4", "a180.mp4", "a270.mp4" };
            int t = 800;
            foreach (var name in names)
            {
                string n = name;
                After(t, () => go(n));
                After(t + 1200, () => { int a; rotations.TryGetValue(shown.File, out a); HuntLog("at start " + n + ": shown turned " + a + ", in file " + VideoInfo.Rotation(shown.File) + ", remembered " + TurnMemory.Get(shown.File) + ", Explorer 256 " + red(Thumbs.FromCache(shown.File, 256))); });
                t += 1800;
            }
            if (File.Exists(Path.Combine(dir, "done.txt"))) { After(t, HuntDone); return; }
            for (int k = 0; k < names.Length; k++)
            {
                int kk = k; string n = names[k], f = Path.Combine(dir, n);
                int turns = kk == 0 ? 1 : kk;
                DateTime date = DateTime.MinValue; double pos = 0;
                After(t, () => { go(n); date = File.GetLastWriteTime(f); });
                After(t + 1200, () =>
                {
                    saveRotation = kk != 0;
                    if (kk == 2) { playing = true; shown.Video.IsMuted = true; shown.Video.Play(); }
                    for (int i = 0; i < turns; i++) Rotate();
                    pos = shown.Video.Position.TotalSeconds;
                    SaveWaitingRotations();
                });
                After(t + 7000, () =>
                {
                    int a; rotations.TryGetValue(f, out a);
                    HuntLog(n + " turned " + (turns * 90) + (saveRotation ? " and saved: " + lastVideoTurnResult : " (not saved)") + "; in file " + VideoInfo.Rotation(f) + ", shown " + a + ", remembered " + TurnMemory.Get(f) +
                        ", date +" + (File.GetLastWriteTime(f) - date).TotalMilliseconds + " ms, leftovers " + Directory.GetFiles(dir, "*.fvturn.*").Length +
                        ", on screen " + Path.GetFileName(shown.File) + " ready " + shown.Ready + " at " + shown.Video.Position.TotalSeconds.ToString("0.0") + " s (was " + pos.ToString("0.0") + "), playing " + playing +
                        ", Explorer 256 " + red(Thumbs.FromCache(f, 256)) + ", Windows thumbnail " + red(Thumbs.Make(f, 200)));
                    lastVideoTurnResult = null;
                });
                t += 7600;
            }
            After(t, () => { File.WriteAllText(Path.Combine(dir, "done.txt"), "1"); HuntDone(); });
        }

        // --case gridsearch (folder with clips of different lengths, photos of different sizes; use --settings):
        // the grid's search box and every sort, plus the keys while typing.
        void HuntGridSearch()
        {
            Func<string> list = () => string.Join(", ", gridFiles.Select(Path.GetFileName)) + "  [" + F<System.Windows.Documents.Run>("GridCount").Text.Trim() + "]";
            Action<Key> press = k =>
            {
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                OnKey(this, e);
                HuntLog("  key " + k + " handled " + e.Handled + ", box focused " + gridSearch.IsKeyboardFocusWithin + ", grid open " + gridOpen);
            };
            After(800, () => { OpenGrid(); HuntLog("open, sort " + GridSortNames[gridSort] + ": " + list()); });
            After(1300, () => { gridSearch.Focus(); gridSearch.Text = "clip"; });
            After(1700, () => { HuntLog("search 'clip': " + list()); Snap("grid_search"); });
            After(1800, () => { gridSearch.Text = "CLIP 1"; ApplyGridSearchNow(); HuntLog("search 'CLIP 1' (two words, any case): " + list()); });
            After(1900, () => { gridSearch.Text = "nothing like this"; ApplyGridSearchNow(); HuntLog("no match: " + list()); });
            After(2000, () => { gridSearch.Text = "photo"; ApplyGridSearchNow(); press(Key.Down); press(Key.Left); HuntLog("  selected " + gridSelected); });
            After(2100, () => { press(Key.Escape); HuntLog("after Esc: '" + gridSearch.Text + "' " + list()); press(Key.Escape); press(Key.Escape); });
            int t = 2600;
            for (int i = 0; i < GridSortNames.Length; i++)
            {
                int s = i;
                After(t, () => { if (!gridOpen) OpenGrid(); SetGridSort(s); });
                After(t + 1500, () => HuntLog("sort " + GridSortNames[s] + ": " + list()));
                t += 1700;
            }
            After(t, () => { gridSearch.Text = "clip"; ApplyGridSearchNow(); HuntLog("search + sort: " + list()); press(Key.Enter); HuntLog("  opened " + Path.GetFileName(CurrentFile)); });
            After(t + 300, () =>
            {
                var hook = huntMenuHook;
                huntMenuHook = (m, b) => HuntLog("sort menu: " + string.Join(" | ", m.Items.OfType<MenuItem>().Select(x => x.Header + (x.Icon != null ? " (ticked)" : ""))));
                OpenGrid(); ShowGridSortMenu(); huntMenuHook = hook;
                HuntLog("reopened grid: search '" + gridSearch.Text + "', " + list());
                SaveSettings(true);
                HuntLog("settings line: " + File.ReadAllLines(SettingsFile).FirstOrDefault(l => l.StartsWith("gridSort=")));
                HuntDone();
            });
        }

        // --case menus (a video; use --settings): both "..." menus as usual, rearranged, reset; the settings line and its reading.
        void HuntMenus()
        {
            Func<ContextMenu, string> text = m => string.Join(" | ", m.Items.Cast<object>().Select(o => o is Separator ? "--" : o is MenuItem ? ((((MenuItem)o).Style == (Style)Application.Current.Resources["MenuHeading"]) ? "[" + ((MenuItem)o).Header + "]" : (((MenuItem)o).Header as string ?? "[" + System.Windows.Automation.AutomationProperties.GetName((MenuItem)o) + " row]")) : "?"));
            Action<string> both = step =>
            {
                var hook = huntMenuHook;
                huntMenuHook = (m, b) => HuntLog(step + " " + b.Name + ": " + text(m));
                ShowMoreMenu(); ShowVideoMenu();
                huntMenuHook = hook;
            };
            Func<string> lines = () => string.Join(" / ", File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("topMenu=") || l.StartsWith("videoMenu=")));
            After(1200, () =>
            {
                both("usual");
                SaveSettings(true);
                HuntLog("  settings lines: '" + lines() + "'");
                topMenuHidden.Add("copyPath"); topMenuHidden.Add("delete");
                MoveMenuItem(false, topMenuOrder.IndexOf("info"), -topMenuOrder.IndexOf("info")); // to the top, in steps
                videoMenuHidden.Add("speeds");
                MoveMenuItem(true, videoMenuOrder.IndexOf("trim"), -1);
                both("changed");
                SaveSettings(true);
                HuntLog("  settings lines: '" + lines() + "'");
                SetLoopStart();
                videoMenuHidden.Add("loopB");
                both("loop set, B hidden");
                ClearLoop();
                LoadMenuLine(false, "-grid,bogus,info,info,saveAs");
                HuntLog("read 'topMenu=-grid,bogus,info,info,saveAs': " + string.Join(",", topMenuOrder.Select(id => topMenuHidden.Contains(id) ? "-" + id : id)));
                foreach (var id in topMenuOrder.ToList()) topMenuHidden.Add(id);
                foreach (var id in videoMenuOrder.ToList()) videoMenuHidden.Add(id);
                both("all off");
                ResetMenuLayout();
                both("reset");
                OpenSettings();
            });
            After(2500, () =>
            {
                ShowSettingsPage(settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Buttons").Value);
                menuRows.BringIntoView(); After(300, () => Snap("menus_settings"));
                HuntLog("Settings > Buttons: " + (menuRows == null ? "no menu rows" : menuRows.Children.Count + " rows, first: " + string.Join(", ", FindAll<TextBlock>(menuRows).Take(4).Select(t => t.Text))));
            });
            After(3000, () =>
            {
                var down = FindAll<Button>(menuRows).Where(b => (string)b.ToolTip == "Move Save as down").FirstOrDefault();
                if (down != null) down.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var sw = FindAll<CheckBox>(menuRows).FirstOrDefault();
                if (sw != null) { sw.IsChecked = false; sw.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                HuntLog("clicked 'Move Save as down' and the first switch: " + string.Join(",", topMenuOrder.Select(id => topMenuHidden.Contains(id) ? "-" + id : id)) + ", focused " + (Keyboard.FocusedElement is Button ? (string)((Button)Keyboard.FocusedElement).ToolTip : "" + Keyboard.FocusedElement));
                HuntLog("  settings lines: '" + lines() + "'");
                var strip = new StackPanel();
                foreach (var lookName in new[] { "Photos", "Glass" })
                {
                    lookId = lookName; ApplyLook();
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)Application.Current.Resources["MenuBg"] ?? Brushes.Gray };
                    foreach (var c in VideoChoices.Concat(TopChoices.Where(x => x.Id == "copy")))
                    {
                        var g = new Glyph { Code = c.Glyph, Size = 20, Margin = new Thickness(8) };
                        g.ToolTip = c.Id;
                        row.Children.Add(g);
                    }
                    var bmp = new RenderTargetBitmap(560, 40, 96, 96, PixelFormats.Pbgra32);
                    row.Measure(new Size(560, 40)); row.Arrange(new Rect(0, 0, 560, 40)); row.UpdateLayout();
                    bmp.Render(row);
                    var e2 = new PngBitmapEncoder(); e2.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = File.Create(Path.Combine(snapDir, "icons_" + lookName + ".png"))) e2.Save(fs);
                }
                HuntLog("icons in order: " + string.Join(", ", VideoChoices.Select(c => c.Id)) + ", copy");
                HuntDone();
            });
        }

        // --case switches (use --settings): every look (and a Custom look on each) x dark/light x accent palettes: a switch
        // On and Off drawn on a settings card. Measures the track, the knob and the card, and flags a knob that can't be
        // seen on its track or an On track that looks like an Off one. Saves switches.png (one row per case).
        void HuntSwitches()
        {
            var palettes = new[]
            {
                Tuple.Create("grey (yours)", Hex("9B9A9900848381006D6B6A004C4A4800363533002625240019191900107C1000")),
                Tuple.Create("blue", Hex("99EBFF004CC2FF000091F8000078D4000067C0003E92FF001A68DE0000B7C300")),
                Tuple.Create("orange", Hex("FFD5A400FFB16500F7820000DA6A0000B85400008F4000006A2F00E8112300")),
                Tuple.Create("Windows now", (byte[])null),
            };
            var looks = Looks.All.Select(l => Tuple.Create(l.Id, l)).ToList();
            string savedBase = customBase, savedLook = lookId;
            var rows = new List<BitmapSource>();
            int problems = 0, cases = 0;
            Func<BitmapSource, int, int, Color> at = (b, x, y) =>
            {
                var px = new byte[4];
                b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                return Color.FromRgb(px[2], px[1], px[0]);
            };
            foreach (var pal in palettes)
            {
                Ui.TestPalette = pal.Item2;
                var list = looks.Concat(looks.Select(l => Tuple.Create("Custom on " + l.Item1, (Look)null))).Concat(new[] { "#000000", "#2A2A2A", "#5A5A5A", "#FFFFFF", "#F2F2F2" }.SelectMany(c => looks.Select(l => Tuple.Create("Custom on " + l.Item1 + " accent " + c, (Look)null)))).ToList();
                foreach (var entry in list)
                {
                    foreach (var light in new[] { false, true })
                    {
                        Look look = entry.Item2;
                        if (look == null)
                        {
                            var bits = entry.Item1.Substring(10).Split(' ');
                            customBase = bits[0];
                            custom.Clear();
                            if (bits.Length > 2) { custom["Dark.Accent"] = bits[2]; custom["Light.Accent"] = bits[2]; }
                            BuildCustomLook(); look = Looks.Custom;
                        }
                        if (light && !look.HasLight) continue;
                        Looks.Apply(look, light ? "Light" : "Dark");
                        var on = new CheckBox { Style = (Style)Application.Current.Resources["ToggleSwitch"], IsChecked = true };
                        var off = new CheckBox { Style = (Style)Application.Current.Resources["ToggleSwitch"], IsChecked = false };
                        var panel = new StackPanel { Orientation = Orientation.Horizontal };
                        panel.Children.Add(on); panel.Children.Add(off);
                        off.Margin = new Thickness(20, 0, 0, 0);
                        var card = Card(panel, new Thickness(10));
                        card.Width = 240; card.Height = 44;
                        card.Measure(new Size(240, 44)); card.Arrange(new Rect(0, 0, 240, 44)); card.UpdateLayout();
                        var rtb = new RenderTargetBitmap(240, 44, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(card);
                        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
                        // On switch: track from x=10+44=54 to 94, knob on the right (centre 94-4-6=84), track sample at 62.
                        var onPos = on.TranslatePoint(new Point(0, 0), card);
                        int ox = (int)onPos.X + 44, oy = (int)onPos.Y + 10;
                        Color trackOn = at(conv, ox + 8, oy), knobOn = at(conv, ox + 30, oy), cardBg = at(conv, 4, 4);
                        var offPos = off.TranslatePoint(new Point(0, 0), card);
                        int fx = (int)offPos.X + 44;
                        Color trackOff = at(conv, fx + 30, oy), knobOff = at(conv, fx + 10, oy);
                        double knob = Contrast(knobOn, trackOn), track = Contrast(trackOn, cardBg), offKnob = Contrast(knobOff, trackOff);
                        double edge = Math.Max(Contrast(at(conv, ox + 20, oy - 10), cardBg), Contrast(at(conv, ox + 20, oy - 9), cardBg));
                        // See-through looks: their cards are glass (the sample hits the bright rim), judged in the live part instead;
                        // and the iOS-style white Off knob on a pale track in Liquid Glass light is by design.
                        bool glass = look.Flag("Look.SeeThrough");
                        bool bad = knob < 2.5 || (!glass && track < 1.4 && edge < 2) || (offKnob < 2 && !(glass && light));
                        cases++;
                        if (bad) problems++;
                        HuntLog((bad ? "PROBLEM " : "ok      ") + pal.Item1 + " / " + entry.Item1 + " / " + (light ? "light" : "dark") +
                            ": on track #" + Hx(trackOn) + " knob #" + Hx(knobOn) + " (" + knob.ToString("0.0") + ":1), card #" + Hx(cardBg) + " (" + track.ToString("0.0") + ":1), outline " + edge.ToString("0.0") + ":1; off knob " + offKnob.ToString("0.0") + ":1" +
                            ", Accent #" + Hx(((SolidColorBrush)Application.Current.Resources["Accent"]).Color));
                        if (rows.Count < 200) rows.Add(conv);
                    }
                }
            }
            Ui.TestPalette = null;
            custom.Clear(); customBase = savedBase; lookId = savedLook; BuildCustomLook(); ApplyLook();
            HuntLog(cases + " cases, " + problems + " problems (drawn on their own)");
            // Live: the real Settings page in the window, a real switch flipped off and on (with its slide and squish).
            OpenSettings();
            ShowSettingsPage(settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Buttons").Value);
            int t = 600;
            foreach (var pal in palettes) foreach (var l in Looks.All) foreach (var light in new[] { false, true })
            {
                if (light && !l.HasLight) continue;
                var p = pal; var look = l; bool lt = light;
                CheckBox sw = null;
                After(t, () =>
                {
                    Ui.TestPalette = p.Item2; lookId = look.Id; lookMode = lt ? "Light" : "Dark"; ApplyLook();
                    sw = FindAll<CheckBox>(settingsPage == null ? (DependencyObject)root : root).FirstOrDefault(c => c.IsVisible && c.Style == (Style)Application.Current.Resources["ToggleSwitch"] && c.IsChecked == true);
                    if (sw == null) return;
                    sw.IsChecked = false; sw.IsChecked = true; // slides back to On
                    var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                    sw.RaiseEvent(press); // the press squish (Liquid Glass)
                    var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
                    sw.RaiseEvent(up);
                });
                After(t + 900, () =>
                {
                    if (sw == null) { HuntLog("live " + p.Item1 + " / " + look.Id + ": no switch found"); return; }
                    int w = (int)root.ActualWidth, h = (int)root.ActualHeight;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
                    var o = sw.TranslatePoint(new Point(44, 10), root);
                    int ox = (int)o.X, oy = (int)o.Y;
                    Color trackOn = at(conv, ox + 8, oy), knobOn = at(conv, ox + 30, oy), around = at(conv, ox - 50, oy);
                    double knob = Contrast(knobOn, trackOn), track = Contrast(trackOn, around);
                    bool bad = knob < 2.5 || track < 1.4;
                    var mv = sw.Template.FindName("KnobMove", sw) as TranslateTransform;
                    var kn = sw.Template.FindName("Knob", sw) as FrameworkElement;
                    HuntLog((bad ? "PROBLEM live " : "ok      live ") + p.Item1 + " / " + look.Id + " / " + (lt ? "light" : "dark") + ": track #" + Hx(trackOn) + " knob #" + Hx(knobOn) + " (" + knob.ToString("0.0") + ":1), card #" + Hx(around) + " (" + track.ToString("0.0") + ":1), knob moved " + (mv == null ? "?" : mv.X.ToString("0.0")) + ", knob size " + (kn == null ? "?" : kn.ActualWidth + "x" + kn.ActualHeight) + ", switch scale " + sw.RenderTransform.Value.M11.ToString("0.00"));
                    if (look.Id == "Photos" && !lt) { var enc2 = new PngBitmapEncoder(); enc2.Frames.Add(BitmapFrame.Create(rtb)); using (var fs = File.Create(Path.Combine(snapDir, "live_" + p.Item1.Split(' ')[0] + ".png"))) enc2.Save(fs); }
                });
                t += 1100;
            }
            After(t, () =>
            {
                Ui.TestPalette = null; lookId = savedLook; ApplyLook();
                var sheet = new DrawingVisual();
                using (var dc = sheet.RenderOpen()) for (int i = 0; i < rows.Count; i++) dc.DrawImage(rows[i], new Rect((i / 40) * 250, (i % 40) * 46, 240, 44));
                var all = new RenderTargetBitmap(((rows.Count + 39) / 40) * 250, 40 * 46, 96, 96, PixelFormats.Pbgra32);
                all.Render(sheet);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(all));
                using (var fs = File.Create(Path.Combine(snapDir, "switches.png"))) enc.Save(fs);
                HuntDone();
            });
        }

        static byte[] Hex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16); return b; }
        static string Hx(Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        void HuntCover()
        {
            string dir = Path.GetDirectoryName(files[0]);
            Func<BitmapSource, string> centre = b =>
            {
                if (b == null) return "none";
                var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
                var px = new byte[4];
                c.CopyPixels(new Int32Rect(c.PixelWidth / 2, c.PixelHeight / 2, 1, 1), px, 4, 0);
                return "rgb(" + px[2] + "," + px[1] + "," + px[0] + ")";
            };
            Func<string, string> inFile = f =>
            {
                try
                {
                    int version; long start; int covers = 0; string mime = null; BitmapSource pic = null;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        ReadId3(fs, out version, out start, null, (id, body) =>
                        {
                            if (id != "APIC") return;
                            covers++;
                            int z = Array.IndexOf(body, (byte)0, 1);
                            mime = System.Text.Encoding.ASCII.GetString(body, 1, z - 1);
                            int d = Array.IndexOf(body, (byte)0, z + 2); // after the picture type: description, then 0
                            var ms = new MemoryStream(body, d + 1, body.Length - d - 1);
                            pic = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        });
                    string title, artist;
                    ReadMusicTags(f, out title, out artist);
                    return "ID3 2." + version + ", covers " + covers + " " + mime + " " + (pic == null ? "" : pic.PixelWidth + "x" + pic.PixelHeight + " centre " + centre(pic)) +
                        ", sound starts at " + start + ", tags '" + title + "' / '" + artist + "', Windows thumbnail centre " + centre(Thumbs.Make(f, 200));
                }
                catch (Exception ex) { return "error " + ex.Message; }
            };
            Action<string> go = name => ShowIndex(files.FindIndex(f => Path.GetFileName(f) == name), 1);
            var coverDates = new Dictionary<string, DateTime>();
            int t = 1500;
            foreach (var pair in new[] { new[] { "song.mp3", "red.png" }, new[] { "v24.mp3", "blue.jpg" }, new[] { "bare.mp3", "red.png" } })
            {
                string name = pair[0], pic = pair[1];
                double before = 0;
                After(t, () => { go(name); coverDates[name] = File.GetLastWriteTime(Path.Combine(dir, name)); HuntLog(name + " before: " + inFile(Path.Combine(dir, name)) + ", cache 256 centre " + centre(Thumbs.FromCache(Path.Combine(dir, name), 256))); });
                After(t + 1500, () => { before = shown.Video.Position.TotalSeconds; HuntLog("  playing " + playing + " at " + before.ToString("0.00") + " s, changing cover to " + pic); ChangeCoverTo(shown.File, Path.Combine(dir, "pics", pic)); });
                After(t + 6500, () =>
                {
                    HuntLog("  result: " + lastCoverResult);
                    HuntLog("  date " + File.GetLastWriteTime(Path.Combine(dir, name)).ToString("HH:mm:ss.fff") + " (was " + coverDates[name].ToString("HH:mm:ss.fff") + "), Explorer's cache 256 centre " + centre(Thumbs.FromCache(Path.Combine(dir, name), 256)));
                    HuntLog("  on screen: " + Path.GetFileName(shown.File) + " playing " + playing + " at " + shown.Video.Position.TotalSeconds.ToString("0.00") + " s (was " + before.ToString("0.00") + "), card cover centre " + centre(shown.AudioCover));
                    HuntLog("  after: " + inFile(Path.Combine(dir, name)) + ", leftover temp files " + Directory.GetFiles(dir, "*.fvcover.tmp").Length);
                });
                t += 7000;
            }
            byte[] lockedBefore = null;
            FileStream lockStream = null;
            After(t, () =>
            {
                go("locked.mp3");
                string f = Path.Combine(dir, "locked.mp3");
                lockedBefore = File.ReadAllBytes(f);
                lockStream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read); // another app has it open
            });
            After(t + 1500, () => ChangeCoverTo(shown.File, Path.Combine(dir, "pics", "red.png")));
            After(t + 7000, () =>
            {
                lockStream.Dispose();
                string f = Path.Combine(dir, "locked.mp3");
                HuntLog("locked.mp3 (open in another app): " + lastCoverResult + "; file unchanged " + File.ReadAllBytes(f).SequenceEqual(lockedBefore) +
                    ", leftover temp files " + Directory.GetFiles(dir, "*.fvcover.tmp").Length + ", still plays " + playing);
                var hit = stage.InputHitTest(new Point(stage.ActualWidth / 2, stage.ActualHeight / 2)) as DependencyObject;
                bool reachesStage = false;
                for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d)) if (d == stage) reachesStage = true;
                var right = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent };
                stage.RaiseEvent(right);
                HuntLog("right-click on the cover: hits " + (hit == null ? "nothing" : hit.GetType().Name) + ", reaches the music view " + reachesStage + ", opens Change cover " + right.Handled);
                var saved = huntMenuHook;
                huntMenuHook = (m, b) => HuntLog("music '...' menu: " + string.Join(" | ", m.Items.OfType<MenuItem>().Select(i => i.Header is string ? (string)i.Header : FindAll<TextBlock>(i).Select(x => x.Text).FirstOrDefault())));
                ShowVideoMenu();
                huntMenuHook = saved;
                filter = 1; ReloadFolder();
                HuntLog("Show: Photos and videos: " + string.Join(", ", files.Select(Path.GetFileName)) + " (only the song on screen stays)");
                filter = 0; ReloadFolder();
                HuntLog("on again: " + files.Count + " files");
                HuntDone();
            });
        }
        // 1.20: music files, speeds 0.25x-5x and Copy frame. Folder: a tagged .mp3 with a cover, an untagged .wav,
        // a video and a photo (use --settings <scratch.ini>).
        void HuntMusic()
        {
            Func<string> state = () => shown == null ? "nothing" : Path.GetFileName(shown.File) + " audio=" + shown.IsAudio + " ready=" + shown.Ready +
                " playing=" + playing + " pos=" + shown.Video.Position.TotalSeconds.ToString("0.00") + " card=" + (shown.Picture.Source == null ? "none" : shown.Picture.Source.GetType().Name) +
                " title='" + shown.AudioTitle + "' artist='" + shown.AudioArtist + "' cover=" + (shown.AudioCover != null) + " shownAfter=" + switchWatch.ElapsedMilliseconds + "ms";
            Action<string> go = name => { int i = files.FindIndex(f => Path.GetFileName(f) == name); ShowIndex(i, 1); };
            After(1500, () => { go("song.mp3"); HuntLog("right after switching: " + state()); });
            After(2000, () => HuntLog("0.5 s later: " + state()));
            After(3500, () => { HuntLog("2 s later: " + state()); Snap("music_card"); ShowVideoMenuForTest(); });
            After(4000, () => { SetSpeed(5); HuntLog("speed 5: " + state()); });
            After(5000, () => { HuntLog("1 s at 5x: " + state() + " ratio " + shown.Video.SpeedRatio); SetSpeed(0.25); });
            After(6000, () => { HuntLog("1 s at 0.25x: " + state()); SetSpeed(1); go("plain.wav"); HuntLog("wav: " + state()); });
            After(7000, () => { HuntLog("wav 1 s: " + state()); Snap("music_nocover"); go("clip.mp4"); });
            After(8000, () => { HuntLog("video: " + state()); CopyFrame(); SetSpeed(4); });
            After(9000, () => { HuntLog("video 1 s at 4x: " + state()); SetSpeed(1);
                var seen = new List<string>(); speed = 1;
                for (int i = 0; i < 14; i++) { StepSpeed(1); seen.Add(SpeedText(speed)); }
                StepSpeed(0); seen.Add("normal " + SpeedText(speed));
                for (int i = 0; i < 6; i++) { StepSpeed(-1); seen.Add(SpeedText(speed)); }
                HuntLog("all speeds, stepping: " + string.Join(" ", seen));
                OpenSettings();
                ShowSettingsPage(settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Videos").Value);
            });
            After(9800, () =>
            {
                var chips = FindAll<Button>(F<FrameworkElement>("SettingsPage")).Where(b => b.Content is string && ((string)b.Content).EndsWith("x")).ToList();
                HuntLog("speed buttons: " + string.Join(" ", chips.Select(b => b.Content + (b.Style == Application.Current.Resources["AccentButton"] ? "*" : ""))));
                var five = chips.First(b => (string)b.Content == "5x");
                five.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                chips.First(b => (string)b.Content == "1x").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                HuntLog("after clicking 5x and 1x: " + string.Join(" ", chips.Select(b => b.Content + (b.Style == Application.Current.Resources["AccentButton"] ? "*" : ""))) +
                    ", saved " + string.Join("", File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("speeds="))));
                Snap("speed_settings");
                HuntDone();
            });
        }

        void ShowVideoMenuForTest()
        {
            // what the video "..." menu would list for the file on screen (the menu itself isn't opened in tests)
            bool music = shown != null && shown.IsAudio;
            HuntLog("video menu for " + (music ? "music" : "video") + ": speeds " + string.Join(" ", Speeds.Select(SpeedText)) + (music ? ", no Copy frame / Save a frame / frame steps / Trim / compatible copy" : ""));
        }

        // Settings > Buttons > Tips when pointing at a button (use with --settings <scratch.ini>).
        void HuntTips()
        {
            Func<string, bool> opens = name =>
            {
                // what Windows does just before a tooltip appears; "handled" = it won't appear
                var args = (ToolTipEventArgs)typeof(ToolTipEventArgs).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null).Invoke(new object[] { true });
                args.RoutedEvent = FrameworkElement.ToolTipOpeningEvent;
                F<FrameworkElement>(name).RaiseEvent(args);
                return !args.Handled;
            };
            After(1500, () =>
            {
                HuntLog("tips on (default " + showTips + "): Next arrow tip opens " + opens("NextBtn") + ", Delete " + opens("DeleteBtn") + ", name for screen readers '" + System.Windows.Automation.AutomationProperties.GetName(F<FrameworkElement>("NextBtn")) + "'");
                OpenSettings();
                ShowSettingsPage(settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Buttons").Value);
            });
            After(2200, () =>
            {
                var card = FindAll<TextBlock>(F<FrameworkElement>("SettingsPage")).FirstOrDefault(tb => tb.Text == "Tips when pointing at a button");
                var toggle = card == null ? null : FindAll<CheckBox>(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(card))).FirstOrDefault();
                HuntLog("Settings > Buttons card " + (card != null) + ", switch on " + (toggle != null && toggle.IsChecked == true));
                toggle.IsChecked = false;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                HuntLog("switched off: Next arrow tip opens " + opens("NextBtn") + ", Delete " + opens("DeleteBtn") + ", screen reader name still '" + System.Windows.Automation.AutomationProperties.GetName(F<FrameworkElement>("NextBtn")) + "', saved line " + string.Join("", File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("tips="))));
                Snap("tips_switch");
                showTips = true;
                LoadSettings(false, true);
                HuntLog("read back from the file: tips " + showTips);
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                HuntLog("switched on again: Next arrow tip opens " + opens("NextBtn"));
                HuntDone();
            });
        }

        // Scrolling through videos as fast as a mouse wheel can (--count n --every ms): the controls show (the mouse
        // is moving), videos play. Measures how long each press takes to show its file and how smooth the window
        // stays (time between drawn frames).
        void HuntWheel()
        {
            int count = huntCount > 0 ? huntCount : 200, every = huntEvery > 0 ? huntEvery : 35;
            var activeSeen = new Dictionary<string, int>();
            grabTimes = new List<long>();
            var waits = new List<double>();
            var frames = new List<double>();
            var press = new Stopwatch();
            Slot pressedFor = null;
            TimeSpan last = TimeSpan.Zero;
            EventHandler onFrame = (o, e) =>
            {
                var now = ((RenderingEventArgs)e).RenderingTime;
                if (last != TimeSpan.Zero && now != last) frames.Add((now - last).TotalMilliseconds);
                if (now != last) last = now;
                if (pressedFor != null && shown == pressedFor) { waits.Add(press.Elapsed.TotalMilliseconds); pressedFor = null; }
            };
            int n = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(every) };
            timer.Tick += delegate
            {
                ShowOverlay(); // as if the mouse moved
                foreach (var a in Motion.Active()) { int c; activeSeen.TryGetValue(a, out c); activeSeen[a] = c + 1; }
                if (pressedFor != null) { waits.Add(press.Elapsed.TotalMilliseconds); } // still not shown: counts as waiting until the next press
                Go(1);
                pressedFor = wanted;
                press.Restart();
                if (++n < count) return;
                timer.Stop();
                After(800, () =>
                {
                    CompositionTarget.Rendering -= onFrame;
                    waits.Sort(); frames.Sort();
                    Func<List<double>, double, double> q = (l, f) => l.Count == 0 ? 0 : l[Math.Min(l.Count - 1, (int)(l.Count * f))];
                    HuntLog(string.Format("wheel: {0} presses every {1} ms, motion {2}, controls showing, frame grabbing stopped (slow PC protection): {3}", count, every, Motion.Level, slowGrabs));
                    HuntLog(string.Format("  press to file on screen: median {0:0} ms, p90 {1:0} ms, worst {2:0} ms", q(waits, 0.5), q(waits, 0.9), q(waits, 1)));
                    var g = grabTimes.OrderBy(x => x).ToList();
                    HuntLog(string.Format("  frame grabs: {0}, median {1} ms, p90 {2} ms, over 120 ms: {3} ({4})", g.Count, g.Count > 0 ? g[g.Count / 2] : 0, g.Count > 0 ? g[g.Count * 9 / 10] : 0,
                        g.Count(x => x > 120), string.Join(", ", grabTimes.Where(x => x > 120))));
                    HuntLog("  moving at press time (times seen): " + (activeSeen.Count == 0 ? "nothing" : string.Join(", ", activeSeen.OrderByDescending(a => a.Value).Select(a => a.Key + " " + a.Value))));
                    HuntLog(string.Format("  window frames: {0}, median {1:0.0} ms, p95 {2:0.0} ms, over 50 ms: {3}, worst {4:0} ms", frames.Count, q(frames, 0.5), q(frames, 0.95), frames.Count(f => f > 50), q(frames, 1)));
                    HuntDone();
                });
            };
            After(2500, () =>
            {
                if (!playing) TogglePlay();
                CompositionTarget.Rendering += onFrame;
                timer.Start();
            });
        }

        // Pictures of the custom look for the release notes: an example look ("Sunset") made with the editor.
        void HuntShowcase()
        {
            Func<StackPanel> page = () => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Custom look").Value;
            Action<string, int, int, int> col = (parts, r, g, b) => SetCustomColors(parts.Split(','), Color.FromRgb((byte)r, (byte)g, (byte)b), false, true);
            int t = 1500;
            After(t, () =>
            {
                lookMode = "Dark";
                customName = "Sunset";
                customBase = "Glass";
                col("Accent", 0xFF, 0x8A, 0x3D);
                col("WindowBg,StageBg,CoverBg,StripBg", 0x1A, 0x12, 0x1E);
                col("TopBarBg,BottomBarBg,PanelBg", 0x24, 0x18, 0x2A);
                col("PageBg", 0x1E, 0x15, 0x24);
                col("CardBg,MenuBg,TipBg,FloatBg,DialogBg", 0x2E, 0x20, 0x36);
                col("TextMain,ControlText,ControlHoverText", 0xFF, 0xF1, 0xE6);
                col("TextSecondary,TextDim", 0xCF, 0xB8, 0xC8);
                SetCustomValue("seeThrough", "off");
                SetCustomValue("roundness", "14");
                SetCustomValue("motionStyle", "springy");
                SetCustomValue("icons", "lines");
            });
            After(t += 800, () => { int v = files.FindIndex(IsVideoFile); ShowIndex(v, 1); });
            After(t += 1500, () => { if (playing) TogglePlay(); ShowOverlay(); });
            After(t += 600, () => Snap("1_viewer_sunset_paused"));
            After(t += 100, () => { TogglePlay(); ShowOverlay(); });
            After(t += 600, () => { Snap("2_viewer_sunset_playing"); TogglePlay(); });
            After(t += 200, () => { OpenSettings(); ShowSettingsPage(page()); });
            After(t += 700, () => Snap("3_custom_look_page"));
            After(t += 100, () => { F<ScrollViewer>("SettingsScroll").ScrollToVerticalOffset(560); });
            After(t += 500, () => Snap("4_custom_look_shapes_motion"));
            After(t += 100, () =>
            {
                F<ScrollViewer>("SettingsScroll").ScrollToTop();
                huntPickerInWindow = true;
                FindAll<Button>(page()).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Accent colour").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            After(t += 600, () => { Snap("5_colour_picker"); root.Children.Remove(huntPicker); huntPickerInWindow = false; });
            After(t += 100, () => ShowSettingsPage(settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Appearance").Value));
            After(t += 700, () => Snap("6_appearance_cards"));
            After(t += 100, () =>
            {
                ShowSettingsPage(page());
                col("TextMain,ControlText,ControlHoverText", 0x3A, 0x2A, 0x40); // too dark on purple: the warning shows
            });
            After(t += 800, () => Snap("7_readability_warning"));
            After(t += 100, () =>
            {
                col("TextMain,ControlText,ControlHoverText", 0xFF, 0xF1, 0xE6);
                string file = Path.Combine(snapDir, "Sunset.fvlook");
                File.WriteAllLines(file, LookFileLines());
                CloseSettings();
                StartCustomOver();
                ImportLook(file); // as a friend would: the message offers Undo
            });
            After(t += 900, () => Snap("8_imported_from_file"));
            After(t += 100, HuntDone);
        }

        static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T) yield return (T)c;
                foreach (var x in FindAll<T>(c)) yield return x;
            }
        }

        // ---------------------------------------------------------------- scrub: dragging the time bar fast, and the dot while playing

        void HuntScrub()
        {
            int moves = 0, backwards = 0;
            double lastValue = -1, biggestGap = 0;
            var gaps = new List<double>();
            var gapWatch = new Stopwatch();
            seek.ValueChanged += delegate
            {
                if (!settingSlider) return; // only the dot moving by itself
                moves++;
                if (lastValue >= 0 && seek.Value < lastValue - 0.001) backwards++;
                lastValue = seek.Value;
                if (gapWatch.IsRunning) { biggestGap = Math.Max(biggestGap, gapWatch.Elapsed.TotalMilliseconds); gaps.Add(gapWatch.Elapsed.TotalMilliseconds); }
                gapWatch.Restart();
            };
            After(1500, () => { if (!playing) TogglePlay(); ShowOverlay(); });
            After(2500, () =>
            {
                ShowOverlay();
                moves = 0; backwards = 0; lastValue = -1; biggestGap = 0; gapWatch.Reset(); gaps.Clear();
            });
            After(3500, () =>
            {
                gaps.Sort();
                if (gaps.Count > 0) HuntLog(string.Format("gaps between dot moves: median {0:0.0} ms, p90 {1:0.0} ms, {2} gaps over 20 ms", gaps[gaps.Count / 2], gaps[gaps.Count * 9 / 10], gaps.Count(g => g > 20)));
                HuntLog(string.Format("dot while playing for 1 s: {0} moves ({1} Hz screen, motion {2}), backwards {3}, longest wait between moves {4:0.0} ms, dot {5:0.00} s vs video {6:0.00} s",
                    moves, Motion.ScreenRate, Motion.Level, backwards, biggestGap, seek.Value, shown.Video.Position.TotalSeconds));
                // a very fast drag: 80 moves in about a second, as the mouse would give them
                scrubSeeks = 0;
                double before = shown.Video.Position.TotalSeconds;
                StartScrub();
                HuntLog(string.Format("drag starts at {0:0.00} s: playing {1} (button state), video held still {2}", before, playing, true));
                int k = 0;
                var drag = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
                drag.Tick += delegate
                {
                    k++;
                    seek.Value = Math.Min(seek.Maximum - 1, 2 + k * 0.17); // raises ValueChanged like the thumb does
                    if (k < 80) return;
                    drag.Stop();
                    double atEnd = shown.Video.Position.TotalSeconds;
                    After(300, () =>
                    {
                        double still = shown.Video.Position.TotalSeconds;
                        HuntLog(string.Format("after 80 drag moves: {0} jumps given to the video, video at {1:0.00} s (dot {2:0.00}), 0.3 s later still {3:0.00} s (held still: {4})",
                            scrubSeeks, atEnd, seek.Value, still, Math.Abs(still - atEnd) < 0.05));
                        EndScrub();
                        double letGo = seek.Value;
                        After(1000, () =>
                        {
                            HuntLog(string.Format("let go at {0:0.00} s: 1 s later the video is at {1:0.00} s, playing {2}, scrubbing {3}", letGo, shown.Video.Position.TotalSeconds, playing, scrubbing));
                            // paused, then dragged: stays paused
                            TogglePlay();
                            StartScrub(); seek.Value = 5; EndScrub();
                            After(800, () =>
                            {
                                HuntLog(string.Format("paused, dragged to 5 s: video at {0:0.00} s after 0.8 s, playing {1}", shown.Video.Position.TotalSeconds, playing));
                                HuntDone();
                            });
                        });
                    });
                };
                drag.Start();
            });
        }

        // ---------------------------------------------------------------- toggles: doing the same thing again undoes it

        void HuntToggles()
        {
            int opened = 0;
            string last = "";
            huntMenuHook = (m, b) => { opened++; last = b.Name; };
            Action<string, Action> menu = (what, open) => { int before = opened; open(); HuntLog(what + ": " + (opened > before ? "menu OPENS (" + last + ")" : "no menu")); };
            After(1500, () =>
            {
                menu("click ...", ShowMoreMenu);
                MenuClosedBy(F<Button>("MoreBtn"));
                menu("click ... while its menu is open (that press closed it)", ShowMoreMenu);
                menu("click ... again", ShowMoreMenu);
                MenuClosedBy(F<Button>("MoreBtn"));
                RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent }); // let go elsewhere
            });
            After(1800, () =>
            {
                menu("after letting go somewhere else, click ...", ShowMoreMenu);
                MenuClosedBy(F<Button>("ZoomBtn"));
                menu("zoom menu closed by its button, then click ...", ShowMoreMenu);
                menu("click the zoom % button", ShowZoomMenu);
                MenuClosedBy(F<Button>("ZoomBtn"));
                RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
            });
            After(2100, () =>
            {
                int v = files.FindIndex(IsVideoFile);
                ShowIndex(v, 1);
            });
            After(3600, () =>
            {
                menu("video ... button", ShowVideoMenu);
                MenuClosedBy(F<Button>("VideoMoreBtn"));
                menu("video ... button while its menu is open", ShowVideoMenu);
                RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
                // keys that do the same thing again (extra test keys F9/F8 because Ctrl can't be pressed in a test)
                shortcuts.First(s => s.Id == "trim").Keys.Add("F9");
                shortcuts.First(s => s.Id == "settings").Keys.Add("F8");
                RebuildShortcutMap();
                if (playing) TogglePlay();
                PressKey(Key.F9);
                HuntLog("trim key: trimming " + trimming);
                PressKey(Key.F9);
                HuntLog("trim key again: trimming " + trimming + ", control bar back " + transport.IsVisible);
                PressKey(Key.F8);
                HuntLog("Settings key: Settings open " + settingsOpen);
                PressKey(Key.F8);
                HuntLog("Settings key again: Settings open " + settingsOpen);
                StartTrim();
                ToggleMini();
                HuntLog("mini player while trimming: mini " + mini + ", trimming " + trimming + ", trim bar " + F<FrameworkElement>("TrimBar").Visibility);
                ToggleMini();
                HuntLog("back from the mini player: mini " + mini);
            });
            After(4200, HuntDone);
        }

        // ---------------------------------------------------------------- refresh rates: the same animation at 60-500 Hz

        // Plays each kind of animation with the frame timing of a 60, 144, 240 and 500 Hz screen (frames are
        // simulated, so no such screen is needed) and compares: how long it takes, where it is at the same
        // moments, the biggest single step, and where it ends.
        // --case squish (run with --look Glass --motion full): a Liquid Glass button squished by taps of different
        // lengths, frame by frame at 144 Hz; then how each Custom look motion style moves.
        void HuntSquish()
        {
            After(1500, () =>
            {
                HuntLog("look " + lookId + ", squish " + Motion.Squish + ", motion " + Motion.Level);
                var b = F<Button>("OpenBtn");
                b.RenderTransformOrigin = new Point(0.5, 0.5);
                var s = Motion.PartsOf(b).Scale;
                const double frame = 1 / 144.0;
                foreach (int hold in new[] { 0, 1, 4, 40 })
                {
                    Motion.Set(s, ScaleTransform.ScaleXProperty, 1);
                    Motion.Set(s, ScaleTransform.ScaleYProperty, 1);
                    Motion.SquishIn(s);
                    for (int f = 0; f < hold; f++) Motion.SimulateFrame(frame);
                    double atRelease = s.ScaleX;
                    Motion.SquishOut(s);
                    double low = s.ScaleX, high = s.ScaleX, lowY = s.ScaleY;
                    int frames = 0, wobbles = 0;
                    bool above = false;
                    for (; frames < 1000 && Motion.Busy(s); frames++)
                    {
                        Motion.SimulateFrame(frame);
                        low = Math.Min(low, s.ScaleX); high = Math.Max(high, s.ScaleX); lowY = Math.Min(lowY, s.ScaleY);
                        if (!above && s.ScaleX > 1.002) { above = true; wobbles++; }
                        if (above && s.ScaleX < 0.998) above = false;
                    }
                    HuntLog(string.Format(CultureInfo.InvariantCulture,
                        "tap held {0,2} frames ({1,3:0} ms): scale at release {2:0.000}, deepest {3:0.000} x {4:0.000}, overshoot {5:0.000}, times past full size {6}, back at rest after {7:0} ms at {8:0.000}{9}",
                        hold, hold * frame * 1000, atRelease, low, lowY, high, wobbles, frames * frame * 1000, s.ScaleX,
                        low > 0.9 || high < 1.01 || frames >= 1000 || s.ScaleX != 1 ? " FAIL" : ""));
                }
                var look = Looks.Find(lookId);
                var styles = new[] { "windows", "subtle", "springy", "calm", "snappy" };
                string was = Motion.Style;
                foreach (var st in styles)
                {
                    var d = new ResourceDictionary();
                    ApplyCustomValue(d, look, "motionStyle", st);
                    Motion.Style = (string)d["Motion.Ease"];
                    var ease = Motion.In;
                    double peak = 0;
                    for (int i = 0; i <= 200; i++) peak = Math.Max(peak, ease.Ease(i / 200.0));
                    HuntLog(string.Format(CultureInfo.InvariantCulture,
                        "style {0,-8}: curve {1,-10} move {2,4:0} ms, fade in {3,3:0} ms, distance x{4:0.0}, press {5:0.00}, progress at 10% {6:0.00} / 25% {7:0.00} / 50% {8:0.00}, overshoot {9:0}%",
                        st, Motion.Style, Motion.Ms((double)d["Motion.Normal"]), (double)d["Motion.FadeIn"], (double)d["Motion.Distance"], (double)d["Motion.Press"],
                        ease.Ease(0.1), ease.Ease(0.25), ease.Ease(0.5), (peak - 1) * 100));
                }
                Motion.Style = was;
                HuntDone();
            });
        }

        // --case preloadghost: hidden preloaded files must never show. The window is drawn as it is and again with
        // every hidden player taken out; any pixel that differs is a hidden file showing through (Liquid Glass used
        // to keep them at 1%, which showed as faint shapes beside the picture). Checked on a see-through PNG, a
        // photo with empty space beside it and a video, in the normal window and the mini player.
        void HuntPreloadGhost()
        {
            int t = 3000;
            foreach (var step in new[] { "first file", "next", "next", "next", "next", "next", "mini", "next", "next", "next" })
            {
                string name = step;
                After(t, () =>
                {
                    if (name == "mini") ToggleMini();
                    else if (name == "next") Go(1);
                });
                After(t + 1000, () => { if (playing) TogglePlay(); }); // a playing video would differ between the two pictures
                After(t + 1300, () => HuntLog(GhostCheck(name)));
                t += 1500;
            }
            After(t + 300, HuntDone);
        }

        string GhostCheck(string step)
        {
            int w = Math.Max(1, (int)ActualWidth), h = Math.Max(1, (int)ActualHeight);
            Func<byte[]> draw = () =>
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);
                var px = new byte[w * h * 4];
                rtb.CopyPixels(px, w * 4, 0);
                return px;
            };
            var asIs = draw();
            var hidden = slots.Where(s => s != shown && s.Host.Visibility == Visibility.Visible).ToList();
            foreach (var s in hidden) s.Host.Visibility = Visibility.Collapsed;
            UpdateLayout();
            var clean = draw();
            foreach (var s in hidden) s.Host.Visibility = Visibility.Visible;
            UpdateLayout();
            int differ = 0, most = 0;
            for (int i = 0; i < asIs.Length; i++)
            {
                int d = Math.Abs(asIs[i] - clean[i]);
                if (d > 0) { differ++; most = Math.Max(most, d); }
            }
            return string.Format("{0,-10} {1,-12} look {2}, {3}, {4} hidden players: {5} pixel values differ (most {6}){7}",
                step, Path.GetFileName(shown.File), lookId, mini ? "mini player" : "window", hidden.Count, differ, most, differ > 0 ? " FAIL" : "");
        }

        // --case skipicons: saves the skip buttons at 4x (skip_<look>_<dark|light>.png) to look at them closely.
        void HuntSkipIcons()
        {
            After(1500, () => { if (playing) TogglePlay(); ShowOverlay(); });
            After(2500, () =>
            {
                var back = F<FrameworkElement>("BackBtn");
                var bar = (FrameworkElement)VisualTreeHelper.GetParent(back);
                var a = back.TranslatePoint(new Point(0, 0), bar);
                var fwd = F<FrameworkElement>("FwdBtn");
                double w = fwd.TranslatePoint(new Point(fwd.ActualWidth, 0), bar).X - a.X, h = back.ActualHeight;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle((Brush)FindResource("FloatBg"), null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(bar) { Viewbox = new Rect(a.X, a.Y, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * 4), (int)(h * 4), 384, 384, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var f = File.Create(Path.Combine(snapDir, "skip_" + lookId + "_" + (Looks.Light ? "light" : "dark") + ".png"))) enc.Save(f);
                HuntLog("skip buttons " + lookId + ": back '" + F<TextBlock>("BackText").Text + "', forward '" + F<TextBlock>("FwdText").Text + "', " + w.ToString("0") + "x" + h.ToString("0"));
                HuntDone();
            });
        }

        // --case squishall (--look Glass --motion full): presses every button in the window, in Settings and on every
        // Settings page, frame by frame at 144 Hz. Each must really shrink on screen (its whole transform, including
        // any of its own) and end exactly where it started.
        void HuntSquishAll()
        {
            After(1500, () => { if (playing) TogglePlay(); ShowOverlay(); HuntLog("look " + lookId + ", squish " + Motion.Squish + ", motion " + Motion.Level); SquishEvery("window"); OpenSettings(); });
            int t = 3000;
            for (int i = 0; i < 14; i++)
            {
                int page = i;
                After(t, () =>
                {
                    var menu = F<StackPanel>("SettingsNav").Children.OfType<ButtonBase>().ToList();
                    if (page == 0) HuntLog("Settings side menu: " + menu.Count + " entries");
                    if (page < menu.Count) { menu[page].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
                });
                After(t + 500, () => SquishEvery("settings page " + (page + 1)));
                t += 800;
            }
            After(t + 300, () => { HuntLog("checked " + squishChecked.Count + " buttons, " + squishFailed + " FAIL"); HuntDone(); });
        }

        readonly HashSet<ButtonBase> squishChecked = new HashSet<ButtonBase>();
        int squishFailed;

        void SquishEvery(string where)
        {
            const double frame = 1 / 144.0;
            int n = 0, bad = 0;
            foreach (var b in FindAll<ButtonBase>(this))
            {
                if (!b.IsVisible || squishChecked.Contains(b) || !PressShrinks(b)) continue;
                squishChecked.Add(b);
                n++;
                var before = b.RenderTransform.Value;
                PressButton(b);
                for (int f = 0; f < 30; f++) Motion.SimulateFrame(frame);
                var pressed = b.RenderTransform.Value;
                ReleaseButton(b);
                for (int f = 0; f < 300 && Motion.Busy(Motion.PartsOf(b).Scale); f++) Motion.SimulateFrame(frame);
                var after = b.RenderTransform.Value;
                double k = Math.Abs(before.M11) > 0 ? Math.Abs(pressed.M11 / before.M11) : 1;
                bool back = Math.Abs(after.M11 - before.M11) < 1e-6 && Math.Abs(after.M22 - before.M22) < 1e-6 && Math.Abs(after.OffsetX - before.OffsetX) < 1e-6 && Math.Abs(after.OffsetY - before.OffsetY) < 1e-6;
                if (k > 0.9 || !back)
                {
                    bad++;
                    squishFailed++;
                    HuntLog(string.Format("  {0} {1} '{2}': pressed scale {3:0.00}, back to start {4} FAIL", where, b.GetType().Name, b.Name,
                        k, back) + (b.Name == "" ? " in " + ((FrameworkElement)VisualTreeHelper.GetParent(b)).Name : ""));
                }
            }
            HuntLog(where + ": " + n + " new buttons pressed, " + bad + " FAIL");
        }


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
            var looks = lookOverride != null ? new[] { lookOverride } : new[] { "Photos", "Clean", "Glass", "Midnight" }; // (--look: just that one)
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
                HuntLog("after scrolling on it: level next to the speaker " + F<FrameworkElement>("MiniVolLevel").Visibility + " showing '" + F<TextBlock>("MiniVolText").Text + "', top message shown " + (F<FrameworkElement>("Toast").Visibility == Visibility.Visible && toastKind == "feedback") + ", volume slider would open " + VolumeSliderOpen);

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
                if (dropped > 5) HuntLog("      frame by frame (ms): " + string.Join(" ", frames.Select(x => x.ToString("0"))));
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
