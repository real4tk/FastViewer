// FastViewer 1.29.0: hold the left mouse button on a video or song and it plays at 2x until you let go (like
// YouTube and TikTok); then it goes back to the speed it had. A short click does what it always did. Only a press
// that stays put for 0.3 s counts, so dragging (panning a zoomed video, moving the mini player) is never a hold.
// The speed change goes the usual way (SetSpeed): the sound follows without stopping (SoundPlay.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        readonly DispatcherTimer holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(300) };
        bool holdPending;      // pressed, not moved, 0.3 s not up yet
        bool holding;          // playing at 2x because the button is held
        bool holdClickPauses;  // the press was on a playing video: a short click pauses it when the button comes up
        bool holdCaptured;     // the stage took the mouse so the release always arrives
        double holdBefore = 1; // the speed to go back to
        Point holdAt;
        System.Drawing.Point holdScreen;
        internal int holdStarts; // tests
        bool holdTestPressed;  // tests: the (simulated) button counts as down
        internal bool holdWarm; // pressed on a playing video (a short click pauses it): FastViewer's own sound takes over at once, so it is
                                // already playing when 2x starts (Windows' player stumbles for ~0.1 s at a speed change, sound too)

        bool CanHold(Slot s)
        {
            return s != null && s.IsVideo && !s.Failed && s.File != null && !cropping && !gridOpen && !settingsOpen;
        }

        void InitHold()
        {
            holdTimer.Tick += delegate { HoldTick(); };
            stage.LostMouseCapture += delegate
            {
                if (!holdCaptured) return;
                holdCaptured = false;
                if (holding || holdPending) HoldReleased(); // (another window took the mouse: the hold is over)
            };
            Deactivated += delegate { if (holding) HoldReleased(); };
        }

        // The button went down on the video: a hold may begin.
        void ArmHold(MouseButtonEventArgs e, bool capture)
        {
            holdTimer.Stop();
            holdPending = false;
            holdWarm = false;
            if (!CanHold(shown)) { holdClickPauses = false; return; }
            holdPending = true;
            holdAt = e.GetPosition(stage);
            holdScreen = System.Windows.Forms.Cursor.Position;
            if (capture && !stage.IsMouseCaptured) holdCaptured = stage.CaptureMouse();
            holdWarm = holdClickPauses && playing; // (only where letting go pauses anyway: no hand-over back mid-play)
            if (holdWarm) UpdateOwnSound();
            holdTimer.Start();
        }

        void HoldMoved(MouseEventArgs e)
        {
            if (!holdPending || holdTestPressed) return;
            if ((e.GetPosition(stage) - holdAt).Length > 6) { holdPending = false; holdTimer.Stop(); } // a drag, not a hold
        }

        void HoldTick()
        {
            holdTimer.Stop();
            if (!holdPending) return;
            holdPending = false;
            if (!holdTestPressed)
            {
                // (Also asked of Windows directly: in the mini player Windows is moving the window, and the window gets no mouse messages.)
                if ((System.Windows.Forms.Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) == 0) return;
                var p = System.Windows.Forms.Cursor.Position;
                if (Math.Abs(p.X - holdScreen.X) > 6 || Math.Abs(p.Y - holdScreen.Y) > 6) return;
            }
            if (!CanHold(shown)) return;
            holding = true;
            holdClickPauses = false;
            holdStarts++;
            holdBefore = speed;
            if (!playing) TogglePlay(); // (mini player on a paused video: holding plays it)
            if (Math.Abs(speed - 2) > 0.001) SetSpeed(2);
            HideToast("feedback");
            Motion.Show(F<FrameworkElement>("HoldHint"), 0, -6);
            Log("hold: 2x (was " + holdBefore + "x)");
        }

        // The button came up (or the hold was taken away).
        void HoldReleased()
        {
            holdTimer.Stop();
            bool was = holding, pauses = holdClickPauses, toggles = miniPressed && miniClick;
            holdWarm = false;
            holdPending = false;
            holdClickPauses = false;
            miniPressed = miniClick = false;
            if (holdCaptured) { holdCaptured = false; if (!panning && stage.IsMouseCaptured) stage.ReleaseMouseCapture(); }
            // Letting go of a hold always plays on, at the speed it had; only a real short click plays or pauses.
            if (was) EndHold();
            else if (pauses && playing && shown != null && shown.IsVideo) { TogglePlay(); clickToggled = true; } // the short click: pause
            else if (toggles && shown != null && shown.IsVideo && !shown.Failed) { TogglePlay(); clickToggled = true; } // mini player: a click plays/pauses
        }

        // Mini player: the press became a drag (moved 4 px, not holding at 2x): from here Windows moves the window.
        bool miniPressed, miniClick;
        System.Drawing.Point miniFrom;

        bool MiniDrag()
        {
            if (!miniPressed || holding) return false;
            if ((System.Windows.Forms.Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) == 0 && !holdTestPressed) return false;
            var p = System.Windows.Forms.Cursor.Position;
            if (Math.Abs(p.X - miniFrom.X) < 4 && Math.Abs(p.Y - miniFrom.Y) < 4) return false;
            miniPressed = miniClick = false;
            holdTimer.Stop();
            holdPending = false;
            holdClickPauses = false;
            if (holdWarm) { holdWarm = false; if (shown != null && OwnSound(shown)) soundSpeedFile = shown.File; } // (no hand-over of the sound mid-play)
            if (holdCaptured) { holdCaptured = false; if (stage.IsMouseCaptured) stage.ReleaseMouseCapture(); }
            try { DragMove(); } catch { }
            return true;
        }

        void EndHold()
        {
            holding = false;
            Motion.Hide(F<FrameworkElement>("HoldHint"), 0, -6, 1, 150);
            // Back to the speed it had (unless the speed was changed meanwhile, with a key or the menu).
            if (Math.Abs(speed - 2) < 0.001 && Math.Abs(holdBefore - 2) > 0.001) SetSpeed(holdBefore);
            Log("hold over: " + speed + "x");
        }

        void CancelHold()
        {
            holdTimer.Stop();
            holdPending = false;
            miniPressed = miniClick = false;
            holdWarm = false;
            holdClickPauses = false;
            if (holdCaptured) { holdCaptured = false; if (!panning && stage.IsMouseCaptured) stage.ReleaseMouseCapture(); }
            if (holding) EndHold();
        }

        // ---------------------------------------------------------------- test: --case hold
        // Clicks and holds on the video with simulated button events (no real mouse: the button counts as down while
        // "held"), and watches the speed, the play state, the picture and the sound every 20 ms.
        void HuntHold()
        {
            muted = false;
            string pick = huntPattern;
            double start = 1.5; // (--pattern name@1: start at that speed)
            if (pick != null && pick.Contains("@")) { start = double.Parse(pick.Substring(pick.IndexOf('@') + 1), System.Globalization.CultureInfo.InvariantCulture); pick = pick.Substring(0, pick.IndexOf('@')); }
            var names = files.Where(f => IsVideoFile(f) || IsAudioFile(f)).Select(System.IO.Path.GetFileName).ToList();
            if (pick != null) names = names.Where(n => n.Equals(pick, StringComparison.OrdinalIgnoreCase)).ToList();
            string photo = files.Where(f => !IsVideoFile(f) && !IsAudioFile(f)).Select(System.IO.Path.GetFileName).FirstOrDefault();
            var setCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            Action<bool, int> press = (down, count) =>
            {
                var a = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { count });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            // Every 20 ms: is the picture moving, is there sound (FastViewer's own, or Windows' player unmuted).
            double lastPos = -1, stillMs = 0, worstStill = 0, silentMs = 0, worstSilent = 0;
            var watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastTick = 0;
            watch.Tick += delegate
            {
                long now = clock.ElapsedMilliseconds, dt = now - lastTick;
                lastTick = now;
                var s = shown;
                if (s == null || !s.IsVideo || !playing) { stillMs = silentMs = 0; lastPos = -1; return; }
                double pos = VideoAt(s).TotalSeconds; // (1.30.0: the drawn picture's place while Windows' player is parked)
                if (lastPos >= 0 && Math.Abs(pos - lastPos) < 0.0005) { stillMs += dt; worstStill = Math.Max(worstStill, stillMs); } else stillMs = 0;
                lastPos = pos;
                bool sound = (OwnSound(s) && soundPlayer != null && soundPlayer.Sounding) || (!s.Video.IsMuted && stillMs == 0); // (Windows' player sounds only while its picture moves)
                if (!sound) { silentMs += dt; worstSilent = Math.Max(worstSilent, silentMs); } else silentMs = 0;
            };
            Action resetWatch = () => { worstStill = worstSilent = 0; stillMs = silentMs = 0; if (soundPlayer != null) soundPlayer.ResetStats(); };
            Func<string> state = () => "speed " + speed + "x (player " + (shown != null && shown.IsVideo ? shown.Video.SpeedRatio.ToString("0.##") : "-") + "), " + (playing ? "playing" : "paused") +
                ", hint " + (F<FrameworkElement>("HoldHint").Visibility == Visibility.Visible ? "shown" : "hidden") + ", holds " + holdStarts +
                ", picture still at most " + worstStill + " ms, sound away at most " + worstSilent + " ms" +
                (soundPlayer != null ? ", sound restarts " + soundPlayer.Resyncs + ", longest gap " + soundPlayer.LongestGap + " ms" : "");
            Action<string, bool> check = (what, ok) => HuntLog("  " + what + ": " + state() + (ok ? "" : " FAIL"));
            int i = 0;
            Action next = null;
            next = () =>
            {
                if (i >= names.Count)
                {
                    if (photo == null) { watch.Stop(); HuntDone(); return; }
                    HuntOpen(photo, () =>
                    {
                        int before = holdStarts;
                        press(true, 1);
                        After(600, () => { press(false, 1); check("photo: holding does nothing", holdStarts == before && speed == 1); watch.Stop(); HuntDone(); });
                    });
                    return;
                }
                string name = names[i++];
                HuntLog(name + (mini ? " (mini player)" : ""));
                HuntOpen(name, () =>
                {
                    SetSpeed(start);
                    shown.Video.Position = TimeSpan.Zero; // (each pass from the start: the clip must not end during it)
                    if (!playing) TogglePlay();
                    After(2500, () =>
                    {
                        // 1. A short click on a playing video pauses it (when the button comes up).
                        press(true, 1);
                        bool stillPlaying = playing;
                        After(120, () =>
                        {
                            press(false, 1);
                            check("short click while playing (playing after the press: " + stillPlaying + ")", stillPlaying && !playing && speed == start);
                            // 2. A short click on a paused video plays it at once.
                            press(true, 1);
                            bool playsAtPress = playing;
                            After(120, () =>
                            {
                                press(false, 1);
                                check("short click while paused (playing after the press: " + playsAtPress + ")", (playsAtPress || mini) && playing && speed == start); // (mini player: a click acts when the button comes up, it could be a drag)
                                After(2000, () =>
                                {
                                    // 3. Hold for 2 s while playing.
                                    int before = holdStarts;
                                    resetWatch();
                                    press(true, 1);
                                    After(200, () => check("0.2 s into a hold", speed == start && playing && holdStarts == before));
                                    After(1000, () => check("1 s into a hold", speed == 2 && playing && holdStarts == before + 1));
                                    After(2300, () =>
                                    {
                                        check("2.3 s into a hold", speed == 2 && playing && worstSilent <= 60);
                                        resetWatch();
                                        press(false, 1);
                                        After(1500, () =>
                                        {
                                            check("1.5 s after letting go", speed == start && playing && worstSilent <= 60 && F<FrameworkElement>("HoldHint").Visibility != Visibility.Visible);
                                            After(2500, () =>
                                            {
                                            check("4 s after letting go (own sound now " + OwnSound(shown) + ")", speed == start && playing && worstSilent <= 60);
                                            // 4. Hold on a paused video: plays at 2x, then on at 1.5x.
                                            TogglePlay();
                                            After(500, () =>
                                            {
                                                press(true, 1);
                                                After(1200, () =>
                                                {
                                                    check("held on a paused video", speed == 2 && playing);
                                                    press(false, 1);
                                                    After(800, () =>
                                                    {
                                                        check("let go", speed == start && playing);
                                                        // 5. Double-click: full screen (or back from the mini player), playing as before.
                                                        bool wasFull = fullscreen, wasMini = mini;
                                                        press(true, 1); press(false, 1);
                                                        After(80, () =>
                                                        {
                                                            press(true, 2); press(false, 2);
                                                            After(1200, () =>
                                                            {
                                                                check("double-click (full screen " + wasFull + " -> " + fullscreen + ", mini " + wasMini + " -> " + mini + ")", playing && speed == start && (fullscreen != wasFull || mini != wasMini));
                                                                if (fullscreen) ToggleFullscreen();
                                                                SetSpeed(1);
                                                                // Next: the same file in the mini player, then the others.
                                                                if (!mini && !wasMini && i == 1 && names.Count > 0) { i = 0; ToggleMini(); After(1500, next); }
                                                                else { if (mini) ToggleMini(); After(1500, next); }
                                                            });
                                                        });
                                                    });
                                                });
                                            });
                                            });
                                        });
                                    });
                                });
                            });
                        });
                    });
                });
            };
            watch.Start();
            next();
        }
    }
    partial class ViewerWindow
    {
        // --case holdstorm [--pattern file]: 12 quick holds in a row (held 0.35-0.9 s, 0.15-0.6 s apart), in the window and
        // then the mini player. Every 20 ms: the speed it should have (2x while a hold is on, else the speed before), the
        // pace the sound is read at, and how fast the picture moves (over the last 200 ms). The sound must never be read
        // slower or faster than it should (0.5% for the tiny correction; right after a change it may still have the old
        // speed); the picture never under 90% of its speed for longer than Windows' own stand-still at a change.
        void HuntHoldStorm()
        {
            muted = false;
            Loudness.On = false;
            var names = files.Where(f => IsVideoFile(f) && !IsAudioFile(f)).Select(System.IO.Path.GetFileName).ToList();
            if (huntPattern != null && huntPattern != "mixed") names = names.Where(nn => nn.Equals(huntPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            var setCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true);
            Action<bool> press = down =>
            {
                var a = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent, Source = stage };
                setCount.Invoke(a, new object[] { 1 });
                holdTestPressed = down;
                stage.RaiseEvent(a);
            };
            var rnd = new Random(7);
            var runs = new List<Action<Action>>();
            foreach (var name in names)
                foreach (bool inMini in new[] { false, true })
                {
                    string nm = name; bool m = inMini;
                    runs.Add(next => HuntOpen(nm, () =>
                    {
                        if (m != mini) ToggleMini();
                        SetSpeed(1);
                        shown.Video.Position = TimeSpan.FromSeconds(1);
                        if (!playing) TogglePlay();
                        After(2500, () =>
                        {
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            var samples = new List<double[]>(); // ms, should, sound pace, picture position
                            long lastChange = 0;
                            double should = 1;
                            var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
                            t.Tick += delegate
                            {
                                double want = holding ? 2 : speed;
                                if (Math.Abs(want - should) > 0.001) { should = want; lastChange = clock.ElapsedMilliseconds; }
                                var p = soundPlayer;
                                samples.Add(new[] { clock.ElapsedMilliseconds, should, p != null && OwnSound(shown) ? p.LastStep : -1, (shown.Video.IsOwn ? shown.Video.CountedNow() : shown.Video.Position.TotalSeconds), lastChange, shown.Video.SpeedRatio, speedApplies, pictureWakes + 100 * speedRepairs });
                            };
                            t.Start();
                            int i = 0;
                            Action hold = null;
                            hold = () =>
                            {
                                if (i++ >= 12)
                                {
                                    // Then 3 s at its speed.
                                    After(3200, () =>
                                    {
                                        t.Stop();
                                        HuntLog(nm + (m ? " (mini player)" : "") + ": " + StormVerdict(samples) + (soundPlayer != null ? " | drift now " + (soundPlayer.LastDrift * 1000).ToString("0") + " ms, output delay " + (soundPlayer.OutputDelay * 1000).ToString("0") + " ms, last timed delay " + (lastTimedDelay * 1000).ToString("0") + " ms" : ""));
                                        if (mini) ToggleMini();
                                        After(1200, next);
                                    });
                                    return;
                                }
                                press(true);
                                int held = 350 + rnd.Next(550), gap = 150 + rnd.Next(450);
                                After(held, () => { press(false); After(gap, hold); });
                            };
                            hold();
                        });
                    }));
                }
            int n = 0;
            Action go = null;
            go = () => { if (n < runs.Count) runs[n++](go); else HuntDone(); };
            go();
        }

        static string StormVerdict(List<double[]> s)
        {
            // Sound: outside the 0.6 s after a change, read at the speed it should have (within 0.5%); at any time never
            // slower than the lower of the old and new speed, never faster than the higher.
            double worstSound = 0, slowest = 9, fastest = 0;
            int soundBad = 0, pictureBad = 0, longestSlowMs = 0, slowRun = 0;
            double minPic = 9, prevShould = 1;
            var episodes = new List<string>();
            for (int k = 0; k < s.Count; k++)
            {
                double ms = s[k][0], should = s[k][1], pace = s[k][2], since = ms - s[k][4];
                if (pace < 0) continue;
                if (k > 0 && Math.Abs(s[k - 1][1] - should) > 0.001) prevShould = s[k - 1][1];
                double lo = Math.Min(should, prevShould), hi = Math.Max(should, prevShould);
                if (since > 600) { double off = Math.Abs(pace / should - 1); worstSound = Math.Max(worstSound, off); if (off > (since < 4000 ? 0.0155 : 0.005)) soundBad++; }
                if (pace < lo * 0.984 || pace > hi * 1.016) soundBad++;
                slowest = Math.Min(slowest, pace / should); fastest = Math.Max(fastest, pace / should);
                // Picture over the last 200 ms.
                int j = k; while (j > 0 && s[k][0] - s[j][0] < 200) j--;
                double dt = (s[k][0] - s[j][0]) / 1000.0;
                if (dt < 0.15) continue;
                double picPace = (s[k][3] - s[j][3]) / dt;
                if (since > 600 && s[k][0] - s[j][4] > 600) // (the whole 200 ms well after a change)
                {
                    double rel = picPace / should;
                    minPic = Math.Min(minPic, rel);
                    if (rel < 0.9) { slowRun += 20; longestSlowMs = Math.Max(longestSlowMs, slowRun); pictureBad++; if (slowRun == 20 && episodes.Count < 8) { int a = k; while (a > 0 && s[a][6] == s[k][6]) a--; episodes.Add(((s[k][0] - s[k][4]) / 1000.0).ToString("0.00") + " s after the change: " + (rel * 100).ToString("0") + "%, picture set to " + s[k][5].ToString("0.##") + "x (should " + should + "), its last speed change " + ((s[k][0] - s[a][0]) / 1000.0).ToString("0.00") + " s before, woken/repaired " + s[k][7]); } } else slowRun = 0;
                }
            }
            var tail = s.Where(x => x[0] > s[s.Count - 1][0] - 2500 && x[2] >= 0).ToList();
            double tailMin = tail.Count > 0 ? tail.Min(x => x[2]) : -1, tailMax = tail.Count > 0 ? tail.Max(x => x[2]) : -1;
            bool ok = soundBad == 0 && pictureBad == 0 && tailMin >= 0.995 && tailMax <= 1.005;
            return "sound read at " + slowest.ToString("0.000") + "-" + fastest.ToString("0.000") + " x of its speed (" + soundBad + " ticks off; 0.6 s after a change at most " + (worstSound * 100).ToString("0.0") + "% off), " +
                "picture well after a change at least " + (minPic * 100).ToString("0") + "% of its speed (" + longestSlowMs + " ms under 90% at most), last 2.5 s at 1x: sound " + tailMin.ToString("0.000") + "-" + tailMax.ToString("0.000") + (ok ? "" : " FAIL") + (episodes.Count > 0 ? " / slow picture: " + string.Join(" / slow picture: ", episodes) : "");
        }
    }
}
