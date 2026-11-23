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
        int statsTicks, statsShown, statsSkipped, statsLate, statsLockWorstUs;
        readonly List<string> statsPlaces = new List<string>();
        // (Tests and the log: the last second's numbers.)
        internal string StatsLine = "";
        internal int LastShownPerSecond, LastTicksPerSecond, LastSkipped, LastLate;

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
            statsTicks = statsShown = statsSkipped = statsLate = statsLockWorstUs = statsBusy = 0;
            frameCopyWorstUs = 0;
            lock (statsPlaces) statsPlaces.Clear();
        }

        void StatsTick(object sender, EventArgs e) { statsTicks++; }

        // From OnFrameTick (FramePath.cs): a picture is shown; `skipped` were passed over before it.
        void NoteFrameShown(VideoFrame f, int skipped, double skippedAt, double clock, double frameSeconds, double pace)
        {
            statsShown++;
            if (skipped > 0)
            {
                statsSkipped += skipped;
                if (statsOn) lock (statsPlaces) if (statsPlaces.Count < 12) statsPlaces.Add("skipped " + skipped + " at " + skippedAt.ToString("0.000", CultureInfo.InvariantCulture));
            }
            // Late: the picture shown belonged to a moment more than 1.5 pictures ago (the right one wasn't ready).
            if (clock - f.Clock > 1.5 * frameSeconds * Math.Max(1, pace))
            {
                statsLate++;
                if (statsOn) lock (statsPlaces) if (statsPlaces.Count < 12) statsPlaces.Add("late " + ((clock - f.Clock) * 1000).ToString("0") + " ms at " + f.FileTime.ToString("0.000", CultureInfo.InvariantCulture));
            }
        }

        void NoteLockWait(long from)
        {
            int us = (int)((Stopwatch.GetTimestamp() - from) * 1000000 / Stopwatch.Frequency);
            if (us > statsLockWorstUs) statsLockWorstUs = us;
        }

        void StatsSecond()
        {
            double secs = Math.Max(0.001, (Stopwatch.GetTimestamp() - statsSince) / (double)Stopwatch.Frequency);
            var s = shown;
            string who = s == null || !s.IsVideo ? "nothing playing" : frameVisible ? (frameSource == frameD3D ? "FastViewer (graphics card)" : "FastViewer (through memory)") : "Windows' player";
            double fps = s != null && s.IsVideo && s.FrameSeconds > 0 ? 1 / s.FrameSeconds : 0;
            if (frameReader != null && frameReader.MeasuredFps > 0) fps = frameReader.MeasuredFps; // (measured from the pictures' times)
            else if (frameReader != null && frameReader.FrameSeconds > 0) fps = 1 / frameReader.FrameSeconds;
            LastTicksPerSecond = (int)Math.Round(statsTicks / secs);
            LastShownPerSecond = (int)Math.Round(statsShown / secs);
            LastSkipped = statsSkipped;
            LastLate = statsLate;
            string places;
            lock (statsPlaces) places = string.Join(", ", statsPlaces);
            double want = Math.Min(fps * speed, LastTicksPerSecond);
            StatsLine = who + " | video " + fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps at " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x | screen " + LastTicksPerSecond + "/s | shown " +
                (frameVisible ? LastShownPerSecond + "/s (of " + want.ToString("0") + " possible)" : "- (Windows' player doesn't tell)") +
                " | skipped " + statsSkipped + " | late " + statsLate + " | surface busy " + statsBusy + " | drawing max " + (frameCopyWorstUs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms, surface wait max " + (statsLockWorstUs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
            if (statsText != null)
                statsText.Text = who + "\nvideo " + fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps at " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x\nscreen frames " + LastTicksPerSecond + "/s\n" +
                    "pictures shown " + (frameVisible ? LastShownPerSecond + "/s of " + want.ToString("0") : "(Windows' player)") + "\nskipped " + statsSkipped + "  late " + statsLate + "  busy " + statsBusy +
                    "\ndrawing max " + (frameCopyWorstUs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms\nsurface wait max " + (statsLockWorstUs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
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
