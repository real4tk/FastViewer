// FastViewer: voice isolation for automatic captions (1.25.0). Whisper is made for speech: with music under
// the voice (songs, TikTok edits) it loses most of the words. So before Whisper listens, the singing or
// talking voice is separated from the music by a small neural network (UVR MDX-Net, run by sherpa-onnx and
// ONNX Runtime), and Whisper gets the voice with only a little of the original sound left under it.
// An optional add-on (Addons.cs; Settings > Videos), used offline after its one-time download. It runs on the
// processor, so the graphics card doesn't matter, in a second, hidden FastViewer ("--isolate"): if anything
// in it goes wrong, the window you're using is not affected and the captions are made from the plain sound.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FastViewer
{
    static class Vocals
    {
        public static readonly Addon Addon = new Addon
        {
            Id = "Vocals", Name = "Voice isolation for captions",
            Needs = new[] { "sherpa-onnx-c-api.dll", "onnxruntime.dll", "model.onnx" },
            Parts = new[]
            {
                new AddonPart
                {
                    Url = "https://api.nuget.org/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.win-x64/2.0.1/org.k2fsa.sherpa.onnx.runtime.win-x64.2.0.1.nupkg",
                    Entry = "runtimes/win-x64/native/sherpa-onnx-c-api.dll", Save = "sherpa-onnx-c-api.dll",
                    Sha256 = "5dd36dbd3572846b72c4a40a4017c5a770160ee8e00da5053bebc8b2632501e8", Size = 1829625
                },
                new AddonPart
                {
                    Url = "https://api.nuget.org/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.win-x64/2.0.1/org.k2fsa.sherpa.onnx.runtime.win-x64.2.0.1.nupkg",
                    Entry = "runtimes/win-x64/native/onnxruntime.dll", Save = "onnxruntime.dll",
                    Sha256 = "7f66f939a881baf4f46a2216496798edf4a1429878b646d12674aa62f27d8a25", Size = 6567524
                },
                new AddonPart
                {
                    Url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/source-separation-models/UVR_MDXNET_9482.onnx", Save = "model.onnx",
                    Sha256 = "9d78f8566fa8198065214ab628be1de966a500c57786695aa4b13e2b27a7727d", Size = 29704738
                },
            }
        };

        // Measured before release (FEATURES.md, "Captions on songs"): with this small model the captions came out no
        // better on average (better on some songs, worse on others, and the large Whisper model lost its place more
        // often). So it isn't offered in Settings yet; everything is here for when a better voice model can be run.
        public static bool Offered = false; // tests: --vocals

        public const int Rate = 44100;      // what the network was trained on (stereo)
        const double Keep = 0.158;          // how much of the original stays under the voice (-16 dB): measured better than the voice alone

        // The voice out of a 44.1 kHz stereo .wav, as the 16 kHz mono .wav Whisper wants. Runs the hidden helper.
        public static void Isolate(string stereoWav, string speechWav, Action<double> progress, CancelFlag cancel)
        {
            string last = null;
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            int code = Helper.Run(self, "--isolate \"" + stereoWav + "\" \"" + speechWav + "\" \"" + Addon.Dir + "\"", delegate(string text)
            {
                double p;
                if (text.StartsWith("P ") && double.TryParse(text.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out p)) { if (progress != null) progress(p); }
                else if (text.StartsWith("E ")) last = text.Substring(2);
            }, cancel, true, null, true);
            if (code != 0 || !File.Exists(speechWav))
                throw new Exception(unchecked((uint)code) == 0xC0000135 ? "a part of Windows it needs is missing (install \"Microsoft Visual C++ Redistributable\" x64)"
                    : unchecked((uint)code) == 0xC000001D ? "this PC's processor is too old for it" : last ?? "it stopped with error " + code);
        }

        // ---------------------------------------------------------------- the hidden helper

        [StructLayout(LayoutKind.Sequential)]
        struct Config
        {
            public IntPtr SpleeterVocals, SpleeterAccompaniment, UvrModel;
            public int Threads, Debug;
            public IntPtr Provider;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Output { public IntPtr Stems; public int Count, SampleRate; }

        [StructLayout(LayoutKind.Sequential)]
        struct Stem { public IntPtr Samples; public int Channels, N; }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern bool SetDllDirectory(string path);
        [DllImport("sherpa-onnx-c-api", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr SherpaOnnxCreateOfflineSourceSeparation(ref Config config);
        [DllImport("sherpa-onnx-c-api", CallingConvention = CallingConvention.Cdecl)]
        static extern void SherpaOnnxDestroyOfflineSourceSeparation(IntPtr ss);
        [DllImport("sherpa-onnx-c-api", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr SherpaOnnxOfflineSourceSeparationProcess(IntPtr ss, IntPtr[] samples, int channels, int count, int rate);
        [DllImport("sherpa-onnx-c-api", CallingConvention = CallingConvention.Cdecl)]
        static extern void SherpaOnnxDestroySourceSeparationOutput(IntPtr output);

        static IntPtr Utf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        // FastViewer.exe --isolate <44.1 kHz stereo .wav> <target .wav> <the add-on's folder>
        // Prints "P 0.42" as it goes and "E why" when it can't; the exit code is 0 when the target was written.
        public static int Child(string[] args)
        {
            try
            {
                string dir = args[3];
                SetDllDirectory(dir);
                int rate, channels;
                short[] pcm = ReadWav(args[1], out rate, out channels);
                if (channels != 2 || rate != Rate) throw new Exception("unexpected sound format");
                int n = pcm.Length / 2;
                var left = new float[n];
                var right = new float[n];
                for (int i = 0; i < n; i++) { left[i] = pcm[2 * i] / 32768f; right[i] = pcm[2 * i + 1] / 32768f; }

                var config = new Config
                {
                    UvrModel = Utf8(Path.Combine(dir, "model.onnx")), SpleeterVocals = Utf8(""), SpleeterAccompaniment = Utf8(""), Provider = Utf8("cpu"),
                    Threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2))
                };
                IntPtr ss = SherpaOnnxCreateOfflineSourceSeparation(ref config);
                if (ss == IntPtr.Zero) throw new Exception("the voice model couldn't be loaded");

                // In pieces of 20 seconds that overlap by one (faded into each other): progress can be shown,
                // and a long video doesn't need all its memory at once.
                var voice = new float[n];
                int piece = 20 * Rate, overlap = Rate;
                for (int start = 0; start < n; )
                {
                    int length = Math.Min(piece, n - start);
                    if (n - (start + length) < 2 * Rate) length = n - start; // no crumb at the end
                    var l = new float[length];
                    var r = new float[length];
                    Array.Copy(left, start, l, 0, length);
                    Array.Copy(right, start, r, 0, length);
                    var hl = GCHandle.Alloc(l, GCHandleType.Pinned);
                    var hr = GCHandle.Alloc(r, GCHandleType.Pinned);
                    try
                    {
                        IntPtr result = SherpaOnnxOfflineSourceSeparationProcess(ss, new[] { hl.AddrOfPinnedObject(), hr.AddrOfPinnedObject() }, 2, length, Rate);
                        if (result == IntPtr.Zero) throw new Exception("the voice couldn't be separated");
                        var output = (Output)Marshal.PtrToStructure(result, typeof(Output));
                        if (output.Count < 1) throw new Exception("the voice model gave nothing back");
                        var stem = (Stem)Marshal.PtrToStructure(output.Stems, typeof(Stem)); // the first one is the voice
                        int got = Math.Min(length, stem.N);
                        var mono = new float[length];
                        var one = new float[got];
                        for (int c = 0; c < stem.Channels; c++)
                        {
                            Marshal.Copy(Marshal.ReadIntPtr(stem.Samples, c * IntPtr.Size), one, 0, got);
                            for (int i = 0; i < got; i++) mono[i] += one[i] / stem.Channels;
                        }
                        SherpaOnnxDestroySourceSeparationOutput(result);
                        for (int i = 0; i < length; i++)
                        {
                            double w = start > 0 && i < overlap ? (double)i / overlap : 1;
                            voice[start + i] = (float)(voice[start + i] * (1 - w) + mono[i] * w);
                        }
                    }
                    finally { hl.Free(); hr.Free(); }
                    if (start + length >= n) break;
                    start += length - overlap;
                    Console.WriteLine("P " + ((double)start / n).ToString("0.###", CultureInfo.InvariantCulture));
                }
                SherpaOnnxDestroyOfflineSourceSeparation(ss);

                // The voice as loud as it goes, a little of the original under it (Whisper then loses the thread
                // less often than with the bare voice), down to 16 kHz mono.
                float voicePeak = 1e-6f, mixPeak = 1e-6f;
                for (int i = 0; i < n; i++)
                {
                    left[i] = (left[i] + right[i]) / 2;
                    voicePeak = Math.Max(voicePeak, Math.Abs(voice[i]));
                    mixPeak = Math.Max(mixPeak, Math.Abs(left[i]));
                }
                float gv = voicePeak < 0.003f ? 0 : 0.89f / voicePeak, gm = (float)(Keep / mixPeak); // (no voice found at all: don't blow up the noise)
                if (gv == 0) gm = 0.89f / mixPeak;
                for (int i = 0; i < n; i++) voice[i] = voice[i] * gv + left[i] * gm;
                WriteWav(args[2], Resample(voice, Rate, 16000), 16000);
                Console.WriteLine("P 1");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("E " + (ex is DllNotFoundException ? "its program files couldn't be loaded" : ex.Message.Replace('\r', ' ').Replace('\n', ' ')));
                return 1;
            }
        }

        static short[] ReadWav(string file, out int rate, out int channels)
        {
            var b = File.ReadAllBytes(file);
            if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') throw new Exception("not a .wav");
            channels = BitConverter.ToInt16(b, 22);
            rate = BitConverter.ToInt32(b, 24);
            var pcm = new short[(b.Length - 44) / 2];
            Buffer.BlockCopy(b, 44, pcm, 0, pcm.Length * 2);
            return pcm;
        }

        static void WriteWav(string file, float[] samples, int rate)
        {
            using (var w = new BinaryWriter(File.Create(file)))
            {
                int bytes = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                var raw = new byte[bytes];
                for (int i = 0; i < samples.Length; i++)
                {
                    int v = (int)Math.Round(Math.Max(-1, Math.Min(1, samples[i])) * 32767);
                    raw[2 * i] = (byte)v;
                    raw[2 * i + 1] = (byte)(v >> 8);
                }
                w.Write(raw);
            }
        }

        // Fewer samples a second, properly: each new sample is a weighted average of the old ones around it
        // (a windowed sinc), so nothing above what the new rate can hold folds back as noise.
        static float[] Resample(float[] x, int from, int to)
        {
            double step = (double)from / to, cut = 0.92 / step; // of the old rate's limit
            const int Half = 24;
            var y = new float[(int)(x.Length / step)];
            System.Threading.Tasks.Parallel.For(0, y.Length, delegate(int i)
            {
                double centre = i * step, sum = 0, weights = 0;
                int first = (int)Math.Floor(centre) - Half + 1;
                for (int k = first; k < first + 2 * Half; k++)
                {
                    if (k < 0 || k >= x.Length) continue;
                    double d = k - centre, a = Math.PI * d * cut;
                    double w = (Math.Abs(a) < 1e-9 ? 1 : Math.Sin(a) / a) * (0.5 + 0.5 * Math.Cos(Math.PI * d / Half));
                    sum += x[k] * w;
                    weights += w;
                }
                y[i] = weights == 0 ? 0 : (float)(sum / weights);
            });
            return y;
        }
    }
}
