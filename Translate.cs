// FastViewer 1.28.0: English translation of subtitles, for learning a language. Every line of the subtitles (automatic
// captions or a loaded .srt/.vtt) is translated on its own into English by a small translation model (Hy-MT2-1.8B by
// Tencent, Apache 2.0) run by llama.cpp's llama-server (MIT) on this PC: nothing is uploaded. Both share the same
// timing, so the original shows on top and the English under it, each can be switched off, and both go into burned-in
// captions and the subtitle editor. An optional one-time download (Addons.cs). With a graphics card that can take it the
// model runs there (a film in about a minute on an RTX 3060 Ti), otherwise on the processor (about 6 minutes on a
// Ryzen 5 5600X). It starts at the line being watched and each line shows as soon as it's done.
// Kept in %LOCALAPPDATA%\FastViewer\Captions\<hash>.en.srt (for a subtitle file: the hash of its path).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace FastViewer
{
    static class Translator
    {
        public static readonly Addon Addon = new Addon
        {
            Id = "Translate", Name = "English translation (Hy-MT2)",
            Needs = new[] { "llama-server.exe", "model.gguf" },
            Parts = new[]
            {
                new AddonPart
                {
                    // llama.cpp release b12418, Windows x64 with Vulkan (the processor code is in it too).
                    Url = "https://github.com/ggml-org/llama.cpp/releases/download/b12418/llama-b12418-bin-win-vulkan-x64.zip", Save = "llama.zip",
                    Unpack = new[] { "llama-server.exe", "llama-server-impl.dll", "llama.dll", "llama-common.dll", "mtmd.dll", "ggml*.dll", "libomp.dll", "LICENSE-LLVM-OpenMP" },
                    Sha256 = "528df34cddc38f257793b496fab0a19ad31ae8c1075cbfa0f1f99c51f36034a4", Size = 38214060
                },
                new AddonPart
                {
                    Url = "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf", Save = "model.gguf",
                    Sha256 = "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699", Size = 1133080448
                },
                new AddonPart
                {
                    Url = "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/LICENSE.txt", Save = "LICENSE.txt",
                    Sha256 = "a1d52d448f81c584a47c583e19dfab2d3851c7c84431b07baee093c1113ed114", Size = 11639
                },
            }
        };

        public static string Engine = "";   // what the last run used: "graphics card" / "processor" (tests, messages)
        public static string ForceEngine;   // tests: --translate-on gpu|cpu

        // ---------------------------------------------------------------- where the English is kept

        public static string FileFor(string video, string from)
        {
            try
            {
                if (from == "auto")
                {
                    string made = Whisper.CaptionsFile(video);
                    return made == null ? null : Path.ChangeExtension(made, ".en.srt");
                }
                if (string.IsNullOrEmpty(from) || from == "new") return null;
                using (var sha = SHA1.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes("subs|" + Path.GetFullPath(from).ToLowerInvariant()));
                    return Path.Combine(Whisper.CaptionsFolder, string.Concat(hash.Take(10).Select(b => b.ToString("x2"))) + ".en.srt");
                }
            }
            catch { return null; }
        }

        // The English kept before, put beside the lines with the same timing (lines changed since are left without).
        public static void Attach(List<Cue> list, string video, string from)
        {
            try
            {
                string file = FileFor(video, from);
                if (file == null || !File.Exists(file)) return;
                var en = SubtitleFile.Load(file);
                int j = 0;
                foreach (var c in list)
                {
                    while (j < en.Count && en[j].From < c.From - 0.05) j++;
                    if (j < en.Count && Math.Abs(en[j].From - c.From) <= 0.05 && Math.Abs(en[j].To - c.To) <= 0.05) c.En = en[j].Text;
                }
            }
            catch { }
        }

        // Writes the lines that have English (also half-done: the rest is translated next time).
        public static void Keep(string video, string from, List<Cue> list)
        {
            try
            {
                string file = FileFor(video, from);
                if (file == null) return;
                var done = list.Where(c => !string.IsNullOrEmpty(c.En)).Select(c => new Cue { From = c.From, To = c.To, Text = c.En }).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (done.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                File.WriteAllText(file, SubtitleFile.ToSrt(done), new UTF8Encoding(true));
            }
            catch { }
        }

        // ---------------------------------------------------------------- the translation server

        // llama-server, hidden, only reachable from this PC (127.0.0.1 on a free port). It belongs to a job that Windows
        // closes together with FastViewer, so it can never keep running after FastViewer is gone.
        class Server : IDisposable
        {
            public Process Process;
            public int Port, Slots;
            public bool Gpu;
            public readonly List<string> Said = new List<string>();
            public void Dispose()
            {
                try { if (Process != null && !Process.HasExited) Process.Kill(); } catch { }
                try { if (Process != null) Process.Dispose(); } catch { }
            }
        }

        static Server Start(bool gpu, CancelFlag cancel)
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)); // as for captions: the rest stays for the video and you
            int slots = gpu ? 8 : 4; // lines worked on at once (measured: more doesn't help on the processor)
            string args = "-m \"" + Addon.File("model.gguf") + "\" -c " + (1024 * slots) + " -np " + slots + " -t " + threads +
                " --host 127.0.0.1 --port " + port + " --no-webui --offline" + (gpu ? " -ngl 99" : " -ngl 0 -dev none");
            var info = new ProcessStartInfo(Addon.File("llama-server.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Addon.Dir
            };
            var server = new Server { Port = port, Slots = slots, Gpu = gpu };
            server.Process = Process.Start(info);
            KeepWithUs(server.Process);
            if (!gpu) try { server.Process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            DataReceivedEventHandler said = (o, e) => { if (e.Data != null) lock (server.Said) { server.Said.Add(e.Data); if (server.Said.Count > 40) server.Said.RemoveAt(0); } };
            server.Process.OutputDataReceived += said;
            server.Process.ErrorDataReceived += said;
            server.Process.BeginOutputReadLine();
            server.Process.BeginErrorReadLine();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 120)
            {
                if (cancel.Cancelled) { server.Dispose(); throw new OperationCanceledException(); }
                if (server.Process.HasExited) break;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                    req.Timeout = 1000; req.Proxy = null;
                    using (var r = (HttpWebResponse)req.GetResponse()) if (r.StatusCode == HttpStatusCode.OK) return server;
                }
                catch { }
                Thread.Sleep(200);
            }
            string last;
            lock (server.Said) last = server.Said.LastOrDefault(l => l.Contains("error") || l.Contains("failed")) ?? server.Said.LastOrDefault() ?? "";
            server.Dispose();
            throw new Exception("the translation program didn't start" + (last.Length > 0 ? " (" + last.Trim() + ")" : ""));
        }

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1 << 24 };

        // One line into English (Hy-MT2's own prompt; greedy, so the same line always gets the same English).
        static string Ask(Server server, string text)
        {
            string prompt = "Translate the following text into English. Note that you should only output the translated result without any additional explanation:\n\n" + text;
            var body = Json.Serialize(new Dictionary<string, object>
            {
                { "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } } },
                { "temperature", 0 }, { "top_k", 1 }, { "max_tokens", 300 }, { "repeat_penalty", 1.05 }, { "cache_prompt", true }
            });
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + server.Port + "/v1/chat/completions");
            req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 120000; req.ReadWriteTimeout = 120000; req.Proxy = null;
            var bytes = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            string answer;
            using (var r = req.GetResponse())
            using (var rd = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) answer = rd.ReadToEnd();
            var reply = Json.Deserialize<Dictionary<string, object>>(answer);
            var choice = (Dictionary<string, object>)((System.Collections.ArrayList)reply["choices"])[0];
            string en = (string)((Dictionary<string, object>)choice["message"])["content"] ?? "";
            return Clean(text, en);
        }

        // Only the translation: as many lines as the original, no quotes it didn't have, no runaway repeats.
        public static string Clean(string original, string en)
        {
            en = Regex.Replace(en ?? "", @"(?s)<think>.*?</think>", "").Replace("\r\n", "\n").Trim();
            if (en.Length > 1 && "\"“«'".IndexOf(en[0]) >= 0 && "\"”»'".IndexOf(en[en.Length - 1]) >= 0 && !Regex.IsMatch(original.Trim(), "^[\"“«']"))
                en = en.Substring(1, en.Length - 2).Trim();
            if (!original.Contains("\n")) en = Regex.Replace(en, @"\s*\n+\s*", " ");
            if (en.Length > 3 * original.Length + 40) en = en.Substring(0, 3 * original.Length + 40).TrimEnd() + "...";
            return en;
        }

        // Translates the lines in `order` (indexes into texts); done(i, english) for each, from several threads, as soon as
        // it's ready. The graphics card is used when it can take it; if that fails, the processor carries on.
        public static void Run(IList<string> texts, IList<int> order, Action<int, string> done, Action<int> progress, CancelFlag cancel)
        {
            var left = new Queue<int>(order);
            bool gpu = ForceEngine != null ? ForceEngine == "gpu" : GpuVerdict.Get().Level >= 2;
            int finished = 0;
            for (int attempt = 0; attempt < 2 && left.Count > 0; attempt++)
            {
                if (cancel.Cancelled) throw new OperationCanceledException();
                Server server;
                try { server = Start(gpu, cancel); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { if (!gpu) throw; gpu = false; attempt--; continue; } // no luck on the graphics card: the processor
                Engine = gpu ? "graphics card" : "processor";
                Exception failed = null;
                using (server)
                {
                    var workers = new List<Thread>();
                    for (int k = 0; k < server.Slots; k++)
                    {
                        var t = new Thread(delegate()
                        {
                            while (!cancel.Cancelled && failed == null)
                            {
                                int i;
                                lock (left) { if (left.Count == 0) return; i = left.Dequeue(); }
                                try
                                {
                                    string en = string.IsNullOrWhiteSpace(texts[i]) ? "" : Ask(server, texts[i]);
                                    done(i, en);
                                    progress(Interlocked.Increment(ref finished));
                                }
                                catch (Exception ex) { lock (left) { var rest = left.ToList(); left.Clear(); left.Enqueue(i); foreach (var x in rest) left.Enqueue(x); } failed = ex; }
                            }
                        });
                        t.IsBackground = true;
                        t.Start();
                        workers.Add(t);
                    }
                    foreach (var t in workers) t.Join();
                }
                if (cancel.Cancelled) throw new OperationCanceledException();
                if (failed == null) return;
                if (!gpu) throw new Exception("the translation stopped (" + failed.Message + ")");
                gpu = false; // the graphics card gave up halfway: the processor does the rest
            }
        }

        // ---------------------------------------------------------------- closing together with FastViewer

        static IntPtr job;

        static void KeepWithUs(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr mem = Marshal.AllocHGlobal(size);
                    try { Marshal.StructureToPtr(limits, mem, false); SetInformationJobObject(job, 9, mem, (uint)size); }
                    finally { Marshal.FreeHGlobal(mem); }
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }

    partial class ViewerWindow
    {
        bool subsOriginal = true, subsEnglish = true; // settings lines subsOrig= / subsEn=
        internal string lastTranslateResult;          // tests
        bool translating;

        bool HasEnglish { get { return HasSubs && cues.Any(c => !string.IsNullOrEmpty(c.En)); } }

        // Video "..." > Subtitles > Translate to English: from the line being watched onwards, then the start.
        void TranslateSubs()
        {
            var s = shown;
            if (s == null || !HasSubs) return;
            if (!Environment.Is64BitOperatingSystem) { ShowToast("The English translation needs 64-bit Windows.", null, null, 6, "error"); return; }
            if (converting) { ShowToast(translating ? "Already translating." : "Busy with something else, one moment...", null, null, 3, null); return; }
            if (!Translator.Addon.Installed)
            {
                GetAddon(Translator.Addon, "The English translation needs", "After that it works without internet.", delegate { if (shown == s) TranslateSubs(); });
                return;
            }
            var list = cues;
            string video = cuesFor, from = cuesFrom;
            double at = VideoAt(s).TotalSeconds;
            int here = list.FindIndex(c => c.To > at);
            if (here < 0) here = 0;
            var order = Enumerable.Range(here, list.Count - here).Concat(Enumerable.Range(0, here)).Where(i => string.IsNullOrEmpty(list[i].En)).ToList();
            if (order.Count == 0) { ShowToast("These subtitles are already in English too.", null, null, 3, "done"); return; }
            var texts = list.Select(c => c.Text).ToList();
            if (!subsEnglish) { subsEnglish = true; SaveSettings(); }
            if (!subsOn) { subsOn = true; SaveSettings(); UpdateSubButton(); }
            int total = order.Count;
            var clock = Stopwatch.StartNew();
            translating = true;
            RunJob("Translating to English", delegate(Action<string, double> progress, CancelFlag cancel)
            {
                Translator.Run(texts, order, delegate(int i, string en)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        list[i].En = en;
                        if (snapDir != null) translateLog.Add(i);
                        if (cues == list && cueShown == i) { cueShown = -2; SubTick(); } // the line on screen gets its English now
                    }));
                }, n => progress("Translating to English" + (Translator.Engine.Length > 0 ? " (" + Translator.Engine + ")" : ""), n / (double)total), cancel);
            },
            delegate(string error, bool cancelled)
            {
                translating = false;
                Translator.Keep(video, from, list); // (what's done stays, also after Cancel: next time only the rest)
                int done = list.Count(c => !string.IsNullOrEmpty(c.En));
                lastTranslateResult = (cancelled ? "cancelled" : error ?? "done") + ", " + done + " of " + list.Count + " lines in English, " +
                    (int)clock.Elapsed.TotalSeconds + " s on the " + Translator.Engine;
                if (snapDir != null) Log("translate: " + lastTranslateResult);
                if (cancelled) { ShowToast("Stopped. The " + done + " lines done so far are kept.", null, null, 4, null); return; }
                if (error != null) { ShowToast("Couldn't translate: " + error + ".", null, null, 10, "error"); return; }
                ShowToast("English ready (" + done + " lines). It'll be there next time too.", null, null, 6, "done");
            });
        }

        void ToggleOriginal()
        {
            subsOriginal = !subsOriginal;
            if (!subsOriginal && !subsEnglish) subsEnglish = true; // one of them always shows while subtitles are on
            SaveSettings(); cueShown = -2; SubTick();
            ShowFeedback(subsOriginal ? "Original subtitles on" : "Original subtitles off");
        }

        void ToggleEnglish()
        {
            subsEnglish = !subsEnglish;
            if (!subsOriginal && !subsEnglish) subsOriginal = true;
            SaveSettings(); cueShown = -2; SubTick();
            ShowFeedback(subsEnglish ? "English subtitles on" : "English subtitles off");
        }

        void SaveEnglishAs()
        {
            if (!HasEnglish) return;
            var list = cues.Where(c => !string.IsNullOrEmpty(c.En)).Select(c => new Cue { From = c.From, To = c.To, Text = c.En }).ToList();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the English subtitles",
                FileName = Path.GetFileNameWithoutExtension(cuesFor) + ".en.srt",
                InitialDirectory = Path.GetDirectoryName(cuesFor),
                DefaultExt = ".srt",
                Filter = "Subtitles (SubRip)|*.srt"
            };
            if (dlg.ShowDialog(this) != true) return;
            try { File.WriteAllText(dlg.FileName, SubtitleFile.ToSrt(list), new UTF8Encoding(true)); ShowToast("Saved " + Path.GetFileName(dlg.FileName), null, null, 5, "done"); }
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, null, null, 6, "error"); }
        }

        // The menu rows under Subtitles (after "Show subtitles").
        IEnumerable<Control> EnglishMenuItems()
        {
            var list = new List<Control>();
            if (!HasSubs) return list;
            bool any = HasEnglish, all = any && cues.All(c => !string.IsNullOrEmpty(c.En) || string.IsNullOrWhiteSpace(c.Text));
            if (any)
            {
                list.Add(Item("Show original", subsOriginal ? "\uE73E" : null, ToggleOriginal));
                list.Add(Item("Show English", subsEnglish ? "\uE73E" : null, ToggleEnglish));
            }
            if (!all) list.Add(Item(translating ? "Translating to English..." : any ? "Finish the English translation" : "Translate to English", "", TranslateSubs));
            if (any) list.Add(Item("Save English as a file...", "\uE74E", SaveEnglishAs));
            return list;
        }

        void AddTranslateSettings()
        {
            AddAddonCard(Translator.Addon, "", "English translation",
                "Translates subtitles and automatic captions into English, line by line, so the original and the English share the same " +
                "timing: handy for learning a language. Video \"...\" > Subtitles > Translate to English; it starts at the line you're " +
                "watching. This needs a one-time download: the translation model Hy-MT2 (by Tencent, from huggingface.co) and llama.cpp " +
                "(github.com/ggml-org/llama.cpp) to run it. Afterwards it works without internet. With a good graphics card a film takes " +
                "about a minute, on the processor alone about 5 to 10 minutes.",
                () => Environment.Is64BitOperatingSystem ? null : "This needs 64-bit Windows.", null);
            AddCard("\uE7F0", "Subtitles in two languages",
                "When subtitles have an English translation, the original shows on top and the English under it. Also in video \"...\" > Subtitles.",
                Dropdown(new[] { "Original and English", "Only the original", "Only the English" }, () => subsOriginal && subsEnglish ? 0 : subsOriginal ? 1 : 2,
                    i => { subsOriginal = i != 2; subsEnglish = i != 1; cueShown = -2; SubTick(); }));
        }
    }
}
