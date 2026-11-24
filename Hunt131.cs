// FastViewer 1.31.0 tests: --hunt <out> --case player [--player own|windows] [--pattern a.mp4,b.mp4]
// For every video and song in the folder: which player plays it, how long it took to open, pictures shown against screen
// refreshes, how fast pause / play / a jump / a frame step take effect, the stand-still at each Repeat loop, a storm of
// speed changes (holding for 2x again and again) and of volume changes, memory and processor time.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        // Every picture the own player hands to the screen: [Stopwatch tick, its time in the file] (kept by the player itself).
        List<double[]> presentTrace;
        bool traceOn { set { if (!value && traceVideo != null) traceVideo.Trace = null; } }
        OwnVideo traceVideo;

        void TracePresents(OwnVideo v)
        {
            presentTrace = new List<double[]>();
            traceVideo = v;
            v.Trace = presentTrace;
        }

        // How long until the own player shows a new picture (ms): its own time stamp of that picture.
        void WhenPresented(OwnVideo v, long from, Action<double> then)
        {
            var list = new List<double[]>();
            v.Trace = list;
            var d = Dispatcher;
            new Thread(delegate()
            {
                var w = Stopwatch.StartNew();
                int n = 0;
                while (w.ElapsedMilliseconds < 2000) { lock (list) n = list.Count; if (n > 0) break; Thread.Sleep(2); }
                double ms;
                lock (list) ms = list.Count > 0 ? (list[0][0] - from) * 1000.0 / Stopwatch.Frequency : -1;
                d.BeginInvoke(new Action(() => { if (v.Trace == list) v.Trace = null; then(ms); }));
            }) { IsBackground = true }.Start();
        }


        // What WPF itself draws (works hidden): along a line through the video's hole, how see-through each point is.
        void HoleCheck(string name)
        {
            var r = root;
            int w = (int)r.ActualWidth, h = (int)r.ActualHeight;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(snapDir, "wpf_" + name + ".png"))) enc.Save(fs);
            var hole = videoHoles.Count > 0 ? videoHoles[0] : Rect.Empty;
            var parts = new List<string>();
            int y = h / 3;
            for (int x = 20; x < w; x += w / 12) { int i = (y * w + x) * 4; parts.Add(x + ":" + px[i + 3] + (hole.Contains(new Point(x, y)) ? "h" : "")); }
            var hits = new List<string>();
            VisualTreeHelper.HitTest(this, null, hr => { var fe = hr.VisualHit as FrameworkElement; if (fe != null) hits.Add(fe.GetType().Name + (string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name) + " op" + fe.Opacity.ToString("0.00")); return HitTestResultBehavior.Continue; }, new PointHitTestParameters(new Point(w / 3, y)));
            HuntLog("  drawn at " + (w / 3) + "," + y + ": " + string.Join(", ", hits.Take(12)));
            HuntLog(name + " WPF alpha along y=" + y + " (h = inside the hole, 0 = see-through): " + string.Join(" ", parts) + "; window background " + Background + ", root " + root.Background + ", stage " + stage.Background + ", cover " + cover.Background);
        }
        double CpuNow() { return Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds; }
        double volCallMax, volCallSum; int volGcs;

        // What Task Manager shows as "Memory" (the private working set), in MB.
        static PerformanceCounter taskMem;
        internal static long TaskMemMB()
        {
            try
            {
                if (taskMem == null)
                {
                    var me = Process.GetCurrentProcess();
                    foreach (var inst in new PerformanceCounterCategory("Process").GetInstanceNames())
                        if (inst.StartsWith(me.ProcessName))
                            using (var idc = new PerformanceCounter("Process", "ID Process", inst, true))
                                if ((int)idc.RawValue == me.Id) { taskMem = new PerformanceCounter("Process", "Working Set - Private", inst, true); break; }
                }
                return taskMem == null ? -1 : taskMem.RawValue >> 20;
            }
            catch { return -1; }
        }

        void HuntPlayer()
        {
            muted = false;
            VideoConverter.FrameReader.Tracing = true;
            HuntLog("player: " + (OwnVideo.Enabled ? "FastViewer's own (Player.cs)" : "Windows' player") + ", " + "Task Manager " + TaskMemMB() + " MB, " + HuntResources());
            var names = files.Where(f => IsVideoFile(f) || IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            var photo = files.FirstOrDefault(f => !IsVideoFile(f) && !IsAudioFile(f));
            int i = 0;
            Action one = null;
            Action finish = () =>
            {
                if (photo != null)
                {
                    HuntOpen(Path.GetFileName(photo), () => After(2500, () => { GC.Collect(); After(500, () => { HuntLog("photo on screen: " + "Task Manager " + TaskMemMB() + " MB, " + HuntResources()); HuntDone(); }); }));
                }
                else HuntDone();
            };
            one = () =>
            {
                if (i >= names.Count) { finish(); return; }
                string nm = names[i++];
                var watch = Stopwatch.StartNew();
                (IsAudioFile(nm) ? (Action<string, Action>)HuntOpenAny : HuntOpen)(nm, () =>
                {
                    var s = shown;
                    var v = s.Video;
                    HuntLog("");
                    HuntLog(nm + ": " + (v.IsOwn ? "own player" : v.IsFallback && OwnVideo.Enabled ? "WINDOWS' PLAYER (fallback)" : "Windows' player") + (v.IsOwn ? ", opened in " + v.OpenMs + " ms" : "") + ", " + v.NaturalVideoWidth + "x" + v.NaturalVideoHeight + ", " + (v.NaturalDuration.HasTimeSpan ? F1(v.NaturalDuration.TimeSpan.TotalSeconds) : "?") + " s, sound " + v.HasAudio + ", shown " + watch.ElapsedMilliseconds + " ms after asking (incl. 500 ms wait)");
                    loop = true;
                    if (!playing) TogglePlay();
                    SetSpeed(1);
                    After(1200, () => Measure(nm, s, () => After(300, one)));
                });
            };
            After(500, one);
        }

        void Measure(string nm, Slot s, Action done)
        {
            var v = s.Video;
            var steps = new List<Action<Action>>();
            // 1. Pictures and screen refreshes over 3 s.
            steps.Add(next =>
            {
                int p0 = v.Presents, r0 = Presenter.Refreshes, sk0 = v.Skipped, l0 = v.Late;
                double c0 = CpuNow(); long t0 = Stopwatch.GetTimestamp(); var th0 = ThreadIds.Snap(); ThreadIds.Note("window"); int fc0 = OwnVideo.FrameCalls;
                if (v.IsOwn) TracePresents(v);
                After(3000, () =>
                {
                    double secs = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                    traceOn = false;
                    double fps = v.MeasuredFps;
                    if (v.IsOwn)
                    {
                        // Stand-stills: gaps between pictures longer than 1.5 frames (loops included).
                        var tr = presentTrace; double worst = 0; int gaps = 0;
                        lock (tr) for (int k = 1; k < tr.Count; k++) { double g = (tr[k][0] - tr[k - 1][0]) * 1000 / Stopwatch.Frequency; if (g > worst) worst = g; if (fps > 0 && g > 1500 / fps + Presenter.Period * 1000) gaps++; }
                        HuntLog(string.Format("  playing {0} s: pictures {1}/s (video {2:0.#} fps, screen {3}/s, refresh {4} ms), skipped {5}, late {6}, longest gap between pictures {7} ms, gaps over 1.5 frames {8}, CPU {9}% of one core, {10}",
                            F1(secs), F1((v.Presents - p0) / secs), fps, F1((Presenter.Refreshes - r0) / secs), (Presenter.Period * 1000).ToString("0.00"), v.Skipped - sk0, v.Late - l0, F1(worst), gaps, F1((CpuNow() - c0) / 10 / secs), "Task Manager " + TaskMemMB() + " MB, " + HuntResources()));
                        HuntLog("  processor by thread: " + ThreadIds.Usage(th0, secs) + "; window placed the video " + F1((OwnVideo.FrameCalls - fc0) / secs) + " times a second");
                        var b = v.Snapshot();
                        if (b != null) HuntLog("  picture on screen: " + b.PixelWidth + "x" + b.PixelHeight + ", average " + F1(Average(b)) + " (0 = black)");
                        HuntLog("  video window up " + VideoBack.For(this).Up + ", holes " + videoHoles.Count + (videoHoles.Count > 0 ? " " + videoHoles[0] : "") + ", " + VideoBack.For(this).BackNote);
                    }
                    else HuntLog("  playing " + F1(secs) + " s: CPU " + F1((CpuNow() - c0) / 10 / secs) + "% of one core, " + "Task Manager " + TaskMemMB() + " MB, " + HuntResources());
                    next();
                });
            });
            // 2. Pause and play: how soon the picture stops / moves again.
            steps.Add(next =>
            {
                double at0 = v.IsOwn ? v.CountedNow() : v.Position.TotalSeconds;
                long t = Stopwatch.GetTimestamp();
                TogglePlay(); // pause
                double ui = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
                int pAfter = v.Presents;
                After(400, () =>
                {
                    double moved = (v.IsOwn ? v.CountedNow() : v.Position.TotalSeconds) - at0;
                    HuntLog("  pause: took " + F1(ui) + " ms on the window's thread; pictures after it " + (v.Presents - pAfter) + ", clock moved " + F1(moved * 1000) + " ms in 400 ms");
                    long t2 = Stopwatch.GetTimestamp();
                    TogglePlay(); // play
                    double ui2 = (Stopwatch.GetTimestamp() - t2) * 1000.0 / Stopwatch.Frequency;
                    if (v.IsOwn) WhenPresented(v, t2, ms => { HuntLog("  play: took " + F1(ui2) + " ms on the window's thread, next picture after " + F1(ms) + " ms"); After(300, next); });
                    else After(300, () => { HuntLog("  play: took " + F1(ui2) + " ms on the window's thread"); next(); });
                });
            });
            // 3. Ten fast pause/play clicks, then: still playing, picture moving?
            steps.Add(next =>
            {
                int k = 0;
                Action click = null;
                click = () =>
                {
                    if (k++ < 10) { TogglePlay(); After(45, click); return; }
                    double a = v.IsOwn ? v.CountedNow() : v.Position.TotalSeconds;
                    After(500, () => { HuntLog("  10 fast clicks: playing " + playing + ", clock moved " + F1(((v.IsOwn ? v.CountedNow() : v.Position.TotalSeconds) - a) * 1000) + " ms in 500 ms" + (playing && (v.IsOwn ? v.CountedNow() : v.Position.TotalSeconds) - a < 0.3 ? " FAIL" : "")); next(); });
                };
                click();
            });
            // 4. Jumps while paused (frame step back and forth, +/-2 s) and while playing.
            steps.Add(next =>
            {
                if (playing) TogglePlay();
                var jumps = new[] { 0.5, 0.5 + s.FrameSeconds, 0.5, 2.0, 1.0, 0.0 };
                int k = 0;
                var times = new List<string>();
                Action jump = null;
                jump = () =>
                {
                    if (k >= jumps.Length || !v.IsOwn)
                    {
                        HuntLog("  jumps while paused (ms to the picture): " + string.Join(", ", times));
                        TogglePlay();
                        long t = Stopwatch.GetTimestamp();
                        After(600, () =>
                        {
                            long t2 = Stopwatch.GetTimestamp();
                            SeekBy(1.0);
                            if (v.IsOwn) WhenPresented(v, t2, ms => { HuntLog("  +1 s while playing: picture after " + F1(ms) + " ms"); After(400, next); });
                            else After(400, next);
                        });
                        return;
                    }
                    double to = jumps[k++];
                    long t0 = Stopwatch.GetTimestamp();
                    v.Position = TimeSpan.FromSeconds(to);
                    WhenPresented(v, t0, ms => { times.Add(to.ToString("0.000") + ": " + F1(ms) + " (shows " + v.shownFile.ToString("0.000") + ")"); if (ms < 0) HuntLog("    NOT SHOWN: " + v.Debug()); After(150, jump); });
                };
                jump();
            });
            // 5. Loops (Repeat): the longest stand-still around each start-over, over the clip's length x2 (at least 3 s).
            steps.Add(next =>
            {
                if (!v.IsOwn || !v.NaturalDuration.HasTimeSpan) { next(); return; }
                double len = v.NaturalDuration.TimeSpan.TotalSeconds;
                v.Position = TimeSpan.FromSeconds(Math.Max(0, len - 0.6));
                After(300, () =>
                {
                    TracePresents(v);
                    After((int)(Math.Max(3, 2 * len) * 1000), () =>
                    {
                        traceOn = false;
                        var tr = presentTrace;
                        var seams = new List<string>();
                        lock (tr)
                            for (int k = 1; k < tr.Count; k++)
                                if (tr[k][1] < tr[k - 1][1] - 0.2) // the picture's time went back: a loop start
                                {
                                    double gap = (tr[k][0] - tr[k - 1][0]) * 1000 / Stopwatch.Frequency;
                                    seams.Add(F1(gap) + " ms (from " + tr[k - 1][1].ToString("0.000") + " to " + tr[k][1].ToString("0.000") + ")");
                                }
                        HuntLog("  loop starts: " + seams.Count + ", time between the last picture and the first of the next loop: " + string.Join(", ", seams) + " (a normal frame is " + F1(1000 / Math.Max(1, v.MeasuredFps)) + " ms)");
                        next();
                    });
                });
            });
            // 6. Speed storm: hold for 2x and let go 20 times quickly; the pictures must keep coming at the right pace.
            steps.Add(next =>
            {
                if (!v.IsOwn) { next(); return; }
                TracePresents(v);
                int k = 0;
                double c0 = v.CountedNow(); long t0 = Stopwatch.GetTimestamp();
                Action flip = null;
                flip = () =>
                {
                    if (k < 20) { SetSpeed(k % 2 == 0 ? 2 : 1); k++; After(90, flip); return; }
                    SetSpeed(1);
                    After(500, () =>
                    {
                        traceOn = false;
                        double worst = 0;
                        var tr = presentTrace;
                        lock (tr) for (int j = 1; j < tr.Count; j++) worst = Math.Max(worst, (tr[j][0] - tr[j - 1][0]) * 1000 / Stopwatch.Frequency);
                        double secs = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                        HuntLog("  speed storm (2x/1x x20, 90 ms apart): clock ran " + F1(v.CountedNow() - c0) + " s in " + F1(secs) + " s (expect ~" + F1(secs + 0.9) + "), longest gap between pictures " + F1(worst) + " ms, speed now " + v.SpeedRatio);
                        next();
                    });
                };
                flip();
            });
            // 7. Volume storm: the wheel up and down fast (60 steps, 8 ms apart).
            steps.Add(next =>
            {
                int k = 0;
                double startVol = volume;
                long t0 = Stopwatch.GetTimestamp();
                Action wheel = null;
                double worstUi = 0;
                long last = t0;
                var ui = Thread.CurrentThread; var stacks = new Dictionary<string, int>(); bool sampling = true;
                new Thread(delegate() { while (sampling) { Thread.Sleep(15); if ((Stopwatch.GetTimestamp() - Interlocked.Read(ref last)) * 1000.0 / Stopwatch.Frequency < 50) continue; string tr; try { // (the window thread has not ticked for 50 ms: where is it?)
#pragma warning disable 618
                    ui.Suspend(); try { tr = new StackTrace(ui, false).ToString(); } finally { ui.Resume(); }
#pragma warning restore 618
                    } catch { continue; } var top = string.Join(" < ", tr.Split((char)10).Select(l => l.Trim()).Where(l => l.Length > 0).Take(7).Select(l => l.Replace("at ", ""))); lock (stacks) { int c; stacks.TryGetValue(top, out c); stacks[top] = c + 1; } } }) { IsBackground = true }.Start();
                wheel = () =>
                {
                    long now = Stopwatch.GetTimestamp();
                    worstUi = Math.Max(worstUi, (now - last) * 1000.0 / Stopwatch.Frequency);
                    last = now;
                    if (k < 60) { long sv = Stopwatch.GetTimestamp(); int g0 = GC.CollectionCount(0) + GC.CollectionCount(1) * 100 + GC.CollectionCount(2) * 10000; SetVolume(volume + (k / 10 % 2 == 0 ? 0.05 : -0.05)); double took = (Stopwatch.GetTimestamp() - sv) * 1000.0 / Stopwatch.Frequency; volCallMax = Math.Max(volCallMax, took); volCallSum += took; volGcs += GC.CollectionCount(0) + GC.CollectionCount(1) * 100 + GC.CollectionCount(2) * 10000 - g0; k++; After(8, wheel); return; }
                    sampling = false;
                    lock (stacks) foreach (var kv in stacks.OrderByDescending(x => x.Value).Take(4)) HuntLog("    window thread busy (" + kv.Value + " samples): " + kv.Key);
                    var p = soundPlayer;
                    HuntLog("  volume storm (60 steps): " + F1((now - t0) * 1000.0 / Stopwatch.Frequency) + " ms, longest step " + F1(worstUi) + " ms, SetVolume itself max " + F1(volCallMax) + " ms, total " + F1(volCallSum) + " ms, collections (gen0+100 gen1+10000 gen2) " + volGcs + ", volume " + startVol.ToString("0.00") + " -> " + volume.ToString("0.00") + ", sound thread asked volume " + (p == null ? "-" : p.AskedVolume.ToString("0.00")) + (p == null ? "" : ", output delay " + F1(p.OutputDelay * 1000) + " ms"));
                    SetVolume(startVol);
                    next();
                };
                wheel();
            });
            Action<int> run = null;
            run = n => { if (n >= steps.Count) { done(); return; } steps[n](() => run(n + 1)); };
            run(0);
        }

        // --case screen (with --front): what the screen really shows (the video lies in a window of its own behind this one,
        // so only a picture of the screen itself shows both): playing with the controls up, paused, full screen, mini player.
        void HuntScreen()
        {
            var video = files.FirstOrDefault(f => IsVideoFile(f) && !IsAudioFile(f));
            if (video == null) { HuntLog("no video"); HuntDone(); return; }
            Action<string> grab = name =>
            {
                if (!testInFront) { HoleCheck(name); return; } // (hidden: never a picture of the real screen, which shows the user's own windows)
                try
                {
                    var src = PresentationSource.FromVisual(this);
                    var m = src.CompositionTarget.TransformToDevice;
                    var p = PointToScreen(new Point(0, 0));
                    int w = (int)(ActualWidth * m.M11), h = (int)(ActualHeight * m.M22);
                    using (var bmp = new System.Drawing.Bitmap(w, h))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, bmp.Size);
                        bmp.Save(Path.Combine(snapDir, "screen_" + name + ".png"));
                        // The middle of the video and a corner of the window (the look's background).
                        var c = bmp.GetPixel(w / 2, h / 2); var k = bmp.GetPixel(8, h - 8);
                        HuntLog(name + ": window " + w + "x" + h + " at " + p.X + "," + p.Y + ", middle " + c.R + "," + c.G + "," + c.B + ", corner " + k.R + "," + k.G + "," + k.B + ", holes " + videoHoles.Count + (videoHoles.Count > 0 ? " " + videoHoles[0] : "") + ", video window up " + VideoBack.For(this).Up + ", zoom " + (shown == null ? 0 : shown.Zoom.ScaleX).ToString("0.00") + "; " + VideoBack.For(this).Describe() + "; " + (shown != null && shown.IsVideo ? shown.Video.Debug() : ""));
                    }
                }
                catch (Exception ex) { HuntLog(name + ": no picture of the screen: " + ex.Message); }
            };
            HuntOpen(Path.GetFileName(video), () =>
            {
                var steps = new List<Action>
                {
                    () => { if (!playing) TogglePlay(); ShowOverlay(); },
                    () => grab("playing_controls"),
                    () => HoleCheck("playing_controls"),
                    () => { int on = 1; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 13, ref on, 4); }, // (FastViewer's window away: only the video window is left)
                    () => grab("video_window_alone"),
                    () => { int off = 0; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 13, ref off, 4); },
                    () => { var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle; SendMessageExp(hw, 0x86, IntPtr.Zero, IntPtr.Zero); SendMessageExp(hw, 0x86, (IntPtr)1, IntPtr.Zero); RedrawWindowExp(hw, IntPtr.Zero, IntPtr.Zero, 0x400 | 1 | 0x100); },
                    () => { GrabScreen("after_activate"); EdgeLog("after_activate"); },
                    () => WindowState = WindowState.Maximized, () => { GrabScreen("maximized"); EdgeLog("maximized"); }, () => WindowState = WindowState.Normal,
                    () => RealActivate(true), () => { GrabScreen("active1"); EdgeLog("active1"); }, () => RealActivate(false), () => { GrabScreen("inactive1"); EdgeLog("inactive1"); },
                    () => RealActivate(true), () => { GrabScreen("active2"); EdgeLog("active2"); }, () => RealActivate(false), () => { GrabScreen("inactive2"); EdgeLog("inactive2"); },
                    () =>
                    {
                        var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        SendMessageExp(hw, 0x323, IntPtr.Zero, (IntPtr)((300 << 16) | 200)); HuntLog("preview: " + VideoBack.LastPreview);
                        SendMessageExp(hw, 0x326, IntPtr.Zero, IntPtr.Zero); HuntLog("preview: " + VideoBack.LastPreview);
                        SendMessageExp(hw, 0x323, IntPtr.Zero, (IntPtr)((300 << 16) | 200)); HuntLog("preview again: " + VideoBack.LastPreview);
                        SendMessageExp(hw, 0x326, IntPtr.Zero, IntPtr.Zero); HuntLog("preview again: " + VideoBack.LastPreview);
                        var b = VideoBack.For(this).Preview(0, 0);
                        if (b != null) using (var fs = File.Create(Path.Combine(snapDir, "preview.png"))) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b)); enc.Save(fs); }
                    },
                    () => { var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle; SetWindowPos(hw, IntPtr.Zero, 0, 0, (int)(ActualWidth * 1.25) - 120, (int)(ActualHeight * 1.25) - 60, 2 | 4 | 0x10); GrabScreen("resizing"); EdgeLog("resizing"); },
                    () => { TogglePlay(); ShowOverlay(); },
                    () => grab("paused"),
                    () => ToggleFullscreen(),
                    () => { ShowOverlay(); grab("fullscreen"); },
                    () => ToggleFullscreen(),
                    () => { ZoomBy(2.0, new Point(stage.ActualWidth / 2, stage.ActualHeight / 2)); },
                    () => grab("zoomed"),
                    () => ResetZoom(shown),
                };
                int i = 0;
                Action go = null;
                go = () => { if (i >= steps.Count) { HuntDone(); return; } steps[i++](); After(900, go); };
                go();
            });
        }

        // --case rotscreen --front (scratch copies only: the turns are saved into the files): every video turned 4 times,
        // the real screen captured right after each click and again after the save; logs where the picture lies.
        void HuntRotScreen()
        {
            saveRotation = true;
            var vids = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).ToList();
            var steps = new List<Action>();
            foreach (var f in vids)
            {
                string file = f, n = Path.GetFileNameWithoutExtension(f);
                steps.Add(() => ShowIndex(files.IndexOf(file), 1));
                steps.Add(() => { if (!playing) TogglePlay(); });
                steps.Add(() => GrabScreen(n + "_0"));
                for (int i = 1; i <= 4; i++)
                {
                    int k = i;
                    steps.Add(() => { Rotate(); After(400, () => GrabScreen(n + "_" + k + "_click")); });
                    steps.Add(() => SaveWaitingRotations());
                    steps.Add(() => { });
                    steps.Add(() => { });
                    steps.Add(() => GrabScreen(n + "_" + k + "_saved"));
                }
            }
            int at = 0;
            Action go = null;
            go = () => { if (at >= steps.Count) { HuntDone(); return; } steps[at++](); After(900, go); };
            After(1500, go);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessageExp(IntPtr h, int m, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RedrawWindow")] static extern bool RedrawWindowExp(IntPtr h, IntPtr r, IntPtr rgn, int flags);
        // (The window's outer 3 pixel columns, left and right, half way down: the look's colour, or a Windows frame painted over it.)
        void EdgeLog(string name)
        {
            try
            {
                using (var b = new System.Drawing.Bitmap(Path.Combine(snapDir, "screen_" + name + ".png")))
                {
                    var parts = new List<string>();
                    foreach (int x in new[] { 0, 1, 2, b.Width - 3, b.Width - 2, b.Width - 1 }) { var c = b.GetPixel(x, b.Height / 2); parts.Add(c.R + "," + c.G + "," + c.B); }
                    HuntLog(name + " edges: " + string.Join(" ", parts));
                }
            }
            catch (Exception ex) { HuntLog(name + " edges: " + ex.Message); }
        }

        // --case solo: 45 s of fast switching through a folder of videos, with skips to the end (Repeat or not), hold for 2x,
        // play/pause and (with the settings' Crossfade) the fades; every 100 ms: which players other than the one on screen
        // still play or aren't muted, which file FastViewer's sound plays, how many windows of this FastViewer Alt+Tab could
        // list, and how many FastViewers run from this exe. FV_NOSOLO=1: the window's own watch (KeepSolo) is off, so the test
        // sees what the switching itself leaves behind.
        void HuntSolo()
        {
            var rnd = new Random(7);
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            int actions = 0, audits = 0, strayPlay = 0, strayLoud = 0, straySound = 0, extraWindows = 0, extraProcs = 0, holds = 0, loops = 0, ends = 0;
            var since = new Dictionary<string, long>();
            var notes = new List<string>();
            var watch = Stopwatch.StartNew();
            Func<string, bool, bool> lasting = (key, now) =>
            {
                if (!now) { since.Remove(key); return false; }
                long t; if (!since.TryGetValue(key, out t)) { since[key] = watch.ElapsedMilliseconds; return false; }
                return watch.ElapsedMilliseconds - t > 700; // (longer than a switch and a fade)
            };
            var audit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            audit.Tick += delegate
            {
                audits++;
                foreach (var s in slots)
                {
                    if (s == null || s == shown || s == revealing) continue;
                    string n = Path.GetFileName(s.File);
                    if (lasting("play " + s.GetHashCode(), s.Video.WantsToPlay)) { strayPlay++; if (notes.Count < 12) notes.Add(watch.ElapsedMilliseconds + " ms: hidden " + n + " plays (shown " + Path.GetFileName(shown != null ? shown.File : null) + ")"); since.Remove("play " + s.GetHashCode()); }
                    if (lasting("loud " + s.GetHashCode(), !s.Video.IsMuted)) { strayLoud++; if (notes.Count < 12) notes.Add(watch.ElapsedMilliseconds + " ms: hidden " + n + " not muted"); since.Remove("loud " + s.GetHashCode()); }
                }
                var sp = soundPlayer;
                string heard = sp != null && sp.Sounding ? sp.WishFile : null;
                bool off = heard != null && (shown == null || !string.Equals(heard, shown.File, StringComparison.OrdinalIgnoreCase));
                if (lasting("sound", off)) { straySound++; if (notes.Count < 12) notes.Add(watch.ElapsedMilliseconds + " ms: sound of " + Path.GetFileName(heard) + " while " + Path.GetFileName(shown != null ? shown.File : null) + " shows"); since.Remove("sound"); }
                int listed = 0; string bad = null;
                var me = (uint)Process.GetCurrentProcess().Id;
                EnumWindowsExp((h, l) =>
                {
                    uint pid; GetWindowThreadProcessIdExp(h, out pid);
                    if (pid != me || !IsWindowVisibleExp(h)) return true;
                    long ex = (long)GetWindowLongPtrExp(h, -20);
                    var c = new System.Text.StringBuilder(64); GetWindowTextExp(h, c, 64);
                    if (c.ToString() == "FastViewer video" && ((ex & 0x80) == 0 || (ex & 0x40000) != 0)) bad = "video window ex style " + ex.ToString("X");
                    if ((ex & 0x80) == 0) listed++;
                    return true;
                }, IntPtr.Zero);
                if (listed > 1 || bad != null) { extraWindows++; if (notes.Count < 12) notes.Add(watch.ElapsedMilliseconds + " ms: " + listed + " windows Alt+Tab can list" + (bad != null ? ", " + bad : "")); }
                if (audits % 10 == 0)
                {
                    int procs = Process.GetProcessesByName("FastViewer").Count(p => { try { return string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                    if (procs > 1) { extraProcs++; if (notes.Count < 12) notes.Add(watch.ElapsedMilliseconds + " ms: " + procs + " FastViewers from this exe"); }
                }
            };
            System.Windows.Threading.DispatcherTimer act = null;
            act = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            act.Tick += delegate
            {
                act.Interval = TimeSpan.FromMilliseconds(40 + rnd.Next(260));
                if (watch.ElapsedMilliseconds > 45000)
                {
                    act.Stop();
                    if (holding) { HoldReleased(); holdTestPressed = false; }
                    After(2500, () =>
                    {
                        audit.Stop();
                        HuntLog("solo: " + actions + " actions (" + holds + " holds, " + loops + " Repeat changes, " + ends + " skips to the end), " + audits + " checks; crossfade " + crossfadeFiles + ", watch " + (Environment.GetEnvironmentVariable("FV_NOSOLO") == "1" ? "off" : "on"));
                        HuntLog("solo: hidden players playing " + strayPlay + ", hidden players not muted " + strayLoud + ", sound of another file " + straySound + ", extra windows " + extraWindows + ", extra FastViewers " + extraProcs + ", the watch caught " + SoloCatches + (SoloLast != null ? " (last: " + SoloLast + ")" : "") + (strayPlay + strayLoud + straySound + extraWindows + extraProcs == 0 ? "" : " FAIL"));
                        foreach (var n in notes) HuntLog("  " + n);
                        HuntDone();
                    });
                    return;
                }
                actions++;
                int r = rnd.Next(100);
                if (holding && r < 50) { HoldReleased(); holdTestPressed = false; return; }
                if (r < 55) Go(rnd.Next(3) == 0 ? -1 : 1);
                else if (r < 67 && shown != null && shown.IsVideo && !holding) { holds++; holdTestPressed = true; holdPending = true; HoldTick(); }
                else if (r < 72) { loops++; ToggleLoop(); }
                else if (r < 82) TogglePlay();
                else if (shown != null && shown.IsVideo && shown.Video.NaturalDuration.HasTimeSpan) { ends++; SeekBy(shown.Video.NaturalDuration.TimeSpan.TotalSeconds - shown.Video.Position.TotalSeconds - 0.2); }
            };
            After(1500, () => { audit.Start(); act.Start(); });
        }

        // --case thumbs: the taskbar thumbnail asked for 25 times a second for 4 s while a video plays (as Windows does while it
        // shows): how long each takes on the window's thread, and pictures shown / skipped / late before and during it.
        void HuntThumbs()
        {
            var video = files.FirstOrDefault(f => IsVideoFile(f) && !IsAudioFile(f));
            HuntOpen(Path.GetFileName(video), () =>
            {
                if (!playing) TogglePlay();
                var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var v = shown.Video;
                Func<string> counts = () => v.Presents + "/" + v.Skipped + "/" + v.Late;
                int p0 = 0, s0 = 0, l0 = 0;
                Action mark = () => { p0 = v.Presents; s0 = v.Skipped; l0 = v.Late; };
                Func<string, string> since = name => name + ": pictures " + ((v.Presents - p0) / 4.0).ToString("0.0") + "/s, skipped " + (v.Skipped - s0) + ", late " + (v.Late - l0) + "; playing " + playing + ", shown is this " + (shown != null && shown.Video == v) + ", " + v.Debug();
                After(1500, () =>
                {
                    mark();
                    After(4000, () =>
                    {
                        HuntLog(since("without thumbnail"));
                        mark();
                        var times = new List<double>();
                        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                        var all = Stopwatch.StartNew();
                        t.Tick += delegate
                        {
                            var sw = Stopwatch.StartNew();
                            SendMessageExp(hw, 0x323, IntPtr.Zero, (IntPtr)((300 << 16) | 200));
                            times.Add(sw.Elapsed.TotalMilliseconds);
                            if (all.ElapsedMilliseconds < 4000) return;
                            t.Stop();
                            HuntLog(since("with thumbnail asked 25/s"));
                            times.Sort();
                            HuntLog("thumbnail: " + times.Count + " in 4 s, median " + times[times.Count / 2].ToString("0.0") + " ms, slowest " + times[times.Count - 1].ToString("0.0") + " ms; " + VideoBack.LastPreview);
                            var b = VideoBack.For(this).Preview(300, 200);
                            if (b != null) using (var fs = File.Create(Path.Combine(snapDir, "thumb.png"))) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b)); enc.Save(fs); }
                            HuntDone();
                        };
                        t.Start();
                    });
                });
            });
        }

        // --case thumbaspect: the taskbar thumbnail's picture (as sent to Windows) saved at moments where its size or place
        // can be off: right after opening each video (0.1/0.5/1.5 s), after a switch, right after a resize, after a turn, in
        // full screen; with the window's shape and the video's box logged, and the bitmap's shape checked against the window's.
        void HuntThumbAspect()
        {
            var vids = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).ToList();
            var steps = new List<Tuple<int, Action>>();
            int n = 0;
            Action<string> save = name =>
            {
                var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                SendMessageExp(hw, 0x323, IntPtr.Zero, (IntPtr)((300 << 16) | 200));
                var b = VideoBack.For(this).Preview(300, 200);
                if (b == null) { HuntLog(name + ": no picture"); return; }
                using (var fs = File.Create(Path.Combine(snapDir, "ta_" + (++n).ToString("00") + "_" + name + ".png"))) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b)); enc.Save(fs); }
                double wa = ActualWidth / ActualHeight, ba = b.PixelWidth / (double)b.PixelHeight;
                HuntLog(n.ToString("00") + " " + name + ": bitmap " + b.PixelWidth + "x" + b.PixelHeight + " (shape " + ba.ToString("0.000") + ", window " + wa.ToString("0.000") + (b.PixelWidth == 300 || b.PixelHeight == 200 ? "" : " NOT THE ASKED SIZE") + "), video element " + (shown != null && shown.IsVideo ? shown.Video.ActualWidth.ToString("0") + "x" + shown.Video.ActualHeight.ToString("0") + " turn " + shown.Rotation.Angle : "-") + "; " + VideoBack.LastPreview);
            };
            int t = 1000;
            foreach (var f in vids)
            {
                string file = f, name = Path.GetFileNameWithoutExtension(f);
                steps.Add(Tuple.Create(t, (Action)(() => ShowIndex(files.IndexOf(file), 1))));
                steps.Add(Tuple.Create(t + 100, (Action)(() => save(name + "_0.1s"))));
                steps.Add(Tuple.Create(t + 500, (Action)(() => save(name + "_0.5s"))));
                steps.Add(Tuple.Create(t + 1500, (Action)(() => save(name + "_1.5s"))));
                t += 2500;
            }
            var first = vids[0];
            steps.Add(Tuple.Create(t, (Action)(() => ShowIndex(files.IndexOf(first), 1))));
            steps.Add(Tuple.Create(t + 1500, (Action)(() => { var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle; SetWindowPos(hw, IntPtr.Zero, 0, 0, 900, 900, 2 | 4 | 0x10); save("resized_at_once"); })));
            steps.Add(Tuple.Create(t + 2500, (Action)(() => save("resized_1s"))));
            steps.Add(Tuple.Create(t + 3000, (Action)(() => { Rotate(); save("turned_at_once"); })));
            steps.Add(Tuple.Create(t + 4000, (Action)(() => save("turned_1s"))));
            steps.Add(Tuple.Create(t + 4500, (Action)(() => { Rotate(); Rotate(); Rotate(); ToggleFullscreen(); })));
            steps.Add(Tuple.Create(t + 5500, (Action)(() => save("fullscreen"))));
            steps.Add(Tuple.Create(t + 6000, (Action)(() => ToggleFullscreen())));
            steps.Add(Tuple.Create(t + 7000, (Action)(() => save("window_again"))));
            foreach (var s in steps) After(s.Item1, s.Item2);
            After(t + 7500, HuntDone);
        }

        delegate bool EnumProcExp(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtrExp(IntPtr h, int i);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "EnumWindows")] static extern bool EnumWindowsExp(EnumProcExp p, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint GetWindowThreadProcessIdExp(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindowVisible")] static extern bool IsWindowVisibleExp(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowTextExp(IntPtr h, System.Text.StringBuilder s, int n);

        // (--front only: a real activation of the test window, and back to the window that had the focus. About a second.)
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        IntPtr focusBefore;
        void RealActivate(bool on)
        {
            if (!testInFront) return;
            var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            IntPtr target = on ? hw : focusBefore;
            if (on) focusBefore = GetForegroundWindow();
            if (target == IntPtr.Zero) return;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
            bool ok = SetForegroundWindow(target);
            if (attached) AttachThreadInput(me, fgThread, false);
            HuntLog((on ? "activated" : "focus given back") + ": " + ok + ", foreground now " + (GetForegroundWindow() == hw ? "test window" : "other"));
        }

        void GrabScreen(string name)
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                var m = src.CompositionTarget.TransformToDevice;
                var p = PointToScreen(new Point(0, 0));
                int w = (int)(ActualWidth * m.M11), h = (int)(ActualHeight * m.M22);
                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, bmp.Size);
                    bmp.Save(Path.Combine(snapDir, "screen_" + name + ".png"));
                    var stagePt = stage.TranslatePoint(new Point(8, stage.ActualHeight / 2), this);
                    var side = bmp.GetPixel((int)(stagePt.X * m.M11), (int)(stagePt.Y * m.M22));
                    int a; rotations.TryGetValue(shown.File, out a);
                    HuntLog(name + ": turn " + a + ", in file " + VideoInfo.Rotation(shown.File) + ", stage edge " + side.R + "," + side.G + "," + side.B + ", holes " + string.Join(" ", videoHoles) + "; " + (shown.IsVideo ? shown.Video.Debug() : ""));
                }
            }
            catch (Exception ex) { HuntLog(name + ": no picture: " + ex.Message); }
        }

        // --case memprobe: what one frame reader, its pool and a swap chain cost in memory (a60.mp4 or the first video).
        void HuntMemProbe()
        {
            var file = files.FirstOrDefault(f => IsVideoFile(f) && !IsAudioFile(f));
            var log = new List<string>();
            VideoConverter.FrameReader.TestNoAdvanced = Environment.GetEnvironmentVariable("FV_NOADV") == "1";
            int.TryParse(Environment.GetEnvironmentVariable("FV_POOL") ?? "0", out VideoConverter.FrameReader.TestPool);
            HuntLog("probe: advanced processing " + !VideoConverter.FrameReader.TestNoAdvanced + ", pool " + VideoConverter.FrameReader.TestPool + ", Task Manager memory at start " + TaskMemMB() + " MB");
            var d = Dispatcher;
            new Thread(delegate()
            {
                Func<string> mem = () => { var p = Process.GetCurrentProcess(); p.Refresh(); return "private " + (p.PrivateMemorySize64 >> 20) + " MB, working set " + (p.WorkingSet64 >> 20) + " MB, Task Manager " + TaskMemMB() + " MB"; };
                log.Add("start: " + mem());
                var readers = new List<VideoConverter.FrameReader>();
                for (int n = 0; n < 3; n++)
                {
                    var r = new VideoConverter.FrameReader(file, 1920, 1080, IntPtr.Zero, true);
                    r.Ask(0, 0, 1);
                    var w = Stopwatch.StartNew();
                    while (!r.Opened && !r.Failed && w.ElapsedMilliseconds < 5000) Thread.Sleep(2);
                    long opened = w.ElapsedMilliseconds;
                    while (r.Waiting == 0 && !r.Failed && w.ElapsedMilliseconds < 5000) Thread.Sleep(2);
                    long first = w.ElapsedMilliseconds;
                    Thread.Sleep(800);
                    GC.Collect();
                    log.Add("reader " + (n + 1) + ": opened " + opened + " ms (" + r.OpenMs + " inside), first picture " + first + " ms, " + r.Waiting + " waiting, on card " + r.OnGpu + ": " + mem());
                    readers.Add(r);
                }
                var chains = new List<IntPtr>();
                if (Comp.Ready) for (int n = 0; n < 3; n++) { chains.Add(Comp.SwapChain(1920, 1080)); chains.Add(Comp.Texture(1920, 1080)); }
                Thread.Sleep(500);
                log.Add("plus 3 swap chains and 3 textures: " + mem());
                foreach (var r in readers) r.Quit();
                Thread.Sleep(1500);
                GC.Collect();
                log.Add("readers gone: " + mem());
                foreach (var c in chains) if (c != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(c);
                Thread.Sleep(500);
                log.Add("swap chains gone: " + mem());
                d.BeginInvoke(new Action(() => { foreach (var l in log) HuntLog(l); HuntDone(); }));
            }) { IsBackground = true }.Start();
        }

        static double Average(BitmapSource b)
        {
            var c = new FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            int w = c.PixelWidth, h = c.PixelHeight;
            var px = new byte[w * h * 4];
            c.CopyPixels(px, w * 4, 0);
            double sum = 0; long n = 0;
            for (int i = 0; i < px.Length; i += 4 * 97) { sum += px[i] + px[i + 1] + px[i + 2]; n += 3; }
            return sum / Math.Max(1, n);
        }
    }
}

namespace FastViewer
{
    // (Tests: which Windows thread is which, to see where processor time goes.)
    static class ThreadIds
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
        public static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        public static void Note(string name) { lock (Names) Names[GetCurrentThreadId()] = name; }
        public static string Usage(Dictionary<int, double> before, double secs)
        {
            var now = new Dictionary<int, double>();
            foreach (ProcessThread t in Process.GetCurrentProcess().Threads) try { now[t.Id] = t.TotalProcessorTime.TotalMilliseconds; } catch { }
            var byName = new Dictionary<string, double>();
            foreach (var kv in now)
            {
                double b; before.TryGetValue(kv.Key, out b);
                string n; lock (Names) if (!Names.TryGetValue(kv.Key, out n)) n = "other";
                double d; byName.TryGetValue(n, out d); byName[n] = d + (kv.Value - b);
            }
            return string.Join(", ", byName.OrderByDescending(x => x.Value).Select(x => x.Key + " " + (x.Value / 10 / secs).ToString("0.0") + "%"));
        }
        public static Dictionary<int, double> Snap()
        {
            var d = new Dictionary<int, double>();
            foreach (ProcessThread t in Process.GetCurrentProcess().Threads) try { d[t.Id] = t.TotalProcessorTime.TotalMilliseconds; } catch { }
            return d;
        }
    }

    partial class ViewerWindow
    {
        // ---- 1.31.1: --case skiphold. A 1.29 leftover: a skip into the last 2-3 s, then hold for 2x at once, played a bit of sound
        // twice before the real loop. Each run records what the speakers got and looks for doubled sound around the jump and
        // around the loop (DoubledMs: pink-noise clips only), and for silence there.
        void HuntSkipHold()
        {
            muted = false; Loudness.On = false; loop = true; UpdateControls();
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => nn.Equals(huntPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            var setCount = typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            Action<bool> press = down =>
            {
                var a = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { 1 });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            var runs = new List<Action<Action>>();
            int bad = 0, total = 0;
            foreach (var name in names.Take(1))
                foreach (bool bySkip in new[] { true, false })
                    foreach (double left in new[] { 2.0, 2.5, 3.0 })
                        for (int rep = 0; rep < 2; rep++)
                        {
                            string nm = name; bool sk = bySkip; double lf = left;
                            runs.Add(next => HuntOpen(nm, () =>
                            {
                                if (holding || holdTestPressed) press(false);
                                if (!playing) TogglePlay();
                                SetSpeed(1);
                                double dur = shown.Video.NaturalDuration.TimeSpan.TotalSeconds;
                                if (sk) SeekTo(dur - lf - skipForward); else SeekTo(dur - lf - 4);
                                After(900, () =>
                                {
                                    var p = soundPlayer;
                                    if (p == null) { HuntLog(nm + ": no own sound FAIL"); next(); return; }
                                    var tap = new float[48000 * 8];
                                    p.TapAt[0] = 0; p.Tap = tap;
                                    loopTapAt = new List<int>();
                                    After(400, () =>
                                    {
                                        int jumpAt = p.TapAt[0];
                                        if (sk) SeekBy(skipForward); else SeekTo(dur - lf);
                                        press(true); // (hold for 2x at once)
                                        After((int)(lf / 2 * 1000) + 1500, () =>
                                        {
                                            press(false);
                                            After(300, () =>
                                            {
                                                p.Tap = null;
                                                var x = tap.Take(Math.Min(tap.Length, p.TapAt[0])).ToArray();
                                                var at = loopTapAt.ToList(); loopTapAt = null;
                                                double b1, b2 = 0, dJump = DoubledMs(x, jumpAt, out b1), sJump = SilentMs(x, jumpAt);
                                                double dLoop = at.Count > 0 ? DoubledMs(x, at[0], out b2) : -1, sLoop = at.Count > 0 ? SilentMs(x, at[0]) : -1; if (at.Count == 0) b2 = 0;
                                                bool ok = at.Count > 0 && dJump == 0 && dLoop == 0 && sLoop < 5;
                                                total++; if (!ok) bad++;
                                                HuntLog(nm + ", " + (sk ? "Skip forward" : "jump") + " to " + lf + " s before the end, hold at once: around the jump doubled " + dJump + " ms (closest " + b1.ToString("0.00") + "), silence " + sJump.ToString("0") + " ms; loops " + at.Count + ", around the loop doubled " + dLoop + " ms (closest " + b2.ToString("0.00") + "), silence " + sLoop.ToString("0") + " ms" + (ok ? "" : " FAIL"));
                                                next();
                                            });
                                        });
                                    });
                                });
                            }));
                        }
            int i = 0;
            Action go = null;
            go = () => { if (i >= runs.Count) { HuntLog("skiphold: " + (total - bad) + " of " + total + " runs clean"); HuntDone(); return; } runs[i++](() => Dispatcher.BeginInvoke(go)); };
            After(500, go);
        }

        // ---- 1.31.1: --case resizefast --front. A bright magenta window right behind FastViewer's (and its video window), then
        // fast resizes, moves, maximize, full screen and the mini player while another thread takes pictures of the screen:
        // magenta inside the window = a gap where the video window lagged. Also reads the shadow just outside the edges.
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT131 r);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr131(IntPtr h, int i);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRgnBox")] static extern int GetWindowRgnBoxRaw(IntPtr h, out RECT131 r);
        static string GetWindowRgnBox131(IntPtr h) { RECT131 r; int t = GetWindowRgnBoxRaw(h, out r); return t == 0 ? "none" : r.Left + "," + r.Top + "-" + r.Right + "," + r.Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct RECT131 { public int Left, Top, Right, Bottom; }
        volatile bool rfRun;
        System.Drawing.Rectangle rfCover = System.Drawing.Rectangle.Empty; // (screen pixels the window covers all the time in this series)
        readonly object rfGate = new object();
        List<string> rfStats = new List<string>();
        int rfShots, rfGapShots, rfWorst; System.Drawing.Bitmap rfWorstBmp;

        static int Magenta(System.Drawing.Bitmap b, System.Drawing.Rectangle r)
        {
            r.Intersect(new System.Drawing.Rectangle(0, 0, b.Width, b.Height));
            if (r.Width <= 0 || r.Height <= 0) return 0;
            var d = b.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[r.Width * 4];
                int n = 0;
                for (int y = 0; y < r.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                    for (int x = 0; x < row.Length; x += 4) if (row[x] > 200 && row[x + 1] < 60 && row[x + 2] > 200) n++;
                }
                return n;
            }
            finally { b.UnlockBits(d); }
        }

        void HuntResizeFast()
        {
            if (!testInFront) { HuntLog("resizefast needs --front (it looks at the real screen)"); HuntDone(); return; }
            var video = files.FirstOrDefault(f => IsVideoFile(f) && !IsAudioFile(f));
            var photo = files.FirstOrDefault(f => !IsVideoFile(f) && !IsAudioFile(f));
            var hw = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var dev = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            int X0 = 300, Y0 = 160, W0 = 1000, H0 = 620;
            var area = new System.Drawing.Rectangle(X0 - 120, Y0 - 100, W0 + 600, H0 + 380); // (what the pictures cover)
            var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)),
                Left = (area.Left - 60) / dev.M11, Top = (area.Top - 60) / dev.M22, Width = (area.Width + 120) / dev.M11, Height = (area.Height + 120) / dev.M22 };
            Action under = () =>
            {
                var bh = new System.Windows.Interop.WindowInteropHelper(backdrop).Handle;
                var vb = VideoBack.For(this).Handle;
                SetWindowPos(bh, vb != IntPtr.Zero && VideoBack.For(this).Up ? vb : hw, 0, 0, 0, 0, 1 | 2 | 0x10);
            };
            Func<System.Drawing.Bitmap> shot = () =>
            {
                var b = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(b)) g.CopyFromScreen(area.Left, area.Top, 0, 0, b.Size);
                return b;
            };
            Func<System.Drawing.Rectangle> winRect = () => { RECT131 r; GetWindowRect(hw, out r); return new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); };
            // The shadow: pixels 1..24 outside the right edge and the bottom edge, in the middle (magenta = no shadow).
            Action<string> shadow = name =>
            {
                using (var b = shot())
                {
                    var w = winRect();
                    Func<int, int, string> px = (sx, sy) => { int x = sx - area.Left, y = sy - area.Top; if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return "-"; var c = b.GetPixel(x, y); return c.R + "," + c.G + "," + c.B; };
                    int my = w.Top + w.Height / 2, mx = w.Left + w.Width / 2;
                    HuntLog(name + ": shadow right of the window (1,3,6,10,16,24 px): " + string.Join(" | ", new[] { 1, 3, 6, 10, 16, 24 }.Select(o => px(w.Right - 1 + o, my))) + "; below: " + string.Join(" | ", new[] { 1, 3, 6, 10, 16, 24 }.Select(o => px(mx, w.Bottom - 1 + o))) + "; above: " + string.Join(" | ", new[] { 1, 3, 6, 10 }.Select(o => px(mx, w.Top - o))) + "; inside the right edge (1,2,3 px) " + px(w.Right - 1, my) + " | " + px(w.Right - 2, my) + " | " + px(w.Right - 3, my) + "; style " + ((long)GetWindowLongPtr131(hw, -16)).ToString("X") + ", region " + GetWindowRgnBox131(hw) + "; magenta inside the window " + Magenta(b, new System.Drawing.Rectangle(w.Left - area.Left + 1, w.Top - area.Top + 1, w.Width - 2, w.Height - 2)));
                    b.Save(Path.Combine(snapDir, "shadow_" + name + ".png"));
                    HuntLog("  " + VideoBack.For(this).Describe() + "; " + (shown != null && shown.IsVideo ? shown.Video.Debug() : "photo") + "; mini " + mini + ", z " + (shown != null ? System.Windows.Controls.Panel.GetZIndex(shown.Host) : -1) + ", opacity " + (shown != null ? shown.Host.Opacity : -1));
                }
            };
            // A series: the picture thread counts magenta inside rfCover (and inside the window's rectangle at that moment).
            var shooter = new Thread(() =>
            {
                while (true)
                {
                    if (!rfRun) { Thread.Sleep(5); continue; }
                    System.Drawing.Rectangle cover; lock (rfGate) cover = rfCover;
                    var w1 = winRect();
                    var b = shot();
                    var w2 = winRect();
                    var now = System.Drawing.Rectangle.Intersect(w1, w2); now.Inflate(-2, -2);
                    if (!cover.IsEmpty) { cover = System.Drawing.Rectangle.Intersect(cover, now); }
                    else cover = now;
                    cover.Offset(-area.Left, -area.Top);
                    int n = Magenta(b, cover);
                    lock (rfGate)
                    {
                        rfShots++;
                        if (n > 0) rfGapShots++;
                        if (n > rfWorst) { rfWorst = n; if (rfWorstBmp != null) rfWorstBmp.Dispose(); rfWorstBmp = b; b = null; }
                    }
                    if (b != null) b.Dispose();
                }
            }) { IsBackground = true, Name = "shots" };
            shooter.Start();
            Action<string> begin = name => { lock (rfGate) { rfShots = rfGapShots = rfWorst = 0; if (rfWorstBmp != null) rfWorstBmp.Dispose(); rfWorstBmp = null; } rfRun = true; };
            Action<string> end = name =>
            {
                rfRun = false;
                Thread.Sleep(60);
                lock (rfGate)
                {
                    HuntLog(name + ": " + rfShots + " pictures, " + rfGapShots + " with the backdrop showing inside the window, most " + rfWorst + " pixels");
                    rfStats.Add(name + " " + rfGapShots + "/" + rfShots + " (most " + rfWorst + " px)");
                    if (rfWorstBmp != null) { rfWorstBmp.Save(Path.Combine(snapDir, "gap_" + name + ".png")); rfWorstBmp.Dispose(); rfWorstBmp = null; }
                }
            };
            // Size changes, one per screen frame (a very fast drag), for ms milliseconds.
            Action<string, Func<int, System.Drawing.Rectangle>, System.Drawing.Rectangle, int, Action> drag = (name, at, cover, ms, then) =>
            {
                lock (rfGate) rfCover = cover;
                begin(name);
                int k = 0; var sw = Stopwatch.StartNew();
                EventHandler tick = null;
                tick = (s, e) =>
                {
                    if (sw.ElapsedMilliseconds > ms)
                    {
                        CompositionTarget.Rendering -= tick;
                        After(300, () => { end(name); lock (rfGate) rfCover = System.Drawing.Rectangle.Empty; then(); });
                        return;
                    }
                    var r = at(k++);
                    SetWindowPos(hw, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, 4 | 0x10);
                };
                CompositionTarget.Rendering += tick;
            };
            Func<int, int> tri = k => { int p = k % 32; return p < 16 ? p : 32 - p; }; // 0..16..0
            Action<string, Action, Action> change = (name, act, then) =>
            {
                lock (rfGate) rfCover = System.Drawing.Rectangle.Empty;
                begin(name); act(); After(700, () => { end(name); then(); });
            };
            var plan = new List<Action<Action>>
            {
                next => { Topmost = true; SetWindowPos(hw, IntPtr.Zero, X0, Y0, W0, H0, 4 | 0x10); backdrop.Show(); After(400, () => { under(); next(); }); },
                next => { if (photo != null) HuntOpen(Path.GetFileName(photo), () => After(600, () => { under(); shadow("photo"); RealActivate(true); After(500, () => { shadow("photo_active"); RealActivate(false); After(300, next); }); })); else next(); },
                next => HuntOpen(Path.GetFileName(video), () => { if (!playing) TogglePlay(); After(1200, () => { under(); next(); }); }),
                next => { shadow("video"); next(); },
                next => { RealActivate(true); After(500, () => { shadow("video_active"); RealActivate(false); After(300, next); }); },
                next => drag("grow_shrink", k => new System.Drawing.Rectangle(X0, Y0, W0 + tri(k) * 30, H0 + tri(k) * 20), new System.Drawing.Rectangle(X0, Y0, W0, H0), 2500, next),
                next => drag("left_top_edge", k => new System.Drawing.Rectangle(X0 + 480 - tri(k) * 30, Y0 + 320 - tri(k) * 20, W0 - 480 + tri(k) * 30, H0 - 320 + tri(k) * 20), new System.Drawing.Rectangle(X0 + 480, Y0 + 320, W0 - 480, H0 - 320), 2500, next),
                next => drag("move", k => new System.Drawing.Rectangle(X0 + tri(k) * 25, Y0 + tri(k) * 12, W0 - 400, H0 - 250), new System.Drawing.Rectangle(X0 + 400, Y0 + 192, W0 - 800, H0 - 442), 2500, next),
                next => { SetWindowPos(hw, IntPtr.Zero, X0, Y0, W0, H0, 4 | 0x10); After(500, () => { shadow("video_after"); next(); }); },
                next => change("maximize", () => WindowState = WindowState.Maximized, next),
                next => change("restore", () => WindowState = WindowState.Normal, next),
                next => change("fullscreen_in", () => ToggleFullscreen(), next),
                next => change("fullscreen_out", () => ToggleFullscreen(), next),
                next => change("mini_in", () => ToggleMini(), next),
                next => change("mini_out", () => { ToggleMini(); Topmost = true; under(); }, next), // (leaving the mini player drops "on top": the backdrop would cover the window)
                next => { After(500, () => { shadow("video_end"); next(); }); },
            };
            int i = 0;
            Action go = null;
            go = () =>
            {
                if (i >= plan.Count) { backdrop.Close(); Topmost = false; HuntLog("summary: " + string.Join("; ", rfStats)); HuntDone(); return; }
                plan[i++](() => Dispatcher.BeginInvoke(go));
            };
            go();
        }
    }
}
