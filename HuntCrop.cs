// FastViewer: hidden test runs for sharing and cropping (--hunt <out> --case share | crop). Like every test
// run: muted, cloaked, never touching the clipboard. The crop case changes files: scratch copies only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        // Runs `then` once `ready` is true (asked ten times a second); a time-out is logged as a failure.
        void Until(Func<bool> ready, int timeoutMs, Action then)
        {
            var started = DateTime.UtcNow;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            t.Tick += delegate
            {
                bool ok = false;
                try { ok = ready(); } catch { }
                if (!ok && (DateTime.UtcNow - started).TotalMilliseconds < timeoutMs) return;
                t.Stop();
                if (!ok) HuntLog("TIMED OUT waiting FAIL");
                try { then(); }
                catch (Exception ex) { HuntLog("test step threw " + ex.GetType().Name + ": " + ex.Message + " FAIL"); HuntDone(); }
            };
            t.Start();
        }

        // Lets the window draw what just changed, so a picture taken right after shows it.
        void Settle()
        {
            Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
            System.Threading.Thread.Sleep(120);
            Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
        }
        void HuntOpen(string name, Action then)
        {
            for (int i = 0; i < files.Count; i++)
                if (Path.GetFileName(files[i]).Equals(name, StringComparison.OrdinalIgnoreCase)) ShowIndex(i, 1);
            Until(() => shown != null && shown.Ready && Path.GetFileName(shown.File).Equals(name, StringComparison.OrdinalIgnoreCase)
                        && (!shown.IsVideo || shown.Video.NaturalVideoWidth > 0), 15000, delegate { After(500, then); });
        }

        static string GifFacts(string file)
        {
            try
            {
                using (var fs = File.OpenRead(file))
                {
                    var d = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    int total = 0, least = int.MaxValue, most = 0, smallest = int.MaxValue;
                    foreach (var f in d.Frames)
                    {
                        var m = (BitmapMetadata)f.Metadata;
                        int delay = Convert.ToInt32(m.GetQuery("/grctlext/Delay"));
                        total += delay;
                        least = Math.Min(least, delay);
                        most = Math.Max(most, delay);
                        smallest = Math.Min(smallest, Convert.ToInt32(m.GetQuery("/imgdesc/Width")) * Convert.ToInt32(m.GetQuery("/imgdesc/Height")));
                    }
                    var screen = (BitmapMetadata)d.Metadata;
                    return "Windows reads the GIF: " + d.Frames.Count + " pictures, screen " + screen.GetQuery("/logscrdesc/Width") + "x" + screen.GetQuery("/logscrdesc/Height") +
                        ", plays " + (total / 100.0).ToString("0.00") + " s, each " + least + "-" + most + " hundredths, smallest stored picture " + smallest + " pixels, " +
                        new FileInfo(file).Length + " bytes";
                }
            }
            catch (Exception ex) { return "Windows can't read the GIF: " + ex.Message + " FAIL"; }
        }

        void HuntShare()
        {
            Func<SharePlan, string> say = p => p == null ? "too long" : (p.VideoBitrate / 1000).ToString("0") + " kbit/s video, " + p.AudioBitrate / 1000 + " sound, short side " +
                (p.MaxShort == 0 ? "kept" : p.MaxShort.ToString()) + ", frames " + (p.MaxFps == 0 ? "kept" : p.MaxFps.ToString("0.##")) + (p.Capped ? ", as good as it gets" : "");
            foreach (double sec in new[] { 8.0, 15, 30, 60, 120, 300, 600, 900 })
                HuntLog("plan 1920x1080 60 fps, " + sec + " s in 20 MB: " + say(SharePlan.Make(20 * 990000L, sec, 1920, 1080, 60, true, 30e6)));
            HuntLog("plan 1080x1920 30 fps, 45 s in 20 MB: " + say(SharePlan.Make(20 * 990000L, 45, 1080, 1920, 30, true, 12e6)));
            HuntLog("plan 1920x1080 60 fps, 30 s in 50 MB: " + say(SharePlan.Make(50 * 990000L, 30, 1920, 1080, 60, true, 30e6)));
            HuntLog("plan 1920x1080 60 fps, 30 s in 500 MB: " + say(SharePlan.Make(500 * 990000L, 30, 1920, 1080, 60, true, 30e6)));
            HuntLog("plan 640x360 30 fps, 20 s in 20 MB, original 800 kbit/s: " + say(SharePlan.Make(20 * 990000L, 20, 640, 360, 30, false, 8e5)));

            string file = null, gif = Path.Combine(snapDir, "out.gif");
            int w = 0, h = 0;
            double fps = 30, length = 0;
            bool sound = false;
            TimeSpan from = TimeSpan.FromSeconds(1), to = TimeSpan.FromSeconds(4);
            Func<string> bar = () => "trim bar " + F<FrameworkElement>("TrimBar").ActualWidth.ToString("0") + " wide: GIF button '" + F<TextBlock>("TrimGifText").Text +
                "' " + F<FrameworkElement>("TrimGifBtn").ActualWidth.ToString("0") + ", Discord words shown " + F<FrameworkElement>("TrimShareText").IsVisible + " " +
                F<FrameworkElement>("TrimShareBtn").ActualWidth.ToString("0") + ", hint shown " + F<FrameworkElement>("TrimHint").IsVisible + ", title '" + F<TextBlock>("TrimText").Text + "' " +
                F<FrameworkElement>("TrimText").ActualWidth.ToString("0") + " wide, save button right edge " +
                F<FrameworkElement>("TrimSaveBtn").TranslatePoint(new Point(F<FrameworkElement>("TrimSaveBtn").ActualWidth, 0), F<FrameworkElement>("TrimBar")).X.ToString("0");
            After(1500, () =>
            {
                if (playing) TogglePlay();
                var s = shown;
                file = s.File;
                w = s.Video.NaturalVideoWidth;
                h = s.Video.NaturalVideoHeight;
                fps = 1 / s.FrameSeconds;
                sound = s.Video.HasAudio;
                StartTrim();
                length = TrimLength;
                HuntLog("video " + Path.GetFileName(file) + " " + w + "x" + h + " " + fps.ToString("0.##") + " fps, " + length.ToString("0.00") + " s, sound " + sound);
            });
            After(2500, () => { HuntLog(bar()); Snap("share_bar"); Width = 470; });
            After(3200, () => { HuntLog("narrow window: " + bar()); Snap("share_bar_narrow"); Width = 1100; });
            After(3900, () =>
            {
                ShowGifMenu();
                HuntLog("GIF menu drawn");
                Snap("share_gif_menu");
            });
            After(4400, () =>
            {
                ShowDiscordMenu();
                Snap("share_discord_menu");
                EndTrim();
                lastShareResult = null;
                RunGif(file, gif, from, to, 480);
                HuntLog("making the GIF: saving flag " + converting + ", message '" + F<TextBlock>("ToastText").Text + "'");
            });
            After(4500, () => Until(() => !converting && lastShareResult != null, 180000, () =>
            {
                HuntLog("GIF: " + lastShareResult + "; message '" + F<TextBlock>("ToastText").Text + "', button '" + F<Button>("ToastBtn").Content + "'");
                HuntLog(GifFacts(gif));
                HuntLog("left behind: " + string.Join(", ", Directory.GetFiles(snapDir, "*.tmp").Select(Path.GetFileName)));
                lastShareResult = null;
                ShareToDiscord(file, from, to, 1, w, h, fps, sound, length); // "1 MB" so a short test clip has to be squeezed
                Until(() => !converting && lastShareResult != null, 300000, () =>
                {
                    foreach (var line in shareLog) HuntLog("  " + line);
                    HuntLog("3 s part in 1 MB: " + lastShareResult + "; message '" + F<TextBlock>("ToastText").Text + "', button '" + F<Button>("ToastBtn").Content + "'");
                    lastShareResult = null;
                    ShareToDiscord(file, null, null, 1, w, h, fps, sound, length);
                    Until(() => !converting && lastShareResult != null, 300000, () =>
                    {
                        foreach (var line in shareLog) HuntLog("  " + line);
                        HuntLog("whole video in 1 MB: " + lastShareResult + "; message '" + F<TextBlock>("ToastText").Text + "'");
                        lastShareResult = null;
                        ShareToDiscord(file, null, null, 500, w, h, fps, sound, length);
                        Until(() => !converting && lastShareResult != null, 300000, () =>
                        {
                            foreach (var line in shareLog) HuntLog("  " + line);
                            HuntLog("whole video in 500 MB: " + lastShareResult + "; message '" + F<TextBlock>("ToastText").Text + "'");
                            HuntLog("settings: discordMb " + discordMb);
                            HuntDone();
                        });
                    });
                });
            }));
        }

        // --case tracks (a scratch folder with two.mp4, a clip with two or more sound tracks): saves it every way a
        // video can be saved (Save clip, compatible copy, Send to Discord, crop as a copy, crop in place) into the
        // output folder, where the runner checks with ffmpeg that each result has every track, in the original order.
        void HuntTracks()
        {
            After(1500, () => HuntOpen("two.mp4", () =>
            {
                if (playing) TogglePlay();
                var s = shown;
                string file = s.File;
                string clip = Path.Combine(snapDir, "out clip.mp4"), compatible = Path.Combine(snapDir, "out compatible.mp4"), discord = Path.Combine(snapDir, "out discord.mp4"),
                       cropCopy = Path.Combine(snapDir, "out crop copy.mp4"), cropped = Path.Combine(snapDir, "out cropped in place.mp4");
                int w = s.Video.NaturalVideoWidth, h = s.Video.NaturalVideoHeight, tracks = VideoConverter.SoundTracks(file);
                double fps = 1 / s.FrameSeconds, length = s.Video.NaturalDuration.TimeSpan.TotalSeconds;
                long bytes = new FileInfo(file).Length;
                TimeSpan from = TimeSpan.FromSeconds(1), to = TimeSpan.FromSeconds(3);
                var part = new[] { 0.25, 0.25, 0.5, 0.5 };
                Func<string, string> facts = name =>
                {
                    int pw, ph, found = VideoConverter.SoundTracks(name);
                    double sec;
                    VideoConverter.Probe(name, out pw, out ph, out sec);
                    return pw + "x" + ph + " " + sec.ToString("0.00") + " s, " + found + " sound tracks" + (found == tracks ? "" : " FAIL");
                };
                HuntLog("two.mp4: " + facts(file) + (tracks >= 2 ? "" : " (the test needs 2 or more) FAIL"));
                RunConversion(file, clip, from, to, "Saving clip");
                Until(() => !converting, 300000, () =>
                {
                    HuntLog("Save clip 1-3 s: " + facts(clip) + "; " + VideoConverter.LastInfo);
                    RunConversion(file, compatible, null, null, "Making a compatible copy");
                    Until(() => !converting, 300000, () =>
                    {
                        HuntLog("compatible copy: " + facts(compatible));
                        lastShareResult = null;
                        ShareToDiscord(file, from, to, 1, w, h, fps, true, length);
                        Until(() => !converting && lastShareResult != null, 300000, () =>
                        {
                            foreach (var line in shareLog) HuntLog("  " + line);
                            string made = lastShareResult.StartsWith("made ") ? lastShareResult.Substring(lastShareResult.LastIndexOf(", ") + 2) : null;
                            if (made != null && File.Exists(made)) File.Copy(made, discord, true);
                            HuntLog("Send to Discord 1-3 s in 1 MB: " + lastShareResult + "; " + (File.Exists(discord) ? facts(discord) : "no copy FAIL"));
                            lastCropResult = null;
                            CropVideoFile(file, cropCopy, part);
                            Until(() => !converting && lastCropResult != null, 300000, () =>
                            {
                                HuntLog("crop, save a copy: " + lastCropResult + "; " + facts(cropCopy) + "; original untouched " + (new FileInfo(file).Length == bytes));
                                lastCropResult = null;
                                CropVideoFile(file, file, part);
                                Until(() => !converting && lastCropResult != null && shown.Ready, 300000, () => After(1500, () =>
                                {
                                    string aside = Path.Combine(TestBin ?? "", "two.mp4");
                                    File.Copy(file, cropped, true);
                                    HuntLog("crop, Save: " + lastCropResult + "; " + facts(file) + "; message '" + F<TextBlock>("ToastText").Text + "'; the original " +
                                        (RealBin ? "went to the real Recycle Bin (look there)" : File.Exists(aside) && new FileInfo(aside).Length == bytes ? "is whole in the test bin" : "is gone FAIL") +
                                        "; left behind: " + string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(file), "*.fvcrop.*").Select(Path.GetFileName)));
                                    HuntDone();
                                }));
                            });
                        });
                    });
                });
            }));
        }

        static double PictureDifference(BitmapSource a, BitmapSource b)
        {
            if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 999;
            int w = a.PixelWidth, h = a.PixelHeight;
            var pa = new byte[w * h * 4];
            var pb = new byte[w * h * 4];
            new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0).CopyPixels(pa, w * 4, 0);
            new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(pb, w * 4, 0);
            long sum = 0;
            for (int i = 0; i < pa.Length; i++) sum += Math.Abs(pa[i] - pb[i]);
            return sum / (double)pa.Length;
        }

        // The part of the photo as FastViewer shows it (turn included), cut the way CropPhoto cuts.
        static BitmapSource ExpectedCrop(string file, double[] part, int turn)
        {
            int fw, fh;
            BitmapSource full = DecodeImage(file, MaxDecodeSide, out fw, out fh);
            if (turn != 0) full = new TransformedBitmap(full, new RotateTransform(turn));
            int W = full.PixelWidth, H = full.PixelHeight;
            int x = Math.Max(0, Math.Min(W - 1, (int)Math.Round(part[0] * W))), y = Math.Max(0, Math.Min(H - 1, (int)Math.Round(part[1] * H)));
            int w = Math.Max(1, Math.Min(W - x, (int)Math.Round(part[2] * W))), h = Math.Max(1, Math.Min(H - y, (int)Math.Round(part[3] * H)));
            var cut = new CachedBitmap(new CroppedBitmap(full, new Int32Rect(x, y, w, h)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cut.Freeze();
            return cut;
        }

        string CropState()
        {
            int w, h;
            CropPixels(out w, out h);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "frame {0:0.000},{1:0.000} - {2:0.000},{3:0.000} = {4}x{5} ({6:0.0000}), text '{7}'",
                cropX0, cropY0, cropX1, cropY1, w, h, (double)w / h, F<TextBlock>("CropText").Text);
        }

        static string PhotoFacts(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var d = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var f = d.Frames[0];
                string extra = "";
                try
                {
                    var m = f.Metadata as BitmapMetadata;
                    if (m != null)
                        extra = ", orientation tag " + (m.GetQuery("System.Photo.Orientation") ?? "none") + ", camera '" + (m.GetQuery("System.Photo.CameraModel") ?? "") +
                            "', preview inside " + (f.Thumbnail != null || m.ContainsQuery("/app1/thumb/")) ;
                }
                catch (Exception ex) { extra = ", details unreadable (" + ex.GetType().Name + ")"; }
                return d.CodecInfo.FriendlyName + " " + f.PixelWidth + "x" + f.PixelHeight + " " + f.Format + extra + ", " + new FileInfo(file).Length + " bytes";
            }
        }

        void HuntCrop()
        {
            string dir = Path.GetDirectoryName(files[0]);
            Func<string> leftovers = () => string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n.Contains(".fv")).DefaultIfEmpty("none"));
            var part = new[] { 0.25, 0.25, 0.5, 0.5 };
            Action finish = () => { HuntLog("left behind in the folder: " + leftovers()); HuntDone(); };

            Action video2 = () => HuntOpen("turned.mp4", () =>
            {
                var s = shown;
                string file = s.File;
                int turn;
                rotations.TryGetValue(file, out turn);
                HuntLog("turned.mp4 shown " + s.Video.NaturalVideoWidth + "x" + s.Video.NaturalVideoHeight + " with turn " + turn + " (the file says " + VideoInfo.Rotation(file) + ")");
                StartCrop();
                int cw, ch;
                cropX0 = 0; cropY0 = 0; cropX1 = 0.5; cropY1 = 0.5;
                PlaceCrop();
                CropPixels(out cw, out ch);
                HuntLog("its crop frame: " + CropState() + ", Save shown " + F<FrameworkElement>("CropSaveBtn").IsVisible);
                lastCropResult = null;
                EndCrop();
                CropVideoFile(file, file, new[] { 0.0, 0.0, 0.5, 0.5 });
                Until(() => !converting && lastCropResult != null && shown.Ready, 300000, () => After(1500, () =>
                {
                    int w, h; double sec;
                    VideoConverter.Probe(file, out w, out h, out sec);
                    HuntLog("turned.mp4 cropped in place: " + lastCropResult + "; Windows reads " + w + "x" + h + " " + sec.ToString("0.00") + " s, turn in the file " + VideoInfo.Rotation(file) +
                        ", turn remembered " + rotations.ContainsKey(file) + ", player shows " + shown.Video.NaturalVideoWidth + "x" + shown.Video.NaturalVideoHeight + " at angle " + shown.Rotation.Angle +
                        (w == cw && h == ch ? "" : " FAIL (wanted " + cw + "x" + ch + ")"));
                    finish();
                }));
            });

            Action video = () => HuntOpen("clip.mp4", () =>
            {
                var s = shown;
                string file = s.File, copy = Path.Combine(dir, "clip copy.mp4");
                var created = File.GetCreationTimeUtc(file);
                var modified = File.GetLastWriteTimeUtc(file);
                if (playing) TogglePlay();
                s.Video.Position = TimeSpan.FromSeconds(2);
                StartCrop();
                HuntLog("clip.mp4 " + s.Video.NaturalVideoWidth + "x" + s.Video.NaturalVideoHeight + ": cropping " + cropping + ", control bar shown " + transport.IsVisible +
                    ", Save shown " + F<FrameworkElement>("CropSaveBtn").IsVisible + ", picture at " + cropPic + ", " + CropState());
                CropTakeHold(3, new Point(0, 0));
                CropDragTo(new Point(0.25, 0.1));
                CropTakeHold(12, new Point(1, 1));
                CropDragTo(new Point(0.75, 0.9));
                HuntLog("dragged two corners: " + CropState());
                Settle(); Snap("crop_video");
                PressKey(Key.Space);
                bool played = playing;
                PressKey(Key.Space);
                HuntLog("Space while cropping plays " + played + ", still cropping " + cropping);
                var vpart = new[] { cropX0, cropY0, cropX1 - cropX0, cropY1 - cropY0 };
                int cw, ch;
                CropPixels(out cw, out ch);
                lastCropResult = null;
                EndCrop();
                CropVideoFile(file, copy, vpart);
                Until(() => !converting && lastCropResult != null, 300000, () =>
                {
                    int w, h; double sec;
                    VideoConverter.Probe(copy, out w, out h, out sec);
                    HuntLog("clip copy.mp4: " + lastCropResult + "; Windows reads " + w + "x" + h + " " + sec.ToString("0.00") + " s, " + new FileInfo(copy).Length + " bytes; message '" +
                        F<TextBlock>("ToastText").Text + "'; original untouched " + (File.GetLastWriteTimeUtc(file) == modified) + (w == cw && h == ch ? "" : " FAIL (wanted " + cw + "x" + ch + ")"));
                    lastCropResult = null;
                    CropVideoFile(file, file, vpart);
                    Until(() => !converting && lastCropResult != null && shown.Ready, 300000, () => After(1500, () =>
                    {
                        VideoConverter.Probe(file, out w, out h, out sec);
                        HuntLog("clip.mp4 cropped in place: " + lastCropResult + "; Windows reads " + w + "x" + h + " " + sec.ToString("0.00") + " s, " + new FileInfo(file).Length +
                            " bytes, date moved " + (File.GetLastWriteTimeUtc(file) - modified).TotalMilliseconds.ToString("0") + " ms, created kept " + (File.GetCreationTimeUtc(file) == created) +
                            ", player shows " + shown.Video.NaturalVideoWidth + "x" + shown.Video.NaturalVideoHeight + " at " + shown.Video.Position.TotalSeconds.ToString("0.00") +
                            " s; message '" + F<TextBlock>("ToastText").Text + "'" + (w == cw && h == ch ? "" : " FAIL (wanted " + cw + "x" + ch + ")"));
                        video2();
                    }));
                });
            });

            Action webp = () => HuntOpen("other.webp", () =>
            {
                StartCrop();
                HuntLog("other.webp: cropping " + cropping + ", Save shown " + F<FrameworkElement>("CropSaveBtn").IsVisible + ", saves as " + CropSaveType(".webp"));
                EndCrop();
                string copy = Path.Combine(dir, "other (cropped).png");
                lastCropResult = null;
                CropPhotoFile(shown.File, copy, ".png", part);
                Until(() => lastCropResult != null, 30000, () =>
                {
                    HuntLog("other (cropped).png: " + lastCropResult + "; " + (File.Exists(copy) ? PhotoFacts(copy) : "missing FAIL"));
                    video();
                });
            });

            Action png = () => HuntOpen("plain.png", () =>
            {
                string file = shown.File;
                var expected = ExpectedCrop(file, part, 0);
                lastCropResult = null;
                CropPhotoFile(file, file, ".png", part);
                Until(() => lastCropResult != null, 30000, () =>
                {
                    int fw, fh;
                    var now = DecodeImage(file, MaxDecodeSide, out fw, out fh);
                    HuntLog("plain.png cropped in place: " + lastCropResult + "; " + PhotoFacts(file) + "; differs from the part that was shown by " +
                        PictureDifference(expected, now).ToString("0.00") + " of 255; player has " + shown.FullWidth + "x" + shown.FullHeight);
                    webp();
                });
            });

            After(1500, () => HuntOpen("photo.jpg", () =>
            {
                var s = shown;
                string file = s.File, copy = Path.Combine(dir, "photo copy.jpg");
                HuntLog("photo.jpg before: " + PhotoFacts(file) + "; shown as " + s.FullWidth + "x" + s.FullHeight);
                StartCrop();
                HuntLog("crop open: cropping " + cropping + ", layer " + F<FrameworkElement>("CropLayer").ActualWidth.ToString("0") + "x" + F<FrameworkElement>("CropLayer").ActualHeight.ToString("0") +
                    ", stage " + stage.ActualWidth.ToString("0") + "x" + stage.ActualHeight.ToString("0") + ", picture at " + cropPic + ", bar " + F<FrameworkElement>("CropBar").ActualWidth.ToString("0") +
                    " wide, Save shown " + F<FrameworkElement>("CropSaveBtn").IsVisible + ", arrows shown " + F<FrameworkElement>("NextBtn").IsVisible + ", " + CropState());
                Settle(); Snap("crop_open");
                CropTakeHold(3, new Point(0, 0));
                CropDragTo(new Point(0.25, 0.25));
                HuntLog("top-left corner to 25%: " + CropState());
                CropTakeHold(12, new Point(1, 1));
                CropDragTo(new Point(0.75, 0.8));
                HuntLog("bottom-right corner to 75%, 80%: " + CropState());
                CropTakeHold(16, new Point(0.5, 0.5));
                CropDragTo(new Point(0.6, 0.5));
                HuntLog("moved right by 10%: " + CropState());
                CropDragTo(new Point(3, 0.5));
                HuntLog("moved far past the edge: " + CropState());
                CropTakeHold(1, new Point(cropX0, 0.5));
                CropDragTo(new Point(2, 0.5));
                HuntLog("left side dragged past the right side: " + CropState());
                Settle(); Snap("crop_dragged");
                SetCropShape("Square", 1);
                HuntLog("Square: " + CropState());
                CropTakeHold(12, new Point(cropX1, cropY1));
                CropDragTo(new Point(1, 1));
                HuntLog("square, bottom-right corner to the picture's corner: " + CropState());
                SetCropShape("16:9", 16 / 9.0);
                HuntLog("16:9: " + CropState());
                CropTakeHold(4, new Point(cropX1, 0.5));
                CropDragTo(new Point(0.5, 0.5));
                HuntLog("16:9, right side inward: " + CropState());
                CropTakeHold(32, new Point(0.1, 0.1));
                CropDragTo(new Point(0.9, 0.3));
                HuntLog("16:9, new frame dragged from 10%,10% to 90%,30%: " + CropState());
                F<Button>("CropResetBtn").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                HuntLog("Reset: " + CropState() + ", shape '" + F<TextBlock>("CropRatioText").Text + "'");
                ShowCropShapeMenu();
                Settle(); Snap("crop_shapes");
                PressKey(Key.Right);
                PressKey(Key.Enter);
                HuntLog("Right, then Enter on the whole picture: still cropping " + cropping + ", file " + Path.GetFileName(shown.File) + ", message '" + F<TextBlock>("ToastText").Text + "'");
                Width = 470;
                After(700, () =>
                {
                    HuntLog("narrow window: bar " + F<FrameworkElement>("CropBar").ActualWidth.ToString("0") + " wide, copy button '" + F<Button>("CropCopyBtn").Content + "', Reset shown " +
                        F<FrameworkElement>("CropResetBtn").IsVisible + ", save button right edge " +
                        F<FrameworkElement>("CropSaveBtn").TranslatePoint(new Point(F<FrameworkElement>("CropSaveBtn").ActualWidth, 0), F<FrameworkElement>("CropBar")).X.ToString("0") +
                        ", title " + F<FrameworkElement>("CropText").ActualWidth.ToString("0") + " wide, picture at " + cropPic);
                    Settle(); Snap("crop_narrow");
                    Width = 1100;
                    PressKey(Key.Escape);
                    HuntLog("Esc: cropping " + cropping + ", layer shown " + F<FrameworkElement>("CropLayer").IsVisible + ", picture margin " + shown.Host.Margin);
                    Rotate(); // a turn that isn't in the file: the crop is of the picture as it's shown
                });
                After(1600, () =>
                {
                    int turn;
                    rotations.TryGetValue(file, out turn);
                    var expected = ExpectedCrop(file, part, turn);
                    var created = File.GetCreationTimeUtc(file);
                    var modified = File.GetLastWriteTimeUtc(file);
                    lastCropResult = null;
                    CropPhotoFile(file, copy, ".jpg", part);
                    Until(() => lastCropResult != null, 30000, () =>
                    {
                        int fw, fh;
                        HuntLog("photo copy.jpg (turned " + turn + "): " + lastCropResult + "; " + PhotoFacts(copy) + "; differs from the part that was shown by " +
                            PictureDifference(expected, DecodeImage(copy, MaxDecodeSide, out fw, out fh)).ToString("0.00") + " of 255; original untouched " +
                            (File.GetLastWriteTimeUtc(file) == modified) + "; message '" + F<TextBlock>("ToastText").Text + "'");
                        lastCropResult = null;
                        CropPhotoFile(file, file, ".jpg", part);
                        Until(() => lastCropResult != null, 30000, () =>
                        {
                            HuntLog("photo.jpg cropped in place: " + lastCropResult + "; " + PhotoFacts(file) + "; differs from the part that was shown by " +
                                PictureDifference(expected, DecodeImage(file, MaxDecodeSide, out fw, out fh)).ToString("0.00") + " of 255; date moved " +
                                (File.GetLastWriteTimeUtc(file) - modified).TotalMilliseconds.ToString("0") + " ms, created kept " + (File.GetCreationTimeUtc(file) == created) +
                                ", player has " + shown.FullWidth + "x" + shown.FullHeight + " at angle " + shown.Rotation.Angle + ", turn remembered " + rotations.ContainsKey(file) +
                                "; message '" + F<TextBlock>("ToastText").Text + "'");
                            Settle(); Snap("crop_saved");
                            png();
                        });
                    });
                });
            }));
        }
    }
}
