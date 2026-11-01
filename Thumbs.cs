// FastViewer: thumbnails for the strip under the picture and the folder grid.
//
// They come from Windows' own thumbnail cache (the same pictures File Explorer shows), so nothing is
// decoded in full. One low-priority background thread makes them, and it waits while you're switching
// files, so preloading the next files always goes first.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    static class Thumbs
    {
        public class Job
        {
            public string File;
            public int Px;
            public Dispatcher Ui;
            public Action<BitmapSource> Done;
            public volatile bool Cancelled; // the tile moved on to another file: skip it
        }

        static readonly object gate = new object();
        static readonly List<Job> queue = new List<Job>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Thread worker;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long quietAt; // the worker waits until this time (ms on clock): files are being switched

        // Thumbnails kept in memory; the least recently used go first. A null picture means "Windows has none".
        const long MaxBytes = 64L << 20;
        class Entry { public string Key; public BitmapSource Image; public long Bytes; }
        static readonly Dictionary<string, LinkedListNode<Entry>> cache = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.OrdinalIgnoreCase);
        static readonly LinkedList<Entry> recent = new LinkedList<Entry>();
        static long cacheBytes;

        static string Key(string file, int px) { return px + "|" + file; }

        public static bool TryGet(string file, int px, out BitmapSource image)
        {
            lock (gate)
            {
                LinkedListNode<Entry> node;
                image = null;
                if (!cache.TryGetValue(Key(file, px), out node)) return false;
                recent.Remove(node);
                recent.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        public static Job Request(string file, int px, Dispatcher ui, Action<BitmapSource> done)
        {
            var job = new Job { File = file, Px = px, Ui = ui, Done = done };
            lock (gate)
            {
                queue.Add(job);
                if (worker == null)
                {
                    worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Thumbnails" };
                    worker.SetApartmentState(ApartmentState.STA); // the Windows shell needs this
                    worker.Start();
                }
            }
            wake.Set();
            return job;
        }

        // Called on every switch: thumbnails wait until switching has been quiet for a moment.
        public static void HoldOff(int ms)
        {
            Interlocked.Exchange(ref quietAt, clock.ElapsedMilliseconds + ms);
        }

        // The file changed (rotation saved into it): make its thumbnails again next time.
        public static void Forget(string file)
        {
            lock (gate)
            {
                var stale = new List<string>();
                foreach (var key in cache.Keys)
                    if (key.EndsWith("|" + file, StringComparison.OrdinalIgnoreCase)) stale.Add(key);
                foreach (var key in stale) Drop(cache[key]);
            }
        }

        public static void Clear()
        {
            lock (gate)
            {
                foreach (var j in queue) j.Cancelled = true;
                queue.Clear();
                cache.Clear();
                recent.Clear();
                cacheBytes = 0;
            }
        }

        public static int CachedCount { get { lock (gate) return cache.Count; } }

        static void Drop(LinkedListNode<Entry> node)
        {
            recent.Remove(node);
            cache.Remove(node.Value.Key);
            cacheBytes -= node.Value.Bytes;
        }

        static void Store(string file, int px, BitmapSource image)
        {
            lock (gate)
            {
                string key = Key(file, px);
                LinkedListNode<Entry> old;
                if (cache.TryGetValue(key, out old)) Drop(old);
                var e = new Entry { Key = key, Image = image, Bytes = image == null ? 64 : (long)image.PixelWidth * image.PixelHeight * 4 };
                cache[key] = recent.AddFirst(e);
                cacheBytes += e.Bytes;
                while (cacheBytes > MaxBytes && recent.Count > 1) Drop(recent.Last);
            }
        }

        static void Work()
        {
            while (true)
            {
                Job job = null;
                lock (gate)
                {
                    queue.RemoveAll(j => j.Cancelled);
                    if (queue.Count > 0)
                    {
                        job = queue[0];
                        queue.RemoveAt(0);
                    }
                }
                if (job == null)
                {
                    wake.WaitOne();
                    continue;
                }
                long wait;
                while ((wait = Interlocked.Read(ref quietAt) - clock.ElapsedMilliseconds) > 0) Thread.Sleep((int)Math.Min(wait, 100));
                if (job.Cancelled) continue;

                BitmapSource image;
                if (!TryGet(job.File, job.Px, out image)) // another tile may have asked for the same one
                {
                    image = Make(job.File, job.Px);
                    Store(job.File, job.Px, image);
                }
                var j2 = job;
                j2.Ui.BeginInvoke(DispatcherPriority.Background, new Action(delegate { if (!j2.Cancelled) j2.Done(image); }));
            }
        }

        // ---------------------------------------------------------------- Windows' thumbnails

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeSize { public int Width, Height; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);

        const int BiggerSizeOk = 0x1, ThumbnailOnly = 0x8, CropToSquare = 0x20;

        // A square thumbnail exactly px wide, or null when Windows has no thumbnail for the file
        // (for example a video whose codec isn't installed).
        static BitmapSource Make(string file, int px)
        {
            IShellItemImageFactory factory = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                if (SHCreateItemFromParsingName(file, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory) != 0 || factory == null) return null;
                if (factory.GetImage(new NativeSize { Width = px, Height = px }, BiggerSizeOk | ThumbnailOnly | CropToSquare, out bitmap) != 0 ||
                    bitmap == IntPtr.Zero) return null;
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                int w = src.PixelWidth, h = src.PixelHeight, side = Math.Min(w, h);
                if (side <= 0) return null;
                if (w != h) src = new CroppedBitmap(src, new Int32Rect((w - side) / 2, (h - side) / 2, side, side)); // middle square
                if (side != px) src = new TransformedBitmap(src, new ScaleTransform((double)px / side, (double)px / side));
                var result = new CachedBitmap(src, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                result.Freeze();
                return result;
            }
            catch { return null; }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }
    }

    // One square thumbnail: the picture with rounded corners, a ring for the current file, a play badge on videos.
    class Tile : Border
    {
        static readonly Brush Placeholder = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B));
        static readonly Brush HoverRing = Frozen(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
        static readonly Brush BadgeBack = Frozen(Color.FromArgb(0xA0, 0x1C, 0x1C, 0x1C));

        readonly Border picture = new Border { CornerRadius = new CornerRadius(4) };
        readonly ImageBrush brush = new ImageBrush { Stretch = Stretch.UniformToFill };
        readonly RotateTransform turn = new RotateTransform(0, 0.5, 0.5);
        readonly TextBlock glyph, play;
        readonly Border badge;
        bool selected;

        public int Index, Offset, Px;
        public string File;
        public Thumbs.Job Job;

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public Tile(double size)
        {
            BorderThickness = new Thickness(2);
            Padding = new Thickness(1);
            CornerRadius = new CornerRadius(7);
            Background = Brushes.Transparent; // the whole square is clickable
            brush.RelativeTransform = turn;
            picture.Background = Placeholder;
            glyph = new TextBlock
            {
                FontFamily = Ui.Icons, Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
            };
            play = new TextBlock
            {
                Text = "", FontFamily = Ui.Icons, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            badge = new Border
            {
                Background = BadgeBack, Child = play, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
            };
            var layers = new Grid();
            layers.Children.Add(glyph);
            layers.Children.Add(badge);
            picture.Child = layers;
            Child = picture;
            MouseEnter += delegate { UpdateRing(); };
            MouseLeave += delegate { UpdateRing(); };
            SetSize(size);
            Clear();
        }

        public void SetSize(double size)
        {
            if (Width == size) return;
            Width = Height = size;
            double b = Math.Max(16, Math.Min(26, Math.Round(size * 0.18)));
            badge.Width = badge.Height = b;
            badge.CornerRadius = new CornerRadius(b / 2);
            badge.Margin = new Thickness(Math.Round(b * 0.25));
            play.FontSize = Math.Round(b * 0.42);
            play.Margin = new Thickness(Math.Round(b * 0.06), 0, 0, 0); // the triangle looks centred a hair to the right
            glyph.FontSize = Math.Round(Math.Max(14, size * 0.22));
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                UpdateRing();
            }
        }

        void UpdateRing()
        {
            BorderBrush = selected ? (Brush)Application.Current.Resources["Accent"] : IsMouseOver ? HoverRing : Brushes.Transparent;
        }

        // Waiting for the thumbnail: a plain dark square (videos already get their badge).
        public void SetLoading(bool video)
        {
            picture.Background = Placeholder;
            glyph.Visibility = Visibility.Collapsed;
            badge.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            Visibility = Visibility.Visible;
        }

        public void SetPicture(BitmapSource image, bool video)
        {
            if (image == null)
            {
                picture.Background = Placeholder; // no thumbnail from Windows: show what kind of file it is
                glyph.Text = video ? "" : "";
                glyph.Visibility = Visibility.Visible;
            }
            else
            {
                brush.ImageSource = image;
                picture.Background = brush;
                glyph.Visibility = Visibility.Collapsed;
            }
            badge.Visibility = video && image != null ? Visibility.Visible : Visibility.Collapsed;
            Visibility = Visibility.Visible;
        }

        // Photos turned in FastViewer (not saved into the file) are turned here too.
        public void SetAngle(double angle)
        {
            if (turn.Angle != angle) turn.Angle = angle;
        }

        // An empty spot, or the tile goes back on the spare pile: forget the file and stop its thumbnail.
        public void Clear()
        {
            if (Job != null)
            {
                Job.Cancelled = true;
                Job = null;
            }
            File = null;
            brush.ImageSource = null;
            picture.Background = Placeholder;
            Visibility = Visibility.Hidden;
            selected = false;
            UpdateRing();
        }
    }
}
