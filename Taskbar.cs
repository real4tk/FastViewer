// FastViewer: buttons under the window's preview when you point at its taskbar icon (previous, play/pause, next),
// like media players have. Play/pause only shows for videos and music and follows the playing state.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace FastViewer
{
    partial class ViewerWindow
    {
        ThumbButtonInfo thumbPrev, thumbPlay, thumbNext;
        string thumbState; // what the buttons show now, so they're only touched when something changed
        bool thumbLight;   // light taskbar: dark icons

        void InitTaskbarButtons()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    thumbLight = v is int && (int)v == 1;
                }
            }
            catch { }
            thumbPrev = new ThumbButtonInfo { ImageSource = ThumbIcon(""), Description = "Previous" };
            thumbPlay = new ThumbButtonInfo { ImageSource = ThumbIcon(""), Description = "Play" };
            thumbNext = new ThumbButtonInfo { ImageSource = ThumbIcon(""), Description = "Next" };
            thumbPrev.Click += delegate { Go(-1); };
            thumbPlay.Click += delegate { TogglePlay(); };
            thumbNext.Click += delegate { Go(1); };
            TaskbarItemInfo = new TaskbarItemInfo();
            TaskbarItemInfo.ThumbButtonInfos.Add(thumbPrev);
            TaskbarItemInfo.ThumbButtonInfos.Add(thumbPlay);
            TaskbarItemInfo.ThumbButtonInfos.Add(thumbNext);
            UpdateTaskbarButtons();
        }

        // A Segoe MDL2 icon drawn at 32 x 32 (Windows shows it at 16 x 16 or bigger on high-DPI screens).
        ImageSource ThumbIcon(string glyph)
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe MDL2 Assets"), 26, thumbLight ? Brushes.Black : Brushes.White, 1);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
            var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        void UpdateTaskbarButtons()
        {
            if (thumbPlay == null) return;
            bool media = shown != null && shown.IsVideo && !shown.Failed && !gridOpen && !settingsOpen;
            bool several = files.Count > 1;
            string state = (media ? (playing ? "pause" : "play") : "none") + (several ? "+nav" : "");
            if (state == thumbState) return;
            thumbState = state;
            thumbPlay.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            if (media)
            {
                thumbPlay.ImageSource = ThumbIcon(playing ? "" : "");
                thumbPlay.Description = (playing ? "Pause" : "Play") + KeyHint("play");
            }
            thumbPrev.IsEnabled = thumbNext.IsEnabled = several;
            thumbPrev.Description = "Previous file" + KeyHint("previous");
            thumbNext.Description = "Next file" + KeyHint("next");
            if (snapDir != null) Log("taskbar buttons: " + state + " (play button " + thumbPlay.Description + ")");
        }

        string KeyHint(string id)
        {
            string key = KeyOf(id);
            return string.IsNullOrEmpty(key) ? "" : " (" + key + ")";
        }
    }
}
