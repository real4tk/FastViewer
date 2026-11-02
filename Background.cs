// FastViewer: "ready in the background" for instant opening.
//
// Closing a window hides FastViewer instead of quitting (when the setting is on); it then lets go
// of all files, shows an icon by the clock, and listens on a private pipe. The next launch
// (double-clicking a file) hands the file over and quits before loading any window code, so the
// hidden one shows it in a blink. Once it's showing a file it stops listening, so a file opened
// while a window is open gets a new window of its own. Only one FastViewer waits in the background;
// closing other windows quits them.

using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;

namespace FastViewer
{
    // Used by Main before any WPF code loads: keep this class free of window types.
    static class SingleInstance
    {
        static readonly string Id = "FastViewer-" + WindowsIdentity.GetCurrent().User.Value; // per Windows user
        public static readonly string PipeName = Id + "-open";
        public static readonly string MutexName = @"Local\" + Id;

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        // True when a FastViewer that's already running took the file (this launch can end).
        public static bool TryForward(string[] args)
        {
            foreach (var a in args) if (a.StartsWith("--")) return false; // test runs always stay separate
            Mutex running;
            if (!Mutex.TryOpenExisting(MutexName, out running)) return false; // none running: quick check, no waiting
            running.Dispose();
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipe.Connect(1500);
                    AllowSetForegroundWindow(-1); // lets the running FastViewer come to the front
                    var data = Encoding.UTF8.GetBytes(args.Length > 0 ? Path.GetFullPath(args[0]) : "");
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch { return false; }
        }

        public const string QuitMessage = "\0quit"; // not a file name: the FastViewer waiting in the background quits

        // After an update: the background FastViewer (old version) quits, so the new version receives files from now on.
        public static void AskBackgroundToQuit()
        {
            Mutex running;
            if (!Mutex.TryOpenExisting(MutexName, out running)) return;
            running.Dispose();
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipe.Connect(500);
                    var data = Encoding.UTF8.GetBytes(QuitMessage);
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();
                }
            }
            catch { }
        }
    }

    partial class ViewerWindow
    {
        bool keepReady = true;   // Settings > Speed > Stay ready in the background
        bool toldAboutBackground; // the one-time "FastViewer is still ready" notice was shown
        bool quitting;
        Mutex instanceMutex;
        volatile int pipeGeneration; // a pipe thread stops when this moves on
        volatile bool pipeListening;
        string lastOpened;
        System.Windows.Forms.NotifyIcon tray;

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        // Called when the window has opened (not in test runs).
        void StartSingleInstance()
        {
            Closing += OnClosingToBackground;
        }

        // Becomes the FastViewer that receives double-clicked files. False if another one already is.
        bool ListenForFiles()
        {
            if (instanceMutex != null) return true;
            bool created;
            var m = new Mutex(true, SingleInstance.MutexName, out created);
            if (!created)
            {
                m.Dispose();
                return false;
            }
            instanceMutex = m;
            int generation = ++pipeGeneration;
            new Thread(delegate() { ServePipe(generation); }) { IsBackground = true }.Start();
            return true;
        }

        // Waits for one file. After that this FastViewer is on screen, so it stops listening right away:
        // a second file double-clicked a moment later must get a window of its own.
        void ServePipe(int generation)
        {
            pipeListening = true;
            try
            {
                var me = WindowsIdentity.GetCurrent().User;
                while (generation == pipeGeneration)
                {
                    try
                    {
                        var security = new PipeSecurity(); // only this Windows user can hand files over
                        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                        using (var server = new NamedPipeServerStream(SingleInstance.PipeName, PipeDirection.In, 1,
                                   PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security))
                        {
                            server.WaitForConnection();
                            var buffer = new MemoryStream();
                            server.CopyTo(buffer);
                            string path = Encoding.UTF8.GetString(buffer.ToArray());
                            if (path == SingleInstance.QuitMessage)
                            {
                                pipeGeneration++;
                                pipeListening = false;
                                Dispatcher.BeginInvoke(new Action(delegate { if (!IsVisible) Quit(); else StopSingleInstance(); }));
                                return;
                            }
                            if (generation != pipeGeneration)
                            {
                                // Handed over just as this window took a file itself: give it a window of its own.
                                if (path.Length > 0)
                                    try { System.Diagnostics.Process.Start(Assembly.GetExecutingAssembly().Location, "\"" + path + "\""); } catch { }
                                return;
                            }
                            pipeGeneration++;
                            pipeListening = false;
                            Dispatcher.BeginInvoke(new Action(delegate { OpenFromOutside(path); }));
                            return;
                        }
                    }
                    catch { Thread.Sleep(200); }
                }
            }
            finally { pipeListening = false; }
        }

        void StopSingleInstance()
        {
            if (instanceMutex == null) return;
            pipeGeneration++;
            if (pipeListening) // wake the pipe thread so it can end
                try
                {
                    using (var wake = new NamedPipeClientStream(".", SingleInstance.PipeName, PipeDirection.Out)) wake.Connect(200);
                }
                catch { }
            try { instanceMutex.ReleaseMutex(); } catch { }
            instanceMutex.Dispose();
            instanceMutex = null;
        }

        // A file double-clicked elsewhere (or "Open" from the icon by the clock).
        void OpenFromOutside(string path)
        {
            StopSingleInstance(); // on screen now: the next double-clicked file gets a new window
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) OpenPath(path);
            else if (files.Count == 0 && lastOpened != null && File.Exists(lastOpened)) OpenPath(lastOpened);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; // brings it in front of the window you double-clicked in
            Topmost = pinned || mini;
            if (tray != null) tray.Visible = false;
        }

        void OnClosingToBackground(object sender, CancelEventArgs e)
        {
            // Another FastViewer is already waiting in the background: this window just closes.
            if (quitting || !keepReady || !ListenForFiles())
            {
                StopSingleInstance();
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                return;
            }
            e.Cancel = true;
            GoToBackground();
        }

        // Hide, let go of every file (so they can be moved or deleted), free memory, and wait.
        void GoToBackground()
        {
            SaveWaitingRotations();
            RememberPosition(shown);
            SavePositions();
            if (trimming) EndTrim();
            if (mini) ToggleMini();
            if (fullscreen) ToggleFullscreen();
            if (settingsOpen) CloseSettings();
            CloseGrid();
            SetVolumeSlider(false);
            StopSlideshow();
            Motion.Hide(dialog);
            HideToast(null);
            ClearLoop();
            SaveSettings(true);
            lastOpened = CurrentFile;
            LetGoOfFiles();

            Hide();
            ShowTray();
            if (!toldAboutBackground) // the first time only: closing didn't quit, so say where FastViewer went
            {
                toldAboutBackground = true;
                SaveSettings();
                try
                {
                    tray.ShowBalloonTip(8000, "FastViewer is still ready",
                        "It waits here so the next photo or video opens instantly. Right-click this icon to quit. " +
                        "You can turn this off in Settings > Speed.", System.Windows.Forms.ToolTipIcon.Info);
                }
                catch { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); // hand unused memory back to Windows
        }

        // Closes every file (so they can be moved or deleted) and drops the pictures and thumbnails.
        void LetGoOfFiles()
        {
            StopRevealWatch();
            gifTimer.Stop();
            foreach (var s in slots)
            {
                s.Token++;
                s.Video.Stop();
                s.Video.Close();
                s.Video.Source = null;
                s.Picture.Source = null;
                s.Poster.Source = null;
                s.Gif = null;
                s.File = null;
                s.Ready = false;
                System.Windows.Controls.Panel.SetZIndex(s.Host, 0);
            }
            shown = wanted = null;
            playing = false;
            files = new System.Collections.Generic.List<string>();
            ClearStrip();
            Thumbs.Clear();
            StopWatching();
            titleText.Text = "";
            message.Text = EmptyMessage();
            F<FrameworkElement>("MessageBtn").Visibility = Visibility.Collapsed;
            UpdateControls();
            UpdateFacts();   // no file: no details or zoom from the last one
            UpdateZoomUi();
        }

        void ShowTray()
        {
            if (tray == null)
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe),
                    Text = "FastViewer is ready (double-click to open)"
                };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open FastViewer", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { OpenFromOutside(null); })); });
                menu.Items.Add("Quit FastViewer", null, delegate { Dispatcher.BeginInvoke(new Action(Quit)); });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(delegate { OpenFromOutside(null); })); };
            }
            tray.Visible = true;
        }

        void Quit()
        {
            quitting = true;
            Close();
        }

        // Before restarting into a new version: stop receiving files, so the new one takes over.
        void PrepareRestart()
        {
            StopSingleInstance();
            quitting = true;
        }
    }
}
