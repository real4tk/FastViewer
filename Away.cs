// FastViewer: memory while you're away (1.23.0). Each video kept ready ahead or behind costs about 135 MB.
// While the window is minimized, or another app has been in front for a while, those hidden video players
// are closed; the file on screen is never touched. The moment the window comes back (it becomes the active
// window, is restored, or the pointer moves onto it) they are loaded again, so the first switch is instant
// like always. Nothing is ever let go while the window is the one you're using.

using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        const double AwayMinimizedSeconds = 5;    // minimized: nothing of it is on screen
        const double AwayBackgroundSeconds = 30;  // another app in front (FastViewer may still be visible beside it)

        DispatcherTimer awayTimer;
        bool awayReleased;        // the hidden video players are closed right now
        bool? awayTest;           // tests: pretend the window is away (true) or in use (false)
        double awayTestSeconds;   // tests: wait this long instead
        internal int AwayReleases, AwayReloads; // (tests)

        void InitAway()
        {
            awayTimer = new DispatcherTimer();
            awayTimer.Tick += delegate { awayTimer.Stop(); ReleaseHiddenPlayers(); };
            StateChanged += delegate { AwayCheck(); };
            Deactivated += delegate { AwayCheck(); };
            Activated += delegate { BackFromAway(); };
            MouseEnter += delegate { BackFromAway(); };      // the pointer arrives before the click does
            PreviewMouseMove += delegate { if (awayReleased) BackFromAway(); };
            PreviewKeyDown += delegate { if (awayReleased) BackFromAway(); };
        }

        // Away: minimized, or not the active window. Test windows are never active, so they only count as away
        // when a test says so.
        bool Away
        {
            get
            {
                if (awayTest != null) return awayTest.Value;
                if (testRun) return false;
                return WindowState == WindowState.Minimized || !IsActive;
            }
        }

        void AwayCheck()
        {
            if (awayTimer == null) return;
            if (!Away) { BackFromAway(); return; }
            if (awayReleased) return;
            bool minimized = WindowState == WindowState.Minimized && awayTest == null;
            awayTimer.Stop();
            awayTimer.Interval = TimeSpan.FromSeconds(awayTestSeconds > 0 ? awayTestSeconds : minimized ? AwayMinimizedSeconds : AwayBackgroundSeconds);
            awayTimer.Start();
        }

        void ReleaseHiddenPlayers()
        {
            if (!Away || awayReleased || slots == null || !IsVisible) return;
            // Still in use from a distance: something is playing (a video beside your game, music, a slideshow
            // moving on by itself), a save is running, or the pointer rests on the window. Look again later.
            if (playing || slideshow || trimming || cropping || (IsMouseOver && WindowState != WindowState.Minimized))
            {
                awayTimer.Start();
                return;
            }
            int closed = 0;
            foreach (var s in slots)
            {
                if (s == shown || s == wanted || s == revealing || s == fading || s.File == null || !s.IsVideo) continue;
                s.Token++;
                s.Video.Stop();
                s.Video.Close();
                s.Video.Source = null;
                Dropped(s.Poster.Source as System.Windows.Media.Imaging.BitmapSource);
                s.Poster.Source = null;
                s.File = null;
                s.Ready = false;
                closed++;
            }
            awayReleased = true;
            AwayReleases += closed;
            if (closed > 0) GC.Collect();
        }

        // Back in use: everything around the current file is made ready again, exactly as after opening it.
        void BackFromAway()
        {
            if (awayTimer == null) return;
            awayTimer.Stop();
            if (!awayReleased) return;
            awayReleased = false;
            if (files.Count == 0 || shown == null) return;
            AwayReloads++;
            var keep = new System.Collections.Generic.HashSet<Slot>();
            Ensure(files[index], keep);
            for (int k = 1; k <= preloadAhead; k++)
            {
                if (files.Count > k) Ensure(files[Wrap(index + lastDir * k)], keep);
                if (files.Count > k + 1) Ensure(files[Wrap(index - lastDir * k)], keep);
            }
        }

        // ---------------------------------------------------------------- test: --hunt <dir> --case away
        // Folder: photo, videos around it. Checks that nothing is let go while the window is in use, that hidden
        // video players close when away (and the file on screen stays), that they're back after returning, and
        // that the first switch after returning is as instant as one before.

        void HuntAway()
        {
            var me = Process.GetCurrentProcess();
            Func<string> mem = () => { GC.Collect(); GC.WaitForPendingFinalizers(); me.Refresh(); return (me.PrivateMemorySize64 >> 20) + " MB private, " + (me.WorkingSet64 >> 20) + " MB in RAM"; };
            Func<int> hiddenVideos = () => slots.Count(s => s != shown && s.File != null && s.IsVideo);
            Func<int> hiddenReady = () => slots.Count(s => s != shown && s.File != null && s.IsVideo && s.Ready);
            Func<string> state = () => "on screen '" + (shown == null ? "-" : System.IO.Path.GetFileName(shown.File)) + "' ready " + (shown != null && shown.Ready) +
                ", hidden videos loaded " + hiddenVideos() + " (ready " + hiddenReady() + ")";
            // One switch: was the new file on screen in the same instant, and if not, after how long.
            Action<string, Action> timedSwitch = (what, then) =>
            {
                var sw = Stopwatch.StartNew();
                Go(1);
                if (shown == wanted) { HuntLog(what + ": instant (0 ms, '" + System.IO.Path.GetFileName(shown.File) + "')"); then(); return; }
                var poll = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(2) };
                poll.Tick += delegate
                {
                    if (shown != wanted && sw.ElapsedMilliseconds < 5000) return;
                    poll.Stop();
                    HuntLog(what + ": " + (shown == wanted ? "shown after " + sw.ElapsedMilliseconds + " ms" : "NOT SHOWN within 5 s") + " ('" + System.IO.Path.GetFileName(wanted.File) + "')");
                    then();
                };
                poll.Start();
            };
            int t = 2500;
            After(t, () => { if (playing) TogglePlay(); HuntLog("start: " + state() + "; " + mem()); });
            // 1. In use (not away) for longer than the wait: nothing may be let go.
            After(t += 200, () => { awayTest = false; awayTestSeconds = 0.3; AwayCheck(); });
            After(t += 1500, () => HuntLog("in use for 1.5 s (wait 0.3 s): " + state() + ", let go so far " + AwayReleases + (AwayReleases == 0 ? "  ok" : "  WRONG")));
            After(t += 100, () => timedSwitch("switch before going away", delegate { }));
            After(t += 2500, () => { if (playing) TogglePlay(); HuntLog("settled: " + state() + "; " + mem()); });
            // 2. Away while a video plays: still in use, nothing let go.
            After(t += 200, () => { if (shown.IsVideo && !playing) TogglePlay(); awayTest = true; AwayCheck(); });
            After(t += 1500, () => { HuntLog("away but playing (" + playing + "): let go " + AwayReleases + (AwayReleases == 0 || !playing ? "  ok" : "  WRONG")); if (playing) TogglePlay(); });
            // 3. Away and paused: the hidden video players close, the file on screen stays as it is.
            After(t += 1500, () => HuntLog("away, paused: " + state() + ", let go " + AwayReleases + "; " + mem() +
                (hiddenVideos() == 0 && shown != null && shown.Ready && shown.File != null ? "  ok" : "  WRONG")));
            After(t += 100, () => Snap("away_released"));
            // 4. Back: reloaded at once; how long until every hidden video is ready again.
            After(t += 300, () =>
            {
                var sw = Stopwatch.StartNew();
                awayTest = false;
                BackFromAway();
                HuntLog("back: reloads " + AwayReloads + ", " + state());
                var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                poll.Tick += delegate
                {
                    if (hiddenReady() < hiddenVideos() && sw.ElapsedMilliseconds < 8000) return;
                    poll.Stop();
                    HuntLog("all hidden videos ready again " + sw.ElapsedMilliseconds + " ms after coming back: " + state() + "; " + mem());
                };
                poll.Start();
            });
            // 5. The first switches after returning.
            After(t += 2500, () => timedSwitch("first switch after coming back", delegate { }));
            After(t += 1200, () => timedSwitch("second switch", delegate { }));
            // 6. Coming back and switching in the same instant (a key press that also activates the window).
            After(t += 2500, () => { if (playing) TogglePlay(); awayTest = true; AwayCheck(); });
            After(t += 1500, () => { HuntLog("away again: " + state()); awayTest = false; BackFromAway(); timedSwitch("switch in the same instant as coming back (worst case)", delegate { }); });
            After(t += 3000, () => { HuntLog("end: " + state() + "; " + mem()); HuntDone(); });
        }
    }
}
