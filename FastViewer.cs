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
            if (args.Length >= 4 && args[0] == "--isolate") { Environment.ExitCode = Vocals.Child(args); return; } // the hidden helper of voice isolation (Vocals.cs)
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
            if (args.Length >= 3 && args[0] == "--speechwav") // test: the sound of <video> as the .wav captions are made from
            {
                try { File.WriteAllText(args[2] + ".log", "OK " + (args.Length >= 5 ? VideoConverter.SpeechWav(args[1], args[2], 0, null, null, int.Parse(args[3]), int.Parse(args[4])) : VideoConverter.SpeechWav(args[1], args[2], 0, null, null)).ToString(CultureInfo.InvariantCulture) + " s"); }
                catch (Exception ex) { File.WriteAllText(args[2] + ".log", "ERROR " + ex); }
                return;
            }
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
            Motion.UseScreenRate(); // animations as smooth as the screen (120/144 Hz too)
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            // If FastViewer ever crashes, the reason is kept in %TEMP%\FastViewer\crash.txt.
            app.DispatcherUnhandledException += (o, e) => WriteCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (o, e) => WriteCrash(e.ExceptionObject as Exception);
            app.Resources.MergedDictionaries.Add(Ui.Load<ResourceDictionary>("FastViewer.Theme.xaml")); // the window applies its look (Looks.cs)
            app.Run(new ViewerWindow(args));
        }

        public static void WriteCrash(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "FastViewer");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.txt"), DateTime.Now + " FastViewer " +
                    Assembly.GetExecutingAssembly().GetName().Version + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
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

        // The Windows accent colour: its light shade for dark looks (what dark-mode controls use), its dark
        // shade for light looks.
        public static byte[] TestPalette; // tests: an accent palette instead of Windows' own
        public static Color AccentColor(bool forLight = false)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var p = TestPalette ?? (key == null ? null : key.GetValue("AccentPalette") as byte[]);
                    int i = forLight ? 16 : 4;
                    if (p != null && p.Length >= i + 3)
                    {
                        // A grey accent (Windows allows it) makes accent buttons and switches look switched off: use blue then.
                        int max = Math.Max(p[i], Math.Max(p[i + 1], p[i + 2])), min = Math.Min(p[i], Math.Min(p[i + 1], p[i + 2]));
                        if (max - min >= 28) return Color.FromRgb(p[i], p[i + 1], p[i + 2]);
                    }
                }
            }
            catch { }
            return forLight ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF);
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
        public MediaElement Video;              // made by NewVideoPlayer
        public readonly Image Picture = new Image();
        public readonly Image Poster = new Image(); // first-frame snapshot shown while a video starts
        public readonly ScaleTransform Zoom = new ScaleTransform(1, 1);
        public readonly TranslateTransform Pan = new TranslateTransform();
        // Turned with a LayoutTransform, so a sideways picture is re-fitted to the window properly.
        public readonly Grid Rotor = new Grid();
        public readonly RotateTransform Rotation = new RotateTransform(0);
        public double FrameSeconds = 1 / 30.0;   // length of one video frame (for frame-by-frame)
        public GifAnimation Gif;                  // frames of an animated GIF, or null (only while it's on screen)
        public int GifState;                      // 0 not looked at yet, 1 loading the frames, 2 a still GIF
        public int GifFrame;
        public int FullWidth, FullHeight;         // photo size in the file (the picture in memory may be smaller)
        public bool Reduced;                      // the picture in memory is screen-sized; zooming in loads the full one
        public string File;
        public bool IsVideo, Ready, Failed;
        public bool IsAudio;                      // music: plays in the video player (IsVideo too), shows its cover card in Picture
        public BitmapSource AudioCover;           // the album cover from Windows, or null
        public string AudioTitle, AudioArtist;
        public TimeSpan? SeekOnOpen;              // where to continue once the player has opened the file again (after a cover change)
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
        static readonly HashSet<string> AudioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma" };

        static string SettingsFile = Path.Combine( // --settings <file>: tests save to a scratch file instead
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
        readonly MessageText message; // the middle-of-the-window message: setting its Text arranges the icon, headline and buttons
        internal class MessageText
        {
            string text = "";
            public Action Changed;
            public string Text { get { return text; } set { value = value ?? ""; if (value == text) return; text = value; if (Changed != null) Changed(); } }
        }
        readonly TextBlock titleText, posText, durText, volText, typeText, dimText, sizeText, zoomText, dialogText;
        readonly Glyph playIcon, volIcon, muteIcon, loopIcon, fullIcon, maxIcon;
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
        Vector panVelocity;
        readonly Stopwatch panClock = new Stopwatch();
        Rect boundsBeforeFullscreen;
        WindowState stateBeforeFullscreen;
        Point lastMouse, panStart;
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
                else if (args[i] == "--update-page" && i + 1 < args.Length) updatePage = args[++i];
                else if (args[i] == "--updated") { } // restarted by the updater (see InstallUpdate)
                else if (args[i] == "--motion" && i + 1 < args.Length) { var m = args[++i].ToLowerInvariant(); motionOverride = m == "full" ? "Full" : m == "reduced" ? "Reduced" : "Off"; }
                else if (args[i] == "--positions" && i + 1 < args.Length) { PositionsFile = args[++i]; positionsOverride = true; }
                else if (args[i] == "--rotations" && i + 1 < args.Length) { TurnMemory.Path = args[++i]; TurnMemory.Overridden = true; }
                else if (args[i] == "--addons" && i + 1 < args.Length) Addon.RootOverride = args[++i];
                else if (args[i] == "--vocals") Vocals.Offered = true;
                else if (args[i] == "--addon-source" && i + 1 < args.Length) Addon.SourceOverride = args[++i];
                else if (args[i] == "--gpu" && i + 1 < args.Length) GpuVerdict.Override = args[++i];
                else if (args[i] == "--frameboost" && i + 1 < args.Length) Smooth.OwnOverride = args[++i];
                else if (args[i] == "--preload" && i + 1 < args.Length) preloadOverride = int.Parse(args[++i]);
                else if (args[i] == "--features" && i + 1 < args.Length) { snapDir = args[++i]; featureTest = true; }
                else if (args[i] == "--ui" && i + 1 < args.Length) { snapDir = args[++i]; uiTest = true; }
                else if (args[i] == "--gallery" && i + 1 < args.Length) { snapDir = args[++i]; galleryTest = true; }
                else if (args[i] == "--controls" && i + 1 < args.Length) { snapDir = args[++i]; controlsTest = true; }
                else if (args[i] == "--tweaks" && i + 1 < args.Length) { snapDir = args[++i]; tweaksTest = true; }
                else if (args[i] == "--memory" && i + 1 < args.Length) { snapDir = args[++i]; memoryTest = true; }
                else if (args[i] == "--stall" && i + 1 < args.Length) { snapDir = args[++i]; stallTest = true; }
                else if (args[i] == "--strip") stripOverride = true;
                else if (args[i] == "--grab-always") grabAlways = true;
                else if (args[i] == "--look" && i + 1 < args.Length) lookOverride = args[++i];
                else if (args[i] == "--looks" && i + 1 < args.Length) { snapDir = args[++i]; looksTest = true; }
                else if (args[i] == "--volume" && i + 1 < args.Length) { snapDir = args[++i]; volumeTest = true; }
                else if (args[i] == "--dim" && i + 1 < args.Length) { snapDir = args[++i]; dimTest = true; }
                else if (args[i] == "--sync" && i + 1 < args.Length) { snapDir = args[++i]; syncTest = true; }
                else if (args[i] == "--toast" && i + 1 < args.Length) { snapDir = args[++i]; toastTest = true; }
                else if (args[i] == "--hunt" && i + 1 < args.Length) { snapDir = args[++i]; huntTest = true; TestBin = Path.Combine(snapDir, "recycled"); }
                else if (args[i] == "--real-bin") RealBin = true; // a test that really uses the Recycle Bin
                else if (args[i] == "--case" && i + 1 < args.Length) huntCase = args[++i];
                else if (args[i] == "--pattern" && i + 1 < args.Length) huntPattern = args[++i];
                else if (args[i] == "--count" && i + 1 < args.Length) huntCount = int.Parse(args[++i]);
                else if (args[i] == "--every" && i + 1 < args.Length) huntEvery = int.Parse(args[++i]);
                else if (args[i] == "--settings" && i + 1 < args.Length) { SettingsFile = args[++i]; settingsOverride = true; }
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
            testRun = args.Any(a => a.StartsWith("--") && a != "--updated"); // any test option: GitHub is never contacted
            Addon.TestRun = Smooth.TestRun = testRun; // tests never see (or download) the real add-ons
            if (testRun && !TurnMemory.Overridden) TurnMemory.Path = null; // tests never touch the real rotations.txt

            Title = "FastViewer";
            Width = 1200;
            Height = 760;
            MinWidth = NormalMinWidth;
            MinHeight = NormalMinHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetResourceReference(BackgroundProperty, "WindowBg");
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
            if (lookOverride != null) lookId = lookOverride;
            var lookWatch = Stopwatch.StartNew();
            ApplyLook(); // before the window's layout loads, so it starts in the right look
            if (testMode) testLog.Add("look " + lookId + " applied in " + lookWatch.ElapsedMilliseconds + " ms");
            if (testMode)
            {
                // Kept out of sight so tests don't get in the way: cloaked (see KeepTestOutOfSight), never on top,
                // and behind other windows.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = 40;
                Top = 40;
                Width = 1440;
                Height = 760;
                ShowActivated = false;
                ShowInTaskbar = false;
                muted = true;
                KeepTestOutOfSight();
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
            message = new MessageText();
            message.Changed = ArrangeMessage;

            playIcon = F<Glyph>("PlayIcon");
            volIcon = F<Glyph>("VolIcon");
            muteIcon = F<Glyph>("MuteIcon");
            loopIcon = F<Glyph>("LoopIcon");
            posText = F<TextBlock>("PosText");
            durText = F<TextBlock>("DurText");
            volText = F<TextBlock>("VolText");
            typeText = F<TextBlock>("TypeText");
            dimText = F<TextBlock>("DimText");
            sizeText = F<TextBlock>("SizeText");
            zoomText = F<TextBlock>("ZoomText");
            fullIcon = F<Glyph>("FullIcon");
            maxIcon = F<Glyph>("MaxIcon");
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
            for (int i = 0; i < slots.Length; i++) slots[i] = NewSlot();
            cover.SetResourceReference(Border.BackgroundProperty, "CoverBg");
            cover.IsHitTestVisible = false;
            Panel.SetZIndex(cover, 1);
            stage.Children.Add(cover);

            OnClick("OpenBtn", OpenDialog);
            OnClick("MessageOpenBtn", OpenDialog);
            OnClick("DeleteBtn", AskDelete);
            OnClick("CopyBtn", CopyFile);
            OnClick("RotateBtn", delegate { RotateButton(false); });
            F<Button>("RotateBtn").PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; RotateButton(true); };
            F<Button>("RotateBtn").PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; }; // (nothing else acts on that press)
            OnClick("MiniBtn", ToggleMiniSmoothly);
            OnClick("MiniExitBtn", ToggleMiniSmoothly);
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
            OnClick("VolBtn", ToggleMute); // the slider opens on hover (InitVolumeHover)
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
            OnClick("FullBtn", ToggleFullscreenSmoothly);
            OnClick("DialogOk", ConfirmDelete);
            OnClick("DialogCancel", HideDeleteDialog);
            OnClick("MessageBtn", OpenWith);
            // The file name sits in the title bar: it moves the window (and double-click maximizes) like the rest of it.
            titleText.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
            {
                if (fullscreen || mini) return;
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else try { DragMove(); } catch { }
            };
            // Previous/next react on press, not release: feels snappier when clicking through quickly.
            F<Button>("PrevBtn").PreviewMouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { Go(-1); e.Handled = true; };
            F<Button>("NextBtn").PreviewMouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { Go(1); e.Handled = true; };
            // Pointing at a side arrow: its chevron leans the way it goes.
            foreach (var arrow in new[] { "Prev", "Next" })
            {
                var glyph = F<FrameworkElement>(arrow + "Glyph");
                double lean = arrow == "Prev" ? -3 : 3;
                F<Button>(arrow + "Btn").MouseEnter += delegate { if (Motion.Moves) Motion.To(Motion.PartsOf(glyph).Move, TranslateTransform.XProperty, lean, Motion.Ms(Motion.Fast), Motion.In); };
                F<Button>(arrow + "Btn").MouseLeave += delegate { Motion.To(Motion.PartsOf(glyph).Move, TranslateTransform.XProperty, 0, Motion.Ms(Motion.Fast), Motion.Smooth); };
            }

            seek.PreviewMouseLeftButtonDown += delegate { StartScrub(); }; // your click or drag wins over the gliding dot
            seek.ValueChanged += delegate
            {
                if (settingSlider || shown == null || !shown.IsVideo) return; // (the dot moving with the video)
                if (scrubbing) { ScrubTo(seek.Value, false); return; }
                shown.Video.Position = TimeSpan.FromSeconds(seek.Value);
                UpdateTime();
            };
            // The drag ends wherever the mouse is let go (also off the time bar, or when the window loses the mouse).
            PreviewMouseLeftButtonUp += delegate { EndScrub(); };
            seek.LostMouseCapture += delegate { if (!seek.IsMouseCaptureWithin && Mouse.LeftButton != MouseButtonState.Pressed) EndScrub(); };
            scrubTimer.Tick += delegate { scrubTimer.Stop(); if (scrubbing) ScrubTo(scrubTarget, true); };
            vol.Value = volume * 100;
            vol.ValueChanged += delegate { if (!settingSlider) SetVolume(vol.Value / 100); };
            zoomSlider.Minimum = 0;
            zoomSlider.Maximum = Math.Log(MaxZoom);
            zoomSlider.ValueChanged += delegate { if (!settingSlider) SetZoom(Math.Exp(zoomSlider.Value), StageCenter()); };
            InitVolumeHover();
            volPopup.MouseWheel += delegate(object o, MouseWheelEventArgs e) { SetVolume(volume + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; };
            stage.SizeChanged += delegate
            {
                FitSmallControls();
                transport.Width = Math.Max(240, Math.Min(720, stage.ActualWidth - (compactStage ? 80 : 112))); // clear of the side arrows
                if (trimming) SizeTrimBar();
                UpdateControls(); // which control-bar buttons fit
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
                string button = e.ChangedButton == MouseButton.XButton1 ? "back" : e.ChangedButton == MouseButton.XButton2 ? "forward"
                              : e.ChangedButton == MouseButton.Middle ? "middle" : null;
                if (button == null) return;
                if (DialogOpen) { e.Handled = true; return; }
                if (gridOpen || settingsOpen) // the back button leaves the grid or Settings, like a browser
                {
                    if (button == "back") { if (gridOpen) CloseGrid(); else CloseSettings(); e.Handled = true; }
                    return;
                }
                if (RunMouseAction(button)) e.Handled = true; // Settings > Mouse
            };
            stage.MouseLeftButtonDown += OnStageDown;
            stage.MouseMove += OnStageMove;
            stage.MouseLeftButtonUp += OnStageUp;
            stage.MouseRightButtonUp += OnStageRightUp; // music: right-click the cover to change it
            Drop += OnDrop;
            // Dragging a file over the window: an outline shows it will open here.
            DragEnter += delegate(object o, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) Motion.Show(F<FrameworkElement>("DropOutline"), 0, 0, 0.98, Motion.Fast); };
            DragLeave += delegate(object o, DragEventArgs e)
            {
                var p = e.GetPosition(this);
                if (p.X <= 1 || p.Y <= 1 || p.X >= ActualWidth - 1 || p.Y >= ActualHeight - 1) Motion.Hide(F<FrameworkElement>("DropOutline"), 0, 0, 1, Motion.Fast);
            };
            StateChanged += delegate { UpdateWindowFrame(); RoundCorners(); };
            SizeChanged += delegate { RoundCorners(); };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (o, e) => // Windows switched dark/light: follow it ("Auto")
            {
                Dispatcher.BeginInvoke(new Action(ApplyMotionLevel)); // Windows' "Show animations" may have changed
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && lookMode == "Auto")
                    Dispatcher.BeginInvoke(new Action(ApplyLook));
            };

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
            slideTimer.Tick += delegate
            {
                if (DialogOpen) return; // waits while "Delete this file?" is open
                slideTimer.Stop();
                Go(1);
            };

            SourceInitialized += delegate
            {
                ApplyWindowLook(); // dark or light frame, see-through for Liquid Glass
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
                if (testMode) SendToBack();
            };
            Closing += delegate { SaveWaitingRotations(); RememberPosition(shown); SavePositions(); SaveSettings(true); };
            Activated += delegate // another window may have changed a setting, or saved where you were in a video
            {
                SyncSettings();
                if (positionsLoaded) ReloadPositions();
            };

            Loaded += delegate
            {
                if (path == null) message.Text = EmptyMessage(); // (opening a file: no "drop a photo here" screen built first)
                UpdateWindowFrame();
                InitShortcuts();
                InitTaskbarButtons(); // previous, play/pause, next under the taskbar preview
                BuildSettingsPage();
                InitExtras();
                InitGallery();
                InitGridSearch();
                InitAway();
                foreach (var name in new[] { "GridScroll", "SettingsScroll", "SettingsNavScroll" }) SmoothWheel(F<ScrollViewer>(name));
                ApplyButtons();
                if (!testMode) StartSingleInstance();
                if (!testRun) Dispatcher.BeginInvoke(new Action(HideThumbnailBadge), DispatcherPriority.ApplicationIdle); // after the first picture
                StartupUpdateCheck();
                ApplyStretch();
                UpdateSkipButtons();
                UpdateControls();
                UpdateResponsive();
                if (path != null) OpenPath(path);
                if (selfTestInterval > 0) RunSelfTest();
                if (testMode) SendToBack();
                if (snapDir != null) { if (huntTest) RunHunt(); else if (uiTest) RunUiTest(); else if (featureTest) RunFeatureTest(); else if (galleryTest) RunGalleryTest(); else if (controlsTest) RunControlsTest(); else if (tweaksTest) RunTweaksTest(); else if (memoryTest) RunMemoryTest(); else if (stallTest) RunStallTest(); else if (looksTest) RunLooksTest(); else if (volumeTest) RunVolumeTest(); else if (dimTest) RunDimTest(); else if (syncTest) RunSyncTest(); else if (toastTest) RunToastTest(); else RunSnapshots(); }
                idleTimer.Start();
            };
        }

        // One player slot, added to the stage (below the cover).
        Slot NewSlot()
        {
            var s = new Slot();
            NewVideoPlayer(s);
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
            s.Rotor.Children.Add(s.Poster);
            s.Rotor.Children.Add(s.Picture);
            s.Host.Children.Add(s.Rotor);
            s.Host.IsHitTestVisible = false;
            stage.Children.Insert(0, s.Host);
            if (glassSlots) s.Host.Opacity = HiddenOpacity;
            return s;
        }

        // Settings > Preload: takes effect right away. More slots are added; spare ones are closed.
        void SetPreload(int ahead)
        {
            if (preloadOverride > 0) { preloadAhead = preloadOverride; return; } // test runs keep theirs
            preloadAhead = Math.Max(1, Math.Min(3, ahead));
            int want = 2 * preloadAhead + 2;
            var list = slots.ToList();
            while (list.Count < want) list.Add(NewSlot());
            foreach (var s in list.Where(x => x != shown && x != wanted && x != revealing).OrderBy(x => x.File == null ? -1 : x.LastUsed).ToList())
            {
                if (list.Count <= want) break;
                s.Token++;
                s.Video.Stop();
                s.Video.Close();
                s.Video.Source = null;
                s.Picture.Source = null;
                s.Poster.Source = null;
                s.Gif = null;
                stage.Children.Remove(s.Host);
                list.Remove(s);
            }
            slots = list.ToArray();
            if (files.Count > 0) ShowIndex(index, lastDir); // gets the new number of files ready
        }

        // A video player for the slot, at the bottom of its layers (below the first-frame picture and the photo).
        void NewVideoPlayer(Slot s)
        {
            if (s.Video != null) s.Rotor.Children.Remove(s.Video);
            var v = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                Stretch = Stretch.Uniform,
                IsMuted = true,
                StretchDirection = smallActualSize ? StretchDirection.DownOnly : StretchDirection.Both,
                Visibility = Visibility.Collapsed
            };
            v.MediaOpened += delegate { OnVideoOpened(s); };
            v.MediaFailed += delegate(object o, ExceptionRoutedEventArgs e) { OnLoadFailed(s, e.ErrorException); };
            v.MediaEnded += delegate { OnVideoEnded(s); };
            s.Video = v;
            s.Rotor.Children.Insert(0, v);
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
        internal static bool IsAudioFile(string f) { return f != null && AudioExts.Contains(Path.GetExtension(f)); }
        static bool IsGifFile(string f) { return f != null && Path.GetExtension(f).Equals(".gif", StringComparison.OrdinalIgnoreCase); }
        static bool IsMedia(string f)
        {
            string ext = Path.GetExtension(f);
            return ImageExts.Contains(ext) || VideoExts.Contains(ext) || AudioExts.Contains(ext);
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
                Filter = "Photos, videos and music|" + string.Join(";", ImageExts.Concat(VideoExts).Concat(AudioExts).Select(e => "*" + e)) +
                         "|All files|*.*"
            };
            if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0 && paths[0].EndsWith(".fvlook", StringComparison.OrdinalIgnoreCase)) { ImportLook(paths[0]); return; } // a shared look
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
            if (files.Count == 0 || DialogOpen) return; // "Delete this file?" stays about the file it names
            growFile = null;
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
            titleText.Text = settingsOpen ? "Settings" : name; // (Settings names itself in the title bar)
            titleText.ToolTip = name; // the whole name, also when it's shortened with "..."
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
                    if (s.Failed && s.IsVideo && s != shown && file == files[index]) Load(s, file); // a video that failed gets another try when you go back to it
                    s.LastUsed = ++useCounter;
                    keep.Add(s);
                    return s;
                }
            }
            var victim = slots.Where(s => !keep.Contains(s) && s != shown)
                              .OrderBy(s => s.File == null ? -1 : s.LastUsed)
                              .First();
            if (victim == fading) FinishCrossfade(); // needed for the next file: its fade ends now
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
            s.IsAudio = IsAudioFile(file);
            s.IsVideo = IsVideoFile(file) || s.IsAudio;
            ResetZoom(s);
            // Phones store some videos sideways with a "turn me" note; Windows ignores it, so apply it here.
            if (!s.IsAudio && !rotations.ContainsKey(file))
            {
                int? kept = TurnMemory.Get(file); // turned in FastViewer before (also before a restart or update)
                int stored = kept != null ? kept.Value : s.IsVideo ? VideoInfo.Rotation(file) : 0;
                if (stored != 0 || kept != null) rotations[file] = stored;
            }
            int angle;
            s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            s.Rotation.Angle = rotations.TryGetValue(file, out angle) ? angle : 0;
            var spin = s.Rotor.RenderTransform as TransformGroup; // a turn still settling from the slot's last file
            if (spin != null) foreach (var part in spin.Children) { if (part is RotateTransform) Motion.Set(part, RotateTransform.AngleProperty, 0); else { Motion.Set(part, ScaleTransform.ScaleXProperty, 1); Motion.Set(part, ScaleTransform.ScaleYProperty, 1); } }

            Dropped(s.Picture.Source as BitmapSource);
            Dropped(s.Poster.Source as BitmapSource);
            s.Poster.Source = null;
            s.Poster.Visibility = Visibility.Collapsed;
            s.Gif = null;
            s.GifState = 0;
            s.Picture.MaxHeight = double.PositiveInfinity; // (a music card is capped)
            s.Picture.Margin = new Thickness(0);
            if (s.IsAudio)
            {
                LoadAudio(s, file, token);
            }
            else if (s.IsVideo)
            {
                s.Video.ScrubbingEnabled = true; // (off while the player held music)
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
                    int fullWidth = 0, fullHeight = 0;
                    try
                    {
                        bmp = DecodeImage(file, Math.Min(ScreenSide, MaxDecodeSide), out fullWidth, out fullHeight);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (s.Token != token) return; // slot was reused for another file meanwhile
                        s.Picture.Source = bmp;
                        s.FullWidth = fullWidth;
                        s.FullHeight = fullHeight;
                        s.Reduced = bmp != null && !IsGifFile(file) && Math.Max(bmp.PixelWidth, bmp.PixelHeight) < Math.Min(MaxDecodeSide, Math.Max(fullWidth, fullHeight));
                        s.Ready = true;
                        s.Failed = bmp == null;
                        s.Error = error == null ? null : FriendlyError(file, error, false);
                        if (s == wanted) TryShow();
                    }));
                });
            }
        }

        // Pictures we stopped using only give their memory back when .NET cleans up, which it rarely does on
        // its own here (the pictures live outside its heap). So once enough has piled up, clean up ourselves:
        // after switching has been quiet for a moment, never in the middle of it. Takes a few milliseconds.
        long droppedBytes;
        DispatcherTimer cleanupTimer;

        void Dropped(BitmapSource b)
        {
            if (b == null) return;
            droppedBytes += (long)b.PixelWidth * b.PixelHeight * 4 * 2; // decoding leaves about one more copy behind
            if (droppedBytes < 32L << 20) return;
            if (cleanupTimer == null)
            {
                cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                cleanupTimer.Tick += delegate { CleanUp(); };
            }
            cleanupTimer.Stop();
            cleanupTimer.Start(); // restarts on every drop: waits until you pause
            // Clicking through without a pause: clean up anyway once a lot has piled up, when nothing else is waiting.
            if (droppedBytes > 128L << 20) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(CleanUp));
        }

        void CleanUp()
        {
            cleanupTimer.Stop();
            if (droppedBytes == 0) return;
            droppedBytes = 0;
            GC.Collect();
        }

        // Photos are decoded no bigger than the biggest screen: a 12 MP photo then takes about 10 MB
        // instead of 48 MB, and JPEGs decode faster too. Zooming in past that loads the whole photo.
        static readonly int ScreenSide = LargestScreenSide();
        const int MaxDecodeSide = 6000; // even the whole photo stops here (huge panoramas)

        delegate bool MonitorProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);
        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);

        // Longest side of the biggest monitor, in pixels (asked from Windows directly: loading Windows Forms
        // for this would slow down the first photo).
        static int LargestScreenSide()
        {
            int best = 0;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr m, IntPtr h, ref NativeRect r, IntPtr d)
                {
                    best = Math.Max(best, Math.Max(r.Right - r.Left, r.Bottom - r.Top));
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return best > 0 ? Math.Max(640, best) : MaxDecodeSide;
        }

        static BitmapSource DecodeImage(string file, int maxSide, out int fullWidth, out int fullHeight)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // Nothing is decoded yet (no cache): the pixels are pulled below, at the size we need.
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
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
                bool sideways = orientation >= 5 && orientation <= 8;
                fullWidth = sideways ? frame.PixelHeight : frame.PixelWidth;
                fullHeight = sideways ? frame.PixelWidth : frame.PixelHeight;

                int longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
                if (longest > maxSide)
                {
                    // Windows decodes JPEGs straight at the smaller size (other types are scaled while decoding).
                    // (BitmapImage's DecodePixelWidth does the same, but its first use costs ~50 ms of start-up.)
                    double k = (double)maxSide / longest;
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
            if (s.SeekOnOpen != null) { s.Video.Position = s.SeekOnOpen.Value; s.SeekOnOpen = null; }
            else if (s != shown) GoToResumePoint(s, true);
            if (s != wanted && !s.IsAudio) SchedulePoster(s, 0);
            if (s == wanted) TryShow();
            if (s == shown) { UpdateControls(); UpdateFacts(); UpdateZoomUi(); }
        }

        void OnLoadFailed(Slot s, Exception ex)
        {
            if (s.IsVideo) try { s.Video.Pause(); s.Video.IsMuted = true; } catch { } // a player that failed (even mid-play) stays still and silent
            if (s == revealing) StopRevealWatch();
            s.Ready = true;
            s.Failed = true;
            string msg = ex == null ? "Unknown error" : ex.Message;
            if (selfTestInterval > 0) testLog.Add("  FAILED " + Path.GetFileName(s.File) + ": " + msg);
            s.Error = FriendlyError(s.File, msg, true);
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
                if (revealing != null && revealing != c) AbandonReveal(); // back to the picture on screen before the next video showed
                RefreshForShown();
                return;
            }

            AbandonReveal(); // a video we started for an earlier press but never got to show: stop it
            // Freeze the old video on its current frame; it stays on screen until the new one draws.
            if (shown != null && shown.IsVideo)
            {
                shown.Video.Pause();
                shown.Video.IsMuted = true;
            }

            if (c.IsAudio && !c.Failed)
            {
                // Music shows its card at once; only the sound starts.
                playing = autoplay;
                Reveal(c);
                if (autoplay)
                {
                    c.Video.Volume = volume;
                    c.Video.IsMuted = muted;
                    c.Video.Play();
                    c.Video.SpeedRatio = speed;
                }
                else ShowOverlay();
                return;
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
            if (snapshotNow && c.Poster.Visibility != Visibility.Visible && !slowGrabs)
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

        // Stops watching a video that was starting up. If you've moved on from it, it's stopped too:
        // otherwise it would keep playing out of sight, with its sound.
        void AbandonReveal()
        {
            var r = revealing;
            StopRevealWatch();
            if (r == null || r == wanted || r == shown) return; // on screen: it's frozen once the next file is ready
            StopHidden(r);
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
                // A slow PC: trust the clock instead of looking at the picture (see Grab).
                bool drawing = slowGrabs ? played >= TimeSpan.FromMilliseconds(150)
                                         : played >= TimeSpan.FromMilliseconds(50) && HasPicture(c.Video);
                if (!drawing && played < TimeSpan.FromMilliseconds(400) && revealWatch.ElapsedMilliseconds < 1500) return;
            }
            if (c == null || c != wanted)
            {
                AbandonReveal(); // you moved on before it showed
                return;
            }
            StopRevealWatch();
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

        // Set once frame grabs get slow. Grabbing has to wait until Windows has the video's current frame
        // ready, on the window's own thread. On a PC that struggles to decode a heavy video that wait took
        // up to 9 s (the window froze and Windows greyed it out). From then on FastViewer doesn't grab
        // frames any more this session: switching waits until the video plays (the old picture stays up),
        // and resuming may show Windows' short black flash instead of a freeze.
        // "Slow" is one grab over 400 ms, or three over 150 ms within 30 s. A single hiccup of a busy PC
        // (other apps, a call) doesn't count: before 1.17.2 one grab over 120 ms switched grabbing off,
        // which made fast switching through videos noticeably slower for the rest of the session.
        static volatile bool slowGrabs;
        static bool grabAlways; // --grab-always: tests without this protection, to compare
        const int SlowGrabMs = 400, SlowishGrabMs = 150;
        static readonly Queue<long> slowishGrabsAt = new Queue<long>();
        static readonly Stopwatch grabClock = Stopwatch.StartNew();

        static void NoteGrab(long ms)
        {
            if (grabAlways) return;
            if (ms > SlowGrabMs) { slowGrabs = true; return; }
            if (ms <= SlowishGrabMs) return;
            long now = grabClock.ElapsedMilliseconds;
            slowishGrabsAt.Enqueue(now);
            while (slowishGrabsAt.Count > 0 && now - slowishGrabsAt.Peek() > 30000) slowishGrabsAt.Dequeue();
            if (slowishGrabsAt.Count >= 3) slowGrabs = true;
        }
        static List<long> grabTimes; // tests: how long each grab took

        // Renders the video's current picture at the given size (works while it's hidden, too).
        static RenderTargetBitmap Grab(MediaElement v, int w, int h)
        {
            var watch = Stopwatch.StartNew();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(v) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            if (grabTimes != null && grabTimes.Count < 5000) grabTimes.Add(watch.ElapsedMilliseconds);
            NoteGrab(watch.ElapsedMilliseconds);
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
                if (s.Token != token || !s.IsVideo || s.Failed || s == shown || s == revealing || slowGrabs) return;
                var v = s.Video;
                if (v.NaturalVideoWidth == 0) return;
                bool drawn = HasPicture(v);
                if (slowGrabs) return; // that look took too long: no more snapshots (see Grab)
                if (!drawn && attempt < 12)
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

        Slot fading; // Settings > Appearance > Transition between files: the last file, fading out over this one

        void Reveal(Slot c)
        {
            FinishCrossfade(); // a fade from the last switch ends now (clicking quickly never waits for it)
            if (shown != null && shown != c)
            {
                bool fade = crossfadeFiles && Motion.Animates && !gridOpen && ((snapDir == null && selfTestInterval == 0) || motionOverride != null); // (tests only with --motion)
                // Rewind while hidden (or go to where you left off), so it's ready if you come back.
                if (shown.IsVideo)
                {
                    RememberPosition(shown);
                    if (fade) { shown.Video.Pause(); shown.Video.IsMuted = true; } // rewound once it has faded
                    else StopHidden(shown);
                }
                if (fade)
                {
                    // The new file is shown at once underneath; the old one fades away on top of it (120 ms).
                    fading = shown;
                    Panel.SetZIndex(shown.Host, 3);
                    Motion.To(shown.Host, OpacityProperty, 0, Motion.Ms(120, false), Motion.Smooth, FinishCrossfade);
                }
                else
                {
                    ResetZoom(shown);
                    Panel.SetZIndex(shown.Host, 0);
                    if (glassSlots) shown.Host.Opacity = HiddenOpacity;
                }
            }
            Panel.SetZIndex(c.Host, 2);
            Motion.Set(c.Host, OpacityProperty, 1);
            var before = shown;
            shown = c;
            OnShownChanged(before);
            if (snapDir != null)
                testLog.Add("reveal " + Path.GetFileName(c.File) + " at " + switchWatch.ElapsedMilliseconds + " ms, video clock " + (c.IsVideo ? c.Video.Position.TotalMilliseconds : 0) + " ms");
            if (selfTestInterval > 0)
                testLog.Add(string.Format("{0,-40} shown after {1,4} ms", Path.GetFileName(c.File), switchWatch.ElapsedMilliseconds));
            if (slideshow) ScheduleSlide();
            RefreshForShown();
            GrowPicked(c); // picked in the grid: grows out of its tile
        }

        void FinishCrossfade()
        {
            var s = fading;
            if (s == null) return;
            fading = null;
            Motion.Set(s.Host, OpacityProperty, glassSlots && s != shown ? HiddenOpacity : 1);
            if (s == shown) return;
            Panel.SetZIndex(s.Host, 0);
            ResetZoom(s);
            if (s.IsVideo) StopHidden(s);
        }

        void StopHidden(Slot s)
        {
            s.Video.Pause();
            s.Video.IsMuted = true;
            GoToResumePoint(s);
            s.Poster.Visibility = Visibility.Collapsed;
            Dropped(s.Poster.Source as BitmapSource);
            s.Poster.Source = null;
            SchedulePoster(s, 0); // fresh snapshot of the first frame for next time
        }

        // The middle-of-the-window message (nothing open, an empty folder, a file that can't be shown): the first
        // line becomes the headline, the rest the details, with an icon that fits and the button for what to do next.
        // Runs only when the text really changes, so switching between normal files never comes here.
        void ArrangeMessage()
        {
            string text = message.Text ?? "";
            var title = F<TextBlock>("MessageTitle");
            var body = F<TextBlock>("MessageBody");
            var icon = F<Glyph>("MessageIcon");
            var open = F<FrameworkElement>("MessageOpenBtn");
            if (text.Length == 0)
            {
                title.Visibility = body.Visibility = icon.Visibility = open.Visibility = Visibility.Collapsed;
                return;
            }
            bool cantOpen = text.StartsWith("Can't open ");
            int cut = text.IndexOf('\n');
            string head = cut < 0 ? text : text.Substring(0, cut), rest = cut < 0 ? "" : text.Substring(cut + 1).Trim();
            bool problem = cantOpen || head.StartsWith("Couldn't") || head.StartsWith("Not found");
            bool folder = head.StartsWith("No ");
            title.Text = cantOpen ? "Can't open this file" : head.TrimEnd(':');
            if (rest.StartsWith("or ")) rest = char.ToUpper(rest[0]) + rest.Substring(1); // "Or press Ctrl+O to open one"
            body.Text = rest;
            icon.Code = problem ? "" : folder ? "" : "";
            title.Visibility = Visibility.Visible;
            body.Visibility = rest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            icon.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            open.Visibility = cantOpen || mini ? Visibility.Collapsed : Visibility.Visible; // nothing to look at: offer Open
        }

        void RefreshForShown()
        {
            var c = shown;
            message.Text = c.Failed ? "Can't open " + Path.GetFileName(c.File) + "\n\n" + c.Error : "";
            var messageBtn = F<FrameworkElement>("MessageBtn");
            if (c.Failed && messageBtn.Visibility != Visibility.Visible) Motion.Enter(F<FrameworkElement>("MessagePanel"), 8); // rises in with the file
            messageBtn.Visibility = c.Failed ? Visibility.Visible : Visibility.Collapsed;
            UpdateControls();
            UpdateFacts();
            UpdateZoomUi();
            if (mini)
            {
                FitMiniWindow(false); // the mini player takes the shape of each new video
                if (MiniSeeThrough() != miniClear) ApplyWindowLook(); // clear around a see-through photo, black for the rest
            }
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

            // The middle button dips and springs back, so the change feels immediate.
            if (Motion.Moves)
            {
                Motion.FromTo(centerScale, ScaleTransform.ScaleXProperty, 0.9, 1, Motion.Ms(Motion.Normal), Motion.In);
                Motion.FromTo(centerScale, ScaleTransform.ScaleYProperty, 0.9, 1, Motion.Ms(Motion.Normal), Motion.In);
            }
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
            if (!overlayVisible) // the time bar isn't showing: say where we are
                ShowFeedback((seconds > 0 ? "+" : "-") + Math.Abs(seconds).ToString("0.#", CultureInfo.InvariantCulture) + " s    " +
                             Fmt(s.Video.Position) + (d.HasTimeSpan ? " / " + Fmt(d.TimeSpan) : ""));
        }

        void SetVolume(double v)
        {
            volume = Math.Max(0, Math.Min(1, v));
            if (volume > 0) muted = false;
            ApplyAudio();
            ShowVolumeFeedback();
        }

        void ToggleMute()
        {
            muted = !muted;
            ApplyAudio();
            ShowVolumeFeedback();
        }

        // The control bar only shows a speaker icon, so a key or the wheel shows the level (not while the slider is open).
        void ShowVolumeFeedback()
        {
            if (mini && F<FrameworkElement>("MiniVolBtn").IsVisible) { ShowMiniVolume(); return; } // next to the speaker, not over the buttons
            if (!volPopup.IsOpen) ShowFeedback(muted || volume == 0 ? "Muted" : "Volume " + Math.Round(volume * 100) + "%");
        }

        // Mini player: the volume as a number next to its speaker, for a moment (longer while the mouse is on it).
        readonly DispatcherTimer miniVolTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };

        void ShowMiniVolume()
        {
            F<TextBlock>("MiniVolText").Text = muted || volume == 0 ? "Muted" : Math.Round(volume * 100) + "%";
            var level = F<FrameworkElement>("MiniVolLevel");
            if (!Motion.IsShown(level) || level.Visibility != Visibility.Visible) Motion.Show(level, 6, 0);
            miniVolTimer.Stop();
            miniVolTimer.Start();
        }

        void HideMiniVolume()
        {
            miniVolTimer.Stop();
            var b = F<FrameworkElement>("MiniVolBtn");
            if (b.IsMouseOver || F<FrameworkElement>("MiniVolLevel").IsMouseOver) { miniVolTimer.Start(); return; } // still pointing at it
            Motion.Hide(F<FrameworkElement>("MiniVolLevel"), 6, 0, 1, 150);
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

        // The volume slider opens when the mouse rests on the speaker and closes a moment after it has left both
        // the speaker and the slider; clicking the speaker mutes. A touch tap opens it instead (touch has no hover).
        readonly DispatcherTimer volOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        readonly DispatcherTimer volCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        bool volumeTestOpen; // tests: what the slider would do, without it appearing on screen

        bool VolumeSliderOpen { get { return snapDir != null ? volumeTestOpen : volPopup.IsOpen; } }

        void InitVolumeHover()
        {
            var panel = (FrameworkElement)volPopup.Child;
            volOpenTimer.Tick += delegate { SetVolumeSlider(true); };
            volCloseTimer.Tick += delegate
            {
                volCloseTimer.Stop();
                if (!volBtn.IsMouseOver && !panel.IsMouseOver && !F<FrameworkElement>("MiniVolBtn").IsMouseOver) SetVolumeSlider(false);
            };
            volBtn.MouseEnter += delegate
            {
                volCloseTimer.Stop();
                PlaceVolumeSlider(volBtn, PlacementMode.Top);
                if (!VolumeSliderOpen) volOpenTimer.Start();
            };
            // The mini player's own volume button: click mutes, the wheel changes the volume, and the level shows as a
            // number next to it (a slider or message would cover the small window's buttons and catch the wheel).
            var miniVol = F<Button>("MiniVolBtn");
            miniVol.Click += delegate { ToggleMute(); };
            miniVol.MouseEnter += delegate { ShowMiniVolume(); };
            miniVolTimer.Tick += delegate { HideMiniVolume(); };
            miniVol.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e) { SetVolume(volume + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; };
            F<FrameworkElement>("MiniVolLevel").PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e) { SetVolume(volume + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; };
            volBtn.MouseLeave += delegate { LeaveVolume(); };
            panel.MouseEnter += delegate { volCloseTimer.Stop(); };
            panel.MouseLeave += delegate { LeaveVolume(); };
            volBtn.PreviewMouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
            {
                if (e.StylusDevice == null) return; // a real mouse click: mute (Click)
                SetVolumeSlider(!VolumeSliderOpen);
                e.Handled = true;
            };
            Deactivated += delegate { SetVolumeSlider(false); EndScrub(); };
        }

        void PlaceVolumeSlider(UIElement target, PlacementMode where)
        {
            if (volPopup.PlacementTarget == target) return;
            if (volPopup.IsOpen) SetVolumeSlider(false);
            volPopup.PlacementTarget = target;
            volPopup.Placement = where;
            volPopup.VerticalOffset = where == PlacementMode.Top ? -8 : 6;
        }

        void LeaveVolume()
        {
            volOpenTimer.Stop();
            volCloseTimer.Stop();
            volCloseTimer.Start();
        }

        bool volWanted;

        // The volume slider grows up out of the speaker (150 ms) and fades away when it closes.
        void SetVolumeSlider(bool open)
        {
            volOpenTimer.Stop();
            if (snapDir != null) { volumeTestOpen = open; return; }
            var box = (FrameworkElement)volPopup.Child;
            if (open)
            {
                bool showing = volPopup.IsOpen && volWanted;
                volWanted = true;
                volPopup.IsOpen = true;
                if (showing) { Motion.To(box, OpacityProperty, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth); return; }
                box.RenderTransformOrigin = new Point(0.5, volPopup.Placement == PlacementMode.Bottom ? 0 : 1); // grows out of its button
                Motion.FromTo(box, OpacityProperty, 0, 1, Motion.Ms(150, false), Motion.Smooth);
                if (!Motion.Moves) return;
                var p = Motion.PartsOf(box);
                Motion.FromTo(p.Scale, ScaleTransform.ScaleXProperty, 0.92, 1, Motion.Ms(Motion.Fast), Motion.In);
                Motion.FromTo(p.Scale, ScaleTransform.ScaleYProperty, 0.85, 1, Motion.Ms(Motion.Fast), Motion.In);
            }
            else
            {
                volWanted = false;
                if (!volPopup.IsOpen) return;
                Motion.To(box, OpacityProperty, 0, Motion.Ms(100, false), Motion.Out, delegate
                {
                    if (volWanted) return; // opened again meanwhile
                    volPopup.IsOpen = false;
                    Motion.Set(box, OpacityProperty, 1);
                });
            }
        }

        void UpdateControls()
        {
            UpdateTaskbarButtons();
            bool isVideo = shown != null && shown.IsVideo && !shown.Failed;
            // A small mini player has no room for the control bar; play/pause stays in the middle.
            transport.Visibility = isVideo && !trimming && !cropping && !(mini && stage.ActualWidth < 420) ? Visibility.Visible : Visibility.Collapsed;
            if (transport.Visibility != Visibility.Visible || settingsOpen || gridOpen) SetVolumeSlider(false); // nothing left to point at
            bool center = centerButton == 0 || (centerButton == 1 && !playing); // Settings > Videos
            var wantCenter = isVideo && center && !cropping ? Visibility.Visible : Visibility.Collapsed;
            if (wantCenter == Visibility.Visible && centerPlayBtn.Visibility != Visibility.Visible && Motion.Animates && overlayVisible && !Switching)
            {
                // ("Only when paused": it appears as you pause) fades in and grows into place
                Motion.FromTo(centerPlayBtn, OpacityProperty, 0, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth);
                if (Motion.Moves) { Motion.FromTo(centerScale, ScaleTransform.ScaleXProperty, 0.85, 1, Motion.Ms(Motion.Normal), Motion.In); Motion.FromTo(centerScale, ScaleTransform.ScaleYProperty, 0.85, 1, Motion.Ms(Motion.Normal), Motion.In); }
            }
            centerPlayBtn.Visibility = wantCenter;
            F<FrameworkElement>("PrevBtn").Visibility = F<FrameworkElement>("NextBtn").Visibility =
                Shown("Arrows") && files.Count > 1 && !cropping ? Visibility.Visible : Visibility.Collapsed; // Settings > Buttons; nothing to go to: none
            // A narrow control bar keeps its time bar: the skip and repeat buttons go first (they're on keys and in "...").
            double room = transport.Width;
            foreach (var id in new[] { "PlayBtn", "BackBtn", "FwdBtn", "VolBtn", "LoopBtn" })
            {
                bool fits = id == "PlayBtn" || (id == "VolBtn" ? room >= 300 : room >= 440);
                F<FrameworkElement>(id).Visibility = Shown(id) && fits ? Visibility.Visible : Visibility.Collapsed;
            }
            SetPlayGlyphs(playing);
            UpdateSubButton();
            UpdateScrim();
            centerPlayBtn.ToolTip = WithKey(playing ? "Pause" : "Play", KeyOf("play"));
            string playCode = playing ? "\uE769" : "\uE768";
            if (playIcon.Code != playCode) { playIcon.Code = playCode; if (!Switching) Motion.Pulse(playIcon); }
            if (loop) loopIcon.SetResourceReference(Glyph.ForegroundProperty, "Accent");
            else loopIcon.ClearValue(Glyph.ForegroundProperty);
            string level = muted || volume == 0 ? "\uE74F" : volume < 0.34 ? "\uE993" : volume < 0.67 ? "\uE994" : "\uE995";
            if (volIcon.Code != level && volIcon.Code != null && !Switching) Motion.Pulse(volIcon, 0.85);
            volIcon.Code = level;
            muteIcon.Code = level;
            F<Glyph>("MiniVolIcon").Code = level;
            F<FrameworkElement>("MiniVolBtn").Visibility = mini && miniVolume && isVideo ? Visibility.Visible : Visibility.Collapsed;
            if (!(mini && miniVolume && isVideo)) { miniVolTimer.Stop(); Motion.Set(F<FrameworkElement>("MiniVolLevel"), OpacityProperty, 1); F<FrameworkElement>("MiniVolLevel").Visibility = Visibility.Collapsed; }
            settingSlider = true;
            vol.Value = volume * 100;
            settingSlider = false;
            volText.Text = Math.Round(volume * 100).ToString(CultureInfo.InvariantCulture);
            fullIcon.Code = fullscreen ? "\uE73F" : "\uE740";
            // While the grid or Settings is open the file buttons are off: the file they'd act on isn't on screen.
            bool hasFile = CurrentFile != null && !gridOpen && !settingsOpen;
            foreach (var name in new[] { "DeleteBtn", "CopyBtn", "MoreBtn", "OpenWithBtn", "SlideshowBtn", "InfoBtn", "GridBtn" })
                F<Button>(name).IsEnabled = hasFile;
            bool music = shown != null && shown.IsAudio; // music has no picture to turn or to show in the mini player
            F<Button>("RotateBtn").IsEnabled = F<Button>("MiniBtn").IsEnabled = hasFile && !music;
            UpdateTime();
        }

        bool? glyphsPlaying;

        // Within half a second of a switch: the small decorations a new file brings (icon morphs and bumps) are
        // skipped, so nothing keeps the window redrawing while the next files get ready (switching stays instant).
        bool Switching { get { return switchWatch.IsRunning && switchWatch.ElapsedMilliseconds < 500; } }

        // Play and pause turn into each other: one shrinks and fades as the other grows in (180 ms).
        void SetPlayGlyphs(bool isPlaying)
        {
            if (glyphsPlaying == isPlaying) return;
            bool first = glyphsPlaying == null || Switching;
            glyphsPlaying = isPlaying;
            playGlyph.Visibility = pauseGlyph.Visibility = Visibility.Visible;
            var show = isPlaying ? pauseGlyph : playGlyph;
            var hide = isPlaying ? playGlyph : pauseGlyph;
            show.RenderTransformOrigin = hide.RenderTransformOrigin = new Point(0.5, 0.5);
            double ms = first ? 0 : Motion.Ms(180);
            Motion.To(show, OpacityProperty, 1, first ? 0 : Motion.Ms(180, false), Motion.Smooth);
            Motion.To(hide, OpacityProperty, 0, first ? 0 : Motion.Ms(120, false), Motion.Out);
            if (!Motion.Moves || first) return;
            var a = Motion.PartsOf(show); var b = Motion.PartsOf(hide);
            Motion.FromTo(a.Scale, ScaleTransform.ScaleXProperty, 0.6, 1, ms, Motion.In);
            Motion.FromTo(a.Scale, ScaleTransform.ScaleYProperty, 0.6, 1, ms, Motion.In);
            Motion.FromTo(b.Scale, ScaleTransform.ScaleXProperty, 1, 0.6, Motion.Ms(120, false), Motion.Out);
            Motion.FromTo(b.Scale, ScaleTransform.ScaleYProperty, 1, 0.6, Motion.Ms(120, false), Motion.Out);
        }

        void UpdateTime()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            var pos = s.Video.Position;
            var d = s.Video.NaturalDuration;
            var dur = d.HasTimeSpan ? d.TimeSpan : TimeSpan.Zero;
            if (!seek.IsMouseCaptureWithin && !scrubbing) // don't fight the user while they drag
            {
                settingSlider = true;
                seek.Maximum = Math.Max(0.01, dur.TotalSeconds);
                double v = Math.Min(pos.TotalSeconds, seek.Maximum);
                if (playing && Motion.Animates && overlayVisible && transport.IsVisible) GlideSeek(v);
                else
                {
                    Motion.Stop("seekGlide");
                    seek.Value = v;
                }
                settingSlider = false;
            }
            posText.Text = Fmt(scrubbing ? TimeSpan.FromSeconds(seek.Value) : pos);
            durText.Text = Fmt(dur);
            if (cues != null) SubTick(); // (also while paused or dragging the time bar)
        }

        // The time bar's dot moves every screen frame while a video plays: between the position updates (10 a
        // second) it carries on at the video's speed, and each update gently corrects it. Only while the
        // controls show; nothing runs when they're hidden or the video is paused.
        double glideAt;                                    // where the dot was heading from (seconds)...
        readonly Stopwatch glideClock = new Stopwatch();   // ...this long ago
        void GlideSeek(double real)
        {
            if (Switching) // clicking through files: nothing redraws the window on its own, so the next file shows sooner
            {
                Motion.Stop("seekGlide");
                seek.Value = real;
                return;
            }
            if (Motion.Running("seekGlide"))
            {
                double predicted = glideAt + glideClock.Elapsed.TotalSeconds * speed;
                double off = real - predicted;
                glideAt = Math.Abs(off) > 0.3 ? real : predicted + off * 0.3; // a jump (loop, seek) moves it at once
                glideClock.Restart();
                return;
            }
            glideAt = real;
            glideClock.Restart();
            seek.Value = real;
            Motion.Run("seekGlide", delegate
            {
                var s = shown;
                if (s == null || !s.IsVideo || !playing || !overlayVisible || scrubbing || seek.IsMouseCaptureWithin || !transport.IsVisible || Switching) return false;
                settingSlider = true;
                seek.Value = Math.Min(seek.Maximum, glideAt + glideClock.Elapsed.TotalSeconds * speed);
                settingSlider = false;
                return true;
            });
        }

        // Dragging the time bar: the video holds still and shows the frames you pass (at most ~20 jumps a second,
        // always ending exactly where you let go), then plays on if it was playing. Jumping a playing video
        // dozens of times a second used to garble its sound, which could keep stuttering long after.
        bool scrubbing, playAfterScrub;
        Slot scrubSlot; // the video being dragged through (another file may show up meanwhile)
        double scrubTarget;
        readonly Stopwatch scrubClock = Stopwatch.StartNew();
        readonly DispatcherTimer scrubTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

        void StartScrub()
        {
            var s = shown;
            Motion.Stop("seekGlide");
            if (scrubbing || s == null || !s.IsVideo || s.Failed) return;
            scrubbing = true;
            scrubSlot = s;
            playAfterScrub = playing;
            if (playing) s.Video.Pause(); // quietly: the play button doesn't flip back and forth
        }

        void ScrubTo(double seconds, bool exact)
        {
            var s = shown;
            if (s == null || !s.IsVideo || s != scrubSlot) return;
            scrubTarget = seconds;
            if (!exact && scrubClock.ElapsedMilliseconds < 50)
            {
                if (!scrubTimer.IsEnabled) scrubTimer.Start(); // the latest place, a moment later
                return;
            }
            scrubTimer.Stop();
            scrubClock.Restart();
            s.Poster.Visibility = Visibility.Collapsed;
            s.Video.Position = TimeSpan.FromSeconds(seconds);
            scrubSeeks++;
            UpdateTime();
        }
        int scrubSeeks; // (tests)

        void EndScrub()
        {
            if (!scrubbing) return;
            scrubTimer.Stop();
            var s = shown;
            if (s == scrubSlot) ScrubTo(seek.Value, true); // exactly where you let go
            scrubbing = false;
            if (playAfterScrub && playing && s == scrubSlot && s != null && s.IsVideo) s.Video.Play();
            scrubSlot = null;
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
            SetSlideshowButton();
            string stop = KeyOf("slideshow");
            ShowToast("Slideshow: " + slideSeconds + " seconds a photo. Press Esc" + (stop.Length > 0 ? " or " + stop : "") + " to stop.", null, null, 3, "slideshow");
        }

        void ScheduleSlide()
        {
            slideTimer.Stop();
            if (!slideshow) return;
            if (shown != null && shown.IsVideo && !shown.Failed) return; // videos advance when they end
            slideTimer.Start();
        }

        // The slideshow button turns into "Stop slideshow" while one runs.
        void SetSlideshowButton()
        {
            var b = F<Button>("SlideshowBtn");
            var g = b.Content as Glyph;
            if (g != null) g.Code = slideshow ? "" : "";
            string text = slideshow ? "Stop slideshow" : "Start slideshow";
            b.ToolTip = WithKey(text, KeyOf("slideshow"));
            System.Windows.Automation.AutomationProperties.SetName(b, text);
        }

        // Dates in the Info pane, in the same language as the rest of FastViewer ("2 November 2026, 01:31").
        static string InfoDate(DateTime d)
        {
            return d.ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB"));
        }

        // What went wrong opening a file, in plain words (Windows' own messages are technical and sometimes wrong).
        static string FriendlyError(string file, string raw, bool video)
        {
            raw = raw ?? "";
            long length = -1;
            try { length = new FileInfo(file).Length; } catch { }
            if (raw.Contains("too long") || raw.Contains("PathTooLong"))
                return "The file's name and folder path are too long for Windows. Move it to a folder with a shorter path.";
            if (length == 0) return "This file is empty (0 bytes): nothing was saved in it.";
            if (raw.Contains("being used by another process"))
                return "Another app has this file open and isn't sharing it. Close it there, then try again.";
            if (raw.Contains("Access to the path") || raw.Contains("denied"))
                return "Windows doesn't allow FastViewer to open this file.";
            if (!video)
                return raw.Contains("No imaging component") || raw.Contains("not valid") || raw.Contains("0x88982F")
                    ? "This file is damaged, or it isn't really a photo."
                    : "Windows couldn't read this photo (" + raw + ")";
            bool? complete = VideoInfo.LooksComplete(file);
            if (complete == false) return "This video file is damaged or incomplete (for example a download that didn't finish).";
            if (complete == true)
            {
                string codec = VideoInfo.VideoCodec(file);
                if (codec == "avc1" || codec == "avc3" || codec == "mp4v") // formats Windows always plays: it was busy (very fast switching, little memory)
                    return "Windows couldn't play this video just now. Go to another file and back to try again.";
                return codec == "hvc1" || codec == "hev1"
                    ? "This is an HEVC video (iPhones and many phones record these). Windows needs\n\"HEVC Video Extensions\" from the Microsoft Store to play it."
                    : "Windows can't play the kind of video in this file (" + (codec ?? "unknown format") + ").";
            }
            return "Windows can't play this video. It may be damaged, or it needs a codec that isn't installed.";
        }

        void StopSlideshow()
        {
            slideshow = false;
            slideTimer.Stop();
            SetSlideshowButton();
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
            else if (s.FullWidth > 0)
            {
                w = s.FullWidth; // the photo's real size, also while a screen-sized copy is in memory
                h = s.FullHeight;
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
            fileFacts.Visibility = narrow || !Shown("FileFacts") ? Visibility.Collapsed : Visibility.Visible;
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
            if (infoOpen) FillInfo();
            PlaceInfo(infoOpen && !fullscreen, true);
            PlaceBars(); // floating bars (Clean) dock while the pane is open, so they don't cover it
        }

        // The Info pane lies over the right edge of the viewing area (it isn't in the column itself), so it can
        // slide in and out while the column under it takes its final width in one go: the picture is laid out
        // once, and it and everything floating over it glide from where they were to their new middle.
        const double InfoWidth = 320;

        void PlaceInfo(bool show, bool animate)
        {
            var pane = F<FrameworkElement>("InfoPanel");
            bool was = infoCol.Width.Value > 0;
            infoCol.Width = new GridLength(show ? InfoWidth : 0);
            var move = Motion.PartsOf(pane).Move;
            if (!animate || was == show || !Motion.Animates)
            {
                Motion.Set(move, TranslateTransform.XProperty, 0);
                Motion.Set(pane, OpacityProperty, 1);
                pane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            double ms = Motion.Ms(Motion.Normal);
            if (!Motion.Moves) // Reduced: a short fade, nothing slides
            {
                Motion.Set(move, TranslateTransform.XProperty, 0);
                if (show) { pane.Visibility = Visibility.Visible; Motion.To(pane, OpacityProperty, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth); }
                else Motion.To(pane, OpacityProperty, 0, Motion.Ms(Motion.Fast, false), Motion.Smooth, delegate { pane.Visibility = Visibility.Collapsed; Motion.Set(pane, OpacityProperty, 1); });
                return;
            }
            Motion.Set(pane, OpacityProperty, 1);
            if (show)
            {
                if (pane.Visibility != Visibility.Visible) { pane.Visibility = Visibility.Visible; Motion.Set(move, TranslateTransform.XProperty, InfoWidth); }
                Motion.To(move, TranslateTransform.XProperty, 0, ms, Motion.In);
            }
            else Motion.To(move, TranslateTransform.XProperty, InfoWidth, ms, Motion.Smooth, delegate { pane.Visibility = Visibility.Collapsed; });
            double shift = show ? InfoWidth / 2 : -InfoWidth / 2; // the middle of the viewing area moves half the pane's width
            foreach (var name in new[] { "Stage", "MessagePanel", "Scrim", "CenterPlayBtn", "Transport", "TrimBar", "CropBar", "Toast" })
                SlideFrom(root.FindName(name) as FrameworkElement, shift, ms);
            SlideFrom(F<FrameworkElement>("NextBtn"), shift * 2, ms); // (the right edge moves the whole width)
        }

        // Glides an element from exactly dx beside its place (on top of wherever it is right now) back to it.
        static void SlideFrom(FrameworkElement e, double dx, double ms)
        {
            if (e == null || e.Visibility != Visibility.Visible) return;
            var move = Motion.PartsOf(e).Move;
            Motion.FromTo(move, TranslateTransform.XProperty, move.X + dx, 0, ms, Motion.In);
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
                AddInfo("Date modified", InfoDate(fi.LastWriteTime));
                AddInfo("Date created", InfoDate(fi.CreationTime));
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
            var caption = new TextBlock { Text = label, FontSize = 12 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(caption);
            panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            infoList.Children.Add(panel);
        }

        // ---------------------------------------------------------------- file actions

        string deleting; // the file the delete dialog names: that one is deleted, whatever is on screen by then

        bool DialogOpen { get { return Motion.IsShown(dialog); } }

        // The shade fades in and the box settles from slightly bigger, like a Windows 11 dialog.
        void ShowDeleteDialog()
        {
            Motion.Show(dialog, 0, 0, 1, Motion.Fast);
            var box = (FrameworkElement)dialog.Children[0];
            box.RenderTransformOrigin = new Point(0.5, 0.5);
            if (!Motion.Moves) return;
            var p = Motion.PartsOf(box);
            Motion.FromTo(p.Scale, ScaleTransform.ScaleXProperty, 1.04, 1, Motion.Ms(Motion.Normal), Motion.In);
            Motion.FromTo(p.Scale, ScaleTransform.ScaleYProperty, 1.04, 1, Motion.Ms(Motion.Normal), Motion.In);
        }

        void HideDeleteDialog()
        {
            Motion.Hide(dialog, 0, 0, 1, 83);
        }

        void AskDelete()
        {
            if (CurrentFile == null) return;
            deleting = CurrentFile;
            if (!confirmDelete)
            {
                ConfirmDelete();
                return;
            }
            dialogText.Text = "\"" + Path.GetFileName(CurrentFile) + "\" will be moved to the Recycle Bin.";
            ShowDeleteDialog();
        }

        void ConfirmDelete()
        {
            HideDeleteDialog();
            string f = deleting;
            deleting = null;
            if (f == null || !files.Contains(f, StringComparer.OrdinalIgnoreCase)) return;
            deleteShot = null;
            if (Motion.Animates && shown != null && !shown.IsVideo && string.Equals(shown.File, f, StringComparison.OrdinalIgnoreCase) && stage.ActualWidth > 0)
                try
                {
                    double k = Dpi();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(stage.ActualWidth * k), (int)(stage.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
                    rtb.Render(shown.Host); // a photo: nothing to wait for (videos are never drawn this way)
                    rtb.Freeze();
                    deleteShot = rtb;
                }
                catch { }
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
            FadeAwayDeleted();
        }

        // The deleted photo fades and shrinks away over whatever comes next (photos only: a picture of the
        // player, taken before it let go of the file).
        Image deleteGhost;
        System.Windows.Media.Imaging.BitmapSource deleteShot;

        void FadeAwayDeleted()
        {
            var shot = deleteShot;
            deleteShot = null;
            if (shot == null || !Motion.Animates) return;
            if (deleteGhost != null) stage.Children.Remove(deleteGhost);
            var img = new Image { Source = shot, Stretch = Stretch.Fill, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
            Panel.SetZIndex(img, 3);
            stage.Children.Add(img);
            deleteGhost = img;
            if (Motion.Moves)
            {
                var p = Motion.PartsOf(img);
                Motion.To(p.Scale, ScaleTransform.ScaleXProperty, 0.94, Motion.Ms(Motion.Normal, false), Motion.Out);
                Motion.To(p.Scale, ScaleTransform.ScaleYProperty, 0.94, Motion.Ms(Motion.Normal, false), Motion.Out);
            }
            Motion.To(img, OpacityProperty, 0, Motion.Ms(Motion.Normal, false), Motion.Out, delegate { stage.Children.Remove(img); if (deleteGhost == img) deleteGhost = null; });
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

            string current = CurrentFile;
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
            int still = string.Equals(current, f, StringComparison.OrdinalIgnoreCase) ? -1
                : files.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
            ShowIndex(still >= 0 ? still : Math.Min(i < 0 ? index : i, files.Count - 1), lastDir);
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
            if (glyph != null) mi.Icon = new Glyph { Code = glyph, Size = 16 }; // the size the bar's icons have (at 14 Windows' icon font draws them heavier)
            mi.Click += delegate { action(); };
            return mi;
        }

        void ShowMenu(string button, PlacementMode placement, params Control[] items)
        {
            var b = F<Button>(button);
            if (ClosingOwnMenu(b)) return;
            var menu = new ContextMenu { PlacementTarget = b, Placement = placement, VerticalOffset = 4 };
            foreach (var i in items) menu.Items.Add(i);
            if (huntMenuHook != null) { huntMenuHook(menu, b); return; } // tests: drawn inside the window instead
            openMenus++; // a menu sits outside the window; don't fade the controls under it
            menu.Closed += delegate { openMenus--; };
            OpenMenu(menu, b);
        }

        // A menu closes as soon as the mouse is pressed outside it. Pressed on the menu's own button, that same
        // click would then open it straight back up: like everywhere in Windows, clicking the button again closes it.
        Button menuClosedOver;

        void OpenMenu(ContextMenu menu, Button b)
        {
            menu.Closed += delegate
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed || !b.IsVisible) return;
                var p = Mouse.GetPosition(b);
                if (p.X >= 0 && p.Y >= 0 && p.X < b.ActualWidth && p.Y < b.ActualHeight) MenuClosedBy(b);
            };
            menu.IsOpen = true;
        }

        void MenuClosedBy(Button b)
        {
            menuClosedOver = b;
            // Forgotten once that click is over (also when the mouse is let go somewhere else).
            MouseButtonEventHandler up = null;
            up = delegate
            {
                PreviewMouseLeftButtonUp -= up;
                Dispatcher.BeginInvoke(new Action(delegate { menuClosedOver = null; }), DispatcherPriority.Background);
            };
            PreviewMouseLeftButtonUp += up;
        }

        bool ClosingOwnMenu(Button b)
        {
            bool own = menuClosedOver == b;
            menuClosedOver = null; // any menu click ends that press
            return own;
        }

        static MenuItem Heading(string text)
        {
            return new MenuItem { Header = text, Style = (Style)Application.Current.Resources["MenuHeading"] };
        }


        void ShowZoomMenu()
        {
            ShowMenu("ZoomBtn", PlacementMode.Top,
                Item("Fit to window", "", delegate { SetZoom(1, StageCenter()); }, KeyOf("fit")),
                Item("Actual size (100%)", null, delegate { ZoomToPercent(100); }, KeyOf("actualSize")),
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
            // Several wheel notches in a row add up to one smooth zoom (from where it's heading, not where it is).
            if (shown != null) SetZoom((zoomTarget > 0 && Motion.Running("zoom") ? zoomTarget : shown.Zoom.ScaleX) * factor, p);
        }

        double zoomTarget;  // where an animated zoom is heading

        void SetZoom(double z, Point p)
        {
            var s = shown;
            if (s == null || !s.Ready || s.Failed || cropping) return;
            z = Math.Max(1, Math.Min(MaxZoom, z));
            Motion.Stop("pan");
            if (!Motion.Moves)
            {
                Motion.Stop("zoom");
                ZoomStep(s, z, p);
                return;
            }
            // Glides there in about 200 ms (no bounce), keeping the point under the pointer still.
            zoomTarget = z;
            Motion.Run("zoom", dt =>
            {
                if (s != shown) return false;
                double now = s.Zoom.ScaleX;
                double next = Math.Exp(Math.Log(now) + (Math.Log(zoomTarget) - Math.Log(now)) * Motion.Follow(dt, Motion.Follows(200)));
                bool done = Math.Abs(next - zoomTarget) < 0.002 * zoomTarget;
                ZoomStep(s, done ? zoomTarget : next, p);
                return !done;
            });
        }

        // One step of zooming: the point p (in the window) stays where it is.
        void ZoomStep(Slot s, double z, Point p)
        {
            double old = s.Zoom.ScaleX;
            double x = (p.X - s.Pan.X) / old, y = (p.Y - s.Pan.Y) / old;
            s.Zoom.ScaleX = s.Zoom.ScaleY = z;
            s.Pan.X = p.X - z * x;
            s.Pan.Y = p.Y - z * y;
            ClampPan(s);
            UpdateZoomUi();
            LoadFullPictureIfNeeded(s);
        }

        // Zoomed in further than the screen-sized copy has detail for: load the whole photo in the background
        // and swap it in (same shape, so nothing moves). Once per file per slot.
        void LoadFullPictureIfNeeded(Slot s)
        {
            if (!s.Reduced || s.IsVideo || IsGifFile(s.File)) return;
            var b = s.Picture.Source as BitmapSource;
            int full = Math.Max(s.FullWidth, s.FullHeight);
            if (b == null || full <= 0) return;
            double detail = (double)Math.Max(b.PixelWidth, b.PixelHeight) / full; // pixels in memory per photo pixel
            if (FitScale(s) * s.Zoom.ScaleX <= detail * 1.05) return;              // screen pixels per photo pixel
            s.Reduced = false;
            int token = s.Token;
            string file = s.File;
            ThreadPool.QueueUserWorkItem(delegate
            {
                BitmapSource whole = null;
                int w, h;
                try { whole = DecodeImage(file, MaxDecodeSide, out w, out h); }
                catch { }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (s.Token == token && whole != null && !IsGifFile(s.File)) s.Picture.Source = whole;
                }));
            });
        }

        void ClampPan(Slot s)
        {
            double z = s.Zoom.ScaleX, w = stage.ActualWidth, h = stage.ActualHeight;
            s.Pan.X = Math.Max(w - w * z, Math.Min(0, s.Pan.X));
            s.Pan.Y = Math.Max(h - h * z, Math.Min(0, s.Pan.Y));
        }

        static void ResetZoom(Slot s)
        {
            Motion.Set(s.Zoom, ScaleTransform.ScaleXProperty, 1); // (also stops a zoom or glide still running)
            Motion.Set(s.Zoom, ScaleTransform.ScaleYProperty, 1);
            Motion.Set(s.Pan, TranslateTransform.XProperty, 0);
            Motion.Set(s.Pan, TranslateTransform.YProperty, 0);
        }

        // A small viewing area (the mini player, a tiny window): the side arrows and the middle play button
        // get smaller, so they don't crowd the picture and the control bar.
        bool compactStage;

        void FitSmallControls()
        {
            bool compact = stage.ActualWidth < 560 || stage.ActualHeight < 330;
            if (compact == compactStage) return;
            compactStage = compact;
            foreach (var arrow in new[] { "Prev", "Next" })
            {
                var b = F<Button>(arrow + "Btn");
                if (compact) { b.Width = 24; b.Height = 44; }
                else { b.ClearValue(WidthProperty); b.ClearValue(HeightProperty); }
                F<Glyph>(arrow + "Glyph").Size = compact ? 11 : 13;
            }
            centerPlayBtn.LayoutTransform = compact ? new ScaleTransform(0.7, 0.7) : null;
        }

        Control[] zoomControls;

        void UpdateZoomUi()
        {
            var s = shown;
            double fit = FitScale(s);
            zoomText.Text = fit > 0 ? Math.Round(fit * s.Zoom.ScaleX * 100) + "%" : "";
            // Nothing to zoom (no file, or one that can't be shown): the zoom controls are off like the file buttons.
            if (zoomControls == null) zoomControls = new Control[] { F<Button>("FitBtn"), F<Button>("ZoomBtn"), F<Button>("ZoomOutBtn"), F<Button>("ZoomInBtn"), zoomSlider };
            if (zoomControls[0].IsEnabled != fit > 0) foreach (var c in zoomControls) c.IsEnabled = fit > 0;
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
            bool clickPlays = mouseActions["click"] == "play"; // Settings > Mouse
            if (e.ClickCount == 2)
            {
                if (video && clickToggled) TogglePlay(); // undo the first click's pause/play
                clickToggled = false;
                if (mini && mouseActions["double"] == "fullscreen") ToggleMiniSmoothly(); // from the mini player: back to the window
                else RunMouseAction("double");
                return;
            }
            clickToggled = false;
            if (shown != null && shown.Zoom.ScaleX > 1.001)
            {
                Motion.Stop("pan"); // catching a picture that's still gliding
                panning = true;
                panStart = e.GetPosition(stage);
                panVelocity = new Vector();
                panClock.Restart();
                stage.CaptureMouse();
                Cursor = Cursors.SizeAll;
            }
            else if (mini && e.ButtonState == MouseButtonState.Pressed)
            {
                double x = Left, y = Top;
                try { DragMove(); } // the mini player has no title bar: drag the video to move it
                catch { }
                bool moved = Math.Abs(Left - x) > 2 || Math.Abs(Top - y) > 2;
                if (!moved && video && clickPlays) { TogglePlay(); clickToggled = true; } // a click without dragging: play/pause
            }
            else if (video && clickPlays)
            {
                TogglePlay();
                clickToggled = true;
            }
        }

        void OnStageMove(object sender, MouseEventArgs e)
        {
            if (!panning || shown == null) return;
            var p = e.GetPosition(stage);
            var moved = p - panStart;
            shown.Pan.X += moved.X;
            shown.Pan.Y += moved.Y;
            ClampPan(shown);
            panStart = p;
            // how fast it's being dragged (smoothed), for the glide after letting go
            double dt = Math.Max(0.001, panClock.Elapsed.TotalSeconds);
            panClock.Restart();
            panVelocity = panVelocity * 0.6 + (moved / dt) * 0.4;
        }

        void OnStageUp(object sender, MouseButtonEventArgs e)
        {
            if (!panning) return;
            panning = false;
            stage.ReleaseMouseCapture();
            Cursor = null;
            // Let go while moving: the picture keeps going and slows down (about half a second), like a phone.
            var s = shown;
            if (!Motion.Moves || s == null || panClock.ElapsedMilliseconds > 80 || panVelocity.Length < 150) return;
            var v = panVelocity;
            Motion.Run("pan", dt =>
            {
                if (s != shown || panning) return false;
                // The exact distance for this frame of a glide slowing down evenly (time constant 0.16 s): it glides
                // just as far on a 60 Hz screen as on a 500 Hz one.
                double x = s.Pan.X, y = s.Pan.Y, slow = Math.Exp(-dt / 0.16), travel = 0.16 * (1 - slow);
                s.Pan.X += v.X * travel;
                s.Pan.Y += v.Y * travel;
                ClampPan(s);
                if (s.Pan.X == x) v.X = 0; // reached an edge: stop that way
                if (s.Pan.Y == y) v.Y = 0;
                v *= slow;
                return v.Length > 20;
            });
        }

        // ---------------------------------------------------------------- window, full screen

        void UpdateWindowFrame()
        {
            maxIcon.Code = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            F<Button>("MaxBtn").ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
            // A maximized window hangs over the screen edge by its (invisible) border; pad it back in.
            if (WindowState == WindowState.Maximized && !fullscreen)
            {
                double d = Dpi();
                double frame = (GetSystemMetrics(32) + GetSystemMetrics(92)) / d; // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                root.Margin = new Thickness(frame);
            }
            else root.Margin = new Thickness(0);
        }

        // The top and bottom bars float over the picture and fade with the other controls in full screen, and
        // always in looks like Clean (Look.BarsFloat), except while Settings or the grid is open. Settings docks them in
        // full screen too (they'd sit on its Back button and title); the grid covers them there instead.
        bool Floating { get { return !mini && !settingsOpen && (fullscreen || (barsFloat && !gridOpen && !infoOpen)); } }

        void PlaceBars()
        {
            if (root == null) return;
            var top = F<FrameworkElement>("TopBar");
            var bottom = F<FrameworkElement>("BottomBar");
            bool floating = Floating;
            topRow.Height = new GridLength(floating || mini ? 0 : 48);
            bottomRow.Height = floating || mini ? new GridLength(0) : GridLength.Auto;
            Grid.SetRow(top, floating ? 1 : 0);
            Grid.SetRow(bottom, floating ? 1 : 2);
            top.VerticalAlignment = floating ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            bottom.VerticalAlignment = floating ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            top.Height = floating ? 48 : double.NaN;
            Panel.SetZIndex(top, floating ? 10 : 0);
            Panel.SetZIndex(bottom, floating ? 10 : 0);
            double under = (showStrip ? 64 : 0) + (BottomControlsShown() ? 32 : 0); // what the floating bottom bar covers
            transport.Margin = new Thickness(0, 0, 0, floating ? under + 16 : 16);
            // Messages ("Update now") go below a floating top bar, which would otherwise cover them and their buttons;
            // the trim bar takes the control bar's place.
            F<FrameworkElement>("Toast").Margin = new Thickness(0, floating ? 48 + 14 : 14, 0, 0);
            F<FrameworkElement>("TrimBar").Margin = transport.Margin;
            foreach (var bar in new[] { top, bottom })
            {
                // (a see-through look: nothing of the viewer shows through the Settings page lying over it)
                Motion.Set(bar, OpacityProperty, bar == bottom && settingsOpen && seeThrough ? 0 : !floating || overlayVisible ? 1 : 0);
                Motion.Set(Motion.PartsOf(bar).Move, TranslateTransform.YProperty, 0);
                bar.IsHitTestVisible = !floating || overlayVisible;
            }
        }

        // Slides an element from (dx, dy) (times the look's distance) back to where it belongs.
        static void Glide(FrameworkElement e, double dx, double dy)
        {
            if (!Motion.Moves || e == null) return;
            var p = Motion.PartsOf(e);
            if (dx != 0) Motion.FromTo(p.Move, TranslateTransform.XProperty, dx * Motion.Distance, 0, Motion.Ms(Motion.Normal), Motion.In);
            if (dy != 0) Motion.FromTo(p.Move, TranslateTransform.YProperty, dy * Motion.Distance, 0, Motion.Ms(Motion.Normal), Motion.In);
        }

        void FadeBars(bool show)
        {
            if (!Floating) return;
            foreach (var name in new[] { "TopBar", "BottomBar" })
            {
                var bar = F<FrameworkElement>(name);
                bar.IsHitTestVisible = show; // hidden buttons mustn't be clickable
                Motion.To(bar, OpacityProperty, show ? 1 : 0, Motion.Ms(show ? fadeInMs : fadeOutMs, false), Motion.Smooth);
                if (show) Glide(bar, 0, name == "TopBar" ? -6 : 6); // floating bars slide in from their edge
            }
        }

        void ToggleFullscreenSmoothly() { FadeThrough(ToggleFullscreen); }
        void ToggleMiniSmoothly() { FadeThrough(ToggleMini); }

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
                PlaceInfo(false, false);
                stage.SetResourceReference(Panel.BackgroundProperty, "FullBg");
                cover.SetResourceReference(Border.BackgroundProperty, "FullBg");

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
                F<FrameworkElement>("MinBtn").Visibility = F<FrameworkElement>("MaxBtn").Visibility = Visibility.Collapsed; // full screen: only Close
                PlaceBars();
            }
            else
            {
                fullscreen = false;
                StopSlideshow();
                chrome.CaptionHeight = 48;
                chrome.ResizeBorderThickness = new Thickness(6);
                PlaceInfo(infoOpen, false);
                stage.SetResourceReference(Panel.BackgroundProperty, "StageBg");
                cover.SetResourceReference(Border.BackgroundProperty, "CoverBg");
                ResizeMode = ResizeMode.CanResize;
                F<FrameworkElement>("MinBtn").Visibility = F<FrameworkElement>("MaxBtn").Visibility = Visibility.Visible;
                Left = boundsBeforeFullscreen.Left;
                Top = boundsBeforeFullscreen.Top;
                Width = boundsBeforeFullscreen.Width;
                Height = boundsBeforeFullscreen.Height;
                WindowState = stateBeforeFullscreen;
                Cursor = null;
                PlaceBars();
            }
            UpdateWindowFrame();
            ApplyWindowLook(); // full screen is never see-through
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
                Shortcut sc;
                bool settingsKey = shortcutByKey.TryGetValue(Code(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers), out sc) && sc.Id == "settings";
                if (e.Key == Key.Escape && Keyboard.FocusedElement is TextBox) { Keyboard.ClearFocus(); e.Handled = true; return; } // Esc while typing: stop typing
                if (e.Key == Key.Escape || settingsKey) { CloseSettings(); e.Handled = true; } // the Settings key again closes it
                return;
            }
            if (gridOpen)
            {
                var searchKey = HandleGridSearchKey(e); // typing in the search box
                if (searchKey == true) { e.Handled = true; return; }
                if (searchKey == false) return;
                if (HandleGridKey(e)) e.Handled = true;
                return;
            }
            if (dialog.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Enter) ConfirmDelete();
                else if (e.Key == Key.Escape) HideDeleteDialog();
                e.Handled = true;
                return;
            }
            if (cropping && HandleCropKey(e)) // Crop.cs: Enter saves, Esc leaves; other files wait
            {
                e.Handled = true;
                return;
            }
            if (trimming && HandleTrimKey(e)) // Trim.cs: arrows move a handle, Enter saves, Esc leaves trimming
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape) // always: leave mini player / slideshow / full screen
            {
                if (slideshow) StopSlideshow();
                if (mini || fullscreen) FadeThrough(delegate { if (mini) ToggleMini(); if (fullscreen) ToggleFullscreen(); });
                e.Handled = true;
                return;
            }
            if (HandleShortcut(e)) e.Handled = true; // see Shortcuts.cs; changeable in Settings > Keyboard
        }
        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (gridOpen) return;
            if (mini && F<FrameworkElement>("MiniBar").IsMouseOver) { e.Handled = true; return; } // the mini player's buttons: never another file
            // Over the speaker: volume. Checked first, because pointing at the speaker opens the volume slider
            // (which handles the wheel over itself) and the speaker must keep working too.
            if (transport.Visibility == Visibility.Visible && Within(e.OriginalSource as DependencyObject, volBtn))
            {
                SetVolume(volume + (e.Delta > 0 ? 0.05 : -0.05));
                e.Handled = true;
                return;
            }
            if (volPopup.IsOpen) return;
            if (cropping) { e.Handled = true; return; } // the frame stays on this picture, at this size
            if (trimming && F<FrameworkElement>("TrimBar").IsMouseOver) { e.Handled = true; return; } // not another file
            if (transport.IsMouseOver && transport.Visibility == Visibility.Visible)
            {
                // Over the control bar: jump on the time bar, nothing elsewhere.
                if (seek.IsMouseOver) SeekBy(e.Delta > 0 ? 5 : -5);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || wheelZooms) // Ctrl+wheel always zooms, like Photos
                ZoomBy(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(stage));
            else
                Go(e.Delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        static bool Within(DependencyObject d, DependencyObject container)
        {
            while (d != null && d != container) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d == container;
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
                Motion.To(overlay, OpacityProperty, 1, Motion.Ms(fadeInMs, false), Motion.Smooth);
                FadeBars(true);
                // The video controls rise into place and the arrows come in from the window's edges.
                Glide(transport, 0, 8);
                Glide(F<FrameworkElement>("PrevBtn"), -6, 0);
                Glide(F<FrameworkElement>("NextBtn"), 6, 0);
                PlaceSubs(true);
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
            bool pointerOnControls = transport.IsMouseOver || centerPlayBtn.IsMouseOver ||
                (Floating && (F<FrameworkElement>("TopBar").IsMouseOver || F<FrameworkElement>("BottomBar").IsMouseOver));
            if (snapDir != null || volPopup.IsOpen || openMenus > 0 || files.Count == 0 || !overlayVisible || gridOpen) return;
            if (!leftWindow && ((pausedVideo && !fadePaused) || pointerOnControls)) return;
            overlayVisible = false;
            Motion.To(overlay, OpacityProperty, 0, Motion.Ms(fadeOutMs, false), Motion.Smooth);
            FadeBars(false);
            PlaceSubs(true);
            // The pointer goes too: always in full screen, and over a video in the window.
            if (fullscreen || (stage.IsMouseOver && shown != null && shown.IsVideo)) Cursor = Cursors.None;
        }

        // ---------------------------------------------------------------- settings

        // sync: re-reading what another FastViewer window saved; this window keeps its own size, place and volume.
        void LoadSettings(bool applyWindow, bool sync = false)
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                settingsSyncedAt = File.GetLastWriteTimeUtc(SettingsFile);
                if (sync)
                {
                    savedKeys.Clear();
                    hiddenButtons.Clear();
                    custom.Clear();
                    ResetMouseActions();
                    ResetMenuLayout(); // no menu line = the usual menus
                }
                var bad = new List<string>();
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    var kv = line.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    string key = kv[0].Trim(), v = kv[1].Trim();
                    try { LoadLine(key, v, applyWindow, sync); }
                    catch { bad.Add(line); } // one unreadable line (edited by hand, damaged file) mustn't lose the settings after it
                }
                if (bad.Count > 0 && !sync)
                    Program.WriteCrash(new Exception("Skipped unreadable lines in " + SettingsFile + ": " + string.Join(" | ", bad)));
            }
            catch { }
        }

        void LoadLine(string key, string v, bool applyWindow, bool sync)
        {
            switch (key)
            {
                case "volume": if (!sync) volume = Clamp(double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture), 0, 1, 0.8); break;
                case "muted": if (!sync) muted = v == "1"; break;
                case "loop": loop = v == "1"; break;
                default: LoadOption(key, v); break;
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

        // A number from settings.ini within its range (NaN, infinity: the default).
        static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        bool settingsOverride;
        DateTime settingsSyncedAt; // the settings file's time when this window last read or wrote it

        // Several FastViewer windows can be open (and one waiting in the background). Each window re-reads the
        // settings when it comes to the front if another one saved meanwhile, so a change made in one window
        // (Dim video off, a look, a key...) reaches the others, and an old window can't undo it later.
        void SyncSettings()
        {
            if ((selfTestInterval > 0 || snapDir != null || fadeTestLog != null) && !settingsOverride) return;
            try
            {
                if (!File.Exists(SettingsFile) || File.GetLastWriteTimeUtc(SettingsFile) <= settingsSyncedAt) return;
            }
            catch { return; }
            int oldFilter = filter, oldOrder = order;
            LoadSettings(false, true);
            if (root == null) return;
            if (slots.Length != 2 * preloadAhead + 2) SetPreload(preloadAhead); // Preload changed in another window: as many players as it needs
            ApplyLook();
            if (shortcuts != null)
            {
                ApplySavedKeys();
                RefreshShortcutEditors();
            }
            idleTimer.Interval = TimeSpan.FromSeconds(hideDelay > 0 ? hideDelay : 2.5);
            slideTimer.Interval = TimeSpan.FromSeconds(slideSeconds);
            Topmost = pinned || mini;
            UpdateSkipButtons();
            ApplyStretch();
            ApplyStrip();
            F<TextBlock>("GridSizeText").Text = GridSizeNames[gridSize];
            F<TextBlock>("GridSortText").Text = GridSortShort[gridSort];
            UpdateGridNamesButton();
            ApplyButtons(); // also the video controls and the dimming
            cueShown = -2; SizeSubs(); UpdateSubButton(); SubTick(); // subtitles on/off or their size
            UpdateRotateButton();
            foreach (var refresh in settingsRefresh) refresh();
            if (filter != oldFilter || order != oldOrder) ReloadFolder();
        }

        // syncFirst: saving when the window closes or goes to the background, with no change of its own to keep:
        // first take in what other windows saved, then add this window's size, place and volume.
        void SaveSettings(bool syncFirst = false)
        {
            if ((selfTestInterval > 0 || snapDir != null || fadeTestLog != null) && !settingsOverride) return;
            if (syncFirst) SyncSettings();
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
                }.Concat(OptionLines()).Concat(ShortcutLines()).Concat(MouseLines()));
                settingsSyncedAt = File.GetLastWriteTimeUtc(SettingsFile);
            }
            catch (Exception ex) { Program.WriteCrash(new Exception("Settings couldn't be saved to " + SettingsFile, ex)); }
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

        // Test runs: the window is "cloaked" before it first shows, as Windows does with windows on another
        // virtual desktop: it's still drawn (so pictures and pixel checks work) but never seen, also in full
        // screen or as the mini player. And never on top: asking for that (mini player, Keep on top) only notes it.
        void KeepTestOutOfSight()
        {
            if (testInFront) return;
            SourceInitialized += delegate
            {
                int on = 1; // DWMWA_CLOAK
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 13, ref on, 4);
            };
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TopmostProperty, typeof(Window))
                .AddValueChanged(this, delegate { if (Topmost) { testLog.Add("asked to stay on top (not in tests)"); Topmost = false; } });
        }

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
