// FastViewer 1.28.0: Parakeet captions. NVIDIA's Parakeet TDT 0.6B v3 speech model (CC BY 4.0; the GGUF Q8_0 conversion by
// handy-computer) run by transcribe.cpp (MIT, github.com/handy-computer/transcribe.cpp) on the processor. Measured against
// Whisper (Ryzen 5 5600X, 6 threads): about 6 seconds per minute of sound against 37 for the large Whisper model and 15
// for the small one; on nine songs with known lyrics 27.5% of the words wrong against 24.0% (large) and 43.7% (small);
// on the film "Духless" as many spoken lines caught as the large model, with better timing. It knows 25 European
// languages; for the others Whisper is used. An optional download (Addons.cs; Settings > Videos), used next to Whisper.
// It runs in a second, hidden FastViewer ("--parakeet") that prints its lines the way whisper-cli does, so a crash there
// never touches the window and the captions code reads both the same way.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FastViewer
{
    static class Parakeet
    {
        public static readonly Addon Addon = new Addon
        {
            Id = "Parakeet", Name = "Faster captions (Parakeet)",
            Needs = new[] { "transcribe.dll", "model.gguf" },
            Parts = new[]
            {
                new AddonPart
                {
                    // transcribe.cpp v0.4.2, Windows x64 (only the processor parts are taken out).
                    Url = "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.4.2/transcribe-native-0.4.2-windows-x86_64-cpu-vulkan.tar.gz",
                    Save = "transcribe.tar.gz", Unpack = new[] { "transcribe.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu-*.dll" },
                    Sha256 = "6094102cf57b404b050707ceb258b12ce647b92e801b5d46077214e5b030c8aa", Size = 18052817
                },
                new AddonPart
                {
                    Url = "https://huggingface.co/handy-computer/parakeet-tdt-0.6b-v3-gguf/resolve/d8d8c32cb2017c57e5307767343e97870f8a7f53/parakeet-tdt-0.6b-v3-Q8_0.gguf",
                    Save = "model.gguf",
                    Sha256 = "5859f77944efcd8eafa23a6350731960b2b55b2203df51f319665c807d802cc7", Size = 739508576
                },
            }
        };

        // The languages it was made for (the model card): Bulgarian ... Ukrainian.
        public static readonly string[] Languages = { "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt", "mt", "pl", "pt", "ro", "ru", "sk", "sl", "es", "sv", "uk" };
        public static bool Knows(string language) { return Languages.Contains(language); }

        public static readonly string[] EngineNames = { "Automatic", "Parakeet", "Whisper" };

        // Which engine writes the captions. engine: 0 automatic, 1 Parakeet, 2 Whisper (setting captionEngine=). With the
        // language on "auto", Automatic first listens to a sample of the video (Whisper, a few seconds) to find it out.
        // Returns true for Parakeet; `language` becomes the one found (Whisper then needn't guess again).
        public static bool Choose(int engine, string wav, double seconds, ref string language, CancelFlag cancel, out string why)
        {
            why = "";
            if (engine == 2 || !Addon.Installed) { why = engine == 2 ? "Whisper chosen" : "Whisper"; return false; }
            if (engine == 1) { why = "Parakeet chosen"; return true; }
            if (language == "auto")
            {
                var clock = Stopwatch.StartNew();
                double p;
                string found = DetectLanguage(wav, seconds, cancel, out p);
                why = "language " + (found ?? "not found") + (found != null ? " (" + p.ToString("0.00", CultureInfo.InvariantCulture) + ")" : "") + " in " + clock.ElapsedMilliseconds + " ms";
                if (found == null) return false; // (Whisper finds it out itself as before)
                language = found;
            }
            bool knows = Knows(language);
            why = (why.Length > 0 ? why + ", " : "") + (knows ? "Parakeet" : "Whisper (Parakeet doesn't know " + language + ")");
            return knows;
        }

        // The language spoken: six 5-second bits from across the video (15%...90%; for a short one the start) put
        // together into one 30-second sample, which Whisper's small model names in about 3 seconds. (whisper-cli's own
        // check always listens to the first 30 seconds, whatever the offset: a film's opening titles said "English".)
        public static string DetectLanguage(string wav, double seconds, CancelFlag cancel, out double probability)
        {
            probability = 0;
            string sample = Path.Combine(Path.GetTempPath(), "FastViewer", "lang_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
            try
            {
                using (var f = new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long data = DataStart(f), bytes = f.Length - data;
                    var pieces = new List<byte[]>();
                    if (seconds < 60) pieces.Add(Read(f, data, 0, Math.Min(bytes, 30 * 32000)));
                    else foreach (double at in new[] { 0.15, 0.3, 0.45, 0.6, 0.75, 0.9 })
                        pieces.Add(Read(f, data, (long)(bytes * at) & ~1L, 5 * 32000));
                    int total = pieces.Sum(p => p.Length);
                    Directory.CreateDirectory(Path.GetDirectoryName(sample));
                    using (var o = new BinaryWriter(File.Create(sample)))
                    {
                        o.Write(Encoding.ASCII.GetBytes("RIFF")); o.Write(36 + total); o.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                        o.Write(16); o.Write((short)1); o.Write((short)1); o.Write(16000); o.Write(32000); o.Write((short)2); o.Write((short)16);
                        o.Write(Encoding.ASCII.GetBytes("data")); o.Write(total);
                        foreach (var p in pieces) o.Write(p);
                    }
                }
                string found = null; double prob = 0;
                int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
                Helper.Run(Whisper.Addon.File("whisper-cli.exe"), "-m \"" + Whisper.Addon.File("model.bin") + "\" -f \"" + sample + "\" -l auto -dl -t " + threads,
                    delegate(string text)
                    {
                        var m = Regex.Match(text, @"auto-detected language: (\w+) \(p = ([\d.]+)\)");
                        if (m.Success) { found = m.Groups[1].Value; double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out prob); }
                    }, cancel, true);
                probability = prob;
                return prob >= 0.3 ? found : null; // (a weak guess: Whisper finds it out itself)
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            finally { try { File.Delete(sample); } catch { } }
        }

        static byte[] Read(FileStream f, long data, long at, long count)
        {
            count = Math.Max(0, Math.Min(count, f.Length - data - at));
            var b = new byte[count];
            f.Position = data + at;
            int got = 0;
            while (got < b.Length) { int n = f.Read(b, got, b.Length - got); if (n <= 0) break; got += n; }
            return b;
        }

        static long DataStart(FileStream f)
        {
            var r = new BinaryReader(f);
            f.Position = 12;
            while (f.Position + 8 <= f.Length)
            {
                string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                uint len = r.ReadUInt32();
                if (id == "data") return f.Position;
                f.Position += len + (len & 1);
            }
            throw new InvalidDataException("no sound in the .wav");
        }

        // How the hidden FastViewer is started (Whisper.Transcribe runs it like whisper-cli).
        public static string Arguments(string wav, int threads) { return "--parakeet \"" + wav + "\" \"" + Addon.Dir + "\" " + threads; }

        // ---------------------------------------------------------------- the hidden FastViewer

        // FastViewer.exe --parakeet <16 kHz mono .wav> <the add-on's folder> <threads>: prints "[hh:mm:ss.mmm --> hh:mm:ss.mmm]  text"
        // lines as each piece is done (as whisper-cli does), "E <reason>" when it can't.
        public static int Child(string[] args)
        {
            // UTF-8 out, as whisper-cli writes (the console's own code page turns Cyrillic and ä/ñ/é into "?").
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            try
            {
                string dir = args[2];
                int threads = int.Parse(args[3], CultureInfo.InvariantCulture);
                SetDllDirectory(dir);
                if (LoadLibrary(Path.Combine(dir, "transcribe.dll")) == IntPtr.Zero) throw new DllNotFoundException();
                Check(transcribe_init_backends(Utf8(dir)), "starting");
                IntPtr load = Struct(64, transcribe_model_load_params_init), sess = Struct(64, transcribe_session_params_init);
                Marshal.WriteInt32(load, 8, 1); // backend: the processor (TRANSCRIBE_BACKEND_CPU)
                Marshal.WriteInt32(sess, 8, threads);
                IntPtr session;
                Check(transcribe_open(Utf8(Path.Combine(dir, "model.gguf")), load, sess, out session), "loading the model");
                short[] pcm;
                using (var f = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long data = DataStart(f);
                    var bytes = Read(f, data, 0, f.Length - data);
                    pcm = new short[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
                }
                var cuts = Cuts(pcm);
                IntPtr word = Marshal.AllocHGlobal(128);
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    int from = cuts[k], n = cuts[k + 1] - cuts[k];
                    var piece = new float[n];
                    for (int i = 0; i < n; i++) piece[i] = pcm[from + i] / 32768f;
                    IntPtr run = Struct(256, transcribe_run_params_init);
                    Marshal.WriteInt32(run, 12, 3); // timestamps: per word
                    var pin = GCHandle.Alloc(piece, GCHandleType.Pinned);
                    try { Check(transcribe_run(session, pin.AddrOfPinnedObject(), n, run), "writing"); }
                    finally { pin.Free(); Marshal.FreeHGlobal(run); }
                    var words = new List<Tuple<double, double, string>>();
                    int count = transcribe_n_words(session);
                    for (int i = 0; i < count; i++)
                    {
                        for (int b = 0; b < 128; b += 8) Marshal.WriteInt64(word, b, 0);
                        transcribe_word_init(word);
                        if (transcribe_get_word(session, i, word) != 0) continue;
                        string text = FromUtf8(Marshal.ReadIntPtr(word, 40)).Trim();
                        if (text.Length > 0) words.Add(Tuple.Create(Marshal.ReadInt64(word, 8) / 1000.0, Marshal.ReadInt64(word, 16) / 1000.0, text));
                    }
                    PrintLines(words, from / 16000.0);
                }
                Marshal.FreeHGlobal(word);
                transcribe_close(session);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("E " + (ex is DllNotFoundException || ex is EntryPointNotFoundException ? "its program files couldn't be loaded" : ex.Message.Replace('\r', ' ').Replace('\n', ' ')));
                return 1;
            }
        }

        // Words into caption lines, as Whisper's "-ml 84 -sow" makes them: a new line after a pause over 0.8 s, before
        // 84 characters, or after a sentence's end once a line has 20.
        static void PrintLines(List<Tuple<double, double, string>> words, double offset)
        {
            var line = new List<Tuple<double, double, string>>();
            Action flush = delegate
            {
                if (line.Count == 0) return;
                Console.WriteLine("[" + Clock(line[0].Item1 + offset) + " --> " + Clock(line[line.Count - 1].Item2 + offset) + "]  " + string.Join(" ", line.Select(w => w.Item3)));
                line.Clear();
            };
            foreach (var w in words)
            {
                if (line.Count > 0)
                {
                    int length = line.Sum(x => x.Item3.Length + 1) - 1;
                    var last = line[line.Count - 1].Item3;
                    if (w.Item1 - line[line.Count - 1].Item2 > 0.8 || length + 1 + w.Item3.Length > 84 || (".?!".IndexOf(last[last.Length - 1]) >= 0 && length >= 20)) flush();
                }
                line.Add(w);
            }
            flush();
            Console.Out.Flush();
        }

        static string Clock(double t)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, t));
            return ((int)ts.TotalHours).ToString("00") + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00") + "." + ts.Milliseconds.ToString("000");
        }

        // Pieces of about 20 seconds, each cut at the quietest half second between 15 and 25 s (no word is cut in half).
        // Measured on the whole film: 2-minute pieces left 10.1% of the spoken lines without text (on long sound the model
        // switches to "local attention"), 30 s 5.9%, 20 s 5.5% (the large Whisper model 4.7%), in the same 9.5 minutes.
        static List<int> Cuts(short[] pcm)
        {
            const int Frame = 1600; // 100 ms
            int frames = pcm.Length / Frame;
            var loud = new double[frames];
            for (int i = 0; i < frames; i++) { double s = 0; for (int k = i * Frame; k < (i + 1) * Frame; k++) s += (double)pcm[k] * pcm[k]; loud[i] = s; }
            var cuts = new List<int> { 0 };
            int last = 0;
            // Up to 5 minutes (a song, a clip) in one go: on nine songs that got 27.5% of the words wrong, 20 s pieces 33.6%.
            while (frames > 3000 && frames - last > 350) // more than 35 seconds left
            {
                int lo = last + 150, hi = Math.Min(frames - 5, last + 250), best = lo;
                double quiet = double.MaxValue;
                for (int i = lo; i <= hi; i++) { double e = loud[i] + loud[i + 1] + loud[i + 2] + loud[i + 3] + loud[i + 4]; if (e < quiet) { quiet = e; best = i; } }
                last = best + 2;
                cuts.Add(last * Frame);
            }
            cuts.Add(pcm.Length);
            return cuts;
        }

        // ---------------------------------------------------------------- transcribe.dll

        delegate void Init(IntPtr p);
        static IntPtr Struct(int size, Init init)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0);
            init(p); // (writes its own size and the defaults)
            return p;
        }

        static void Check(int status, string what)
        {
            if (status == 0) return;
            string text = "";
            try { text = FromUtf8(transcribe_status_string(status)); } catch { }
            throw new Exception(what + " failed: " + (text.Length > 0 ? text : "error " + status));
        }

        static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        static string FromUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        const CallingConvention C = CallingConvention.Cdecl;
        [DllImport("transcribe", CallingConvention = C)] static extern int transcribe_init_backends(byte[] dir);
        [DllImport("transcribe", CallingConvention = C)] static extern void transcribe_model_load_params_init(IntPtr p);
        [DllImport("transcribe", CallingConvention = C)] static extern void transcribe_session_params_init(IntPtr p);
        [DllImport("transcribe", CallingConvention = C)] static extern void transcribe_run_params_init(IntPtr p);
        [DllImport("transcribe", CallingConvention = C)] static extern int transcribe_open(byte[] path, IntPtr load, IntPtr session, out IntPtr outSession);
        [DllImport("transcribe", CallingConvention = C)] static extern void transcribe_close(IntPtr session);
        [DllImport("transcribe", CallingConvention = C)] static extern int transcribe_run(IntPtr session, IntPtr pcm, int samples, IntPtr run);
        [DllImport("transcribe", CallingConvention = C)] static extern int transcribe_n_words(IntPtr session);
        [DllImport("transcribe", CallingConvention = C)] static extern void transcribe_word_init(IntPtr word);
        [DllImport("transcribe", CallingConvention = C)] static extern int transcribe_get_word(IntPtr session, int i, IntPtr word);
        [DllImport("transcribe", CallingConvention = C)] static extern IntPtr transcribe_status_string(int status);
    }
}
