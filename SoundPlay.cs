// FastViewer: its own sound for videos that need it (1.26.0). Windows' player (the MediaElement that shows the
// picture) plays exactly one sound track, for an .mp4 the last one, and never louder than 100%. For a video with
// several tracks (a Medal clip: game and microphone apart) or a volume over 100%, FastViewer therefore plays the
// sound itself: the tracks are decoded by Windows (Media Foundation), mixed here (Tracks.cs) and played through
// the PC's speakers, kept in step with the picture, whose player is silenced meanwhile. Every other video plays
// exactly as before, so switching between files is untouched.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FastViewer
{
    // Plays 48 kHz stereo sound through Windows (waveOut) and says how much of it has been heard.
    class SpeakerOut : IDisposable
    {
        const int Buffers = 12, Capacity = 4800; // frames in each buffer (0.1 s)
        IntPtr handle;
        readonly IntPtr[] headers = new IntPtr[Buffers], data = new IntPtr[Buffers];
        readonly bool[] busy = new bool[Buffers];
        static readonly int HeaderSize = IntPtr.Size == 8 ? 48 : 32, FlagsAt = IntPtr.Size == 8 ? 24 : 16;
        long written;
        public bool Open { get { return handle != IntPtr.Zero; } }

        public bool Start()
        {
            var format = new byte[18];
            BitConverter.GetBytes((short)1).CopyTo(format, 0);            // PCM
            BitConverter.GetBytes((short)2).CopyTo(format, 2);            // channels
            BitConverter.GetBytes(TrackMixer.Rate).CopyTo(format, 4);
            BitConverter.GetBytes(TrackMixer.Rate * 4).CopyTo(format, 8);
            BitConverter.GetBytes((short)4).CopyTo(format, 12);
            BitConverter.GetBytes((short)16).CopyTo(format, 14);
            if (waveOutOpen(out handle, -1, format, IntPtr.Zero, IntPtr.Zero, 0) != 0) { handle = IntPtr.Zero; return false; }
            for (int i = 0; i < Buffers; i++)
            {
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                data[i] = Marshal.AllocHGlobal(Capacity * 4);
            }
            return true;
        }

        // Frames written and not yet played.
        public int Queued { get { return (int)Math.Max(0, written - Played); } }
        public long Written { get { return written; } }

        public long Played
        {
            get
            {
                if (handle == IntPtr.Zero) return 0;
                var time = new byte[12];
                BitConverter.GetBytes(2).CopyTo(time, 0); // in samples
                if (waveOutGetPosition(handle, time, 12) != 0) return written;
                long value = BitConverter.ToUInt32(time, 4);
                return BitConverter.ToInt32(time, 0) == 4 ? value / 4 : value; // (some drivers only count bytes)
            }
        }

        // Takes as much as there is room for; returns how many frames it took.
        public int Write(short[] frames, int offset, int count)
        {
            int took = 0;
            while (count > 0 && handle != IntPtr.Zero)
            {
                int free = -1;
                for (int i = 0; i < Buffers && free < 0; i++)
                {
                    if (busy[i] && (Marshal.ReadInt32(headers[i], FlagsAt) & 1) != 0) { waveOutUnprepareHeader(handle, headers[i], HeaderSize); busy[i] = false; }
                    if (!busy[i]) free = i;
                }
                if (free < 0) break;
                int n = Math.Min(count, Capacity);
                Marshal.Copy(frames, offset * 2, data[free], n * 2);
                for (int b = 0; b < HeaderSize; b++) Marshal.WriteByte(headers[free], b, 0);
                Marshal.WriteIntPtr(headers[free], 0, data[free]);
                Marshal.WriteInt32(headers[free], IntPtr.Size, n * 4);
                if (waveOutPrepareHeader(handle, headers[free], HeaderSize) != 0 || waveOutWrite(handle, headers[free], HeaderSize) != 0) break;
                busy[free] = true;
                written += n;
                offset += n;
                count -= n;
                took += n;
            }
            return took;
        }

        // Drops what is waiting and starts counting from nothing again.
        public void Flush()
        {
            if (handle == IntPtr.Zero) return;
            waveOutReset(handle);
            for (int i = 0; i < Buffers; i++)
                if (busy[i]) { waveOutUnprepareHeader(handle, headers[i], HeaderSize); busy[i] = false; }
            written = 0;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            Flush();
            waveOutClose(handle);
            handle = IntPtr.Zero;
            for (int i = 0; i < Buffers; i++) { Marshal.FreeHGlobal(headers[i]); Marshal.FreeHGlobal(data[i]); }
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr handle, int device, byte[] format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr handle);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr handle, byte[] time, int size);
    }

    // What the window wants to hear right now. The window writes it (about 30 times a second while it matters);
    // the player's own thread reads it.
    class SoundWish
    {
        public string File;        // null: nothing
        public double[] Gains;     // how loud each of the file's tracks is mixed in (the file's order)
        public double Volume;      // 0..2 over all of it
        public double Position;    // where the picture is (seconds on its player's clock)...
        public long Stamp;         // ...at this moment (Stopwatch ticks)
        public bool Playing;
        public double Speed = 1;
        public double PicturePace = -1; // how fast the picture runs (when not the sound's pace: just around a speed change)
        public bool Silent;        // tests: everything runs, nothing is heard
        public double LoopLength;  // Repeat: the sound goes on from the start after this many seconds (the picture's length); 0 = no
        public bool Looping;       // the picture is starting over just now: its clock is unsteady for a moment
        public double LoopBase;    // the picture's clock at the start of the loop it is in (the lengths already played)
        public double PaceAt = -1, NextPace = 1; // Repeat (1.29.0): from this place on the picture's clock (the next loop's start) the sound takes NextPace
        public double SeamExtra;   // Repeat (1.29.0): seconds to skip at the seam on top of being behind (learnt: Windows' player gains a little after starting over)
    }

    static partial class VideoConverter
    {
        // The player below reads sound the same way saving does.
        internal static class Reading
        {
            public static IMFSourceReader Open(string file)
            {
                IMFSourceReader reader;
                return MFCreateSourceReaderFromURL(file, null, out reader) < 0 ? null : reader;
            }

            // Asks for one of the file's sound streams as 48 kHz stereo 16-bit; false if Windows won't.
            public static bool Select(IMFSourceReader reader, int stream)
            {
                if (reader.SetStreamSelection(stream, true) < 0) return false;
                IMFMediaType a, got;
                MFCreateMediaType(out a);
                a.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Audio);
                a.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFAudioFormat_PCM);
                a.SetUINT32(ref G.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                a.SetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, 2);
                a.SetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, TrackMixer.Rate);
                a.SetUINT32(ref G.MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
                a.SetUINT32(ref G.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, TrackMixer.Rate * 4);
                if (reader.SetCurrentMediaType(stream, IntPtr.Zero, a) < 0 || reader.GetCurrentMediaType(stream, out got) < 0) { reader.SetStreamSelection(stream, false); return false; }
                int channels, rate;
                got.GetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, out channels);
                got.GetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, out rate);
                Marshal.ReleaseComObject(got);
                if (channels == 2 && rate == TrackMixer.Rate) return true;
                reader.SetStreamSelection(stream, false);
                return false;
            }

            // How long the file is, in seconds (0: not known).
            public static double Length(IMFSourceReader reader) { return Duration(reader) / 1e7; }
            public static void DeselectAll(IMFSourceReader reader) { reader.SetStreamSelection(ALL_STREAMS, false); }
            public static void JumpTo(IMFSourceReader reader, long time) { Seek(reader, Math.Max(0, time)); }

            // The next piece of sound: false at a read error. sample stays null at the end of a stream (ended says so).
            public static bool Next(IMFSourceReader reader, out int stream, out long time, out IMFSample sample, out bool ended, out bool changed)
            {
                int flags;
                sample = null;
                ended = changed = false;
                if (reader.ReadSample(ANY_STREAM, 0, out stream, out flags, out time, out sample) < 0 || (flags & 1) != 0) return false;
                ended = (flags & 2) != 0;
                changed = (flags & 0x20) != 0;
                return true;
            }

            public static void Shape(IMFSourceReader reader, int stream, out int channels, out int rate)
            {
                channels = rate = 0;
                IMFMediaType now;
                if (reader.GetCurrentMediaType(stream, out now) < 0) return;
                now.GetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, out channels);
                now.GetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, out rate);
                Marshal.ReleaseComObject(now);
            }

            public static bool Begin() { return MFStartup(0x00020070, 0) >= 0; }
            public static void End() { MFShutdown(); }
        }
    }

    // One thread that plays what SoundWish asks for, in step with the picture. Nothing that goes wrong in it ends
    // it: it lets go of the file and the speakers and starts over. (The window replaces it if it stops answering.)
    class SoundPlayer
    {
        volatile SoundWish wish = new SoundWish();
        readonly Thread thread;
        public Thread Worker { get { return thread; } } // (tests: its stack when it stops answering)
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool quit;
        // What it measured, for tests: how far the sound was from the picture (seconds), and what went wrong.
        public volatile int Starts, Resyncs, Failures;
        public volatile string Trouble;
        public volatile float LastDrift, WorstDrift, Level;
        public volatile int StartMs;       // from being asked to the first sound written
        public volatile int LongestGap;    // the longest silence a restart made, in ms (tests)
        public readonly List<string> Notes = new List<string>(); // tests: why it restarted (how far off in ms, g = around a speed change)
        bool gapOpen;
        int leadMs;
        readonly Stopwatch gapWatch = new Stopwatch();
        long lastSound, beat;              // (Stopwatch times) the last real sound handed to the speakers; the last sign of life
        const double latency = 0.05;       // about what Windows and the sound card add after the sound is handed over

        // Sound was handed to the speakers within the last third of a second.
        public bool Sounding { get { return Since(Interlocked.Read(ref lastSound)) < 0.35; } }
        // Seconds since the thread last went round (it goes round every few milliseconds while it lives).
        public double BeatAge { get { return Since(Interlocked.Read(ref beat)); } }

        static double Since(long stamp) { return stamp == 0 ? 1e9 : (Stopwatch.GetTimestamp() - stamp) / (double)Stopwatch.Frequency; }

        public SoundPlayer()
        {
            Interlocked.Exchange(ref beat, Stopwatch.GetTimestamp());
            thread = new Thread(Run) { IsBackground = true, Name = "Sound", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Ask(SoundWish w) { wish = w; wake.Set(); }
        public void Quit() { quit = true; wake.Set(); }
        public void ResetStats() { Starts = Resyncs = Failures = LongestGap = Splices = Wraps = LongestStarve = SeamSkips = 0; LastDrift = WorstDrift = MaxNudge = 0; Trouble = null; lock (Notes) Notes.Clear(); }

        static double Now(SoundWish w)
        {
            double since = (Stopwatch.GetTimestamp() - w.Stamp) / (double)Stopwatch.Frequency;
            return w.Position + Math.Min(since, 0.5) * (w.PicturePace > 0 ? w.PicturePace : w.Speed); // (the window stopped telling: don't run off)
        }

        SpeakerOut speaker;
        IMFSourceReader reader;
        string open, gainsKey;
        readonly Dictionary<int, int> slotOf = new Dictionary<int, int>();       // stream -> the file's track
        readonly Dictionary<int, double> offset = new Dictionary<int, double>(); // stream: seconds to add to its stamps to get the picture's clock
        Dictionary<int, int> mixerSlots = new Dictionary<int, int>();
        TrackMixer mixer;
        double origin;        // the picture's clock at the mixer's frame 0
        double mediaEnd;      // the picture's clock at the end of what was handed to the speakers
        double length;        // of the file, seconds (0: unknown)
        double wrapBase;      // Repeat: the picture's clock at the file's start in the loop being read (lengths already read)
        readonly HashSet<int> atWrap = new HashSet<int>(); // streams that have reached the loop's end
        // Repeat (1.29.0): per stream, the sound that runs past the picture's end ([stamp, frames]), kept for the start of the
        // next loop; the streams whose first sound in the new loop hasn't come yet; the loop the kept sound belongs to.
        readonly Dictionary<int, List<long[]>> tails = new Dictionary<int, List<long[]>>();
        readonly Dictionary<long[], IMFSample> tailSamples = new Dictionary<long[], IMFSample>();
        readonly HashSet<int> freshStreams = new HashSet<int>();
        double tailBase;
        bool seamPending;
        public volatile int SeamSkips; // tests
        public List<Dictionary<int, long>> LoopFrames; readonly Dictionary<int, long> lastAdded = new Dictionary<int, long>(); // tests: frames of each stream put in per loop
        public long DroppedFrames; // tests: frames of the file's sound left out after a start (should stay 0)

        void Stash(int stream, long stamp, IMFSample sample)
        {
            List<long[]> list;
            if (!tails.TryGetValue(stream, out list)) tails[stream] = list = new List<long[]>();
            var key = new[] { stamp, (long)TrackMixer.FramesOf(sample) };
            list.Add(key);
            tailSamples[key] = sample;
        }

        void ClearTail(int stream)
        {
            List<long[]> list;
            if (!tails.TryGetValue(stream, out list)) return;
            foreach (var k in list) { IMFSample smp; if (tailSamples.TryGetValue(k, out smp)) { try { Marshal.ReleaseComObject(smp); } catch { } tailSamples.Remove(k); } }
            list.Clear();
        }

        void ClearTails() { foreach (var s in tails.Keys.ToList()) ClearTail(s); freshStreams.Clear(); }
        // 1.29.0: the sound no longer waits at a loop for the picture to start over (that was the silence before a restart,
        // and the re-sync after it the doubled bit): it runs straight on into the next loop, and the window starts the
        // picture over just early enough to meet it (PlanLoop, Stretch.cs). Kept switchable for comparing.
        internal static bool LoopWaits = false;
        short[] held;         // the next loop's first piece, waiting for the picture to start over
        readonly Stopwatch loopRelease = new Stopwatch(); // since the loop's sound began
        long heldTime;
        bool running, finished, pendingLead, first, catching;
        double runSpeed = 1;
        readonly TimeStretch stretcher = new TimeStretch(); // the same pitch at any speed (Stretch.cs)
        short[] stretched = new short[0];
        bool stretchMode = TimeStretch.KeepPitch;
        public volatile int LongestStarve; bool starving; readonly Stopwatch starveWatch = new Stopwatch(); // tests
        public volatile float LastStep = 1;  // tests: the pace the sound was read at last (speed x nudge)
        // Repeat (1.29.0): the heard tracks' sound spans [SoundFrom, SoundTo) on the picture's clock (their own first and last
        // sample, edit-list offsets included). A loop of the sound is SoundTo - SoundFrom long: every sample of every track
        // plays, and the next loop's sound begins right after the last one. The window fits the picture to it.
        public double SoundFrom, SoundTo; public volatile string SpanFile; // (doubles: a float is a few samples off after a minute)
        public double LoopSpan { get { return SoundTo > SoundFrom ? SoundTo - SoundFrom : 0; } }
        readonly Dictionary<int, double> spanStart = new Dictionary<int, double>(), spanEnd = new Dictionary<int, double>();

        public static Action<string> DebugStep; // (tests: where the sound thread is)
        static void Dbg(string what) { var d = DebugStep; if (d != null) d(what); }

        // The mixer frame where this track's sound ends in the loop being read (Repeat only; nothing after it is put in).
        long CutFor(int stream)
        {
            double end;
            if (wish.LoopLength <= 0.5 || SpanFile != open || !spanEnd.TryGetValue(stream, out end)) return long.MaxValue;
            return (long)Math.Round((end + wrapBase - origin) * TrackMixer.Rate);
        }

        void ProbeSpans()
        {
            Dbg("probe spans: start (length " + length.ToString("0.000") + ")");
            var probeWatch = Stopwatch.StartNew();
            spanStart.Clear(); spanEnd.Clear();
            SoundFrom = SoundTo = 0; SpanFile = null;
            if (reader == null || slotOf.Count == 0) return;
            try
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    // First: the first sample of each track. Then from 1.5 s before the end: the last one.
                    VideoConverter.Reading.JumpTo(reader, pass == 0 ? 0 : (long)(Math.Max(0, length - 1.5) * 1e7));
                    var done = new HashSet<int>();
                    for (int reads = 0; reads < 4000 && done.Count < slotOf.Count; reads++)
                    {
                        int stream; long stamp; IMFSample sample; bool ended, changed;
                        if (!VideoConverter.Reading.Next(reader, out stream, out stamp, out sample, out ended, out changed)) break;
                        if (sample != null)
                        {
                            if (slotOf.ContainsKey(stream))
                            {
                                double at = stamp / 1e7 + offset[stream];
                                if (pass == 0) { if (!spanStart.ContainsKey(stream)) { spanStart[stream] = at; done.Add(stream); } }
                                else spanEnd[stream] = at + TrackMixer.FramesOf(sample) / (double)TrackMixer.Rate;
                            }
                            Marshal.ReleaseComObject(sample);
                        }
                        if (pass == 1 && ended) done.Add(stream);
                    }
                }
                // (Where each track's real sound ends: what the file says, without the encoder's padding after it.)
                foreach (var pr in slotOf)
                {
                    double es = VideoInfo.EditStart(open, "soun", pr.Value), ed = VideoInfo.EditDuration(open, "soun", pr.Value);
                    if (ed > 0.5 && spanStart.ContainsKey(pr.Key) && spanEnd.ContainsKey(pr.Key)) spanEnd[pr.Key] = Math.Min(spanEnd[pr.Key], spanStart[pr.Key] + es + ed);
                }
                if (spanStart.Count == slotOf.Count && spanEnd.Count == slotOf.Count)
                {
                    SoundFrom = spanStart.Values.Min();
                    SoundTo = spanEnd.Values.Max();
                    SpanFile = open;
                }
            }
            catch (Exception e) { SoundFrom = SoundTo = 0; Dbg("probe spans: " + e.Message); }
            Dbg("probe spans: done in " + probeWatch.ElapsedMilliseconds + " ms, from " + SoundFrom.ToString("0.000") + " to " + SoundTo.ToString("0.000"));
        }

        public volatile float OutputDelay = 0.15f; // seconds from handing sound over to hearing it (queued + Windows and the card): a new speed is heard that much later
        public float[] Tap; public readonly int[] TapAt = new int[1]; // tests: what went to the speakers before the volume (left)
        short[] chunk = new short[0];
        readonly Stopwatch asked = Stopwatch.StartNew(), sinceStart = Stopwatch.StartNew(), sinceSpeed = Stopwatch.StartNew();
        int settle;
        // What was handed to the speakers lately: frames, and the seconds of the picture's clock they cover (they
        // were made at different speeds, so what is still waiting can't be worked out from one speed).
        readonly List<double[]> handed = new List<double[]>();

        void Run()
        {
            if (!VideoConverter.Reading.Begin()) { Trouble = "Media Foundation didn't start"; return; }
            try
            {
                while (!quit)
                {
                    Interlocked.Exchange(ref beat, Stopwatch.GetTimestamp());
                    try { Step(); }
                    catch (Exception e)
                    {
                        // Whatever it was (the file went away, the speakers were unplugged...): let go of everything and try again.
                        Failures++;
                        Trouble = e.GetType().Name + ": " + e.Message + " [" + (e.StackTrace ?? "").Split((char)10).Take(2).Aggregate("", (a, b) => a + b.Trim() + " ") + "]";
                        LetGo();
                        wake.WaitOne(250);
                    }
                }
            }
            finally
            {
                LetGo();
                VideoConverter.Reading.End();
            }
        }

        void LetGo()
        {
            ClearTails();
            try { if (reader != null) Marshal.ReleaseComObject(reader); } catch { }
            try { if (speaker != null) speaker.Dispose(); } catch { }
            reader = null;
            speaker = null;
            open = null;
            mixer = null;
            running = false;
            Interlocked.Exchange(ref lastSound, 0);
        }

        // A stretch of a mixed piece as the speakers get it: read at `step` frames a time (the speed), at the volume.
        // Returns how many frames it put into chunk.
        int Render(short[] piece, int from, int count, double step, float volume)
        {
            int frames = piece.Length / 2, made = Math.Max(1, (int)(count / step));
            if (chunk.Length < made * 2) chunk = new short[made * 2];
            for (int i = 0; i < made; i++)
            {
                double at = from + i * step;
                int a = Math.Min(frames - 1, (int)at), b = Math.Min(frames - 1, a + 1);
                float f = (float)(at - a);
                chunk[i * 2] = TrackMixer.Limit((piece[a * 2] + (piece[b * 2] - piece[a * 2]) * f) * volume);
                chunk[i * 2 + 1] = TrackMixer.Limit((piece[a * 2 + 1] + (piece[b * 2 + 1] - piece[a * 2 + 1]) * f) * volume);
            }
            return made;
        }

        // Makes the sound at frame `to` of a piece start as the sound at `from` and turn into its own over 10 ms.
        static bool CrossFade(short[] piece, int from, int to, int frames)
        {
            const int Over = 480;
            if (from < 0 || to < 0 || from + Over > frames || to + Over > frames) return false;
            var old = new short[Over * 2];
            Array.Copy(piece, from * 2, old, 0, Over * 2);
            for (int i = 0; i < Over; i++)
            {
                float f = (i + 0.5f) / Over, g = (float)Math.Sqrt(f), h = (float)Math.Sqrt(1 - f); // (equal power: no dip in loudness)
                piece[(to + i) * 2] = TrackMixer.Limit(old[i * 2] * h + piece[(to + i) * 2] * g);
                piece[(to + i) * 2 + 1] = TrackMixer.Limit(old[i * 2 + 1] * h + piece[(to + i) * 2 + 1] * g);
            }
            return true;
        }

        short lastLeft, lastRight; // the last sample the speakers got
        int skipLeft;              // frames still to be left out to get back in step
        bool smooth;               // the next piece doesn't follow on from it: glide over, or it clicks
        readonly Stopwatch sinceSplice = Stopwatch.StartNew();
        public volatile int Splices;      // tests: slivers left out or played twice to stay with the picture
        public volatile int Wraps;        // tests: times the sound went on from the start by itself
        public volatile float MaxNudge;   // tests: the most the reading pace was bent outside a speed change (0.004 = 0.4%)
        public List<string> Trace;        // tests: how far off it was every 50 ms after each start
        long traceNext;

        void Hand(short[] frames, int count, double seconds)
        {
            if (count > 0 && Tap != null) for (int i = 0; i < count && TapAt[0] < Tap.Length; i++) Tap[TapAt[0]++] = frames[i * 2]; // (tests: everything the speakers get, silence too)
            if (count > 0 && wish.Silent) Array.Clear(frames, 0, count * 2);
            HandOut(frames, count, seconds);
        }

        void HandOut(short[] frames, int count, double seconds)
        {
            if (count <= 0) return;
            if (smooth)
            {
                int over = Math.Min(count, 144); // 3 ms
                for (int i = 0; i < over; i++)
                {
                    float f = (i + 1) / (float)over;
                    frames[i * 2] = (short)(lastLeft + (frames[i * 2] - lastLeft) * f);
                    frames[i * 2 + 1] = (short)(lastRight + (frames[i * 2 + 1] - lastRight) * f);
                }
            }
            lastLeft = frames[(count - 1) * 2];
            lastRight = frames[(count - 1) * 2 + 1];
            smooth = seconds == 0 && count > 0 ? smooth : false; // (after a repeated sliver the piece itself is a joint too)
            int done = 0;
            var patience = Stopwatch.StartNew();
            while (done < count && !quit)
            {
                int took = speaker.Write(frames, done, count - done);
                done += took;
                if (took > 0) patience.Restart();
                else if (patience.ElapsedMilliseconds > 1500) throw new Exception("the speakers stopped taking sound");
                if (done < count) Thread.Sleep(3);
            }
            handed.Add(new[] { (double)count, seconds });
            if (handed.Count > 200) handed.RemoveRange(0, 100);
        }

        // The seconds of the picture's clock that are still waiting in the speakers, and (with the latency) also in
        // Windows and the sound card after them. 1.29.0: that last part counted at the speed it was made at (it used to
        // be counted at the new speed, which put the reckoning 50 ms off at each speed change for a moment).
        double Waiting(bool andLatency = false)
        {
            double frames = speaker.Queued + (andLatency ? latency * TrackMixer.Rate : 0), seconds = 0;
            for (int i = handed.Count - 1; i >= 0 && frames > 0; i--)
            {
                double part = Math.Min(1, frames / Math.Max(1, handed[i][0]));
                seconds += handed[i][1] * part;
                frames -= handed[i][0];
            }
            if (andLatency && frames > 0) seconds += frames / TrackMixer.Rate * runSpeed; // (just started: nothing before it)
            return seconds;
        }

        void Stop()
        {
            if (running && speaker != null) speaker.Flush();
            handed.Clear();
            stretcher.Reset();
            running = false;
            Interlocked.Exchange(ref lastSound, 0);
        }

        void Step()
        {
            var w = wish;
            bool stale = (Stopwatch.GetTimestamp() - w.Stamp) / (double)Stopwatch.Frequency > 0.7;
            if (w.File == null || !w.Playing || stale || w.Gains == null)
            {
                Stop();
                if (w.File == null && (reader != null || speaker != null)) LetGo();
                wake.WaitOne(w.File == null ? 2000 : 30);
                return;
            }
            string key = string.Join(",", w.Gains.Select(g => g > 0 ? "1" : "0"));
            if (open != w.File || gainsKey != key || reader == null)
            {
                // Another file, or other tracks: a reader with just the tracks that are heard.
                Stop();
                if (reader != null) { Marshal.ReleaseComObject(reader); reader = null; }
                open = w.File;
                gainsKey = key;
                asked.Restart();
                first = true;
                Starts++;
                slotOf.Clear();
                offset.Clear();
                mixer = null;
                var layout = SoundMixing.Layout(w.File);
                Dbg("open reader " + Path.GetFileName(w.File));
                reader = VideoConverter.Reading.Open(w.File);
                Dbg("reader open");
                if (reader == null || layout.Streams == null) { open = null; throw new Exception("can't read " + Path.GetFileName(w.File)); }
                length = VideoConverter.Reading.Length(reader);
                VideoConverter.Reading.DeselectAll(reader);
                for (int k = 0; k < layout.Streams.Length && k < w.Gains.Length; k++)
                    if (w.Gains[k] > 0 && VideoConverter.Reading.Select(reader, layout.Streams[k]))
                    {
                        slotOf[layout.Streams[k]] = k;
                        offset[layout.Streams[k]] = layout.Offsets != null ? layout.Offsets[k] : 0;
                    }
                if (slotOf.Count == 0 && w.Gains.Any(g => g > 0)) Trouble = "no sound track could be decoded in " + Path.GetFileName(w.File); // (every track at 0 is just silence)
                ProbeSpans(); // (1.29.0: where each heard track really begins and ends: a loop plays all of it)
            }
            if (slotOf.Count == 0) { wake.WaitOne(200); return; }
            if (speaker == null)
            {
                speaker = new SpeakerOut();
                if (!speaker.Start()) { speaker = null; throw new Exception("no sound device"); }
            }

            // Another speed: just read at another pace from here on. No stop, no jump: nothing to hear but the change.
            double target = w.PaceAt >= 0 && running && mediaEnd >= w.PaceAt - 0.0005 ? w.NextPace : w.Speed; // (a change saved for the next loop: exactly at its start)
            if (running && Math.Abs(target - runSpeed) > 0.001) { runSpeed = target; sinceSpeed.Restart(); catching = w.PaceAt < 0; }
            // The picture's own player stumbles for a moment at a speed change (it stands still for 0.1-0.8 s), which puts the
            // sound ahead: for the next seconds, until it is back in step, more is put up with and the nudge is stronger.
            bool grace = sinceSpeed.ElapsedMilliseconds < 2500 || (catching && sinceSpeed.ElapsedMilliseconds < 9000);
            bool grace0 = sinceSpeed.ElapsedMilliseconds < 2500; // (never bent then: see below)

            double expected = Now(w);
            // (What the stretcher holds is pushed but not heard yet: the same pitch at another speed, Stretch.cs.)
            bool keep = TimeStretch.KeepPitch;
            if (keep != stretchMode) { stretchMode = keep; stretcher.Reset(); if (running) { speaker.Flush(); handed.Clear(); running = false; } }
            double heard = running ? mediaEnd - Waiting(true) - (stretchMode ? stretcher.PendingSeconds : 0) : 0;
            if (speaker != null) OutputDelay = (float)(speaker.Queued / (double)TrackMixer.Rate + latency);
            double drift = expected - heard; // over 0: the sound is behind the picture
            if (catching && sinceSpeed.ElapsedMilliseconds >= 2500 && Math.Abs(drift) < 0.03) catching = false;
            if (running && !finished)
            {
                LastDrift = (float)drift;
                if (Trace != null && sinceStart.ElapsedMilliseconds < 1500 && sinceStart.ElapsedMilliseconds >= traceNext) { traceNext = sinceStart.ElapsedMilliseconds + 50; lock (Trace) Trace.Add(sinceStart.ElapsedMilliseconds + ":" + (drift * 1000).ToString("0")); }
                if (settle > 0) settle--;
                else if (held == null && !w.Looping && Math.Abs(drift) > Math.Abs(WorstDrift)) WorstDrift = (float)drift; // (not while waiting for the picture to start over)
            }
            // Far off (a jump in the video): start again from where the picture is. Right after a start the picture's
            // clock is still settling, and around a speed change it stumbles: more is put up with then, and the
            // sound catches up or waits by reading a little faster or slower instead.
            // (Waiting for the picture to start over, or on its last frame: anything. Just after the loop's sound began: the
            // picture may stand still once more (at other speeds), and the sound follows that at once.)
            bool justLooped = loopRelease.IsRunning && loopRelease.ElapsedMilliseconds < 1200;
            // (1.29.0: starting again from the picture's place replays what was just heard when the sound is ahead, and cuts
            // when it is behind; up to half a second ahead / a quarter behind the gap is closed smoothly instead. A real
            // jump in the video is seen by the window, which stops the sound first.)
            double farAt = held != null ? 1.5 : justLooped ? 0.11 : finished ? 0.3 : w.Looping ? 1.5 : grace ? 0.5 : drift < 0 ? 0.5 : 0.25;
            bool far = Math.Abs(drift) > farAt && (sinceStart.ElapsedMilliseconds > 350 || Math.Abs(drift) > 1.5);
            if (!running || far)
            {
                smooth = true;
                if (running)
                {
                    Resyncs++;
                    lock (Notes) { if (Notes.Count < 40) Notes.Add((drift * 1000).ToString("0") + (grace ? "g" : "") + (w.Looping ? "L" : "") + "@" + runSpeed.ToString("0.##") + ">" + w.Speed.ToString("0.##") + " at " + (w.Position - w.LoopBase).ToString("0.000") + " s, " + sinceStart.ElapsedMilliseconds + " ms after its start"); }
                    if (!gapOpen) { gapOpen = true; gapWatch.Restart(); }
                }
                speaker.Flush();
                handed.Clear();
                runSpeed = w.Speed;
                double from = expected + 0.03 * runSpeed;
                mixer = new TrackMixer();
                lastAdded.Clear();
                var map = new Dictionary<int, int>();
                foreach (var pair in slotOf) map[pair.Key] = mixer.AddTrack(1);
                mixerSlots = map;
                origin = from;
                stretcher.Reset();
                mediaEnd = from;
                running = true;
                double loopLen = w.LoopLength > 0.5 ? w.LoopLength : 0, fileFrom = from;
                if (loopLen > 0)
                {
                    // (Where in its loop: the window counts the picture's clock on by the sound's loop length, LoopBase.)
                    double span = LoopSpan > 0 && SpanFile == open ? LoopSpan : loopLen;
                    fileFrom = from - w.LoopBase;
                    if (fileFrom < 0 || fileFrom >= span + 0.5) fileFrom = ((from % span) + span) % span;
                    if (fileFrom > SoundTo - 0.02 && SpanFile == open) fileFrom -= span; // (at the very end: the next loop, from its start)
                }
                wrapBase = from - fileFrom;
                atWrap.Clear();
                ClearTails();
                finished = loopLen == 0 && length > 0 && from > length - 0.05; // (at the very end: nothing left to play)
                settle = 40;
                pendingLead = true;
                skipLeft = 0;
                held = null;
                sinceStart.Restart();
                traceNext = 0;
                if (Trace != null) lock (Trace) Trace.Add("| start at " + from.ToString("0.000") + ":");
                if (!finished)
                {
                    try { VideoConverter.Reading.JumpTo(reader, (long)((fileFrom - offset.Values.Max() - 0.05) * 1e7)); }
                    catch { finished = true; } // (a place Windows won't jump to: silence until the picture is somewhere else)
                }
            }

            // (Tests: how long the speakers ran dry while playing, the longest time.)
            if (running && !finished && sinceStart.ElapsedMilliseconds > 300 && speaker.Queued == 0) { if (!starving) { starving = true; starveWatch.Restart(); } }
            else if (starving) { starving = false; LongestStarve = Math.Max(LongestStarve, (int)starveWatch.ElapsedMilliseconds); }
            // Keep about a tenth of a second waiting in the speakers.
            if (speaker.Queued < TrackMixer.Rate / 10 && !finished)
            {
                long time;
                // Small pieces (about 60 ms of sound as it will be heard), so a change is heard at once at any speed.
                short[] piece;
                double loopNow = w.LoopLength > 0.5 ? w.LoopLength : 0;
                if (held != null)
                {
                    // The start of the next loop is ready: it goes to the speakers the moment the picture starts over.
                    if (loopNow == 0) { held = null; finished = true; return; }
                    // (After starting over Windows' player may sit on the first frame for a moment, longer at other speeds:
                    // the sound begins when its clock moves, together with the picture.)
                    if (w.LoopBase < wrapBase - 0.01 || w.Position < wrapBase + 0.005) { wake.WaitOne(4); return; }
                    piece = held;
                    time = heldTime;
                    held = null;
                    loopRelease.Restart();
                    pendingLead = true;
                }
                else
                {
                    // (Pieces never run over into the next loop: that part waits for the picture.)
                    int most = Math.Max(1024, (int)(TrackMixer.Rate * 0.06 * runSpeed));
                    if (w.PaceAt >= 0) { long toSeam = (long)Math.Round((w.PaceAt - origin) * TrackMixer.Rate) - mixer.Position; if (toSeam >= 256 && toSeam < most) most = (int)toSeam; }
                    long wrapFrame = (long)Math.Round((wrapBase - origin) * TrackMixer.Rate);
                    if (LoopWaits && loopNow > 0 && w.LoopBase < wrapBase - 0.01 && mixer.Position < wrapFrame) most = (int)Math.Min(most, wrapFrame - mixer.Position);
                    piece = mixer.Take(false, out time, most);
                }
                if (piece == null)
                {
                    int stream;
                    long stamp;
                    IMFSample sample;
                    bool ended, changed;
                    if (!VideoConverter.Reading.Next(reader, out stream, out stamp, out sample, out ended, out changed)) { reader = null; throw new Exception("reading the sound stopped"); }
                    int slot;
                    bool mine = mixerSlots.TryGetValue(stream, out slot);
                    if (mine && changed)
                    {
                        int ch, hz;
                        VideoConverter.Reading.Shape(reader, stream, out ch, out hz);
                        mixer.Shape(slot, ch, hz);
                    }
                    double loopLen = w.LoopLength > 0.5 ? w.LoopLength : 0;
                    if (sample != null)
                    {
                        double fileTime = stamp / 1e7 + (mine ? offset[stream] : 0);
                        float gainNow = mine ? (float)w.Gains[slotOf[stream]] : 0;
                        // 1.29.0: every track is read to its own end, nothing is cut at the picture's end; the first sound
                        // of each track in a new loop is placed exactly at its own time in that loop (no sliding to follow
                        // on), so each loop's sound is the file's sound, all of it, one after the other.
                        if (mine && freshStreams.Contains(stream))
                        {
                            freshStreams.Remove(stream);
                            mixer.Add(slot, stamp + (long)((offset[stream] + wrapBase - origin) * 1e7), sample, gainNow, true, CutFor(stream), -1, false);
                        }
                        else if (mine && !atWrap.Contains(stream)) mixer.Add(slot, stamp + (long)((offset[stream] + wrapBase - origin) * 1e7), sample, gainNow, false, CutFor(stream), -1, false);
                        Marshal.ReleaseComObject(sample);
                    }
                    if (mine && ended) { if (loopLen > 0) atWrap.Add(stream); else mixer.End(slot); }
                    if (loopLen > 0 && atWrap.Count >= mixerSlots.Count)
                    {
                        // Every track has reached the end of the loop: on from the start, as one continuous sound.
                        atWrap.Clear();
                        tailBase = wrapBase;
                        freshStreams.Clear();
                        foreach (var sk in mixerSlots.Keys) freshStreams.Add(sk);
                        if (LoopFrames != null) { var counts = new Dictionary<int, long>(); foreach (var pr in mixerSlots) { long a = mixer.AddedFrames(pr.Value), b; lastAdded.TryGetValue(pr.Key, out b); counts[pr.Key] = a - b; lastAdded[pr.Key] = a; } lock (LoopFrames) LoopFrames.Add(counts); }
                        wrapBase += LoopSpan > 0 && SpanFile == open ? LoopSpan : loopLen; // (the sound's own length: all of every track)
                        // (No skipping at the seam any more: the picture is fitted to the sound, PlanLoop.)
                        VideoConverter.Reading.JumpTo(reader, (long)(Math.Max(0, -offset.Values.Max()) * 1e7));
                        Wraps++;
                        return;
                    }
                    if (!mixer.AllEnded) return;
                    piece = mixer.Take(true, out time, Math.Max(1024, (int)(TrackMixer.Rate * 0.06 * runSpeed)));
                    if (piece == null) { finished = true; return; }
                }
                // The piece starts at origin + time on the picture's clock.
                if (LoopWaits && loopNow > 0 && w.LoopBase < wrapBase - 0.01 && origin + time / 1e7 >= wrapBase - 0.002)
                {
                    // The next loop's start, while the picture still shows the end of this one: kept until it starts over.
                    held = piece;
                    heldTime = time;
                    return;
                }
                int frames = piece.Length / 2, skip = 0;
                if (pendingLead)
                {
                    // The first sound after a start: wait (as silence) until the picture gets there, or skip what it has
                    // already passed. That can be more than one piece (the first read after a loop takes longer): whole
                    // pieces are dropped until one reaches the picture, so the sound begins exactly in step.
                    leadMs = 0;
                    double ahead = origin + time / 1e7 - (Now(wish) + latency * runSpeed + (stretchMode ? stretcher.PendingSeconds : 0)); // (1.29.0: what the stretcher still holds plays first)
                    if (ahead > 0)
                    {
                        int quiet = (int)Math.Min(TrackMixer.Rate / 2, ahead / runSpeed * TrackMixer.Rate);
                        Hand(new short[quiet * 2], quiet, 0);
                        leadMs = quiet * 1000 / TrackMixer.Rate;
                    }
                    else
                    {
                        skip = (int)(-ahead * TrackMixer.Rate);
                        if (skip >= frames - 64) { mediaEnd = origin + time / 1e7 + frames / (double)TrackMixer.Rate; return; }
                    }
                    pendingLead = false;
                }
                // The seam of a loop (the end joined to the start): if the sound is behind the picture there, it skips
                // forward by that much right at the seam, where the cut is not heard. (Ahead of it: the picture is simply
                // started over earlier next time, PlanLoop.) Never a repeat.
                if (seamPending && origin + time / 1e7 >= wrapBase - 0.002)
                {
                    seamPending = false;
                    double want = drift + w.SeamExtra;
                    lock (Notes) { if (Notes.Count < 40) Notes.Add("seam " + (drift * 1000).ToString("0") + "+" + (w.SeamExtra * 1000).ToString("0") + (skipLeft > 0 ? " busy" : "")); }
                    if (want > 0.012 && skipLeft == 0 && !LoopWaits) { skipLeft = (int)(Math.Min(want, 0.2) * TrackMixer.Rate); SeamSkips++; }
                }
                if (skipLeft > 0)
                {
                    // A sliver to leave out (see below): from the start of this piece, or all of it and more of the next.
                    if (skipLeft >= frames - skip - 64) { DroppedFrames += frames - skip; smooth = true; skipLeft -= frames - skip; mediaEnd = origin + time / 1e7 + frames / (double)TrackMixer.Rate; return; }
                    // Within this piece: the sound that is left out fades into the sound after it over 10 ms (a crossfade, so
                    // nothing clicks or jumps); with too little piece left for that, the short glide at the joint.
                    if (!CrossFade(piece, skip, skip + skipLeft, frames)) smooth = true;
                    DroppedFrames += skipLeft;
                    skip += skipLeft;
                    skipLeft = 0;
                }
                // Staying with the picture. Normally the sound starts in step and only the two clocks' tiny difference
                // has to be made up: reading at most 0.4% faster or slower, which can't be heard. If it is off by more
                // (25 ms and up), a sliver of at most 30 ms is left out or played twice, with the joint smoothed: the
                // pitch never moves. In the 2.5 s after a speed change the sound plays at exactly the new speed, never
                // bent (1.29.0; it used to read up to 20% slower to wait for Windows' player, which stands still for a
                // moment at a change: heard as a slow-down after letting go of a 2x hold). The two changes are timed
                // against each other instead (TimeSpeedChange in the window), so they land together.
                // (Same pitch: from 0.9 s after a change, when both have taken it, what is left of the gap (a few hundredths:
                // Windows' stand-still varies) is closed at most 3% faster or slower, which isn't heard at the same pitch.)
                bool fresh = sinceSpeed.ElapsedMilliseconds < 900 || (!stretchMode && grace0);
                // (Round 4: at most 1.5% at the same pitch (0.5% the old way), only in the 4 s after a speed change and only when
                // they are over 20 ms apart; otherwise 0.4% as always. Round 3 had 3%, which could be heard.)
                bool closeGap = !fresh && sinceSpeed.ElapsedMilliseconds < 4000 && Math.Abs(drift) > 0.02;
                double gapCap = stretchMode ? 0.015 : 0.005; // (the old way changes the pitch with it)
                double nudge = settle > 0 || Math.Abs(drift) < 0.004 || fresh || w.Looping ? 0 // (starting over: the picture comes to the sound)
                    : closeGap ? Math.Max(-gapCap, Math.Min(gapCap, drift * 0.5)) : Math.Max(-0.004, Math.Min(0.004, drift * 0.1));
                if (!grace && !closeGap && Math.Abs(nudge) > MaxNudge) MaxNudge = (float)Math.Abs(nudge);
                int again = 0;
                if (!grace && !w.Looping && settle == 0 && Math.Abs(drift) > 0.25 && skipLeft == 0 && sinceSplice.ElapsedMilliseconds > 200 && frames > 600)
                {
                    if (drift > 0) skipLeft = (int)(Math.Min(drift, 0.03) * TrackMixer.Rate);           // behind the picture: a sliver left out (from the next piece on)
                    else again = Math.Min(frames - 300, (int)(Math.Min(-drift, 0.03) * TrackMixer.Rate));                // ahead of it: a sliver twice
                    Splices++;
                    sinceSplice.Restart();
                }
                double step = runSpeed * (1 + nudge);
                LastStep = (float)step;
                // (Tests: everything as usual, the sound kept for measuring, then silence instead of it.)
                float volume = w.Silent && Tap == null ? 0 : w.Silent ? 1 : (float)w.Volume;
                double power = 0; // (tests: how loud the mix is before the volume, as a running average)
                for (int i = 0; i < frames; i += 4) power += (double)piece[i * 2] * piece[i * 2];
                Level = (float)(Level * 0.6 + Math.Sqrt(power / Math.Max(1, frames / 4)) * 0.4);
                if (stretchMode)
                {
                    // The same pitch (Stretch.cs): the stretcher takes the original sound and hands out what is ready.
                    // At 1x it hands the sound through untouched, and the tiny pace corrections (at most 0.4%, as before
                    // 1.29) are made the old way afterwards: the stretcher's search would put the sound up to 15 ms off.
                    bool plain = Math.Abs(runSpeed - 1) < 0.001 && Math.Abs(nudge) <= 0.005;
                    stretcher.Speed = plain ? 1 : step;
                    if (again > 0)
                    {
                        stretcher.Push(piece, skip, again, 0); // (a sliver twice: it covers no time of the picture)
                        if (CrossFade(piece, skip + again, skip, frames)) smooth = false;
                    }
                    stretcher.Push(piece, skip, frames - skip, (frames - skip) / (double)TrackMixer.Rate);
                    double covered;
                    int out1;
                    if (plain)
                    {
                        int n = stretcher.Pull(ref stretched, 1, out covered, null, null, true);
                        var exact = new short[n * 2];
                        Array.Copy(stretched, exact, n * 2);
                        out1 = n > 0 ? Render(exact, 0, n, step, volume) : 0;
                    }
                    else out1 = stretcher.Pull(ref chunk, volume, out covered);
                    Hand(chunk, out1, covered);
                }
                else
                {
                    // (Render first: it may make chunk bigger, and Hand must get the new one.)
                    int made = again > 0 ? Render(piece, skip, again, step, volume) : 0;
                    if (made > 0)
                    {
                        Hand(chunk, made, 0);
                        // The repeated sliver ends where it began: what would have followed it fades into that place.
                        if (CrossFade(piece, skip + again, skip, frames)) smooth = false;
                    }
                    made = Render(piece, skip, frames - skip, step, volume);
                    Hand(chunk, made, (frames - skip) / (double)TrackMixer.Rate);
                }
                mediaEnd = origin + time / 1e7 + frames / (double)TrackMixer.Rate;
                if (first) { first = false; StartMs = (int)asked.ElapsedMilliseconds; }
                if (gapOpen) { gapOpen = false; LongestGap = Math.Max(LongestGap, (int)gapWatch.ElapsedMilliseconds + leadMs); }
                Interlocked.Exchange(ref lastSound, Stopwatch.GetTimestamp());
                return;
            }
            wake.WaitOne(4);
        }
    }

    partial class ViewerWindow
    {
        internal SoundPlayer soundPlayer;
        internal int soundChoice;              // 0: every track (at its Sound volume); n: only the n-th one
        readonly DispatcherTimer soundTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(30) };
        static readonly Dictionary<string, int> soundTrackCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal static bool ownSoundOff;      // --own-sound-off: everything as before 1.26 (for comparing)

        // How many sound tracks a file has, if that's known yet (it's read off the window's thread when the file is loaded).
        static int KnownTracks(string file)
        {
            int n;
            lock (soundTrackCounts) return file != null && soundTrackCounts.TryGetValue(file, out n) ? n : -1;
        }

        // Called for each video as it is loaded (also the hidden ones): finds out, in the background, what its
        // sound tracks are, so nothing has to be worked out when it comes on screen.
        void LearnSound(string file)
        {
            if (ownSoundOff || KnownTracks(file) >= 0) return;
            lock (learnQueue)
            {
                if (!learnQueue.Contains(file)) learnQueue.Add(file);
                if (learnThread != null) { Monitor.Pulse(learnQueue); return; }
                learnThread = new Thread(LearnLoop) { IsBackground = true, Name = "Sound tracks", Priority = ThreadPriority.BelowNormal };
                learnThread.SetApartmentState(ApartmentState.MTA);
                learnThread.Start();
            }
        }

        // One file at a time, so opening videos never has to share the disk and processor with several of these:
        // the file that is wanted on screen first and at once, its neighbours once the switching has stopped.
        static readonly List<string> learnQueue = new List<string>();
        static Thread learnThread;
        static readonly List<string> listenQueue = new List<string>(); // (files with 2+ tracks still to be listened to)
        static volatile string learnFirst, learnShown;
        static long learnHold; // Stopwatch time of the last switch

        void SoundWanted(string file)
        {
            learnFirst = file;
            Interlocked.Exchange(ref learnHold, Stopwatch.GetTimestamp());
            lock (learnQueue) Monitor.Pulse(learnQueue);
        }

        void LearnLoop()
        {
            while (true)
            {
                string file = null, listen = null;
                lock (learnQueue)
                {
                    // Counting a file's tracks is a glance at its header: at once, the wanted file first. Listening to them
                    // (Windows decodes 12 s of each) waits until that file's picture is on screen, so opening it is never
                    // held up; the neighbours' turn comes once the switching has stopped.
                    string first = learnFirst, up = learnShown;
                    bool quiet = (Stopwatch.GetTimestamp() - Interlocked.Read(ref learnHold)) / (double)Stopwatch.Frequency > 0.6;
                    if (first != null && learnQueue.Contains(first)) file = first;
                    else if (learnQueue.Count > 0) file = learnQueue[0];
                    if (file != null) learnQueue.Remove(file);
                    else if (up != null && listenQueue.Contains(up)) listen = up;
                    else if (quiet && first == up && listenQueue.Count > 0) listen = listenQueue[0];
                    if (listen != null) listenQueue.Remove(listen);
                    if (file == null && listen == null) { Monitor.Wait(learnQueue, listenQueue.Count > 0 ? 100 : 5000); continue; }
                }
                try
                {
                    if (listen != null)
                    {
                        SoundMixing.Layout(listen); // (0.1-0.3 s; remembered, also for next time)
                        string heard = listen;
                        Dispatcher.BeginInvoke(new Action(delegate { if (shown != null && shown.File == heard) UpdateOwnSound(); }));
                        continue;
                    }
                    int n = VideoInfo.SoundTrackCount(file);
                    lock (soundTrackCounts)
                    {
                        if (soundTrackCounts.Count > 2000) soundTrackCounts.Clear();
                        soundTrackCounts[file] = n;
                    }
                    if (n >= 2) lock (learnQueue) { if (SoundMixing.Known(file) == null && !listenQueue.Contains(file)) listenQueue.Add(file); }
                    if (n >= 2) Dispatcher.BeginInvoke(new Action(delegate { if (shown != null && shown.File == file) UpdateOwnSound(); }));
                }
                catch { }
            }
        }

        // Does FastViewer play this slot's sound itself? Then the picture's own player stays silent.
        internal bool OwnSound(Slot s)
        {
            if (ownSoundOff || s == null || !s.IsVideo || s.Failed || s.File == null) return false;
            if (volume * LevelOf(s) > 1.001 && !muted) return true;  // louder than Windows' player goes (also: Even out volume)
            if (holdWarm && s == shown && playing) return true; // (a press that may become a hold: Hold.cs)
            // Another speed: FastViewer's sound changes speed without a gap. Back at 1x it stays for the two seconds a speed
            // change counts as going on (a slider dragged through 1x), then Windows' player takes the sound back.
            if (Math.Abs(speed - 1) > 0.001 || (s.File == soundSpeedFile && (SpeedChanging || playing))) return true; // (1.29.0: back at 1x it stays until the video is paused or another file shows: no hand-over mid-sound)
            if (KnownTracks(s.File) < 2) return false;
            var layout = SoundMixing.Known(s.File);
            return layout == null || layout.Sources.Length >= 2; // (not known yet: quiet for the moment that takes, not the wrong track)
        }

        // What of the file's tracks is heard: all of them at their Sound volumes, or only the chosen one.
        double[] HeardGains(SoundLayout layout)
        {
            var gains = new double[layout.Count];
            if (layout.Count == 1) { gains[0] = 1; return gains; }
            if (layout.Sources.Length < 2) { gains[layout.MixTrack >= 0 ? layout.MixTrack : 0] = 1; return gains; }
            // (While trimming you hear what will be saved: every track at its Sound volume, whatever the track button says.)
            int only = !trimming && soundChoice >= 1 && soundChoice <= layout.Sources.Length ? soundChoice - 1 : -1;
            for (int k = 0; k < layout.Sources.Length; k++)
                gains[layout.Sources[k]] = only >= 0 ? (k == only ? 1 : 0) : SoundMixing.Volume(k) / 100.0;
            return gains;
        }

        // Starts or stops FastViewer's own sound for what is on screen, and keeps telling it where the picture is.
        internal void UpdateOwnSound()
        {
            var s = shown;
            string up = s != null && revealing != s ? s.File : null; // (its picture is on screen: its tracks may be listened to now)
            if (up != learnShown) { learnShown = up; lock (learnQueue) Monitor.Pulse(learnQueue); }
            if (up != null && up != levelUp) LevelPictureUp();
            // While a video plays, an eye is kept on whether its picture really moves (WatchPicture).
            bool watch = s != null && s.IsVideo && !s.Failed && playing && IsVisible;
            if (watch != rateTimer.IsEnabled) { if (watch) { rateAt = -1; rateTimer.Start(); } else rateTimer.Stop(); }
            if (!playing && s != null && s.File == soundSpeedFile && !OtherSpeed && !SpeedChanging) soundSpeedFile = null; // (paused at 1x: Windows' player has the sound again when it plays on)
            bool own = OwnSound(s) && !gridOpen && IsVisible;
            if (own)
            {
                if (!soundTimer.IsEnabled) soundTimer.Start();
                TellSound();
            }
            else
            {
                if (soundTimer.IsEnabled) soundTimer.Stop();
                if (soundPlayer != null)
                {
                    soundPlayer.Ask(new SoundWish());
                    // Hidden (closed with the X while FastViewer stays ready in the background): the sound thread goes
                    // too, so opening the next file always starts with a fresh one.
                    if (!IsVisible) { soundPlayer.Quit(); soundPlayer = null; }
                }
                if (s != null && s.IsVideo && !s.Failed && s.Video.IsMuted != muted && (playing || s.IsAudio)) s.Video.IsMuted = muted;
            }
            UpdateTracksButton();
        }

        // Closing with the X hides FastViewer when it stays ready in the background: the same program goes on, so
        // whatever state its sound was in would still be there for the next file. Everything about it ends here.
        internal void EndOwnSound()
        {
            soundTimer.Stop();
            rateTimer.Stop();
            if (speedTimer != null) speedTimer.Stop();
            if (soundPlayer != null) { soundPlayer.Ask(new SoundWish()); soundPlayer.Quit(); soundPlayer = null; }
            soundSpeedFile = soundQuietFile = soundAsked = soundSeenFile = null;
            soundGo = false;
            soundMoves = 0;
            soundLoopBase = 0;
            soundFallback = false;
            rateAt = -1;
            rateStill = rateOff = rateWakes = 0;
            pictureRate = 0;
        }

        void TellSound()
        {
            var s = shown;
            if (!OwnSound(s)) { UpdateOwnSound(); return; }
            if (OtherSpeed) soundSpeedFile = s.File;
            if (revealing != s && learnShown != s.File) { learnShown = s.File; lock (learnQueue) Monitor.Pulse(learnQueue); } // (its picture is up now)
            var layout = SoundMixing.Known(s.File);
            if (layout == null)
            {
                if (KnownTracks(s.File) < 0) LearnSound(s.File); // (a boost on a file that was loaded before it was wanted)
                else if (KnownTracks(s.File) < 2 && soundAsked != s.File) { soundAsked = s.File; string file = s.File; ThreadPool.QueueUserWorkItem(delegate { SoundMixing.Layout(file); }); }
            }
            // A sound thread that has stopped answering (stuck inside Windows) is left behind and a new one takes over.
            if (soundPlayer != null && soundPlayer.BeatAge > 2) { soundPlayer.Quit(); soundPlayer = null; soundRecoveries++; Log("the sound thread was replaced"); }
            if (soundPlayer == null) soundPlayer = new SoundPlayer();
            // A picture that stands still (looking for the place after a jump, a slow disk) has its sound wait with
            // it. Around a speed change Windows' player stumbles for a moment: the sound plays on through that.
            double at = s.Video.Position.TotalSeconds;
            long tick = Stopwatch.GetTimestamp();
            double set = s.Video.SpeedRatio, pace = set > 0 ? set : 1;
            bool changing = (tick - speedAppliedAt) / (double)Stopwatch.Frequency < 2.5;
            // The sound starts only once the picture is seen to run (its place moved on, twice in a row, by what its speed
            // makes likely): right after a jump, a loop or a start Windows' player says where it will be while it still
            // stands there, and sound started then would be ahead and have to catch up. A jump stops it at once.
            // With Repeat on the sound loops by itself, straight from the end into the start, so a loop has no gap: the
            // picture's clock is counted on across loops (soundLoopBase: the lengths already played) and the player wraps
            // its reading at the same length. While the picture restarts (it stands still for a moment, then makes up)
            // the sound plays on; what is left over is corrected without being heard.
            if (s.File != soundSeenFile || !loop) soundLoopBase = 0;
            // (Windows' player shows position 0 a moment before it says the video ended: that is the loop, not a jump.)
            var dur0 = s.Video.NaturalDuration;
            if (loop && dur0.HasTimeSpan && s.File == soundSeenFile && at < 0.5 && soundSeenAt - soundLoopBase >= dur0.TimeSpan.TotalSeconds - 0.6) NoteLoop(s);
            at += soundLoopBase;
            // (Starting over: the last moments before the end, where Windows' player may stand on the last frame for a while,
            // and the first second after.)
            var dur = s.Video.NaturalDuration;
            bool planned = !SoundPlayer.LoopWaits; // (1.29.0: the picture is started over in time, PlanLoop: only the moment around it counts)
            bool nearEnd = loop && dur.HasTimeSpan && at - soundLoopBase >= dur.TimeSpan.TotalSeconds - (planned ? 0.05 * pace : 0.5 * Math.Max(1, pace));
            bool looping = soundGo && ((tick - soundLoopTick) / (double)Stopwatch.Frequency < (planned ? 0.2 : 1.0) || nearEnd || pictureHeld);
            WatchStall(s, s.Video.Position.TotalSeconds, tick);
            if (speedTrace != null && speedTrace.Count < 400) speedTrace.Add(((tick - speedTraceFrom) * 1000 / Stopwatch.Frequency) + ":" + s.Video.Position.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "/" + pace + "/" + SoundPace(s) + "/" + (soundPlayer == null ? 0 : soundPlayer.LastDrift * 1000).ToString("0"));
            bool catchMove = CatchUpTick(s.Video.Position.TotalSeconds, at, tick); // (Stretch.cs: the picture being moved forward to the sound)
            double since = (tick - soundSeenTick) / (double)Stopwatch.Frequency, moved = at - soundSeenAt;
            bool can = playing && !scrubbing && s.Ready && (revealing != s || looping) && s.File == soundSeenFile; // (the picture comes first: the sound starts once it is on screen)
            bool jumped = !looping && !catchMove && (moved < -0.05 || moved > since * pace + 0.25);
            if (!can || jumped) { if (soundGo && testRun) Log("own sound held: " + (jumped ? "jump " + moved.ToString("0.000") + " in " + since.ToString("0.000") + " s" : "can't: playing " + playing + " ready " + s.Ready + " revealing " + (revealing == s) + " looping " + looping)); soundGo = false; soundMoves = 0; soundMovedAt = tick; }
            else if (looping) soundMovedAt = tick; // (standing still while it starts over isn't stopping)
            else if (moved > 0)
            {
                soundMovedAt = tick;
                // (At a steady pace only: right after a loop Windows' player runs ahead for a moment to make up for its start.)
                bool steady = since > 0.005 && moved > since * pace * 0.6 && moved < since * pace * 1.5;
                if (soundGo) { }
                else if (!steady) soundMoves = 0;
                else if (++soundMoves >= 3) soundGo = true;
            }
            else if (!catchMove && (tick - soundMovedAt) / (double)Stopwatch.Frequency > (changing ? 1.2 : 0.12)) { if (soundGo && testRun) Log("own sound held: picture still at " + at.ToString("0.000")); soundGo = false; soundMoves = 0; } // stands still
            soundSeenAt = at;
            soundSeenTick = tick;
            soundSeenFile = s.File;
            if (!catchMove) CatchUpPicture(s, moved, since, pace, changing, looping || nearEnd || loop, tick);
            CheckDeferred(s);
            WatchChange(tick);
            PlanLoop(s, s.Video.Position.TotalSeconds, pace, tick); // (Stretch.cs: seamless loops)
            bool wanted = can && soundGo;
            soundPlayer.Ask(new SoundWish
            {
                File = s.File,
                Gains = layout == null || layout.Count == 0 ? null : HeardGains(layout),
                Volume = muted ? 0 : volume * LevelOf(s),
                Position = at,
                Stamp = tick,
                Playing = wanted,
                LoopLength = loop ? LoopLenFor(s) : 0, // (1.29.0: the sound's own length when known, Stretch.cs)
                Looping = looping,
                LoopBase = soundLoopBase,
                SeamExtra = SeamExtraFor(s, pace),
                Speed = TellPace(s), // (Stretch.cs: around a speed change the sound may take it a moment before or after the picture)
                PicturePace = pace,
                PaceAt = deferredPace > 0 ? deferPaceAt : -1,
                NextPace = deferredPace > 0 ? deferredPace : 1,
                Silent = testRun && !ownSoundAloud
            });

            // Windows' player goes quiet for as long as FastViewer's own sound is there; for a file with several tracks
            // at once (it would play the wrong one). Should FastViewer's sound stay away for a second and a half
            // while the picture plays, Windows' player is let back in until it returns: never silence.
            bool there = soundPlayer.Sounding;
            if (there || !wanted || s.File != soundQuietFile) { soundQuietSince = tick; soundQuietFile = s.File; }
            bool lost = (tick - soundQuietSince) / (double)Stopwatch.Frequency > 1.5;
            if (lost != soundFallback) { soundFallback = lost; if (lost) { soundFallbacks++; Log("own sound stayed away: Windows' player sounds meanwhile"); } }
            bool quiet = lost ? muted : there || KnownTracks(s.File) >= 2 || s.Video.IsMuted;
            if (s.Video.IsMuted != quiet) s.Video.IsMuted = quiet;
        }

        double soundSeenAt;
        long soundMovedAt, soundQuietSince, soundSeenTick;
        internal long soundLoopTick; // when the video on screen last started over (OnVideoEnded)
        internal double soundLoopBase; // the picture's clock counted on across loops (seconds of the lengths already played)

        // The video on screen started over (Repeat): its clock is counted on. Once per loop, whichever notices it first
        // (OnVideoEnded, or TellSound seeing the position go back to 0 at the end).
        internal void NoteLoop(Slot s)
        {
            long now = Stopwatch.GetTimestamp();
            if ((now - soundLoopTick) / (double)Stopwatch.Frequency < 0.5) return;
            soundLoopTick = now;
            soundLoopBase += LoopLenFor(s); // (the sound's loop: all of every track, Stretch.cs)
            if (loopTapAt != null && soundPlayer != null) loopTapAt.Add(soundPlayer.TapAt[0]); // (tests: --case loops)
        }
        string soundQuietFile, soundSpeedFile, soundAsked, soundSeenFile;
        bool soundGo;
        int soundMoves;
        internal bool soundFallback;
        internal int soundFallbacks, soundRecoveries;

        // ---------------------------------------------------------------- speed changes, and a picture that stops
        long speedAppliedAt, speedAskedAt;
        DispatcherTimer speedTimer;
        internal int speedApplies; // tests: how often Windows' player really got a new speed

        // Every new speed makes Windows' player stand still for a moment (0.1-0.8 s) and start again from where it
        // was; set many times in a row it locks up. So a single change (a key, a click) goes to it at once, and a
        // run of changes (a dragged slider, a held key) only once the run has stopped for a moment: the video
        // plays on undisturbed at the speed it had and then takes the last one asked for, in one step.
        void ApplySpeedSoon()
        {
            long now = Stopwatch.GetTimestamp();
            double sinceAsked = (now - speedAskedAt) * 1000.0 / Stopwatch.Frequency, sinceApplied = (now - speedAppliedAt) * 1000.0 / Stopwatch.Frequency;
            speedAskedAt = now;
            var s = shown;
            if (s == null || !s.IsVideo || !playing) return; // (a video that starts gets the speed then)
            if (sinceAsked >= 400 && sinceApplied >= 400 && (speedTimer == null || !speedTimer.IsEnabled)) { ApplySpeedNow(); return; }
            if (speedTimer == null)
            {
                speedTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(140) };
                speedTimer.Tick += delegate { ApplySpeedNow(); };
            }
            speedTimer.Stop();
            speedTimer.Start();
        }

        void ApplySpeedNow()
        {
            if (speedTimer != null) speedTimer.Stop();
            var s = shown;
            if (s == null || !s.IsVideo || !playing || s.Failed) return;
            if (Math.Abs(s.Video.SpeedRatio - speed) < 0.0001)
            {
                // (Back to the speed the picture still has while it was waiting for a slower one: nothing to do.)
                if (pictureSpeedTimer != null && pictureSpeedTimer.IsEnabled) { pictureSpeedTimer.Stop(); soundPaceHeld = -1; soundPaceUntilPicture = false; }
                deferredPace = -1; // (back to the speed it has: nothing saved for the loop start)
                return;
            }

            if (TimeSpeedChange(s)) { UpdateOwnSound(); return; } // (Stretch.cs: the picture gets it a moment later)
            SetPictureSpeed(s);
        }

        void SetPictureSpeed(Slot s)
        {
            if (Math.Abs(s.Video.SpeedRatio - speed) < 0.0001) { soundPaceHeld = soundPaceUntilPicture ? -1 : soundPaceHeld; return; }
            double from = s.Video.SpeedRatio;

            s.Video.SpeedRatio = speed;
            speedApplies++;
            speedAppliedAt = Stopwatch.GetTimestamp();
            rateRepairs = 0;
            ResetPictureWatch();
            StartStallWatch(s, from); // (Stretch.cs: how long it stands still, for next time)
            UpdateOwnSound();
        }
        // A speed change is under way: asked for or given to Windows' player within the last two seconds.
        bool SpeedChanging
        {
            get
            {
                long now = Stopwatch.GetTimestamp();
                return (speedTimer != null && speedTimer.IsEnabled) || (now - speedAskedAt) / (double)Stopwatch.Frequency < 2 || (now - speedAppliedAt) / (double)Stopwatch.Frequency < 2;
            }
        }

        // Every 0.4 s while a video plays: does the picture move? Only when it has stood still for two whole
        // seconds while it is meant to play (not at the end, not while a place is looked for, and never while the
        // speed is being changed or for two seconds after: Windows' player stands still by itself then) is
        // Windows' player woken: first just told to play again, then paused and played, and if the picture still
        // doesn't move the file is opened afresh at the same place. Nothing here ever moves the video to another
        // place. A picture that runs clearly FASTER than the speed that was set (a speed that didn't arrive) gets
        // the speed once more, at most twice.
        readonly DispatcherTimer rateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        double rateAt = -1, pictureRate;
        long rateTick, rateFixedAt;
        int rateStill, rateOff, rateWakes, rateRepairs;
        internal int pictureWakes, pictureReopens, speedRepairs, missedEnds;
        double rateEndAt = -1;
        int rateEndStill;

        void ResetPictureWatch()
        {
            rateAt = -1;
            rateStill = rateOff = 0;
            pictureRate = 0;
        }

        void WatchPicture()
        {
            var s = shown;
            long now = Stopwatch.GetTimestamp();
            if (s == null || !s.IsVideo || s.Failed || !s.Ready || !playing || scrubbing || revealing == s || trimDrag != '\0') { ResetPictureWatch(); rateEndAt = -1; return; }
            double at = s.Video.Position.TotalSeconds, set = s.Video.SpeedRatio > 0 ? s.Video.SpeedRatio : 1;
            var d = s.Video.NaturalDuration;
            if (d.HasTimeSpan && at >= d.TimeSpan.TotalSeconds - Math.Max(1.5, 0.75 * set))
            {
                // At the end. Windows' player doesn't always say that a video has ended (seen when the speed was changed just
                // then): standing on its last moment for 0.8 s counts as ended, so Repeat and "play the next" go on.
                ResetPictureWatch();
                if (at != rateEndAt) { rateEndAt = at; rateEndStill = 0; return; }
                if (++rateEndStill < 2) return; // 0.8 s
                rateEndStill = 0;
                rateEndAt = -1;
                missedEnds++;
                Log("the video stands at its end without having said so: taken as ended");
                OnVideoEnded(s);
                return;
            }
            rateEndAt = -1;
            rateEndStill = 0;
            // (While the speed is being changed Windows' player stands still by itself for up to 0.8 s: still for 2 s is more
            // than that, and waking only says "play", never moves the video, so that check runs then too.)
            if ((now - rateFixedAt) / (double)Stopwatch.Frequency < 2.5) { ResetPictureWatch(); return; }
            if (rateAt < 0 || at < rateAt - 0.05) { rateAt = at; rateTick = now; rateStill = 0; return; }
            double seconds = (now - rateTick) / (double)Stopwatch.Frequency;
            if (seconds < 0.2) return;
            double rate = (at - rateAt) / seconds, part = rate / set;
            rateAt = at;
            rateTick = now;
            if (part > 2.5) { ResetPictureWatch(); return; } // (a jump in the video, not a speed)
            pictureRate = rate;
            if (part < 0.05)
            {
                rateOff = 0;
                if (++rateStill < 5) return; // 2 s
                rateStill = 0;
                rateFixedAt = now;
                rateWakes++;
                if (rateWakes <= 2) WakePicture(s, rateWakes == 2);
                else { rateWakes = 0; ReopenPicture(s); }
                return;
            }
            rateStill = 0;
            rateWakes = 0;
            if (part > 1.4 && rateRepairs < 2 && !SpeedChanging)
            {
                if (++rateOff < 3) return;
                rateOff = 0;
                rateRepairs++;
                rateFixedAt = now;
                speedRepairs++;
                Log("the picture moves at " + rate.ToString("0.00") + "x instead of " + set + "x: speed set again");
                s.Video.SpeedRatio = set > 1 ? set - 0.01 : set + 0.01; // (the same number again would be ignored)
                s.Video.SpeedRatio = speed;
                speedAppliedAt = now;
            }
            else rateOff = 0;
        }

        void WakePicture(Slot s, bool harder)
        {
            pictureWakes++;
            Log("the picture stood still at " + s.Video.Position.TotalSeconds.ToString("0.00") + " of " + (s.Video.NaturalDuration.HasTimeSpan ? s.Video.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.00") : "?") + " s, speed " + s.Video.SpeedRatio + ": Windows' player " + (harder ? "paused and played" : "told to play"));
            if (harder) s.Video.Pause();
            s.Video.Play();

            s.Video.SpeedRatio = speed;
        }

        // The file opened afresh in the same player, at the same place (the way a saved rotation does it).
        void ReopenPicture(Slot s)
        {
            pictureReopens++;
            Log("the picture still stands still: file opened afresh");
            var at = s.Video.Position;
            string file = s.File;
            ++s.Token;
            try
            {
                s.Video.Close();
                s.SeekOnOpen = at;
                s.Ready = false;
                s.Video.Source = new Uri(file);
                s.Video.Pause();
                s.Video.Play();
                s.Video.SpeedRatio = speed;
                speedAppliedAt = Stopwatch.GetTimestamp();
            }
            catch { }
        }
        internal static bool ownSoundAloud; // --own-sound-aloud: a test the user asked to hear
        internal static string soundStoreOverride;

        // Test (--hunt <out> --case ownsound; folder with clip.mp4 = a Medal clip with three sound tracks, and
        // one.mp4 = one track): FastViewer's own sound starts, stays with the picture through pausing, jumping
        // and other speeds, and isn't used where it isn't needed. Nothing is heard (the test runs silent).
        // Test (--hunt <out> --case trimsound, with --settings; folder with two.mp4 = two sound tracks, 440 and 880 Hz,
        // equally loud): the trim bar's Sound sliders are heard at once, paused or in "Play selection", whatever
        // the track button says. Measured as the loudness of what the player mixes (silent test).
        void HuntTrimSound()
        {
            List<object> rows = null;
            float one = 0;
            Func<float> level = () => soundPlayer == null ? 0 : soundPlayer.Level;
            Action<string, double, int> expect = (what, times, wait) => After(wait, () =>
            {
                float now = level();
                bool ok = one > 0 && Math.Abs(now / one - times) < 0.12 * Math.Max(1, times);
                HuntLog(what + ": loudness " + now.ToString("0") + " = " + (one > 0 ? (now / one).ToString("0.00") : "?") + " x one track (should be " + times.ToString("0.00") + "), sounding " +
                    (soundPlayer != null && soundPlayer.Sounding) + ", playing " + playing + (ok ? "" : " FAIL"));
            });
            muted = false;
            HuntOpen("two.mp4", () =>
            {
                if (playing) TogglePlay();
                StartTrim();
                Until(() => trimLayout != null, 10000, () =>
                {
                    HuntLog("two.mp4: " + trimLayout.Count + " tracks, sources " + string.Join("/", trimLayout.Names) + ", Sound button " + F<FrameworkElement>("TrimSoundBtn").IsVisible);
                    soundChoice = 1; // "only track 1" picked on the video bar earlier: the trim bar still plays what will be saved
                    var hook = huntMenuHook;
                    huntMenuHook = (m, b) => { rows = m.Items.Cast<object>().ToList(); m.Items.Clear(); var host = new Menu(); foreach (var r in rows) host.Items.Add(r); host.Visibility = Visibility.Hidden; root.Children.Add(host); host.UpdateLayout(); };
                    ShowSoundMenu();
                    huntMenuHook = hook;
                    var first = FindAll<Slider>((MenuItem)rows[0]).First();
                    var second = FindAll<Slider>((MenuItem)rows[1]).First();
                    second.Value = 0;
                    PlaySelection();
                    After(1500, () => { one = level(); HuntLog("Play selection, track 2 at 0: loudness " + one.ToString("0") + (one > 500 ? "" : " FAIL")); second.Value = 100; });
                    expect("0.5 s after track 2 to 100", 1.41, 2000);
                    After(2050, () => first.Value = 200);
                    expect("0.5 s after track 1 to 200", 2.24, 2550);
                    After(2600, () => { first.Value = 100; second.Value = 0; });
                    expect("0.5 s after 100 / 0", 1.0, 3100);
                    After(3150, () => { second.Value = 100; EndTrim(); if (!playing) TogglePlay(); });
                    expect("trim bar closed, video bar says only track 1", 1.0, 4200);
                    After(4300, () => { soundChoice = 0; UpdateOwnSound(); });
                    expect("video bar: every track", 1.41, 5000);
                    After(5200, HuntDone);
                });
            });
        }

        // Test (--hunt <out> --case speedstorm --pattern <file name>; folder with that video, 20 s or longer): speed
        // changes at every pace, as from the menu's slider, the keys and the "back to 1x" button. After each burst
        // the picture must move at the speed that was set and the sound must be there; while it runs, the longest
        // time the picture stood still and the longest silence are measured.
        void HuntSpeedStorm()
        {
            var random = new Random(7);
            string name = huntPattern ?? "clip.mp4";
            muted = false;
            HuntOpen(name, () =>
            {
                if (!playing) TogglePlay();
                loop = true; // (a short clip runs off its end at 5x: it starts over)
                var bursts = new[] { new[] { 6, 600 }, new[] { 12, 250 }, new[] { 30, 80 }, new[] { 60, 30 }, new[] { 80, 12 }, new[] { 30, 80 }, new[] { 60, 30 }, new[] { 12, 250 } };
                int burst = -1, left = 0;
                double lastAt = -1, stood = 0, longestStill = 0;
                var clock = Stopwatch.StartNew();
                long lastTick = 0, silentSince = -1, longestSilent = 0;
                var watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
                watch.Tick += delegate
                {
                    var s = shown;
                    if (s == null || !s.IsVideo) return;
                    long now = clock.ElapsedMilliseconds;
                    double at = s.Video.Position.TotalSeconds;
                    if (at != lastAt) { lastAt = at; stood = 0; } else if (playing) { stood += now - lastTick; longestStill = Math.Max(longestStill, stood); }
                    bool silent = playing && s.Video.IsMuted && (soundPlayer == null || !soundPlayer.Sounding); // neither Windows' player nor FastViewer's own sound
                    if (!silent) silentSince = -1; else { if (silentSince < 0) silentSince = now; longestSilent = Math.Max(longestSilent, now - silentSince); }
                    lastTick = now;
                };
                watch.Start();
                Action next = null;
                Action change = null;
                change = () =>
                {
                    if (left-- <= 0) { After(1800, next); return; }
                    var s = shown;
                    // Far from the end, so the video never just stops by itself.
                    if (s.Video.NaturalDuration.HasTimeSpan && s.Video.Position.TotalSeconds > s.Video.NaturalDuration.TimeSpan.TotalSeconds - 6) s.Video.Position = TimeSpan.FromSeconds(1);
                    int kind = random.Next(10);
                    if (kind < 5) SetSpeed(AllSpeeds[random.Next(AllSpeeds.Length)]); // the slider, dragged
                    else if (kind < 8) StepSpeed(random.Next(2) == 0 ? -1 : 1);       // the keys
                    else SetSpeed(1);                                                 // the "back to 1x" button
                    After(bursts[burst][1], change);
                };
                next = () =>
                {
                    if (burst >= 0)
                    {
                        // Settled for 1.8 s: does the picture move at the speed that is set, and is there sound?
                        var s = shown;
                        double from = s.Video.Position.TotalSeconds;
                        long t0 = clock.ElapsedMilliseconds;
                        int done = burst;
                        After(700, () =>
                        {
                            double moved = (s.Video.Position.TotalSeconds - from) / ((clock.ElapsedMilliseconds - t0) / 1000.0);
                            if (moved < 0) moved = speed; // (it started over while being measured: a short clip at a high speed)
                            bool own = OwnSound(s), mine = soundPlayer != null && soundPlayer.Sounding;
                            bool sound = mine != !s.Video.IsMuted; // exactly one of the two is heard
                            bool ok = playing && Math.Abs(moved / speed - 1) < 0.25 && sound;
                            HuntLog("burst " + (done + 1) + " (" + bursts[done][0] + " changes, one every " + bursts[done][1] + " ms), now " + SpeedText(speed) + ": picture moves at " + moved.ToString("0.00") +
                                "x, stood still " + longestStill.ToString("0") + " ms at most, sound " + (mine ? "FastViewer's own" : "Windows' player") + " " + (sound ? "on" : mine ? "DOUBLE" : "SILENT") + ", silent " + longestSilent + " ms at most" +
                                ", picture woken " + pictureWakes + ", reopened " + pictureReopens + ", speed set again " + speedRepairs + ", fallbacks " + soundFallbacks + ", sound thread replaced " + soundRecoveries +
                                (soundPlayer != null ? ", failures " + soundPlayer.Failures + ", restarts " + soundPlayer.Resyncs + " [" + string.Join(" ", soundPlayer.Notes.ToArray()) + "], longest gap " + soundPlayer.LongestGap + " ms" + ", off by " + (soundPlayer.LastDrift * 1000).ToString("0") + " ms" + (soundPlayer.Trouble != null ? ", trouble: " + soundPlayer.Trouble : "") : "") + (ok ? "" : " FAIL"));
                            longestStill = 0; longestSilent = 0; silentSince = -1;
                            if (soundPlayer != null) soundPlayer.ResetStats();
                        });
                    }
                    if (++burst >= bursts.Length)
                    {
                        // Then two failures made on purpose: the picture frozen behind FastViewer's back, and the sound thread ended.
                        After(900, () =>
                        {
                            watch.Stop();
                            SetSpeed(1.25);
                            shown.Video.Position = TimeSpan.FromSeconds(2);
                        });
                        After(2400, () => { shown.Video.Pause(); HuntLog("picture frozen on purpose at " + shown.Video.Position.TotalSeconds.ToString("0.00") + " s (FastViewer still thinks it plays: " + playing + ")"); });
                        After(7400, () =>
                        {
                            double from = shown.Video.Position.TotalSeconds;
                            After(600, () =>
                            {
                                double moved = (shown.Video.Position.TotalSeconds - from) / 0.6 / speed;
                                HuntLog("5 s later: picture woken " + pictureWakes + " time(s), reopened " + pictureReopens + ", moves at " + moved.ToString("0.00") + " of its speed, own sound " +
                                    (soundPlayer != null && soundPlayer.Sounding) + (pictureWakes + pictureReopens >= 1 && moved > 0.7 && moved < 1.3 ? "" : " FAIL"));
                                if (soundPlayer != null) soundPlayer.Quit();
                                HuntLog("sound thread ended on purpose");
                            });
                        });
                        After(11800, () =>
                        {
                            bool mine = soundPlayer != null && soundPlayer.Sounding;
                            HuntLog("3.5 s later: sound thread replaced " + soundRecoveries + " time(s), fallbacks to Windows' player " + soundFallbacks + ", own sound " + mine + ", Windows' player " +
                                (shown.Video.IsMuted ? "silenced" : "on") + (soundRecoveries >= 1 && mine && shown.Video.IsMuted ? "" : " FAIL"));
                            EndOwnSound();
                            HuntLog("as when closed with the X: sound thread " + (soundPlayer == null ? "gone" : "still there FAIL"));
                            UpdateControls();
                        });
                        After(13300, () =>
                        {
                            bool mine = soundPlayer != null && soundPlayer.Sounding;
                            HuntLog("playing on after that: own sound " + mine + ", Windows' player " + (shown.Video.IsMuted ? "silenced" : "on") + (mine != !shown.Video.IsMuted ? "" : " FAIL"));
                            SetSpeed(1);
                            HuntDone();
                        });
                        return;
                    }
                    left = bursts[burst][0];
                    After(800, change);
                };
                After(1500, next);
            });
        }

        // Test (--hunt <out> --case speeddrag --pattern <file name>): the speed slider dragged left and right for six
        // seconds (a change every 16 ms), as by hand. Meanwhile the video must play on: it may never go back, hardly
        // stand still, and Windows' player may get next to no speed changes; when the drag ends, the picture must
        // move at the last speed within a moment. Then single steps (a key every 0.7 s), counted the same way.
        void HuntSpeedDrag()
        {
            string name = huntPattern ?? "clip.mp4";
            muted = false;
            HuntOpen(name, () =>
            {
                if (!playing) TogglePlay();
                loop = true;
                double lastAt = -1, still = 0, longestStill = 0, startAt = 0;
                int back = 0;
                long lastTick = 0, startTick = 0;
                var clock = Stopwatch.StartNew();
                var watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
                watch.Tick += delegate
                {
                    var s = shown;
                    long now = clock.ElapsedMilliseconds;
                    double at = s.Video.Position.TotalSeconds;
                    if (lastAt >= 0 && at < lastAt - 0.02 && lastAt < s.Video.NaturalDuration.TimeSpan.TotalSeconds - 1.6 && at > 0.5) back++; // (not the loop itself, nor an end taken as ended)
                    if (at != lastAt) still = 0; else { still += now - lastTick; longestStill = Math.Max(longestStill, still); }
                    lastAt = at;
                    lastTick = now;
                };
                Action reset = () => { back = 0; longestStill = 0; still = 0; startAt = shown.Video.Position.TotalSeconds; startTick = clock.ElapsedMilliseconds; speedApplies = 0; pictureWakes = pictureReopens = speedRepairs = 0; if (soundPlayer != null) soundPlayer.ResetStats(); };
                Func<string> facts = () => "went back " + back + " times, stood still " + longestStill.ToString("0") + " ms at most, Windows' player got " + speedApplies + " speed changes, woken " + pictureWakes +
                    ", reopened " + pictureReopens + ", speed set again " + speedRepairs + ", ends that went unsaid " + missedEnds + (soundPlayer != null ? ", sound restarts " + soundPlayer.Resyncs + ", longest gap " + soundPlayer.LongestGap + " ms" : "");
                // How long until the picture moves at the speed that is set (three 100 ms looks in a row within 20%).
                Action<string, Action> settle = null;
                settle = (what, then) =>
                {
                    long t0 = clock.ElapsedMilliseconds;
                    int good = 0;
                    double from = shown.Video.Position.TotalSeconds;
                    long fromTick = t0;
                    var look = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    look.Tick += delegate
                    {
                        long now = clock.ElapsedMilliseconds;
                        double at = shown.Video.Position.TotalSeconds, rate = (at - from) / ((now - fromTick) / 1000.0) / speed;
                        from = at; fromTick = now;
                        good = rate > 0.8 && rate < 1.2 ? good + 1 : 0;
                        if (good < 3 && now - t0 < 8000) return;
                        look.Stop();
                        long took = now - t0 - 300;
                        bool sound = (soundPlayer != null && soundPlayer.Sounding) != !shown.Video.IsMuted;
                        HuntLog(what + ": at " + SpeedText(speed) + " after " + Math.Max(0, took) + " ms, Windows' player set to " + shown.Video.SpeedRatio + ", sound " + (sound ? "on" : "SILENT") + "; " + facts() +
                            (good >= 3 && took < 1200 && back == 0 && pictureWakes + pictureReopens + speedRepairs == 0 && sound ? "" : " FAIL"));
                        then();
                    };
                    look.Start();
                };
                After(1500, () =>
                {
                    watch.Start();
                    reset();
                    int i = 0, at = Array.IndexOf(AllSpeeds, 1.0), dir = 1;
                    var drag = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
                    drag.Tick += delegate
                    {
                        if (++i % 3 == 0) { at += dir; if (at <= 0 || at >= AllSpeeds.Length - 1) dir = -dir; } // the thumb passes a stop every 50 ms
                        SetSpeed(AllSpeeds[Math.Max(0, Math.Min(AllSpeeds.Length - 1, at))]);
                        if (i < 375) return; // 6 s
                        drag.Stop();
                        SetSpeed(1.5); // let go here
                        double progress = (shown.Video.Position.TotalSeconds - startAt), wall = (clock.ElapsedMilliseconds - startTick) / 1000.0;
                        HuntLog("during a 6 s drag: the video went on " + progress.ToString("0.00") + " s in " + wall.ToString("0.00") + " s; " + facts() + (back == 0 && longestStill < 500 && speedApplies <= 3 && (progress > wall * 0.7 || progress < 0) ? "" : " FAIL"));
                        settle("let go at 1.5x", () =>
                        {
                            reset();
                            int step = 0;
                            var keys = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                            keys.Tick += delegate
                            {
                                if (step < 8) { StepSpeed(step < 4 ? 1 : -1); step++; return; }
                                keys.Stop();
                                settle("eight single steps, one every 0.7 s", () =>
                                {
                                    SetSpeed(1);
                                    After(3500, () =>
                                    {
                                        bool mine = soundPlayer != null && soundPlayer.Sounding;
                                        bool several = KnownTracks(shown.File) >= 2 && SoundMixing.Known(shown.File) != null && SoundMixing.Known(shown.File).Sources.Length >= 2;
                                        HuntLog("3.5 s back at 1x: sound from " + (mine ? "FastViewer" : "Windows' player") + ", Windows' player " + (shown.Video.IsMuted ? "silenced" : "on") + " at " + shown.Video.SpeedRatio + "x" +
                                            (mine == several && mine != !shown.Video.IsMuted && shown.Video.SpeedRatio == 1 ? "" : " FAIL"));
                                        watch.Stop();
                                        HuntDone();
                                    });
                                });
                            };
                            keys.Start();
                        });
                    };
                    drag.Start();
                });
            });
        }

        // Test (--hunt <out> --case loopsound --pattern <file name>; a short video with Repeat on): FastViewer's own
        // sound (volume 120%, so it plays at 1x too) through six loops and two jumps. Each time the sound must start
        // in step with the picture and stay there without its pitch being bent.
        void HuntLoopSound()
        {
            string name = huntPattern ?? "loop.mp4";
            double loopSpeed = 1; // (--pattern name@1.5: at that speed)
            if (name.Contains("@")) { loopSpeed = double.Parse(name.Substring(name.IndexOf('@') + 1), System.Globalization.CultureInfo.InvariantCulture); name = name.Substring(0, name.IndexOf('@')); }
            muted = false;
            HuntOpen(name, () =>
            {
                if (!playing) TogglePlay();
                loop = true;
                SetVolume(1.2);
                if (loopSpeed != 1) SetSpeed(loopSpeed);
                After(2000, () => { soundPlayer.Trace = new List<string>(); });
                double length = shown.Video.NaturalDuration.TimeSpan.TotalSeconds;
                int round = 0;
                Action next = null;
                next = () =>
                {
                    if (round == 3 && soundPlayer.Trace != null) { lock (soundPlayer.Trace) HuntLog("  how far off after each start (ms after start:ms off): " + string.Join(" ", soundPlayer.Trace)); soundPlayer.Trace = null; }
                    if (round >= 8) { SetVolume(0.8); After(1200, () => { bool mine = soundPlayer != null && soundPlayer.Sounding, several = SoundMixing.Known(shown.File) != null && SoundMixing.Known(shown.File).Sources.Length >= 2; HuntLog("back at 80%: sound from " + (mine ? "FastViewer" : "Windows' player") + ", Windows' player " + (shown.Video.IsMuted ? "silenced" : "on") + (mine == several && mine == shown.Video.IsMuted ? "" : " FAIL")); HuntDone(); }); return; }
                    bool jump = round >= 6;
                    // A loop: 1.2 s before the end, then on through the end. A jump: to the middle.
                    shown.Video.Position = TimeSpan.FromSeconds(jump ? length * (round == 6 ? 0.5 : 0.25) : length - 1.2);
                    After(jump ? 500 : 900, () => { if (soundPlayer != null) soundPlayer.ResetStats(); });
                    After(jump ? 3000 : 3600, () =>
                    {
                        var p = soundPlayer;
                        bool ok = p != null && p.Sounding && shown.Video.IsMuted && Math.Abs(p.LastDrift) < 0.025 && Math.Abs(p.WorstDrift) < 0.1 && p.MaxNudge <= 0.0041 && p.Splices <= 2 && (jump ? p.Resyncs <= 1 : p.Resyncs == 0 && p.Wraps == 1);
                        HuntLog((jump ? "jump " : "loop ") + (round + 1) + ": went on from the start by itself " + (p == null ? 0 : p.Wraps) + ", longest gap " + (p == null ? 0 : p.LongestGap) + " ms, sound " + (p != null && p.Sounding ? "on" : "SILENT") + ", restarts " + (p == null ? 0 : p.Resyncs) + ", off by " + (p == null ? 0 : p.WorstDrift * 1000).ToString("0") + " ms at worst, " +
                            (p == null ? 0 : p.LastDrift * 1000).ToString("0") + " ms now, slivers left out or repeated " + (p == null ? 0 : p.Splices) + ", pace bent " + ((p == null ? 0 : p.MaxNudge) * 100).ToString("0.00") + "% at most, at " +
                            shown.Video.Position.TotalSeconds.ToString("0.00") + " s" + (ok ? "" : " FAIL"));
                        round++;
                        next();
                    });
                };
                After(2500, next);
            });
        }

        void HuntOwnSound()
        {
            Func<string> state = () =>
            {
                var p = soundPlayer;
                var s = shown;
                return (p == null ? "no player" : "sounding " + p.Sounding + ", starts " + p.Starts + ", jumps " + p.Resyncs + ", first sound after " + p.StartMs + " ms, off by " +
                    (p.LastDrift * 1000).ToString("0") + " ms now, " + (p.WorstDrift * 1000).ToString("0") + " ms at worst" + (p.Trouble != null ? ", TROUBLE " + p.Trouble + " FAIL" : "")) +
                    "; picture's own sound " + (s != null && s.IsVideo ? (s.Video.IsMuted ? "silenced" : "on") : "-") + ", at " + (s != null && s.IsVideo ? s.Video.Position.TotalSeconds.ToString("0.00") : "-") +
                    " s, track button " + F<FrameworkElement>("TracksBtn").IsVisible;
            };
            Action<string, bool> check = (what, ok) => HuntLog(what + ": " + state() + (ok ? "" : " FAIL"));
            Func<double, bool> within = ms => soundPlayer != null && Math.Abs(soundPlayer.WorstDrift) * 1000 <= ms;
            muted = false; // (the test is silent anyway; "muted" would hide the volume cases)
            HuntOpen("clip.mp4", () =>
            {
                if (!playing) TogglePlay();
                After(300, () => check("clip.mp4 0.3 s after it showed", soundPlayer != null && shown.Video.IsMuted));
                After(4000, () =>
                {
                    check("after 4 s of playing", soundPlayer.Sounding && within(60) && F<FrameworkElement>("TracksBtn").IsVisible);
                    var l = SoundMixing.Known(shown.File);
                    HuntLog("  heard: " + string.Join("/", HeardGains(l).Select(g => g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))) + " of " + l.Count + " tracks (" + string.Join(", ", l.Names) + ")");
                    soundPlayer.ResetStats();
                    TogglePlay();
                });
                After(5000, () => { check("paused for a second", !soundPlayer.Sounding); TogglePlay(); });
                After(8000, () =>
                {
                    check("3 s after playing on", soundPlayer.Sounding && within(60));
                    soundPlayer.ResetStats();
                    shown.Video.Position = TimeSpan.FromSeconds(15);
                });
                After(13300, () =>
                {
                    check("3 s after a jump to 0:15", soundPlayer.Sounding && Math.Abs(soundPlayer.LastDrift) < 0.03 && Math.Abs(soundPlayer.WorstDrift) < 0.06);
                    soundPlayer.ResetStats();
                    SetSpeed(2);
                });
                After(14000, () =>
                {
                    check("3 s at 2x", soundPlayer.Sounding && Math.Abs(soundPlayer.LastDrift) < 0.08);
                    soundPlayer.ResetStats();
                    SetSpeed(0.5);
                });
                After(17000, () =>
                {
                    check("3 s at 0.5x", soundPlayer.Sounding && Math.Abs(soundPlayer.LastDrift) < 0.15); // (for a few seconds after a speed change the sound may lead a little)
                    SetSpeed(1);
                    soundChoice = 1;
                    UpdateOwnSound();
                    soundPlayer.ResetStats();
                });
                After(19500, () =>
                {
                    var l = SoundMixing.Known(shown.File);
                    check("only the game for 2.5 s", soundPlayer.Sounding && HeardGains(l)[1] == 1 && HeardGains(l)[2] == 0 && Math.Abs(soundPlayer.LastDrift) < 0.06);
                    var hook = huntMenuHook;
                    huntMenuHook = (m, b) => HuntLog("  track menu: " + string.Join(" | ", m.Items.OfType<MenuItem>().Select(i => i.Header + (i.Icon != null ? " (ticked)" : ""))));
                    ShowTracksMenu();
                    huntMenuHook = hook;
                    Snap("ownsound_bar");
                    soundChoice = 0;
                    SetVolume(1.5);
                    soundPlayer.ResetStats();
                });
                After(21500, () =>
                {
                    check("volume 150%", soundPlayer.Sounding && volume == 1.5);
                    SetVolume(0.8);
                    HuntOpen("one.mp4", () =>
                    {
                        if (!playing) TogglePlay();
                        After(1200, () =>
                        {
                            check("one.mp4 (one track, 80%)", !soundPlayer.Sounding && !shown.Video.IsMuted && !F<FrameworkElement>("TracksBtn").IsVisible);
                            soundPlayer.ResetStats();
                            SetVolume(1.4);
                        });
                        After(3200, () =>
                        {
                            check("one.mp4 at 140%", soundPlayer.Sounding && shown.Video.IsMuted && Math.Abs(soundPlayer.LastDrift) < 0.06);
                            SetVolume(1.0);
                        });
                        After(4000, () =>
                        {
                            check("one.mp4 back at 100%", !soundPlayer.Sounding && !shown.Video.IsMuted);
                            HuntDone();
                        });
                    });
                });
            });
        }

        void InitOwnSound()
        {
            soundTimer.Tick += delegate { TellSound(); };
            rateTimer.Tick += delegate { WatchPicture(); };
            if (!testRun) SoundMixing.StoreFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer", "tracks.txt");
            else if (soundStoreOverride != null) SoundMixing.StoreFile = soundStoreOverride; // --tracks <scratch file>
            Closed += delegate { if (soundPlayer != null) soundPlayer.Quit(); };
            OnClick("TracksBtn", ShowTracksMenu);
        }

        // The video bar's track button: only there for a video with two or more tracks to choose from.
        void UpdateTracksButton()
        {
            var b = F<FrameworkElement>("TracksBtn");
            if (b == null) return;
            var s = shown;
            var layout = s != null && s.IsVideo && !s.Failed && !ownSoundOff ? SoundMixing.Known(s.File) : null;
            bool want = layout != null && layout.Sources.Length >= 2 && KnownTracks(s.File) >= 2 && transport.Width >= 480;
            b.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
            if (!want) return;
            var icon = F<Glyph>("TracksIcon");
            if (soundChoice != 0) icon.SetResourceReference(Glyph.ForegroundProperty, "Accent"); // not everything is heard
            else icon.ClearValue(Glyph.ForegroundProperty);
        }

        void ShowTracksMenu()
        {
            var s = shown;
            var layout = s == null ? null : SoundMixing.Known(s.File);
            if (layout == null || layout.Sources.Length < 2) return;
            if (soundChoice > layout.Sources.Length) soundChoice = 0;
            var items = new List<Control>();
            Action<int, string> add = (choice, text) =>
            {
                var item = Item(text, soundChoice == choice ? "" : null, delegate
                {
                    soundChoice = choice;
                    SaveSettings();
                    UpdateOwnSound();
                });
                System.Windows.Automation.AutomationProperties.SetName(item, text + (soundChoice == choice ? ", chosen" : ""));
                items.Add(item);
            };
            add(0, layout.Sources.Length == 2 ? layout.Names[0] + " and " + layout.Names[1].ToLowerInvariant() : "Every track");
            for (int k = 0; k < layout.Sources.Length; k++) add(k + 1, "Only " + (layout.Names[k].StartsWith("Track") ? layout.Names[k].ToLowerInvariant() : layout.Names[k].ToLowerInvariant()));
            ShowMenu("TracksBtn", PlacementMode.Top, items.ToArray());
        }
    }
}
