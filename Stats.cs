// FastViewer 1.30.0 round 3: playback stats. Ctrl+Shift+P (Settings > Keyboard: "Playback stats") shows, top left, once a
// second: who draws the picture, the video's own frame rate, how many screen frames WPF drew, how many video pictures
// FastViewer showed, how many it passed over unseen (they came too late for their moment) and how many it showed late, and
// how long showing one took. While it is on, each second is also written to %LOCALAPPDATA%\FastViewer\playback.txt with
// the places of skipped or late pictures, so a stutter can be looked at afterwards.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool statsOn;
        Border statsBox;
        TextBlock statsText;
        DispatcherTimer statsTimer;
        long statsSince;
        int statsTicks, statsShown, statsSkipped, statsLate;
        readonly List<string> statsPlaces = new List<string>();
        // (Tests and the log: the last second's numbers.)
        internal string StatsLine = "";
        internal int LastShownPerSecond, LastTicksPerSecond, LastSkipped, LastLate;
        bool statsOwnPlaying;
        OwnVideo statsOwn; int statsRefr0, statsPres0, statsSkip0, statsLate0; // (1.31.0: the own player's counters a second ago)

        void ToggleStats()
        {
            statsOn = !statsOn;
            if (statsBox == null)
            {
                statsText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap };
                statsBox = new Border
                {
                    Child = statsText,
                    Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(12, 12, 0, 0),
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(statsBox, 1000);
                Grid.SetRow(statsBox, 1); // (1.30.0 round 4: in the video row; row 0 is the 48 px top bar, where the box was cut off)
                root.Children.Add(statsBox);
                statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                statsTimer.Tick += delegate { StatsSecond(); };
            }
            statsBox.Visibility = statsOn ? Visibility.Visible : Visibility.Collapsed;
            if (statsOn)
            {
                ResetStatsCounters();
                CompositionTarget.Rendering += StatsTick;
                statsTimer.Start();
                statsText.Text = "Playback stats: counting...";
            }
            else
            {
                CompositionTarget.Rendering -= StatsTick;
                statsTimer.Stop();
            }
            ShowToast("Playback stats " + (statsOn ? "on" : "off"), null, null, 2, "stats");
        }

        void ResetStatsCounters()
        {
            statsSince = Stopwatch.GetTimestamp();
            statsTicks = statsShown = statsSkipped = statsLate = 0;
            lock (statsPlaces) statsPlaces.Clear();
        }

        void StatsTick(object sender, EventArgs e) { statsTicks++; }

        void StatsSecond()
        {
            double secs = Math.Max(0.001, (Stopwatch.GetTimestamp() - statsSince) / (double)Stopwatch.Frequency);
            var s = shown;
            string who = s == null || !s.IsVideo ? "nothing playing" : "Windows' player";
            double fps = s != null && s.IsVideo && s.FrameSeconds > 0 ? 1 / s.FrameSeconds : 0;
            LastTicksPerSecond = (int)Math.Round(statsTicks / secs);
            LastShownPerSecond = (int)Math.Round(statsShown / secs);
            LastSkipped = statsSkipped;
            LastLate = statsLate;
            bool own = s != null && s.IsVideo && s.Video.IsOwn;
            if (own)
            {
                // (1.31.0: FastViewer's own player: the screen's own refreshes, counted by the thread that shows the pictures,
                // and the pictures it really handed to the screen; WPF's frames don't matter for the video any more.)
                var v = s.Video;
                int refr = Presenter.Refreshes, pres = v.Presents, skip = v.Skipped, late = v.Late;
                if (statsOwn == v)
                {
                    LastTicksPerSecond = (int)Math.Round((refr - statsRefr0) / secs);
                    LastShownPerSecond = (int)Math.Round((pres - statsPres0) / secs);
                    statsSkipped = skip - statsSkip0;
                    statsLate = late - statsLate0;
                }
                bool sameRun = statsOwn == v && statsOwnPlaying == playing;
                statsOwn = v; statsRefr0 = refr; statsPres0 = pres; statsSkip0 = skip; statsLate0 = late;
                // (1.31.0 fix: paused, a song, or a second in which play/pause was pressed: no rates, they would only be half a
                // second's worth; the screen thread rests while paused.)
                string still = !v.HasVideo ? "song: no pictures to show" : !playing ? "paused" : !sameRun ? "counting..." : null;
                statsOwnPlaying = playing;
                if (still != null)
                {
                    StatsLine = "FastViewer's player | " + still;
                    if (statsText != null) statsText.Text = "FastViewer's player (graphics card)\n" + still;
                    ResetStatsCounters();
                    return;
                }
                double mfps = v.MeasuredFps;
                if (mfps > 0) fps = mfps;
                LastSkipped = statsSkipped;
                LastLate = statsLate;
                double can = Math.Min(fps * speed, LastTicksPerSecond);
                StatsLine = "FastViewer's player | video " + fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps at " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x | screen " + LastTicksPerSecond + "/s | shown " +
                    LastShownPerSecond + "/s (of " + can.ToString("0") + " possible) | skipped " + statsSkipped + " | late " + statsLate + " | refresh " + (Presenter.Period * 1000).ToString("0.00", CultureInfo.InvariantCulture) + " ms";
                if (statsText != null)
                    statsText.Text = "FastViewer's player (graphics card)\nvideo " + fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps at " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x\nscreen frames " + LastTicksPerSecond + "/s\n" +
                        "pictures shown " + LastShownPerSecond + "/s of " + can.ToString("0") + "\nskipped " + statsSkipped + "  late " + statsLate;
                if (statsOn && !testRun)
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer");
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, "playback.txt");
                        if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
                        File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss ") + Path.GetFileName(s.File) + " at " + s.Video.Position.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s: " + StatsLine + Environment.NewLine);
                    }
                    catch { }
                ResetStatsCounters();
                return;
            }
            statsOwn = null;
            string places;
            lock (statsPlaces) places = string.Join(", ", statsPlaces);
            StatsLine = who + " | video " + fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps at " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x | screen " + LastTicksPerSecond + "/s | shown " +
                "- (Windows' player doesn't tell)";
            if (statsText != null)
                statsText.Text = who + "\nvideo " + fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps at " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x\nscreen frames " + LastTicksPerSecond + "/s\n" +
                    "pictures shown (Windows' player doesn't tell)";
            if (statsOn && !testRun)
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, "playback.txt");
                    if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
                    File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss ") + (s != null && s.File != null ? Path.GetFileName(s.File) + " at " + VideoAt(s).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s: " : "") + StatsLine + (places.Length > 0 ? " | " + places : "") + Environment.NewLine);
                }
                catch { }
            ResetStatsCounters();
        }
    }
}
