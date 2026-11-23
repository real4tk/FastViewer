// FastViewer 1.29.0: "Even out volume" (Settings > Videos). Every video and song is measured once, in the
// background, the way EBU R128 / ITU-R BS.1770 measures loudness (K-weighted, 400 ms blocks, gated), and is then
// played that much louder or quieter, so one file isn't much louder than the next. What is measured is what is
// heard: for a Medal clip the tracks mixed as FastViewer plays them. Results are kept per file (loudness.txt).
// Quiet files are made louder only a little (at most 6 dB), never if they're nearly silent (no hiss turned up),
// and never past their own loudest peak (no clipping).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FastViewer
{
    class LoudnessResult
    {
        public double Lufs = double.NegativeInfinity; // integrated loudness; -inf: silent or no sound
        public double Peak;                           // loudest sample of what is heard (1 = full scale)
        public double Seconds;                        // how much sound was measured
        public int Ms;                                // how long measuring took (tests)
    }

    static class Loudness
    {
        public static bool On = true;                 // settings line evenVolume=
        public const double Target = -18;             // LUFS everything is brought to (between YouTube's -14 and TV's -23; Medal clips are about -24)
        public const double MaxBoost = 6, MaxCut = 15; // dB
        public static string StoreFile;               // loudness.txt (null: kept in memory only, as in tests)

        static readonly Dictionary<string, LoudnessResult> byKey = new Dictionary<string, LoudnessResult>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, LoudnessResult> byPath = new Dictionary<string, LoudnessResult>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> stored;

        static string KeyOf(string file)
        {
            try
            {
                var info = new FileInfo(file);
                return file + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
            catch { return null; }
        }

        // Known without measuring (memory only, no disk): false if it still has to be measured.
        public static bool Known(string file, out LoudnessResult r)
        {
            lock (byKey) return byPath.TryGetValue(file ?? "", out r);
        }

        // From loudness.txt if it was measured before (a glance at memory and one small file; off the window's thread).
        public static LoudnessResult Remembered(string file)
        {
            LoudnessResult r;
            if (Known(file, out r)) return r;
            string key = KeyOf(file);
            if (key == null) return null;
            lock (byKey)
            {
                if (byKey.TryGetValue(key, out r)) { byPath[file] = r; return r; }
                r = FromDisk(key);
                if (r != null) { byKey[key] = r; byPath[file] = r; }
                return r;
            }
        }

        public static void Forget() { lock (byKey) { byKey.Clear(); byPath.Clear(); } }

        // How much louder (over 1) or quieter (under 1) a file is played.
        public static double GainOf(LoudnessResult r)
        {
            if (r == null || double.IsNaN(r.Lufs) || r.Lufs < -70 || r.Seconds < 1) return 1; // silent, or too little to tell
            double db = Target - r.Lufs;
            // Quiet files: louder by at most 6 dB, and less the closer they are to silence (-30 to -40 LUFS: from 6 down to 0
            // dB), so a file that is mostly hiss or room noise stays as it is.
            double boost = r.Lufs >= -30 ? MaxBoost : r.Lufs <= -40 ? 0 : MaxBoost * (r.Lufs + 40) / 10;
            db = Math.Max(-MaxCut, Math.Min(boost, db));
            // Never past full scale: the loudest peak stays at least 1 dB under it.
            if (db > 0 && r.Peak > 0) db = Math.Max(0, Math.Min(db, 20 * Math.Log10(0.89 / r.Peak)));
            return Math.Pow(10, db / 20);
        }

        // Measures (or finds) a file's loudness. stop() is asked now and then: true gives up (null).
        public static LoudnessResult Measure(string file, Func<bool> stop)
        {
            var have = Remembered(file);
            if (have != null) return have;
            string key = KeyOf(file);
            if (key == null) return null;
            var watch = Stopwatch.StartNew();
            var r = Run(file, stop);
            if (r == null) return null;
            r.Ms = (int)watch.ElapsedMilliseconds;
            lock (byKey)
            {
                if (byKey.Count > 3000) { byKey.Clear(); byPath.Clear(); }
                byKey[key] = r;
                byPath[file] = r;
            }
            ToDisk(key, r);
            return r;
        }

        // What is heard, mixed as the player mixes it (all tracks at their Sound volumes, as "Game and mic"), decoded
        // from start to end at 48 kHz stereo, K-weighted and measured in 400 ms blocks every 100 ms.
        static LoudnessResult Run(string file, Func<bool> stop)
        {
            var layout = SoundMixing.Layout(file);
            var result = new LoudnessResult();
            if (layout == null || layout.Count == 0 || layout.Streams == null) return result; // no sound: nothing to even out
            var gains = new double[layout.Count];
            if (layout.Count == 1) gains[0] = 1;
            else if (layout.Sources.Length < 2) gains[layout.MixTrack >= 0 ? layout.MixTrack : 0] = 1;
            else for (int k = 0; k < layout.Sources.Length; k++) gains[layout.Sources[k]] = SoundMixing.Volume(k) / 100.0;
            if (gains.All(g => g <= 0)) return result;
            IMFSourceReader reader = VideoConverter.Reading.Open(file);
            if (reader == null) return null;
            try
            {
                VideoConverter.Reading.DeselectAll(reader);
                var mixer = new TrackMixer();
                var slotOf = new Dictionary<int, int>();
                for (int k = 0; k < layout.Streams.Length && k < gains.Length; k++)
                    if (gains[k] > 0 && VideoConverter.Reading.Select(reader, layout.Streams[k]))
                        slotOf[layout.Streams[k]] = mixer.AddTrack(gains[k]);
                if (slotOf.Count == 0) return null;
                var meter = new Meter();
                int reads = 0;
                while (true)
                {
                    if ((++reads & 15) == 0 && stop != null && stop()) return null;
                    int stream;
                    long time;
                    IMFSample sample;
                    bool ended, changed;
                    if (!VideoConverter.Reading.Next(reader, out stream, out time, out sample, out ended, out changed)) return null;
                    int slot;
                    bool mine = slotOf.TryGetValue(stream, out slot);
                    if (sample != null)
                    {
                        try { if (mine) mixer.Add(slot, time, sample); }
                        finally { Marshal.ReleaseComObject(sample); }
                    }
                    if (mine && ended) mixer.End(slot);
                    bool all = mixer.AllEnded;
                    long at;
                    short[] piece;
                    while ((piece = mixer.Take(all, out at, TrackMixer.Rate)) != null) meter.Add(piece);
                    if (all) break;
                    if (sample == null && !ended && !mine && reads > 200000) return null; // (never: a reader that only says nothing)
                }
                meter.Finish(result);
                return result;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(reader); }
        }

        // BS.1770 at 48 kHz: a high shelf (+4 dB above ~1.5 kHz, the head) then a high-pass (~38 Hz), per channel.
        class Meter
        {
            const double B0 = 1.53512485958697, B1 = -2.69169618940638, B2 = 1.19839281085285, A1 = -1.69065929318241, A2 = 0.73248077421585;
            const double C1 = -1.99004745483398, C2 = 0.99007225036621; // (high-pass: 1, -2, 1 over these)
            readonly double[] x1 = new double[2], x2 = new double[2], y1 = new double[2], y2 = new double[2];
            readonly double[] u1 = new double[2], u2 = new double[2], v1 = new double[2], v2 = new double[2];
            const int Sub = TrackMixer.Rate / 10; // 100 ms
            double subSum;
            int subFrames;
            readonly List<double> subs = new List<double>(); // mean square (both channels added) of every 100 ms
            int peak;
            long frames;

            public void Add(short[] piece)
            {
                for (int i = 0; i + 1 < piece.Length; i += 2)
                {
                    double sum = 0;
                    for (int c = 0; c < 2; c++)
                    {
                        int raw = piece[i + c];
                        int abs = raw < 0 ? -raw : raw;
                        if (abs > peak) peak = abs;
                        double x = raw / 32768.0;
                        double y = B0 * x + B1 * x1[c] + B2 * x2[c] - A1 * y1[c] - A2 * y2[c];
                        x2[c] = x1[c]; x1[c] = x; y2[c] = y1[c]; y1[c] = y;
                        double z = y - 2 * u1[c] + u2[c] - C1 * v1[c] - C2 * v2[c];
                        u2[c] = u1[c]; u1[c] = y; v2[c] = v1[c]; v1[c] = z;
                        sum += z * z;
                    }
                    subSum += sum;
                    if (++subFrames == Sub) { subs.Add(subSum / Sub); subSum = 0; subFrames = 0; }
                    frames++;
                }
            }

            public void Finish(LoudnessResult r)
            {
                r.Peak = peak / 32768.0;
                r.Seconds = frames / (double)TrackMixer.Rate;
                // 400 ms blocks, 75% overlap; gated at -70 LUFS, then at 10 LU under the loudness of what's left.
                var blocks = new List<double>();
                for (int i = 0; i + 4 <= subs.Count; i++) blocks.Add((subs[i] + subs[i + 1] + subs[i + 2] + subs[i + 3]) / 4);
                if (blocks.Count == 0 && subs.Count > 0) blocks.Add(subs.Average()); // (shorter than 0.4 s)
                Func<double, double> lufs = z => -0.691 + 10 * Math.Log10(z);
                var loud = blocks.Where(z => z > 0 && lufs(z) > -70).ToList();
                if (loud.Count == 0) return;
                double gate = lufs(loud.Average()) - 10;
                var kept = loud.Where(z => lufs(z) > gate).ToList();
                r.Lufs = lufs(kept.Average());
                r.Seconds = Math.Min(r.Seconds, kept.Count * 0.1 + 0.3); // (what counts: the sound that isn't near silence)
            }
        }

        static LoudnessResult FromDisk(string key)
        {
            if (StoreFile == null) return null;
            try
            {
                if (stored == null)
                {
                    stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(StoreFile))
                        foreach (var line in File.ReadAllLines(StoreFile))
                        {
                            int cut = line.IndexOf("|file ", StringComparison.Ordinal);
                            if (cut > 0) stored[line.Substring(cut + 6)] = line.Substring(0, cut);
                        }
                }
                string value;
                if (!stored.TryGetValue(key, out value)) return null;
                var p = value.Split('|');
                var r = new LoudnessResult
                {
                    Lufs = p[0] == "-" ? double.NegativeInfinity : double.Parse(p[0], CultureInfo.InvariantCulture),
                    Peak = double.Parse(p[1], CultureInfo.InvariantCulture),
                    Seconds = double.Parse(p[2], CultureInfo.InvariantCulture)
                };
                return r;
            }
            catch { return null; }
        }

        // "loudness|peak|seconds|file <path|size|date>", 5000 lines at most (then it starts over).
        static void ToDisk(string key, LoudnessResult r)
        {
            if (StoreFile == null) return;
            try
            {
                string value = (double.IsInfinity(r.Lufs) || double.IsNaN(r.Lufs) ? "-" : r.Lufs.ToString("0.##", CultureInfo.InvariantCulture)) + "|" +
                    r.Peak.ToString("0.####", CultureInfo.InvariantCulture) + "|" + r.Seconds.ToString("0.#", CultureInfo.InvariantCulture);
                lock (byKey)
                {
                    if (stored == null) FromDisk(key);
                    Directory.CreateDirectory(Path.GetDirectoryName(StoreFile));
                    if (stored != null && stored.Count > 5000) { stored.Clear(); File.WriteAllText(StoreFile, ""); }
                    if (stored != null) stored[key] = value;
                    File.AppendAllText(StoreFile, value + "|file " + key + "\r\n");
                }
            }
            catch { }
        }
    }

    partial class ViewerWindow
    {
        // The level the file on screen plays at (1 = as it is), and where it is gliding to.
        double levelNow = 1, levelTarget = 1;
        string levelFile;          // the file levelNow is for
        bool levelLate;            // its measurement wasn't there when it started: it glides in, and only quieter or up to 100%
        readonly DispatcherTimer levelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        internal static string loudnessStoreOverride; // --loudness <file> (tests keep nothing otherwise)
        internal double levelBiggestStep; // tests: the largest change in one step of a glide (dB)

        // The volume for a slot's own player (Windows' player goes to 100% at most; FastViewer's sound does the rest).
        // (1.30.0: test runs give Windows' player no volume at all, so not a moment of a test can be heard: a guard that
        // turned it back down after each change let a few milliseconds through every time, heard as crackling.)
        double PlayVolume(Slot s) { return testRun && !ownSoundAloud ? 0 : Math.Min(1, volume * LevelOf(s)); }

        internal double LevelOf(Slot s)
        {
            if (!Loudness.On || s == null || s.File == null || !s.IsVideo) return 1;
            if (s.File == levelFile) return levelNow;
            LoudnessResult r;
            return Loudness.Known(s.File, out r) ? Loudness.GainOf(r) : 1;
        }

        // A file starts playing on screen: its level is known (preloaded) or it plays as it is for now.
        void LevelStart(Slot c)
        {
            if (c == null || c.File == null || c.File == levelFile) return;
            levelTimer.Stop();
            levelFile = c.File;
            LoudnessResult r;
            levelLate = !Loudness.Known(c.File, out r);
            levelNow = levelTarget = levelLate ? 1 : Loudness.GainOf(r);
            if (levelLate && Loudness.On) LevelWanted(c.File, true);
        }

        // A measurement came in: the file on screen that was playing as it is glides to its level (about 3 dB a second).
        void LevelArrived(string file)
        {
            var s = shown;
            if (!Loudness.On || s == null || s.File != file || file != levelFile || !levelLate) return;
            LoudnessResult r;
            if (!Loudness.Known(file, out r)) return;
            double g = Loudness.GainOf(r);
            // (Not louder than 100% unless FastViewer plays the sound already: going over would switch players mid-sound.)
            if (!OwnSound(s) && volume > 0) g = Math.Min(g, Math.Max(levelNow, 1 / volume));
            levelTarget = g;
            if (Math.Abs(levelTarget - levelNow) > 0.001) levelTimer.Start();
        }

        void LevelStep()
        {
            double now = 20 * Math.Log10(levelNow), want = 20 * Math.Log10(levelTarget), step = 3 * levelTimer.Interval.TotalSeconds;
            double next = Math.Abs(want - now) <= step ? want : now + Math.Sign(want - now) * step;
            levelBiggestStep = Math.Max(levelBiggestStep, Math.Abs(next - now));
            levelNow = Math.Pow(10, next / 20);
            if (next == want) levelTimer.Stop();
            var s = shown;
            if (s != null && s.File == levelFile && s.IsVideo && !s.Failed && !OwnSound(s)) s.Video.Volume = PlayVolume(s); // (FastViewer's own sound reads it 30 times a second)
        }

        // Send to Discord: the copy gets the level the file plays at (only when it is made anew; one already small
        // enough is copied as it is). Not measured yet: as it is.
        internal double ExportLevel(string file)
        {
            LoudnessResult r;
            return Loudness.On && Loudness.Known(file, out r) ? Loudness.GainOf(r) : 1;
        }

        // The setting was switched: the file on screen takes its new level at once.
        void LevelSettingChanged()
        {
            levelFile = null;
            levelTimer.Stop();
            if (shown != null && shown.IsVideo) LevelStart(shown);
            if (Loudness.On) foreach (var s in slots) if (s.File != null && s.IsVideo) LevelWanted(s.File, false);
            ApplyAudio();
            UpdateOwnSound();
        }

        void InitLoudness()
        {
            levelTimer.Tick += delegate { LevelStep(); };
            if (!testRun) Loudness.StoreFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer", "loudness.txt");
            else if (loudnessStoreOverride != null) Loudness.StoreFile = loudnessStoreOverride;
        }

        // ---------------------------------------------------------------- measuring in the background
        // One thread in Windows' background mode (lowest processor, disk and memory priority), so it never competes
        // with playing. The file on screen first, once its picture is up; the preloaded neighbours once the switching
        // has stopped for 0.6 s (like the track listening). A neighbour being measured gives way to the file on screen.
        static readonly List<string> levelQueue = new List<string>();
        static Thread levelThread;
        static volatile string levelFirst;
        internal static volatile string levelUp; // the file whose picture is on screen (not while the next one is still being revealed)

        void LevelPictureUp()
        {
            var s = shown;
            string up = s != null && s.IsVideo && !s.Failed && revealing != s ? s.File : null;
            if (up == null || up == levelUp) return;
            levelUp = up;
            if (levelThread == null) { StartLevelThread(); return; }
            lock (levelQueue) Monitor.Pulse(levelQueue);
        }

        void LevelWanted(string file, bool first)
        {
            if (!Loudness.On || file == null) return;
            LoudnessResult r;
            if (Loudness.Known(file, out r)) return;
            lock (levelQueue)
            {
                if (first) levelFirst = file;
                if (!levelQueue.Contains(file)) levelQueue.Add(file);
                if (levelThread != null) Monitor.Pulse(levelQueue);
                else if (levelUp != null) StartLevelThread(); // (not before the first picture is up: starting Windows' media tools then would slow the first open)
            }
        }

        void StartLevelThread()
        {
            lock (levelQueue)
            {
                if (levelThread != null || !Loudness.On) return;
                var self = this;
                levelThread = new Thread(() => self.LevelLoop()) { IsBackground = true, Name = "Loudness", Priority = ThreadPriority.Lowest };
                levelThread.SetApartmentState(ApartmentState.MTA);
                levelThread.Start();
            }
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr thread, int priority);

        void LevelLoop()
        {
            try { SetThreadPriority(GetCurrentThread(), 0x00010000); } catch { } // THREAD_MODE_BACKGROUND_BEGIN
            VideoConverter.Reading.Begin();
            while (true)
            {
                string file = null;
                lock (levelQueue)
                {
                    string first = levelFirst, up = levelUp;
                    bool quiet = (Stopwatch.GetTimestamp() - Interlocked.Read(ref learnHold)) / (double)Stopwatch.Frequency > 0.6;
                    // (Its tracks are known first: a Medal clip is measured as its tracks are mixed.)
                    Func<string, bool> ready = f => { int n = KnownTracks(f); return n >= 0 && (n < 2 || SoundMixing.Known(f) != null); };
                    if (first != null && !levelQueue.Contains(first)) levelFirst = first = null; // (done)
                    if (first != null && first == up && ready(first)) file = first;
                    else if (quiet && up != null) file = levelQueue.FirstOrDefault(f => ready(f) && f != null); // (never before the first picture is up)
                    if (file != null) levelQueue.Remove(file);
                    else { Monitor.Wait(levelQueue, levelQueue.Count > 0 ? 150 : 5000); continue; }
                }
                try
                {
                    string measuring = file;
                    // A neighbour gives way when the file on screen still needs measuring (it goes back in the queue).
                    var r = Loudness.Measure(file, () => { string f = levelFirst; if (f == null || f == measuring || f != levelUp) return false; lock (levelQueue) return levelQueue.Contains(f); });
                    if (r == null)
                    {
                        lock (levelQueue) if (!levelQueue.Contains(file) && levelFirst != file) levelQueue.Add(file);
                        Thread.Sleep(50);
                        continue;
                    }
                    if (testRun) Log("loudness " + Path.GetFileName(file) + ": " + r.Lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS, peak " + r.Peak.ToString("0.000", CultureInfo.InvariantCulture) + ", " + r.Ms + " ms");
                    Dispatcher.BeginInvoke(new Action(delegate { LevelArrived(measuring); }));
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- tests
        // --case loudness: measures every video/song in the folder (twice: the second time must come from memory) and
        // logs loudness, peak, the level it would play at, and how long measuring took.
        void HuntLoudness()
        {
            var list = files.Where(f => IsVideoFile(f) || IsAudioFile(f)).ToList();
            ThreadPool.QueueUserWorkItem(delegate
            {
                VideoConverter.Reading.Begin();
                var lines = new List<string>();
                foreach (var f in list)
                {
                    var w = Stopwatch.StartNew();
                    var r = Loudness.Measure(f, null);
                    long ms = w.ElapsedMilliseconds;
                    if (r == null) { lines.Add(Path.GetFileName(f) + ": not measured FAIL"); continue; }
                    double g = Loudness.GainOf(r);
                    lines.Add(Path.GetFileName(f) + ": " + (double.IsInfinity(r.Lufs) ? "silent" : r.Lufs.ToString("0.00", CultureInfo.InvariantCulture) + " LUFS") +
                        ", peak " + (20 * Math.Log10(Math.Max(1e-9, r.Peak))).ToString("0.0", CultureInfo.InvariantCulture) + " dBFS, " + r.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s counted, plays at " +
                        (20 * Math.Log10(g)).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB, took " + ms + " ms");
                }
                var again = Stopwatch.StartNew();
                foreach (var f in list) Loudness.Measure(f, null);
                lines.Add("second time (remembered): " + again.ElapsedMilliseconds + " ms for " + list.Count + " files");
                if (Loudness.StoreFile != null)
                {
                    Loudness.Forget();
                    var disk = Stopwatch.StartNew();
                    int found = list.Count(f => Loudness.Remembered(f) != null);
                    lines.Add("from " + Path.GetFileName(Loudness.StoreFile) + ": " + found + " of " + list.Count + " in " + disk.ElapsedMilliseconds + " ms");
                }
                // --pattern save: copies made with the level (as Send to Discord does) must measure about -18 LUFS (or as far as allowed).
                if (huntPattern == "save")
                    foreach (var f in list.Where(x => IsVideoFile(x) && !IsAudioFile(x)).Take(4))
                    {
                        var r = Loudness.Measure(f, null);
                        double g = Loudness.GainOf(r);
                        string to = Path.Combine(snapDir, "level " + Path.GetFileNameWithoutExtension(f) + ".mp4");
                        try
                        {
                            VideoConverter.Convert(f, to, null, TimeSpan.FromSeconds(10), 0, p => { }, new CancelFlag(), new ConvertOptions { Level = g, MaxLong = 640 });
                            var o = Loudness.Measure(to, null);
                            lines.Add("saved " + Path.GetFileName(f) + " with " + (20 * Math.Log10(g)).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " dB: copy " + o.Lufs.ToString("0.0", CultureInfo.InvariantCulture) +
                                " LUFS (expected " + (Math.Max(-70, r.Lufs) + 20 * Math.Log10(g)).ToString("0.0", CultureInfo.InvariantCulture) + " for the first 10 s' share), peak " + (20 * Math.Log10(Math.Max(1e-9, o.Peak))).ToString("0.0", CultureInfo.InvariantCulture) + " dBFS, " + VideoConverter.LastInfo);
                        }
                        catch (Exception e) { lines.Add("saving " + Path.GetFileName(f) + " failed: " + e.Message + " FAIL"); }
                    }
                Dispatcher.BeginInvoke(new Action(delegate { foreach (var l in lines) HuntLog(l); HuntDone(); }));
            });
        }

        // --case even: plays loud.mp4, quiet.mp4, ... in turn and logs what each is played at; then a file whose
        // measurement isn't there yet (memory forgotten) must glide in, in small steps.
        void HuntEven()
        {
            Loudness.On = true;
            muted = false;
            var names = files.Where(f => IsVideoFile(f) || IsAudioFile(f)).Select(Path.GetFileName).ToList();
            Func<string> state = () =>
            {
                var s = shown;
                if (s == null) return "nothing";
                LoudnessResult r;
                bool known = Loudness.Known(s.File, out r);
                return Path.GetFileName(s.File) + ": " + (known ? r.Lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS" : "not measured") + ", level " + (20 * Math.Log10(LevelOf(s))).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) +
                    " dB, Windows' player at " + s.Video.Volume.ToString("0.00", CultureInfo.InvariantCulture) + (s.Video.IsMuted ? " (silenced)" : "") + ", own sound " + OwnSound(s) +
                    (soundPlayer != null && OwnSound(s) ? " sounding " + soundPlayer.Sounding : "") + ", volume " + volume.ToString("0.00", CultureInfo.InvariantCulture);
            };
            int i = 0;
            Action next = null;
            next = () =>
            {
                if (i >= names.Count)
                {
                    // Late: forget everything, open the first file again: it must start at 0 dB and glide.
                    Loudness.Forget();
                    levelFile = null;
                    levelBiggestStep = 0;
                    string late = names[0];
                    ShowIndex(files.FindIndex(f => Path.GetFileName(f) == names[names.Count > 1 ? 1 : 0]), 1);
                    After(300, () => HuntOpen(late, () =>
                    {
                        if (!playing) TogglePlay();
                        HuntLog("late, at the start: " + state());
                        After(4000, () =>
                        {
                            HuntLog("late, 4 s later: " + state() + ", biggest step " + levelBiggestStep.ToString("0.00", CultureInfo.InvariantCulture) + " dB" + (levelBiggestStep <= 0.2 ? "" : " FAIL"));
                            SetVolume(0.5);
                            After(400, () =>
                            {
                                HuntLog("volume 50%: " + state());
                                Loudness.On = false; LevelSettingChanged();
                                After(400, () => { HuntLog("setting off: " + state()); Loudness.On = true; LevelSettingChanged(); After(400, () => { HuntLog("setting on: " + state()); HuntDone(); }); });
                            });
                        });
                    }));
                    return;
                }
                string name = names[i++];
                HuntOpen(name, () =>
                {
                    if (!playing) TogglePlay();
                    After(1500, () => { HuntLog(state()); next(); });
                });
            };
            // Measure everything first (as the preloading would), then go through the files.
            ThreadPool.QueueUserWorkItem(delegate
            {
                VideoConverter.Reading.Begin();
                foreach (var f in files.Where(f => IsVideoFile(f) || IsAudioFile(f)).ToList()) Loudness.Measure(f, null);
                Dispatcher.BeginInvoke(next);
            });
        }
    }
}
