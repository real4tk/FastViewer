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

        // GPU use of this process per engine kind (3D, VideoDecode, Copy...), from Windows' own counters.
        static List<KeyValuePair<string, PerformanceCounter>> GpuCounters()
        {
            var list = new List<KeyValuePair<string, PerformanceCounter>>();
            try
            {
                string me = "pid_" + Process.GetCurrentProcess().Id + "_";
                foreach (var inst in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                    if (inst.Contains(me))
                    {
                        int e = inst.IndexOf("engtype_");
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                        pc.NextValue();
                        list.Add(new KeyValuePair<string, PerformanceCounter>(e >= 0 ? inst.Substring(e + 8) : inst, pc));
                    }
            }
            catch { }
            return list;
        }

        static string GpuRead(List<KeyValuePair<string, PerformanceCounter>> list)
        {
            var sums = new Dictionary<string, double>();
            foreach (var kv in list)
            {
                double v = 0;
                try { v = kv.Value.NextValue(); } catch { }
                double old;
                sums.TryGetValue(kv.Key, out old);
                sums[kv.Key] = old + v;
            }
            return string.Join(", ", sums.Where(kv => kv.Value >= 0.05).OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%"));
        }

        // What is seen right now, on the sound's clock (counted on across loops).
        double SeenClock(Slot s) { return frameVisible ? frameOnClock : s.Video.Position.TotalSeconds + soundLoopBase; }

        void HuntFrames()
        {
            muted = false;
            Loudness.On = false;
            loop = true;
            UpdateControls();
            VideoConverter.FrameReader.Tracing = true;
            HuntLog("pictures drawn by FastViewer: " + (ownFrames ? "ON" : "off (Windows' player, as 1.29)"));
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            var setCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            Action<bool> press = down =>
            {
                var a = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { 1 });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            var runs = new List<Action<Action>>();
            var summary = new List<string>();
            foreach (var name in names)
            {
                string nm = name;
                bool onlySkip = huntCase == "skiphold", onlyLoops = huntCase == "frameloops"; // (--case skiphold: only part C; frameloops: only part A)
                // A. Steady loops at 1x, 2x (same pitch) and 1.5x (pitch with speed).
                foreach (var mode in onlySkip ? new string[0] : new[] { "1|k", "2|k", "1.5|p" })
                {
                    double spd = double.Parse(mode.Split('|')[0], CultureInfo.InvariantCulture);
                    bool keep = mode.EndsWith("k");
                    runs.Add(next => HuntOpen(nm, () =>
                    {
                        TimeStretch.KeepPitch = keep;
                        if (!playing) TogglePlay();
                        SetSpeed(spd);
                        After(900, () =>
                        {
                            double len = shown.Video.NaturalDuration.HasTimeSpan ? shown.Video.NaturalDuration.TimeSpan.TotalSeconds : 10;
                            int loops = len < 4 ? 6 : len < 10 ? 4 : 2;
                            double wait = ((len - shown.Video.Position.TotalSeconds) + loops * len) / spd + 0.8;
                            Record(nm + " loops at " + spd + "x " + (keep ? "same pitch" : "pitch with speed"), wait, null, summary, next);
                        });
                    }));
                }
                // B. Speed changes and holds while it plays at 1x, one of each just before the loop point.
                if (!onlySkip && !onlyLoops)
                runs.Add(next => HuntOpen(nm, () =>
                {
                    TimeStretch.KeepPitch = true;
                    if (!playing) TogglePlay();
                    SetSpeed(1);
                    After(1500, () =>
                    {
                        double len = shown.Video.NaturalDuration.HasTimeSpan ? shown.Video.NaturalDuration.TimeSpan.TotalSeconds : 10;
                        var plan = new List<KeyValuePair<double, Action>>();
                        double t = 0.6;
                        foreach (double sp in new[] { 2.0, 1.0, 1.5, 1.0, 2.0, 1.0 }) { double v = sp; plan.Add(new KeyValuePair<double, Action>(t, () => SetSpeed(v))); t += 1.4; }
                        plan.Add(new KeyValuePair<double, Action>(t, () => press(true))); // a hold: 2x after 0.3 s
                        plan.Add(new KeyValuePair<double, Action>(t + 1.5, () => press(false)));
                        t += 2.2;
                        // Right before the loop point: a change 0.4 s before the end, and a hold let go 0.3 s before it.
                        double at = shown.Video.Position.TotalSeconds, toEnd = len - (at + t) % len;
                        if (toEnd < 1.0) toEnd += len;
                        plan.Add(new KeyValuePair<double, Action>(t + toEnd - 0.4, () => SetSpeed(2)));
                        double t2 = t + toEnd - 0.4 + 0.5;
                        plan.Add(new KeyValuePair<double, Action>(t2 + 1.0, () => SetSpeed(1)));
                        double t3 = t2 + 1.6;
                        plan.Add(new KeyValuePair<double, Action>(t3, () => press(true)));
                        plan.Add(new KeyValuePair<double, Action>(t3 + 1.2, () => press(false)));
                        Record(nm + " speed changes and holds at 1x", t3 + 2.5, plan, summary, next);
                    });
                }));
                // C. Skipping to the last 2-3 seconds and holding for 2x straight away (the repeated sound of 1.29).
                foreach (var v in onlyLoops ? new double[0][] : new[] { new[] { 2.5, 0 }, new[] { 2.0, 0.15 }, new[] { 3.0, 0.3 }, new[] { 2.5, 0.5 }, new[] { 2.5, -0.001 }, new[] { 2.0, -0.15 }, new[] { 3.0, -0.3 } })
                {
                    double bk = v[0], late = Math.Abs(v[1]); bool bar = v[1] < 0; // (bar: a click on the time bar, as with the mouse)
                    runs.Add(next => HuntOpen(nm, () =>
                    {
                        TimeStretch.KeepPitch = true;
                        if (!playing) TogglePlay();
                        SetSpeed(1);
                        shown.Video.Position = TimeSpan.Zero; // (the skip below must go forward, as in use)
                        SetSpeed(1);
                        After(1200, () =>
                        {
                            double len = shown.Video.NaturalDuration.HasTimeSpan ? shown.Video.NaturalDuration.TimeSpan.TotalSeconds : 10;
                            if (len < bk + 1) { next(); return; }
                            var plan = new List<KeyValuePair<double, Action>>();
                            plan.Add(new KeyValuePair<double, Action>(0.3, () => { if (bar) { StartScrub(); ScrubTo(len - bk, true); EndScrub(); } else SeekBy(len - bk - shown.Video.Position.TotalSeconds); })); // (as the time bar / the skip keys do)
                            plan.Add(new KeyValuePair<double, Action>(0.3 + late, () => press(true)));
                            plan.Add(new KeyValuePair<double, Action>(0.3 + late + 3.2, () => press(false)));
                            Record(nm + " skip to " + bk + " s before the end, hold 2x " + (late < 0.01 ? "at once" : late + " s later") + (bar ? " (time bar)" : " (skip key)"), 5.0, plan, summary, next, true);
                        });
                    }));
                }
            }
            Action<int> go = null;
            go = i =>
            {
                if (i >= runs.Count)
                {
                    HuntLog("");
                    HuntLog("SUMMARY (" + (ownFrames ? "own pictures ON" : "Windows' player") + ")");
                    foreach (var l in summary) HuntLog(l);
                    HuntLog(HuntResources());
                    HuntDone();
                    return;
                }
                runs[i](() => After(300, () => go(i + 1)));
            };
            go(0);
        }

        // Records every screen frame for `seconds` while the plan's steps run at their times; then the numbers.
        readonly List<string> lateSnaps = new List<string>();
        double loudestPlayer; // (tests: the loudest Windows' player was set to while recording; must stay 0)

        void Record(string title, double seconds, List<KeyValuePair<double, Action>> plan, List<string> summary, Action next, bool repeats = false)
        {
            Step("record " + title);
            var s = shown;
            var p = soundPlayer;
            var trace = new List<double[]>();
            var changes = new List<long>();
            long t0 = Stopwatch.GetTimestamp();
            double freq = Stopwatch.Frequency;
            var me = Process.GetCurrentProcess();
            TimeSpan cpu0 = me.TotalProcessorTime;
            List<KeyValuePair<string, PerformanceCounter>> gpu = null;
            var gpuReady = new ManualResetEvent(false);
            ThreadPool.QueueUserWorkItem(delegate { gpu = GpuCounters(); gpuReady.Set(); });
            float[] tap = null;
            if (repeats && p != null) { tap = new float[(int)(48000 * (seconds + 2))]; p.TapAt[0] = 0; p.Tap = tap; }
            if (p != null) p.ResetStats();
            framesDrawn = 0;
            frameCopyWorstUs = 0;
            EventHandler onFrame = delegate
            {
                long now = Stopwatch.GetTimestamp();
                var sp = soundPlayer;
                double heard = double.NaN, at, pace, len;
                long stamp;
                if (sp != null && sp.Clock(s.File, out at, out stamp, out pace, out len)) heard = at + (now - stamp) / freq * pace;
                if (s.Video.Volume > loudestPlayer) loudestPlayer = s.Video.Volume;
                if (lateSnaps.Count < 2 && frameVisible && !double.IsNaN(heard) && heard - frameOnClock > 3 * s.FrameSeconds && frameReader != null) lock (frameReader.Recent) lateSnaps.Add(Ms((now - t0) / freq) + " ms, heard " + heard.ToString("0.000") + ", shown " + frameOnClock.ToString("0.000") + ": " + string.Join(" ", frameReader.Recent));
                bool ownHeard = sp != null && sp.Sounding && !double.IsNaN(heard);
                var fr = frameReader;
                trace.Add(new[] { (now - t0) / freq, SeenClock(s), frameVisible ? 1 : 0, s.Video.Position.TotalSeconds, heard, sp == null ? double.NaN : sp.LastDrift, speed, fr == null ? -1 : fr.Waiting, fr == null ? double.NaN : fr.NextClock, ownHeard ? heard : !s.Video.IsMuted ? s.Video.Position.TotalSeconds + soundLoopBase : double.NaN, (ownHeard ? 1 : 0) + (!s.Video.IsMuted ? 2 : 0), frameVisible ? frameOnFile : s.Video.Position.TotalSeconds });
            };
            CompositionTarget.Rendering += onFrame;
            if (plan != null)
                foreach (var kv in plan)
                {
                    var step = kv;
                    After((int)(step.Key * 1000), () => { changes.Add(Stopwatch.GetTimestamp()); step.Value(); });
                }
            After((int)(seconds * 1000), () =>
            {
                CompositionTarget.Rendering -= onFrame;
                double wall = (Stopwatch.GetTimestamp() - t0) / freq;
                double cpu = (me.TotalProcessorTime - cpu0).TotalSeconds / wall * 100;
                gpuReady.WaitOne(3000);
                string gpuUse = gpu == null ? "?" : GpuRead(gpu);
                if (p != null) p.Tap = null;
                double fs = s.FrameSeconds > 0 ? s.FrameSeconds : 1 / 30.0;
                var lines = new List<string>();
                // Stand-stills, per screen frame, beyond a normal frame (at the speed then):
                // - Windows' player on screen: how long its clock has stood still (its place unchanged over screen frames);
                // - own pictures on screen: how much older the picture shown is than it should be for the sound's clock
                //   (the next picture wasn't ready). Both don't count the hidden test window's late screen frames.
                var extra = new double[trace.Count];
                int i0 = 0;
                for (int i = 1; i <= trace.Count; i++)
                {
                    if (i < trace.Count && trace[i][2] == 0 && trace[i0][2] == 0 && Math.Abs(trace[i][3] - trace[i0][3]) < 1e-7) continue;
                    if (trace[i0][2] == 0 && i - i0 > 1)
                    {
                        double dur = (i < trace.Count ? trace[i][0] : trace[i - 1][0]) - trace[i0][0];
                        double e = Math.Max(0, dur - fs / Math.Max(0.25, trace[i0][6]));
                        for (int j = i0; j < i; j++) extra[j] = e;
                    }
                    i0 = i;
                }
                for (int i = 0; i < trace.Count; i++)
                    if (trace[i][2] > 0 && !double.IsNaN(trace[i][4]))
                        extra[i] = Math.Max(0, trace[i][4] + 0.012 * trace[i][6] - trace[i][1] - fs * 1.05) / Math.Max(0.25, trace[i][6]);
                int shownLate = 0;
                for (int i = 2; i < trace.Count - 2 && shownLate < 6; i++)
                    if (trace[i][2] > 0 && extra[i] > 0.02 && extra[i - 1] <= 0.02)
                    {
                        shownLate++;
                        var parts = new List<string>();
                        for (int j = i - 2; j <= Math.Min(trace.Count - 1, i + 3); j++) parts.Add(Ms(trace[j][0]) + ": heard " + trace[j][4].ToString("0.000", CultureInfo.InvariantCulture) + " shown " + trace[j][1].ToString("0.000", CultureInfo.InvariantCulture) + " waiting " + trace[j][7] + " next " + trace[j][8].ToString("0.000", CultureInfo.InvariantCulture));
                        HuntLog("    late picture: " + string.Join(" | ", parts));
                    }
                var stills = new List<double[]>();
                for (int i = 0; i < trace.Count; i++) stills.Add(new[] { trace[i][0], extra[i], 0 });
                // Loop points: Windows' player starting over underneath (it does in both ways).
                var loopsAt = new List<double>();
                for (int i = 1; i < trace.Count; i++) if (trace[i][11] < trace[i - 1][11] - 0.5) loopsAt.Add(trace[i][0]); // (what is seen starting over: drawn picture or Windows' player)
                Func<double, double, double> worst = (a, b) => stills.Where(x => x[0] >= a && x[0] <= b).Select(x => x[1]).DefaultIfEmpty(0).Max();
                var loopWorst = loopsAt.Select(t => worst(t - 1.0, t + 1.0)).ToList();
                var changeWorst = changes.Select(c => (c - t0) / freq).Select(t => worst(t, t + 1.0)).ToList();
                // Sound and picture: what is seen against what is heard (own pictures: the middle of the frame shown).
                var sync = new List<double>();
                for (int i = 0; i < trace.Count; i++)
                {
                    if (double.IsNaN(trace[i][4])) continue;
                    double seen = trace[i][2] > 0 ? trace[i][1] + fs * 0.5 : trace[i][1];
                    sync.Add(seen - trace[i][4]);
                }
                var absSync = sync.Select(Math.Abs).OrderBy(x => x).ToList();
                Func<List<double>, double, double> pct = (l, q) => l.Count == 0 ? double.NaN : l[Math.Min(l.Count - 1, (int)(q * l.Count))];
                double drawn = trace.Count == 0 ? 0 : trace.Count(x => x[2] > 0) * 100.0 / trace.Count;
                var allExtra = stills.Select(x => x[1]).OrderBy(x => x).ToList();
                lines.Add(title + ":");
                lines.Add("  screen frames " + trace.Count + " in " + wall.ToString("0.0", CultureInfo.InvariantCulture) + " s; drawn by FastViewer " + drawn.ToString("0", CultureInfo.InvariantCulture) + "% (" + framesDrawn + " pictures copied, slowest copy " + (frameCopyWorstUs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms)");
                lines.Add("  stand-still beyond a normal frame: at loop points [" + string.Join(" ", loopWorst.Select(Ms)) + "] ms; after speed changes/holds [" + string.Join(" ", changeWorst.Select(Ms)) + "] ms; anywhere: worst " + Ms(allExtra.DefaultIfEmpty(0).Last()) + " ms, over 20 ms " + allExtra.Count(x => x > 0.02) + " times");
                lines.Add("  seen vs heard: median " + Ms(pct(absSync, 0.5)) + " ms, 95% within " + Ms(pct(absSync, 0.95)) + " ms, worst " + Ms(absSync.DefaultIfEmpty(double.NaN).Last()) + " ms (" + absSync.Count + " frames with own sound)");
                var gaps = new List<double>();
                for (int i = 1; i < trace.Count; i++) gaps.Add(trace[i][0] - trace[i - 1][0]);
                gaps.Sort();
                lines.Add("  screen ticks: median " + Ms(pct(gaps, 0.5)) + " ms, 99% within " + Ms(pct(gaps, 0.99)) + " ms, longest " + Ms(gaps.DefaultIfEmpty(0).Last()) + " ms, over 25 ms " + gaps.Count(g => g > 0.025) + " times");
                lines.Add("  Windows' player volume at most " + loudestPlayer.ToString("0.00", CultureInfo.InvariantCulture) + (loudestPlayer > 0 ? " FAIL (audible)" : " (silent)") + "; FastViewer's own sound " + (p != null && testRun && !ownSoundAloud ? "silent" : "?"));
                lines.Add("  CPU " + cpu.ToString("0", CultureInfo.InvariantCulture) + "% of one core; GPU: " + gpuUse + "; memory: private " + (Process.GetCurrentProcess().PrivateMemorySize64 / 1048576) + " MB");
                if (p != null) lines.Add("  sound: restarts " + p.Resyncs + " [" + string.Join(" ", p.Notes.ToArray()) + "], speakers dry " + p.LongestStarve + " ms at most");
                var r = frameReader;
                if (r != null) lines.Add("  reader: opened in " + r.OpenMs + " ms, " + r.Width + "x" + r.Height + (r.Scaled ? " (scaled)" : "") + ", jumps " + r.Seeks + ", loops read ahead " + r.Wraps + " (slowest next-loop start " + r.LongestWrapMs + " ms), decoded " + r.Decoded + ", kept " + r.Kept + ", time shift " + Ms(r.Shift) + " ms, " + (r.OnGpu ? "pictures stayed on the graphics card (" + r.CardCopies + " copies there)" : "pictures through memory") + ", reopened " + r.Reopens);
                else if (ownFrames) lines.Add("  reader: none" + (frameBad.Contains(s.File) ? " (file not usable)" : ""));
                string head = title + ": loops " + string.Join("/", loopWorst.Select(Ms)) + " ms, changes " + string.Join("/", changeWorst.Select(Ms)) + " ms, sync p95 " + Ms(pct(absSync, 0.95)) + " ms, CPU " + cpu.ToString("0") + "%, drawn " + drawn.ToString("0") + "%";
                foreach (var l in lines) HuntLog(l);
                foreach (var l in lateSnaps) HuntLog("    late, decoded lately: " + l);
                lateSnaps.Clear();
                // What is heard (FastViewer's sound, or Windows' player when it isn't silenced) going back: a bit heard twice.
                var backs = new List<string>();
                double lastHp = double.NaN; int both = 0;
                foreach (var x in trace)
                {
                    if (x[10] == 3) both++;
                    if (double.IsNaN(x[9])) continue;
                    if (!double.IsNaN(lastHp) && x[9] < lastHp - 0.08) backs.Add(Ms(lastHp - x[9]) + " ms at " + x[0].ToString("0.00", CultureInfo.InvariantCulture) + " s (" + (x[10] == 2 ? "Windows' player" : "own sound") + ")");
                    lastHp = x[9];
                }
                HuntLog("  heard sound went back: " + backs.Count + (backs.Count > 0 ? " [" + string.Join("; ", backs.Take(6)) + "]" : "") + "; both players audible " + both + " screen frames");
                head += ", heard went back " + backs.Count + "x, both audible " + both;
                if (tap == null) { summary.Add(head); next(); return; }
                // Sound played twice: 20 ms blocks of the sound's changes that match anything 50-800 ms before them.
                var x0 = tap.Take(p.TapAt[0]).ToArray();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var rep = Repeated(x0, 2400, 38400);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        string rl = "  sound played twice: " + rep.Count + " blocks of 20 ms" + (rep.Count > 0 ? " (at " + string.Join(", ", rep.Take(8).Select(v => v.ToString("0.00", CultureInfo.InvariantCulture) + " s")) + ")" : "");
                        HuntLog(rl);
                        summary.Add(head + ", repeated " + rep.Count * 20 + " ms");
                        next();
                    }));
                });
            });
        }

        // --case clicks (1.30.0 round 2): what goes to the speakers, checked for clicks, clipping and running dry.
        // Tone clips (t*.mp4: every track a 440 Hz sine, so the mix is one clean sine) show every click exactly: the
        // next sample of a sine follows from the two before it (x[n] = 2cos(w)x[n-1] - x[n-2]); anything else is a jump.
        void HuntClicks()
        {
            muted = false;
            loop = true;
            UpdateControls();
            HuntLog("pictures drawn by FastViewer: " + (ownFrames ? "ON" : "off"));
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
                if (i >= runs.Count) { HuntLog(""); HuntLog("SUMMARY (" + (ownFrames ? "own pictures ON" : "Windows' player") + ")"); foreach (var l in summary) HuntLog(l); HuntLog(HuntResources()); HuntDone(); return; }
                runs[i](() => After(300, () => go(i + 1)));
            };
            go(0);
        }

        // --case usage (1.30.0 round 2): what Task Manager shows while one video plays at 1x with Repeat: memory (private
        // working set), processor, and the graphics card's video decoding and 3D, averaged over 8 s after 3 s of playing.
        void HuntUsage()
        {
            muted = false;
            loop = true;
            UpdateControls();
            HuntLog("pictures drawn by FastViewer: " + (ownFrames ? "ON" : "off"));
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            var me = Process.GetCurrentProcess();
            PerformanceCounter priv = null;
            // (By process id: the user's own FastViewer has the same name.)
            try { foreach (var inst in new PerformanceCounterCategory("Process").GetInstanceNames()) if (inst.StartsWith(me.ProcessName)) using (var idc = new PerformanceCounter("Process", "ID Process", inst, true)) if ((int)idc.RawValue == me.Id) { priv = new PerformanceCounter("Process", "Working Set - Private", inst, true); break; } } catch { }
            int i = 0;
            Action one = null;
            one = () =>
            {
                if (i >= names.Count) { HuntLog(HuntResources()); HuntDone(); return; }
                string nm = names[i++];
                HuntOpen(nm, () =>
                {
                    if (!playing) TogglePlay();
                    SetSpeed(1);
                    List<KeyValuePair<string, PerformanceCounter>> gpu = null;
                    ThreadPool.QueueUserWorkItem(delegate { gpu = GpuCounters(); });
                    After(3000, () =>
                    {
                        me.Refresh();
                        var cpu0 = me.TotalProcessorTime; var t0 = Stopwatch.StartNew();
                        if (gpu != null) GpuRead(gpu);
                        var mem = new List<double>();
                        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        tick.Tick += delegate { try { mem.Add(priv != null ? priv.NextValue() / 1048576.0 : 0); } catch { } };
                        tick.Start();
                        After(8000, () =>
                        {
                            tick.Stop();
                            me.Refresh();
                            double cpu = (me.TotalProcessorTime - cpu0).TotalSeconds / t0.Elapsed.TotalSeconds * 100;
                            HuntLog(nm + ": memory (Task Manager) " + (mem.Count > 0 ? mem.Average().ToString("0") : "?") + " MB (max " + (mem.Count > 0 ? mem.Max().ToString("0") : "?") + "), private " + (me.PrivateMemorySize64 / 1048576) + " MB, CPU " + cpu.ToString("0") + "% of one core, GPU: " + (gpu == null ? "?" : GpuRead(gpu)) + (frameParked ? ", Windows' player parked" : ""));
                            one();
                        });
                    });
                });
            };
            one();
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
                            HuntLog("  " + (ticks * 100) + " ms: playing " + playing + ", poster " + poster + grey + ", drawn picture " + frameVisible + ", parked " + frameParked + ", player " + s.Video.Visibility + ", controls " + overlayVisible + ", scrim " + scrim.Visibility + " " + scrim.Opacity.ToString("0.00"));
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

        // --case colours (1.30.0 round 3): the same frame as FastViewer draws it and as Windows' player shows it, compared
        // pixel by pixel: black and white points (1st/99th percentile of brightness), a straight-line fit per channel
        // (drawn = a x player + b: a washed-out picture has a < 1 and b > 0), and the average difference. Pictures of both.
        static byte[] Pixels(BitmapSource b, out int w, out int h)
        {
            var f = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            w = f.PixelWidth; h = f.PixelHeight;
            var px = new byte[w * h * 4];
            f.CopyPixels(px, w * 4, 0);
            return px;
        }

        static void SavePng(BitmapSource b, string file)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        internal static string CompareColours(BitmapSource drawn, BitmapSource player)
        {
            int w1, h1, w2, h2;
            var a = Pixels(drawn, out w1, out h1);
            var b = Pixels(player, out w2, out h2);
            if (w1 != w2 || h1 != h2) return "sizes differ: " + w1 + "x" + h1 + " vs " + w2 + "x" + h2;
            var lines = new List<string>();
            double diff = 0; long n = 0;
            var la = new List<double>(); var lb = new List<double>();
            for (int c = 0; c < 3; c++)
            {
                double sx = 0, sy = 0, sxx = 0, sxy = 0; long m = 0;
                for (int i = c; i < a.Length; i += 4 * 7) { double x = b[i], y = a[i]; sx += x; sy += y; sxx += x * x; sxy += x * y; m++; diff += Math.Abs(x - y); n++; }
                double slope = (m * sxy - sx * sy) / Math.Max(1e-9, m * sxx - sx * sx), icept = (sy - slope * sx) / m;
                lines.Add(new[] { "blue", "green", "red" }[c] + ": drawn = " + slope.ToString("0.000", CultureInfo.InvariantCulture) + " x player + " + icept.ToString("0.0", CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < a.Length; i += 4 * 7) { la.Add(0.0722 * a[i] + 0.7152 * a[i + 1] + 0.2126 * a[i + 2]); lb.Add(0.0722 * b[i] + 0.7152 * b[i + 1] + 0.2126 * b[i + 2]); }
            la.Sort(); lb.Sort();
            Func<List<double>, double, string> q = (l, p) => l[Math.Min(l.Count - 1, (int)(p * l.Count))].ToString("0", CultureInfo.InvariantCulture);
            return string.Join("; ", lines) + "; brightness 1%/50%/99%: drawn " + q(la, 0.01) + "/" + q(la, 0.5) + "/" + q(la, 0.99) + ", player " + q(lb, 0.01) + "/" + q(lb, 0.5) + "/" + q(lb, 0.99) +
                "; average difference " + (diff / n).ToString("0.0", CultureInfo.InvariantCulture) + " of 255";
        }

        // Brightness 1%/50%/99% of the middle of the window as it was drawn on screen (from a picture of the window).
        string ScreenStats(string png)
        {
            try
            {
                var dec = new PngBitmapDecoder(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                int w, h;
                var px = Pixels(dec.Frames[0], out w, out h);
                var l = new List<double>();
                for (int y = h * 3 / 10; y < h * 7 / 10; y += 3)
                    for (int x = w * 3 / 10; x < w * 7 / 10; x += 3) { int i = (y * w + x) * 4; l.Add(0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]); }
                l.Sort();
                Func<double, string> q = p => l[Math.Min(l.Count - 1, (int)(p * l.Count))].ToString("0", CultureInfo.InvariantCulture);
                return q(0.01) + "/" + q(0.5) + "/" + q(0.99);
            }
            catch (Exception ex) { return "? " + ex.Message; }
        }

        void HuntColours()
        {
            muted = false;
            loop = true;
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            int i = 0;
            Action one = null;
            one = () =>
            {
                if (i >= names.Count) { HuntDone(); return; }
                string nm = names[i++];
                HuntOpen(nm, () =>
                {
                    if (!playing) TogglePlay();
                    After(2500, () =>
                    {
                        var s = shown;
                        if (!frameVisible) { HuntLog(nm + ": the drawn picture isn't up (not compared)"); one(); return; }
                        string tag0 = nm.Replace(".", "_");
                        Snap(tag0 + "_screen_drawn");
                        TogglePlay(); // pause: the drawn picture stays while Windows' player is put on the same frame
                        var drawn = DrawnPicture();
                        HuntLog(nm + ": shared texture format " + sharedFmt + ", its 4th byte averages " + DrawnAlpha().ToString("0") + " (255 = opaque)");
                        double at = frameOnFile;
                        After(1800, () =>
                        {
                            var player = Grab(s.Video, drawn.PixelWidth, drawn.PixelHeight);
                            Snap(tag0 + "_screen_player");
                            HuntLog(nm + ": on screen (middle of the window) 1%/50%/99%: drawn picture " + ScreenStats(Path.Combine(snapDir, tag0 + "_screen_drawn.png")) + ", Windows' player " + ScreenStats(Path.Combine(snapDir, tag0 + "_screen_player.png")));
                            string tag = nm.Replace(".", "_");
                            try { SavePng(drawn, Path.Combine(snapDir, tag + "_drawn.png")); SavePng(player, Path.Combine(snapDir, tag + "_player.png")); } catch { }
                            HuntLog(nm + " at " + at.ToString("0.000", CultureInfo.InvariantCulture) + " s (player at " + s.Video.Position.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "), " + drawn.PixelWidth + "x" + drawn.PixelHeight + ": " + CompareColours(drawn, player));
                            After(300, one);
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
                                    ", playing " + playing + ", drawn " + frameVisible + ", parked " + frameParked + ", sound " + sound;
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
                    if (frameParked) Unpark(true);
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

        // --case fps (1.30.0 round 3): each video plays 7 s at 1x with Repeat and the playback stats on; their lines logged.
        void HuntFps()
        {
            muted = false;
            loop = true;
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => huntPattern.Split(',').Any(x => x.Equals(nn, StringComparison.OrdinalIgnoreCase))).ToList();
            int i = 0;
            Action one = null;
            one = () =>
            {
                if (i >= names.Count) { if (statsOn) ToggleStats(); HuntDone(); return; }
                string nm = names[i++];
                HuntOpen(nm, () =>
                {
                    if (!playing) TogglePlay();
                    SetSpeed(1);
                    After(1500, () =>
                    {
                        if (!statsOn) ToggleStats();
                        for (int k = 1; k <= 6; k++) { int kk = k; After(kk * 1000 + 50, () => HuntLog(nm + " s" + kk + ": " + StatsLine)); }
                        After(1200, () => { var p = statsBox.TransformToAncestor(root).Transform(new Point(0, 0)); HuntLog(nm + " stats box: " + statsBox.Visibility + ", " + statsBox.ActualWidth.ToString("0") + "x" + statsBox.ActualHeight.ToString("0") + " at " + p.X.ToString("0") + "," + p.Y.ToString("0") + " in a " + root.ActualWidth.ToString("0") + "x" + root.ActualHeight.ToString("0") + " window, drawn picture " + frameVisible); Snap("stats_" + nm.Replace(".", "_")); }); // (1.30.0 round 4: the box itself shows)
                        After(6500, one);
                    });
                });
            };
            one();
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
