// FastViewer 1.29.0: faster or slower sound at the same pitch (like YouTube, TikTok, Chrome), by WSOLA
// (waveform-similarity overlap-add): the sound is cut into 20 ms windows that overlap by half; for every 10 ms of
// sound out, the window is taken from about speed x 10 ms further on in the original, at the place within 15 ms of
// that where it lines up best with what was just played (so no phase jumps, no warble). At 1x exactly it hands the
// sound through untouched. Used by the player (SoundPlay.cs) and by "Keep speed" saves (Tracks.cs, SoundStretch).
// Settings > Videos > "Change pitch with speed" (off) brings back the old way (faster = higher, like a tape).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;

namespace FastViewer
{
    class TimeStretch
    {
        public static volatile bool KeepPitch = true; // settings line speedPitch= (1: the pitch changes with the speed)

        const int N = 960, H = 480, S = 720; // window 20 ms, step 10 ms, search 15 ms either way (48 kHz)
        static readonly float[] win = MakeWindow();
        static float[] MakeWindow()
        {
            var w = new float[N];
            for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); // (w[i] + w[i + H] = 1: the halves add up to the sound itself)
            return w;
        }

        public double Speed = 1;
        float[] buf = new float[48000 * 2]; // stereo, from input frame bufStart on
        long bufStart;
        int bufLen;
        float[] mono = new float[48000];
        float[] tail = new float[H * 2];     // the second half of the last window, still to be added to the next
        long prev = -1;                      // where the last window began (input frames)
        double ana;                          // where the next window would begin without searching
        bool any;
        // Seconds of the picture's clock per input frame (what is pushed may be a repeated sliver: 0 s).
        readonly List<double[]> segs = new List<double[]>(); // [frames, seconds]
        double segUsed;                      // frames of segs[0] already counted out
        public int Searches;                 // tests

        public void Reset()
        {
            bufStart = 0; bufLen = 0; prev = -1; ana = 0; any = false;
            Array.Clear(tail, 0, tail.Length);
            segs.Clear(); segUsed = 0;
        }

        long InEnd { get { return bufStart + bufLen; } }

        public void Push(short[] piece, int from, int count, double seconds)
        {
            if (count <= 0) return;
            Trim();
            if ((bufLen + count) * 2 > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, (bufLen + count) * 2));
            if (bufLen + count > mono.Length) Array.Resize(ref mono, Math.Max(mono.Length * 2, bufLen + count));
            for (int i = 0; i < count; i++)
            {
                float l = piece[(from + i) * 2], r = piece[(from + i) * 2 + 1];
                buf[(bufLen + i) * 2] = l;
                buf[(bufLen + i) * 2 + 1] = r;
                mono[bufLen + i] = l + r;
            }
            if (!any) { any = true; ana = bufStart + bufLen; }
            bufLen += count;
            segs.Add(new[] { (double)count, seconds });
        }

        // Drops what no window can reach any more.
        void Trim()
        {
            long keep = Math.Min(prev < 0 ? (long)ana : prev, (long)ana - S) - 8;
            int drop = (int)Math.Max(0, Math.Min(bufLen, keep - bufStart));
            if (drop < 48000) return;
            Array.Copy(buf, drop * 2, buf, 0, (bufLen - drop) * 2);
            Array.Copy(mono, drop, mono, 0, bufLen - drop);
            bufStart += drop;
            bufLen -= drop;
        }

        // Seconds of the picture's clock in input frames [from, from + frames) counted out from the front of segs.
        double Count(double frames)
        {
            double sec = 0;
            while (frames > 1e-9 && segs.Count > 0)
            {
                double left = segs[0][0] - segUsed, take = Math.Min(left, frames);
                sec += segs[0][0] > 0 ? segs[0][1] * take / segs[0][0] : 0;
                segUsed += take;
                frames -= take;
                if (segUsed >= segs[0][0] - 1e-9) { segs.RemoveAt(0); segUsed = 0; }
            }
            return sec;
        }

        // The picture's seconds pushed in but not yet out (they are inside the stretcher).
        public double PendingSeconds
        {
            get
            {
                double sec = 0;
                for (int i = 0; i < segs.Count; i++) sec += segs[i][0] > 0 ? segs[i][1] * (segs[i][0] - (i == 0 ? segUsed : 0)) / segs[i][0] : 0;
                return sec;
            }
        }

        // As much sound out as the input allows (10 ms at a time) into out (stereo, from frame 0), at the volume.
        // seconds: the picture's clock that sound covers.
        public int Pull(ref short[] output, float volume, out double seconds, float[] tap = null, int[] tapAt = null, bool hard = false)
        {
            seconds = 0;
            int made = 0;
            while (true)
            {
                bool exact = Math.Abs(Speed - 1) < 1e-6;
                long t = prev < 0 ? (long)Math.Round(ana) : prev + H; // (the natural next window: what follows the last one)
                long nominal = exact ? t : (long)Math.Round(ana);
                long b;
                if (prev < 0 || exact) { b = prev < 0 ? nominal : t; if (b + N > InEnd) break; }
                else
                {
                    if (Math.Max(nominal + S, t) + N > InEnd) break;
                    b = Best(t, nominal);
                }
                if ((made + H) * 2 > output.Length) Array.Resize(ref output, Math.Max(output.Length * 2, (made + H) * 2));
                int at = (int)(b - bufStart);
                for (int i = 0; i < H; i++)
                {
                    float w = win[i];
                    float l = tail[i * 2] + w * buf[(at + i) * 2], r = tail[i * 2 + 1] + w * buf[(at + i) * 2 + 1];
                    if (tap != null && tapAt[0] < tap.Length) tap[tapAt[0]++] = l;
                    output[(made + i) * 2] = hard ? Clamp(l * volume) : TrackMixer.Limit(l * volume);
                    output[(made + i) * 2 + 1] = hard ? Clamp(r * volume) : TrackMixer.Limit(r * volume);
                    tail[i * 2] = win[H + i] * buf[(at + H + i) * 2];
                    tail[i * 2 + 1] = win[H + i] * buf[(at + H + i) * 2 + 1];
                }
                made += H;
                // (Counted at the even pace, not at the window found: the clock never wobbles by the 15 ms searched.)
                double next = prev < 0 ? b + H * (exact ? 1 : Speed) : exact ? b + H : ana + H * Speed;
                seconds += Count(Math.Max(0, next - ana));
                ana = next;
                prev = b;
            }
            return made;
        }

        // Where the window that best continues the last one begins, near `nominal` (target: the sound right after it).
        long Best(long t, long nominal)
        {
            Searches++;
            int tt = (int)(t - bufStart);
            double te = 0;
            for (int j = 0; j < N; j += 4) te += mono[tt + j] * mono[tt + j];
            if (te < 1e-3 * N) return nominal; // (silence: anywhere is fine)
            long lo = Math.Max(bufStart, nominal - S), hi = nominal + S;
            long best = nominal;
            double bestScore = double.MinValue;
            for (long c = lo; c <= hi; c += 6) // coarse: every 6th place, every 4th sample
            {
                double s = Score(tt, (int)(c - bufStart), 4);
                if (s > bestScore) { bestScore = s; best = c; }
            }
            long center = best;
            bestScore = double.MinValue;
            for (long c = Math.Max(lo, center - 6); c <= Math.Min(hi, center + 6); c++) // fine: every place near it, every 2nd sample
            {
                double s = Score(tt, (int)(c - bufStart), 2);
                if (s > bestScore) { bestScore = s; best = c; }
            }
            return best;
        }

        double Score(int tt, int cc, int every)
        {
            double xy = 0, yy = 1e-9;
            for (int j = 0; j < N; j += every)
            {
                float y = mono[cc + j];
                xy += mono[tt + j] * y;
                yy += y * y;
            }
            return xy / Math.Sqrt(yy);
        }

        static short Clamp(float v) { return (short)Math.Max(-32768, Math.Min(32767, Math.Round(v))); }

        // The end: what is left of the last window (saving; nothing more comes).
        public int Finish(ref short[] output, float volume)
        {
            if (prev < 0) return 0;
            if (output.Length < H * 2) output = new short[H * 2];
            for (int i = 0; i < H; i++) { output[i * 2] = TrackMixer.Limit(tail[i * 2] * volume); output[i * 2 + 1] = TrackMixer.Limit(tail[i * 2 + 1] * volume); }
            Array.Clear(tail, 0, tail.Length);
            prev = -1;
            return H;
        }
    }
    // The picture catches up with the sound (1.29.0). At a speed change Windows' player stands still for about 0.1 s
    // and then goes on from where it stood, so it ends up behind; the sound plays straight on at the new speed and
    // isn't bent to wait for it. Once the picture runs steadily again it is moved forward by what it is behind (plus
    // the moment the move itself takes), at most three times per change and only while the change is going on.
    partial class ViewerWindow
    {
        int catchSteady, catchTries;
        long catchAt, catchSpeedStamp, catchSince;
        double catchTarget, catchPace = 1;
        bool catchPending;
        string catchFile;
        internal int pictureCatchUps;  // tests
        internal double lastCatchJump, lastSeekCost; // tests
        // How long Windows' player stands still after being moved, per file (a 1080p60 game clip: about 0.4 s, it has to
        // decode from the last whole picture; a small clip: a few hundredths). Learnt at each move.
        static readonly Dictionary<string, double> seekCosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        static double SeekCost(string file)
        {
            double c;
            lock (seekCosts) return file != null && seekCosts.TryGetValue(file, out c) ? c : 0.15;
        }

        // Before TellSound looks at how far the picture moved: while a catch-up move is under way (until the picture runs
        // on from its new place) that is neither a jump nor standing still.
        bool CatchUpTick(double raw, double at, long tick)
        {
            if (!catchPending) return false;
            double took = (tick - catchSince) / (double)Stopwatch.Frequency;
            if (raw >= catchTarget + 0.015 * catchPace || took > 1.2)
            {
                catchPending = false;
                double cost = Math.Max(0.01, Math.Min(0.8, took - 0.015));
                lock (seekCosts)
                {
                    double old;
                    seekCosts[catchFile] = seekCosts.TryGetValue(catchFile, out old) ? old * 0.4 + cost * 0.6 : cost;
                    if (seekCosts.Count > 500) seekCosts.Clear();
                }
                lastSeekCost = cost;
                ResetPictureWatch();
                soundSeenAt = at; // (counted from here: the move isn't a jump)
                soundSeenTick = tick;
            }
            return true;
        }

        void CatchUpPicture(Slot s, double moved, double since, double pace, bool changing, bool loopy, long tick)
        {
            if (speedAppliedAt != catchSpeedStamp) { catchSpeedStamp = speedAppliedAt; catchTries = 0; }
            bool steady = since > 0.005 && moved > since * pace * 0.6 && moved < since * pace * 1.5;
            catchSteady = steady ? catchSteady + 1 : 0;
            var p = soundPlayer;
            if (catchPending || !changing || loopy || !soundGo || p == null || !p.Sounding || catchTries >= 3 || catchSteady < 3 || scrubbing) return;
            if ((tick - catchAt) / (double)Stopwatch.Frequency < 0.3) return;
            double drift = p.LastDrift; // (under 0: the sound is ahead of the picture)
            if (drift > -0.15) return; // (only when far off: the timed change keeps them together; a move costs Windows' player 0.05-0.6 s)
            double jump = -drift + SeekCost(s.File) * pace, pos = s.Video.Position.TotalSeconds;
            var d = s.Video.NaturalDuration;
            if (jump > 2.5 || (d.HasTimeSpan && pos + jump > d.TimeSpan.TotalSeconds - 1)) return;
            catchTarget = pos + jump;
            s.Video.Position = TimeSpan.FromSeconds(catchTarget);
            catchPending = true;
            catchSince = catchAt = tick;
            catchFile = s.File;
            catchPace = pace;
            catchTries++;
            pictureCatchUps++;
            lastCatchJump = jump;
            ResetPictureWatch(); // (the move is not the picture running too fast)
            if (testRun) Log("picture caught up with the sound: +" + (jump * 1000).ToString("0") + " ms (sound was " + (-drift * 1000).ToString("0") + " ms ahead)");
        }
    }
    // Speed changes in step (1.29.0). Windows' player stands still for about 0.1 s (per file: learnt) whenever it
    // gets a new speed, then goes on from where it stood. So the two are timed against each other instead of one
    // waiting for the other afterwards: going slower (letting go of a 2x hold), the sound takes the new speed at once
    // and the picture gets it a moment later (speed x stand-still / difference: one stand-still at 2x -> 1x), so it is
    // just far enough ahead to stand still and land where the sound is. Going faster, the picture gets it at once and
    // the sound a moment later (twice the stand-still at 1x -> 2x). Only while FastViewer plays the sound itself.
    partial class ViewerWindow
    {
        DispatcherTimer pictureSpeedTimer;
        double soundPaceHeld = -1;      // the sound keeps (or already has) this pace...
        long soundPaceUntil;            // ...until then (Stopwatch ticks)
        bool soundPaceUntilPicture;     // (going slower: until the picture has it too)
        string soundPaceFile;
        static readonly Dictionary<string, double> speedStalls = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        long stallFrom; double stallPos, stallSpeed; string stallFile; bool stallWatch, stallFaster; int stallCatchUps;
        internal int timedChanges; internal double lastStall = -1, lastTimedDelay; // tests
        internal List<string> speedTrace; internal long speedTraceFrom; // tests: every tick around a change: ms:position/picture speed/sound speed/apart

        double TellPace(Slot s) { double p = SoundPace(s); NoteToldPace(p); return p; }

        static double StallOf(string file, bool faster)
        {
            double t;
            lock (speedStalls) return file != null && speedStalls.TryGetValue(file + (faster ? "|+" : "|-"), out t) ? t : 0.1;
        }

        // The pace FastViewer's sound plays at now (usually the picture's own speed).
        double SoundPace(Slot s)
        {
            if (soundPaceHeld > 0 && s.File == soundPaceFile && (soundPaceUntilPicture || Stopwatch.GetTimestamp() < soundPaceUntil)) return soundPaceHeld;
            soundPaceHeld = -1;
            return s.Video.SpeedRatio > 0 ? s.Video.SpeedRatio : 1;
        }

        // From ApplySpeedNow: true when the picture gets its speed later (pictureSpeedTimer).
        bool TimeSpeedChange(Slot s)
        {
            if (pictureSpeedTimer != null) pictureSpeedTimer.Stop();
            double a = s.Video.SpeedRatio > 0 ? s.Video.SpeedRatio : 1, b = speed;
            double heldBefore = SoundPace(s);
            soundPaceHeld = -1; soundPaceUntilPicture = false;
            var p = soundPlayer;
            if (!OwnSound(s) || p == null || !p.Sounding || loop || Math.Abs(a - b) < 0.001) return false;
            double T = StallOf(s.File, b > a);
            long now = Stopwatch.GetTimestamp();
            timedChanges++;
            soundPaceFile = s.File;
            if (b < a)
            {
                string bk = s.File + "|-";
                double d = Math.Max(0, Math.Min(0.8, p.OutputDelay + b * T / (a - b) + BiasOf(bk))); // (the sound already queued plays at the old speed first)
                biasKey = bk; biasRate = a - b; biasApplied = BiasOf(bk); biasAt = now; biasWatch = true;
                lastTimedDelay = d;
                soundPaceHeld = b;
                soundPaceUntilPicture = true;
                if (pictureSpeedTimer == null)
                {
                    pictureSpeedTimer = new DispatcherTimer(DispatcherPriority.Send);
                    pictureSpeedTimer.Tick += delegate
                    {
                        pictureSpeedTimer.Stop();
                        soundPaceUntilPicture = false;
                        soundPaceUntil = 0;
                        var now2 = shown;
                        if (now2 != null && now2.IsVideo && playing && !now2.Failed) SetPictureSpeed(now2);
                        else soundPaceHeld = -1;
                    };
                }
                pictureSpeedTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, d));
                pictureSpeedTimer.Start();
                speedAskedAt = now; // (counts as a change going on)
                return true;
            }
            string fk = s.File + "|+";
            double e = Math.Max(0, Math.Min(0.8, b * T / (b - a) - p.OutputDelay + BiasOf(fk)));
            biasKey = fk; biasRate = b - a; biasApplied = BiasOf(fk); biasAt = now; biasWatch = true;
            lastTimedDelay = e;
            soundPaceHeld = heldBefore > 0 ? heldBefore : a;
            soundPaceUntil = now + (long)(e * Stopwatch.Frequency);
            return false;
        }


        // How far apart sound and picture land after a timed change, learnt per file and direction: the next change's
        // delay is moved by exactly that (the sound keeps its speed; nothing is bent afterwards). Seconds of the delay.
        static readonly Dictionary<string, double> changeBias = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string biasKey; long biasAt; double biasRate, biasApplied; bool biasWatch;

        static double BiasOf(string key)
        {
            double v;
            lock (changeBias) return changeBias.TryGetValue(key, out v) ? v : 0;
        }

        // From TellSound: 0.9 s after a timed change (the picture has stood still and runs again; the tiny correction has
        // done next to nothing): over 0 the sound is behind, so the delay was too long by drift / (speed difference).
        void WatchChange(long tick)
        {
            if (!biasWatch || soundPlayer == null || (tick - biasAt) / (double)Stopwatch.Frequency < 0.9) return;
            biasWatch = false;
            if (SpeedChanging && (tick - speedAskedAt) / (double)Stopwatch.Frequency < 0.85) return; // (another change came)
            double d = soundPlayer.LastDrift;
            if (Math.Abs(d) > 0.4) return;
            double now = Math.Max(-0.4, Math.Min(0.4, biasApplied - d / Math.Max(0.1, biasRate)));
            lock (changeBias)
            {
                double old;
                changeBias[biasKey] = changeBias.TryGetValue(biasKey, out old) ? old * 0.7 + now * 0.3 : now * 0.5; // (slowly: each stand-still of Windows' player varies)
                if (changeBias.Count > 500) changeBias.Clear();
            }
        }
        // How much time the picture lost at its last speed change: 0.8 s later, how far behind it is of where that
        // speed would have taken it (TellSound, every tick). Learnt per file, for going faster and slower apart.
        void WatchStall(Slot s, double raw, long tick)
        {
            if (!stallWatch) return;
            double took = (tick - stallFrom) / (double)Stopwatch.Frequency;
            if (s.File != stallFile || catchPending || pictureCatchUps != stallCatchUps || Math.Abs(speed - stallSpeed) > 0.001 || !playing) { stallWatch = false; return; }
            if (took < 0.8) return;
            stallWatch = false;
            double lost = took - (raw - stallPos) / stallSpeed;
            if (lost < -0.05 || lost > 0.6) return; // (something else happened meanwhile)
            double t = Math.Max(0, Math.Min(0.4, lost));
            lastStall = t;
            string key = stallFile + (stallFaster ? "|+" : "|-");
            lock (speedStalls)
            {
                double old;
                speedStalls[key] = speedStalls.TryGetValue(key, out old) ? old * 0.5 + t * 0.5 : t;
                if (speedStalls.Count > 500) speedStalls.Clear();
            }
        }

        void StartStallWatch(Slot s, double from)
        {
            stallFrom = Stopwatch.GetTimestamp();
            stallPos = s.Video.Position.TotalSeconds;
            stallFile = s.File;
            stallSpeed = speed;
            stallFaster = speed > from;
            stallCatchUps = pictureCatchUps;
            stallWatch = true;
        }
    }
    // Seamless loops with FastViewer's own sound (1.29.0). The sound is the master: every track plays to its own last
    // sample and the next loop's sound follows right after (SoundPlayer: a loop of the sound is SoundTo - SoundFrom long).
    // The picture is fitted to it: started over exactly when the sound reaches the next loop's start, early by the moment
    // Windows' player needs for it. When the sound is a little longer than the picture, the picture waits on its last
    // frame for that moment (it has ended; OnVideoEnded leaves it); when shorter, its last pictures are left out.
    // How early is learnt per file and speed from how far apart sound and picture are 0.9 s after each start-over.
    partial class ViewerWindow
    {
        DispatcherTimer restartTimer;
        bool loopArmed = true, loopSyncWatch;
        internal bool pictureAtEnd;     // Windows' player said "ended" and waits for the planned start-over
        internal bool pictureHeld;      // paused just before its end, waiting for the sound to reach the next loop
        bool pauseFirst; long restartDue;
        string loopFile, loopKey;
        long loopSeekAt;
        double loopApplied;
        static readonly Dictionary<string, double> loopCosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        internal int plannedLoops; internal double lastLoopCost, lastLoopOffset; // tests

        // The length of one loop on the picture's clock: the sound's own length when FastViewer plays it.
        internal double LoopLenFor(Slot s)
        {
            var p = soundPlayer;
            if (p != null && !SoundPlayer.LoopWaits && p.SpanFile == s.File && p.LoopSpan > 0.5) return p.LoopSpan;
            return s.Video.NaturalDuration.HasTimeSpan ? s.Video.NaturalDuration.TimeSpan.TotalSeconds : 0;
        }

        static string LoopKey(Slot s, double pace) { return s.File + "|" + pace.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + (TimeStretch.KeepPitch ? "" : "|p"); }

        static double LoopCost(string key)
        {
            double c;
            lock (loopCosts) return loopCosts.TryGetValue(key, out c) ? c : 0.03; // (seconds early; measured 20-35 ms at 1-2x)
        }

        double SeamExtraFor(Slot s, double pace) { return 0; } // (no skipping at the seam any more)

        // A speed change in the last moments before a planned loop start (Repeat, FastViewer's sound) is saved for that
        // start: the sound keeps its speed to the very end and takes the new one exactly at the next loop's first sample
        // (SoundWish.PaceAt), the picture gets it with its start-over (one stand-still). In step from the first picture.
        internal double deferredPace = -1;
        double deferPaceAt, lastEarly;
        string deferFile;
        long deferSince;


        // From TellSound: should the planned start never come, the speed goes to the picture as usual.
        void CheckDeferred(Slot s)
        {
            if (deferredPace < 0) return;
            if (!loop || s.File != deferFile || (Stopwatch.GetTimestamp() - deferSince) / (double)Stopwatch.Frequency > 1.5) { deferredPace = -1; ApplySpeedNow(); }
        }

        // What the sound is told, and since when (TellSound): a new pace is heard only after what is already queued.
        double toldPace = 1, toldPrev = 1; long toldAt;
        void NoteToldPace(double pace)
        {
            if (Math.Abs(pace - toldPace) < 0.0005) return;
            toldPrev = toldPace; toldPace = pace; toldAt = Stopwatch.GetTimestamp();
        }

        // Seconds until the sound has played `media` more seconds of the picture's clock: what is queued still at the
        // pace it was told before, then the pace it has now, then (when it waits for the picture) the speed it will get.
        double SoundTimeTo(double media)
        {
            var p = soundPlayer;
            double od = p != null ? p.OutputDelay : 0.15, now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var seg = new List<double[]>(); // [until (s, absolute; 1e9 = for good), pace]
            double toldSec = toldAt / (double)Stopwatch.Frequency;
            if (now < toldSec + od) seg.Add(new[] { toldSec + od, toldPrev });
            if (soundPaceHeld > 0 && !soundPaceUntilPicture && Stopwatch.GetTimestamp() < soundPaceUntil)
            {
                seg.Add(new[] { soundPaceUntil / (double)Stopwatch.Frequency + od, toldPace });
                seg.Add(new[] { 1e9, speed });
            }
            else seg.Add(new[] { 1e9, toldPace });
            double t = now, left = media;
            foreach (var sg in seg)
            {
                double pace = Math.Max(0.1, sg[1]), span = Math.Max(0, sg[0] - t);
                if (left <= span * pace) return t - now + left / pace;
                left -= span * pace;
                t = Math.Max(t, sg[0]);
            }
            return t - now;
        }

        // A loop is under way, planned: Windows' player's "ended" is not a reason to start over at once.
        internal bool PlannedLoop(Slot s)
        {
            return loop && !SoundPlayer.LoopWaits && s != null && s.File == loopFile && soundPlayer != null && soundPlayer.Sounding && soundPlayer.SpanFile == s.File;
        }

        // From TellSound, every tick while FastViewer plays the sound.
        void PlanLoop(Slot s, double raw, double pace, long tick)
        {
            if (loopSyncWatch && s.File == loopFile && soundPlayer != null && (tick - loopSeekAt) / (double)Stopwatch.Frequency >= 0.3)
            {
                // 0.3 s after starting over (the picture runs again, the tiny correction has done next to nothing yet):
                // over 0 the sound is behind, i.e. the picture started over too early.
                loopSyncWatch = false;
                double dd = soundPlayer.LastDrift;
                lastLoopOffset = dd;
                bool afterChange = (Stopwatch.GetTimestamp() - Math.Max(speedAppliedAt, speedAskedAt)) / (double)Stopwatch.Frequency < 3;
                if (Math.Abs(dd) < 0.3 && !afterChange)
                    lock (loopCosts)
                    {
                        double now = Math.Max(-0.3, Math.Min(0.6, loopApplied - dd / Math.Max(0.25, pace)));
                        double old;
                        loopCosts[loopKey] = loopCosts.TryGetValue(loopKey, out old) ? old * 0.4 + now * 0.6 : now;
                        lastLoopCost = loopCosts[loopKey];
                        if (loopCosts.Count > 500) loopCosts.Clear();
                    }
            }
            if (!loop || !playing || scrubbing || s.Failed || revealing == s || soundPlayer == null || !soundPlayer.Sounding || SoundPlayer.LoopWaits) return;
            double len = LoopLenFor(s);
            if (len < 0.8) return;
            if (s.File != loopFile) { loopFile = s.File; loopArmed = true; pictureHeld = false; }
            if (pictureHeld && (restartTimer == null || !restartTimer.IsEnabled)) pictureHeld = false; // (paused by hand meanwhile, or another file)
            if (raw < len * 0.5 && !pictureAtEnd) loopArmed = true;
            if (!loopArmed || (restartTimer != null && restartTimer.IsEnabled)) return;
            // When does the sound reach the next loop's start? It is where the picture is, minus how far it is behind.
            double heard = raw - soundPlayer.LastDrift;
            double toStart = (len - heard) / Math.Max(0.1, SoundPace(s));
            string key = LoopKey(s, pace) + (deferredPace > 0 ? ">" + deferredPace.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "");
            double early;
            lock (loopCosts) early = loopCosts.ContainsKey(key) ? loopCosts[key] : LoopCost(LoopKey(s, pace)) + (deferredPace > 0 ? 0.5 * StallOf(s.File, deferredPace > pace) : 0);
            if (pictureSpeedTimer != null && pictureSpeedTimer.IsEnabled) early += StallOf(s.File, speed > pace);
            lastEarly = early;
            double wait = toStart - early;
            // (Windows' player must never reach its own end here: starting it over in the moment it finishes the file can
            // jam its whole media pipeline. Should the picture have to wait for the sound, it waits paused a moment before
            // its end, then starts over from there.)
            double dur = s.Video.NaturalDuration.HasTimeSpan ? s.Video.NaturalDuration.TimeSpan.TotalSeconds : len;
            double toSafe = (dur - 0.08 * Math.Max(1, pace) - raw) / Math.Max(0.1, pace);
            if (Math.Min(wait, toSafe) > 0.12) return; // (a later tick; the timer below is exact)
            loopArmed = false;
            if (restartTimer == null)
            {
                restartTimer = new DispatcherTimer(DispatcherPriority.Send);
                restartTimer.Tick += delegate
                {
                    restartTimer.Stop();
                    var now = shown;
                    if (now == null || now.File != loopFile || !loop || !playing || !now.IsVideo || now.Failed) { pauseFirst = false; return; }
                    if (pauseFirst)
                    {
                        pauseFirst = false;
                        now.Video.Pause();
                        pictureHeld = true;
                        Step("picture holds before its end for the sound");
                        double left = (restartDue - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                        if (left > 0.002) { restartTimer.Interval = TimeSpan.FromSeconds(left); restartTimer.Start(); return; }
                    }
                    RestartPicture(now, (string)restartTimer.Tag, now.Video.SpeedRatio);
                };
            }
            restartTimer.Tag = key;
            restartDue = Stopwatch.GetTimestamp() + (long)(Math.Max(0, wait) * Stopwatch.Frequency);
            pauseFirst = wait > toSafe + 0.005;
            double first = pauseFirst ? toSafe : wait;
            if (first <= 0.002 && !pauseFirst) { RestartPicture(s, key, pace); return; }
            restartTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, first));
            restartTimer.Start();
        }

        void RestartPicture(Slot s, string key, double pace)
        {
            // A speed change still waiting for the picture (going slower) is given with the start-over: one stand-still.
            if (pictureSpeedTimer != null && pictureSpeedTimer.IsEnabled) { pictureSpeedTimer.Stop(); soundPaceUntilPicture = false; soundPaceHeld = -1; SetPictureSpeed(s); }
            if (deferredPace > 0) { deferredPace = -1; SetPictureSpeed(s); } // (the speed saved for this start)
            Step("restart picture (at end " + pictureAtEnd + ", pos " + s.Video.Position.TotalSeconds.ToString("0.000") + ")");
            s.Video.Position = TimeSpan.Zero;
            pictureAtEnd = false;
            pictureHeld = false;
            s.Video.Play(); // (harmless while it plays; needed once Windows' player has ended, or says so late)
            NoteLoop(s); // (the picture's clock counts on by the sound's loop: the sound sees no jump)
            plannedLoops++;
            loopSeekAt = Stopwatch.GetTimestamp();
            loopKey = key;
            loopApplied = lastEarly;
            loopSyncWatch = true;
            ResetPictureWatch();
            if (testRun) Log("loop: picture started over " + (loopApplied * 1000).ToString("0") + " ms early (learnt)");
        }
    }
}
