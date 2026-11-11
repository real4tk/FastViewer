// FastViewer 1.24.0: Convert to JPG / PNG (built in, for any photo Windows can open) and Smoother video
// (2x or 4x the frames, made on the graphics card by RIFE; an optional one-time download, see Addons.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FastViewer
{
    // What this PC's graphics card can be expected to do with RIFE.
    class GpuVerdict
    {
        public string Name = "";
        public long Megabytes;
        public int Level;      // 0: too weak or none, 1: works but slowly, 2: fine
        public string Text;
        public string Vendor { get { string n = Name.ToLowerInvariant(); return n.Contains("nvidia") || n.Contains("geforce") ? "nvidia" : n.Contains("amd") || n.Contains("radeon") ? "amd" : n.Contains("intel") ? "intel" : ""; } }

        public static string Override; // tests: --gpu "name|megabytes" (or "none", "novulkan")
        static GpuVerdict cached;
        public static void Reset() { cached = null; }

        public static GpuVerdict Get()
        {
            if (cached != null) return cached;
            var v = new GpuVerdict();
            bool vulkan = true;
            try
            {
                if (Override != null)
                {
                    var parts = Override.Split('|');
                    vulkan = parts[0] != "novulkan";
                    if (parts.Length == 2) { v.Name = parts[0]; v.Megabytes = long.Parse(parts[1]); }
                }
                else
                {
                    vulkan = File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));
                    // Every display adapter Windows knows, with the memory its driver reports; the strongest one counts.
                    using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                        foreach (var sub in cls == null ? new string[0] : cls.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue;
                            try
                            {
                                using (var k = cls.OpenSubKey(sub))
                                {
                                    string name = k.GetValue("DriverDesc") as string;
                                    if (string.IsNullOrEmpty(name) || Regex.IsMatch(name, "Microsoft Basic|Remote|Virtual|Parsec|Mirror|IddCx|Meta |Citrix", RegexOptions.IgnoreCase)) continue;
                                    long bytes = 0;
                                    object q = k.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
                                    if (q is long) bytes = (long)q;
                                    else if (q is int) bytes = (uint)(int)q;
                                    else if (q is byte[] && ((byte[])q).Length >= 8) bytes = BitConverter.ToInt64((byte[])q, 0);
                                    else if (q is byte[] && ((byte[])q).Length >= 4) bytes = BitConverter.ToUInt32((byte[])q, 0);
                                    long mb = bytes >> 20;
                                    if (v.Name.Length == 0 || Score(name, mb) > Score(v.Name, v.Megabytes)) { v.Name = name; v.Megabytes = mb; }
                                }
                            }
                            catch { }
                        }
                }
            }
            catch { }
            string size = v.Megabytes >= 1000 ? ", " + Math.Round(v.Megabytes / 1024.0) + " GB" : "";
            if (v.Name.Length == 0) { v.Level = 0; v.Text = "No graphics card that can do this was found on this PC."; }
            else if (!vulkan) { v.Level = 0; v.Text = "The graphics driver on this PC is too old for this (" + v.Name + "). Updating the driver may fix it."; }
            else if (BuiltIn(v.Name) || (v.Megabytes >= 1800 && v.Megabytes < 3600))
            {
                v.Level = 1;
                v.Text = "Your graphics card (" + v.Name + size + ") can do this, but slowly: expect minutes for a short clip, and 4x or videos above 1080p may not work.";
            }
            else if (v.Megabytes < 1800) { v.Level = 0; v.Text = "This PC's graphics card (" + v.Name + ") is too weak for this: it would fail or take hours."; }
            else { v.Level = 2; v.Text = "Your graphics card (" + v.Name + size + ") is fine for this."; }
            return cached = v;
        }

        // Chips that share the PC's memory (Intel HD/UHD/Iris, AMD "Radeon Graphics"/Vega): they work, slowly.
        static bool BuiltIn(string name)
        {
            return Regex.IsMatch(name, @"Intel.*(HD|UHD|Iris)|Radeon\(TM\) Graphics|Radeon Graphics|Vega \d", RegexOptions.IgnoreCase);
        }

        static long Score(string name, long mb) { return BuiltIn(name) ? mb / 16 : mb + 1; }
    }

    static class Smooth
    {
        public static readonly Addon Addon = new Addon
        {
            Id = "Smooth", Name = "Smoother video (RIFE and FFmpeg)",
            Needs = new[] { "ffmpeg.exe", @"rife\rife-ncnn-vulkan.exe", @"rife\vcomp140.dll", @"rife\rife-v4.6\flownet.bin", @"rife\rife-v4.6\flownet.param" },
            Parts = new[]
            {
                // RIFE for Windows by nihui (the 431 MB release holds every model: only these four files are fetched)
                Rife("rife-ncnn-vulkan.exe", @"rife\rife-ncnn-vulkan.exe", "4b970319db2814c82b15fceed8193151560a676a9eb63f20d4877be77b98f44f", 2201831),
                Rife("vcomp140.dll", @"rife\vcomp140.dll", "54fe6b087528b33c2969143d811eb62f1bd49071d37de9db0745fc079764d698", 91040),
                Rife("rife-v4.6/flownet.bin", @"rife\rife-v4.6\flownet.bin", "f334ed2260149ce0188a6dcf049844e8b0cdd912e01cbcfb63553157d2508958", 9891608),
                Rife("rife-v4.6/flownet.param", @"rife\rife-v4.6\flownet.param", "28df14d57a225725ee5386f52eba422488450d37c9f40800ed4f62e8ba846692", 2461),
                // FFmpeg, the "essentials" build by Gyan (linked from ffmpeg.org): only ffmpeg.exe
                new AddonPart { Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip", Entry = "bin/ffmpeg.exe", Save = "ffmpeg.exe", Sha256 = "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec", Size = 37386196 },
            }
        };

        static AddonPart Rife(string entry, string save, string sha, long size)
        {
            return new AddonPart { Url = "https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip", Entry = entry, Save = save, Sha256 = sha, Size = size };
        }

        // The user's own FrameBoost tool, when it's on this PC: its programs are used and nothing is downloaded.
        public static string OwnOverride; // tests: --frameboost <its tools folder | none>
        public static bool TestRun;
        static string own;
        static DateTime lookedAt;

        public static string Own
        {
            get
            {
                if ((DateTime.UtcNow - lookedAt).TotalSeconds < 5) return own;
                lookedAt = DateTime.UtcNow;
                own = null;
                try
                {
                    string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    var places = OwnOverride != null ? new[] { OwnOverride } : TestRun ? new string[0] : new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Tools\FrameBoost\tools"),
                        Path.Combine(exeDir, @"FrameBoost\tools"),
                    };
                    own = places.FirstOrDefault(p => p != "none" && File.Exists(Path.Combine(p, @"ffmpeg\ffmpeg.exe")) &&
                        File.Exists(Path.Combine(p, @"rife\rife-ncnn-vulkan.exe")) && File.Exists(Path.Combine(p, @"rife\rife-v4.6\flownet.bin")));
                }
                catch { }
                return own;
            }
        }

        public static bool Ready { get { return Own != null || Addon.Installed; } }
        static string Ffmpeg { get { return Own != null ? Path.Combine(Own, @"ffmpeg\ffmpeg.exe") : Addon.File("ffmpeg.exe"); } }
        static string RifeDir { get { return Own != null ? Path.Combine(Own, "rife") : Addon.File("rife"); } }

        static readonly double[][] StandardRates =
        {
            new double[] { 24000, 1001 }, new double[] { 24, 1 }, new double[] { 25, 1 }, new double[] { 30000, 1001 }, new double[] { 30, 1 }, new double[] { 48, 1 }, new double[] { 50, 1 },
            new double[] { 60000, 1001 }, new double[] { 60, 1 }, new double[] { 90, 1 }, new double[] { 100, 1 }, new double[] { 120000, 1001 }, new double[] { 120, 1 }, new double[] { 240, 1 }
        };

        static double Number(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        static string Q(string path) { return "\"" + path + "\""; }
        public static readonly List<string> Log = new List<string>();
        static void Note(string s) { lock (Log) if (Log.Count < 600) Log.Add(s); }

        // Makes source `times` (2 or 4) as smooth; returns the new file. progress: label (null = keep), 0..1.
        public static string Make(string source, int times, Action<string, double> progress, CancelFlag cancel)
        {
            string ffmpeg = Ffmpeg, rife = Path.Combine(RifeDir, "rife-ncnn-vulkan.exe"), model = Path.Combine(RifeDir, "rife-v4.6");
            string tempRoot = Path.Combine(Path.GetTempPath(), "FastViewer");
            Directory.CreateDirectory(tempRoot);
            // Leftovers of a run that was cut off (PC switched off, FastViewer ended) go after half a day.
            try { foreach (var d in Directory.GetDirectories(tempRoot, "Smooth_*")) if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-12)) Directory.Delete(d, true); } catch { }

            // 1. What kind of video is it?
            var info = new List<string>();
            Helper.Run(ffmpeg, "-hide_banner -i " + Q(source), info.Add, cancel, true);
            string video = info.FirstOrDefault(l => l.Contains("Video:")), audio = info.FirstOrDefault(l => l.Contains("Audio:"));
            if (video == null) throw new Exception("there is no picture in this file that FFmpeg can read");
            var size = Regex.Match(video, @", (\d{2,5})x(\d{2,5})");
            var fpsM = Regex.Match(video, @"([\d.]+) fps");
            var tbrM = Regex.Match(video, @"([\d.]+) tbr");
            var durM = info.Select(l => Regex.Match(l, @"Duration: (\d+):(\d+):([\d.]+)")).FirstOrDefault(m => m.Success);
            if (!size.Success || (!fpsM.Success && !tbrM.Success)) throw new Exception("the video's size or frame rate couldn't be read");
            int w = int.Parse(size.Groups[1].Value), h = int.Parse(size.Groups[2].Value);
            double nominal = tbrM.Success ? Number(tbrM.Groups[1].Value) : 0, average = fpsM.Success ? Number(fpsM.Groups[1].Value) : 0;
            double fps = nominal > 0 && average > 0 && Math.Abs(nominal - average) / nominal < 0.02 ? nominal : average > 0 ? average : nominal;
            // A constant, clean frame rate (phones record a slightly uneven one): the nearest usual rate within 2%.
            double[] rate = StandardRates.OrderBy(r => Math.Abs(r[0] / r[1] - fps)).First();
            if (Math.Abs(rate[0] / rate[1] - fps) / fps > 0.02) rate = new[] { Math.Round(fps * 1000), 1000 };
            double seconds = durM == null ? 0 : int.Parse(durM.Groups[1].Value) * 3600 + int.Parse(durM.Groups[2].Value) * 60 + Number(durM.Groups[3].Value);
            bool hdr = video.Contains("arib-std-b67") || video.Contains("smpte2084");
            string audioCodec = audio == null ? null : Regex.Match(audio, @"Audio: (\w+)").Groups[1].Value;
            double outFps = rate[0] * times / rate[1];
            long expected = (long)Math.Max(2, seconds * rate[0] / rate[1]);
            Note("video " + w + "x" + h + ", " + fps + " fps -> " + rate[0] + "/" + rate[1] + " x" + times + ", " + seconds + " s, hdr " + hdr + ", sound " + (audioCodec ?? "none"));

            // Room for the frames while it works (each one is a picture file).
            long need = (long)(expected * (1 + times) * (w * (double)h * 0.3));
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(tempRoot));
                if (drive.AvailableFreeSpace < need * 1.15)
                    throw new Exception("it needs about " + GB(need * 1.15) + " of free space on drive " + drive.Name.TrimEnd('\\') + " while it works (there is " + GB(drive.AvailableFreeSpace) + "). Trim the video to a shorter part first");
            }
            catch (ArgumentException) { }

            string folder = Path.GetDirectoryName(source), stem = Path.GetFileNameWithoutExtension(source), fpsName = Math.Round(outFps).ToString(CultureInfo.InvariantCulture);
            string target = Path.Combine(folder, stem + "_" + fpsName + "fps.mp4");
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, stem + "_" + fpsName + "fps_" + n + ".mp4");
            string partFile = target + ".fvsmooth.tmp.mp4";

            string work = Path.Combine(tempRoot, "Smooth_" + Guid.NewGuid().ToString("N").Substring(0, 8)), inDir = Path.Combine(work, "in"), outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);
            try
            {
                // 2. Every frame as a picture, at the constant rate.
                progress("reading the frames", 0.01);
                var errors = new List<string>();
                // ffmpeg's "frame=  123 ..." lines -> progress
                Func<double, double, long, Action<string>> watch = (from, span, all) => text =>
                {
                    var m = Regex.Match(text, @"^frame=\s*(\d+)");
                    if (m.Success) progress(null, from + span * Math.Min(1, long.Parse(m.Groups[1].Value) / (double)Math.Max(1, all)));
                    else if (text.Trim().Length > 0) { lock (errors) errors.Add(text.Trim()); Note(text.Trim()); }
                };
                string filter = "fps=" + rate[0] + "/" + rate[1];
                string tone = ",zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
                Func<string, int> extract = f => Helper.Run(ffmpeg, "-hide_banner -loglevel error -stats -y -i " + Q(source) + " -map 0:v:0 -vf " + Q(f) +
                    " -fps_mode passthrough -q:v 2 " + Q(Path.Combine(inDir, "%08d.jpg")), watch(0.01, 0.11, expected), cancel, true);
                int code = extract(filter + (hdr ? tone : "")); // HDR (HLG / PQ) becomes normal colours
                if (code != 0 && hdr) { errors.Clear(); code = extract(filter); }
                int inCount = Directory.GetFiles(inDir, "*.jpg").Length;
                if (code != 0 || inCount < 2) throw new Exception("the frames couldn't be read" + Last(errors));
                long made = (long)inCount * times;

                // 3. The in-between frames, on the graphics card. Progress = pictures made so far.
                progress("making the new frames", 0.12);
                errors.Clear();
                var clock = Stopwatch.StartNew();
                code = Helper.Run(rife, "-i " + Q(inDir) + " -o " + Q(outDir) + " -m " + Q(model) + " -n " + made + " -f %08d.jpg -j 4:2:4",
                    text => { lock (errors) errors.Add(text.Trim()); Note(text.Trim()); }, cancel, true, delegate
                    {
                        int done = Directory.GetFiles(outDir, "*.jpg").Length;
                        string left = "";
                        if (done > 20) { double s = clock.Elapsed.TotalSeconds / done * (made - done); left = s < 1.5 ? ", almost done" : ", about " + (s >= 90 ? Math.Round(s / 60) + " min" : Math.Round(s) + " s") + " left"; }
                        progress("making the new frames" + left, 0.12 + 0.76 * Math.Min(1, done / (double)made));
                    });
                int outCount = Directory.GetFiles(outDir, "*.jpg").Length;
                if (code != 0 || outCount < made)
                {
                    string said = string.Join(" ", errors);
                    throw new Exception(said.Contains("vkAllocateMemory") || said.Contains("out of memory") || said.Contains("vkQueueSubmit") ? "the graphics card ran out of memory for a video this size"
                        : said.Contains("invalid gpu") || said.Contains("no vulkan") || said.Contains("vkCreateInstance") ? "this PC's graphics card or its driver can't do this (update the graphics driver)"
                        : "the graphics card stopped after " + outCount + " of " + made + " frames (update the graphics driver)");
                }

                // 4. Back into a video (the graphics card's own encoder first, then the processor), with the sound.
                progress("saving the video", 0.88);
                string sound = audioCodec == null ? "" : " -map 1:a:0 " + (audioCodec == "aac" ? "-c:a copy" : "-c:a aac -b:a 192k");
                string common = "-hide_banner -loglevel error -stats -y -framerate " + (rate[0] * times) + "/" + rate[1] + " -i " + Q(Path.Combine(outDir, "%08d.jpg")) + " -i " + Q(source) +
                    " -map 0:v:0" + sound + " -vf " + Q("scale=trunc(iw/2)*2:trunc(ih/2)*2:out_range=tv,format=yuv420p") +
                    " -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709";
                string tail = " -movflags +faststart " + Q(partFile);
                var encoders = new List<string>();
                string vendor = GpuVerdict.Get().Vendor;
                if (vendor == "nvidia") encoders.Add("-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 18 -b:v 0 -profile:v high");
                if (vendor == "amd") encoders.Add("-c:v h264_amf -quality quality -rc cqp -qp_i 18 -qp_p 20 -profile:v high");
                if (vendor == "intel") encoders.Add("-c:v h264_qsv -global_quality 19 -profile:v high");
                encoders.Add("-c:v libx264 -preset medium -crf 18 -profile:v high");
                code = 1;
                foreach (var enc in encoders)
                {
                    errors.Clear();
                    code = Helper.Run(ffmpeg, common + " " + enc + tail, watch(0.88, 0.12, made), cancel, true);
                    Note("encoder " + enc.Split(' ')[1] + ": exit " + code);
                    if (code == 0) break;
                }
                if (code != 0 || !File.Exists(partFile) || new FileInfo(partFile).Length < 1000) throw new Exception("the new video couldn't be saved" + Last(errors));
                File.Move(partFile, target);
                progress("saving the video", 1);
                return target;
            }
            finally
            {
                try { if (File.Exists(partFile)) File.Delete(partFile); } catch { }
                for (int attempt = 0; attempt < 5; attempt++) // (a program just stopped may hold a file for a moment)
                {
                    try { if (Directory.Exists(work)) Directory.Delete(work, true); break; }
                    catch { Thread.Sleep(300); }
                }
            }
        }

        static string Last(List<string> errors) { lock (errors) return errors.Count == 0 ? "" : " (" + errors[errors.Count - 1] + ")"; }
        static string GB(double bytes) { return (bytes / 1073741824.0).ToString(bytes < 10 * 1073741824.0 ? "0.0" : "0", CultureInfo.InvariantCulture) + " GB"; }
    }

    partial class ViewerWindow
    {
        static bool IsPhotoFile(string f) { return f != null && !IsVideoFile(f) && !IsAudioFile(f); }

        // ---------------------------------------------------------------- Smoother video

        void SmoothVideo(int times) { SmoothVideo(times, false); }

        void SmoothVideo(int times, bool anyway)
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed) return;
            if (converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            var gpu = GpuVerdict.Get();
            if (gpu.Level == 0 && !anyway)
            {
                ShowToast(gpu.Text, "Try anyway", delegate { HideToast(null); SmoothVideo(times, true); }, 10, "error");
                return;
            }
            if (!Smooth.Ready)
            {
                GetAddon(Smooth.Addon, "Smoother video needs", gpu.Level == 2 ? null : gpu.Text, delegate { if (shown == s) SmoothVideo(times, true); });
                return;
            }
            if (playing) TogglePlay();
            string source = s.File, saved = null;
            string what = "Making it " + times + "x smoother";
            RunJob(what, delegate(Action<string, double> progress, CancelFlag cancel)
            {
                try { saved = Smooth.Make(source, times, (label, p) => progress(label == null ? null : what + ": " + label, p), cancel); }
                catch (OperationCanceledException) { }
            },
            delegate(string error, bool cancelled)
            {
                if (snapDir != null) Log("smooth " + times + "x: " + (cancelled ? "cancelled" : error ?? "saved " + saved));
                if (cancelled || (error == null && saved == null)) { ShowToast("Cancelled.", null, null, 4, null); return; }
                if (error != null) { ShowToast("Couldn't make it smoother: " + error + ".", null, null, 10, "error"); return; }
                string made = saved;
                ShowToast("Saved " + Path.GetFileName(made), "Open it", delegate { HideToast(null); OpenPath(made); }, 10, "done");
            });
        }

        IEnumerable<Control> SmoothMenuItems()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed) return new Control[0];
            string dots = Smooth.Ready ? "" : "...";
            return new Control[]
            {
                Item("Make smoother (2x the frames)" + dots, "", delegate { SmoothVideo(2); }),
                Item("Make smoother (4x the frames)" + dots, "", delegate { SmoothVideo(4); }),
            };
        }

        void AddSmoothSettings()
        {
            AddSubheading("Smoother video");
            AddAddonCard(Smooth.Addon, "", "Make videos smoother",
                "Doubles or quadruples a video's frames (30 becomes 60 or 120 a second) by drawing the in-between frames on your graphics card, " +
                "and saves the result as a new video next to the original. Video \"...\" > Make smoother. This needs a one-time download: " +
                "RIFE (by nihui) and FFmpeg, from their own pages on GitHub. Afterwards it works without internet.",
                () => GpuVerdict.Get().Text,
                () => Smooth.Own != null ? "Using your FrameBoost" : null);
        }

        // ---------------------------------------------------------------- Convert to JPG / PNG

        // A copy of the photo on screen as a .jpg or .png next to it, upright as shown. The original is never
        // touched and no file is ever replaced: the copy is written under a temporary name, read back to check
        // it, and only then gets its real name (with a number added when that name is taken).
        void ConvertPhoto(string ext)
        {
            string source = CurrentFile;
            if (source == null || !IsPhotoFile(source) || shown == null || shown.Failed) return;
            int turn;
            rotations.TryGetValue(source, out turn);
            string kind = ext == ".png" ? "PNG" : "JPG";
            ShowToast("Converting to " + kind + "...", null, null, 0, "convert");
            F<System.Windows.FrameworkElement>("ToastProgress").Visibility = System.Windows.Visibility.Collapsed;
            var thread = new Thread(delegate()
            {
                string made = null, error = null;
                try { made = ConvertPhotoFile(source, ext, turn); }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (snapDir != null) Log("convert to " + kind + ": " + (error ?? "made " + made));
                    if (error != null) { ShowToast("Couldn't convert it: " + error, null, null, 8, "error"); return; }
                    ShowToast("Saved " + Path.GetFileName(made), "Open it", delegate { HideToast(null); OpenPath(made); }, 8, "done");
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        internal static string ConvertPhotoFile(string source, string ext, int turn)
        {
            string folder = Path.GetDirectoryName(source), stem = Path.GetFileNameWithoutExtension(source);
            string temp = Path.Combine(folder, stem + ".fvconvert.tmp");
            try
            {
                string from = Path.GetExtension(source).ToLowerInvariant();
                if (ext == ".jpg" && turn == 0 && (from == ".jfif" || from == ".jpe")) File.Copy(source, temp, true); // already a JPG under an odd name: nothing is lost
                else CropPhoto(source, temp, ext, new[] { 0.0, 0.0, 1.0, 1.0 }, turn, ext != ".png"); // a JPG has no see-through: those parts become white
                for (int n = 1; n < 1000; n++)
                {
                    string target = Path.Combine(folder, stem + (n == 1 ? "" : " (" + n + ")") + ext);
                    if (File.Exists(target)) continue;
                    try { File.Move(temp, target); } // (Move never replaces a file: one that appeared this instant makes it try the next name)
                    catch (IOException) { if (File.Exists(target)) continue; throw; }
                    try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); } catch { } // stays next to the original when sorted by date
                    return target;
                }
                throw new IOException("too many files with this name already");
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        IEnumerable<Control> ConvertMenuItems()
        {
            string file = CurrentFile;
            var list = new List<Control>();
            if (file == null || !IsPhotoFile(file) || shown == null || shown.Failed) return list;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            bool jpg = ext == ".jpg" || ext == ".jpeg";
            if (!jpg) list.Add(Item("Convert to JPG", "", delegate { ConvertPhoto(".jpg"); }, KeyOf("toJpg")));
            if (ext != ".png") list.Add(Item("Convert to PNG", "", delegate { ConvertPhoto(".png"); }, KeyOf("toPng")));
            return list;
        }
    }
}
