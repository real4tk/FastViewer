// FastViewer 1.30.0 tests: --case frames (pictures drawn by FastViewer itself, FramePath.cs, against Windows' player).
// Run it twice on the same folder, once with --frames on and once with --frames off, and compare the logs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        static string Ms(double seconds) { return (seconds * 1000).ToString("0", CultureInfo.InvariantCulture); }

        // --case clicks (1.30.0 round 2): what goes to the speakers, checked for clicks, clipping and running dry.
        // Tone clips (t*.mp4: every track a 440 Hz sine, so the mix is one clean sine) show every click exactly: the
        // next sample of a sine follows from the two before it (x[n] = 2cos(w)x[n-1] - x[n-2]); anything else is a jump.
        void HuntClicks()
        {
            muted = false;
            loop = true;
            UpdateControls();
            HuntLog("pictures drawn by FastViewer: " + (OwnVideo.Enabled ? "own player" : "Windows' player"));
            var names = files.Where(f => IsVideoFile(f) || IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            var runs = new List<Action<Action>>();
            var summary = new List<string>();
            foreach (var name in names)
                foreach (double vol in new[] { 1.0, 2.0 })
                    foreach (bool keep in new[] { true, false })
                        foreach (double spd in new[] { 1.0, 2.0 })
                        {
                            string nm = name; double v = vol, sp = spd; bool k = keep;
                            runs.Add(next => HuntOpen(nm, () =>
                            {
                                bool tone = nm.StartsWith("t", StringComparison.OrdinalIgnoreCase);
                                Loudness.On = !tone; // (tones: 100% is exactly the file; real clips as in use)
                                volume = v;
                                ApplyAudio();
                                TimeStretch.KeepPitch = k;
                                if (!playing) TogglePlay();
                                SetSpeed(sp);
                                After(1200, () =>
                                {
                                    var p = soundPlayer;
                                    string title = nm + " " + (v * 100) + "% " + sp + "x " + (k ? "same pitch" : "pitch with speed");
                                    if (p == null || !OwnSound(shown)) { HuntLog(title + ": Windows' player plays the sound (not measured; muted " + muted + ", volume " + volume + ", level " + LevelOf(shown).ToString("0.00") + ", speed " + speed + ", player " + (p != null) + ", own " + OwnSound(shown) + ", audio " + shown.IsAudio + ", video " + shown.IsVideo + ", failed " + shown.Failed + ", base " + OwnSoundBase(shown) + ", file " + (shown.File != null) + ")"); summary.Add(title + ": Windows' player"); next(); return; }
                                    p.ResetStats();
                                    var tap = new float[48000 * 8];
                                    p.TapAt[0] = 0;
                                    p.Tap = tap;
                                    After(6000, () =>
                                    {
                                        p.Tap = null;
                                        int n = p.TapAt[0];
                                        int starves = p.Starves, longest = p.LongestStarve, resyncs = p.Resyncs, minQ = p.MinQueuedMs, stepGap = p.LongestStepGap;
                                        string notes = string.Join(" ", p.Notes.ToArray());
                                        var x = tap.Take(n).ToArray();
                                        ThreadPool.QueueUserWorkItem(delegate
                                        {
                                            int knee = 0, full = 0, clicks = 0, worst = 0, zeros = 0;
                                            var at = new List<double>();
                                            foreach (var s in x) { float a = Math.Abs(s); if (a >= 26000) knee++; if (a >= 32700) full++; }
                                            if (tone)
                                            {
                                                double w = 2 * Math.PI * 440 * (k ? 1 : sp) / 48000, c2 = 2 * Math.Cos(w);
                                                int last = -100000, run = 0;
                                                for (int i = 2; i < x.Length; i++)
                                                {
                                                    if (x[i] == 0) { if (++run == 96) zeros++; } else run = 0; // (2 ms of silence)
                                                    double r = Math.Abs(x[i] - c2 * x[i - 1] + x[i - 2]);
                                                    if (r > worst) worst = (int)r;
                                                    if (r > 400 && i > 960) { if (i - last > 480) { clicks++; if (at.Count < 6) at.Add(i / 48000.0); } last = i; }
                                                }
                                            }
                                            string line = title + ": " + (n / 48000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s heard; " +
                                                (tone ? "clicks " + clicks + " (worst jump " + worst + ")" + (at.Count > 0 ? " at " + string.Join(", ", at.Select(t => t.ToString("0.00", CultureInfo.InvariantCulture))) + " s" : "") + ", silent bits " + zeros + "; " : "") +
                                                "samples over -2 dBFS " + knee + ", at full scale " + full + "; speakers ran dry " + starves + "x (longest " + longest + " ms), least queued " + minQ + " ms, sound thread pauses up to " + stepGap + " ms; restarts " + resyncs + (notes.Length > 0 ? " [" + notes + "]" : "");
                                            Dispatcher.BeginInvoke(new Action(delegate { HuntLog(line); summary.Add(line); next(); }));
                                        });
                                    });
                                });
                            }));
                        }
            Action<int> go = null;
            go = i =>
            {
                if (i >= runs.Count) { HuntLog(""); HuntLog("SUMMARY (" + (OwnVideo.Enabled ? "own player" : "Windows' player") + ")"); foreach (var l in summary) HuntLog(l); HuntLog(HuntResources()); HuntDone(); return; }
                runs[i](() => After(300, () => go(i + 1)));
            };
            go(0);
        }

        // --case clickspam (1.30.0 round 2): double-click for full screen off, left clicks every 70-160 ms on a playing
        // video for 4 s; every 100 ms: which layers are up (poster, drawn picture, scrim, controls) and how grey the
        // middle of the window is; pictures of the window when the poster is up.
        void HuntClickSpam()
        {
            muted = false;
            loop = true;
            mouseActions["double"] = "none";
            bool keepDouble = false; // (restored at the end: the scratch settings file is shared by later tests)
            var setCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            int clickNo = 0;
            long lastClick = 0;
            Action<bool> press = down =>
            {
                long now = Environment.TickCount;
                if (down) clickNo = now - lastClick < 500 ? clickNo + 1 : 1;
                if (down) lastClick = now;
                var a = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { clickNo });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            var rnd = new Random(7);
            int fi = 0, snaps = 0;
            Action one = null;
            one = () =>
            {
                if (fi >= names.Count) { mouseActions["double"] = keepDouble ? "none" : "fullscreen"; SaveSettings(); HuntDone(); return; }
                string nm = names[fi++];
                HuntOpen(nm, () =>
                {
                    if (!playing) TogglePlay();
                    After(2500, () =>
                    {
                        var s = shown;
                        int t = 0, posterUp = 0, posterGrey = 0;
                        while (t < 4000) { int d = 70 + rnd.Next(90); int at = t; After(at, () => press(true)); After(at + 25, () => press(false)); t += d; }
                        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                        int ticks = 0;
                        watch.Tick += delegate
                        {
                            ticks++;
                            bool poster = s.Poster.Visibility == Visibility.Visible && s.Poster.Source != null;
                            string grey = "";
                            if (poster)
                            {
                                posterUp++;
                                var src = s.Poster.Source as BitmapSource;
                                if (src != null)
                                {
                                    // (How the poster itself looks: its average brightness and how much its pixels differ.)
                                    try
                                    {
                                        var f = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                                        int w = f.PixelWidth, h = f.PixelHeight; var px = new byte[w * h * 4]; f.CopyPixels(px, w * 4, 0);
                                        double sum = 0, sum2 = 0, alpha = 0; int n = 0;
                                        for (int i = 0; i < px.Length; i += 4 * 37) { double v = (px[i] + px[i + 1] + px[i + 2]) / 3.0; sum += v; sum2 += v * v; alpha += px[i + 3]; n++; }
                                        double mean = sum / n, sd = Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
                                        grey = " poster mean " + mean.ToString("0") + " spread " + sd.ToString("0") + " alpha " + (alpha / n).ToString("0");
                                        if (sd < 12) posterGrey++;
                                    }
                                    catch (Exception ex) { grey = " poster ? " + ex.Message; }
                                }
                                if (snaps < 6) Snap("spam_" + nm.Replace(".", "_") + "_" + (snaps++));
                            }
                            HuntLog("  " + (ticks * 100) + " ms: playing " + playing + ", poster " + poster + grey + ", own player " + (shown != null && shown.IsVideo && shown.Video.IsOwn) + ", player " + s.Video.Visibility + ", controls " + overlayVisible + ", scrim " + scrim.Visibility + " " + scrim.Opacity.ToString("0.00"));
                        };
                        watch.Start();
                        After(4600, () =>
                        {
                            watch.Stop();
                            HuntLog(nm + ": poster up " + posterUp + " of " + ticks + " checks, flat grey " + posterGrey + (posterGrey > 0 ? " FAIL" : ""));
                            After(800, one);
                        });
                    });
                });
            };
            one();
        }

        // --case skipjam (1.30.0 round 3): Repeat on, skip to the last second, then "skip forward" every 60-160 ms for
        // 3 s (as clicking the button fast), let go; then: does it play on (its place moves, sound comes), does another
        // file still open, does a new Media Foundation reader still open (a jam of Windows' media pipeline stops those for
        // the whole program). Several rounds per file.
        void HuntSkipJam()
        {
            muted = false;
            loop = true;
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            var rnd = new Random(11);
            var plan = new List<string>();
            foreach (var nm in names) for (int k = 0; k < 4; k++) plan.Add(nm);
            int i = 0, jams = 0;
            Action one = null;
            one = () =>
            {
                if (i >= plan.Count) { HuntLog("SUMMARY: " + jams + " of " + plan.Count + " rounds jammed" + (jams > 0 ? " FAIL" : "") + "; players rebuilt by themselves " + playerRebuilds + ", restarts asked " + jamRestarts); HuntDone(); return; }
                string nm = plan[i++];
                int round = i;
                HuntOpen(nm, () =>
                {
                    if (!playing) TogglePlay();
                    After(1500, () =>
                    {
                        var s = shown;
                        double len = s.Video.NaturalDuration.HasTimeSpan ? s.Video.NaturalDuration.TimeSpan.TotalSeconds : 10;
                        SeekBy(len - 1.0 - VideoAt(s).TotalSeconds);
                        int t = 200;
                        while (t < 3200) { After(t, () => SeekBy(10)); t += 60 + rnd.Next(100); }
                        After(3300, () =>
                        {
                            double a0 = VideoAt(s).TotalSeconds;
                            var p = soundPlayer;
                            After(2500, () =>
                            {
                                double a1 = VideoAt(s).TotalSeconds;
                                bool moves = Math.Abs(a1 - a0) > 0.3 || (a1 < a0 - 0.5); // (moved on, or looped)
                                bool sound = p != null && p.Sounding;
                                string state = "place " + a0.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + a1.ToString("0.00", CultureInfo.InvariantCulture) + ", Windows' player at " + s.Video.Position.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) +
                                    ", playing " + playing + ", own " + (s.Video.IsOwn) + ", sound " + sound;
                                // A new reader of a file, on another thread (a jammed pipeline never answers).
                                string other = files.Select(Path.GetFileName).FirstOrDefault(f => !f.Equals(nm, StringComparison.OrdinalIgnoreCase) && IsVideoFile(f)) ?? nm;
                                var done = new ManualResetEvent(false);
                                var sw = Stopwatch.StartNew();
                                long openMs = -1;
                                var th = new Thread(() =>
                                {
                                    try { VideoConverter.Reading.Begin(); var rr = VideoConverter.Reading.Open(Path.Combine(Path.GetDirectoryName(s.File), other)); openMs = sw.ElapsedMilliseconds; if (rr != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(rr); } catch { }
                                    done.Set();
                                }) { IsBackground = true };
                                th.SetApartmentState(ApartmentState.MTA);
                                th.Start();
                                bool readerOk = done.WaitOne(4000);
                                bool jam = !moves || !readerOk;
                                if (jam) jams++;
                                HuntLog("round " + round + " " + nm + ": " + state + "; a new reader " + (readerOk ? "opened in " + openMs + " ms" : "DIDN'T OPEN in 4 s") + (jam ? " JAM" : " ok"));
                                After(300, one);
                            });
                        });
                    });
                });
            };
            one();
        }

        // --case jamrepair (1.30.0 round 3): Windows' player is stopped behind FastViewer's back while it should play;
        // the jam watch must make it again (or, with --jam-probe-fail, ask for a restart) and it must play on.
        void HuntJamRepair()
        {
            muted = false;
            loop = true;
            string nm = files.Select(Path.GetFileName).First(f => IsVideoFile(f) && !IsAudioFile(f));
            HuntOpen(nm, () =>
            {
                if (!playing) TogglePlay();
                After(2000, () =>
                {
                    var s = shown;
                    double before = VideoAt(s).TotalSeconds;
                    s.Video.Pause(); // (stuck: it says nothing, it just doesn't move)
                    HuntLog("player stopped behind FastViewer's back at " + before.ToString("0.00", CultureInfo.InvariantCulture) + " s");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    int rebuilds0 = playerRebuilds, restarts0 = jamRestarts;
                    double atFix = -1;
                    var stubborn = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    stubborn.Tick += delegate { if (playerRebuilds == rebuilds0 && jamRestarts == restarts0 && watch.Elapsed.TotalSeconds < 12) { try { s.Video.Pause(); } catch { } } else stubborn.Stop(); };
                    stubborn.Start();
                    t.Tick += delegate
                    {
                        if (atFix < 0 && (playerRebuilds > rebuilds0 || jamRestarts > restarts0)) { atFix = watch.Elapsed.TotalSeconds; HuntLog("noticed after " + atFix.ToString("0.0", CultureInfo.InvariantCulture) + " s: rebuilt " + (playerRebuilds - rebuilds0) + ", restart asked " + (jamRestarts - restarts0)); }
                        if (watch.Elapsed.TotalSeconds < 14) return;
                        t.Stop();
                        double a0 = VideoAt(shown).TotalSeconds;
                        After(1500, () =>
                        {
                            double a1 = VideoAt(shown).TotalSeconds;
                            bool moves = Math.Abs(a1 - a0) > 0.5;
                            HuntLog("afterwards: place " + a0.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + a1.ToString("0.00", CultureInfo.InvariantCulture) + (moves ? " (plays on)" : " (still stuck)") + ", playing " + playing +
                                (atFix < 0 ? " FAIL: never noticed" : jamProbeFails ? (jamRestarts > restarts0 ? " (restart asked: ok)" : " FAIL") : (moves ? " ok" : " FAIL")));
                            HuntDone();
                        });
                    };
                    t.Start();
                });
            });
        }

        // --case narrowsettings: every Settings page in a narrow window (pictures), and whether each card fits its width.
        void HuntNarrowSettings()
        {
            var widths = new[] { 480, 600, 760 };
            var steps = new List<Action>();
            foreach (int w in widths)
            {
                int ww = w;
                steps.Add(() => { WindowState = WindowState.Normal; Width = ww; Height = 760; });
                steps.Add(() => OpenSettings());
                for (int k = 0; k < settingsPages.Count; k++)
                {
                    int kk = k;
                    steps.Add(() => ShowSettingsPage(settingsPages[kk].Value));
                    steps.Add(() =>
                    {
                        var page = settingsPages[kk].Value;
                        string name = ((TextBlock)page.Children[0]).Text;
                        var scroll = F<System.Windows.Controls.ScrollViewer>("SettingsScroll");
                        int cut = 0, total = 0;
                        foreach (var el in page.Children.OfType<FrameworkElement>())
                        {
                            total++;
                            var right = el.TranslatePoint(new Point(el.ActualWidth, 0), scroll).X;
                            if (right > scroll.ViewportWidth + 1) cut++;
                        }
                        HuntLog("width " + ww + ", page " + name + ": " + cut + " of " + total + " items reach past the visible width (" + scroll.ViewportWidth.ToString("0") + ")" + (cut > 0 ? " FAIL" : ""));
                        foreach (var cb in FindAll<CheckBox>(page).Take(2)) { cb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); var g = cb.Parent as Grid; HuntLog("    switch wants " + cb.DesiredSize.Width.ToString("0") + " px, card grid " + (g == null ? -1 : g.ActualWidth).ToString("0") + ", glyph column " + (g == null ? -1 : g.ColumnDefinitions[0].ActualWidth).ToString("0") + ", row " + Grid.GetRow(cb)); }
                        Snap("w" + ww + "_" + kk + "_" + name.Replace(" ", ""));
                    });
                }
            }
            Action<int> go = null;
            go = i => { if (i >= steps.Count) { HuntDone(); return; } steps[i](); After(i % 2 == 0 ? 350 : 150, () => go(i + 1)); };
            go(0);
        }

        // Where (seconds into x) a 20 ms block of the sound's changes matches something lagMin..lagMax samples before it.
        static List<double> Repeated(float[] x0, int lagMin, int lagMax)
        {
            var found = new List<double>();
            var x = new float[x0.Length];
            for (int i = 1; i < x0.Length; i++) x[i] = x0[i] - x0[i - 1];
            const int B = 960;
            for (int at = lagMin + 4800; at + B < x.Length; at += 960)
            {
                double ee = 0;
                for (int j = 0; j < B; j += 4) ee += (double)x[at + j] * x[at + j];
                if (ee < B / 4 * 25.0) continue;
                double top = 0;
                for (int lag = lagMin; lag <= lagMax && at - lag >= 0; lag += 1)
                {
                    double xy = 0, yy = 1e-9;
                    int b = at - lag;
                    for (int j = 0; j < B; j += 4) { xy += (double)x[at + j] * x[b + j]; yy += (double)x[b + j] * x[b + j]; }
                    double c = xy / Math.Sqrt(ee * yy);
                    if (c > top) { top = c; if (top > 0.9) break; }
                }
                if (top > 0.9) found.Add(at / 48000.0);
            }
            return found;
        }
    }
}
