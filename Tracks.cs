// FastViewer: videos with several sound tracks (1.26.0). A Medal clip keeps the game and the microphone on
// tracks of their own, and its first track is Medal's own ready mix of them; most players and Discord play
// only one track. Every save (clip, Send to Discord, crop, compatible copy, copy with captions) therefore
// mixes the tracks into one, each at its own volume (trim bar > Sound), unless "Keep the tracks separate".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FastViewer
{
    // What a file's sound tracks are, in the file's own order.
    class SoundLayout
    {
        public int Count;                 // sound tracks in the file
        public int MixTrack = -1;         // the track that already holds the others mixed together (Medal: the first); -1 = none
        public int[] Sources = new int[0]; // the tracks that make up the sound (all of them, or all but the ready mix)
        public string[] Names = new string[0];
        public string How = "";           // tests: how this was found

        // From the number of tracks and the ready mix: the tracks that make up the sound, and their names.
        public void Complete()
        {
            Sources = Enumerable.Range(0, Count).Where(x => x != MixTrack).ToArray();
            Names = new string[Sources.Length];
            for (int k = 0; k < Names.Length; k++)
                // Medal's order: ready mix, game, microphone, anything else.
                Names[k] = MixTrack == 0 && k == 0 ? "Game" : MixTrack == 0 && k == 1 ? "Mic" : "Track " + (Sources[k] + 1);
        }
        public int[] Streams;             // Windows' stream number of each track, in the file's order (null: the file couldn't be read)
        public double[] Offsets;          // per track: seconds to add to its sound's time stamps to get the picture's clock (each track has its own skipped start)
    }

    static class SoundMixing
    {
        public const int MaxVolume = 200;
        public static bool On = true;                 // settings line soundMix=
        static int[] volumes = new int[0];            // percent for the 1st, 2nd... source (settings line soundVol=); missing = 100
        static readonly Dictionary<string, SoundLayout> known = new Dictionary<string, SoundLayout>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<string, SoundLayout> byPath = new Dictionary<string, SoundLayout>(StringComparer.OrdinalIgnoreCase);

        // What is already known about a file's tracks, without looking at the disk (null: not worked out yet).
        public static SoundLayout Known(string file)
        {
            SoundLayout have;
            lock (known) return file != null && byPath.TryGetValue(file, out have) ? have : null;
        }

        public static int Volume(int slot) { return slot < volumes.Length ? volumes[slot] : 100; }

        public static void SetVolume(int slot, int percent)
        {
            if (slot < 0 || slot >= 16) return;
            if (slot >= volumes.Length)
            {
                int had = volumes.Length;
                Array.Resize(ref volumes, slot + 1);
                for (int i = had; i < volumes.Length; i++) volumes[i] = 100;
            }
            volumes[slot] = Math.Max(0, Math.Min(MaxVolume, percent));
        }

        public static string VolumesLine { get { return string.Join(",", volumes.Select(v => v.ToString(CultureInfo.InvariantCulture))); } }

        public static void LoadVolumes(string line)
        {
            var list = new List<int>();
            foreach (var part in line.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (list.Count < 16 && int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) list.Add(Math.Max(0, Math.Min(MaxVolume, v)));
            }
            volumes = list.ToArray();
        }

        // Found once per file (it has to listen to the tracks: 50-150 ms), then remembered.
        public static SoundLayout Layout(string file, CancelFlag cancel = null)
        {
            string key;
            try
            {
                var info = new System.IO.FileInfo(file);
                key = file + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
            catch { return new SoundLayout(); }
            lock (known)
            {
                SoundLayout have;
                if (known.TryGetValue(key, out have)) return have;
            }
            var found = FromDisk(key);
            bool fresh = found == null;
            if (fresh) found = VideoConverter.FindLayout(file, cancel);
            if (cancel != null && cancel.Cancelled) return found;
            if (fresh && found.Count >= 2 && found.Streams != null && !found.How.Contains("failed") && !found.How.Contains("couldn't")) ToDisk(key, found);
            lock (known)
            {
                if (known.Count > 400) known.Clear();
                known[key] = found;
                if (byPath.Count > 400) byPath.Clear();
                byPath[file] = found;
            }
            return found;
        }

        // What was found is kept between runs (tracks.txt next to the add-ons; tests keep nothing), so a clip that
        // was looked at once never has to be listened to again: "count|ready mix|streams|offsets|file, size, date".
        public static string StoreFile; // null: not kept
        static Dictionary<string, string> stored;

        static SoundLayout FromDisk(string key)
        {
            if (StoreFile == null) return null;
            try
            {
                lock (known)
                {
                    if (stored == null)
                    {
                        stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (System.IO.File.Exists(StoreFile))
                            foreach (var line in System.IO.File.ReadAllLines(StoreFile))
                            {
                                int cut = line.IndexOf("|file ", StringComparison.Ordinal);
                                if (cut > 0) stored[line.Substring(cut + 6)] = line.Substring(0, cut);
                            }
                    }
                    string value;
                    if (!stored.TryGetValue(key, out value)) return null;
                    var parts = value.Split('|');
                    var layout = new SoundLayout { Count = int.Parse(parts[0]), MixTrack = int.Parse(parts[1]), How = "remembered" };
                    layout.Streams = parts[2].Split(',').Select(int.Parse).ToArray();
                    layout.Offsets = parts[3].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    if (layout.Count < 2 || layout.Streams.Length != layout.Count || layout.Offsets.Length != layout.Count || layout.MixTrack >= layout.Count) return null;
                    layout.Complete();
                    return layout;
                }
            }
            catch { return null; }
        }

        static void ToDisk(string key, SoundLayout layout)
        {
            if (StoreFile == null) return;
            try
            {
                string value = layout.Count + "|" + layout.MixTrack + "|" + string.Join(",", layout.Streams) + "|" +
                    string.Join(",", layout.Offsets.Select(x => x.ToString("0.#####", CultureInfo.InvariantCulture)));
                lock (known)
                {
                    if (stored != null) stored[key] = value;
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StoreFile));
                    if (stored != null && stored.Count > 5000) // (start over rather than grow for ever)
                    {
                        stored.Clear();
                        stored[key] = value;
                        System.IO.File.WriteAllText(StoreFile, "");
                    }
                    System.IO.File.AppendAllText(StoreFile, value + "|file " + key + "\r\n");
                }
            }
            catch { }
        }

        // How loud each of the file's tracks goes into the one mixed track (0 = left out); null = keep the tracks as they are.
        public static double[] For(string file, CancelFlag cancel = null)
        {
            if (!On) return null;
            var layout = Layout(file, cancel);
            if (layout.Count < 2) return null;
            var gains = new double[layout.Count];
            if (layout.Sources.Length == 1 && layout.MixTrack >= 0) gains[layout.MixTrack] = 1; // two tracks with the same sound
            else for (int k = 0; k < layout.Sources.Length; k++) gains[layout.Sources[k]] = Volume(k) / 100.0;
            return gains;
        }

        // How many sound tracks a saved copy of this file gets.
        public static int TracksSaved(string file)
        {
            int tracks = VideoConverter.SoundTracks(file);
            return tracks >= 2 && On ? 1 : tracks;
        }
    }

    // Adds decoded sound tracks together by time, each at its own volume: 48 kHz stereo, 16 bits.
    class TrackMixer
    {
        public const int Rate = 48000;
        const int Fade = 120; // 2.5 ms
        class Track { public double Gain; public long Next, Added; public bool Started, Ended; public int Channels = 2, Hz = Rate; }
        public long AddedFrames(int track) { return track < tracks.Count ? tracks[track].Added : 0; } // (tests: frames of this track put in)
        readonly List<Track> tracks = new List<Track>();
        float[] sum = new float[Rate * 2 * 2]; // what's added up so far, from frame `start` on
        long start;
        int frames;
        byte[] bytes = new byte[0];

        public static List<string> DebugLog;
        public int Tracks { get { return tracks.Count; } }
        public int AddTrack(double gain) { tracks.Add(new Track { Gain = gain }); return tracks.Count - 1; }

        // How many frames a decoded buffer holds (48 kHz stereo 16-bit, as the player asks for).
        public static int FramesOf(IMFSample sample)
        {
            IMFMediaBuffer buffer;
            if (sample.ConvertToContiguousBuffer(out buffer) < 0) return 0;
            IntPtr data;
            int max, size;
            buffer.Lock(out data, out max, out size);
            buffer.Unlock();
            Marshal.ReleaseComObject(buffer);
            return size / 4;
        }
        public void Shape(int track, int channels, int hz) { if (channels >= 1 && hz >= 4000) { tracks[track].Channels = channels; tracks[track].Hz = hz; } }
        public void End(int track) { tracks[track].Ended = true; }

        public bool AllEnded { get { return tracks.All(t => t.Ended); } }
        public long Position { get { return start; } } // frames handed out so far

        // exact: placed at its own time, never moved to follow on from the last buffer. cutAt / fadeInFrom (frames):
        // nothing from cutAt on, faded out over the 2.5 ms before it; faded in over the 2.5 ms from fadeInFrom (1.29.0 loops).
        public void Add(int track, long time, IMFSample sample, float gainNow = -1, bool exact = false, long cutAt = long.MaxValue, long fadeInFrom = -1, bool cutFade = true)
        {
            IMFMediaBuffer buffer;
            if (sample.ConvertToContiguousBuffer(out buffer) < 0) return;
            IntPtr data;
            int max, size;
            buffer.Lock(out data, out max, out size);
            if (bytes.Length < size) bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            buffer.Unlock();
            Marshal.ReleaseComObject(buffer);

            var t = tracks[track];
            int ch = t.Channels, inFrames = size / (2 * ch);
            int outFrames = t.Hz == Rate ? inFrames : (int)((long)inFrames * Rate / t.Hz);
            long at = (long)Math.Round(time * (double)Rate / 1e7);
            if (DebugLog != null && DebugLog.Count < 3000) DebugLog.Add(track + " time " + time / 10000.0 + " ms, frames " + inFrames + ", expected " + (t.Started ? (t.Next * 1000.0 / Rate).ToString("0.00") : "-"));
            // Buffers follow each other exactly; only a real gap or jump (over 50 ms) moves the writing place.
            if (t.Started && !exact && Math.Abs(t.Next - at) < Rate / 20) at = t.Next;
            if (at + outFrames > cutAt) { outFrames = (int)Math.Max(0, cutAt - at); if (outFrames <= 0) return; }
            t.Next = at + outFrames;
            t.Started = true;
            int skip = (int)Math.Max(0, Math.Min(outFrames, start - at)); // (already handed out: can't be added any more)
            long need = at + outFrames - start;
            if (need <= 0) return;
            if (need * 2 > sum.Length) Array.Resize(ref sum, (int)Math.Max(need * 2, sum.Length * 2L));
            t.Added += outFrames - skip;
            float gain = gainNow >= 0 ? gainNow : (float)t.Gain; // (the player passes the volume of this moment)
            for (int f = skip; f < outFrames; f++)
            {
                int from = (t.Hz == Rate ? f : (int)((long)f * t.Hz / Rate)) * ch * 2;
                int left = (short)(bytes[from] | (bytes[from + 1] << 8));
                int right = ch > 1 ? (short)(bytes[from + 2] | (bytes[from + 3] << 8)) : left;
                int to = (int)(at + f - start) * 2;
                float g = gain;
                long abs = at + f;
                if (cutFade && abs >= cutAt - Fade) g *= (cutAt - abs) / (float)Fade;
                if (fadeInFrom >= 0 && abs >= fadeInFrom && abs < fadeInFrom + Fade) g *= (abs - fadeInFrom + 1) / (float)Fade;
                sum[to] += left * g;
                sum[to + 1] += right * g;
            }
            if (need > frames) frames = (int)need;
        }

        // The next piece every track has reached (all: whatever is left), at most `most` frames (half a second unless said); null when there is none yet.
        public short[] Take(bool all, out long time, int most = Rate / 2)
        {
            time = start * 10000000L / Rate;
            long ready = frames;
            if (!all)
            {
                foreach (var t in tracks)
                    if (!t.Ended) ready = Math.Min(ready, t.Started ? t.Next - start : 0);
                ready = Math.Max(ready, frames - Rate * 20L); // a track that stays quiet for 20 s doesn't hold up the rest
                if (ready < 2048) return null;
            }
            int n = (int)Math.Max(0, Math.Min(ready, Math.Max(256, most)));
            if (n == 0) return null;
            var result = new short[n * 2];
            for (int i = 0; i < result.Length; i++) result[i] = Limit(sum[i]);
            Array.Copy(sum, n * 2, sum, 0, (frames - n) * 2);
            Array.Clear(sum, (frames - n) * 2, n * 2);
            start += n;
            frames -= n;
            return result;
        }

        // Louder than full scale bends over smoothly instead of being cut flat (which crackles).
        internal static short Limit(float v)
        {
            const float Knee = 26000, Room = 32767 - Knee;
            float a = Math.Abs(v);
            if (a > Knee) a = Knee + Room * (float)Math.Tanh((a - Knee) / Room);
            return (short)(v < 0 ? -a : a);
        }
    }

    // Plays sound faster or slower the simple way, as the viewer does at another speed: the samples are read at
    // another pace (straight lines between them), so the pitch goes up or down with the speed. Pieces follow each
    // other without a seam: the place between two samples is carried over.
    class SoundStretch
    {
        public double Speed = 1;
        double at;            // where in the next piece the next output sample is read (-1..0: between the last piece and it)
        short lastLeft, lastRight;
        long written;
        long firstTime = -1;

        TimeStretch keep;   // 1.29.0: the same pitch (unless Settings > Videos > "Change pitch with speed")
        short[] kept = new short[0];

        public short[] Apply(short[] piece, ref long time)
        {
            if (firstTime < 0) firstTime = (long)(time / Speed);
            if (keep != null || (TimeStretch.KeepPitch && written == 0 && at == 0))
            {
                if (keep == null) keep = new TimeStretch { Speed = Speed };
                keep.Push(piece, 0, piece.Length / 2, piece.Length / 2 / (double)TrackMixer.Rate);
                double covered;
                int n = keep.Pull(ref kept, 1, out covered, null, null, true);
                return Out(n, ref time);
            }
            return Resampled(piece, ref time);
        }

        // Saving ends: the last bit the stretcher still holds.
        public short[] Finish(ref long time)
        {
            if (keep == null) return new short[0];
            return Out(keep.Finish(ref kept, 1), ref time);
        }

        short[] Out(int n, ref long time)
        {
            var result = new short[n * 2];
            Array.Copy(kept, result, n * 2);
            time = firstTime + written * 10000000L / TrackMixer.Rate;
            written += n;
            return result;
        }

        short[] Resampled(short[] piece, ref long time)
        {
            int frames = piece.Length / 2;
            if (firstTime < 0) firstTime = (long)(time / Speed);
            var made = new System.Collections.Generic.List<short>((int)(frames / Speed) * 2 + 8);
            while (at < frames - 1)
            {
                int a = (int)Math.Floor(at);
                float f = (float)(at - a);
                int l0 = a < 0 ? lastLeft : piece[a * 2], r0 = a < 0 ? lastRight : piece[a * 2 + 1];
                made.Add((short)(l0 + (piece[(a + 1) * 2] - l0) * f));
                made.Add((short)(r0 + (piece[(a + 1) * 2 + 1] - r0) * f));
                at += Speed;
            }
            at -= frames;
            if (frames > 0) { lastLeft = piece[(frames - 1) * 2]; lastRight = piece[(frames - 1) * 2 + 1]; }
            time = firstTime + written * 10000000L / TrackMixer.Rate;
            written += made.Count / 2;
            return made.ToArray();
        }
    }

    static partial class VideoConverter
    {
        static IMFSample MakeSoundSample(short[] data, long time)
        {
            IntPtr to;
            int max, length;
            IMFMediaBuffer buffer;
            Check(MFCreateMemoryBuffer(data.Length * 2, out buffer), "mixing the sound");
            buffer.Lock(out to, out max, out length);
            Marshal.Copy(data, 0, to, data.Length);
            buffer.Unlock();
            buffer.SetCurrentLength(data.Length * 2);
            IMFSample result;
            Check(MFCreateSample(out result), "mixing the sound");
            result.AddBuffer(buffer);
            Marshal.ReleaseComObject(buffer);
            result.SetSampleTime(time);
            result.SetSampleDuration(data.Length / 2 * 10000000L / TrackMixer.Rate);
            return result;
        }

        // Hands what the mixer has ready to the file being written.
        static void WriteMixed(IMFSinkWriter writer, int stream, TrackMixer mixer, bool all, SoundStretch speed = null)
        {
            long time;
            short[] piece;
            while ((piece = mixer.Take(all, out time)) != null)
            {
                if (speed != null)
                {
                    piece = speed.Apply(piece, ref time);
                    if (piece.Length == 0) continue;
                }
                var made = MakeSoundSample(piece, time);
                try { Check(writer.WriteSample(stream, made), "writing the sound"); }
                finally { Marshal.ReleaseComObject(made); }
            }
            if (all && speed != null)
            {
                long end = 0;
                var last = speed.Finish(ref end);
                if (last.Length > 0)
                {
                    var made = MakeSoundSample(last, end);
                    try { Check(writer.WriteSample(stream, made), "writing the sound"); }
                    finally { Marshal.ReleaseComObject(made); }
                }
            }
        }

        // Works out what a file's sound tracks are: how many, and whether one of them is the others mixed together.
        internal static SoundLayout FindLayout(string file, CancelFlag cancel)
        {
            var layout = new SoundLayout();
            if (MFStartup(0x00020070, 0) < 0) return layout;
            IMFSourceReader reader = null;
            try
            {
                var streams = new List<int>();
                if (MFCreateSourceReaderFromURL(file, null, out reader) < 0) return layout;
                for (int i = 0; i < 64; i++)
                {
                    IMFMediaType t;
                    if (reader.GetNativeMediaType(i, 0, out t) < 0) break;
                    Guid major;
                    t.GetGUID(ref G.MF_MT_MAJOR_TYPE, out major);
                    Marshal.ReleaseComObject(t);
                    if (major == G.MFMediaType_Audio) streams.Add(i);
                }
                Marshal.ReleaseComObject(reader);
                reader = null;
                layout.Count = streams.Count;
                layout.Streams = streams.ToArray();
                layout.Offsets = new double[streams.Count];
                double pictureSkip = 2 * VideoInfo.EditStart(file, "vide");
                if (streams.Count == 1) layout.Offsets[0] = pictureSkip - 2 * VideoInfo.EditStart(file, "soun");
                layout.Sources = Enumerable.Range(0, streams.Count).ToArray();
                if (streams.Count >= 2)
                {
                    var inOrder = FileOrder(file, streams, cancel);
                    // The file's order known: only its first track can be the ready mix. Not known: any of them.
                    double[] skips = null; // each track's own skipped start (see Convert), known only with the file's order
                    if (inOrder != null) skips = Enumerable.Range(0, inOrder.Count).Select(k => 2 * VideoInfo.EditStart(file, "soun", k)).ToArray();
                    if (inOrder != null) { layout.Streams = inOrder.ToArray(); layout.Offsets = skips.Select(x => pictureSkip - x).ToArray(); }
                    var heard = Listen(file, inOrder ?? streams, skips, 12, cancel);
                    if (heard == null) layout.How = "couldn't listen";
                    else
                    {
                        for (int c = 0; c < (inOrder != null ? 1 : heard.Length) && layout.MixTrack < 0; c++)
                        {
                            double left = MixFit(heard, c);
                            layout.How += "track " + (c + 1) + " as the mix leaves " + (left < 0 ? "?" : (left * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%") + "; ";
                            if (left >= 0 && left < 0.06) layout.MixTrack = c;
                        }
                        if (inOrder == null) layout.How += "Windows' order";
                        if (layout.MixTrack >= 0) layout.Sources = layout.Sources.Where(x => x != layout.MixTrack).ToArray();
                    }
                }
                layout.Complete();
                return layout;
            }
            catch (Exception e) { layout.How += " failed: " + e.Message; return layout; }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        // The first seconds of each sound stream at 8 kHz mono (enough to compare them); null when Windows won't.
        static short[][] Listen(string file, IList<int> streams, double[] skips, double seconds, CancelFlag cancel)
        {
            const int Asked = 16000, Rate = 8000; // (Windows resamples to 16 kHz when asked exactly; halved here)
            IMFSourceReader reader = null;
            try
            {
                if (MFCreateSourceReaderFromURL(file, null, out reader) < 0) return null;
                reader.SetStreamSelection(ALL_STREAMS, false);
                var slot = new Dictionary<int, int>();
                for (int k = 0; k < streams.Count; k++)
                {
                    int i = streams[k];
                    if (reader.SetStreamSelection(i, true) < 0) return null;
                    IMFMediaType exact;
                    MFCreateMediaType(out exact);
                    exact.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Audio);
                    exact.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFAudioFormat_PCM);
                    exact.SetUINT32(ref G.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    exact.SetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, 1);
                    exact.SetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, Asked);
                    exact.SetUINT32(ref G.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
                    exact.SetUINT32(ref G.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Asked * 2);
                    if (reader.SetCurrentMediaType(i, IntPtr.Zero, exact) < 0) return null;
                    slot[i] = k;
                }
                int want = (int)(Rate * seconds);
                var heard = new short[streams.Count][];
                var next = new long[streams.Count];
                var full = new bool[streams.Count];
                for (int k = 0; k < heard.Length; k++) { heard[k] = new short[want]; next[k] = long.MinValue; }
                byte[] bytes = new byte[0];
                while (full.Any(x => !x))
                {
                    if (cancel != null && cancel.Cancelled) return null;
                    int index, flags;
                    long time;
                    IMFSample sample;
                    if (reader.ReadSample(ANY_STREAM, 0, out index, out flags, out time, out sample) < 0 || (flags & 1) != 0) return null;
                    int k;
                    bool mine = slot.TryGetValue(index, out k);
                    if (mine && (flags & 2) != 0) full[k] = true;
                    if (sample == null) continue;
                    try
                    {
                        if ((flags & 0x20) != 0) return null; // the sound turned out different from its header: no comparing
                        IMFMediaBuffer buffer;
                        if (!mine || full[k] || sample.ConvertToContiguousBuffer(out buffer) < 0) continue;
                        IntPtr data;
                        int max, size;
                        buffer.Lock(out data, out max, out size);
                        if (bytes.Length < size) bytes = new byte[size];
                        Marshal.Copy(data, bytes, 0, size);
                        buffer.Unlock();
                        Marshal.ReleaseComObject(buffer);
                        long at = (long)Math.Round((time / 1e7 - (skips != null ? skips[k] : 0)) * Asked);
                        if (next[k] != long.MinValue && Math.Abs(next[k] - at) < Asked / 20) at = next[k];
                        int count = size / 2;
                        next[k] = at + count;
                        for (int f = 0; f < count; f++)
                        {
                            if (at + f < 0) continue;
                            long to = (at + f) / 2;
                            if (to >= want) { full[k] = true; break; }
                            heard[k][to] = (short)(heard[k][to] + (short)(bytes[f * 2] | (bytes[f * 2 + 1] << 8)) / 2);
                        }
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                return heard;
            }
            catch { return null; }
            finally { if (reader != null) Marshal.ReleaseComObject(reader); }
        }

        // How much of track `mix` is left (0..1 of its energy) after taking away the best blend of the other tracks,
        // each moved up to 60 ms to line up (Medal's editor shifts its mix by 5 ms). Under a few percent: it is
        // those tracks mixed together. -1 when the track is silent here, so nothing can be told.
        static double MixFit(short[][] heard, int mix)
        {
            const int Reach = 200, Edge = 512; // (25 ms either way)
            int n = heard[mix].Length;
            if (n < 4 * Edge) return -1;
            var target = heard[mix];
            double energy = 0;
            for (int i = Edge; i < n - Edge; i++) energy += (double)target[i] * target[i];
            if (energy / (n - 2 * Edge) < 20 * 20) return -1; // quieter than -64 dB
            var others = new List<short[]>();
            var lags = new List<int>();
            int common = 0;
            double strongest = 0;
            for (int k = 0; k < heard.Length; k++)
            {
                if (k == mix) continue;
                var other = heard[k];
                double own = 0;
                for (int i = Edge; i < n - Edge; i += 2) own += (double)other[i] * other[i];
                if (own / ((n - 2 * Edge) / 2) < 4 * 4) continue; // silent: adds nothing
                int best = 0;
                double bestSum = 0;
                for (int lag = -Reach; lag <= Reach; lag++)
                {
                    double s = 0;
                    for (int i = Edge; i < n - Edge; i += 3) s += target[i] * other[i + lag];
                    if (Math.Abs(s) > Math.Abs(bestSum)) { bestSum = s; best = lag; }
                }
                others.Add(other);
                lags.Add(best);
                double match = Math.Abs(bestSum) / Math.Sqrt(own * energy / 2);
                if (match > strongest) { strongest = match; common = best; }
            }
            if (others.Count == 0) return 1;
            // Each track at the place it fits best, or all at the place the clearest one fits (a quiet voice under
            // loud game sound is hard to place by itself).
            return Math.Min(LeftAfter(target, others, lags, energy, Edge), LeftAfter(target, others, Enumerable.Repeat(common, others.Count).ToList(), energy, Edge));
        }

        static double LeftAfter(short[] target, List<short[]> others, List<int> lags, double energy, int edge)
        {
            int m = others.Count, n = target.Length;
            var a = new double[m, m];
            var b = new double[m];
            for (int p = 0; p < m; p++)
            {
                for (int q = p; q < m; q++)
                {
                    double s = 0;
                    for (int i = edge; i < n - edge; i++) s += (double)others[p][i + lags[p]] * others[q][i + lags[q]];
                    a[p, q] = a[q, p] = s;
                }
                double t = 0;
                for (int i = edge; i < n - edge; i++) t += (double)others[p][i + lags[p]] * target[i];
                b[p] = t;
            }
            // Least squares by elimination (2 or 3 unknowns).
            var c = new double[m];
            var a2 = (double[,])a.Clone();
            var b2 = (double[])b.Clone();
            for (int p = 0; p < m; p++)
            {
                double pivot = Math.Abs(a2[p, p]) < 1e-6 ? 1e-6 : a2[p, p];
                for (int q = p + 1; q < m; q++)
                {
                    double r = a2[q, p] / pivot;
                    for (int j = p; j < m; j++) a2[q, j] -= r * a2[p, j];
                    b2[q] -= r * b2[p];
                }
            }
            for (int p = m - 1; p >= 0; p--)
            {
                double s = b2[p];
                for (int j = p + 1; j < m; j++) s -= a2[p, j] * c[j];
                c[p] = s / (Math.Abs(a2[p, p]) < 1e-6 ? 1e-6 : a2[p, p]);
            }
            double explained = 0;
            for (int p = 0; p < m; p++) explained += c[p] * b[p];
            return Math.Max(0, Math.Min(1, (energy - explained) / energy));
        }
    }

    partial class ViewerWindow
    {
        internal SoundLayout trimLayout; // the tracks of the video being trimmed (null until found)

        void InitSound()
        {
            OnClick("TrimSoundBtn", ShowSoundMenu);
        }

        // The Sound button is only there for a video with two or more tracks to balance.
        void FindTrimSound(Slot s)
        {
            trimLayout = null;
            SizeTrimBar();
            string file = s.File;
            var thread = new Thread(delegate()
            {
                var layout = SoundMixing.Layout(file);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!trimming || shown == null || shown.File != file) return;
                    trimLayout = layout;
                    if (layout.Sources.Length < 2) return;
                    var b = F<FrameworkElement>("TrimSoundBtn");
                    SizeTrimBar();
                    if (b.Visibility == Visibility.Visible) Motion.FromTo(b, OpacityProperty, 0, 1, Motion.Ms(Motion.Normal, false), Motion.Smooth);
                }));
            }) { IsBackground = true, Name = "Sound tracks" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        void ShowSoundMenu()
        {
            var layout = trimLayout;
            if (layout == null || layout.Sources.Length < 2) return;
            var items = new List<Control>();
            for (int k = 0; k < layout.Sources.Length; k++) items.Add(SoundRow(k, layout.Names[k]));
            items.Add(new Separator());
            var keep = Item("Keep the tracks separate", SoundMixing.On ? null : "", delegate
            {
                SoundMixing.On = !SoundMixing.On;
                SaveSettings();
                Dispatcher.BeginInvoke(new Action(delegate { if (trimming) { ClosingOwnMenu(F<Button>("TrimSoundBtn")); ShowSoundMenu(); } }), System.Windows.Threading.DispatcherPriority.Background);
            });
            keep.ToolTip = "Saves every sound track as it is instead of mixing them into one. Most players and Discord then play only the first.";
            System.Windows.Automation.AutomationProperties.SetName(keep, "Keep the tracks separate, " + (SoundMixing.On ? "off" : "on"));
            items.Add(keep);
            ShowMenu("TrimSoundBtn", PlacementMode.Top, items.ToArray());
        }

        // Test (--hunt <out> --case sound, with --settings; folder with clip.mp4 = three sound tracks, the first the
        // other two mixed, and one.mp4 = one track): the Sound button, its menu, the volumes and what is remembered.
        void HuntSound()
        {
            List<object> rows = null;
            Action<ContextMenu, Button> draw = (menu, button) =>
            {
                // A ContextMenu can't sit in the window: its items move to a Menu drawn the same way (as the audit does).
                if (huntMenuHost != null) { root.Children.Remove(huntMenuHost); huntMenuHost = null; }
                var look = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate TargetType='Menu' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                    "<Border Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuEdge}' BorderThickness='1' " +
                    "CornerRadius='{DynamicResource Radius.Float}' Padding='4' MinWidth='220'><StackPanel IsItemsHost='True'/></Border></ControlTemplate>");
                var copy = new Menu { Template = look, FontSize = 14 };
                copy.SetResourceReference(Control.ForegroundProperty, "TextMain");
                copy.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
                rows = menu.Items.Cast<object>().ToList();
                menu.Items.Clear();
                foreach (var it in rows) copy.Items.Add(it);
                var host = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = copy };
                Grid.SetRowSpan(host, 3);
                Panel.SetZIndex(host, 100);
                var p = button.TranslatePoint(new Point(0, 0), root);
                root.Children.Add(host);
                host.UpdateLayout();
                host.Margin = new Thickness(Math.Max(0, Math.Min(p.X, root.ActualWidth - host.ActualWidth)), Math.Max(0, p.Y - host.ActualHeight - 4), 0, 0);
                huntMenuHost = host;
            };
            Func<string> menuText = () => rows == null ? "no menu" : string.Join(" | ", rows.OfType<MenuItem>().Select(r =>
            {
                var slider = FindAll<Slider>(r).FirstOrDefault();
                return slider != null ? FindAll<TextBlock>(r).First().Text + " " + slider.Value + (slider.IsEnabled ? "" : " (off)") + " [" + FindAll<Button>(r).First().Content + "]"
                                      : r.Header + (r.Icon != null ? " (ticked)" : "");
            }));
            Func<string> bar = () =>
            {
                var b = F<FrameworkElement>("TrimSoundBtn");
                var save = F<FrameworkElement>("TrimSaveBtn");
                var play = F<FrameworkElement>("TrimPlayBtn");
                var title = F<FrameworkElement>("TrimText");
                double barWidth = F<FrameworkElement>("TrimBar").ActualWidth;
                double right = save.TranslatePoint(new Point(save.ActualWidth, 0), F<FrameworkElement>("TrimBar")).X, left = play.TranslatePoint(new Point(0, 0), F<FrameworkElement>("TrimBar")).X;
                return "bar " + barWidth.ToString("0") + ": Sound button " + (b.IsVisible ? "shown " + b.ActualWidth.ToString("0") + " wide, word " + F<FrameworkElement>("TrimSoundText").IsVisible : "hidden") +
                    ", hint " + F<FrameworkElement>("TrimHint").IsVisible + ", title " + title.ActualWidth.ToString("0") + " wide, buttons from " + left.ToString("0") + " to " + right.ToString("0") +
                    (right > barWidth - 10 || left < 14 || (b.IsVisible && title.IsVisible && title.ActualWidth < 30) || b.IsVisible != (trimLayout != null && trimLayout.Sources.Length >= 2 && barWidth >= 370) ? " CRAMPED FAIL" : "");
            };
            Func<string> lines = () => string.Join(" / ", System.IO.File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("soundMix=") || l.StartsWith("soundVol=")));
            string outFile = System.IO.Path.Combine(snapDir, "out mixed.mp4"), keepFile = System.IO.Path.Combine(snapDir, "out separate.mp4");
            HuntOpen("clip.mp4", () =>
            {
                if (playing) TogglePlay();
                StartTrim();
                Until(() => trimLayout != null, 10000, () => After(400, () =>
                {
                    var l = trimLayout;
                    HuntLog("clip.mp4: " + l.Count + " tracks, ready mix " + (l.MixTrack + 1) + ", sources " + string.Join("/", l.Names) + " (" + l.How + ")" + (l.MixTrack == 0 && l.Sources.Length == 2 ? "" : " FAIL"));
                    HuntLog(bar() + (F<FrameworkElement>("TrimSoundBtn").IsVisible ? "" : " FAIL"));
                    Snap("sound_bar");
                    foreach (int width in new[] { 900, 760, 600, 470, 380 })
                    {
                        Width = width;
                        Settle();
                        HuntLog("window " + width + ": " + bar());
                        if (width == 470) Snap("sound_bar_narrow");
                    }
                    Width = 1100;
                    Settle();
                    var hook = huntMenuHook;
                    huntMenuHook = draw;
                    ShowSoundMenu();
                    Settle();
                    HuntLog("menu: " + menuText());
                    Snap("sound_menu");
                    var mic = FindAll<Slider>((MenuItem)rows[1]).First();
                    mic.Value = 50;
                    HuntLog("Mic slider to 50: volume " + SoundMixing.Volume(1) + ", " + menuText() + (SoundMixing.Volume(1) == 50 ? "" : " FAIL"));
                    mic.Value = 163; // snaps to steps of 5
                    HuntLog("Mic slider to 163: volume " + SoundMixing.Volume(1) + (SoundMixing.Volume(1) == 165 ? "" : " FAIL"));
                    Width = 380; Settle(); Snap("sound_bar_380"); Width = 1100; Settle();
                    FindAll<Button>((MenuItem)rows[1]).First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    HuntLog("its number clicked: volume " + SoundMixing.Volume(1) + (SoundMixing.Volume(1) == 100 ? "" : " FAIL"));
                    mic.Value = 50;
                    var game = FindAll<Slider>((MenuItem)rows[0]).First();
                    game.Value = 150;
                    Settle();
                    Snap("sound_menu_changed");
                    After(900, () =>
                    {
                        HuntLog("remembered: " + lines() + (lines().Contains("soundVol=150,50") ? "" : " FAIL"));
                        var mix = SoundMixing.For(shown.File);
                        HuntLog("what a save mixes: " + string.Join("/", mix.Select(g => g.ToString("0.##", CultureInfo.InvariantCulture))) + (mix.Length == 3 && mix[0] == 0 && mix[1] == 1.5 && mix[2] == 0.5 ? "" : " FAIL"));
                        ((MenuItem)rows.Last()).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        After(500, () =>
                        {
                            Settle();
                            HuntLog("Keep the tracks separate clicked: mixing " + SoundMixing.On + ", menu again: " + menuText() + ", " + lines() + (!SoundMixing.On && lines().Contains("soundMix=0") ? "" : " FAIL"));
                            Snap("sound_menu_separate");
                            HuntLog("a save now mixes: " + (SoundMixing.For(shown.File) == null ? "nothing, tracks kept" : "still mixing FAIL"));
                            string file = shown.File;
                            if (huntMenuHost != null) { root.Children.Remove(huntMenuHost); huntMenuHost = null; }
                            EndTrim();
                            RunConversion(file, keepFile, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(3), "Saving clip");
                            Until(() => !converting, 120000, () =>
                            {
                                HuntLog("saved with separate tracks: " + VideoConverter.SoundTracks(keepFile) + " sound tracks" + (VideoConverter.SoundTracks(keepFile) == 3 ? "" : " FAIL"));
                                SoundMixing.On = true;
                                SaveSettings(true);
                                RunConversion(file, outFile, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(3), "Saving clip");
                                Until(() => !converting, 120000, () =>
                                {
                                    HuntLog("saved mixed (Game 150, Mic 50): " + VideoConverter.SoundTracks(outFile) + " sound track" + (VideoConverter.SoundTracks(outFile) == 1 ? "" : " FAIL") + "; " + VideoConverter.LastInfo);
                                    huntMenuHook = hook;
                                    HuntOpen("one.mp4", () =>
                                    {
                                        if (playing) TogglePlay();
                                        StartTrim();
                                        Until(() => trimLayout != null, 10000, () => After(400, () =>
                                        {
                                            HuntLog("one.mp4: " + trimLayout.Count + " track, " + bar() + (F<FrameworkElement>("TrimSoundBtn").IsVisible ? " FAIL" : ""));
                                            Snap("sound_bar_one_track");
                                            EndTrim();
                                            HuntDone();
                                        }));
                                    });
                                });
                            });
                        });
                    });
                }));
            });
        }

        // One track's volume in what gets saved: a slider from 0 to 200% and the number on a button that goes back to 100%.
        Control SoundRow(int slot, string name)
        {
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
            var slider = new Slider
            {
                Minimum = 0, Maximum = SoundMixing.MaxVolume, Value = SoundMixing.Volume(slot), IsSnapToTickEnabled = true, TickFrequency = 5, SmallChange = 5, LargeChange = 25,
                MinWidth = 150, Margin = new Thickness(14, 0, 10, 0), IsEnabled = SoundMixing.On
            };
            slider.SetResourceReference(StyleProperty, "FluentSlider");
            System.Windows.Automation.AutomationProperties.SetName(slider, name + " volume in saved videos");
            var value = new Button { Content = SoundMixing.Volume(slot) + "%", MinWidth = 54, Height = 26, Padding = new Thickness(8, 0, 8, 0), FontSize = 12, Focusable = false, IsEnabled = SoundMixing.On };
            value.SetResourceReference(StyleProperty, "Filled");
            System.Windows.Automation.AutomationProperties.SetName(value, name + " back to 100%");
            if (showTips) value.ToolTip = "Back to 100%";
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            var row = new MenuItem { Header = grid, StaysOpenOnClick = true, Icon = new Glyph { Code = "", Size = 16 } };
            System.Windows.Automation.AutomationProperties.SetName(row, name + " volume");
            if (!SoundMixing.On) { label.Opacity = 0.5; return row; }
            Action<double> move = by => slider.Value = Math.Max(0, Math.Min(SoundMixing.MaxVolume, Math.Round(slider.Value / 5) * 5 + by));
            System.Windows.Threading.DispatcherTimer save = null;
            slider.ValueChanged += delegate
            {
                int v = (int)Math.Round(slider.Value / 5) * 5;
                if (Math.Abs(slider.Value - v) > 0.01) { slider.Value = v; return; } // steps of 5, however it was set
                SoundMixing.SetVolume(slot, v);
                value.Content = v + "%";
                if (save == null)
                {
                    save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    save.Tick += delegate { save.Stop(); SaveSettings(); };
                }
                save.Stop();
                save.Start();
            };
            value.Click += delegate { slider.Value = 100; };
            row.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e) { move(e.Delta > 0 ? 5 : -5); e.Handled = true; };
            row.PreviewKeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.Key == Key.Left || e.Key == Key.Right) { move(e.Key == Key.Right ? 5 : -5); e.Handled = true; }
                else if (e.Key == Key.Enter || e.Key == Key.Space) { slider.Value = 100; e.Handled = true; }
            };
            return row;
        }
    }
}
