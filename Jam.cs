// FastViewer 1.30.0 round 3: a jammed video player is noticed and made again by itself.
//
// Once a second, while a video is on screen: if it should be playing but its place hasn't moved for 3.5 s, or it hasn't
// opened 6 s after being asked, a test reader is opened on the file off the window's thread. Opens: only this player is
// stuck: it is made again (a new Windows player, the file opened afresh at the same place, playing on). Doesn't open in
// 4 s: Windows' media pipeline is stuck for the whole program (seen after skipping forward again and again at the end of a
// clip: nothing played any more, other videos neither, until FastViewer was ended in Task Manager); nothing inside the
// program can mend that, so FastViewer starts itself again on the same file and place and the stuck one ends. Both are
// written to errors.txt. A player made again twice within a minute counts as the pipeline being stuck.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FastViewer
{
    static partial class Program
    {
        // Set when this FastViewer was started by one that was stuck (--after-jam): where to go on.
        internal static string JamResumeFile;
        internal static double JamResumeAt = -1;
        internal static bool JamResumePlaying;

        // In Main, before anything else: wait for the stuck one to end, and take the place to go on from.
        internal static string[] AfterJam(string[] args)
        {
            int at = Array.IndexOf(args, "--after-jam");
            if (at < 0) return args;
            var rest = new List<string>();
            int pid = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--after-jam" && i + 1 < args.Length) { int.TryParse(args[++i], out pid); continue; }
                if (args[i] == "--resume-at" && i + 1 < args.Length) { double v; if (double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) JamResumeAt = v; continue; }
                if (args[i] == "--resume-playing" && i + 1 < args.Length) { JamResumePlaying = args[++i] == "1"; continue; }
                rest.Add(args[i]);
            }
            try { if (pid > 0) Process.GetProcessById(pid).WaitForExit(6000); } catch { }
            if (rest.Count > 0 && !rest[0].StartsWith("--")) JamResumeFile = rest[0];
            return rest.ToArray();
        }
    }

    partial class ViewerWindow
    {
        internal int playerRebuilds, jamRestarts; // (tests)
        internal static bool jamProbeFails;       // (tests: --jam-probe-fail, the test reader "never opens")
        DispatcherTimer jamTimer;
        string jamFile;
        double jamPlace;
        long jamStillSince, jamNotReadySince;
        bool jamProbing;
        readonly List<long> jamRebuildTimes = new List<long>();

        void StartJamWatch()
        {
            jamTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            jamTimer.Tick += delegate { JamTick(); };
            jamTimer.Start();
        }

        static double Secs(long since) { return (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency; }

        void JamTick()
        {
            var s = shown;
            long now = Stopwatch.GetTimestamp();
            if (jamProbing) return;
            if (s == null || !s.IsVideo || s.Failed || s.File == null || !IsVisible || gridOpen || scrubbing || trimDrag != '\0' || Switching)
            {
                jamStillSince = jamNotReadySince = 0;
                return;
            }
            if (!s.Ready)
            {
                if (jamNotReadySince == 0 || jamFile != s.File) { jamNotReadySince = now; jamFile = s.File; }
                else if (Secs(jamNotReadySince) > 6) Probe(s, "it didn't open in 6 s");
                return;
            }
            jamNotReadySince = 0;
            if (!playing) { jamStillSince = 0; return; }
            double place = VideoAt(s).TotalSeconds;
            if (s.File != jamFile || jamStillSince == 0 || Math.Abs(place - jamPlace) > 0.02)
            {
                jamFile = s.File;
                jamPlace = place;
                jamStillSince = now;
                return;
            }
            if (Secs(jamStillSince) > 3.5) Probe(s, "it stood still for 3.5 s while playing, at " + place.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s");
        }

        // Is Windows' media pipeline still answering? A reader of the file, opened off the window's thread.
        void Probe(Slot s, string why)
        {
            jamProbing = true;
            string file = s.File;
            var done = new ManualResetEvent(false);
            bool opened = false;
            var t = new Thread(() =>
            {
                try
                {
                    if (!jamProbeFails)
                    {
                        VideoConverter.Reading.Begin();
                        var r = VideoConverter.Reading.Open(file);
                        if (r != null) { opened = true; System.Runtime.InteropServices.Marshal.ReleaseComObject(r); }
                    }
                    else Thread.Sleep(10000);
                }
                catch { }
                done.Set();
            }) { IsBackground = true, Name = "Jam probe" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            var started = Stopwatch.StartNew();
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            wait.Tick += delegate
            {
                bool finished = done.WaitOne(0);
                if (!finished && started.ElapsedMilliseconds < 4000) return;
                wait.Stop();
                jamProbing = false;
                jamStillSince = jamNotReadySince = 0;
                if (shown != s || s.File != file) return; // (moved on meanwhile)
                long now = Stopwatch.GetTimestamp();
                jamRebuildTimes.RemoveAll(x => Secs(x) > 60);
                if (finished && opened && jamRebuildTimes.Count < 2) RebuildPlayer(s, why);
                else RestartForJam(s, why + (finished ? (opened ? "; made again twice within a minute" : "; a test reader failed") : "; a test reader didn't open in 4 s"));
            };
            wait.Start();
        }

        // A new Windows player for the slot, the file opened afresh where it was, playing on if it was.
        void RebuildPlayer(Slot s, string why)
        {
            playerRebuilds++;
            jamRebuildTimes.Add(Stopwatch.GetTimestamp());
            var at = VideoAt(s);
            Program.LogUiError(new Exception("A video player stopped (" + why + "): it was made again. File: " + Path.GetFileName(s.File)));
            Log("jam: player made again (" + why + ")");
            if (frameParked && parkSlot == s) Unpark(false, false);
            if (frameReader != null) DropFrames();
            string file = s.File;
            ++s.Token;
            try { s.Video.Stop(); s.Video.Close(); } catch { }
            NewVideoPlayer(s);
            s.Ready = false;
            s.SeekOnOpen = at;
            s.Video.Visibility = Visibility.Visible;
            s.Video.Volume = PlayVolume(s);
            s.Video.IsMuted = muted || OwnSoundBase(s);
            s.Video.Source = new Uri(file);
            if (playing) { s.Video.Play(); s.Video.SpeedRatio = speed; }
            else s.Video.Pause();
            UpdateOwnSound();
        }

        // Windows' media pipeline is stuck for the whole program: FastViewer starts itself again on the same file and place.
        void RestartForJam(Slot s, string why)
        {
            jamRestarts++;
            Program.LogUiError(new Exception("Windows' video pipeline stopped answering (" + why + "): FastViewer started itself again. File: " + Path.GetFileName(s.File)));
            Log("jam: restart (" + why + ")");
            if (testRun) return; // (tests count it; they never start programs)
            try
            {
                double at = VideoAt(s).TotalSeconds;
                RememberPosition(s);
                SavePositions();
                SaveSettings(true);
                string exe = Assembly.GetExecutingAssembly().Location;
                string args = "\"" + s.File + "\" --after-jam " + Process.GetCurrentProcess().Id + " --resume-at " +
                    at.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " --resume-playing " + (playing ? "1" : "0");
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            }
            catch (Exception ex) { Program.LogUiError(ex); }
            // (A stuck pipeline can also hold up a normal exit: end for sure.)
            new Thread(() => { Thread.Sleep(2000); try { Process.GetCurrentProcess().Kill(); } catch { } }) { IsBackground = true }.Start();
            try { Environment.Exit(0); } catch { }
        }

        // Started by a FastViewer that was stuck: on to the same place, playing if it was.
        void ResumeAfterJam()
        {
            if (Program.JamResumeAt < 0 || Program.JamResumeFile == null) return;
            string file = Path.GetFullPath(Program.JamResumeFile);
            double at = Program.JamResumeAt;
            bool play = Program.JamResumePlaying;
            Program.JamResumeAt = -1;
            var watch = Stopwatch.StartNew();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            t.Tick += delegate
            {
                var s = shown;
                if (watch.ElapsedMilliseconds > 10000) { t.Stop(); return; }
                if (s == null || !s.Ready || s.File == null || !string.Equals(Path.GetFullPath(s.File), file, StringComparison.OrdinalIgnoreCase)) return;
                t.Stop();
                s.Video.Position = TimeSpan.FromSeconds(at);
                if (play && !playing) TogglePlay();
                else if (!play && playing) TogglePlay();
            };
            t.Start();
        }
    }
}
