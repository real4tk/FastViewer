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

        // Better quality (1.25.0): the large model, an extra download next to the small one. Whichever is there is used.
        public static readonly Addon Large = new Addon
        {
            Id = "WhisperLarge", Name = "Better quality captions (large Whisper model)",
            Needs = new[] { "model.bin" },
            Parts = new[]
            {
                new AddonPart
                {
                    Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin", Save = "model.bin",
                    Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", Size = 574041195
                },
            }
        };

        public static bool UsesLarge { get { return Large.Installed; } }
        static string ModelFile { get { return UsesLarge ? Large.File("model.bin") : Addon.File("model.bin"); } }

        public static readonly string[] LanguageNames = { "Find out by itself", "English", "Dutch", "German", "Russian", "Slovak", "Chinese", "French", "Spanish", "Polish", "Ukrainian", "Italian", "Portuguese", "Turkish", "Japanese", "Korean" };
        public static readonly string[] LanguageCodes = { "auto", "en", "nl", "de", "ru", "sk", "zh", "fr", "es", "pl", "uk", "it", "pt", "tr", "ja", "ko" };

        internal static string CaptionsFolder { get { return CaptionsDir; } }
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

        // With nothing it can make out, Whisper tends to answer with what such sound had under it in the videos it
        // learned from: the credits of whoever made the subtitles, or a description of the music. Never said in the video.
        static readonly Regex Made = new Regex(
            @"DimaTorzok|^\W*Субтитры\b|Субтитры\s+(с?делал|создавал|подогнал)|Редактор субтитров|Корректор\s+\p{Lu}\.|Продолжение следует|Amara\.org|" +
            @"subtitles\s+(made\s+)?by\b|(thanks|thank you) for watching|Untertitel\s+(der|von|im Auftrag)|Sous-titres\s+(réalisés|par)\b|" +
            @"Ondertitel\w*\s+(door|gemaakt)|Subtítulos\s+(por|realizados)|Napisy stworzone|Titulky vytvořil|字幕\s*(由|by|志愿者|制作)|" +
            @"^\W*((спокойная|весёлая|веселая|грустная|динамичная|торжественная|тревожная|лирическая|звучит)\s+)?музыка\W*$|^\W*(music|muziek|musik|musique|música|hudba)\W*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static readonly List<string> Log = new List<string>();

        // Writes out the speech of a 16 kHz mono .wav. onCue gets each line as it's made (on another thread).
        public static List<Cue> Transcribe(string wav, double seconds, string language, Action<double> progress, Action<Cue> onCue, CancelFlag cancel, bool parakeet = false)
        {
            var cues = new List<Cue>();
            var said = new List<string>();
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)); // the cores themselves; the rest stays for the video and you
            string arguments = "-m \"" + ModelFile + "\" -f \"" + wav + "\" -l " + language + " -t " + threads + " -ml 84 -sow -np -sns -mc 0";
            // (1.25.0, for songs. -sns: Whisper may not answer "[MUSIC]" or a note sign for a stretch of sound; with singing it
            // did that for half a minute at a time, at the toss of a coin. -mc 0: each half minute is written without looking
            // at the text before it, or one sung line gets repeated to the end of a song. Measured on nine songs with known
            // words: 42% of the words wrong before, 34% with both; the large model 23%.)
            // Whisper works through the sound half a minute at a time and only then prints that part: in between,
            // the progress moves on at the speed measured so far, so the line never sits still.
            double real = 0, speed = 3; // of the sound done; seconds of sound per second of work (a first guess)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var sinceReal = System.Diagnostics.Stopwatch.StartNew();
            // 1.28.0: Parakeet (Parakeet.cs) prints its lines the same way, from a hidden FastViewer.
            int code = Helper.Run(parakeet ? System.Reflection.Assembly.GetExecutingAssembly().Location : Addon.File("whisper-cli.exe"),
                parakeet ? Parakeet.Arguments(wav, threads) : arguments, delegate(string text)
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
                if (c.To <= c.From || Noise.IsMatch(c.Text) || Made.IsMatch(c.Text)) return;
                // The same words over and over is what Whisper does with silence or music: not speech.
                // (A chorus does repeat a line, so it takes four in a row before a fifth is left out.)
                if (cues.Count >= 4 && cues.Skip(cues.Count - 4).All(x => x.Text == c.Text)) return;
                cues.Add(c);
                if (onCue != null) onCue(new Cue { From = Math.Max(c.From, c.To - ReadingTime(c.Text)), To = c.To, Text = c.Text }); // (shown while writing: no longer than its reading time)
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
                    : said.Count > 0 ? (said[said.Count - 1].StartsWith("E ") ? said[said.Count - 1].Substring(2) : said[said.Count - 1]) : "it stopped with error " + code;
                throw new Exception(why);
            }
            return Tidy(cues); // (1.27.0: repeated blocks, labels, reading time)
        }


        // ---------------------------------------------------------------- tidying (1.27.0)

        // How long a line may stay: 1.5 s plus 60 ms a character (about 16 a second, a usual reading speed), 7 s at most.
        public static double ReadingTime(string text)
        {
            return Math.Min(MaxShown, 1.5 + 0.06 * (text ?? "").Length);
        }
        public const double MaxShown = 7;

        static readonly Regex Label = new Regex(@"^[^\p{Ll}]*\p{Lu}[^\p{Ll}]*\p{Lu}[^\p{Ll}]*\p{Lu}[^\p{Ll}]*$", RegexOptions.Compiled); // ТЕЛЕФОННЫЙ ЗВОНОК, PHONE RINGS
        static string Plain(string text) { return new string((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }

        // What Whisper wrote, made fit to show (Measured on a full film, see FEATURES.md 1.27.0):
        // 1. a block of 2-10 lines written twice in a row (it went over the same sound again) keeps only the second
        //    copy, which is re-timed over the time both took when its own times are made-up even steps;
        // 2. the same sound label (all capitals) again within 5 s becomes one line;
        // 3. a line ends when the next begins, and after its reading time at the latest (a line stayed up to 30 s);
        //    a very short line gets up to 1.2 s when nothing follows at once.
        public static List<Cue> Tidy(List<Cue> raw, List<string> notes = null)
        {
            var c = raw.Where(x => x != null && x.To > x.From && !string.IsNullOrWhiteSpace(x.Text))
                       .OrderBy(x => x.From).Select(x => new Cue { From = x.From, To = x.To, Text = x.Text.Trim() }).ToList();
            // 1. repeated blocks
            for (int i = 0; i < c.Count; i++)
                for (int k = Math.Min(10, (c.Count - i) / 2); k >= 2; k--)
                {
                    bool same = true;
                    for (int j = 0; j < k && same; j++) same = Plain(c[i + j].Text) == Plain(c[i + k + j].Text) && Plain(c[i + j].Text).Length > 0;
                    if (!same || c[i + k].From - c[i + k - 1].To > 30) continue;
                    double first = c[i].From;
                    var kept = c.GetRange(i + k, k);
                    if (notes != null) notes.Add("block of " + k + " at " + first.ToString("0.0") + " s written twice, kept the copy at " + kept[0].From.ToString("0.0") + " s");
                    c.RemoveRange(i, k);
                    if (MadeUpSteps(kept))
                    {
                        // The second copy is where the words really are (Whisper wrote them early first, then again as it got
                        // there: the film at 49:08, said 49:18-49:30). Its own steps are made up, so its lines are spread by
                        // reading time from where that copy starts up to the next line. (1.27.0 test build: spread over both
                        // copies, which put them 10 s early.)
                        double from = kept[0].From, to = i + k < c.Count ? c[i + k].From : kept[k - 1].To;
                        double want = kept.Sum(x => ReadingTime(x.Text));
                        double scale = to > from && want > to - from ? (to - from) / want : 1;
                        double at = from;
                        foreach (var x in kept)
                        {
                            x.From = at;
                            x.To = at + ReadingTime(x.Text) * scale;
                            at = x.To;
                        }
                    }
                    k = 0; i = Math.Max(-1, i - 2); // look again from a little before (blocks can come three times)
                    break;
                }
            // 2. the same sound label again and again
            for (int i = c.Count - 1; i > 0; i--)
                if (Label.IsMatch(c[i].Text) && Plain(c[i].Text) == Plain(c[i - 1].Text) && c[i].From - c[i - 1].To < 5)
                {
                    c[i - 1].To = Math.Max(c[i - 1].To, c[i].To);
                    c.RemoveAt(i);
                }
            // 3. how long each line stays
            for (int i = 0; i < c.Count; i++)
            {
                double next = i + 1 < c.Count ? c[i + 1].From : double.MaxValue;
                double read = ReadingTime(c[i].Text);
                if (c[i].To - c[i].From > read) c[i].From = c[i].To - read; // too long: Whisper starts such a line early, in the quiet before it (the end is right more often: on the film 11.9% instead of 14.0% of the spoken lines without text)
                double to = c[i].To;
                if (to - c[i].From < 1.2) to = Math.Max(to, Math.Min(c[i].From + Math.Min(1.2, read), next)); // (a word or two still gets read)
                c[i].To = Math.Min(to, Math.Max(next, c[i].From + 0.3));
            }
            return c;
        }

        // Times Whisper falls back to when it lost its place: lines back to back in whole seconds.
        static bool MadeUpSteps(List<Cue> lines)
        {
            int even = lines.Count(x => Math.Abs((x.To - x.From) - Math.Round(x.To - x.From)) < 0.005);
            bool touching = lines.Zip(lines.Skip(1), (a, b) => Math.Abs(b.From - a.To) < 0.005).All(t => t);
            return touching && even * 2 >= lines.Count;
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
        public static double SpeechWav(string source, string target, double seconds, Action<double> progress, CancelFlag cancel, int Rate = 16000, int outChannels = 1)
        {
            const int Block = 1 << 20; // (Rate 44100 with 2 channels is what the voice isolation wants: Vocals.cs)
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
                    int nativeChannels;
                    if (native.GetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, out nativeChannels) < 0) nativeChannels = 0;
                    Marshal.ReleaseComObject(native);
                    // 5.1 / 7.1 (a film): the voices are in the centre channel, so mono is made here from the centre and a
                    // little of the front left and right (1.27.0; Windows' own mix buried them under music and effects: on a
                    // film's hard parts 17% of the spoken lines got no text, 4% this way).
                    bool surround = outChannels == 1 && (nativeChannels == 6 || nativeChannels == 8);
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
                        exact.SetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, surround ? nativeChannels : outChannels);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, Rate);
                        exact.SetUINT32(ref G.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2 * (surround ? nativeChannels : outChannels));
                        exact.SetUINT32(ref G.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Rate * 2 * (surround ? nativeChannels : outChannels));
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
                            for (int oc = 0; oc < outChannels && at >= 0; oc++)
                            {
                                // Mono out: every channel; stereo out: left ones left, right ones right (mono sound on both).
                                long sum = 0;
                                int count = 0;
                                if (outChannels == 1 && (channels == 6 || channels == 8)) // centre + 0.3 x (front left + front right)
                                {
                                    for (int f = a; f < b; f++)
                                    {
                                        int o = f * channels * 2;
                                        int left = (short)(bytes[o] | (bytes[o + 1] << 8)), right = (short)(bytes[o + 2] | (bytes[o + 3] << 8)), centre = (short)(bytes[o + 4] | (bytes[o + 5] << 8));
                                        sum += centre + (3 * (left + right)) / 10;
                                        count++;
                                    }
                                }
                                else
                                for (int f = a; f < b; f++)
                                    for (int c = 0; c < channels; c++)
                                    {
                                        if (outChannels > 1 && channels > 1 && c % 2 != oc) continue;
                                        sum += (short)(bytes[(f * channels + c) * 2] | (bytes[(f * channels + c) * 2 + 1] << 8));
                                        count++;
                                    }
                                int value = count == 0 ? 0 : (int)Math.Max(short.MinValue, Math.Min(short.MaxValue, sum / count));
                                int blockIndex = (int)(at / Block), offset = (int)(at % Block) * outChannels + oc;
                                while (mix.Count <= blockIndex) mix.Add(new short[Block * outChannels]);
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
                    int dataBytes = (int)Math.Min(int.MaxValue - 100, length * 2 * outChannels) & ~3;
                    output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + dataBytes); output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    output.Write(16); output.Write((short)1); output.Write((short)outChannels); output.Write(Rate); output.Write(Rate * 2 * outChannels); output.Write((short)(2 * outChannels)); output.Write((short)16);
                    output.Write(Encoding.ASCII.GetBytes("data")); output.Write(dataBytes);
                    var raw = new byte[Block * 2 * outChannels];
                    long left = dataBytes / 2;
                    foreach (var block in mix)
                    {
                        int n = (int)Math.Min(Block * outChannels, left);
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
        int captionEngine;               // 1.28.0: 0 Automatic, 1 Parakeet, 2 Whisper (captionEngine=)
        internal string lastCaptionResult; // tests
        volatile string captionSteps = ""; // what the last run used (tests)

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
            string stereo = wav.Replace(".wav", "_all.wav");
            bool isolate = Vocals.Offered && Vocals.Addon.Installed;
            RunJob("Making captions", delegate(Action<string, double> progress, CancelFlag cancel)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(wav));
                    progress("Making captions: listening", 0);
                    Action<Cue> onCue = delegate(Cue c)
                    {
                        // Each line shows as soon as it exists, while the rest is still being written.
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            live.Add(c);
                            if (shown == null || shown.File != video) return;
                            if (cues != live) { subsLookup++; if (!subsOn) { subsOn = true; SaveSettings(); } SetCues(video, live, "auto"); }
                            else { UpdateSubButton(); SubTick(); }
                        }));
                    };
                    double length = 0, done = 0.05;
                    captionSteps = Whisper.UsesLarge ? "large model" : "small model";
                    if (isolate)
                    {
                        // The voice without the music first (Vocals.cs). Whatever goes wrong there, the plain
                        // sound still gets its turn below.
                        try
                        {
                            length = VideoConverter.SpeechWav(video, stereo, seconds, p => progress("Making captions: listening", p * 0.05), cancel, Vocals.Rate, 2);
                            progress("Making captions: finding the voice", 0.05);
                            Vocals.Isolate(stereo, wav, p => progress("Making captions: finding the voice", 0.05 + 0.25 * p), cancel);
                            done = 0.3;
                            progress("Making captions: writing", done);
                            bool parakeetToo = ChooseEngine(wav, length, ref language, cancel, progress, done);
                            all = Whisper.Transcribe(wav, length, language, p => progress("Making captions: writing", done + (1 - done) * p), onCue, cancel, parakeetToo);
                            captionSteps += ", voice isolated";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { captionSteps += ", voice isolation failed (" + ex.Message + ")"; all = null; }
                        if (all != null && all.Count > 0) return;
                        captionSteps += ", plain sound after all";
                    }
                    length = VideoConverter.SpeechWav(video, wav, seconds, p => progress("Making captions: listening", done + p * 0.05), cancel);
                    done += 0.05;
                    progress("Making captions: writing", done);
                    bool parakeet = ChooseEngine(wav, length, ref language, cancel, progress, done);
                    all = Whisper.Transcribe(wav, length, language, p => progress("Making captions: writing", done + (1 - done) * p), onCue, cancel, parakeet);
                }
                catch (OperationCanceledException) { }
                finally { try { File.Delete(wav); } catch { } try { File.Delete(stereo); } catch { } }
            },
            delegate(string error, bool cancelled)
            {
                lastCaptionResult = cancelled ? "cancelled" : error ?? (all == null ? "nothing" : all.Count + " lines") + " (" + captionSteps + ")";
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

        // 1.28.0: Parakeet or Whisper (Parakeet.Choose), written into captionSteps for the tests.
        bool ChooseEngine(string wav, double length, ref string language, CancelFlag cancel, Action<string, double> progress, double done)
        {
            if (Parakeet.Addon.Installed && captionEngine == 0 && language == "auto") progress("Making captions: finding the language", done);
            string why;
            bool parakeet = Parakeet.Choose(captionEngine, wav, length, ref language, cancel, out why);
            captionSteps += ", " + why;
            return parakeet;
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
            if (Vocals.Offered) AddAddonCard(Vocals.Addon, "", "Captions on songs: voice isolation",
                "For videos with music under the voice (songs, TikTok edits). Before the captions are written, the voice is separated from the music, " +
                "so many more of the words are understood. An extra one-time download (the programs from github.com/k2-fsa/sherpa-onnx and a UVR voice model); " +
                "once it's there it is used for every video. It adds roughly 15 seconds for each minute of video and works on any PC: it uses the processor, not the graphics card.",
                () => !Environment.Is64BitOperatingSystem ? "This needs 64-bit Windows." : Whisper.Addon.Installed ? null : "Used together with Automatic captions above.", null);
            AddAddonCard(Whisper.Large, "", "Better quality captions",
                "A much larger Whisper model that gets more words right, most of all in songs, mumbled speech and languages other than English. " +
                "An extra one-time download; once it's there it is used instead of the small one (Remove goes back to the small one). " +
                "It is slower: a minute of video takes roughly a minute on an average PC. With Faster captions below, it is used for the languages Parakeet doesn't know.",
                () => !Environment.Is64BitOperatingSystem ? "This needs 64-bit Windows." : Whisper.Addon.Installed ? null : "Used together with Automatic captions above.", null);
            // 1.28.0: Parakeet (Parakeet.cs)
            AddAddonCard(Parakeet.Addon, "\uE945", "Faster captions (Parakeet)",
                "A newer speech model, Parakeet by NVIDIA, that writes captions about 6 times faster than the large Whisper model and " +
                "about as well, with better timing. For 25 European languages (English, Dutch, German, French, Spanish, Italian, Polish, " +
                "Portuguese, Russian, Ukrainian and more); videos in other languages still get Whisper. An extra one-time download " +
                "(the program from github.com/handy-computer/transcribe.cpp and the model from huggingface.co); once it's there it is " +
                "used by itself. It works on any PC: it uses the processor.",
                () => !Environment.Is64BitOperatingSystem ? "This needs 64-bit Windows." : Whisper.Addon.Installed ? null : "Used together with Automatic captions above.", null);
            AddCard("\uE945", "Caption engine",
                "Automatic: Parakeet for the languages it knows (once it's downloaded), Whisper for the others. With the language on " +
                "\"Find out by itself\", FastViewer first listens to a few seconds of the video to know which.",
                Dropdown(Parakeet.EngineNames, () => captionEngine, i => captionEngine = i));
            AddCard("", "Language spoken in your videos",
                "For automatic captions. Pick the language when it guesses wrong.",
                Dropdown(Whisper.LanguageNames, () => Math.Max(0, Array.IndexOf(Whisper.LanguageCodes, captionLanguage)), i => captionLanguage = Whisper.LanguageCodes[i]));
        }
    }
}
