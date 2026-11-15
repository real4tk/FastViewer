// FastViewer: files whose sound stops Windows from playing them (1.26.0). Windows refuses a whole .mp4 or .m4a
// when it can't make sense of the sound in it, also when the picture is fine. This names the cause, and repairs the
// one that can be repaired without touching a single picture or sound sample: an AAC track whose description
// (the "AudioSpecificConfig": kind of AAC, samples a second, channels) was left out by the tool that made the file
// (seen in videos saved from VK). "Make a playable copy" copies the file and writes those two bytes into it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace FastViewer
{
    class SoundTrouble
    {
        public string Cause;                 // in plain words; null = nothing wrong found
        public bool Fixable;                 // a copy with the missing description plays
        public List<KeyValuePair<long, byte[]>> Patches = new List<KeyValuePair<long, byte[]>>(); // where in the file, the bytes to put there
    }

    static partial class VideoInfo
    {
        static readonly int[] AacRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        // Looks at how each sound track of an MP4/MOV/M4A describes itself. Null when nothing is wrong (or it isn't such a file).
        public static SoundTrouble SoundProblem(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long moov, moovEnd;
                    if (!FindChild(fs, 0, fs.Length, "moov", out moov, out moovEnd)) return null;
                    var found = new SoundTrouble();
                    long pos = moov, size, header;
                    string type;
                    while (ReadHeader(fs, pos, moovEnd, out size, out type, out header))
                    {
                        long trak = pos + header, trakEnd = pos + size;
                        pos += size;
                        if (type != "trak" || Handler(fs, trak, trakEnd) != "soun") continue;
                        long a, aEnd, b, bEnd;
                        if (!FindChild(fs, trak, trakEnd, "mdia", out a, out aEnd) || !FindChild(fs, a, aEnd, "minf", out b, out bEnd) ||
                            !FindChild(fs, b, bEnd, "stbl", out a, out aEnd) || !FindChild(fs, a, aEnd, "stsd", out b, out bEnd)) continue;
                        long entry = b + 8, entrySize, entryHeader; // (version, flags, count)
                        string format;
                        if (!ReadHeader(fs, entry, bEnd, out entrySize, out format, out entryHeader)) continue;
                        var head = new byte[28];
                        fs.Position = entry + entryHeader;
                        if (fs.Read(head, 0, 28) != 28) continue;
                        int channels = (head[16] << 8) | head[17], rate = (head[24] << 8) | head[25];
                        if (format == "mp4a")
                        {
                            long esds, esdsEnd;
                            int soundVersion = (head[8] << 8) | head[9]; // QuickTime's longer headers
                            long inside = entry + entryHeader + 28 + (soundVersion == 1 ? 16 : soundVersion == 2 ? 36 : 0);
                            if (!FindChild(fs, inside, entry + entrySize, "esds", out esds, out esdsEnd)) continue;
                            string cause = LookAtEsds(fs, esds, esdsEnd, channels, rate, found);
                            if (cause != null && found.Cause == null) found.Cause = cause;
                        }
                        else if (found.Cause == null)
                        {
                            string name = format == "Opus" ? "Opus" : format == "dtsc" || format == "dtsh" || format == "dtsl" || format == "dtse" ? "DTS"
                                : format == "ec-3" ? "Dolby Digital Plus" : format == "ac-4" ? "Dolby AC-4" : format == "mlpa" ? "Dolby TrueHD" : null;
                            if (name != null) found.Cause = "The sound in this file is " + name + ", which Windows can't play from this kind of file. The picture itself is fine.";
                        }
                    }
                    if (found.Cause == null) return null;
                    found.Fixable = found.Patches.Count > 0 && found.Cause.StartsWith("The sound in this file is AAC, but");
                    if (found.Fixable)
                    {
                        // Such files (saved in pieces) often don't say how long they are either: no time bar. The copy gets that too.
                        var length = LengthPatch(fs, moov, moovEnd);
                        if (length != null) found.Patches.Add(length.Value);
                    }
                    return found;
                }
            }
            catch { return null; }
        }

        // One AAC track's description. Returns what is wrong with it (null: nothing) and, for a missing
        // AudioSpecificConfig, adds the bytes that put one in without the box growing by a single byte.
        static string LookAtEsds(Stream fs, long start, long end, int channels, int rate, SoundTrouble found)
        {
            int length = (int)Math.Min(end - start, 4096);
            if (length < 12) return null;
            var d = new byte[length];
            fs.Position = start;
            if (fs.Read(d, 0, length) != length) return null;
            int at = 4; // (version, flags)
            int esLen, dcLen;
            if (!Descriptor(d, ref at, 0x03, out esLen)) return null;
            int esEnd = Math.Min(length, at + esLen), esHead = at;
            if (at + 3 > length) return null;
            int flags = d[at + 2];
            at += 3;
            if ((flags & 0x80) != 0) at += 2;
            if ((flags & 0x40) != 0 && at < length) at += 1 + d[at];
            if ((flags & 0x20) != 0) at += 2;
            int esHeadLength = at - esHead;
            if (!Descriptor(d, ref at, 0x04, out dcLen) || at + 13 > length) return null;
            int dcStart = at, dcEnd = Math.Min(length, at + dcLen);
            int objectType = d[at];
            if (objectType != 0x40 && objectType != 0x67) return null; // (MP3 and the like in an .mp4: Windows plays those)
            at += 13;
            int infoLen;
            if (at < dcEnd && d[at] == 0x05 && Descriptor(d, ref at, 0x05, out infoLen))
            {
                if (infoLen < 2 || at + 2 > length) return "The sound in this file is AAC with a damaged description. Windows refuses the whole file because of it.";
                int kind = d[at] >> 3;
                if (kind == 31) kind = 32 + (((d[at] & 7) << 3) | (d[at + 1] >> 5));
                string name = kind == 1 ? "AAC Main" : kind == 3 ? "AAC SSR" : kind == 4 ? "AAC LTP" : kind == 23 ? "AAC-LD" : kind == 39 ? "AAC-ELD" : kind == 42 ? "xHE-AAC" : null;
                return name == null ? null // AAC-LC, HE-AAC, HE-AAC v2: Windows plays these
                    : "The sound in this file is " + name + ", a rare kind of AAC that Windows 10 can't play. The picture itself is fine.";
            }
            // No AudioSpecificConfig. The same descriptors, written as short as they go, leave room for it.
            int rateIndex = Array.IndexOf(AacRates, rate);
            const string missing = "The sound in this file is AAC, but the short description every AAC track needs is missing (the tool that saved or downloaded it left it out). Windows refuses the whole file because of that; the picture and the sound themselves are fine.";
            if (rateIndex < 0 || channels < 1 || channels > 7) return missing;
            var config = new[] { (byte)((2 << 3) | (rateIndex >> 1)), (byte)(((rateIndex & 1) << 7) | (channels << 3)) }; // AAC-LC (HE-AAC announces itself in the sound)
            var rest = new List<byte>(); // what follows inside the ES descriptor (the "SL" descriptor), each written short
            int p = dcEnd;
            while (p + 2 <= esEnd)
            {
                int tag = d[p], q = p + 1, len;
                if (!DescriptorLength(d, ref q, out len) || q + len > length) return missing;
                rest.Add((byte)tag);
                rest.Add((byte)len);
                for (int i = 0; i < len; i++) rest.Add(d[q + i]);
                p = q + len;
            }
            int room = length - 4, smallest = 2 + esHeadLength + 2 + 13 + 4 + rest.Count;
            int spare = room - smallest; // filled by writing lengths the long way (up to 3 more bytes each)
            if (spare < 0 || spare > 9) return missing;
            int padEs = Math.Min(3, spare), padDc = Math.Min(3, spare - padEs), padInfo = spare - padEs - padDc;
            var o = new List<byte>();
            Action<int, int, int> open = (tag, len, pad) =>
            {
                o.Add((byte)tag);
                for (int i = 0; i < pad; i++) o.Add(0x80);
                o.Add((byte)len);
            };
            int infoSize = 2 + padInfo + 2, dcSize = 13 + infoSize;
            open(0x03, esHeadLength + 2 + padDc + dcSize + rest.Count, padEs);
            for (int i = 0; i < esHeadLength; i++) o.Add(d[esHead + i]);
            open(0x04, dcSize, padDc);
            for (int i = 0; i < 13; i++) o.Add(d[dcStart + i]);
            open(0x05, 2, padInfo);
            o.AddRange(config);
            o.AddRange(rest);
            if (o.Count != room) return missing;
            found.Patches.Add(new KeyValuePair<long, byte[]>(start + 4, o.ToArray()));
            return missing;
        }

        // For a file made of pieces ("fragmented") whose header gives its length as 0: the length worked out from
        // the pieces, as the bytes to write into the header (same size). Null when it has a length or none can be found.
        static KeyValuePair<long, byte[]>? LengthPatch(Stream fs, long moov, long moovEnd)
        {
            long mvhd, mvhdEnd;
            if (!FindChild(fs, moov, moovEnd, "mvhd", out mvhd, out mvhdEnd)) return null;
            var b = new byte[32];
            fs.Position = mvhd;
            if (fs.Read(b, 0, 20) != 20 || b[0] != 0) return null; // (version 1 headers: left alone)
            long movieScale = Int(b, 12);
            if (Int(b, 16) != 0 || movieScale <= 0) return null;
            // Each track's own clock (ticks a second) and usual sample length.
            var scale = new Dictionary<long, long>();
            var usual = new Dictionary<long, long>();
            long pos = moov, size, header;
            string type;
            while (ReadHeader(fs, pos, moovEnd, out size, out type, out header))
            {
                long inside = pos + header, insideEnd = pos + size;
                pos += size;
                if (type == "trak")
                {
                    long tkhd, tkhdEnd, mdia, mdiaEnd, mdhd, mdhdEnd;
                    if (!FindChild(fs, inside, insideEnd, "tkhd", out tkhd, out tkhdEnd) || !FindChild(fs, inside, insideEnd, "mdia", out mdia, out mdiaEnd) ||
                        !FindChild(fs, mdia, mdiaEnd, "mdhd", out mdhd, out mdhdEnd)) continue;
                    fs.Position = tkhd;
                    fs.Read(b, 0, 24);
                    long id = b[0] == 1 ? Int(b, 20) : Int(b, 12);
                    fs.Position = mdhd;
                    fs.Read(b, 0, 24);
                    scale[id] = b[0] == 1 ? Int(b, 20) : Int(b, 12);
                }
                else if (type == "mvex")
                {
                    long p = inside, s2, h2;
                    string t2;
                    while (ReadHeader(fs, p, insideEnd, out s2, out t2, out h2))
                    {
                        if (t2 == "trex") { fs.Position = p + h2; fs.Read(b, 0, 16); usual[Int(b, 4)] = Int(b, 12); }
                        p += s2;
                    }
                }
            }
            double longest = 0;
            pos = 0;
            int pieces = 0;
            while (ReadHeader(fs, pos, fs.Length, out size, out type, out header) && size >= 8)
            {
                if (type == "moof")
                {
                    pieces++;
                    long p = pos + header, end = pos + size, s2, h2;
                    string t2;
                    while (ReadHeader(fs, p, end, out s2, out t2, out h2))
                    {
                        if (t2 == "traf")
                        {
                            long id = 0, each = 0, starts = -1, ticks = 0, q = p + h2, s3, h3;
                            string t3;
                            while (ReadHeader(fs, q, p + s2, out s3, out t3, out h3))
                            {
                                fs.Position = q + h3;
                                if (t3 == "tfhd")
                                {
                                    fs.Read(b, 0, 28);
                                    long flags = Int(b, 0) & 0xFFFFFF;
                                    id = Int(b, 4);
                                    int at = 8 + ((flags & 1) != 0 ? 8 : 0) + ((flags & 2) != 0 ? 4 : 0);
                                    usual.TryGetValue(id, out each);
                                    if ((flags & 8) != 0) each = Int(b, at);
                                }
                                else if (t3 == "tfdt")
                                {
                                    fs.Read(b, 0, 12);
                                    starts = b[0] == 1 ? (Int(b, 4) << 32) | Int(b, 8) : Int(b, 4);
                                }
                                else if (t3 == "trun")
                                {
                                    fs.Read(b, 0, 8);
                                    long flags = Int(b, 0) & 0xFFFFFF, count = Int(b, 4);
                                    if ((flags & 0x100) == 0) ticks += count * each;
                                    else
                                    {
                                        int skip = ((flags & 1) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 4 : 0);
                                        int row = 4 + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                                        if (count > 1000000) return null;
                                        var rows = new byte[count * row];
                                        fs.Position = q + h3 + 8 + skip;
                                        if (fs.Read(rows, 0, rows.Length) != rows.Length) return null;
                                        for (int i = 0; i < count; i++) ticks += Int(rows, i * row);
                                    }
                                }
                                q += s3;
                            }
                            long perSecond;
                            if (starts >= 0 && scale.TryGetValue(id, out perSecond) && perSecond > 0) longest = Math.Max(longest, (starts + ticks) / (double)perSecond);
                        }
                        p += s2;
                    }
                }
                pos += size;
            }
            if (pieces == 0 || longest <= 0 || longest * movieScale > uint.MaxValue) return null;
            uint total = (uint)Math.Round(longest * movieScale);
            return new KeyValuePair<long, byte[]>(mvhd + 16, new[] { (byte)(total >> 24), (byte)(total >> 16), (byte)(total >> 8), (byte)total });
        }

        // How many sound tracks a file has: read straight from an MP4/MOV's own list of tracks (quick), else asked of Windows.
        public static int SoundTrackCount(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long moov, moovEnd;
                    if (FindChild(fs, 0, fs.Length, "moov", out moov, out moovEnd))
                    {
                        int found = 0;
                        long pos = moov, size, header;
                        string type;
                        while (ReadHeader(fs, pos, moovEnd, out size, out type, out header))
                        {
                            if (type == "trak" && Handler(fs, pos + header, pos + size) == "soun") found++;
                            pos += size;
                        }
                        return found;
                    }
                }
            }
            catch { }
            return VideoConverter.SoundTracks(file);
        }

        static bool Descriptor(byte[] d, ref int at, int tag, out int length)
        {
            length = 0;
            if (at >= d.Length || d[at] != tag) return false;
            at++;
            return DescriptorLength(d, ref at, out length);
        }

        static bool DescriptorLength(byte[] d, ref int at, out int length)
        {
            length = 0;
            for (int i = 0; i < 4 && at < d.Length; i++)
            {
                int b = d[at++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }
    }

    partial class ViewerWindow
    {
        internal string lastFixResult; // tests
        System.Windows.Threading.DispatcherTimer silentTimer;

        // Windows' player doesn't always say that it gave up on a file whose sound it can't read: sometimes it
        // just never finishes opening it (and the old file would stay on screen for good). So a video or song that
        // still isn't open a moment after it was asked for gets its sound description looked at (off the window's
        // thread); if that is what's wrong, the file counts as failed, with the reason.
        void WatchForSilentFailure()
        {
            if (silentTimer == null)
            {
                silentTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                silentTimer.Tick += delegate
                {
                    silentTimer.Stop();
                    var s = wanted;
                    if (s == null || !s.IsVideo || s.Failed || s.File == null) return;
                    if (s.Ready && (!s.IsAudio || s.Video.NaturalDuration.HasTimeSpan)) return; // it opened
                    string file = s.File;
                    int token = s.Token;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        var trouble = VideoInfo.SoundProblem(file);
                        if (trouble == null) return;
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (s.Token != token || s.Failed || s.File != file) return;
                            if (s.Ready && (!s.IsAudio || s.Video.NaturalDuration.HasTimeSpan)) return;
                            Log("sound can't be read: " + Path.GetFileName(file));
                            OnLoadFailed(s, new Exception("the sound can't be read"));
                        }));
                    });
                };
            }
            silentTimer.Stop();
            var w = wanted;
            if (w != null && w.IsVideo && !w.Failed && (!w.Ready || w.IsAudio)) silentTimer.Start();
        }

        // Test (--hunt <out> --case soundfix; scratch folder with broken.mp4 and broken.m4a = AAC without its description,
        // good.mp4): the message, the button, the copy, and that the copy plays with sound.
        void HuntSoundFix()
        {
            Func<string> screen = () => "title '" + F<System.Windows.Controls.TextBlock>("MessageTitle").Text + "', text '" + F<System.Windows.Controls.TextBlock>("MessageBody").Text.Replace("\n", " ") +
                "', buttons: " + (F<FrameworkElement>("MessageFixBtn").IsVisible ? "[" + F<System.Windows.Controls.Button>("MessageFixBtn").Content + "] " : "") +
                (F<FrameworkElement>("MessageBtn").IsVisible ? "[" + F<System.Windows.Controls.Button>("MessageBtn").Content + "]" : "");
            Action<string, Action> fix = null;
            fix = (name, then) => HuntOpenAny(name, () =>
            {
                var s = shown;
                HuntLog(name + ": failed " + s.Failed + "; " + screen() + (s.Failed && F<FrameworkElement>("MessageFixBtn").IsVisible ? "" : " FAIL"));
                Snap("soundfix_" + Path.GetExtension(name).TrimStart('.'));
                long before = new FileInfo(s.File).Length;
                string hash = Md5(s.File);
                MakePlayableCopy();
                Until(() => !converting && lastFixResult != null, 60000, () => After(2500, () =>
                {
                    var now = shown;
                    lastFixResult = lastFixResult ?? "nothing happened";
                    string copy = lastFixResult.StartsWith("made ") ? lastFixResult.Substring(5) : null;
                    HuntLog("  " + lastFixResult + "; message '" + F<System.Windows.Controls.TextBlock>("ToastText").Text + "'" + (copy != null ? "" : " FAIL"));
                    if (copy != null)
                    {
                        HuntLog("  original untouched: " + (Md5(s.File) == hash) + ", copy " + new FileInfo(copy).Length + " bytes (original " + before + ")" +
                            (Md5(s.File) == hash && new FileInfo(copy).Length == before ? "" : " FAIL"));
                        bool on = now != null && string.Equals(now.File, copy, StringComparison.OrdinalIgnoreCase);
                        HuntLog("  now showing " + (now == null ? "nothing" : Path.GetFileName(now.File)) + ": failed " + (now != null && now.Failed) + ", has sound " + (now != null && now.IsVideo && now.Video.HasAudio) +
                            ", length " + (now != null && now.IsVideo && now.Video.NaturalDuration.HasTimeSpan ? now.Video.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.00") : "?") +
                            ", fix button " + F<FrameworkElement>("MessageFixBtn").IsVisible + (on && !now.Failed && now.Video.HasAudio && !F<FrameworkElement>("MessageFixBtn").IsVisible ? "" : " FAIL"));
                        HuntLog("  leftovers: " + string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(copy), "*.tmp").Select(Path.GetFileName)));
                    }
                    then();
                }));
            });
            After(1200, () => fix("broken.mp4", () => fix("broken.m4a", () => HuntOpenAny("good.mp4", () =>
            {
                HuntLog("good.mp4: failed " + shown.Failed + ", fix button " + F<FrameworkElement>("MessageFixBtn").IsVisible + (shown.Failed || F<FrameworkElement>("MessageFixBtn").IsVisible ? " FAIL" : ""));
                HuntDone();
            }))));
        }

        static string Md5(string file)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(md5.ComputeHash(fs));
        }

        // Like HuntOpen, but also done when the file failed to open.
        void HuntOpenAny(string name, Action then)
        {
            for (int i = 0; i < files.Count; i++)
                if (Path.GetFileName(files[i]).Equals(name, StringComparison.OrdinalIgnoreCase)) ShowIndex(i, 1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Until(() => shown != null && shown.Ready && Path.GetFileName(shown.File).Equals(name, StringComparison.OrdinalIgnoreCase), 45000, delegate { HuntLog(name + " settled after " + sw.ElapsedMilliseconds + " ms; wanted " + (wanted == null ? "-" : Path.GetFileName(wanted.File) + " ready " + wanted.Ready + " failed " + wanted.Failed) + ", shown " + (shown == null ? "-" : Path.GetFileName(shown.File))); After(700, then); });
        }

        // "<name> (playable).mp4" next to the original: the same bytes with the missing description filled in.
        void MakePlayableCopy()
        {
            var s = shown;
            if (s == null || !s.Failed) return;
            if (converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            string file = s.File, folder = Path.GetDirectoryName(file), name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
            string target = Path.Combine(folder, name + " (playable)" + ext);
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, name + " (playable " + n + ")" + ext);
            string temp = target + ".fvfix.tmp";
            lastFixResult = null;
            RunJob("Making a playable copy", delegate(Action<string, double> progress, CancelFlag cancel)
            {
                var trouble = VideoInfo.SoundProblem(file);
                if (trouble == null || !trouble.Fixable) throw new Exception("this file can't be repaired this way.");
                long total = new FileInfo(file).Length, done = 0;
                using (var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var to = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var block = new byte[1 << 20];
                    int got;
                    while ((got = from.Read(block, 0, block.Length)) > 0)
                    {
                        if (cancel.Cancelled) return;
                        to.Write(block, 0, got);
                        done += got;
                        progress(null, total > 0 ? 0.95 * done / total : 0);
                    }
                    foreach (var patch in trouble.Patches)
                    {
                        to.Position = patch.Key;
                        to.Write(patch.Value, 0, patch.Value.Length);
                    }
                }
                // Checked before it gets its name: the description reads back fine and Windows finds the sound.
                if (VideoInfo.SoundProblem(temp) != null || VideoConverter.SoundTracks(temp) < 1)
                    throw new Exception("Windows still can't read the sound in the copy.");
                try { File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(file).AddMilliseconds(10)); } catch { } // next to the original when sorted by date
                File.Move(temp, target);
                progress(null, 1);
            }, delegate(string error, bool cancelled)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                if (cancelled || error != null)
                {
                    lastFixResult = cancelled ? "cancelled" : "failed: " + error;
                    ShowToast(cancelled ? "Cancelled." : "Couldn't make a playable copy: " + error, null, null, 7, cancelled ? null : "error");
                    return;
                }
                lastFixResult = "made " + target;
                ShowToast("Saved " + Path.GetFileName(target) + ". The original is untouched.", "Show in folder", delegate
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\"");
                    HideToast(null);
                }, 8, "done");
                // Straight to the copy (the folder watcher has usually added it by now; give it a moment if not).
                int tries = 0;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                timer.Tick += delegate
                {
                    int i = files.FindIndex(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
                    if (i < 0 && ++tries < 20) return;
                    timer.Stop();
                    if (i >= 0 && shown != null && string.Equals(shown.File, file, StringComparison.OrdinalIgnoreCase)) ShowIndex(i, 1);
                };
                timer.Start();
            });
        }
    }
}
