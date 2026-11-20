// FastViewer 1.29.0 tests: the pitch at other speeds (measured on a 1 kHz tone), the moment a 2x hold is let go,
// and "Keep speed" saves. All silent: the sound is measured before it would reach the speakers (SoundPlayer.Tap),
// and the test's own Windows volume is 0 anyway (TestSilence.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        // Frequency of a tone by its rising zero crossings (between samples, straight lines), and how much of the sound
        // is that tone (1 = a clean tone; clicks, warble and doubled bits bring it down).
        static void ToneOf(float[] x, int from, int count, out double hz, out double clean)
        {
            hz = 0; clean = 0;
            var cross = new List<double>();
            for (int i = from + 1; i < from + count && i < x.Length; i++)
                if (x[i - 1] < 0 && x[i] >= 0) cross.Add(i - 1 + x[i - 1] / (x[i - 1] - x[i]));
            if (cross.Count < 3) return;
            hz = (cross.Count - 1) * 48000.0 / (cross[cross.Count - 1] - cross[0]);
            // Least-squares fit of a sine at that frequency; what's left over isn't the tone.
            int a = (int)Math.Ceiling(cross[0]), b = (int)Math.Floor(cross[cross.Count - 1]);
            double w = 2 * Math.PI * hz / 48000, ss = 0, sc = 0, cc = 0, xs = 0, xc = 0, xx = 0;
            for (int i = a; i <= b; i++) { double s = Math.Sin(w * i), c = Math.Cos(w * i); ss += s * s; sc += s * c; cc += c * c; xs += x[i] * s; xc += x[i] * c; xx += x[i] * x[i]; }
            double det = ss * cc - sc * sc;
            if (det <= 0 || xx <= 0) return;
            double p = (xs * cc - xc * sc) / det, q = (xc * ss - xs * sc) / det, fit = p * xs + q * xc;
            clean = fit / xx;
        }

        // The first seconds of a file's first sound track, left channel, 48 kHz.
        static float[] DecodeLeft(string file, double seconds)
        {
            var layout = SoundMixing.Layout(file);
            if (layout.Streams == null || layout.Streams.Length == 0) return new float[0];
            var reader = VideoConverter.Reading.Open(file);
            if (reader == null) return new float[0];
            var list = new List<float>();
            try
            {
                VideoConverter.Reading.DeselectAll(reader);
                if (!VideoConverter.Reading.Select(reader, layout.Streams[0])) return new float[0];
                var mixer = new TrackMixer();
                mixer.AddTrack(1);
                while (list.Count < seconds * 48000)
                {
                    int stream; long time; IMFSample sample; bool ended, changed;
                    if (!VideoConverter.Reading.Next(reader, out stream, out time, out sample, out ended, out changed)) break;
                    if (sample != null) { try { mixer.Add(0, time, sample); } finally { Marshal.ReleaseComObject(sample); } }
                    if (ended) mixer.End(0);
                    long at; short[] piece;
                    while ((piece = mixer.Take(mixer.AllEnded, out at, 48000)) != null) for (int i = 0; i < piece.Length; i += 2) list.Add(piece[i]);
                    if (mixer.AllEnded) break;
                }
            }
            finally { Marshal.ReleaseComObject(reader); }
            return list.ToArray();
        }

        // --case pitch (folder with tone.mp4: a 1 kHz tone; and a Medal clip, r1.mp4, if there): pitch at each speed with
        // the setting off and on; then the let-go of a 2x hold, tone.mp4 and the Medal clip; then Keep-speed saves.
        bool traced, tracedGo;
        void HuntPitch()
        {
            muted = false;
            Loudness.On = false; // (the level doesn't matter here)
            var setCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            Action<bool> press = down =>
            {
                var a = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { 1 });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            Action<Action<float[]>> listen = done =>
            {
                var p = soundPlayer;
                if (p == null) { done(new float[0]); return; }
                var tap = new float[48000];
                p.TapAt[0] = 0;
                p.Tap = tap;
                After(700, () => { p.Tap = null; done(tap.Take(p.TapAt[0]).ToArray()); });
            };
            var steps = new List<Action<Action>>();
            // A. The tone at each speed, both ways.
            foreach (bool keep in new[] { true, false })
                foreach (double sp in new[] { 2, 1.5, 1.25, 0.75, 0.5, 3 })
                {
                    bool k = keep; double speedNow = sp;
                    steps.Add(next =>
                    {
                        TimeStretch.KeepPitch = k;
                        SetSpeed(speedNow);
                        After(1800, () => listen(x =>
                        {
                            double hz, clean;
                            ToneOf(x, 2000, x.Length - 2000, out hz, out clean);
                            double want = k ? 1000 : 1000 * speedNow;
                            HuntLog("  " + (k ? "same pitch" : "pitch with speed") + " at " + speedNow + "x: " + F1(hz) + " Hz (should be " + F1(want) + "), " + F1(clean * 100) + "% clean tone, " +
                                (soundPlayer != null ? soundPlayer.Resyncs + " restarts" : "") + (Math.Abs(hz - want) < want * 0.005 && clean > (k ? 0.98 : 0.85) ? "" : " FAIL")); // (the old way: straight lines between samples, as before 1.29)
                            next();
                        }));
                    });
                }
            // B. Letting go of a hold: the pace the sound is read at, every 20 ms, and how far it is from the picture.
            Action<string, Action> letGo = (name, next) =>
            {
                if (!files.Any(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))) { next(); return; }
                TimeStretch.KeepPitch = true;
                SetSpeed(1);
                HuntOpen(name, () =>
                {
                    shown.Video.Position = TimeSpan.FromSeconds(2);
                    if (!playing) TogglePlay();
                    After(3000, () =>
                    {
                        HuntLog("let go, " + name + ":");
                        bool tr = name == "r1.mp4" && !traced; if (tr) traced = true;
                        if (tr) { speedTrace = new List<string>(); speedTraceFrom = System.Diagnostics.Stopwatch.GetTimestamp(); After(1300, () => { HuntLog("  trace from the press: " + string.Join(" ", speedTrace)); speedTrace = null; }); }
                        press(true);
                        After(2500, () =>
                        {
                            var p = soundPlayer;
                            int catchBefore = pictureCatchUps;
                            HuntLog("  2.5 s into the hold: speed " + speed + "x, sound and picture " + (p == null ? 0 : p.LastDrift * 1000).ToString("0") + " ms apart, picture moved forward " + pictureCatchUps + "x so far; picture stood " + (lastStall * 1000).ToString("0") + " ms at the last speed change, sound timed " + (lastTimedDelay * 1000).ToString("0") + " ms apart, timed changes " + timedChanges);
                            double min2 = 9, max2 = 0, minStep = 9, maxStep = 0, worst = 0, settled = -1, lastPos = -1, stillMs = 0, worstStill = 0;
                            int quietTicks = 0, ticks = 0;
                            var trace = new List<string>();
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            float[] heardAfter = null;
                            var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
                            long last = 0;
                            t.Tick += delegate
                            {
                                long ms = clock.ElapsedMilliseconds, dt = ms - last; last = ms;
                                ticks++;
                                if (p == null) return;
                                double st = p.LastStep, dr = p.LastDrift;
                                if (ms > 150 && ms < 900) { minStep = Math.Min(minStep, st); maxStep = Math.Max(maxStep, st); } // (the first 0.15 s: what was already queued at 2x)
                                else if (ms >= 900) { min2 = Math.Min(min2, st); max2 = Math.Max(max2, st); }
                                worst = Math.Max(worst, Math.Abs(dr));
                                if (Math.Abs(dr) <= 0.04) { if (settled < 0) settled = ms; } else settled = -1;
                                if (!p.Sounding && (OwnSound(shown) || shown.Video.IsMuted)) quietTicks++;
                                double pos = shown.Video.Position.TotalSeconds;
                                if (lastPos >= 0 && Math.Abs(pos - lastPos) < 0.0005) { stillMs += dt; worstStill = Math.Max(worstStill, stillMs); } else stillMs = 0;
                                lastPos = pos;
                                if (ms % 100 < 20 && trace.Count < 30) trace.Add(ms + ":" + F3(st) + "/" + (dr * 1000).ToString("0"));
                            };
                            if (name == "r1.mp4" && !tracedGo) { tracedGo = true; speedTrace = new List<string>(); speedTraceFrom = System.Diagnostics.Stopwatch.GetTimestamp(); After(1300, () => { HuntLog("  trace from letting go: " + string.Join(" ", speedTrace)); speedTrace = null; }); }
                            press(false);
                            t.Start();
                            After(200, () => listen(x => heardAfter = x));
                            After(3000, () =>
                            {
                                t.Stop();
                                double hz = 0, clean = 0;
                                if (heardAfter != null && heardAfter.Length > 4000 && name.StartsWith("tone")) ToneOf(heardAfter, 1000, heardAfter.Length - 1000, out hz, out clean);
                                HuntLog("  speed " + speed + "x; sound read at " + F3(minStep) + "-" + F3(maxStep) + " x from 0.15 to 0.9 s after letting go (must be 1.000), then " + F3(min2) + "-" + F3(max2) + " x (same pitch, at most 3% to close what is left), " +
                                    "sound and picture apart at most " + (worst * 1000).ToString("0") + " ms, within 40 ms from " + (settled < 0 ? "never" : settled + " ms") +
                                    ", picture moved forward " + (pictureCatchUps - catchBefore) + "x (last +" + (lastCatchJump * 1000).ToString("0") + " ms, it then stood " + (lastSeekCost * 1000).ToString("0") + " ms), picture still at most " + worstStill + " ms, sound missing " + quietTicks + " of " + ticks + " ticks" +
                                    (name.StartsWith("tone") ? ", tone 0.2-0.9 s after: " + F1(hz) + " Hz " + F1(clean * 100) + "% clean" : "") +
                                    (minStep > 0.9995 && maxStep < 1.0005 && min2 > 0.969 && max2 < 1.031 && quietTicks == 0 && settled >= 0 && settled < 2500 ? "" : " FAIL"));
                                HuntLog("  pace/apart (ms) every 100 ms: " + string.Join(" ", trace));
                                next();
                            });
                        });
                    });
                });
            };
            steps.Add(next => letGo("tone.mp4", next));
            steps.Add(next => letGo("tone.mp4", next));
            steps.Add(next => letGo("r1.mp4", next));
            steps.Add(next => letGo("r1.mp4", next));
            steps.Add(next => letGo("r1.mp4", next));
            // C. Keep-speed saves: the tone in the copy.
            steps.Add(next =>
            {
                string tone = files.FirstOrDefault(f => Path.GetFileName(f).Equals("tone.mp4", StringComparison.OrdinalIgnoreCase));
                if (tone == null) { next(); return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var lines = new List<string>();
                    foreach (bool k in new[] { true, false })
                        foreach (double sp in new[] { 2, 0.5 })
                        {
                            Step("opened; setting up");
                            TimeStretch.KeepPitch = k;
                            string to = Path.Combine(snapDir, "saved " + sp + "x " + (k ? "same" : "pitch") + ".mp4");
                            try
                            {
                                VideoConverter.Convert(tone, to, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(9), 0, pr => { }, new CancelFlag(), new ConvertOptions { Speed = sp, MaxLong = 320 });
                                var x = DecodeLeft(to, 3);
                                double hz, clean;
                                ToneOf(x, 4800, x.Length - 4800, out hz, out clean);
                                double want = k ? 1000 : 1000 * sp, len = VideoConverter.Reading.Length(VideoConverter.Reading.Open(to));
                                lines.Add("  saved at " + sp + "x, " + (k ? "same pitch" : "pitch with speed") + ": " + F1(hz) + " Hz (should be " + F1(want) + "), " + F1(clean * 100) + "% clean, " + F3(len) + " s long (should be about " + F3(8 / sp) + ")" +
                                    (Math.Abs(hz - want) < want * 0.005 && clean > 0.98 && Math.Abs(len - 8 / sp) < 0.15 ? "" : " FAIL"));
                            }
                            catch (Exception e) { lines.Add("  saving at " + sp + "x failed: " + e.Message + " FAIL"); }
                        }
                    TimeStretch.KeepPitch = true;
                    Dispatcher.BeginInvoke(new Action(delegate { foreach (var l in lines) HuntLog(l); next(); }));
                });
            });
            int n = 0;
            Action run = null;
            run = () => { if (n < steps.Count) steps[n++](run); else HuntDone(); };
            HuntLog("tone.mp4 at each speed:");
            HuntOpen("tone.mp4", () => { shown.Video.Position = TimeSpan.Zero; if (!playing) TogglePlay(); After(1000, run); });
        }
    }
    partial class ViewerWindow
    {
        internal List<int> loopTapAt; // tests: where in the recorded sound each picture loop happened

        // Doubled sound: 10 ms blocks from 0.3 s before to 0.6 s after a loop point, each compared with everything 30-300 ms
        // before it (normalized correlation). Noise never matches itself; a bit played twice (or overlapping) does.
        static double DoubledMs(float[] x0, int around, out double best)
        {
            // (On the changes from sample to sample: noise that is mostly bass looks alike over a few ms by chance;
            // its changes don't. A bit really played twice still matches at 0.95-1.00.)
            var x = new float[x0.Length];
            for (int i = 1; i < x0.Length; i++) x[i] = x0[i] - x0[i - 1];
            best = 0;
            int doubled = 0;
            const int B = 960; // 20 ms
            for (int at = Math.Max(15000, around - 14400); at + B < x.Length && at < around + 28800; at += 480)
            {
                double ee = 0;
                for (int j = 0; j < B; j++) ee += (double)x[at + j] * x[at + j];
                if (ee < B * 25.0) continue; // (near silence: nothing to compare)
                double top = 0;
                for (int lag = 1440; lag <= 14400 && at - lag >= 0; lag++)
                {
                    double xy = 0, yy = 1e-9;
                    int b = at - lag;
                    for (int j = 0; j < B; j += 2) { xy += (double)x[at + j] * x[b + j]; yy += (double)x[b + j] * x[b + j]; }
                    double c = xy / Math.Sqrt(ee / 2 * yy);
                    if (c > top) top = c;
                }
                best = Math.Max(best, top);
                if (top > 0.9) doubled += 10;
            }
            return doubled;
        }

        // Longest run of (near) silence in the recorded sound around a loop point, ms.
        static double SilentMs(float[] x, int around)
        {
            int run = 0, most = 0;
            for (int i = Math.Max(0, around - 24000); i < x.Length && i < around + 28800; i++)
            {
                if (Math.Abs(x[i]) < 30) run++; else run = 0;
                if (run > most) most = run;
            }
            return most / 48.0;
        }

        // --case loops [--pattern file]: Repeat on, each video (or the one named) at 1x (after a speed change, so FastViewer
        // plays the sound), 1.5x and 2x, with the same pitch and with "Change pitch with speed"; several loops each.
        // Per loop: silence at the loop point (in what the speakers got, and the speakers running dry), doubled sound
        // right after it, and how far sound and picture are apart 0.15-1.2 s after the picture started over.
        void HuntLoops()
        {
            string dbgFile = Path.Combine(snapDir, "log.partial.txt"); var dbgClock = System.Diagnostics.Stopwatch.StartNew(); SoundPlayer.DebugStep = w => { try { lock (dbgFile) File.AppendAllText(dbgFile, "    ~ " + dbgClock.ElapsedMilliseconds + " ms sound thread: " + w + Environment.NewLine); } catch { } };
            muted = false;
            Loudness.On = false;
            loop = true;
            UpdateControls();
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => nn.Equals(huntPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            var runs = new List<Action<Action>>();
            int totalBad = 0, totalLoops = 0;
            foreach (var name in names)
                foreach (bool keep in new[] { true, false })
                    foreach (double sp in new[] { 1.0, 1.5, 2.0 })
                    {
                        string nm = name; bool k = keep; double spd = sp;
                        runs.Add(next => { Step("run " + nm + " " + spd + "x " + k); HuntOpen(nm, () =>
                        {
                            Step("opened; setting up");
                            TimeStretch.KeepPitch = k;
                            if (!playing) TogglePlay();
                            SetSpeed(spd == 1 ? 1.25 : spd);
                            After(700, () =>
                            {
                                Step("measuring starts");
                                if (spd == 1) SetSpeed(1); // (back at 1x FastViewer keeps playing the sound itself)
                                double len = shown.Video.NaturalDuration.HasTimeSpan ? shown.Video.NaturalDuration.TimeSpan.TotalSeconds : 10;
                                int loops = len < 4 ? 6 : len < 10 ? 4 : 2;
                                double wait = (len - shown.Video.Position.TotalSeconds) / spd + loops * len / spd + 1.5;
                                var p = soundPlayer;
                                if (p == null) { HuntLog(nm + ": no own sound FAIL"); next(); return; }
                                p.ResetStats();
                                p.LoopFrames = new List<Dictionary<int, long>>(); p.DroppedFrames = 0;
                                var tap = new float[(int)(48000 * (wait + 2))];
                                p.TapAt[0] = 0;
                                p.Tap = tap;
                                loopTapAt = new List<int>();
                                var loopTimes = new List<long>();
                                var drifts = new List<long[]>();
                                var clock = System.Diagnostics.Stopwatch.StartNew();
                                int seenLoops = 0;
                                var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
                                t.Tick += delegate
                                {
                                    if (loopTapAt.Count > seenLoops) { seenLoops = loopTapAt.Count; loopTimes.Add(clock.ElapsedMilliseconds); }
                                    drifts.Add(new[] { clock.ElapsedMilliseconds, (long)(p.LastDrift * 1e6), (long)(p.LastStep * 1e6) });
                                };
                                t.Start();
                                After((int)(wait * 1000), () =>
                                {
                                    Step("measuring done");
                                    t.Stop();
                                    p.Tap = null;
                                    // (Everything counted now; the heavy comparing runs off the window's thread, which would
                                    // otherwise stand still for seconds and look to the sound like a stopped picture.)
                                    var x = tap.Take(p.TapAt[0]).ToArray();
                                    var at = loopTapAt.ToList();
                                    var perLoop = p.LoopFrames.ToList(); p.LoopFrames = null; long dropped = p.DroppedFrames;
                                    var heardGains = HeardGains(SoundMixing.Known(shown.File) ?? SoundMixing.Layout(shown.File)); var lay = SoundMixing.Known(shown.File);
                                    string fileNow = shown.File; bool sampleCheck = spd == 1 && k && lay != null && lay.Count == 1;
                                    var times = loopTimes.ToList();
                                    var ds = drifts.ToList();
                                    int resyncs = p.Resyncs, starve = p.LongestStarve, splices = p.Splices, seams = p.SeamSkips;
                                    string notes = string.Join(" ", p.Notes.ToArray());
                                    string head = nm + " at " + spd + "x, " + (k ? "same pitch" : "pitch with speed") + ": " + at.Count + " loops, speakers ran dry " + starve + " ms at most, sound restarts " + resyncs +
                                        " [" + notes + "], slivers " + splices + ", seam skips " + seams + ", watchdog: woken " + pictureWakes + " reopened " + pictureReopens + " speed set again " + speedRepairs;
                                    loopTapAt = null;
                                    ThreadPool.QueueUserWorkItem(delegate
                                    {
                                        var lines = new List<string>();
                                        VideoConverter.Reading.Begin();
                                        // Every sample: per whole loop, the frames of each heard track put in, against the file's own.
                                        analysisThread = System.Threading.Thread.CurrentThread; analysisSince = Environment.TickCount;
                                        Step("analysis: counting the file");
                                        var fileFrames = StreamFrames(fileNow, lay, heardGains);
                                        Step("analysis: counted");
                                        int fullLoops = 0, fullOk = 0;
                                        var miss = new List<string>();
                                        for (int j = 1; j < perLoop.Count; j++)
                                        {
                                            fullLoops++;
                                            bool all = fileFrames.Count > 0;
                                            foreach (var kv in fileFrames) { long got; perLoop[j].TryGetValue(kv.Key, out got); if (got != kv.Value) { all = false; miss.Add("loop " + j + " stream " + kv.Key + ": " + got + " of " + kv.Value); } }
                                            if (all) fullOk++;
                                        }
                                        bool samplesOk = fullLoops > 0 && fullOk == fullLoops && dropped == 0;
                                        string allSamples = "every sample of every track (" + string.Join("/", fileFrames.Values.Select(v => (v / 48.0).ToString("0") + " ms")) + ") put in " + fullOk + " of " + fullLoops + " whole loops" + (miss.Count > 0 ? " (" + string.Join("; ", miss.Take(3)) + ")" : "") + ", frames left out " + dropped;
                                        float[] content = null;
                                        if (sampleCheck)
                                        {
                                            var whole = DecodeLeft(fileNow, 3600);
                                            int f0 = 0; while (f0 < whole.Length && whole[f0] == 0) f0++;
                                            int f1 = whole.Length; while (f1 > f0 && whole[f1 - 1] == 0) f1--;
                                            content = whole.Skip(f0).Take(f1 - f0).ToArray();
                                            double es0 = VideoInfo.EditStart(fileNow, "soun", 0), ed0 = VideoInfo.EditDuration(fileNow, "soun", 0);
                                            if (ed0 > 0.5 && content.Length > (es0 + ed0) * 48000) content = content.Take((int)Math.Round((es0 + ed0) * 48000)).ToArray(); // (without the padding)
                                        }
                                        int bad = 0;
                                        for (int i = 0; i < at.Count; i++)
                                        {
                                            Step("analysis: loop " + i);
                                            double best, dbl = DoubledMs(x, at[i], out best), sil = SilentMs(x, at[i]);
                                            long from = times.Count > i ? times[i] : 0;
                                            long until = from + (long)Math.Min(1200, 700 * len / spd); // (up to 70% of a loop: not the next start-over's planned wait)
                                            var after = ds.Where(d => d[0] >= from + 150 && d[0] <= until).Select(d => Math.Abs(d[1]) / 1000.0).ToList();
                                            double off = after.Count > 0 ? after.Max() : -1;
                                            double near = ds.Where(d => d[0] >= from + 150).Select(d => d[1] / 1000.0).FirstOrDefault(), later = ds.Where(d => d[0] >= from + 900).Select(d => d[1] / 1000.0).FirstOrDefault();
                                            var steps = ds.Where(d => d[0] >= from + 150 && d[0] <= from + 1200).Select(d => d[2] / 1e6).ToList();
                                            string pace = steps.Count > 0 ? steps.Min().ToString("0.000") + "-" + steps.Max().ToString("0.000") : "-";
                                            double em = 1, nx = 1;
                                            if (content != null) MatchEnd(x, content, at[i], out em, out nx);
                                            if (off < 0) { lines.Add("loop " + (i + 1) + ": at the very end of the measuring (not counted)"); continue; }
                                            bool ok = dbl == 0 && sil < 5 && off <= 40 && em >= 0.8 && nx >= 0.85; // (the end: 20 ms blocks at 0.9, the inaudible 0.4% correction smooths the highest frequencies a little)
                                            if (!ok) bad++;
                                            lines.Add("loop " + (i + 1) + ": silence " + sil.ToString("0") + " ms, doubled " + dbl + " ms (closest match " + best.ToString("0.00") + "), apart " + off.ToString("0") + " ms (signed +0.15 s " + near.ToString("0") + ", +0.9 s " + later.ToString("0") + "; over 0 = sound behind; read at " + pace + "x)" + (content != null ? ", the file's last 500 ms matched " + em.ToString("0.000") + ", its first 100 ms right after " + nx.ToString("0.000") : "") + (ok ? "" : " FAIL"));
                                        }
                                        Dispatcher.BeginInvoke(new Action(delegate
                                        {
                                            totalBad += bad; totalLoops += at.Count;
                                            HuntLog(head + "; " + allSamples + (at.Count >= loops && bad == 0 && starve < 5 && resyncs == 0 && samplesOk ? "" : " FAIL"));
                                            foreach (var l in lines) HuntLog("  " + l);
                                            next();
                                        }));
                                    });
                                });
                            });
                        }); });
                    }
            int n = 0;
            Action go = null;
            go = () => { if (n < runs.Count) runs[n++](go); else { HuntLog("all: " + totalLoops + " loops, " + totalBad + " with a problem"); TimeStretch.KeepPitch = true; HuntDone(); } };
            go();
        }
    }
    partial class ViewerWindow
    {
        // --case decodeinfo: what Windows' decoder hands out for each video's sound: first and last time stamps, the
        // silence at the start and end, and the first stamp after jumping back to 0 (as a loop does).
        void HuntDecodeInfo()
        {
            var list = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).ToList();
            ThreadPool.QueueUserWorkItem(delegate
            {
                VideoConverter.Reading.Begin();
                var lines = new List<string>();
                foreach (var f in list)
                {
                    var layout = SoundMixing.Layout(f);
                    if (layout.Streams == null) { lines.Add(Path.GetFileName(f) + ": no layout"); continue; }
                    for (int k = 0; k < layout.Streams.Length; k++)
                    {
                        var r = VideoConverter.Reading.Open(f);
                        VideoConverter.Reading.DeselectAll(r);
                        VideoConverter.Reading.Select(r, layout.Streams[k]);
                        long first = -1, lastEnd = 0; int frames = 0, lead = -1, sinceLoud = 0;
                        long firstAfterJump = -1;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            while (true)
                            {
                                int st; long tm; IMFSample smp; bool ended, ch;
                                if (!VideoConverter.Reading.Next(r, out st, out tm, out smp, out ended, out ch)) break;
                                if (smp != null)
                                {
                                    IMFMediaBuffer buf; smp.ConvertToContiguousBuffer(out buf);
                                    IntPtr data; int max, size; buf.Lock(out data, out max, out size);
                                    var b = new short[size / 2]; Marshal.Copy(data, b, 0, b.Length); buf.Unlock(); Marshal.ReleaseComObject(buf);
                                    Marshal.ReleaseComObject(smp);
                                    if (pass == 1) { firstAfterJump = tm; break; }
                                    if (first < 0) first = tm;
                                    for (int i = 0; i < b.Length; i += 2)
                                    {
                                        bool loud = Math.Abs((int)b[i]) > 30;
                                        if (loud && lead < 0) lead = frames;
                                        sinceLoud = loud ? 0 : sinceLoud + 1;
                                        frames++;
                                    }
                                    lastEnd = tm + (long)(b.Length / 2 * 1e7 / 48000);
                                }
                                if (ended) break;
                            }
                            if (pass == 0) VideoConverter.Reading.JumpTo(r, 0);
                        }
                        Marshal.ReleaseComObject(r);
                        lines.Add(Path.GetFileName(f) + " track " + (k + 1) + " (offset " + (layout.Offsets != null ? (layout.Offsets[k] * 1000).ToString("0") : "-") + " ms): first stamp " + (first / 10000.0).ToString("0.0") + " ms, ends at " + (lastEnd / 10000.0).ToString("0.0") +
                            " ms (" + frames + " frames = " + (frames / 48.0).ToString("0.0") + " ms), silent start " + (lead / 48.0).ToString("0.0") + " ms, silent end " + (sinceLoud / 48.0).ToString("0.0") + " ms, first stamp after jumping to 0: " + (firstAfterJump / 10000.0).ToString("0.0") + " ms");
                    }
                }
                Dispatcher.BeginInvoke(new Action(delegate { foreach (var l in lines) HuntLog(l); HuntDone(); }));
            });
        }
    }
    partial class ViewerWindow
    {
        // How many frames (48 kHz) each heard stream of the file holds, decoded from start to end.
        static Dictionary<int, long> StreamFrames(string file, SoundLayout layout, double[] gains)
        {
            var result = new Dictionary<int, long>();
            if (layout == null || layout.Streams == null) return result;
            for (int k = 0; k < layout.Streams.Length && k < gains.Length; k++)
            {
                if (gains[k] <= 0) continue;
                var r = VideoConverter.Reading.Open(file);
                if (r == null) continue;
                try
                {
                    VideoConverter.Reading.DeselectAll(r);
                    if (!VideoConverter.Reading.Select(r, layout.Streams[k])) continue;
                    long frames = 0;
                    while (true)
                    {
                        int st; long tm; IMFSample smp; bool ended, ch;
                        if (!VideoConverter.Reading.Next(r, out st, out tm, out smp, out ended, out ch)) break;
                        if (smp != null) { frames += TrackMixer.FramesOf(smp); Marshal.ReleaseComObject(smp); }
                        if (ended) break;
                    }
                    // (The real sound: what the file says, without the encoder's padding after it.)
                    double es = VideoInfo.EditStart(file, "soun", k), ed = VideoInfo.EditDuration(file, "soun", k);
                    if (ed > 0.5) frames = Math.Min(frames, (long)Math.Round((es + ed) * TrackMixer.Rate));
                    result[layout.Streams[k]] = frames;
                }
                finally { Marshal.ReleaseComObject(r); }
            }
            return result;
        }

        // At 1x: where in what the speakers got (near `around`) the file's last 500 ms is, how well it matches there
        // (1 = sample for sample), and how well the file's first 100 ms of sound matches right after it.
        static void MatchEnd(float[] tap, float[] content, int around, out double endMatch, out double nextMatch)
        {
            endMatch = nextMatch = 0;
            int L = 24000, N = 4800;
            if (content.Length < L + N) return;
            int probeAt = content.Length - L; // the start of the last 500 ms
            int best = -1; double bestC = -1;
            for (int pos = Math.Max(0, around - 48000); pos < around + 48000 && pos + L + N < tap.Length; pos++)
            {
                double xy = 0, yy = 1e-9, xx = 1e-9;
                for (int j = 0; j < 2400; j += 4) { double a = content[probeAt + j], b = tap[pos + j]; xy += a * b; xx += a * a; yy += b * b; }
                double c = xy / Math.Sqrt(xx * yy);
                if (c > bestC) { bestC = c; best = pos; }
            }
            if (best < 0) return;
            // (In 20 ms blocks, each lined up by itself within 5 ms: the inaudible 0.4% pace correction stretches 500 ms by up
            // to 2 ms. The worst block counts.)
            endMatch = BlockMatch(content, probeAt, tap, best, L);
            nextMatch = BlockMatch(content, 0, tap, best + L, N);
        }

        static double BlockMatch(float[] a, int ia, float[] b, int ib, int n)
        {
            double worst = 1;
            int drift = 0;
            for (int k = 0; k + 960 <= n; k += 960)
            {
                double top = -1; int topLag = 0;
                for (int lag = drift - 240; lag <= drift + 240; lag++) { if (ib + k + lag < 0) continue; double c = Corr(a, ia + k, b, ib + k + lag, 960); if (c > top) { top = c; topLag = lag; } }
                drift = topLag;
                worst = Math.Min(worst, top);
            }
            return worst;
        }

        static double Corr(float[] a, int ia, float[] b, int ib, int n)
        {
            double xy = 0, xx = 1e-9, yy = 1e-9;
            for (int j = 0; j < n && ia + j < a.Length && ib + j < b.Length; j++) { xy += (double)a[ia + j] * b[ib + j]; xx += (double)a[ia + j] * a[ia + j]; yy += (double)b[ib + j] * b[ib + j]; }
            return xy / Math.Sqrt(xx * yy);
        }
    }
    partial class ViewerWindow
    {
        // --case loopchange [--pattern file]: Repeat on; a speed change (or a hold starting / ending) in the last second
        // before the loop's end, 3 times each. After the start-over: how far apart sound and picture are (0.15 s to 70% of
        // the loop), whether the sound ever left anything out, and whether that loop got every sample of every track.
        void HuntLoopChange()
        {
            muted = false; Loudness.On = false; loop = true; UpdateControls(); TimeStretch.KeepPitch = true;
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => nn.Equals(huntPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            var setCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            Action<bool> press = down =>
            {
                var a = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { 1 });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            // (label, speed before, seconds before the end, what happens then)
            var scen = new List<Tuple<string, double, double, Action>>
            {
                Tuple.Create("1x -> 2x 0.8 s before the end", 1.0, 0.8, (Action)(() => SetSpeed(2))),
                Tuple.Create("2x -> 1x 0.5 s before the end", 2.0, 0.5, (Action)(() => SetSpeed(1))),
                Tuple.Create("1x -> 1.5x 0.3 s before the end", 1.0, 0.3, (Action)(() => SetSpeed(1.5))),
                Tuple.Create("hold pressed 1.1 s before the end", 1.0, 1.1, (Action)(() => press(true))),
                Tuple.Create("hold let go 0.4 s before the end", -2.0, 0.4, (Action)(() => press(false))),
            };
            var runs = new List<Action<Action>>();
            int bad = 0, total = 0;
            foreach (var name in names)
                foreach (var sc in scen)
                    for (int rep = 0; rep < 3; rep++)
                    {
                        string nm = name; var c = sc;
                        runs.Add(next => HuntOpen(nm, () =>
                        {
                            if (holding || holdTestPressed) press(false);
                            if (!playing) TogglePlay();
                            SetSpeed(1.25); // (so FastViewer plays the sound, also at 1x after it)
                            After(600, () =>
                            {
                                if (c.Item2 > 0) SetSpeed(c.Item2); else { SetSpeed(1); press(true); } // (-2: a hold that is on)
                                double dur = shown.Video.NaturalDuration.TimeSpan.TotalSeconds;
                                // Start where the change comes 3 s later.
                                double sp = c.Item2 > 0 ? c.Item2 : 2;
                                After(c.Item2 > 0 ? 400 : 900, () =>
                                {
                                    var p = soundPlayer;
                                    if (p == null) { HuntLog(nm + ", " + c.Item1 + ": no own sound FAIL"); next(); return; }
                                    p.DroppedFrames = 0;
                                    p.LoopFrames = new List<Dictionary<int, long>>();
                                    int loopsBefore = 0;
                                    loopTapAt = new List<int>();
                                    bool done = false;
                                    long changedAt = -1, loopAt = -1;
                                    var drifts = new List<double[]>();
                                    var clock = System.Diagnostics.Stopwatch.StartNew();
                                    var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
                                    t.Tick += delegate
                                    {
                                        long ms = clock.ElapsedMilliseconds;
                                        double pos = shown.Video.Position.TotalSeconds, spNow = shown.Video.SpeedRatio > 0 ? shown.Video.SpeedRatio : 1;
                                        if (changedAt < 0 && loopTapAt.Count == loopsBefore && (dur - pos) / spNow <= c.Item3 && (dur - pos) / spNow > c.Item3 - 0.08) { c.Item4(); changedAt = ms; loopsBefore = loopTapAt.Count; }
                                        if (changedAt >= 0 && loopAt < 0 && loopTapAt.Count > loopsBefore) loopAt = ms;
                                        if (loopAt >= 0) drifts.Add(new[] { ms - loopAt, p.LastDrift * 1000.0 });
                                        double loopWall = LoopLenFor(shown) / Math.Max(0.25, speed);
                                        if (!done && loopAt >= 0 && ms - loopAt > Math.Max(1500, 1000 * loopWall * 0.8))
                                        {
                                            done = true;
                                            t.Stop();
                                            var win = drifts.Where(d => d[0] >= 150 && d[0] <= 700 * loopWall).Select(d => d[1]).ToList();
                                            double worst = win.Count > 0 ? win.Max(v => Math.Abs(v)) : -1, first = win.Count > 0 ? win[0] : 0;
                                            bool ok = worst >= 0 && worst <= 40 && p.DroppedFrames == 0;
                                            total++; if (!ok) bad++;
                                            HuntLog(nm + ", " + c.Item1 + ": after the start-over sound and picture at most " + worst.ToString("0") + " ms apart (first " + first.ToString("0") + "), now " + speed + "x, frames left out " + p.DroppedFrames + (ok ? "" : " FAIL"));
                                            if (holding || holdTestPressed) press(false);
                                            loopTapAt = null; p.LoopFrames = null;
                                            After(300, next);
                                        }
                                        if (!done && ms > 30000) { done = true; t.Stop(); total++; bad++; HuntLog(nm + ", " + c.Item1 + ": no start-over within 30 s FAIL"); loopTapAt = null; After(300, next); }
                                    };
                                    t.Start();
                                });
                            });
                        }));
                    }
            int n = 0;
            Action go = null;
            go = () => { if (n < runs.Count) runs[n++](go); else { HuntLog("all: " + total + " changes, " + bad + " with a problem"); HuntDone(); } };
            go();
        }
    }
}
