// FastViewer: resume where you left off, frame-by-frame, A-B loop, trim, and the small pop-up message.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        int preloadAhead = 2;      // files loaded ahead and behind (Settings > Preload); 2 keeps even very fast clicking instant
        bool resumeVideos = true;  // continue long videos where you stopped (Settings)
        const double ResumeMinSeconds = 60;

        static string PositionsFile = Path.Combine( // --positions <file>: tests use a scratch file
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastViewer", "positions.txt");
        readonly Dictionary<string, double> positions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool positionsLoaded;

        TimeSpan? loopA, loopB;
        readonly DispatcherTimer loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        bool trimming, converting;

        readonly DispatcherTimer toastTimer = new DispatcherTimer();
        Action toastAction;
        string toastKind;

        void InitExtras()
        {
            OnClick("ToastBtn", delegate { if (toastAction != null) toastAction(); });
            OnClick("ToastClose", delegate { HideToast(null); });
            InitTrim();
            InitShare();
            InitCrop();
            toastTimer.Tick += delegate { HideToast(null); };
            loopTimer.Tick += delegate { KeepInLoop(); };
            transport.SizeChanged += delegate { UpdateLoopUi(); };
            gifTimer.Tick += delegate { NextGifFrame(); };
        }

        // Called whenever a different file appears on screen.
        void OnShownChanged(Slot before)
        {
            if (trimming) EndTrim();
            if (cropping) EndCrop();
            ClearLoop();
            HideToast("resume");
            var s = shown;
            double saved;
            if (s.IsVideo && resumeVideos && positions.TryGetValue(s.File, out saved) && saved > 1)
            {
                var file = s.File;
                ShowToast("Continuing where you left off (" + Fmt(TimeSpan.FromSeconds(saved)) + ")", "Start over", delegate
                {
                    if (shown != null && shown.File == file) shown.Video.Position = TimeSpan.Zero;
                    ForgetPosition(file);
                    HideToast(null);
                }, 4, "resume");
            }
            UpdateGif();
        }

        // ---------------------------------------------------------------- resume

        // Changes this window made since it last saved, oldest first (NaN = forgotten). Saving merges them into
        // what's in the file now, so several FastViewer windows don't wipe out each other's places.
        readonly List<KeyValuePair<string, double>> positionChanges = new List<KeyValuePair<string, double>>();
        DateTime positionsReadAt;
        bool positionsOverride;

        // positions.txt, oldest first.
        static List<KeyValuePair<string, double>> ReadPositionsFile()
        {
            var list = new List<KeyValuePair<string, double>>();
            if (!File.Exists(PositionsFile)) return list;
            foreach (var line in File.ReadAllLines(PositionsFile))
            {
                int bar = line.IndexOf('|');
                double sec;
                if (bar > 0 && double.TryParse(line.Substring(0, bar), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out sec))
                    list.Add(new KeyValuePair<string, double>(line.Substring(bar + 1), sec));
            }
            return list;
        }

        void LoadPositions()
        {
            if (positionsLoaded) return;
            positionsLoaded = true;
            ReloadPositions();
        }

        // Also when the window comes to the front: another window may have saved places meanwhile.
        void ReloadPositions()
        {
            try
            {
                if (!File.Exists(PositionsFile) || File.GetLastWriteTimeUtc(PositionsFile) == positionsReadAt) return;
                positionsReadAt = File.GetLastWriteTimeUtc(PositionsFile);
                positions.Clear();
                foreach (var p in ReadPositionsFile()) positions[p.Key] = p.Value;
                foreach (var c in positionChanges) // this window's own changes win
                {
                    if (double.IsNaN(c.Value)) positions.Remove(c.Key);
                    else positions[c.Key] = c.Value;
                }
            }
            catch { }
        }

        void ForgetPosition(string file)
        {
            positions.Remove(file);
            positionChanges.Add(new KeyValuePair<string, double>(file, double.NaN));
        }

        void SavePositions()
        {
            if (!positionsLoaded || positionChanges.Count == 0 || ((snapDir != null || selfTestInterval > 0 || fadeTestLog != null) && !positionsOverride)) return;
            try
            {
                var list = ReadPositionsFile();
                foreach (var c in positionChanges)
                {
                    list.RemoveAll(p => string.Equals(p.Key, c.Key, StringComparison.OrdinalIgnoreCase));
                    if (!double.IsNaN(c.Value)) list.Add(c); // at the end = most recent
                }
                positionChanges.Clear();
                // Keep the 300 most recent.
                File.WriteAllLines(PositionsFile, list.Skip(Math.Max(0, list.Count - 300))
                    .Select(p => p.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "|" + p.Key));
                positionsReadAt = DateTime.MinValue;
                ReloadPositions();
            }
            catch { }
        }

        // Remembers where you are in a long video (from 5 s in until 5 s before the end).
        void RememberPosition(Slot s)
        {
            if (s == null || !s.IsVideo || s.Failed || s.File == null) return;
            var d = s.Video.NaturalDuration;
            if (!d.HasTimeSpan) return;
            LoadPositions();
            double length = d.TimeSpan.TotalSeconds, at = s.Video.Position.TotalSeconds;
            if (resumeVideos && length >= (s.IsAudio ? 600 : ResumeMinSeconds) && at >= 5 && at <= length - 5)
            {
                positions[s.File] = at;
                positionChanges.Add(new KeyValuePair<string, double>(s.File, at));
            }
            else if (positions.ContainsKey(s.File)) ForgetPosition(s.File); // watched to the end, or started over
        }
        // Puts a hidden video at its first frame, or where you left off. A freshly opened video is
        // already at its first frame (onlyIfResuming): seeking there anyway would slow switching down.
        void GoToResumePoint(Slot s, bool onlyIfResuming = false)
        {
            LoadPositions();
            double at;
            if (!resumeVideos || s.File == null || !positions.TryGetValue(s.File, out at)) at = 0;
            if (at == 0 && onlyIfResuming) return;
            s.Video.Position = TimeSpan.FromSeconds(at);
        }

        // ---------------------------------------------------------------- frame by frame

        void StepFrame(int direction)
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed) return;
            if (playing) TogglePlay();
            var d = s.Video.NaturalDuration;
            double at = s.Video.Position.TotalSeconds + direction * s.FrameSeconds;
            if (d.HasTimeSpan) at = Math.Min(at, d.TimeSpan.TotalSeconds);
            s.Poster.Visibility = Visibility.Collapsed;
            s.Video.Position = TimeSpan.FromSeconds(Math.Max(0, at));
            UpdateTime();
            ShowOverlay();
        }

        // ---------------------------------------------------------------- A-B loop

        void SetLoopStart()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            loopA = s.Video.Position;
            if (loopB.HasValue && loopB.Value <= loopA.Value) loopB = null;
            AfterLoopChange("Loop start (A) set at " + Fmt(loopA.Value) + ". Press ] at the end point.");
        }

        void SetLoopEnd()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            var at = s.Video.Position;
            if (!loopA.HasValue) loopA = TimeSpan.Zero;
            if (at <= loopA.Value) { loopB = loopA; loopA = at; } // set the "wrong" way round: swap
            else loopB = at;
            AfterLoopChange("Looping " + Fmt(loopA.Value) + " - " + Fmt(loopB.Value) + ". Press \\ to stop.");
        }

        void ClearLoop()
        {
            if (!loopA.HasValue && !loopB.HasValue) return;
            loopA = loopB = null;
            loopTimer.Stop();
            UpdateLoopUi();
            HideToast("loop");
        }

        void AfterLoopChange(string message)
        {
            if (loopB.HasValue) loopTimer.Start();
            UpdateLoopUi();
            var band = F<FrameworkElement>("LoopRange"); // the looped part grows from A to B
            if (loopB.HasValue && Motion.Moves && band.Visibility == Visibility.Visible && band.Width > 14)
            {
                band.RenderTransformOrigin = new Point(0, 0.5);
                Motion.FromTo(Motion.PartsOf(band).Scale, ScaleTransform.ScaleXProperty, 14 / band.Width, 1, Motion.Ms(200), Motion.In);
            }
            if (trimming) PlaceTrim();
            else ShowToast(message, null, null, 3, "loop");
            ShowOverlay();
        }

        void KeepInLoop()
        {
            var s = shown;
            if (s == null || !s.IsVideo || !loopB.HasValue) return;
            if (trimming) PlaceTrimHead();
            var at = s.Video.Position;
            if (at >= loopB.Value || (loopA.HasValue && at < loopA.Value - TimeSpan.FromMilliseconds(500)))
                s.Video.Position = loopA ?? TimeSpan.Zero;
        }

        // Shows the A-B part as a band on the time bar.
        void UpdateLoopUi()
        {
            var band = F<FrameworkElement>("LoopRange");
            var s = shown;
            if (!loopA.HasValue || s == null || !s.IsVideo || !s.Video.NaturalDuration.HasTimeSpan || seek.ActualWidth <= 20)
            {
                band.Visibility = Visibility.Collapsed;
                return;
            }
            double length = s.Video.NaturalDuration.TimeSpan.TotalSeconds;
            double usable = seek.ActualWidth - 20; // the thumb is 20 wide; its centre runs from 10 to width-10
            double x1 = 10 + usable * loopA.Value.TotalSeconds / length;
            double x2 = loopB.HasValue ? 10 + usable * loopB.Value.TotalSeconds / length : x1 + 4;
            band.Margin = new Thickness(x1 - 7, 0, 0, 0);
            band.Width = Math.Max(14, x2 - x1 + 14);
            band.Visibility = Visibility.Visible;
        }

        // ---------------------------------------------------------------- trim

        void SaveTrim()
        {
            var s = shown;
            if (s == null || !loopA.HasValue || !loopB.HasValue) return;
            if (playing) TogglePlay();
            string target = AskSaveVideo(s.File, " (clip)");
            if (target == null) return;
            var from = loopA.Value;
            var to = loopB.Value;
            EndTrim();
            RunConversion(s.File, target, from, to, "Saving clip");
        }

        void MakeCompatibleCopy()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed) return;
            if (playing) TogglePlay();
            string target = AskSaveVideo(s.File, " (compatible)");
            if (target != null) RunConversion(s.File, target, null, null, "Making compatible copy");
        }

        string AskSaveVideo(string source, string suffix)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save video",
                FileName = Path.GetFileNameWithoutExtension(source) + suffix + ".mp4",
                InitialDirectory = Path.GetDirectoryName(source),
                DefaultExt = ".mp4",
                Filter = "MP4 video|*.mp4"
            };
            if (dlg.ShowDialog(this) != true) return null;
            if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("Pick a different name: the original can't be replaced while converting.", null, null, 5, null);
                return null;
            }
            return dlg.FileName;
        }

        bool closeWhenSaved;

        // Closing while a clip or compatible copy is being saved would cut the file off half-way: the window
        // disappears, the saving finishes, then FastViewer closes.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e); // saves settings; may go to the background instead (saving goes on there)
            if (e.Cancel || !converting) return;
            e.Cancel = true;
            closeWhenSaved = true;
            Hide();
        }

        // Converts in the background with Windows' own video tools; progress shows in the pop-up.
        void RunConversion(string source, string target, TimeSpan? from, TimeSpan? to, string what)
        {
            if (converting)
            {
                ShowToast("Already saving a video, one moment...", null, null, 3, null);
                return;
            }
            converting = true;
            int turn;
            rotations.TryGetValue(source, out turn); // save it the way it's shown
            var cancel = new CancelFlag();
            ShowToast(what + "... 0%", "Cancel", delegate { cancel.Cancelled = true; }, 0, "convert");
            Motion.Set((ScaleTransform)F<FrameworkElement>("ToastProgress").RenderTransform, ScaleTransform.ScaleXProperty, 0);
            F<FrameworkElement>("ToastProgress").Visibility = Visibility.Visible;
            int lastPercent = 0;
            var thread = new Thread(delegate()
            {
                string error = null;
                try
                {
                    VideoConverter.Convert(source, target, from, to, turn, delegate(double p)
                    {
                        int percent = (int)(p * 100);
                        if (percent == lastPercent) return;
                        lastPercent = percent;
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (toastKind != "convert") return;
                            F<TextBlock>("ToastText").Text = what + "... " + percent + "%";
                            var bar = F<FrameworkElement>("ToastProgress"); // a thin line that fills smoothly
                            bar.Visibility = Visibility.Visible;
                            var fill = (ScaleTransform)bar.RenderTransform;
                            Motion.To(fill, ScaleTransform.ScaleXProperty, percent / 100.0, Motion.Ms(200, false), Motion.Smooth);
                        }));
                    }, cancel);
                }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    converting = false;
                    if (closeWhenSaved) Dispatcher.BeginInvoke(new Action(Close)); // the window was closed while saving
                    if (cancel.Cancelled || error != null)
                    {
                        try { if (File.Exists(target)) File.Delete(target); } catch { }
                        ShowToast(cancel.Cancelled ? "Cancelled." : "Couldn't save the video: " + error, null, null, 6, null);
                        return;
                    }
                    ShowToast("Saved " + Path.GetFileName(target), "Show in folder", delegate
                    {
                        Process.Start("explorer.exe", "/select,\"" + target + "\"");
                        HideToast(null);
                    }, 8, null);
                    if (snapDir != null) testLog.Add("converted " + target);
                }));
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        // ---------------------------------------------------------------- pop-up message

        void ShowToast(string text, string button, Action action, double seconds, string kind)
        {
            F<TextBlock>("ToastText").Text = text;
            var b = F<Button>("ToastBtn");
            b.Content = button;
            b.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
            toastAction = action;
            toastKind = kind;
            // An icon says what kind of message it is (the short notes of a key or the wheel have none).
            var toastIcon = F<Glyph>("ToastIcon");
            string code = kind == "feedback" || kind == null ? null : kind == "error" ? "\uE7BA" : kind == "update" ? "\uE72C" : kind == "resume" ? "\uE768" : kind == "convert" ? "\uE74E" : "\uE946";
            toastIcon.Visibility = code == null ? Visibility.Collapsed : Visibility.Visible;
            if (code != null) toastIcon.Code = code;
            // Drops in from just above (a message that's already showing only changes its text).
            Motion.Show(F<FrameworkElement>("Toast"), 0, -8);
            if (kind != "convert") F<FrameworkElement>("ToastProgress").Visibility = Visibility.Collapsed;
            toastTimer.Stop();
            if (seconds > 0)
            {
                toastTimer.Interval = TimeSpan.FromSeconds(seconds);
                toastTimer.Start();
            }
        }

        // A short note of what a key or the wheel just changed (volume, skipping). Never covers a message
        // that has a button, like "Start over".
        void ShowFeedback(string text)
        {
            if (toastKind != null && toastKind != "feedback" && toastAction != null) return;
            ShowToast(text, null, null, 1.2, "feedback");
        }

        // Hides the pop-up; with a kind, only if it's that kind of message.
        void HideToast(string kind)
        {
            if (kind != null && kind != toastKind) return; // (closing the save progress just hides it; saving goes on)
            toastTimer.Stop();
            Motion.Hide(F<FrameworkElement>("Toast"), 0, -6, 1, 150); // fades out, rising a little
            toastKind = null;
        }
    }
}
