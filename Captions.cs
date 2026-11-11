// FastViewer: automatic captions (1.24.0). The speech in a video is written out as subtitles by Whisper
// (whisper.cpp by Georgi Gerganov, model "small"), running on this PC: nothing is uploaded and no internet is
// needed once the add-on is there (Addons.cs; Settings > Videos > Automatic captions). FastViewer takes the
// sound out of the video itself (Windows' own decoders, all sound tracks mixed, 16 kHz mono), hands that to
// whisper-cli.exe and shows each line as soon as it's written. Captions are remembered per video in
// %LOCALAPPDATA%\FastViewer\Captions, so the next time they are just there.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace FastViewer
{
    static class Whisper
    {
        public static readonly Addon Addon = new Addon
        {
            Id = "Whisper", Name = "Automatic captions (Whisper)",
            Needs = new[] { "whisper-cli.exe", "model.bin" },
            Parts = new[]
            {
                new AddonPart
                {
                    Url = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip", Save = "whisper.zip",
                    Unpack = new[] { "whisper-cli.exe", "*.dll" },
                    Sha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb", Size = 8928640
                },
                new AddonPart
                {
                    Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small-q5_1.bin", Save = "model.bin",
                    Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", Size = 190085487
                },
            }
        };

        public static readonly string[] LanguageNames = { "Find out by itself", "English", "Dutch", "German", "Russian", "Slovak", "Chinese", "French", "Spanish", "Polish", "Ukrainian", "Italian", "Portuguese", "Turkish", "Japanese", "Korean" };
        public static readonly string[] LanguageCodes = { "auto", "en", "nl", "de", "ru", "sk", "zh", "fr", "es", "pl", "uk", "it", "pt", "tr", "ja", "ko" };

        static string CaptionsDir { get { return Path.Combine(Path.GetDirectoryName(Addon.Dir), "Captions"); } }

        // Where a video's captions are kept: by its path, size and date, so an edited video gets new ones.
        public static string CaptionsFile(string video)
        {
            try
            {
                var f = new FileInfo(video);
                using (var sha = SHA1.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(f.FullName.ToLowerInvariant() + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks));
                    return Path.Combine(CaptionsDir, string.Concat(hash.Take(10).Select(b => b.ToString("x2"))) + ".srt");
                }
            }
            catch { return null; }
        }

        public static void Keep(string video, List<Cue> cues)
        {
            try
            {
                string file = CaptionsFile(video);
                if (file == null) return;
                Directory.CreateDirectory(CaptionsDir);
                File.WriteAllText(file, SubtitleFile.ToSrt(cues), new UTF8Encoding(true));
            }
            catch { }
        }

        static readonly Regex Line = new Regex(@"^\[(\d+):(\d+):(\d+)[.,](\d+)\s*-->\s*(\d+):(\d+):(\d+)[.,](\d+)\]\s*(.*)$", RegexOptions.Compiled);
        static readonly Regex Noise = new Regex(@"^[\[\(\*].*[\]\)\*]$|^[\s♪♫\-\.]*$", RegexOptions.Compiled); // [Music], (applause), *sighs*, a lone note

        public static readonly List<string> Log = new List<string>();

        // Writes out the speech of a 16 kHz mono .wav. onCue gets each line as it's made (on another thread).
        public static List<Cue> Transcribe(string wav, double seconds, string language, Action<double> progress, Action<Cue> onCue, CancelFlag cancel)
        {
            var cues = new List<Cue>();
            var said = new List<string>();
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)); // the cores themselves; the rest stays for the video and you
            string arguments = "-m \"" + Addon.File("model.bin") + "\" -f \"" + wav + "\" -l " + language + " -t " + threads + " -ml 84 -sow -np";
            // Whisper works through the sound half a minute at a time and only then prints that part: in between,
            // the progress moves on at the speed measured so far, so the line never sits still.
            double real = 0, speed = 3; // of the sound done; seconds of sound per second of work (a first guess)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var sinceReal = System.Diagnostics.Stopwatch.StartNew();
            int code = Helper.Run(Addon.File("whisper-cli.exe"), arguments, delegate(string text)
            {
                var m = Line.Match(text.Trim());
                if (!m.Success) { lock (said) { said.Add(text.Trim()); if (Log.Count < 200) Log.Add(text.Trim()); } return; }
                var c = new Cue { From = At(m, 1), To = At(m, 5), Text = m.Groups[9].Value.Trim() };
                if (seconds > 0)
                {
                    real = Math.Max(real, Math.Min(1, c.To / seconds));
                    if (clock.Elapsed.TotalSeconds > 1) speed = Math.Max(0.2, real * seconds / clock.Elapsed.TotalSeconds);
                    sinceReal.Restart();
                    progress(real);
                }
                if (c.To <= c.From || Noise.IsMatch(c.Text)) return;
                // The same words over and over is what Whisper does with silence or music: not speech.
                if (cues.Count >= 2 && cues[cues.Count - 1].Text == c.Text && cues[cues.Count - 2].Text == c.Text) return;
                cues.Add(c);
                if (onCue != null) onCue(c);
            }, cancel, true, delegate
            {
                if (seconds <= 0) return;
                double part = Math.Min(1 - real, 30 / seconds); // the half minute being worked on
                progress(Math.Min(0.99, real + part * 0.9 * (1 - Math.Exp(-sinceReal.Elapsed.TotalSeconds * speed / 30))));
            }, true);
            if (code != 0)
            {
                string why = unchecked((uint)code) == 0xC0000135 ? "a part of Windows it needs is missing (install \"Microsoft Visual C++ Redistributable\" x64)"
                    : unchecked((uint)code) == 0xC000001D ? "this PC's processor is too old for it"
                    : said.Count > 0 ? said[said.Count - 1] : "it stopped with error " + code;
                throw new Exception(why);
            }
            return cues;
        }

        static double At(Match m, int g)
        {
            return int.Parse(m.Groups[g].Value) * 3600 + int.Parse(m.Groups[g + 1].Value) * 60 + int.Parse(m.Groups[g + 2].Value) + int.Parse(m.Groups[g + 3].Value.PadRight(3, '0').Substring(0, 3)) / 1000.0;
        }
    }

    static partial class VideoConverter
    {
        // The sound of a video or song as a 16 kHz, mono, 16-bit .wav (what speech recognition wants): every sound
        // track mixed together (a game clip can have the voices on their own track). Returns its length in seconds.
        public static double SpeechWav(string source, string target, double seconds, Action<double> progress, CancelFlag cancel)
        {
            const int Rate = 16000, Block = 1 << 20;
            Check(MFStartup(0x00020070, 0), "starting Windows' video tools");
            IMFSourceReader reader = null;
            try
            {
                Check(MFCreateSourceReaderFromURL(source, null, out reader), "opening the file");
                reader.SetStreamSelection(ALL_STREAMS, false);
                var shape = new Dictionary<int, int[]>();    // stream -> channels, samples a second
                for (int i = 0; i < 64; i++)
                {
                    IMFMediaType native;
                    if (reader.GetNativeMediaType(i, 0, out native) < 0) break;
                    Guid major;
                    native.GetGUID(ref G.MF_MT_MAJOR_TYPE, out major);
                    Marshal.ReleaseComObject(native);
                    if (major != G.MFMediaType_Audio || reader.SetStreamSelection(i, true) < 0) continue;
                    IMFMediaType want, got = null;
                    MFCreateMediaType(out want);
                    want.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Audio);
                    want.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFAudioFormat_PCM);
                    want.SetUINT32(ref G.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    int channels = 0, rate = 0;
                    if (reader.SetCurrentMediaType(i, IntPtr.Zero, want) >= 0 && reader.GetCurrentMediaType(i, out got) >= 0)
                    {
                        // Windows resamples properly when asked for exactly what is wanted; where it won't, the
                        // simpler conversion below takes whatever it delivers.
                        IMFMediaType exact, gotExact;
                        MFCreateMediaType(out exact);
                        exact.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Audio);
                        exact.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFAudioFormat_PCM);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, 1);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Rate * 2);
                        if (reader.SetCurrentMediaType(i, IntPtr.Zero, exact) >= 0 && reader.GetCurrentMediaType(i, out gotExact) >= 0) got = gotExact;
                        else if (reader.SetCurrentMediaType(i, IntPtr.Zero, want) < 0 || reader.GetCurrentMediaType(i, out got) < 0) { reader.SetStreamSelection(i, false); continue; }
                        got.GetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, out channels);
                        got.GetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, out rate);
                    }
                    if (channels < 1 || rate < 4000) { reader.SetStreamSelection(i, false); continue; }
                    shape[i] = new[] { channels, rate };
                }
                if (shape.Count == 0) throw new Exception("there is no sound in this file (or Windows can't decode it)");

                var mix = new List<short[]>();               // the result, in blocks
                long length = 0;
                var next = new Dictionary<int, long>();      // per stream: where its next sample goes...
                var carry = new Dictionary<int, double>();   // ...and how far into its next buffer the next output sample starts
                int ended = 0;
                byte[] bytes = new byte[0];
                while (ended < shape.Count)
                {
                    if (cancel != null && cancel.Cancelled) throw new OperationCanceledException();
                    int index, flags;
                    long time;
                    IMFSample sample;
                    Check(reader.ReadSample(ANY_STREAM, 0, out index, out flags, out time, out sample), "reading the sound");
                    if ((flags & 1) != 0) throw new Exception("Windows couldn't read the sound of this file");
                    if ((flags & 2) != 0) ended++;
                    if ((flags & 0x20) != 0 && shape.ContainsKey(index))
                    {
                        // The decoder found out more about the sound once it started (some AAC really has twice
                        // the samples its header says): go by what it delivers now.
                        IMFMediaType now;
                        if (reader.GetCurrentMediaType(index, out now) >= 0)
                        {
                            int ch, hz;
                            now.GetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, out ch);
                            now.GetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, out hz);
                            if (ch >= 1 && hz >= 4000) shape[index] = new[] { ch, hz };
                            Marshal.ReleaseComObject(now);
                        }
                    }
                    if (sample == null) continue;
                    try
                    {
                        int[] sh;
                        IMFMediaBuffer buffer;
                        if (!shape.TryGetValue(index, out sh) || sample.ConvertToContiguousBuffer(out buffer) < 0) continue;
                        IntPtr data;
                        int max, size;
                        buffer.Lock(out data, out max, out size);
                        if (bytes.Length < size) bytes = new byte[size];
                        Marshal.Copy(data, bytes, 0, size);
                        buffer.Unlock();
                        Marshal.ReleaseComObject(buffer);

                        int channels = sh[0], frames = size / (2 * channels);
                        double step = sh[1] / (double)Rate; // source frames per output sample
                        long at = (long)Math.Round(time * (double)Rate / 1e7);
                        long expected;
                        double pos = 0;
                        // Buffers follow each other exactly; only a real gap or jump (over 50 ms) moves the writing place.
                        if (next.TryGetValue(index, out expected) && Math.Abs(expected - at) < Rate / 20) { at = expected; pos = carry[index]; }
                        while (pos < frames - 1e-9)
                        {
                            int a = (int)pos, b = Math.Min(frames, Math.Max(a + 1, (int)(pos + step)));
                            long sum = 0;
                            for (int f = a; f < b; f++)
                                for (int c = 0; c < channels; c++) sum += (short)(bytes[(f * channels + c) * 2] | (bytes[(f * channels + c) * 2 + 1] << 8));
                            int value = (int)(sum / ((b - a) * channels));
                            if (at >= 0)
                            {
                                int blockIndex = (int)(at / Block), offset = (int)(at % Block);
                                while (mix.Count <= blockIndex) mix.Add(new short[Block]);
                                int mixed = mix[blockIndex][offset] + value;
                                mix[blockIndex][offset] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mixed));
                                if (at + 1 > length) length = at + 1;
                            }
                            at++;
                            pos += step;
                        }
                        next[index] = at;
                        carry[index] = pos - frames;
                        if (progress != null && seconds > 0) progress(Math.Min(1, time / 1e7 / seconds));
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                if (length < Rate / 4) throw new Exception("there is no sound in this file");

                using (var output = new BinaryWriter(File.Create(target)))
                {
                    int dataBytes = (int)Math.Min(int.MaxValue - 100, length * 2);
                    output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + dataBytes); output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(Rate); output.Write(Rate * 2); output.Write((short)2); output.Write((short)16);
                    output.Write(Encoding.ASCII.GetBytes("data")); output.Write(dataBytes);
                    var raw = new byte[Block * 2];
                    long left = dataBytes / 2;
                    foreach (var block in mix)
                    {
                        int n = (int)Math.Min(Block, left);
                        if (n <= 0) break;
                        Buffer.BlockCopy(block, 0, raw, 0, n * 2);
                        output.Write(raw, 0, n * 2);
                        left -= n;
                    }
                }
                return length / (double)Rate;
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }
    }

    partial class ViewerWindow
    {
        string captionLanguage = "auto"; // settings line captionLang=
        internal string lastCaptionResult; // tests

        void MakeCaptions()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            if (!Environment.Is64BitOperatingSystem) { ShowToast("Automatic captions need 64-bit Windows.", null, null, 6, "error"); return; }
            if (converting) { ShowToast("Already busy saving, one moment...", null, null, 3, null); return; }
            if (!Whisper.Addon.Installed)
            {
                GetAddon(Whisper.Addon, "Automatic captions need", "After that they work without internet.", delegate { if (shown == s) MakeCaptions(); });
                return;
            }
            string video = s.File, language = captionLanguage;
            var d = s.Video.NaturalDuration;
            double seconds = d.HasTimeSpan ? d.TimeSpan.TotalSeconds : 0;
            var live = new List<Cue>();
            List<Cue> all = null;
            string wav = Path.Combine(Path.GetTempPath(), "FastViewer", "speech_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
            RunJob("Making captions", delegate(Action<string, double> progress, CancelFlag cancel)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(wav));
                    progress("Making captions: listening", 0);
                    double length = VideoConverter.SpeechWav(video, wav, seconds, p => progress("Making captions: listening", p * 0.05), cancel);
                    progress("Making captions: writing", 0.05);
                    all = Whisper.Transcribe(wav, length, language, p => progress("Making captions: writing", 0.05 + 0.95 * p), delegate(Cue c)
                    {
                        // Each line shows as soon as it exists, while the rest is still being written.
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            live.Add(c);
                            if (shown == null || shown.File != video) return;
                            if (cues != live) { subsLookup++; if (!subsOn) { subsOn = true; SaveSettings(); } SetCues(video, live, "auto"); }
                            else { UpdateSubButton(); SubTick(); }
                        }));
                    }, cancel);
                }
                catch (OperationCanceledException) { }
                finally { try { File.Delete(wav); } catch { } }
            },
            delegate(string error, bool cancelled)
            {
                lastCaptionResult = cancelled ? "cancelled" : error ?? (all == null ? "nothing" : all.Count + " lines");
                if (snapDir != null) Log("captions: " + lastCaptionResult);
                if (cancelled || (error == null && all == null))
                {
                    if (shown != null && shown.File == video && cues == live) FindSubtitles(); // the half-made ones go
                    ShowToast("Cancelled.", null, null, 4, null);
                    return;
                }
                if (error != null) { ShowToast("Couldn't make captions: " + error + ".", null, null, 10, "error"); return; }
                if (all.Count == 0)
                {
                    if (shown != null && shown.File == video && cues == live) FindSubtitles();
                    ShowToast("No speech was found in this " + (IsAudioFile(video) ? "song" : "video") + ".", null, null, 6, "info");
                    return;
                }
                Whisper.Keep(video, all);
                if (shown != null && shown.File == video)
                {
                    subsLookup++;
                    if (!subsOn) { subsOn = true; SaveSettings(); }
                    SetCues(video, all, "auto");
                }
                ShowToast("Captions ready (" + all.Count + " lines). They'll be there next time too.", null, null, 6, "done");
            });
        }

        void SaveCaptionsAs()
        {
            if (!HasSubs) return;
            var list = cues.ToList();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save captions",
                FileName = Path.GetFileNameWithoutExtension(cuesFor) + ".srt",
                InitialDirectory = Path.GetDirectoryName(cuesFor),
                DefaultExt = ".srt",
                Filter = "Subtitles (SubRip)|*.srt"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, SubtitleFile.ToSrt(list), new UTF8Encoding(true));
                ShowToast("Saved " + Path.GetFileName(dlg.FileName), null, null, 5, "done");
            }
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, null, null, 6, "error"); }
        }

        void AddCaptionSettings()
        {
            AddAddonCard(Whisper.Addon, "", "Automatic captions",
                "FastViewer can write the subtitles itself by listening to a video, on this PC: nothing is uploaded. Video \"...\" > Make captions " +
                "automatically. This needs a one-time download: Whisper (the program from github.com/ggml-org/whisper.cpp and its speech model " +
                "from huggingface.co). Afterwards it works without internet. A minute of video takes roughly 5 to 20 seconds.",
                () => Environment.Is64BitOperatingSystem ? null : "This needs 64-bit Windows.", null);
            AddCard("", "Language spoken in your videos",
                "For automatic captions. Pick the language when it guesses wrong.",
                Dropdown(Whisper.LanguageNames, () => Math.Max(0, Array.IndexOf(Whisper.LanguageCodes, captionLanguage)), i => captionLanguage = Whisper.LanguageCodes[i]));
        }
    }
}
