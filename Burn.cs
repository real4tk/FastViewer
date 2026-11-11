// FastViewer: captions burned into the picture (1.25.0). Discord (and most chat apps) ignore subtitle files,
// so the text has to be part of the video itself. "Burn in captions" in Send to Discord and "Save a copy with
// captions" in the video "..." menu draw each line onto the pictures while the copy is made (Convert.cs hands
// every finished picture to CaptionPainter): the same white text on a dark box as FastViewer's own subtitles,
// in the look's font, readable on any video. Everything else about the copy is as usual: every sound track
// is kept, and it's all Windows' own video tools (no FFmpeg).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    class CaptionPainter
    {
        readonly List<Cue> cues;
        readonly double start, size, radius; // start: where in the video the copy begins (seconds); size: of the picture's height
        readonly FontFamily font;
        int current = -2, bw, bh;
        int[] box;                           // the line on screen, drawn once: its pixels (colour already multiplied by see-through)
        public int Painted, Lines;           // pictures that got text; different lines drawn (tests, and to tell when nothing fell inside the part)

        public CaptionPainter(IEnumerable<Cue> cues, double startSeconds, double size, FontFamily font, double radius)
        {
            this.cues = cues.OrderBy(c => c.From).ToList();
            start = startSeconds;
            this.size = size;
            this.font = font;
            this.radius = radius;
        }

        public void Paint(int[] px, int w, int h, long time)
        {
            double at = start + time / 1e7;
            // The last line that has started; it shows until its own end (as on screen, Subtitles.cs SubTick).
            int lo = 0, hi = cues.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cues[mid].From <= at) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found >= 0 && cues[found].To <= at) found = -1;
            if (found != current)
            {
                current = found;
                box = null;
                if (found >= 0) { Draw(cues[found].Text, w, h); Lines++; }
            }
            if (box == null) return;
            int x0 = Math.Max(0, (w - bw) / 2), y0 = Math.Max(0, h - bh - Math.Max(4, (int)(h * 0.045)));
            int cols = Math.Min(bw, w - x0), rows = Math.Min(bh, h - y0);
            for (int y = 0; y < rows; y++)
            {
                int s = y * bw, d = (y0 + y) * w + x0;
                for (int x = 0; x < cols; x++, s++, d++)
                {
                    int c = box[s], a = (int)((uint)c >> 24);
                    if (a == 0) continue;
                    if (a == 255) { px[d] = c; continue; }
                    int under = px[d], rest = 255 - a;
                    int r = ((c >> 16) & 255) + (((under >> 16) & 255) * rest + 127) / 255;
                    int g = ((c >> 8) & 255) + (((under >> 8) & 255) * rest + 127) / 255;
                    int b = (c & 255) + ((under & 255) * rest + 127) / 255;
                    px[d] = unchecked((int)0xFF000000) | (Math.Min(255, r) << 16) | (Math.Min(255, g) << 8) | Math.Min(255, b);
                }
            }
            Painted++;
        }

        // One line as a small picture, made with the same text engine the window uses (so every language and
        // emoji Windows can show comes out right). That engine wants a thread of its own kind.
        void Draw(string text, int w, int h)
        {
            var thread = new Thread(delegate()
            {
                try
                {
                    double f = Math.Max(12, size * Math.Min(h, w * 1.1)); // an upright phone video gets text sized for its width
                    double padX = f * 0.42, padTop = f * 0.14, padBottom = f * 0.22;
                    double room = Math.Max(40, w * 0.88 - 2 * padX);
                    var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), f, Brushes.White, 1.0);
                    ft.TextAlignment = TextAlignment.Center;
                    ft.MaxTextWidth = room;
                    ft.MaxLineCount = 4;
                    ft.Trimming = TextTrimming.WordEllipsis;
                    int width = Math.Min(w, (int)Math.Ceiling(ft.Width + 2 * padX)), height = Math.Min(h, (int)Math.Ceiling(ft.Height + padTop + padBottom));
                    if (width < 2 || height < 2) return;
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        double r = Math.Min(Math.Min(width, height) / 2.0, radius * f / 22);
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), null, new Rect(0, 0, width, height), r, r);
                        dc.DrawText(ft, new Point((width - room) / 2, padTop)); // (centred text sits in the middle of its room)
                    }
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var pixels = new int[width * height];
                    bitmap.CopyPixels(pixels, width * 4, 0);
                    bw = width;
                    bh = height;
                    box = pixels;
                }
                catch { box = null; } // a line that can't be drawn is left out; the copy still gets made
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
        }
    }

    partial class ViewerWindow
    {
        bool discordBurn;                  // "Burn in captions" in Send to Discord (settings line discordBurn=)
        internal CaptionPainter lastPainter; // tests

        // The subtitles on screen right now, ready to be drawn into a copy that starts at `from`.
        CaptionPainter MakePainter(string video, TimeSpan? from)
        {
            if (!HasSubs || cuesFor != video) return null;
            var font = TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
            object corner = TryFindResource("Radius.Control");
            double radius = corner is CornerRadius ? ((CornerRadius)corner).TopLeft : 4;
            return lastPainter = new CaptionPainter(cues.ToList(), from.HasValue ? from.Value.TotalSeconds : 0,
                SubSizes[Math.Max(0, Math.Min(2, subSize))], font, radius);
        }

        void SaveCopyWithCaptions()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed) return;
            if (!HasSubs)
            {
                ShowToast("This video has no captions or subtitles yet.", "Make captions", delegate { HideToast(null); MakeCaptions(); }, 6, "info");
                return;
            }
            if (converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            if (playing) TogglePlay();
            string target = AskSaveVideo(s.File, " (captions)");
            if (target == null) return;
            var painter = MakePainter(s.File, null);
            if (painter == null) return;
            RunConversion(s.File, target, null, null, "Saving a copy with captions", new ConvertOptions { Paint = painter.Paint });
        }

    }
}
