// FastViewer: sharing from the trim bar. "Make GIF" saves the part between the handles as an animated GIF.
// "Send to Discord" makes an .mp4 of that part that fits under a size limit (20 MB for Discord without Nitro,
// 50 or 500 MB with) and copies the file, ready to paste into a chat. Both use Windows' own video tools
// (Convert.cs) and FastViewer's own GIF writer (Gif.cs): nothing else has to be installed.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FastViewer
{
    // How a part of a video is made to fit under a size limit: how many bits a second, how big, how many frames.
    class SharePlan
    {
        public double VideoBitrate, Cap; // Cap: more than this adds nothing you'd see
        public int AudioBitrate, MaxShort; // AudioBitrate: for each sound track
        public int Sound;                  // all sound tracks together
        public double MaxFps;
        public bool Capped;              // already as good as it gets: no need to use the room that's left

        const double Enough = 0.05;      // bits per pixel per frame under which H.264 from Windows turns blocky

        public static SharePlan Make(long ceiling, double seconds, int w, int h, double fps, bool sound, double sourceBitrate, int soundTracks = 1)
        {
            if (seconds <= 0 || w <= 0 || h <= 0) return null;
            if (fps <= 0 || fps > 240) fps = 30;
            double total = ceiling * 8.0 / seconds * 0.97; // a little under: the encoder never hits it exactly
            var plan = new SharePlan { AudioBitrate = !sound ? 0 : total >= 2e6 ? 128000 : 96000 };
            plan.Sound = plan.AudioBitrate * Math.Max(1, soundTracks); // every sound track is kept
            double video = total - plan.Sound -(Math.Min(fps, 60) + 47) * 96; // (the .mp4's own bookkeeping per frame)
            if (video < 100000) return null; // too long for this size
            int shortSide = Math.Min(w, h);
            var sides = new List<int> { shortSide };
            sides.AddRange(new[] { 2160, 1440, 1080, 720, 540, 480, 360, 240 }.Where(x => x < shortSide));
            double lowFps = fps >= 50 ? fps / 2 : fps; // 60 frames a second become 30 when it gets tight
            int side = 0;
            double rate = fps;
            // Full frame rate while the picture can stay 540 or bigger; then fewer frames, then smaller still.
            foreach (int s in sides.Where(x => x >= 540 || x == shortSide))
                if (video / (Pixels(w, h, s) * fps) >= Enough) { side = s; break; }
            if (side == 0)
            {
                rate = lowFps;
                foreach (int s in sides)
                    if (video / (Pixels(w, h, s) * rate) >= Enough) { side = s; break; }
                if (side == 0) side = sides[sides.Count - 1];
            }
            plan.MaxShort = side < shortSide ? side : 0;
            plan.MaxFps = rate < fps ? rate : 0;
            plan.Cap = Math.Min(40e6, Math.Max(2e6, Pixels(w, h, side) * rate * 0.12));
            if (sourceBitrate > 0) plan.Cap = Math.Min(plan.Cap, Math.Max(1e6, sourceBitrate * 1.25)); // never much more than the original had
            plan.Capped = video >= plan.Cap;
            plan.VideoBitrate = Math.Min(video, plan.Cap);
            return plan;
        }

        static double Pixels(int w, int h, int shortSide)
        {
            double k = (double)shortSide / Math.Min(w, h);
            return w * k * h * k;
        }
    }

    partial class ViewerWindow
    {
        int discordMb = 20;                 // the size picked last (settings line discordMb=)
        internal string lastShareResult;    // tests
        internal readonly List<string> shareLog = new List<string>();

        void InitShare()
        {
            OnClick("TrimGifBtn", ShowGifMenu);
            OnClick("TrimShareBtn", ShowDiscordMenu);
        }

        void ShowGifMenu()
        {
            ShowMenu("TrimGifBtn", PlacementMode.Top,
                Item("Small GIF (320 pixels)", null, delegate { MakeGif(320); }),
                Item("Medium GIF (480 pixels)", null, delegate { MakeGif(480); }),
                Item("Large GIF (640 pixels)", null, delegate { MakeGif(640); }));
        }

        void ShowDiscordMenu()
        {
            Func<int, string, Control> size = (mb, who) =>
                Item("Fit in " + mb + " MB (" + who + ")", discordMb == mb ? "" : null, delegate { SendToDiscord(mb); });
            var items = new List<Control> { size(20, "Discord without Nitro"), size(50, "Nitro Basic"), size(500, "Nitro") };
            if (HasSubs && shown != null && !shown.IsAudio)
            {
                // Discord shows no subtitle files: ticked, the lines are drawn into the picture. Ticking keeps the menu
                // there (it opens again), so the size can be picked next.
                items.Add(new Separator());
                var burn = Item("Burn in captions", discordBurn ? "\uE73E" : null, delegate
                {
                    discordBurn = !discordBurn;
                    SaveSettings();
                    Dispatcher.BeginInvoke(new Action(delegate { if (trimming) { ClosingOwnMenu(F<Button>("TrimShareBtn")); ShowDiscordMenu(); } }), System.Windows.Threading.DispatcherPriority.Background);
                });
                burn.ToolTip = "Discord doesn't show subtitle files. This draws the captions into the video itself.";
                System.Windows.Automation.AutomationProperties.SetName(burn, "Burn in captions, " + (discordBurn ? "on" : "off"));
                items.Add(burn);
            }
            ShowMenu("TrimShareBtn", PlacementMode.Top, items.ToArray());
        }

        static string SizeText(long bytes)
        {
            double mb = bytes / 1048576.0; // as File Explorer counts
            return (mb >= 100 ? mb.ToString("0", CultureInfo.InvariantCulture) : mb.ToString("0.0", CultureInfo.InvariantCulture)) + " MB";
        }

        // Puts a file on the clipboard, like Copy in File Explorer: Ctrl+V in a chat uploads it.
        bool CopyForPasting(string file)
        {
            if (snapDir != null) { Log("copied for pasting: " + file); return true; } // tests never touch the clipboard
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { Clipboard.SetFileDropList(new StringCollection { file }); return true; }
                catch { Thread.Sleep(60); } // another app has the clipboard open for a moment
            }
            return false;
        }

        // Runs work in the background with the progress message at the top (like saving a clip). work gets a way
        // to report progress (a label, or null for the usual one, and 0..1) and the Cancel flag; done runs back
        // here with the error (null when it went well) and whether it was cancelled.
        void RunJob(string what, Action<Action<string, double>, CancelFlag> work, Action<string, bool> done)
        {
            converting = true;
            var cancel = new CancelFlag();
            ShowToast(what + "... 0%", "Cancel", delegate { cancel.Cancelled = true; }, 0, "convert");
            var line = F<FrameworkElement>("ToastProgress");
            Motion.Set((ScaleTransform)line.RenderTransform, ScaleTransform.ScaleXProperty, 0);
            line.Visibility = Visibility.Visible;
            int lastPercent = -1;
            string lastLabel = what;
            Action<string, double> progress = delegate(string label, double p)
            {
                int percent = (int)(Math.Max(0, Math.Min(1, p)) * 100);
                string text = label ?? what;
                if (percent == lastPercent && text == lastLabel) return;
                lastPercent = percent;
                lastLabel = text;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (toastKind != "convert") return;
                    F<TextBlock>("ToastText").Text = text + "... " + percent + "%";
                    line.Visibility = Visibility.Visible;
                    Motion.To((ScaleTransform)line.RenderTransform, ScaleTransform.ScaleXProperty, percent / 100.0, Motion.Ms(200, false), Motion.Smooth);
                }));
            };
            var thread = new Thread(delegate()
            {
                string error = null;
                try { work(progress, cancel); }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    converting = false;
                    if (closeWhenSaved) Dispatcher.BeginInvoke(new Action(Close)); // the window was closed meanwhile
                    done(error, cancel.Cancelled && error == null);
                }));
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        // ---------------------------------------------------------------- GIF

        void MakeGif(int side)
        {
            var s = shown;
            if (s == null || !trimming || !loopA.HasValue || !loopB.HasValue) return;
            if (converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            var from = loopA.Value;
            var to = loopB.Value;
            if ((to - from).TotalSeconds > 60.05)
            {
                ShowToast("A GIF can be a minute long at most (longer ones get huge): move the handles closer together.", null, null, 6, null);
                return;
            }
            if (playing) TogglePlay();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save GIF",
                FileName = Path.GetFileNameWithoutExtension(s.File) + ".gif",
                InitialDirectory = Path.GetDirectoryName(s.File),
                DefaultExt = ".gif",
                Filter = "GIF animation|*.gif"
            };
            if (dlg.ShowDialog(this) != true) return;
            EndTrim();
            RunGif(s.File, dlg.FileName, from, to, side);
        }

        void RunGif(string source, string target, TimeSpan from, TimeSpan to, int side)
        {
            int turn;
            rotations.TryGetValue(source, out turn); // the way it's shown
            string temp = target + ".fvgif.tmp";
            long bytes = 0;
            int frames = 0;
            string made = "";
            RunJob("Making GIF", delegate(Action<string, double> progress, CancelFlag cancel)
            {
                // Twice through the part: first to pick the colours, then to write the pictures with them.
                var colours = new GifColours();
                var o = new ConvertOptions { MaxLong = side, MaxFps = 15 };
                o.Frame = (px, w, h, time) => colours.Add(px, w * h);
                VideoConverter.Convert(source, null, from, to, turn, p => progress(null, p * 0.3), cancel, o);
                if (cancel.Cancelled) return;
                colours.Build();
                using (var fs = File.Create(temp))
                {
                    GifWriter gif = null;
                    o.Frame = (px, w, h, time) =>
                    {
                        if (gif == null) gif = new GifWriter(fs, w, h, colours);
                        gif.Add(px, time);
                    };
                    VideoConverter.Convert(source, null, from, to, turn, p => progress(null, 0.3 + p * 0.7), cancel, o);
                    if (cancel.Cancelled) return;
                    if (gif == null) throw new Exception("no pictures could be read from this part of the video.");
                    gif.Finish();
                    frames = gif.Frames;
                }
                made = o.OutWidth + " x " + o.OutHeight;
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                bytes = new FileInfo(target).Length;
            }, delegate(string error, bool cancelled)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                if (cancelled || error != null)
                {
                    lastShareResult = cancelled ? "cancelled" : "failed: " + error;
                    ShowToast(cancelled ? "Cancelled." : "Couldn't make the GIF: " + error, null, null, 6, null);
                    return;
                }
                lastShareResult = "gif " + made + ", " + frames + " pictures, " + bytes + " bytes";
                ShowToast("Saved " + Path.GetFileName(target) + " (" + SizeText(bytes) + ")", "Copy to paste", delegate
                {
                    ShowToast(CopyForPasting(target) ? "Copied: paste it with Ctrl+V" : "Couldn't copy it: the clipboard is in use.", null, null, 4, null);
                }, 10, null);
            });
        }

        // ---------------------------------------------------------------- Send to Discord

        void SendToDiscord(int megabytes)
        {
            var s = shown;
            if (s == null || !trimming || !loopA.HasValue || !loopB.HasValue) return;
            if (converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            if (discordMb != megabytes)
            {
                discordMb = megabytes;
                SaveSettings();
            }
            if (playing) TogglePlay();
            double length = TrimLength;
            bool whole = loopA.Value.TotalSeconds < 0.05 && loopB.Value.TotalSeconds > length - 0.05;
            TimeSpan? from = whole ? (TimeSpan?)null : loopA.Value, to = whole ? (TimeSpan?)null : loopB.Value;
            string file = s.File;
            int w = s.Video.NaturalVideoWidth, h = s.Video.NaturalVideoHeight;
            double fps = s.FrameSeconds > 0 ? 1 / s.FrameSeconds : 30;
            bool sound = s.Video.HasAudio;
            EndTrim();
            ShareToDiscord(file, from, to, megabytes, w, h, fps, sound, length);
        }

        void ShareToDiscord(string source, TimeSpan? from, TimeSpan? to, int megabytes, int w, int h, double fps, bool sound, double length)
        {
            // Just under the size, counted in steps of 1000: it then fits whether Discord counts in 1000s or 1024s
            // (20 MB -> at most 19.8 million bytes, which File Explorer shows as 18.9 MB).
            long ceiling = megabytes * 990000L;
            double seconds = ((to ?? TimeSpan.FromSeconds(length)) - (from ?? TimeSpan.Zero)).TotalSeconds;
            long sourceSize = new FileInfo(source).Length;
            int turn;
            rotations.TryGetValue(source, out turn);
            string name = Path.GetFileNameWithoutExtension(source);
            shareLog.Clear();

            // The whole video, already small enough, an ordinary .mp4 that's shown the way it's stored: nothing to do.
            var painter = discordBurn ? MakePainter(source, from) : null; // captions drawn into the picture
            if (painter == null && from == null && sourceSize <= ceiling && Path.GetExtension(source).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && turn == VideoInfo.Rotation(source) && VideoInfo.VideoCodec(source) == "avc1")
            {
                lastShareResult = "as is, " + sourceSize + " bytes";
                bool copied = CopyForPasting(source);
                ShowToast(copied ? "Already under " + megabytes + " MB: copied as it is. Paste it in Discord with Ctrl+V"
                                 : "Couldn't copy it: the clipboard is in use.", null, null, 8, null);
                return;
            }

            var plan = SharePlan.Make(ceiling, seconds, w, h, fps, sound, length > 0 ? sourceSize * 8.0 / length : 0,
                                      sound ? VideoConverter.SoundTracks(source) : 1);
            if (plan == null)
            {
                lastShareResult = "too long";
                ShowToast("This part is " + LengthText(TimeSpan.FromSeconds(seconds)) + " long: too long to fit in " + megabytes +
                          " MB. Trim it shorter or pick a bigger size.", null, null, 8, null);
                return;
            }

            string folder = Path.Combine(Path.GetTempPath(), "FastViewer", "Discord");
            string target = Path.Combine(folder, name + ".mp4"), temp = null;
            long bytes = 0;
            string made = "";
            bool have = false;
            RunJob("Making it fit in " + megabytes + " MB", delegate(Action<string, double> progress, CancelFlag cancel)
            {
                Directory.CreateDirectory(folder);
                foreach (var old in Directory.GetFiles(folder)) // copies made over a week ago
                    try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old); } catch { }
                for (int n = 2; File.Exists(target); n++) // an earlier copy of the same video (it may still be uploading)
                {
                    try { File.Delete(target); }
                    catch { target = Path.Combine(folder, name + " (" + n + ").mp4"); }
                }
                temp = target + ".part";
                double video = plan.VideoBitrate;
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    var o = new ConvertOptions { VideoBitrate = (int)video, AudioBitrate = plan.AudioBitrate, MaxShort = plan.MaxShort, MaxFps = plan.MaxFps };
                    if (painter != null) { painter.Painted = painter.Lines = 0; o.Paint = painter.Paint; }
                    string label = attempt == 1 ? null : "Fitting it once more (try " + attempt + ")";
                    VideoConverter.Convert(source, temp, from, to, turn, p => progress(label, p), cancel, o);
                    if (cancel.Cancelled) return;
                    long size = new FileInfo(temp).Length;
                    lock (shareLog) shareLog.Add("try " + attempt + ": video " + (video / 1000).ToString("0") + " kbit/s, sound " + plan.Sound / 1000 +
                        " kbit/s, " + o.OutWidth + "x" + o.OutHeight + " at " + o.OutFps.ToString("0.##") + " fps -> " + size + " bytes (limit " + ceiling + ")");
                    if (size <= ceiling)
                    {
                        if (have) File.Delete(target);
                        File.Move(temp, target);
                        have = true;
                        bytes = size;
                        made = Math.Min(o.OutWidth, o.OutHeight) + "p";
                        if (plan.Capped || attempt > 1 || size >= ceiling * 0.85) break;
                        // A good deal of room left: once more with more bits (the smaller one stays if that overshoots).
                        double more = Math.Min(plan.Cap, (video + plan.Sound) * (ceiling * 0.96 / size) - plan.Sound);
                        if (more < video * 1.08) break;
                        video = more;
                    }
                    else
                    {
                        if (have) break;
                        video = (video + plan.Sound) * ((double)ceiling / size) * 0.95 - plan.Sound;
                        if (video < 60000) throw new Exception("this part is too long to fit in " + megabytes + " MB.");
                    }
                }
                if (!have) throw new Exception("it kept coming out bigger than " + megabytes + " MB.");
            }, delegate(string error, bool cancelled)
            {
                try { if (temp != null && File.Exists(temp)) File.Delete(temp); } catch { }
                if (cancelled || error != null)
                {
                    try { if (have) File.Delete(target); } catch { }
                    lastShareResult = cancelled ? "cancelled" : "failed: " + error;
                    ShowToast(cancelled ? "Cancelled." : "Couldn't make the copy for Discord: " + error, null, null, 6, null);
                    return;
                }
                lastShareResult = "made " + made + ", " + bytes + " bytes" + (painter != null ? ", captions on " + painter.Painted + " pictures (" + painter.Lines + " lines)" : "") + ", " + target;
                bool copied = CopyForPasting(target);
                ShowToast((copied ? "Copied (" : "Made, but the clipboard is in use (") + SizeText(bytes) + ", " + made + (painter != null && painter.Lines > 0 ? ", with captions" : "") + ")" +
                          (copied ? ": paste it in Discord with Ctrl+V" : ": use Show in folder"), "Show in folder", delegate
                {
                    Process.Start("explorer.exe", "/select,\"" + target + "\"");
                    HideToast(null);
                }, 12, null);
            });
        }
    }
}
