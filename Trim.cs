// FastViewer: trimming a video. A filmstrip of the video with a handle at each end of the part to keep:
// drag a handle (the video shows that frame), click the strip to look at a moment, play just the part,
// then save it as a new video (Playback.cs, SaveTrim). Keys: left/right move a handle by a frame (Shift: a
// second), Tab picks the other handle, [ and ] put a handle at the current moment, Space plays the part,
// Enter saves, Esc leaves.

using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        const double TrimHandle = 14;      // handle width; the filmstrip runs between the two handles' outer edges
        const double TrimMinSeconds = 0.2; // the shortest clip
        const string TrimHintText = "Drag the handles to choose the part to keep";
        char trimDrag;          // what the mouse is dragging: 'A' (start), 'B' (end), 'P' (the playhead) or nothing
        char trimActive = 'A';  // the handle the arrow keys move
        double trimGrab;        // where on the handle the mouse took hold
        double trimHeadAt;      // where the playhead is drawn while dragging (the video catches up a moment later)
        DateTime trimSeekAt;
        CancelFlag filmCancel;
        string filmKey;         // the last filmstrip (file + rotation), so trimming the same video again is instant
        BitmapSource[] filmFrames;

        void InitTrim()
        {
            OnClick("TrimPlayBtn", PlaySelection);
            OnClick("TrimSaveBtn", SaveTrim);
            OnClick("TrimCancelBtn", EndTrim);
            var bar = F<FrameworkElement>("TrimBar");
            bar.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; }; // not a click on the video
            bar.MouseRightButtonUp += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; };
            var track = F<FrameworkElement>("TrimTrack");
            track.MouseLeftButtonDown += TrimMouseDown;
            track.MouseMove += delegate(object o, MouseEventArgs e) { if (trimDrag != '\0' && track.IsMouseCaptured) TrimDragTo(e.GetPosition(track).X); };
            track.MouseLeftButtonUp += delegate
            {
                if (trimDrag == '\0') return;
                trimDrag = '\0';
                track.ReleaseMouseCapture();
                TrimSeek(trimHeadAt, true); // the exact frame where the mouse let go
                PlaceTrim();
            };
            track.LostMouseCapture += delegate { trimDrag = '\0'; };
            track.SizeChanged += delegate { PlaceTrim(); };
            foreach (var name in new[] { "TrimHandleA", "TrimHandleB" })
            {
                char which = name[name.Length - 1];
                F<FrameworkElement>(name).GotKeyboardFocus += delegate { trimActive = which; PlaceTrim(); };
            }
        }

        double TrimLength
        {
            get
            {
                var s = shown;
                return s != null && s.IsVideo && s.Video.NaturalDuration.HasTimeSpan ? s.Video.NaturalDuration.TimeSpan.TotalSeconds : 0;
            }
        }

        double TrimShortest { get { return Math.Min(TrimLength, Math.Max(TrimMinSeconds, shown != null ? shown.FrameSeconds : 0)); } }

        double TrimX(double seconds)
        {
            double usable = F<FrameworkElement>("TrimTrack").ActualWidth - 2 * TrimHandle;
            return TrimHandle + usable * seconds / Math.Max(0.001, TrimLength);
        }

        double TrimTime(double x)
        {
            double usable = Math.Max(1, F<FrameworkElement>("TrimTrack").ActualWidth - 2 * TrimHandle);
            return Math.Max(0, Math.Min(TrimLength, (x - TrimHandle) / usable * TrimLength));
        }

        // 1:05.3 (a tenth of a second is about three frames: precise enough to see where a handle is)
        static string FmtExact(TimeSpan t)
        {
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
        }

        static string LengthText(TimeSpan t)
        {
            return t.TotalSeconds < 60 ? t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" : Fmt(t);
        }

        void StartTrim()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed || !s.Video.NaturalDuration.HasTimeSpan || trimming) return;
            if (mini) ToggleMini();
            if (playing) TogglePlay();
            trimming = true;
            trimActive = 'A';
            trimDrag = '\0';
            HideToast(null);
            loopA = TimeSpan.Zero;
            loopB = s.Video.NaturalDuration.TimeSpan;
            loopTimer.Start();
            UpdateLoopUi();
            UpdateControls(); // the control bar makes way for the trim bar
            F<TextBlock>("TrimHint").Text = TrimHintText;
            var bar = F<FrameworkElement>("TrimBar");
            SizeTrimBar();
            Motion.Show(bar, 0, 12);
            bar.UpdateLayout();
            LoadFilmstrip(s);
            PlaceTrim();
            ShowOverlay();
        }

        void EndTrim()
        {
            if (!trimming) return;
            trimming = false;
            trimDrag = '\0';
            if (filmCancel != null) filmCancel.Cancelled = true;
            if (filmFrames != null && filmFrames.Any(f => f == null)) filmKey = null; // not finished: start over next time
            Motion.Hide(F<FrameworkElement>("TrimBar"), 0, 8, 1, 150);
            ClearLoop();
            UpdateControls();
            Glide(transport, 0, 8);
        }

        // As wide as the window allows; a narrow one keeps the buttons and drops the words.
        void SizeTrimBar()
        {
            var bar = F<FrameworkElement>("TrimBar");
            bar.Width = Math.Max(260, Math.Min(900, stage.ActualWidth - 32));
            bool narrow = bar.Width < 720;
            F<FrameworkElement>("TrimHint").Visibility = bar.Width < 880 ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("TrimPlayText").Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("TrimPlayIcon").Margin = new Thickness(0, 0, narrow ? 0 : 8, 0);
            F<TextBlock>("TrimGifText").Text = narrow ? "GIF" : "Make GIF";
            F<FrameworkElement>("TrimShareText").Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("TrimShareIcon").Margin = new Thickness(0, 0, narrow ? 0 : 8, 0);
            F<FrameworkElement>("TrimIcon").Visibility = bar.Width < 440 ? Visibility.Collapsed : Visibility.Visible;
        }

        // Puts the handles, the dimmed parts, the playhead and the times where they belong.
        void PlaceTrim()
        {
            if (!trimming || !loopA.HasValue || !loopB.HasValue || shown == null) return;
            var track = F<FrameworkElement>("TrimTrack");
            double width = track.ActualWidth;
            if (TrimLength <= 0 || width < 4 * TrimHandle) return;
            double a = TrimX(loopA.Value.TotalSeconds), b = TrimX(loopB.Value.TotalSeconds);
            var handleA = F<FrameworkElement>("TrimHandleA");
            var handleB = F<FrameworkElement>("TrimHandleB");
            Canvas.SetLeft(handleA, a - TrimHandle);
            Canvas.SetLeft(handleB, b);
            var sel = F<FrameworkElement>("TrimSel");
            Canvas.SetLeft(sel, a);
            sel.Width = Math.Max(0, b - a);
            var dimLeft = F<FrameworkElement>("TrimDimLeft");
            Canvas.SetLeft(dimLeft, TrimHandle);
            dimLeft.Width = Math.Max(0, a - TrimHandle);
            var dimRight = F<FrameworkElement>("TrimDimRight");
            Canvas.SetLeft(dimRight, b);
            dimRight.Width = Math.Max(0, width - TrimHandle - b);
            // The handle the arrow keys move has the longer grip.
            ((FrameworkElement)((Border)handleA).Child).Height = trimActive == 'A' ? 26 : 16;
            ((FrameworkElement)((Border)handleB).Child).Height = trimActive == 'B' ? 26 : 16;
            PlaceTrimHead();

            // Times under the handles; they make room for each other when the handles are close.
            var from = F<TextBlock>("TrimFromText");
            var to = F<TextBlock>("TrimToText");
            from.Text = FmtExact(loopA.Value);
            to.Text = FmtExact(loopB.Value);
            from.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            to.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double fw = from.DesiredSize.Width, tw = to.DesiredSize.Width;
            double fx = Math.Max(0, Math.Min(width - fw, a - TrimHandle / 2 - fw / 2));
            double tx = Math.Max(0, Math.Min(width - tw, b + TrimHandle / 2 - tw / 2));
            if (fx + fw + 10 > tx)
            {
                double middle = (fx + fw + tx) / 2;
                fx = Math.Max(0, middle - 5 - fw);
                tx = Math.Min(width - tw, Math.Max(fx + fw + 10, middle + 5));
            }
            Canvas.SetLeft(from, fx);
            Canvas.SetLeft(to, tx);

            var length = loopB.Value - loopA.Value;
            F<TextBlock>("TrimText").Text = "Clip: " + LengthText(length) + (length.TotalSeconds >= TrimLength - 0.05 ? " (the whole video)" : "");
            AutomationProperties.SetName(handleA, "Start of the clip, " + FmtExact(loopA.Value));
            AutomationProperties.SetName(handleB, "End of the clip, " + FmtExact(loopB.Value));
        }

        // The playhead and the play button (also on every tick of the loop timer while trimming).
        void PlaceTrimHead()
        {
            var s = shown;
            if (!trimming || s == null || TrimLength <= 0) return;
            double at = trimDrag != '\0' ? trimHeadAt : s.Video.Position.TotalSeconds;
            Canvas.SetLeft(F<FrameworkElement>("TrimHead"), TrimX(Math.Min(at, TrimLength)) - 2);
            // While playing, the playhead moves every screen frame (the loop timer alone would be ~66 a second).
            if (playing && !Motion.Running("trimHead")) Motion.Run("trimHead", delegate { PlaceTrimHead(); return trimming && playing; });
            var icon = F<Glyph>("TrimPlayIcon");
            string code = playing ? "" : "";
            if (icon.Code != code)
            {
                icon.Code = code;
                F<TextBlock>("TrimPlayText").Text = playing ? "Pause" : "Play selection";
            }
        }

        void TrimMouseDown(object sender, MouseButtonEventArgs e)
        {
            var track = (FrameworkElement)sender;
            double x = e.GetPosition(track).X;
            var src = e.OriginalSource as DependencyObject;
            if (Within(src, F<FrameworkElement>("TrimHandleA"))) { trimDrag = 'A'; trimGrab = x - TrimX(loopA.Value.TotalSeconds); }
            else if (Within(src, F<FrameworkElement>("TrimHandleB"))) { trimDrag = 'B'; trimGrab = x - TrimX(loopB.Value.TotalSeconds); }
            else { trimDrag = 'P'; trimGrab = 0; }
            if (trimDrag != 'P')
            {
                trimActive = trimDrag;
                if (playing) TogglePlay(); // a handle shows its own frame
            }
            track.CaptureMouse();
            TrimDragTo(x);
            e.Handled = true;
        }

        void TrimDragTo(double x)
        {
            if (!loopA.HasValue || !loopB.HasValue) return;
            double t = TrimTime(x - trimGrab);
            if (trimDrag == 'A' || trimDrag == 'B') SetTrimEnd(trimDrag, t, false);
            else
            {
                t = Math.Max(loopA.Value.TotalSeconds, Math.Min(loopB.Value.TotalSeconds, t)); // only inside the part to keep
                TrimSeek(t, false);
                PlaceTrimHead();
            }
        }

        // Moves a handle (it can't pass the other one) and shows its frame.
        void SetTrimEnd(char which, double t, bool exact)
        {
            if (!loopA.HasValue || !loopB.HasValue) return;
            double shortest = TrimShortest;
            if (which == 'A') t = Math.Max(0, Math.Min(t, loopB.Value.TotalSeconds - shortest));
            else t = Math.Min(TrimLength, Math.Max(t, loopA.Value.TotalSeconds + shortest));
            if (which == 'A') loopA = TimeSpan.FromSeconds(t);
            else loopB = TimeSpan.FromSeconds(t);
            trimActive = which;
            if (F<TextBlock>("TrimHint").Text != TrimHintText) KeyHint();
            TrimSeek(t, exact);
            UpdateLoopUi();
            PlaceTrim();
        }

        // Shows the frame at t. While dragging, at most every 40 ms: the decoder can't keep up with every mouse move.
        void TrimSeek(double t, bool exact)
        {
            var s = shown;
            if (s == null || !s.IsVideo) return;
            trimHeadAt = t;
            if (!exact && (DateTime.UtcNow - trimSeekAt).TotalMilliseconds < 40) return;
            trimSeekAt = DateTime.UtcNow;
            s.Poster.Visibility = Visibility.Collapsed;
            s.Video.Position = TimeSpan.FromSeconds(t);
            UpdateTime();
        }

        void NudgeTrim(int direction, bool bySecond)
        {
            var s = shown;
            if (s == null || !loopA.HasValue || !loopB.HasValue) return;
            if (playing) TogglePlay();
            double step = bySecond ? 1 : Math.Max(0.01, s.FrameSeconds);
            double t = (trimActive == 'A' ? loopA.Value : loopB.Value).TotalSeconds + direction * step;
            SetTrimEnd(trimActive, t, true);
            KeyHint();
        }

        // After a key: says which handle the arrows move.
        void KeyHint()
        {
            F<TextBlock>("TrimHint").Text = "Moving the " + (trimActive == 'A' ? "start" : "end") + ": arrows move a frame, Shift + arrows a second, Tab the other end";
        }

        // Plays the part between the handles (the loop timer keeps it there); again pauses.
        void PlaySelection()
        {
            var s = shown;
            if (s == null || !trimming || !loopA.HasValue || !loopB.HasValue) return;
            if (!playing)
            {
                var at = s.Video.Position;
                if (at < loopA.Value || at >= loopB.Value - TimeSpan.FromMilliseconds(100)) s.Video.Position = loopA.Value;
            }
            TogglePlay();
            PlaceTrimHead();
        }

        // Keys while trimming (before the normal shortcuts). Left/right would otherwise go to another file.
        bool HandleTrimKey(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (key)
            {
                case Key.Escape: EndTrim(); return true;
                case Key.Enter: SaveTrim(); return true;
                case Key.Left: NudgeTrim(-1, shift); return true;
                case Key.Right: NudgeTrim(1, shift); return true;
                case Key.Tab:
                    trimActive = trimActive == 'A' ? 'B' : 'A';
                    PlaceTrim();
                        KeyHint();
                    return true;
            }
            Shortcut sc;
            if (!shortcutByKey.TryGetValue(Code(key, Keyboard.Modifiers), out sc)) return false;
            var s = shown;
            switch (sc.Id)
            {
                case "play": PlaySelection(); return true;
                case "loopA": if (s != null) SetTrimEnd('A', s.Video.Position.TotalSeconds, true); return true;
                case "loopB": if (s != null) SetTrimEnd('B', s.Video.Position.TotalSeconds, true); return true;
                case "loopStop": case "previous": case "next": case "first": case "last": return true; // stay with this video
                case "trim": EndTrim(); return true; // the trim key again leaves, like G for the grid
            }
            return false;
        }

        // Small pictures from across the video, made in the background; each fades in as it's ready.
        void LoadFilmstrip(Slot s)
        {
            var box = F<UniformGrid>("TrimFrames");
            box.Children.Clear();
            int turn;
            rotations.TryGetValue(s.File, out turn);
            string key = s.File + "|" + turn;
            double vw = s.Video.NaturalVideoWidth, vh = s.Video.NaturalVideoHeight;
            double aspect = vw > 0 && vh > 0 ? (turn == 90 || turn == 270 ? vh / vw : vw / vh) : 16 / 9.0;
            double room = Math.Max(120, F<FrameworkElement>("TrimTrack").ActualWidth - 2 * TrimHandle);
            int count = Math.Max(3, Math.Min(30, (int)Math.Ceiling(room / (48 * aspect))));
            bool cached = key == filmKey && filmFrames != null;
            if (cached) count = filmFrames.Length;
            box.Columns = count;
            var images = new Image[count];
            for (int i = 0; i < count; i++)
            {
                images[i] = new Image { Stretch = Stretch.UniformToFill, Opacity = cached ? 1 : 0 };
                if (cached) images[i].Source = filmFrames[i];
                box.Children.Add(images[i]);
            }
            if (cached) return;
            if (filmCancel != null) filmCancel.Cancelled = true;
            var cancel = filmCancel = new CancelFlag();
            var frames = filmFrames = new BitmapSource[count];
            filmKey = key;
            int pixels = (int)Math.Ceiling(48 * Dpi());
            string file = s.File;
            var thread = new Thread(delegate()
            {
                try
                {
                    VideoConverter.Filmstrip(file, count, pixels, turn, delegate(int i, int w, int h, int[] px)
                    {
                        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                        bmp.Freeze();
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            frames[i] = bmp;
                            if (cancel.Cancelled) return;
                            images[i].Source = bmp;
                            Motion.FromTo(images[i], OpacityProperty, 0, 1, Motion.Ms(Motion.Normal, false), Motion.Smooth);
                        }));
                    }, cancel);
                }
                catch { } // no pictures: the strip stays plain, trimming still works
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Filmstrip" };
            thread.Start();
        }
    }
}
