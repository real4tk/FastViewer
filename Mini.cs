// FastViewer: the mini player (just the video, window shaped like it, always on top) and "Keep on top".

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace FastViewer
{
    partial class ViewerWindow
    {
        const double NormalMinWidth = 380, NormalMinHeight = 260;
        bool mini, pinned, fittingMini;
        bool miniVolume;     // Settings > Buttons: a volume button in the mini player (off by default)
        double miniLong = 480;                                // longest side of the mini player
        double miniLeft = double.NaN, miniTop = double.NaN;   // where you last left it
        double miniAspect;
        Rect boundsBeforeMini;
        WindowState stateBeforeMini;

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

        void TogglePin()
        {
            pinned = !pinned;
            Topmost = pinned || mini;
            SaveSettings();
            ShowToast(pinned ? "Keep on top: on" : "Keep on top: off", null, null, 1.5, "pin");
        }

        // Mini player: no bars, always on top, window shaped like the video. Controls appear on hover;
        // drag the video to move it, drag the edges to resize (the shape is kept).
        void ToggleMini()
        {
            if (!mini)
            {
                if (shown == null || shown.Failed || shown.IsAudio) return;
                if (trimming) EndTrim(); // the trim bar needs the full window
                if (fullscreen) ToggleFullscreen();
                if (settingsOpen) CloseSettings();
                stateBeforeMini = WindowState;
                boundsBeforeMini = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                mini = true;
                WindowState = WindowState.Normal;
                infoCol.Width = new GridLength(0);
                chrome.CaptionHeight = 0;
                stage.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "FullBg");
                cover.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "FullBg");
                MinWidth = 120;
                MinHeight = 68;
                Topmost = true;
                F<FrameworkElement>("MiniBar").Visibility = Visibility.Visible;
                FitMiniWindow(true);
            }
            else
            {
                mini = false;
                F<FrameworkElement>("MiniBar").Visibility = Visibility.Collapsed;
                chrome.CaptionHeight = 48;
                infoCol.Width = new GridLength(infoOpen ? 320 : 0);
                stage.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "StageBg");
                cover.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CoverBg");
                MinWidth = NormalMinWidth;
                MinHeight = NormalMinHeight;
                Topmost = pinned;
                Left = boundsBeforeMini.Left;
                Top = boundsBeforeMini.Top;
                Width = boundsBeforeMini.Width;
                Height = boundsBeforeMini.Height;
                WindowState = stateBeforeMini;
            }
            SetVolumeSlider(false);
            PlaceBars();
            ApplyWindowLook(); // the mini player is only see-through around a photo with see-through parts
            UpdateMiniUi();
            UpdateControls();
            UpdateResponsive();
            ShowOverlay();
        }

        // Sizes the mini player to the current video's shape (sideways if rotated).
        void FitMiniWindow(bool entering)
        {
            int w, h;
            DisplaySize(shown, out w, out h);
            if (w <= 0 || h <= 0) return;
            miniAspect = (double)w / h;
            double nw = w >= h ? miniLong : miniLong * miniAspect;
            double nh = w >= h ? miniLong / miniAspect : miniLong;

            double left, top;
            Rect area;
            if (entering && !double.IsNaN(miniLeft))
            {
                left = miniLeft; // where you left it last time
                top = miniTop;
                area = WorkAreaAt(left + nw / 2, top + nh / 2);
            }
            else
            {
                area = WorkAreaAt(Left + Width / 2, Top + Height / 2);
                if (entering)
                {
                    left = area.Right - nw - 16; // first time: bottom-right corner, like picture-in-picture
                    top = area.Bottom - nh - 16;
                }
                else
                {
                    // New shape: keep the corner nearest the screen edge in place and grow away from it.
                    bool right = Left + Width / 2 > area.Left + area.Width / 2;
                    bool bottom = Top + Height / 2 > area.Top + area.Height / 2;
                    left = right ? Left + Width - nw : Left;
                    top = bottom ? Top + Height - nh : Top;
                }
            }
            left = Math.Max(area.Left, Math.Min(left, area.Right - nw));
            top = Math.Max(area.Top, Math.Min(top, area.Bottom - nh));

            fittingMini = true;
            Left = left;
            Top = top;
            Width = nw;
            Height = nh;
            fittingMini = false;
            miniLeft = left;
            miniTop = top;
        }

        // Work area (screen minus taskbar) of the monitor at a point, in WPF units.
        Rect WorkAreaAt(double x, double y)
        {
            double d = Dpi();
            var mi = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            var p = new NativePoint { X = (int)(x * d), Y = (int)(y * d) };
            if (!GetMonitorInfo(MonitorFromPoint(p, 2), ref mi)) return SystemParameters.WorkArea;
            return new Rect(mi.Work.Left / d, mi.Work.Top / d, (mi.Work.Right - mi.Work.Left) / d, (mi.Work.Bottom - mi.Work.Top) / d);
        }

        void OnMiniResized()
        {
            UpdateMiniUi();
            if (mini && !fittingMini && IsLoaded) miniLong = Math.Max(ActualWidth, ActualHeight); // remember your size
        }

        void OnMiniMoved()
        {
            if (mini && !fittingMini && IsLoaded)
            {
                miniLeft = Left;
                miniTop = Top;
            }
        }

        // A small window or mini player gets a smaller play button (a small mini player also no control bar, see UpdateControls).
        void UpdateMiniUi()
        {
            if (centerPlayBtn == null) return;
            double k = stage.ActualWidth < 520 || stage.ActualHeight < 340 ? 0.6 : 1; // small windows and the mini player
            centerPlayBtn.LayoutTransform = new ScaleTransform(k, k);
            if (shown != null) UpdateControls();
        }

        // Keeps the mini player's shape while you drag its edges (WM_SIZING).
        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SIZING = 0x0214;
            if (msg != WM_SIZING || !mini || miniAspect <= 0) return IntPtr.Zero;
            var r = (NativeRect)Marshal.PtrToStructure(lParam, typeof(NativeRect));
            int edge = wParam.ToInt32(); // 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (edge == 3 || edge == 6)
                r.Right = r.Left + (int)Math.Round(h * miniAspect);  // dragging top/bottom: width follows
            else
            {
                h = (int)Math.Round(w / miniAspect);                // otherwise: height follows
                if (edge == 3 || edge == 4 || edge == 5) r.Top = r.Bottom - h;
                else r.Bottom = r.Top + h;
            }
            Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
    }
}
