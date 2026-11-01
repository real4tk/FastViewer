// FastViewer: a fast photo & video viewer for Windows, laid out like the Windows Photos app.
//
// Why switching is instant: the previous and next files are pre-loaded into hidden
// players, so "next" just swaps which player is visible. The current picture is never
// removed until the new one is ready, so there is no black flash between files.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace FastViewer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (SingleInstance.TryForward(args)) return; // a FastViewer waiting in the background shows the file
            Run(args);
        }

        // Separate, so the window code only loads when this FastViewer actually opens a window.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run(string[] args)
        {
            try
            {
                // Faster startup: .NET remembers which code ran last time and prepares it in parallel.
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastViewer");
                Directory.CreateDirectory(dir);
                System.Runtime.ProfileOptimization.SetProfileRoot(dir);
                System.Runtime.ProfileOptimization.StartProfile("startup.profile");
            }
            catch { }
            // Test hook: FastViewer.exe --convert <source> <target> <rotation> [from-seconds to-seconds]
            if (args.Length >= 4 && args[0] == "--convert")
            {
                string log = args[2] + ".log";
                try
                {
                    TimeSpan? from = args.Length >= 6 ? TimeSpan.FromSeconds(double.Parse(args[4], CultureInfo.InvariantCulture)) : (TimeSpan?)null;
                    TimeSpan? to = args.Length >= 6 ? TimeSpan.FromSeconds(double.Parse(args[5], CultureInfo.InvariantCulture)) : (TimeSpan?)null;
                    var sw = Stopwatch.StartNew();
                    VideoConverter.Convert(args[1], args[2], from, to, int.Parse(args[3]), delegate { }, new CancelFlag());
                    File.WriteAllText(log, "OK in " + sw.ElapsedMilliseconds + " ms; " + VideoConverter.LastInfo);
                }
                catch (Exception ex) { File.WriteAllText(log, "ERROR " + ex.Message); }
                return;
            }
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Resources.MergedDictionaries.Add(Ui.Load<ResourceDictionary>("FastViewer.Theme.xaml"));
            app.Resources["Accent"] = new SolidColorBrush(Ui.AccentColor());
            app.Run(new ViewerWindow(args));
        }
    }

    static class Ui
    {
        public static readonly FontFamily Icons = new FontFamily("Segoe MDL2 Assets");

        public static T Load<T>(string resource)
        {
            using (var s = typeof(Ui).Assembly.GetManifestResourceStream(resource))
                return (T)XamlReader.Load(s);
        }

        // The light shade of the Windows accent colour, which is what dark-mode controls use.
        public static Color AccentColor()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var p = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (p != null && p.Length >= 8) return Color.FromRgb(p[4], p[5], p[6]);
                }
            }
            catch { }
            return Color.FromRgb(0x60, 0xCD, 0xFF);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int StrCmpLogicalW(string a, string b);
    }

    // Puts files in the same order File Explorer shows them, by asking the open Explorer
    // window for that folder how it is sorted and grouped (Downloads: newest first).
    static class ExplorerOrder
    {
        class Row
        {
            public string File;
            public object[] Values;
            public object Group;
        }

        public static List<string> Sort(string folder, List<string> files)
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType);

            string sortSpec = null, groupSpec = null;
            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count && sortSpec == null; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    if (w == null) continue;
                    string p = w.Document.Folder.Self.Path;
                    if (!string.Equals(p.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    sortSpec = w.Document.SortColumns;
                    try { groupSpec = w.Document.GroupBy; } catch { }
                }
                catch { } // not a folder window (Control Panel, Internet Explorer...)
            }
            if (string.IsNullOrEmpty(sortSpec)) return null; // folder isn't open in Explorer: keep name order

            // "prop:-System.DateModified;System.ItemNameDisplay;"  ("-" = descending)
            var props = new List<string>();
            var descending = new List<bool>();
            string spec = sortSpec.StartsWith("prop:") ? sortSpec.Substring(5) : sortSpec;
            foreach (var part in spec.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                descending.Add(p.StartsWith("-"));
                props.Add(p.TrimStart('-', '+'));
            }

            string groupProp = null;
            bool groupDesc = false;
            if (!string.IsNullOrEmpty(groupSpec))
            {
                string g = groupSpec.Trim().TrimEnd(';');
                if (g.StartsWith("prop:")) g = g.Substring(5);
                groupDesc = g.StartsWith("-");
                g = g.TrimStart('-', '+');
                if (g.Length > 0 && g != "System.Null") groupProp = g;
                // Explorer orders the groups the same way as the column it's sorted by.
                if (groupProp != null && props.Count > 0 && props[0] == groupProp) groupDesc = descending[0];
            }

            dynamic ns = shell.NameSpace(folder);
            var rows = new List<Row>();
            foreach (var f in files)
            {
                var r = new Row { File = f, Values = new object[props.Count] };
                for (int k = 0; k < props.Count; k++) r.Values[k] = Value(ns, f, props[k]);
                if (groupProp != null) r.Group = GroupKey(Value(ns, f, groupProp));
                rows.Add(r);
            }
            rows.Sort(delegate(Row a, Row b)
            {
                int c;
                if (groupProp != null)
                {
                    c = Compare(a.Group, b.Group);
                    if (c != 0) return groupDesc ? -c : c;
                }
                for (int k = 0; k < props.Count; k++)
                {
                    c = Compare(a.Values[k], b.Values[k]);
                    if (c != 0) return descending[k] ? -c : c;
                }
                return Compare(Path.GetFileName(a.File), Path.GetFileName(b.File));
            });
            return rows.Select(r => r.File).ToList();
        }

        static object Value(dynamic ns, string file, string prop)
        {
            var fi = new FileInfo(file);
            switch (prop)
            {
                case "System.ItemNameDisplay":
                case "System.ItemNameDisplayWithoutExtension":
                case "System.FileName": return fi.Name;
                case "System.DateModified": return fi.LastWriteTime;
                case "System.DateCreated": return fi.CreationTime;
                case "System.DateAccessed": return fi.LastAccessTime;
                case "System.Size": return fi.Length;
            }
            // Anything else (Type, Date taken, Length...): ask the shell, like Explorer does.
            try
            {
                dynamic item = ns.ParseName(fi.Name);
                if (item == null) return null;
                object v = item.ExtendedProperty(prop);
                if (v is DateTime && ((DateTime)v).Kind == DateTimeKind.Utc) v = ((DateTime)v).ToLocalTime();
                return v;
            }
            catch { return null; }
        }

        // Explorer's date groups: Today, Yesterday, Earlier this week, Last week, ...
        static object GroupKey(object v)
        {
            if (!(v is DateTime)) return v;
            DateTime d = ((DateTime)v).Date, today = DateTime.Today;
            if (d > today) return 8;
            if (d == today) return 7;
            if (d == today.AddDays(-1)) return 6;
            int sinceWeekStart = ((int)today.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            DateTime weekStart = today.AddDays(-sinceWeekStart);
            if (d >= weekStart) return 5;
            if (d >= weekStart.AddDays(-7)) return 4;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            if (d >= monthStart) return 3;
            if (d >= monthStart.AddMonths(-1)) return 2;
            if (d.Year == today.Year) return 1;
            return 0;
        }

        static int Compare(object a, object b)
        {
            if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
            if (a is string && b is string) return Ui.StrCmpLogicalW((string)a, (string)b);
            if (a is DateTime && b is DateTime) return ((DateTime)a).CompareTo((DateTime)b);
            if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
            var ca = a as IComparable;
            if (ca != null && a.GetType() == b.GetType()) return ca.CompareTo(b);
            return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
        }

        static bool IsNumber(object o)
        {
            return o is int || o is long || o is uint || o is ulong || o is short || o is ushort
                || o is byte || o is double || o is float || o is decimal;
        }
    }

    // One player "slot". A few of these are stacked on top of each other; only one is visible.
    class Slot
    {
        public readonly Grid Host = new Grid();
        public readonly MediaElement Video = new MediaElement();
        public readonly Image Picture = new Image();
        public readonly Image Poster = new Image(); // first-frame snapshot shown while a video starts
        public readonly ScaleTransform Zoom = new ScaleTransform(1, 1);
        public readonly TranslateTransform Pan = new TranslateTransform();
        // Turned with a LayoutTransform, so a sideways picture is re-fitted to the window properly.
        public readonly Grid Rotor = new Grid();
        public readonly RotateTransform Rotation = new RotateTransform(0);
        public double FrameSeconds = 1 / 30.0;   // length of one video frame (for frame-by-frame)
        public GifAnimation Gif;                  // frames of an animated GIF, or null
        public int GifFrame;
        public string File;
        public bool IsVideo, Ready, Failed;
        public string Error;
        public long LastUsed;
        public int Token;
    }

    partial class ViewerWindow : Window
    {
        static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".jxr", ".wdp", ".dng" };
        static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".3gp", ".3g2", ".mts", ".m2ts", ".ts", ".mpg", ".mpeg", ".asf", ".webm" };

        static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastViewer", "settings.ini");

        const double MaxZoom = 20;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref ShFileOp op);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHOpenWithDialog(IntPtr hwnd, ref OpenAsInfo info);

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShFileOp
        {
            public IntPtr Hwnd; public uint Func; public string From; public string To; public ushort Flags;
            public bool Aborted; public IntPtr NameMappings; public string ProgressTitle;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenAsInfo { public string File; public string Class; public int Flags; }

        // 4 slots = current + previous + next + whatever is still on screen while the new one loads.
        Slot[] slots; // 2 x preload + 2: current, the files ahead and behind, and the one still on screen
        List<string> files = new List<string>();
        int index, lastDir = 1, openToken;
        Slot wanted, shown, revealing;
        readonly Stopwatch revealWatch = new Stopwatch();
        TimeSpan revealStart;
        bool renderHooked;
        long useCounter;

        bool playing, loop, muted, slideshow;
        double volume = 0.8, speed = 1;

        readonly Grid root;
        readonly WindowChrome chrome;
        readonly Grid stage, overlay, dialog;
        // Sits between the hidden players (below) and the visible one (above). Hidden players must stay
        // fully opaque: WPF stops drawing video frames for transparent elements, which caused black flashes.
        readonly Border cover = new Border();
        readonly RowDefinition topRow, bottomRow;
        readonly ColumnDefinition infoCol;
        readonly TextBlock titleText, message, playIcon, volIcon, muteIcon, loopIcon, posText, durText, volText,
                           typeText, dimText, sizeText, zoomText, fullIcon, maxIcon, dialogText;
        readonly Slider seek, vol, zoomSlider;
        readonly Border transport;
        readonly Popup volPopup;
        readonly StackPanel infoList, fileFacts;
        readonly Button volBtn, centerPlayBtn;
        readonly FrameworkElement playGlyph, pauseGlyph, scrim;
        readonly ScaleTransform centerScale = new ScaleTransform(1, 1);

        readonly DispatcherTimer uiTimer = new DispatcherTimer();
        readonly DispatcherTimer idleTimer = new DispatcherTimer();
        readonly DispatcherTimer slideTimer = new DispatcherTimer();
        bool settingSlider, overlayVisible = true, fullscreen, panning, infoOpen;
        Rect boundsBeforeFullscreen;
        WindowState stateBeforeFullscreen;
        Point lastMouse, panStart;
        DateTime volPopupClosedAt;
        int openMenus;
        bool clickToggled; // the last click on the video paused/played it (a double-click undoes that)
        // --fadetest <file>: logs when the controls show/hide (to check hiding when the mouse leaves).
        List<string> fadeTestLog;
        bool fadeTestLast = true;

        // Test modes. --selftest <ms>: presses "next" rapidly and logs switch times.
        // --snap <folder>: renders screenshots of the window off-screen.
        int selfTestInterval;
        string snapDir;
        bool testInFront; // --front: run the test on top (without taking focus) instead of behind everything
        readonly Stopwatch switchWatch = new Stopwatch();
        readonly List<string> testLog = new List<string>();

        public ViewerWindow(string[] args)
        {
            string path = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--selftest" && i + 1 < args.Length) selfTestInterval = int.Parse(args[++i]);
                else if (args[i] == "--snap" && i + 1 < args.Length) snapDir = args[++i];
                else if (args[i] == "--front") testInFront = true;
                else if (args[i] == "--update-feed" && i + 1 < args.Length) updateFeed = args[++i];
                else if (args[i] == "--update-test") updateTestAccept = true;
                else if (args[i] == "--preload" && i + 1 < args.Length) preloadOverride = int.Parse(args[++i]);
                else if (args[i] == "--features" && i + 1 < args.Length) { snapDir = args[++i]; featureTest = true; }
                else if (args[i] == "--ui" && i + 1 < args.Length) { snapDir = args[++i]; uiTest = true; }
                else if (args[i] == "--gallery" && i + 1 < args.Length) { snapDir = args[++i]; galleryTest = true; }
                else if (args[i] == "--strip") stripOverride = true;
                else if (args[i] == "--fadetest" && i + 1 < args.Length)
                {
                    string logFile = args[++i];
                    fadeTestLog = new List<string>();
                    switchWatch.Start();
                    Closing += delegate { File.WriteAllLines(logFile, fadeTestLog); };
                    After(4500, Close);
                }
                else if (path == null) path = args[i];
            }
            bool testMode = selfTestInterval > 0 || snapDir != null || fadeTestLog != null;

            Title = "FastViewer";
            Width = 1200;
            Height = 760;
            MinWidth = NormalMinWidth;
            MinHeight = NormalMinHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            AllowDrop = true;
            chrome = new WindowChrome
            {
                CaptionHeight = 48,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);
            LoadSettings(!testMode);
            if (stripOverride) showStrip = true;
            if (testMode)
            {
                // Kept behind all other windows (see SendToBack) so tests don't get in the way.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = 40;
                Top = 40;
                Width = 1440;
                Height = 760;
                ShowActivated = false;
                ShowInTaskbar = false;
                muted = true;
            }

            root = Ui.Load<Grid>("FastViewer.Window.xaml");
            Content = root;
            stage = F<Grid>("Stage");
            overlay = F<Grid>("Overlay");
            dialog = F<Grid>("Dialog");
            topRow = F<RowDefinition>("TopRow");
            bottomRow = F<RowDefinition>("BottomRow");
            infoCol = F<ColumnDefinition>("InfoCol");
            titleText = F<TextBlock>("TitleText");
            message = F<TextBlock>("Message");
            playIcon = F<TextBlock>("PlayIcon");
            volIcon = F<TextBlock>("VolIcon");
            muteIcon = F<TextBlock>("MuteIcon");
            loopIcon = F<TextBlock>("LoopIcon");
            posText = F<TextBlock>("PosText");
            durText = F<TextBlock>("DurText");
            volText = F<TextBlock>("VolText");
            typeText = F<TextBlock>("TypeText");
            dimText = F<TextBlock>("DimText");
            sizeText = F<TextBlock>("SizeText");
            zoomText = F<TextBlock>("ZoomText");
            fullIcon = F<TextBlock>("FullIcon");
            maxIcon = F<TextBlock>("MaxIcon");
            dialogText = F<TextBlock>("DialogText");
            seek = F<Slider>("Seek");
            vol = F<Slider>("Vol");
            zoomSlider = F<Slider>("ZoomSlider");
            transport = F<Border>("Transport");
            volPopup = F<Popup>("VolPopup");
            infoList = F<StackPanel>("InfoList");
            fileFacts = F<StackPanel>("FileFacts");
            volBtn = F<Button>("VolBtn");
            centerPlayBtn = F<Button>("CenterPlayBtn");
            playGlyph = F<FrameworkElement>("PlayGlyph");
            pauseGlyph = F<FrameworkElement>("PauseGlyph");
            scrim = F<FrameworkElement>("Scrim");
            centerPlayBtn.RenderTransform = centerScale;

            if (preloadOverride > 0) preloadAhead = preloadOverride; // test runs
            slots = new Slot[2 * preloadAhead + 2];
            for (int i = 0; i < slots.Length; i++)
            {
                var s = new Slot();
                s.Video.LoadedBehavior = MediaState.Manual;
                s.Video.UnloadedBehavior = MediaState.Manual;
                s.Video.ScrubbingEnabled = true;
                s.Video.Stretch = Stretch.Uniform;
                s.Video.IsMuted = true;
                s.Video.MediaOpened += delegate { OnVideoOpened(s); };
                s.Video.MediaFailed += delegate(object o, ExceptionRoutedEventArgs e) { OnLoadFailed(s, e.ErrorException); };
                s.Video.MediaEnded += delegate { OnVideoEnded(s); };
                s.Picture.Stretch = Stretch.Uniform;
                RenderOptions.SetBitmapScalingMode(s.Picture, BitmapScalingMode.HighQuality);
                var transforms = new TransformGroup();
                transforms.Children.Add(s.Zoom);
                transforms.Children.Add(s.Pan);
                s.Host.RenderTransform = transforms;
                s.Poster.Stretch = Stretch.Uniform;
                s.Poster.Visibility = Visibility.Collapsed;
                RenderOptions.SetBitmapScalingMode(s.Poster, BitmapScalingMode.HighQuality);
                s.Rotor.LayoutTransform = s.Rotation;
                s.Rotor.Children.Add(s.Video);
                s.Rotor.Children.Add(s.Poster);
                s.Rotor.Children.Add(s.Picture);
                s.Host.Children.Add(s.Rotor);
                s.Host.IsHitTestVisible = false;
                stage.Children.Add(s.Host);
                slots[i] = s;
            }
            cover.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            cover.IsHitTestVisible = false;
            Panel.SetZIndex(cover, 1);
            stage.Children.Add(cover);

            OnClick("OpenBtn", OpenDialog);
            OnClick("DeleteBtn", AskDelete);
            OnClick("CopyBtn", CopyFile);
            OnClick("RotateBtn", Rotate);
            OnClick("MiniBtn", ToggleMini);
            OnClick("MiniExitBtn", ToggleMini);
            OnClick("MiniCloseBtn", Close);
            OnClick("MoreBtn", ShowMoreMenu);
            OnClick("OpenWithBtn", OpenWith);
            OnClick("MinBtn", delegate { WindowState = WindowState.Minimized; });
            OnClick("MaxBtn", delegate { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; });
            OnClick("CloseBtn", Close);
            OnClick("PlayBtn", TogglePlay);
            OnClick("CenterPlayBtn", TogglePlay);
            OnClick("BackBtn", delegate { SeekBy(-skipBack); });
            OnClick("FwdBtn", delegate { SeekBy(skipForward); });
            OnClick("VolBtn", ToggleVolumePopup);
            OnClick("MuteBtn", ToggleMute);
            OnClick("LoopBtn", ToggleLoop);
            OnClick("VideoMoreBtn", ShowVideoMenu);
            OnClick("SlideshowBtn", ToggleSlideshow);
            OnClick("InfoBtn", ToggleInfo);
            OnClick("InfoCloseBtn", ToggleInfo);
            OnClick("FitBtn", delegate { SetZoom(1, StageCenter()); });
            OnClick("ZoomBtn", ShowZoomMenu);
            OnClick("ZoomOutBtn", delegate { ZoomBy(0.8, StageCenter()); });
            OnClick("ZoomInBtn", delegate { ZoomBy(1.25, StageCenter()); });
            OnClick("FullBtn", ToggleFullscreen);
            OnClick("DialogOk", ConfirmDelete);
            OnClick("DialogCancel", delegate { dialog.Visibility = Visibility.Collapsed; });
            // Previous/next react on press, not release: feels snappier when clicking through quickly.
            F<Button>("PrevBtn").PreviewMouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { Go(-1); e.Handled = true; };
            F<Button>("NextBtn").PreviewMouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { Go(1); e.Handled = true; };

            seek.ValueChanged += delegate
            {
                if (settingSlider || shown == null || !shown.IsVideo) return;
                shown.Video.Position = TimeSpan.FromSeconds(seek.Value);
                UpdateTime();
            };
            vol.Value = volume * 100;
            vol.ValueChanged += delegate { if (!settingSlider) SetVolume(vol.Value / 100); };
            zoomSlider.Minimum = 0;
            zoomSlider.Maximum = Math.Log(MaxZoom);
            zoomSlider.ValueChanged += delegate { if (!settingSlider) SetZoom(Math.Exp(zoomSlider.Value), StageCenter()); };
            volPopup.Closed += delegate { volPopupClosedAt = DateTime.Now; };
            volPopup.MouseWheel += delegate(object o, MouseWheelEventArgs e) { SetVolume(volume + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; };
            stage.SizeChanged += delegate
            {
                transport.Width = Math.Max(240, Math.Min(720, stage.ActualWidth - 96));
                if (shown != null) ClampPan(shown);
                UpdateZoomUi();
                UpdateScrim();
            };
            SizeChanged += delegate { UpdateResponsive(); OnMiniResized(); };
            LocationChanged += delegate { OnMiniMoved(); };

            PreviewKeyDown += OnKey;
            MouseWheel += OnWheel;
            MouseMove += OnAnyMouseMove;
            PreviewMouseDown += delegate(object o, MouseButtonEventArgs e)
            {
                if (gridOpen) { if (e.ChangedButton == MouseButton.XButton1) { CloseGrid(); e.Handled = true; } return; } // mouse back button leaves the grid
                if (e.ChangedButton == MouseButton.XButton1) { Go(-1); e.Handled = true; }
                else if (e.ChangedButton == MouseButton.XButton2) { Go(1); e.Handled = true; }
            };
            stage.MouseLeftButtonDown += OnStageDown;
            stage.MouseMove += OnStageMove;
            stage.MouseLeftButtonUp += OnStageUp;
            Drop += OnDrop;
            StateChanged += delegate { UpdateWindowFrame(); };

            uiTimer.Interval = TimeSpan.FromMilliseconds(100);
            uiTimer.Tick += delegate
            {
                UpdateTime();
                // Hide the controls as soon as the pointer is off the window, like Photos. (Checking the
                // real position is needed: Windows' "mouse left" event fires early, on the resize border.)
                if (overlayVisible && !CursorInsideWindow()) HideOverlay(true);
                if (fadeTestLog != null && overlayVisible != fadeTestLast)
                {
                    fadeTestLast = overlayVisible;
                    fadeTestLog.Add(switchWatch.ElapsedMilliseconds + " ms: controls " + (overlayVisible ? "shown" : "hidden"));
                }
            };
            uiTimer.Start();
            idleTimer.Interval = TimeSpan.FromSeconds(hideDelay > 0 ? hideDelay : 2.5);
            idleTimer.Tick += delegate { HideOverlay(); };
            slideTimer.Interval = TimeSpan.FromSeconds(slideSeconds);
            slideTimer.Tick += delegate { slideTimer.Stop(); Go(1); };

            SourceInitialized += delegate
            {
                try
                {
                    int on = 1; // dark window frame (Windows 10 20H1+ uses 20, older builds 19)
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                }
                catch { }
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
                if (testMode) SendToBack();
            };
            Closing += delegate { RememberPosition(shown); SavePositions(); SaveSettings(); };

            Loaded += delegate
            {
                message.Text = "Drop a photo, video or folder here\nor press Ctrl+O to open one";
                UpdateWindowFrame();
                InitShortcuts();
                BuildSettingsPage();
                InitExtras();
                InitGallery();
                if (!testMode) StartSingleInstance();
                StartupUpdateCheck();
                ApplyStretch();
                UpdateSkipButtons();
                UpdateControls();
                UpdateResponsive();
                if (path != null) OpenPath(path);
                if (selfTestInterval > 0) RunSelfTest();
                if (testMode) SendToBack();
                if (snapDir != null) { if (uiTest) RunUiTest(); else if (featureTest) RunFeatureTest(); else if (galleryTest) RunGalleryTest(); else RunSnapshots(); }
                idleTimer.Start();
            };
        }

        T F<T>(string name) where T : class
        {
            return (T)root.FindName(name);
        }

        void OnClick(string button, Action action)
        {
            F<Button>(button).Click += delegate { action(); };
        }

        // ---------------------------------------------------------------- opening files

        static bool IsVideoFile(string f) { return VideoExts.Contains(Path.GetExtension(f)); }
        static bool IsMedia(string f)
        {
            string ext = Path.GetExtension(f);
            return ImageExts.Contains(ext) || VideoExts.Contains(ext);
        }

        void OpenPath(string path)
        {
            string start = null, folder;
            shuffled = false;
            try
            {
                if (Directory.Exists(path)) folder = path;
                else if (File.Exists(path))
                {
                    start = Path.GetFullPath(path);
                    folder = Path.GetDirectoryName(start);
                }
                else
                {
                    message.Text = "Not found:\n" + path;
                    return;
                }

                // Only this folder (not subfolders), skipping hidden files, like Photos.
                var list = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(fi => (fi.Attributes & FileAttributes.Hidden) == 0)
                    .Select(fi => fi.FullName)
                    .Where(f => PassesFilter(f) || string.Equals(f, start, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                list.Sort((a, b) => Ui.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
                ApplyOrder(list);
                files = list;
                index = start == null ? 0
                    : Math.Max(0, files.FindIndex(f => string.Equals(f, start, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                message.Text = "Couldn't open folder:\n" + ex.Message;
                return;
            }

            if (files.Count == 0)
            {
                message.Text = "No photos or videos in this folder";
                return;
            }
            ShowIndex(index, 1);
            if (gridOpen) FillGrid(true); // a folder dropped on the grid: show that folder
            WatchFolder(folder); // new / renamed / deleted files update the list by themselves

            // Then, in the background, switch to the order File Explorer shows (asking Explorer
            // takes a moment, and we don't want to delay the first picture for it).
            int token = ++openToken;
            if (order != 0) return; // a fixed order was chosen in Settings
            var snapshot = new List<string>(files);
            var thread = new Thread(delegate()
            {
                List<string> sorted = null;
                try { sorted = ExplorerOrder.Sort(folder, snapshot); }
                catch { }
                if (sorted == null) return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (token != openToken || files.Count == 0) return;
                    string current = files[index];
                    var still = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase); // drop files deleted meanwhile
                    files = sorted.Where(still.Contains).ToList();
                    index = Math.Max(0, files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase)));
                    ShowIndex(index, lastDir);
                    if (gridOpen) FillGrid(false);
                }));
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        void OpenDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a photo or video",
                Filter = "Photos and videos|" + string.Join(";", ImageExts.Concat(VideoExts).Select(e => "*" + e)) +
                         "|All files|*.*"
            };
            if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0) OpenPath(paths[0]);
        }

        string CurrentFile { get { return files.Count == 0 ? null : files[index]; } }

        // ---------------------------------------------------------------- navigation & preloading

        int Wrap(int i)
        {
            int n = files.Count;
            return ((i % n) + n) % n;
        }

        void Go(int dir)
        {
            if (files.Count == 0) return;
            if (!wrapAround && (index + dir < 0 || index + dir >= files.Count)) return;
            ShowIndex(index + dir, dir);
        }

        void ShowIndex(int i, int dir)
        {
            if (files.Count == 0) return;
            index = Wrap(i);
            lastDir = dir;
            switchWatch.Restart();
            Thumbs.HoldOff(250); // thumbnails wait: getting the files around this one ready comes first

            // Current first, then the one you're most likely to go to next, then the one behind.
            var keep = new HashSet<Slot>();
            wanted = Ensure(files[index], keep);
            // Then the files around it: the direction you're going first (how many: Settings > Preload).
            for (int k = 1; k <= preloadAhead; k++)
            {
                if (files.Count > k) Ensure(files[Wrap(index + dir * k)], keep);
                if (files.Count > k + 1) Ensure(files[Wrap(index - dir * k)], keep);
            }
            string name = Path.GetFileName(files[index]);
            titleText.Text = name;
            Title = name + " - FastViewer";
            TryShow();
            RequestStripUpdate();
        }

        // Returns the slot holding `file`, loading it into a free slot if needed.
        // Never reuses the slot that's on screen, so the picture can't disappear mid-switch.
        Slot Ensure(string file, HashSet<Slot> keep)
        {
            foreach (var s in slots)
            {
                if (string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase))
                {
                    s.LastUsed = ++useCounter;
                    keep.Add(s);
                    return s;
                }
            }
            var victim = slots.Where(s => !keep.Contains(s) && s != shown)
                              .OrderBy(s => s.File == null ? -1 : s.LastUsed)
                              .First();
            Load(victim, file);
            victim.LastUsed = ++useCounter;
            keep.Add(victim);
            return victim;
        }

        void Load(Slot s, string file)
        {
            int token = ++s.Token;
            s.File = file;
            s.Ready = false;
            s.Failed = false;
            s.Error = null;
            s.IsVideo = IsVideoFile(file);
            ResetZoom(s);
            // Phones store some videos sideways with a "turn me" note; Windows ignores it, so apply it here.
            if (s.IsVideo && !rotations.ContainsKey(file))
            {
                int stored = VideoInfo.Rotation(file);
                if (stored != 0) rotations[file] = stored;
            }
            int angle;
            s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            s.Rotation.Angle = rotations.TryGetValue(file, out angle) ? angle : 0;

            s.Poster.Source = null;
            s.Poster.Visibility = Visibility.Collapsed;
            s.Gif = null;
            if (s.IsVideo)
            {
                s.Picture.Source = null;
                s.Picture.Visibility = Visibility.Collapsed;
                s.Video.Visibility = Visibility.Visible;
                s.Video.IsMuted = true;
                s.Video.Source = new Uri(file);
                s.Video.Pause(); // opens the file and renders the first frame, without playing
                s.FrameSeconds = VideoInfo.FrameSeconds(file);
            }
            else
            {
                s.Video.Source = null;
                s.Video.Visibility = Visibility.Collapsed;
                s.Picture.Source = null;
                s.Picture.Visibility = Visibility.Visible;
                // Decode on a background thread so the UI never stutters.
                ThreadPool.QueueUserWorkItem(delegate
                {
                    BitmapSource bmp = null;
                    string error = null;
                    GifAnimation gif = null;
                    try
                    {
                        bmp = DecodeImage(file);
                        if (Path.GetExtension(file).Equals(".gif", StringComparison.OrdinalIgnoreCase)) gif = GifAnimation.Load(file);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (s.Token != token) return; // slot was reused for another file meanwhile
                        s.Picture.Source = bmp;
                        s.Gif = gif;
                        s.Ready = true;
                        s.Failed = bmp == null;
                        s.Error = error;
                        if (s == wanted) TryShow();
                    }));
                });
            }
        }

        static BitmapSource DecodeImage(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames[0];
                BitmapSource src = frame;

                int orientation = 1;
                try
                {
                    var meta = frame.Metadata as BitmapMetadata;
                    object o = meta == null ? null : meta.GetQuery("System.Photo.Orientation");
                    if (o != null) orientation = Convert.ToInt32(o);
                }
                catch { }

                // Keep memory sane for huge panoramas.
                const int max = 6000;
                int longest = Math.Max(src.PixelWidth, src.PixelHeight);
                if (longest > max)
                {
                    double k = (double)max / longest;
                    src = new TransformedBitmap(src, new ScaleTransform(k, k));
                }

                // Phones store photos sideways and mark the rotation in EXIF; apply it.
                Transform t = null;
                switch (orientation)
                {
                    case 2: t = new ScaleTransform(-1, 1); break;
                    case 3: t = new RotateTransform(180); break;
                    case 4: t = new ScaleTransform(1, -1); break;
                    case 5: t = Group(new RotateTransform(90), new ScaleTransform(-1, 1)); break;
                    case 6: t = new RotateTransform(90); break;
                    case 7: t = Group(new RotateTransform(270), new ScaleTransform(-1, 1)); break;
                    case 8: t = new RotateTransform(270); break;
                }
                if (t != null) src = new TransformedBitmap(src, t);

                // Treat every photo as 96 dpi, so "actual size" means one photo pixel per screen pixel.
                if (Math.Abs(src.DpiX - 96) > 0.5 || Math.Abs(src.DpiY - 96) > 0.5)
                {
                    int stride = (src.PixelWidth * src.Format.BitsPerPixel + 7) / 8;
                    var px = new byte[stride * src.PixelHeight];
                    src.CopyPixels(px, stride, 0);
                    src = BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, src.Format, src.Palette, px, stride);
                }

                var result = new CachedBitmap(src, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                result.Freeze();
                return result;
            }
        }

        static Transform Group(Transform a, Transform b)
        {
            var g = new TransformGroup();
            g.Children.Add(a);
            g.Children.Add(b);
            return g;
        }

        void OnVideoOpened(Slot s)
        {
            Log("opened " + Path.GetFileName(s.File));
            if (!s.IsVideo) return;
            if (selfTestInterval > 0) testLog.Add("  opened " + Path.GetFileName(s.File) + " at " + switchWatch.ElapsedMilliseconds + " ms");
            s.Ready = true;
            if (s != shown) GoToResumePoint(s, true);
            if (s != wanted) SchedulePoster(s, 0);
            if (s == wanted) TryShow();
            if (s == shown) { UpdateControls(); UpdateFacts(); UpdateZoomUi(); }
        }

        void OnLoadFailed(Slot s, Exception ex)
        {
            s.Ready = true;
            s.Failed = true;
            string msg = ex == null ? "Unknown error" : ex.Message;
            if (selfTestInterval > 0) testLog.Add("  FAILED " + Path.GetFileName(s.File) + ": " + msg);
            if (msg.Contains("C00D5212") || msg.Contains("C00D36C4") || msg.Contains("C00D36B4") || msg.Contains("C00D11B1"))
                msg = "The video codec isn't installed on this PC.\n" +
                      "iPhone/HEVC videos need \"HEVC Video Extensions\" from the Microsoft Store.";
            s.Error = msg;
            if (s == wanted) TryShow();
        }

        void OnVideoEnded(Slot s)
        {
            Log("ended " + Path.GetFileName(s.File));
            if (s != shown) return;
            if (slideshow)
            {
                Go(1);
            }
            else if (loop)
            {
                s.Video.Position = TimeSpan.Zero;
                StartPlayback(s, true);
            }
            else if (playNextAtEnd && files.Count > 1 && (wrapAround || index < files.Count - 1))
            {
                Go(1); // Settings > Videos > When a video ends
            }
            else
            {
                playing = false;
                UpdateControls();
                ShowOverlay();
            }
        }

        // Swap the wanted slot onto the screen, but only once it's ready.
        // Until then the old picture stays up, so you never see a black frame.
        void TryShow()
        {
            var c = wanted;
            if (c == null || !c.Ready) return;
            if (c == shown)
            {
                RefreshForShown();
                return;
            }

            // A video we started for an earlier press but never got to show: stop it.
            if (revealing != null && revealing != c)
            {
                if (revealing == shown) revealing.Video.Pause(); // still on screen: just freeze it
                else StopHidden(revealing);
            }
            StopRevealWatch();
            // Freeze the old video on its current frame; it stays on screen until the new one draws.
            if (shown != null && shown.IsVideo)
            {
                shown.Video.Pause();
                shown.Video.IsMuted = true;
            }

            if (c.IsVideo && !c.Failed)
            {
                if (!autoplay)
                {
                    // Show it paused on its first frame (already drawn: it waited behind the cover).
                    c.Poster.Visibility = Visibility.Collapsed;
                    playing = false;
                    Reveal(c);
                    ShowOverlay();
                    return;
                }
                // With a first-frame snapshot we can show it instantly; otherwise it starts behind the
                // current picture and OnRendering swaps it in once it's drawing.
                bool hasPoster = c.Poster.Source != null;
                playing = true;
                StartPlayback(c, false);
                if (hasPoster) Reveal(c);
                return;
            }
            playing = false;
            Reveal(c);
        }

        // Windows' video player shows black for ~0.1 s whenever playback starts (also when resuming).
        // A picture covers that: the first-frame snapshot, or (snapshotNow) the frame on screen right
        // now. OnRendering removes it as soon as real frames are being drawn.
        void StartPlayback(Slot c, bool snapshotNow)
        {
            if (snapshotNow && c.Poster.Visibility != Visibility.Visible)
            {
                try { c.Poster.Source = Snapshot(c.Video); FitPoster(c); }
                catch { c.Poster.Source = null; }
            }
            c.Poster.Visibility = c.Poster.Source != null ? Visibility.Visible : Visibility.Collapsed;
            c.Video.Volume = volume;
            c.Video.IsMuted = muted;
            c.Video.Play();
            c.Video.SpeedRatio = speed;
            revealing = c;
            revealStart = c.Video.Position;
            Log("StartPlayback " + Path.GetFileName(c.File) + " snapshot=" + snapshotNow);
            revealWatch.Restart();
            if (!renderHooked)
            {
                CompositionTarget.Rendering += OnRendering;
                renderHooked = true;
            }
        }

        void Log(string what)
        {            if (updateTestAccept)
                try { File.AppendAllText(Assembly.GetExecutingAssembly().Location + ".update.log", DateTime.Now.ToString("HH:mm:ss.fff ") + what + Environment.NewLine); } catch { }
            if (snapDir != null)
                testLog.Add(string.Format("{0,6} ms  {1}  (playing={2}, shown={3})", switchWatch.ElapsedMilliseconds, what,
                    playing, shown == null ? "-" : Path.GetFileName(shown.File)));
        }

        void StopRevealWatch()
        {
            if (renderHooked) CompositionTarget.Rendering -= OnRendering;
            renderHooked = false;
            revealing = null;
        }

        void OnRendering(object sender, EventArgs e)
        {
            var c = revealing;
            if (c != null && c == wanted)
            {
                var played = c.Video.Position - revealStart;
                bool drawing = played >= TimeSpan.FromMilliseconds(50) && HasPicture(c.Video);
                if (!drawing && played < TimeSpan.FromMilliseconds(400) && revealWatch.ElapsedMilliseconds < 1500) return;
            }
            StopRevealWatch();
            if (c == null || c != wanted) return;
            Log("live " + Path.GetFileName(c.File));
            if (c != shown) Reveal(c);
            c.Poster.Visibility = Visibility.Collapsed;
            if (snapDir != null) testLog.Add("live video at " + switchWatch.ElapsedMilliseconds + " ms");
        }

        static BitmapSource Snapshot(MediaElement v)
        {
            if (v.NaturalVideoWidth == 0) return null;
            double k = Math.Min(1.0, 1920.0 / v.NaturalVideoWidth);
            var b = Grab(v, (int)(v.NaturalVideoWidth * k), (int)(v.NaturalVideoHeight * k));
            b.Freeze();
            return b;
        }

        // Renders the video's current picture at the given size (works while it's hidden, too).
        static RenderTargetBitmap Grab(MediaElement v, int w, int h)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(v) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return rtb;
        }

        // True when the player is drawing something other than its start-up black.
        static bool HasPicture(MediaElement v)
        {
            if (v.NaturalVideoWidth == 0) return false;
            var px = new byte[24 * 14 * 4];
            Grab(v, 24, 14).CopyPixels(px, 24 * 4, 0);
            int sum = 0;
            for (int i = 0; i < px.Length; i += 4) sum += px[i] + px[i + 1] + px[i + 2];
            return sum / (24 * 14 * 3) > 4;
        }

        // Snapshot the paused first frame of a hidden video, for an instant, flash-free start.
        void SchedulePoster(Slot s, int attempt)
        {
            int token = s.Token;
            After(attempt == 0 ? 40 : 60, delegate // check soon and often: rapid clicking needs it early
            {
                if (s.Token != token || !s.IsVideo || s.Failed || s == shown || s == revealing) return;
                var v = s.Video;
                if (v.NaturalVideoWidth == 0) return;
                if (!HasPicture(v) && attempt < 12)
                {
                    SchedulePoster(s, attempt + 1); // frame not drawn yet
                    return;
                }
                s.Poster.Source = Snapshot(v);
                FitPoster(s);
                // Visible right away (the slot is behind the cover), so it's already on the GPU when you switch.
                s.Poster.Visibility = Visibility.Visible;
            });
        }

        void Reveal(Slot c)
        {
            if (shown != null && shown != c)
            {
                // Rewind while hidden (or go to where you left off), so it's ready if you come back.
                if (shown.IsVideo)
                {
                    RememberPosition(shown);
                    StopHidden(shown);
                }
                ResetZoom(shown);
                Panel.SetZIndex(shown.Host, 0);
            }
            Panel.SetZIndex(c.Host, 2);
            var before = shown;
            shown = c;
            OnShownChanged(before);
            if (snapDir != null)
                testLog.Add("reveal " + Path.GetFileName(c.File) + " at " + switchWatch.ElapsedMilliseconds + " ms, video clock " + (c.IsVideo ? c.Video.Position.TotalMilliseconds : 0) + " ms");
            if (selfTestInterval > 0)
                testLog.Add(string.Format("{0,-40} shown after {1,4} ms", Path.GetFileName(c.File), switchWatch.ElapsedMilliseconds));
            if (slideshow) ScheduleSlide();
            RefreshForShown();
        }

        void StopHidden(Slot s)
        {
            s.Video.Pause();
            s.Video.IsMuted = true;
            GoToResumePoint(s);
            s.Poster.Visibility = Visibility.Collapsed;
            s.Poster.Source = null;
            SchedulePoster(s, 0); // fresh snapshot of the first frame for next time
        }

        void RefreshForShown()
        {
            var c = shown;
            message.Text = c.Failed ? "Can't open " + Path.GetFileName(c.File) + "\n\n" + c.Error : "";
            UpdateControls();
            UpdateFacts();
            UpdateZoomUi();
            if (mini) FitMiniWindow(false); // the mini player takes the shape of each new video
        }

        // ---------------------------------------------------------------- playback

        void TogglePlay()
        {
            Log("TogglePlay");
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            if (playing)
            {
                s.Video.Pause();
                playing = false;
                if (revealing == s) StopRevealWatch(); // keep the cover picture: it's the current frame
            }
            else
            {
                var d = s.Video.NaturalDuration;
                if (d.HasTimeSpan && s.Video.Position >= d.TimeSpan - TimeSpan.FromMilliseconds(200))
                    s.Video.Position = TimeSpan.Zero;
                StartPlayback(s, true);
                playing = true;
            }
            UpdateControls();
            ShowOverlay();

            // Little "pop" on the center button so the change feels immediate.
            var pop = new DoubleAnimation(1.15, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            centerScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            centerScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        void SeekBy(double seconds)
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            var d = s.Video.NaturalDuration;
            double pos = s.Video.Position.TotalSeconds + seconds;
            if (d.HasTimeSpan) pos = Math.Min(pos, d.TimeSpan.TotalSeconds);
            s.Video.Position = TimeSpan.FromSeconds(Math.Max(0, pos));
            UpdateTime();
        }

        void SetVolume(double v)
        {
            volume = Math.Max(0, Math.Min(1, v));
            if (volume > 0) muted = false;
            ApplyAudio();
        }

        void ToggleMute()
        {
            muted = !muted;
            ApplyAudio();
        }

        void ApplyAudio()
        {
            if (shown != null && shown.IsVideo)
            {
                shown.Video.Volume = volume;
                shown.Video.IsMuted = muted;
            }
            UpdateControls();
        }

        void ToggleLoop()
        {
            loop = !loop;
            UpdateControls();
        }

        void SetSpeed(double s)
        {
            speed = s;
            if (shown != null && shown.IsVideo && playing) shown.Video.SpeedRatio = speed;
        }

        void ToggleVolumePopup()
        {
            // A click on the button also closes the popup (it closes on any outside click); don't reopen it then.
            if ((DateTime.Now - volPopupClosedAt).TotalMilliseconds < 250) return;
            volPopup.IsOpen = !volPopup.IsOpen;
        }

        void UpdateControls()
        {
            bool isVideo = shown != null && shown.IsVideo && !shown.Failed;
            // A small mini player has no room for the control bar; play/pause stays in the middle.
            transport.Visibility = isVideo && !(mini && stage.ActualWidth < 420) ? Visibility.Visible : Visibility.Collapsed;
            centerPlayBtn.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            playGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            pauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            UpdateScrim();
            centerPlayBtn.ToolTip = playing ? "Pause (Space)" : "Play (Space)";
            playIcon.Text = playing ? "" : "";
            loopIcon.Foreground = loop ? (Brush)Application.Current.Resources["Accent"] : Brushes.White;
            string level = muted || volume == 0 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";
            volIcon.Text = level;
            muteIcon.Text = level;
            settingSlider = true;
            vol.Value = volume * 100;
            settingSlider = false;
            volText.Text = Math.Round(volume * 100).ToString(CultureInfo.InvariantCulture);
            fullIcon.Text = fullscreen ? "" : "";
            // While the grid is open the file buttons are off: the file they'd act on isn't on screen.
            bool hasFile = CurrentFile != null && !gridOpen;
            foreach (var name in new[] { "DeleteBtn", "CopyBtn", "MoreBtn", "OpenWithBtn", "SlideshowBtn", "InfoBtn", "GridBtn" })
                F<Button>(name).IsEnabled = hasFile;
            F<Button>("RotateBtn").IsEnabled = F<Button>("MiniBtn").IsEnabled = !gridOpen;
            UpdateTime();
        }

        void UpdateTime()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            var pos = s.Video.Position;
            var d = s.Video.NaturalDuration;
            var dur = d.HasTimeSpan ? d.TimeSpan : TimeSpan.Zero;
            if (!seek.IsMouseCaptureWithin) // don't fight the user while they drag
            {
                settingSlider = true;
                seek.Maximum = Math.Max(0.01, dur.TotalSeconds);
                seek.Value = Math.Min(pos.TotalSeconds, seek.Maximum);
                settingSlider = false;
            }
            posText.Text = Fmt(pos);
            durText.Text = Fmt(dur);
        }

        static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }

        // ---------------------------------------------------------------- slideshow

        void ToggleSlideshow()
        {
            if (slideshow)
            {
                StopSlideshow();
                return;
            }
            if (files.Count == 0) return;
            slideshow = true;
            if (!fullscreen) ToggleFullscreen();
            ScheduleSlide();
        }

        void ScheduleSlide()
        {
            slideTimer.Stop();
            if (!slideshow) return;
            if (shown != null && shown.IsVideo && !shown.Failed) return; // videos advance when they end
            slideTimer.Start();
        }

        void StopSlideshow()
        {
            slideshow = false;
            slideTimer.Stop();
        }

        // ---------------------------------------------------------------- file details, info pane

        static void ContentSize(Slot s, out int w, out int h)
        {
            w = h = 0;
            if (s == null || !s.Ready || s.Failed) return;
            if (s.IsVideo)
            {
                w = s.Video.NaturalVideoWidth;
                h = s.Video.NaturalVideoHeight;
            }
            else
            {
                var b = s.Picture.Source as BitmapSource;
                if (b != null) { w = b.PixelWidth; h = b.PixelHeight; }
            }
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0") + " KB";
            return bytes + " bytes";
        }

        void UpdateFacts()
        {
            var s = shown;
            if (s == null || s.Failed)
            {
                fileFacts.Visibility = Visibility.Collapsed;
                return;
            }
            fileFacts.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            typeText.Text = Path.GetExtension(s.File).TrimStart('.').ToUpperInvariant();
            int w, h;
            ContentSize(s, out w, out h);
            dimText.Text = w > 0 ? w + " x " + h : "-";
            long len = 0;
            try { len = new FileInfo(s.File).Length; }
            catch { }
            sizeText.Text = FormatSize(len);
            if (infoOpen) FillInfo();
        }

        void ToggleInfo()
        {
            infoOpen = !infoOpen;
            infoCol.Width = new GridLength(infoOpen && !fullscreen ? 320 : 0);
            if (infoOpen) FillInfo();
        }

        void FillInfo()
        {
            infoList.Children.Clear();
            string f = CurrentFile;
            if (f == null) return;
            var fi = new FileInfo(f);
            AddInfo("Name", fi.Name);
            AddInfo("Folder", fi.DirectoryName);
            try
            {
                AddInfo("Date modified", fi.LastWriteTime.ToString("f"));
                AddInfo("Date created", fi.CreationTime.ToString("f"));
                AddInfo("Size", FormatSize(fi.Length) + "  (" + fi.Length.ToString("N0") + " bytes)");
            }
            catch { }
            int w, h;
            ContentSize(shown, out w, out h);
            if (w > 0) AddInfo("Dimensions", w + " x " + h);
            if (shown != null && shown.IsVideo && shown.Video.NaturalDuration.HasTimeSpan)
                AddInfo("Length", Fmt(shown.Video.NaturalDuration.TimeSpan));
        }

        void AddInfo(string label, string value)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextSecondary"] });
            panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            infoList.Children.Add(panel);
        }

        // ---------------------------------------------------------------- file actions

        void AskDelete()
        {
            if (CurrentFile == null) return;
            if (!confirmDelete)
            {
                ConfirmDelete();
                return;
            }
            dialogText.Text = "\"" + Path.GetFileName(CurrentFile) + "\" will be moved to the Recycle Bin.";
            dialog.Visibility = Visibility.Visible;
        }

        void ConfirmDelete()
        {
            dialog.Visibility = Visibility.Collapsed;
            string f = CurrentFile;
            if (f == null) return;
            // Close the file in any player first, otherwise Windows won't let us delete it.
            foreach (var s in slots)
            {
                if (!string.Equals(s.File, f, StringComparison.OrdinalIgnoreCase)) continue;
                s.Token++;
                s.Video.Stop();
                s.Video.Close();
                s.Video.Source = null;
                s.Picture.Source = null;
                s.Poster.Source = null;
                s.File = null;
                s.Ready = false;
                Panel.SetZIndex(s.Host, 0);
                if (s == shown) shown = null;
                if (s == wanted) wanted = null;
            }
            playing = false;
            StopRevealWatch();
            TryDelete(f, 0);
        }

        void TryDelete(string f, int attempt)
        {
            var op = new ShFileOp
            {
                Hwnd = new WindowInteropHelper(this).Handle,
                Func = 3,              // FO_DELETE
                From = f + "\0",       // list must end with a double null
                Flags = 0x40 | 0x10 | 0x400 | 0x4 // allow undo (Recycle Bin), no confirm, no error UI, silent
            };
            int result = SHFileOperation(ref op);
            if (result != 0 || File.Exists(f))
            {
                if (attempt < 10)
                {
                    // The video player can take a moment to let go of the file.
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    t.Tick += delegate { t.Stop(); TryDelete(f, attempt + 1); };
                    t.Start();
                    return;
                }
                message.Text = "Couldn't delete " + Path.GetFileName(f);
                ShowIndex(index, lastDir);
                return;
            }

            int i = files.FindIndex(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) files.RemoveAt(i);
            if (files.Count == 0)
            {
                message.Text = "No more photos or videos in this folder";
                titleText.Text = "";
                UpdateControls();
                UpdateFacts();
                return;
            }
            ShowIndex(Math.Min(i < 0 ? index : i, files.Count - 1), lastDir);
        }

        void CopyFile()
        {
            if (CurrentFile == null) return;
            try { Clipboard.SetFileDropList(new StringCollection { CurrentFile }); }
            catch { }
        }

        void ShowInFolder()
        {
            if (CurrentFile != null) Process.Start("explorer.exe", "/select,\"" + CurrentFile + "\"");
        }

        void OpenWith()
        {
            if (CurrentFile == null) return;
            if (playing) TogglePlay();
            var info = new OpenAsInfo { File = CurrentFile, Flags = 0x4 }; // OAIF_EXEC: open it in the chosen app
            SHOpenWithDialog(new WindowInteropHelper(this).Handle, ref info);
        }

        // ---------------------------------------------------------------- menus

        static MenuItem Item(string header, string glyph, Action action, string shortcut = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = shortcut ?? "" };
            if (glyph != null) mi.Icon = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 14 };
            mi.Click += delegate { action(); };
            return mi;
        }

        void ShowMenu(string button, PlacementMode placement, params Control[] items)
        {
            var menu = new ContextMenu { PlacementTarget = F<Button>(button), Placement = placement, VerticalOffset = 4 };
            foreach (var i in items) menu.Items.Add(i);
            openMenus++; // a menu sits outside the window; don't fade the controls under it
            menu.Closed += delegate { openMenus--; };
            menu.IsOpen = true;
        }

        static MenuItem Heading(string text)
        {
            return new MenuItem { Header = text, Style = (Style)Application.Current.Resources["MenuHeading"] };
        }

        void ShowMoreMenu()
        {
            ShowMenu("MoreBtn", PlacementMode.Bottom,
                Heading("File"),
                Item("Save as", "\uE792", SaveAs, KeyOf("saveAs")),
                Item("Copy", "\uE8C8", CopyFile, KeyOf("copy")),
                Item("Copy file path", "\uE71B", delegate { if (CurrentFile != null) Clipboard.SetText(CurrentFile); }),
                Item("Show in folder", "\uE838", ShowInFolder),
                Item("Delete", "\uE74D", AskDelete, KeyOf("delete")),
                new Separator(),
                Heading("View"),
                Item("Rotate", "\uE7AD", Rotate, KeyOf("rotate")),
                Enabled(Item("Save rotation to file", "\uE74E", SaveRotationToFile), CanSaveRotation()),
                Item("Mini player", "\uE944", ToggleMini, KeyOf("mini")),
                Item("Keep on top", pinned ? "\uE73E" : "\uE840", TogglePin),
                Item("Slideshow", "\uE786", ToggleSlideshow, KeyOf("slideshow")),
                Item("Shuffle", shuffled ? "\uE73E" : "\uE8B1", ToggleShuffle, KeyOf("shuffle")),
                Item("All files in folder", "\uF0E2", OpenGrid, KeyOf("grid")),
                Item("File info", "\uE946", ToggleInfo, KeyOf("info")),
                new Separator(),
                Item("Settings", "\uE713", OpenSettings, KeyOf("settings")));
        }
        void ShowVideoMenu()
        {
            var items = new List<Control>();
            foreach (var s in new[] { 0.5, 1.0, 1.5, 2.0 })
            {
                double value = s;
                items.Add(Item("Speed " + value.ToString(CultureInfo.InvariantCulture) + "x",
                               Math.Abs(speed - value) < 0.01 ? "" : null, delegate { SetSpeed(value); }));
            }
            items.Add(new Separator());
            items.Add(Item("Repeat", loop ? "" : null, ToggleLoop, "R"));
            items.Add(Item("Save a frame", "", SaveFrame));
            items.Add(new Separator());
            items.Add(Item("Previous frame", "\uE76B", delegate { StepFrame(-1); }, ","));
            items.Add(Item("Next frame", "\uE76C", delegate { StepFrame(1); }, "."));
            items.Add(Item("Loop from here (A)", "\uE8EE", SetLoopStart, "["));
            items.Add(Item("Loop until here (B)", "\uE8EE", SetLoopEnd, "]"));
            if (loopA != null || loopB != null) items.Add(Item("Stop A-B loop", "\uE711", ClearLoop, "\\"));
            items.Add(new Separator());
            items.Add(Item("Trim...", "\uE7A8", StartTrim, "Ctrl+T"));
            items.Add(Item("Make compatible copy...", "\uE8C8", MakeCompatibleCopy));
            ShowMenu("VideoMoreBtn", PlacementMode.Top, items.ToArray());
        }

        void ShowZoomMenu()
        {
            ShowMenu("ZoomBtn", PlacementMode.Top,
                Item("Fit to window", "", delegate { SetZoom(1, StageCenter()); }, "0"),
                Item("Actual size (100%)", null, delegate { ZoomToPercent(100); }),
                Item("200%", null, delegate { ZoomToPercent(200); }),
                Item("400%", null, delegate { ZoomToPercent(400); }));
        }

        // ---------------------------------------------------------------- zoom & pan

        double Dpi()
        {
            var src = PresentationSource.FromVisual(this);
            return src == null || src.CompositionTarget == null ? 1 : src.CompositionTarget.TransformToDevice.M11;
        }

        // Screen pixels per image pixel when the picture fits the window.
        double FitScale(Slot s)
        {
            int w, h;
            DisplaySize(s, out w, out h);
            if (w <= 0 || h <= 0 || stage.ActualWidth <= 0) return 0;
            double fit = Math.Min(stage.ActualWidth / w, stage.ActualHeight / h);
            if (smallActualSize) fit = Math.Min(fit, 1); // small items aren't blown up (Settings)
            return fit * Dpi();
        }

        void ZoomToPercent(double percent)
        {
            double fit = FitScale(shown);
            if (fit > 0) SetZoom(percent / 100 / fit, StageCenter());
        }

        void ZoomBy(double factor, Point p)
        {
            if (shown != null) SetZoom(shown.Zoom.ScaleX * factor, p);
        }

        void SetZoom(double z, Point p)
        {
            var s = shown;
            if (s == null || !s.Ready || s.Failed) return;
            z = Math.Max(1, Math.Min(MaxZoom, z));
            double old = s.Zoom.ScaleX;
            // Keep the point under the cursor fixed while zooming.
            double x = (p.X - s.Pan.X) / old, y = (p.Y - s.Pan.Y) / old;
            s.Zoom.ScaleX = s.Zoom.ScaleY = z;
            s.Pan.X = p.X - z * x;
            s.Pan.Y = p.Y - z * y;
            ClampPan(s);
            UpdateZoomUi();
        }

        void ClampPan(Slot s)
        {
            double z = s.Zoom.ScaleX, w = stage.ActualWidth, h = stage.ActualHeight;
            s.Pan.X = Math.Max(w - w * z, Math.Min(0, s.Pan.X));
            s.Pan.Y = Math.Max(h - h * z, Math.Min(0, s.Pan.Y));
        }

        static void ResetZoom(Slot s)
        {
            s.Zoom.ScaleX = s.Zoom.ScaleY = 1;
            s.Pan.X = s.Pan.Y = 0;
        }

        void UpdateZoomUi()
        {
            var s = shown;
            double fit = FitScale(s);
            zoomText.Text = fit > 0 ? Math.Round(fit * s.Zoom.ScaleX * 100) + "%" : "-";
            settingSlider = true;
            zoomSlider.Value = s == null ? 0 : Math.Log(s.Zoom.ScaleX);
            settingSlider = false;
            UpdateScrim();
        }

        Point StageCenter()
        {
            return new Point(stage.ActualWidth / 2, stage.ActualHeight / 2);
        }

        void OnStageDown(object sender, MouseButtonEventArgs e)
        {
            bool video = shown != null && shown.IsVideo && !shown.Failed;
            if (e.ClickCount == 2)
            {
                if (video && clickToggled) TogglePlay(); // undo the first click's pause/play
                clickToggled = false;
                if (mini) ToggleMini(); else ToggleFullscreen();
                return;
            }
            clickToggled = false;
            if (shown != null && shown.Zoom.ScaleX > 1.001)
            {
                panning = true;
                panStart = e.GetPosition(stage);
                stage.CaptureMouse();
                Cursor = Cursors.SizeAll;
            }
            else if (mini && e.ButtonState == MouseButtonState.Pressed)
            {
                double x = Left, y = Top;
                try { DragMove(); } // the mini player has no title bar: drag the video to move it
                catch { }
                bool moved = Math.Abs(Left - x) > 2 || Math.Abs(Top - y) > 2;
                if (!moved && video) { TogglePlay(); clickToggled = true; } // a click without dragging: play/pause
            }
            else if (video)
            {
                TogglePlay();
                clickToggled = true;
            }
        }

        void OnStageMove(object sender, MouseEventArgs e)
        {
            if (!panning || shown == null) return;
            var p = e.GetPosition(stage);
            shown.Pan.X += p.X - panStart.X;
            shown.Pan.Y += p.Y - panStart.Y;
            ClampPan(shown);
            panStart = p;
        }

        void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            if (!panning) return;
            panning = false;
            stage.ReleaseMouseCapture();
            Cursor = null;
        }

        // ---------------------------------------------------------------- window, full screen

        void UpdateWindowFrame()
        {
            maxIcon.Text = WindowState == WindowState.Maximized ? "" : "";
            // A maximized window hangs over the screen edge by its (invisible) border; pad it back in.
            if (WindowState == WindowState.Maximized && !fullscreen)
            {
                double d = Dpi();
                double frame = (GetSystemMetrics(32) + GetSystemMetrics(92)) / d; // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                root.Margin = new Thickness(frame);
            }
            else root.Margin = new Thickness(0);
        }

        // Full screen: the top and bottom bars float over the picture and fade with the other controls.
        void PlaceBars()
        {
            var top = F<FrameworkElement>("TopBar");
            var bottom = F<FrameworkElement>("BottomBar");
            Grid.SetRow(top, fullscreen ? 1 : 0);
            Grid.SetRow(bottom, fullscreen ? 1 : 2);
            top.VerticalAlignment = fullscreen ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            bottom.VerticalAlignment = fullscreen ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            top.Height = fullscreen ? 48 : double.NaN;
            Panel.SetZIndex(top, fullscreen ? 10 : 0);
            Panel.SetZIndex(bottom, fullscreen ? 10 : 0);
            transport.Margin = new Thickness(0, 0, 0, fullscreen ? (showStrip ? 48 + 64 : 48) : 16);
            foreach (var bar in new[] { top, bottom })
            {
                bar.BeginAnimation(OpacityProperty, null);
                bar.Opacity = !fullscreen || overlayVisible ? 1 : 0;
                bar.IsHitTestVisible = !fullscreen || overlayVisible;
            }
        }

        void FadeBars(bool show)
        {
            if (!fullscreen) return;
            foreach (var name in new[] { "TopBar", "BottomBar" })
            {
                var bar = F<FrameworkElement>(name);
                bar.IsHitTestVisible = show; // hidden buttons mustn't be clickable
                bar.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 300)));
            }
        }

        void ToggleFullscreen()
        {
            if (!fullscreen && mini) ToggleMini(); // full screen goes from the normal window
            if (!fullscreen)
            {
                stateBeforeFullscreen = WindowState;
                boundsBeforeFullscreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                fullscreen = true;
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
                topRow.Height = new GridLength(0);
                bottomRow.Height = new GridLength(0);
                infoCol.Width = new GridLength(0);
                stage.Background = Brushes.Black;
                cover.Background = Brushes.Black;

                var hwnd = new WindowInteropHelper(this).Handle;
                var mi = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi);
                double d = Dpi();
                WindowState = WindowState.Normal;
                Left = mi.Monitor.Left / d;
                Top = mi.Monitor.Top / d;
                Width = (mi.Monitor.Right - mi.Monitor.Left) / d;
                Height = (mi.Monitor.Bottom - mi.Monitor.Top) / d;
                ResizeMode = ResizeMode.NoResize;
                PlaceBars();
            }
            else
            {
                fullscreen = false;
                StopSlideshow();
                chrome.CaptionHeight = 48;
                chrome.ResizeBorderThickness = new Thickness(6);
                topRow.Height = new GridLength(48);
                bottomRow.Height = GridLength.Auto;
                infoCol.Width = new GridLength(infoOpen ? 320 : 0);
                stage.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                cover.Background = stage.Background;
                ResizeMode = ResizeMode.CanResize;
                Left = boundsBeforeFullscreen.Left;
                Top = boundsBeforeFullscreen.Top;
                Width = boundsBeforeFullscreen.Width;
                Height = boundsBeforeFullscreen.Height;
                WindowState = stateBeforeFullscreen;
                Cursor = null;
                PlaceBars();
            }
            UpdateWindowFrame();
            UpdateControls();
        }

        // ---------------------------------------------------------------- input

        void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (CaptureKey(e)) // choosing a new shortcut in Settings
            {
                e.Handled = true;
                return;
            }
            if (settingsOpen)
            {
                if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
                return;
            }
            if (gridOpen)
            {
                if (HandleGridKey(e)) e.Handled = true;
                return;
            }
            if (dialog.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Enter) ConfirmDelete();
                else if (e.Key == Key.Escape) dialog.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape) // always: leave mini player / slideshow / full screen
            {
                if (mini) ToggleMini();
                if (slideshow) StopSlideshow();
                if (fullscreen) ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (HandleShortcut(e)) e.Handled = true; // see Shortcuts.cs; changeable in Settings > Keyboard
        }
        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (volPopup.IsOpen || gridOpen) return;
            if (transport.IsMouseOver && transport.Visibility == Visibility.Visible)
            {
                // Over the control bar: volume on the volume button, jump on the time bar, nothing elsewhere.
                if (volBtn.IsMouseOver) SetVolume(volume + (e.Delta > 0 ? 0.05 : -0.05));
                else if (seek.IsMouseOver) SeekBy(e.Delta > 0 ? 5 : -5);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || wheelZooms) // Ctrl+wheel always zooms, like Photos
                ZoomBy(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(stage));
            else
                Go(e.Delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        void OnAnyMouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - lastMouse.X) < 2 && Math.Abs(p.Y - lastMouse.Y) < 2) return; // ignore jitter
            lastMouse = p;
            ShowOverlay();
        }

        // Any mouse movement anywhere in the window brings the controls back; they fade after 2.5 s of stillness.
        void ShowOverlay()
        {
            if (!overlayVisible)
            {
                overlayVisible = true;
                overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
                FadeBars(true);
            }
            if (!panning) Cursor = null;
            idleTimer.Stop();
            idleTimer.Start();
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out NativePoint p);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out NativeRect r);
        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }

        bool CursorInsideWindow()
        {
            NativePoint p;
            NativeRect r;
            if (!GetCursorPos(out p) || !GetWindowRect(new WindowInteropHelper(this).Handle, out r)) return false;
            return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        }

        // leftWindow: the mouse moved off the window, so hide right away (even when paused), like Photos.
        void HideOverlay(bool leftWindow = false)
        {
            idleTimer.Stop();
            if (hideDelay <= 0) return; // "Never" in Settings
            bool pausedVideo = shown != null && shown.IsVideo && !shown.Failed && !playing; // keep controls up while paused
            bool pointerOnControls = transport.IsMouseOver || centerPlayBtn.IsMouseOver;
            if (snapDir != null || volPopup.IsOpen || openMenus > 0 || files.Count == 0 || !overlayVisible || gridOpen) return;
            if (!leftWindow && (pausedVideo || pointerOnControls)) return;
            overlayVisible = false;
            overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            FadeBars(false);
            if (fullscreen) Cursor = Cursors.None;
        }

        // ---------------------------------------------------------------- settings

        void LoadSettings(bool applyWindow)
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    var kv = line.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    var v = kv[1].Trim();
                    switch (kv[0].Trim())
                    {
                        case "volume": volume = double.Parse(v, CultureInfo.InvariantCulture); break;
                        case "muted": muted = v == "1"; break;
                        case "loop": loop = v == "1"; break;
                        default: LoadOption(kv[0].Trim(), v); break;
                        case "maximized": if (applyWindow && v == "1") WindowState = WindowState.Maximized; break;
                        case "bounds":
                            if (!applyWindow) break;
                            var r = Rect.Parse(v);
                            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                            if (r.Width >= MinWidth && r.Height >= MinHeight && screen.IntersectsWith(r))
                            {
                                WindowStartupLocation = WindowStartupLocation.Manual;
                                Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
                            }
                            break;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            if (selfTestInterval > 0 || snapDir != null || fadeTestLog != null) return;
            try
            {
                var bounds = mini ? boundsBeforeMini : fullscreen ? boundsBeforeFullscreen : RestoreBounds;
                bool max = mini ? stateBeforeMini == WindowState.Maximized
                    : fullscreen ? stateBeforeFullscreen == WindowState.Maximized : WindowState == WindowState.Maximized;
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllLines(SettingsFile, new[]
                {
                    "volume=" + volume.ToString(CultureInfo.InvariantCulture),
                    "muted=" + (muted ? "1" : "0"),
                    "loop=" + (loop ? "1" : "0"),
                    "maximized=" + (max ? "1" : "0"),
                    "bounds=" + bounds.ToString(CultureInfo.InvariantCulture)
                }.Concat(OptionLines()).Concat(ShortcutLines()));
            }
            catch { }
        }

        // ---------------------------------------------------------------- tests

        void RunSelfTest()
        {
            int presses = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(selfTestInterval) };
            t.Tick += delegate
            {
                if (presses == 0) testLog.Add("--- next x20 every " + selfTestInterval + " ms");
                if (presses == 20) testLog.Add("--- previous x10");
                if (presses < 20) Go(1);
                else if (presses < 30) Go(-1);
                else
                {
                    t.Stop();
                    testLog.Add("--- final order: " + string.Join(", ", files.Select(Path.GetFileName)));
                    File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"), testLog);
                    Close();
                    return;
                }
                presses++;
            };
            After(1500, t.Start);
        }

        void RunSnapshots()
        {
            Directory.CreateDirectory(snapDir);
            var steps = new List<KeyValuePair<int, Action>>();
            Action<int, Action> at = (ms, a) => steps.Add(new KeyValuePair<int, Action>(ms, a));
            at(2000, () => Snap("1_playing"));
            at(2050, () => TogglePlay());
            at(2500, () => Snap("2_paused"));
            at(2550, () => { switchWatch.Restart(); TogglePlay(); });
            foreach (int ms in new[] { 16, 40, 70, 100, 150 })
            {
                int t = ms;
                at(2550 + t, () => Snap("3_resume_" + t.ToString("000") + "ms"));
            }
            at(3500, () => Go(1));
            foreach (int ms in new[] { 16, 40, 70, 100, 150 })
            {
                int t = ms;
                at(3500 + t, () => Snap("4_next_" + t.ToString("000") + "ms"));
            }
            at(4250, () => Rotate());
            at(4550, () => Snap("4r_video_rotated"));
            at(4600, () => { int photo = files.FindIndex(f => !IsVideoFile(f)); if (photo >= 0) ShowIndex(photo, 1); });
            at(5700, () => Rotate());
            at(5750, () => Snap("5_photo_mid_turn"));
            at(6000, () => Snap("5_photo_rotated"));
            at(5800, () => ToggleInfo());
            at(6200, () => Snap("6_info"));
            at(6300, () => { ToggleInfo(); AskDelete(); }); // only shows the dialog; nothing is deleted
            at(6700, () => Snap("7_delete_dialog"));
            at(6800, () => { dialog.Visibility = Visibility.Collapsed; OpenSettings(); });
            at(7300, () => Snap("8_settings"));
            at(7400, () => { CloseSettings(); Width = 560; });
            at(7900, () => Snap("9_narrow"));
            Action logSize = () => testLog.Add(string.Format("window {0:0}x{1:0} at {2:0},{3:0}, topmost={4}", ActualWidth, ActualHeight, Left, Top, Topmost));
            at(8000, () => ToggleMini());
            at(8600, () => { Snap("10_mini_photo"); logSize(); });
            at(8700, () => { int v = files.FindIndex(f => f.EndsWith("portrait.mp4")); if (v >= 0) ShowIndex(v, 1); });
            at(9500, () => { Snap("11_mini_portrait_video"); logSize(); });
            at(9600, () => Rotate());
            at(10200, () => { Snap("12_mini_rotated"); logSize(); });
            at(10300, () => ToggleMini());
            at(10800, () => { Snap("13_back_to_window"); logSize(); });
            at(11000, () =>
            {
                File.WriteAllLines(Path.Combine(snapDir, "order.txt"), files.Select(Path.GetFileName));
                File.WriteAllLines(Path.Combine(snapDir, "log.txt"), testLog);
                Close();
            });
            foreach (var s in steps) After(s.Key, s.Value);
        }
        void After(int ms, Action action)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); action(); };
            t.Start();
        }

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        void SendToBack()
        {
            if (testInFront)
            {
                Topmost = true;
                return;
            }
            // HWND_BOTTOM; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
            SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        }

        // Captures the window's real on-screen pixels (including video frames), even off-screen.
        void Snap(string name)
        {
            testLog.Add("snap " + name + " at " + switchWatch.ElapsedMilliseconds + " ms" +
                (revealing != null ? ", hidden clip clock " + revealing.Video.Position.TotalMilliseconds + " ms" : ""));
            double d = Dpi();
            int w = (int)Math.Round(ActualWidth * d), h = (int)Math.Round(ActualHeight * d);
            using (var bmp = new System.Drawing.Bitmap(w, h))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(new WindowInteropHelper(this).Handle, hdc, 2); // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(Path.Combine(snapDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
