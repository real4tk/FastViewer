// FastViewer: animated GIFs, saving a photo's rotation into the file, and the optional startup speed-up.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    // All frames of an animated GIF, already composed into full pictures.
    class GifAnimation
    {
        public BitmapSource[] Frames;
        public int[] DelaysMs;

        // Returns null for a still GIF, or one too big to keep in memory (it then shows as a still).
        public static GifAnimation Load(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int count = decoder.Frames.Count;
                if (count < 2) return null;
                int w = decoder.Frames[0].PixelWidth, h = decoder.Frames[0].PixelHeight;
                try
                {
                    var screen = decoder.Metadata;
                    if (screen != null)
                    {
                        w = Math.Max(w, Convert.ToInt32(screen.GetQuery("/logscrdesc/Width")));
                        h = Math.Max(h, Convert.ToInt32(screen.GetQuery("/logscrdesc/Height")));
                    }
                }
                catch { }
                if ((long)w * h * 4 * count > 400L * 1024 * 1024) return null;

                var anim = new GifAnimation { Frames = new BitmapSource[count], DelaysMs = new int[count] };
                var canvas = new int[w * h];
                for (int i = 0; i < count; i++)
                {
                    var frame = decoder.Frames[i];
                    int left = 0, top = 0, delay = 10, disposal = 0;
                    var meta = frame.Metadata as BitmapMetadata;
                    if (meta != null)
                    {
                        try { left = Convert.ToInt32(meta.GetQuery("/imgdesc/Left")); } catch { }
                        try { top = Convert.ToInt32(meta.GetQuery("/imgdesc/Top")); } catch { }
                        try { delay = Convert.ToInt32(meta.GetQuery("/grctlext/Delay")); } catch { }
                        try { disposal = Convert.ToInt32(meta.GetQuery("/grctlext/Disposal")); } catch { }
                    }
                    int[] before = disposal == 3 ? (int[])canvas.Clone() : null;

                    var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
                    var px = new int[fw * fh];
                    bgra.CopyPixels(px, fw * 4, 0);
                    for (int y = 0; y < fh; y++)
                    {
                        int cy = top + y;
                        if (cy < 0 || cy >= h) continue;
                        for (int x = 0; x < fw; x++)
                        {
                            int cx = left + x, p = px[y * fw + x];
                            if (cx >= 0 && cx < w && (p >> 24) != 0) canvas[cy * w + cx] = p; // GIF pixels are fully clear or fully solid
                        }
                    }

                    var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, canvas, w * 4);
                    full.Freeze();
                    anim.Frames[i] = full;
                    anim.DelaysMs[i] = delay <= 1 ? 100 : delay * 10; // like browsers: 0 or 1 means "default"

                    if (disposal == 2) // clear this frame's area for the next one
                    {
                        for (int y = Math.Max(0, top); y < Math.Min(h, top + fh); y++)
                            for (int x = Math.Max(0, left); x < Math.Min(w, left + fw); x++)
                                canvas[y * w + x] = 0;
                    }
                    else if (disposal == 3) canvas = before; // go back to how it was before this frame
                }
                return anim;
            }
        }
    }

    partial class ViewerWindow
    {
        readonly DispatcherTimer gifTimer = new DispatcherTimer();
        bool saveRotation;   // photos: also save the rotation into the file (Settings)
        readonly DispatcherTimer rotationSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };

        // ---------------------------------------------------------------- animated GIFs

        void UpdateGif()
        {
            gifTimer.Stop();
            var s = shown;
            if (s == null || s.Gif == null) return;
            s.GifFrame = 0;
            s.Picture.Source = s.Gif.Frames[0];
            gifTimer.Interval = TimeSpan.FromMilliseconds(s.Gif.DelaysMs[0]);
            gifTimer.Start();
        }

        void NextGifFrame()
        {
            var s = shown;
            if (s == null || s.Gif == null)
            {
                gifTimer.Stop();
                return;
            }
            s.GifFrame = (s.GifFrame + 1) % s.Gif.Frames.Length;
            s.Picture.Source = s.Gif.Frames[s.GifFrame];
            gifTimer.Interval = TimeSpan.FromMilliseconds(s.Gif.DelaysMs[s.GifFrame]);
        }

        // ---------------------------------------------------------------- save a photo's rotation

        // After rotating a photo: saves it into the file a moment later, if that's switched on in Settings.
        void AfterPhotoRotated()
        {
            if (!saveRotation || shown == null || shown.IsVideo) return;
            rotationSaveTimer.Stop();
            rotationSaveTimer.Tick -= OnRotationSaveTimer;
            rotationSaveTimer.Tick += OnRotationSaveTimer;
            rotationSaveTimer.Start(); // waits a little, so pressing rotate 3 times saves once
        }

        void OnRotationSaveTimer(object sender, EventArgs e)
        {
            rotationSaveTimer.Stop();
            SaveRotationToFile();
        }

        // Writes the rotation into the photo. JPEGs: only the "orientation" tag changes, so there's no
        // quality loss. Other types are saved again, turned. The file's date stays the same, so it
        // keeps its place in the folder.
        void SaveRotationToFile()
        {
            var s = shown;
            int turn;
            if (s == null || s.IsVideo || s.Failed || !rotations.TryGetValue(s.File, out turn) || turn == 0) return;
            string file = s.File, ext = Path.GetExtension(file).ToLowerInvariant();
            try
            {
                var modified = File.GetLastWriteTime(file);
                bool done = (ext == ".jpg" || ext == ".jpeg" || ext == ".jfif" || ext == ".jpe") && TryTurnJpegTag(file, turn);
                if (!done) TurnPixels(file, ext, turn);
                File.SetLastWriteTime(file, modified);

                rotations.Remove(file);
                s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                s.Rotation.Angle = 0;
                int fullWidth, fullHeight;
                var picture = DecodeImage(file, Math.Min(ScreenSide, MaxDecodeSide), out fullWidth, out fullHeight);
                s.Picture.Source = picture;
                s.FullWidth = fullWidth;
                s.FullHeight = fullHeight;
                s.Reduced = Math.Max(picture.PixelWidth, picture.PixelHeight) < Math.Min(MaxDecodeSide, Math.Max(fullWidth, fullHeight));
                RefreshThumbnail(file);
                UpdateZoomUi();
                UpdateFacts();
                ShowToast("Rotation saved to the file" + (done ? " (no quality lost)." : "."), null, null, 3, null);
            }
            catch (Exception ex)
            {
                ShowToast("Couldn't save the rotation: " + ex.Message, null, null, 6, null);
            }
        }

        static int OrientationDegrees(int o) { return o == 6 ? 90 : o == 3 ? 180 : o == 8 ? 270 : 0; }
        static int OrientationTag(int degrees) { return degrees == 90 ? 6 : degrees == 180 ? 3 : degrees == 270 ? 8 : 1; }

        static bool TryTurnJpegTag(string file, int turn)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                var meta = frame.Metadata as BitmapMetadata;
                object current = meta == null ? null : meta.GetQuery("/app1/ifd/{ushort=274}");
                if (current == null) return false; // no orientation tag to change in place
                int o = Convert.ToInt32(current);
                if (o != 1 && o != 3 && o != 6 && o != 8) return false; // mirrored photos: save pixels instead
                var writer = frame.CreateInPlaceBitmapMetadataWriter();
                writer.SetQuery("/app1/ifd/{ushort=274}", (ushort)OrientationTag((OrientationDegrees(o) + turn) % 360));
                return writer.TrySave();
            }
        }

        static void TurnPixels(string file, string ext, int turn)
        {
            BitmapEncoder encoder;
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".jfif": case ".jpe": encoder = new JpegBitmapEncoder { QualityLevel = 95 }; break;
                case ".png": encoder = new PngBitmapEncoder(); break;
                case ".bmp": encoder = new BmpBitmapEncoder(); break;
                case ".tif": case ".tiff": encoder = new TiffBitmapEncoder(); break;
                default: throw new Exception("this file type can't be saved by Windows. The rotation still shows in FastViewer.");
            }
            BitmapSource turned;
            BitmapMetadata meta = null;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                int o = 1;
                try
                {
                    var m = frame.Metadata as BitmapMetadata;
                    object q = m == null ? null : m.GetQuery("System.Photo.Orientation");
                    if (q != null) o = Convert.ToInt32(q);
                    if (m != null && encoder is JpegBitmapEncoder)
                    {
                        meta = m.Clone();
                        try { meta.SetQuery("System.Photo.Orientation", (ushort)1); } catch { }
                    }
                }
                catch { }
                turned = new TransformedBitmap(frame, new RotateTransform((OrientationDegrees(o) + turn) % 360));
                turned = new CachedBitmap(turned, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            encoder.Frames.Add(BitmapFrame.Create(turned, null, meta, null));
            string temp = file + ".fastviewer-tmp";
            using (var fs = File.Create(temp)) encoder.Save(fs);
            File.Copy(temp, file, true);
            File.Delete(temp);
        }

        bool CanSaveRotation()
        {
            int turn;
            return shown != null && !shown.IsVideo && !shown.Failed && rotations.TryGetValue(shown.File, out turn) && turn != 0;
        }

        static System.Windows.Controls.MenuItem Enabled(System.Windows.Controls.MenuItem item, bool on)
        {
            item.IsEnabled = on;
            return item;
        }

        // ---------------------------------------------------------------- startup speed-up

        // Lets Windows translate FastViewer to machine code ahead of time (NGen), so it doesn't
        // have to do that each time it starts. Needs administrator approval once.
        void SpeedUpStartup()
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string ngen = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                       @"Microsoft.NET\Framework64\v4.0.30319\ngen.exe");
            try
            {
                var p = Process.Start(new ProcessStartInfo(ngen, "install \"" + exe + "\" /nologo")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                ShowToast("Speeding up... (takes about half a minute)", null, null, 0, "ngen");
                var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                watch.Tick += delegate
                {
                    if (!p.HasExited) return;
                    watch.Stop();
                    ShowToast(p.ExitCode == 0 ? "Done. FastViewer will start faster from now on." :
                              "That didn't work (code " + p.ExitCode + ").", null, null, 6, null);
                };
                watch.Start();
            }
            catch (Win32Exception)
            {
                ShowToast("Cancelled: Windows needs your OK as administrator for this.", null, null, 5, null);
            }
        }
    }
}
