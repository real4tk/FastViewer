// FastViewer: looks (themes). Each look is one XAML dictionary, Looks.<Id>.xaml, embedded in the program:
// its colours for dark and light ("Dark.X" / "Light.X"), fonts, corner radii, icon set and motion. Apply copies
// the right half into the app's resources, and everything on screen follows through DynamicResource, so
// switching is instant. A new look is one new file plus its /resource line in build.cmd.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace FastViewer
{
    class Look
    {
        public string Id, Name, Description;
        public bool HasLight;
        public int Order;
        public ResourceDictionary Source;

        public object Get(string key) { return Source.Contains(key) ? Source[key] : null; }
        public bool Flag(string key) { var v = Get(key); return v is bool && (bool)v; }
    }

    static class Looks
    {
        public const string DefaultId = "Photos";
        static List<Look> all;
        static ResourceDictionary lineIcons;
        public static Look Current { get; private set; }
        public static bool Light { get; private set; }

        static readonly Dictionary<string, Look> loaded = new Dictionary<string, Look>(StringComparer.OrdinalIgnoreCase);

        // Every look, for Settings > Appearance (read the first time that page opens; starting only reads the one in use).
        public static List<Look> All
        {
            get
            {
                if (all != null) return all;
                all = new List<Look>();
                foreach (var name in typeof(Looks).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("FastViewer.Looks.") && n.EndsWith(".xaml")))
                    all.Add(Load(name.Substring(17, name.Length - 22)));
                all.Sort((a, b) => a.Order.CompareTo(b.Order));
                return all;
            }
        }

        static Look Load(string id)
        {
            Look look;
            if (loaded.TryGetValue(id, out look)) return look;
            var name = "FastViewer.Looks." + id + ".xaml";
            if (typeof(Looks).Assembly.GetManifestResourceInfo(name) == null) return null;
            look = new Look { Id = id, Source = Ui.Load<ResourceDictionary>(name) };
            look.Name = (look.Get("Look.Name") as string) ?? look.Id;
            look.Description = (look.Get("Look.Description") as string) ?? "";
            look.HasLight = look.Flag("Look.HasLight");
            look.Order = look.Get("Look.Order") is int ? (int)look.Get("Look.Order") : 99;
            loaded[id] = look;
            return look;
        }

        // The custom look (CustomLook.cs): built from a look plus your changes, not from a file.
        public static Look Custom;

        public static Look Find(string id)
        {
            if (id == "Custom" && Custom != null) return Custom;
            return (string.IsNullOrEmpty(id) ? null : Load(id)) ?? Load(DefaultId);
        }

        public static bool WindowsUsesLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        public static bool IsLight(Look look, string mode)
        {
            return look.HasLight && (mode == "Light" || (mode != "Dark" && WindowsUsesLight()));
        }

        // Puts the look's values (the dark or light half) into `target`: the app for real, or a preview card
        // (fonts = false: previews don't need them, and loading Inter just for a preview would be wasted).
        public static void Fill(ResourceDictionary target, Look look, bool light, bool fonts = true)
        {
            string mine = light ? "Light." : "Dark.", other = light ? "Dark." : "Light.";
            foreach (DictionaryEntry e in look.Source)
            {
                var key = e.Key as string;
                if (key == null || key.StartsWith(other) || (!fonts && key.StartsWith("Font."))) continue;
                target[key.StartsWith(mine) ? key.Substring(mine.Length) : key] = Resolve(e.Value);
            }
            if (!look.Source.Contains(mine + "Accent")) // the Windows accent colour, unless the look has its own
            {
                var accent = new SolidColorBrush(Ui.AccentColor(light));
                accent.Freeze();
                target["Accent"] = accent;
            }
            target["Look.IsLight"] = light;
            KeepSwitchKnobVisible(target);
        }

        // A switch's On knob sits on the accent. Looks pick one knob colour for their usual accent, but the accent can be
        // anything (Windows' own, or a Custom look's): a dark accent with a black knob drew a black pill with no knob.
        // So when the knob can't be seen on the accent, it becomes black or white, whichever stands out more.
        internal static void KeepSwitchKnobVisible(ResourceDictionary target)
        {
            var accent = target["Accent"] as SolidColorBrush;
            var knob = target["SwitchKnobOn"] as SolidColorBrush;
            if (accent == null || knob == null) return;
            var a = Flatten(accent.Color);
            // The On track's outline: the accent itself, unless the accent nearly disappears into the cards behind it (a dark
            // accent on a dark look): then an outline in the text colour, so an On switch never looks like an empty dark blob.
            var card = target["CardBg"] as SolidColorBrush;
            var text = target["TextSecondary"] as SolidColorBrush;
            target["SwitchOnEdge"] = card != null && text != null && Contrast(a, Flatten(card.Color)) < 1.4 ? (object)text : accent;
            if (Contrast(knob.Color, a) >= 2.5) return;
            var better = new SolidColorBrush(Contrast(Colors.Black, a) >= Contrast(Colors.White, a) ? Colors.Black : Colors.White);
            better.Freeze();
            target["SwitchKnobOn"] = better;
        }

        static Color Flatten(Color c) { return c.A == 255 ? c : Color.FromRgb((byte)(c.R * c.A / 255), (byte)(c.G * c.A / 255), (byte)(c.B * c.A / 255)); }

        internal static double Contrast(Color a, Color b)
        {
            Func<Color, double> lum = c =>
            {
                Func<byte, double> ch = v => { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
                return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
            };
            double l1 = lum(a), l2 = lum(b);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        public static void Apply(Look look, string mode)
        {
            var r = Application.Current.Resources;
            Light = IsLight(look, mode);
            Fill(r, look, Light);
            // Icon set: the look's own line icons, or (none) the Segoe icons every Glyph falls back to.
            bool lines = (look.Get("Look.Icons") as string) == "Lines";
            if (lineIcons == null && lines) lineIcons = Ui.Load<ResourceDictionary>("FastViewer.Icons.Lines.xaml");
            if (lineIcons != null)
            {
                r.MergedDictionaries.Remove(lineIcons);
                if (lines) r.MergedDictionaries.Add(lineIcons);
            }
            Current = look;
        }

        // "@Inter" in a look means the Inter font that travels inside FastViewer (or SF Pro, if it's installed).
        static object Resolve(object v)
        {
            var s = v as string;
            if (s == null || !s.StartsWith("@")) return v;
            if (s.StartsWith("@Inter")) return InterFont(s.Contains("Display"));
            return new FontFamily(s.Substring(1));
        }

        static FontFamily inter, interDisplay;

        static FontFamily InterFont(bool display)
        {
            if (inter != null) return display ? interDisplay : inter;
            var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToList();
            if (installed.Contains("SF Pro Text") && installed.Contains("SF Pro Display"))
            {
                inter = new FontFamily("SF Pro Text, Segoe UI");
                interDisplay = new FontFamily("SF Pro Display, Segoe UI");
            }
            else
            {
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer", "fonts");
                    Directory.CreateDirectory(dir);
                    var asm = typeof(Looks).Assembly;
                    foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("FastViewer.fonts.")))
                    {
                        var file = Path.Combine(dir, name.Substring("FastViewer.fonts.".Length));
                        using (var s = asm.GetManifestResourceStream(name))
                        {
                            if (File.Exists(file) && new FileInfo(file).Length == s.Length) continue;
                            using (var f = File.Create(file)) s.CopyTo(f);
                        }
                    }
                    var baseUri = new Uri(dir.TrimEnd('\\') + "\\");
                    inter = new FontFamily(baseUri, "./#Inter, Segoe UI");
                    interDisplay = inter;
                }
                catch
                {
                    inter = interDisplay = new FontFamily("Segoe UI");
                }
            }
            return display ? interDisplay : inter;
        }

        // ---- See-through windows (Liquid Glass): Windows blurs whatever is behind the window ----

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int State, Flags; public uint Color; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)]
        struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

        public static void SetBlurBehind(IntPtr hwnd, bool on)
        {
            var accent = new AccentPolicy { State = on ? 3 : 0 }; // 3: blur behind (smooth while dragging, unlike acrylic on Windows 10)
            int size = Marshal.SizeOf(accent);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new CompositionData { Attribute = 19, Data = p, Size = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr hwnd, out RgnBox r);
        [StructLayout(LayoutKind.Sequential)] struct RgnBox { public int Left, Top, Right, Bottom; }
        // (1.31.1) The window has the big square region (SetSquareRegion with 32767): nothing to set again.
        public static bool HasBigRegion(IntPtr hwnd)
        {
            RgnBox r;
            return GetWindowRgnBox(hwnd, out r) == 2 && r.Left == 0 && r.Top == 0 && r.Right >= 32767 && r.Bottom >= 32767; // SIMPLEREGION
        }

        // A plain square region (no shadow or frame drawn around it); 1.31.1: always far bigger than the window.
        public static void SetSquareRegion(IntPtr hwnd, int width, int height)
        {
            SetWindowRgn(hwnd, CreateRectRgn(0, 0, width, height), true);
        }

        // Rounded corners (radius in pixels; 0 = the normal square window).
        public static void SetRoundCorners(IntPtr hwnd, int width, int height, int radius)
        {
            SetWindowRgn(hwnd, radius <= 0 ? IntPtr.Zero : CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2), true);
        }
    }

    // A damped spring like iOS uses: quick, a few percent of overshoot, settles by the end (from Image Converter).
    public class SpringEase : EasingFunctionBase
    {
        // Zeta: damping (lower = more overshoot and wobble), Omega: stiffness (over the animation's length).
        readonly double Zeta = 0.74, Omega = 12.5;

        public SpringEase() { EasingMode = EasingMode.EaseIn; }
        public SpringEase(double zeta, double omega) : this() { Zeta = zeta; Omega = omega; }

        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            double wd = Omega * Math.Sqrt(1 - Zeta * Zeta);
            return 1 - Math.Exp(-Zeta * Omega * t) * (Math.Cos(wd * t) + Zeta * Omega / wd * Math.Sin(wd * t));
        }

        protected override Freezable CreateInstanceCore() { return new SpringEase(Zeta, Omega); }
    }

    // An icon. Code is the Segoe MDL2 symbol; when the look has its own line icon for that symbol (resource
    // "Ic.<code>", a 24 x 24 outline) that is drawn instead. Takes its colour from the text around it.
    public class Glyph : FrameworkElement
    {
        static readonly FontFamily Mdl2 = new FontFamily("Segoe MDL2 Assets");

        public static readonly DependencyProperty CodeProperty = DependencyProperty.Register("Code", typeof(string), typeof(Glyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Glyph)d).Hook()));
        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register("Size", typeof(double), typeof(Glyph),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register("Shape", typeof(object), typeof(Glyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(object), typeof(Glyph),
            new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Glyph),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public Glyph()
        {
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
            SetResourceReference(StrokeProperty, "Look.IconStroke");
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Center;
        }

        public string Code { get { return (string)GetValue(CodeProperty); } set { SetValue(CodeProperty, value); } }
        public double Size { get { return (double)GetValue(SizeProperty); } set { SetValue(SizeProperty, value); } }
        public Brush Foreground { get { return (Brush)GetValue(ForegroundProperty); } set { SetValue(ForegroundProperty, value); } }

        // Icons drawn as lines in every look (1.27.0), on the same 24 x 24 grid as the line icons.
        // RotateLeft: the "rotate" icon of Tabler Icons (tabler.io/icons, MIT licence, Copyright (c) 2020-2024 Pawel Kuna),
        // "M19.95 11a8 8 0 1 0 -.5 4m.5 5v-5h-5"; Rotate right is the same, mirrored.
        internal const string RotateLeft = "";
        static readonly System.Collections.Generic.Dictionary<string, Geometry> Always = MakeAlways();
        static System.Collections.Generic.Dictionary<string, Geometry> MakeAlways()
        {
            var g = Geometry.Parse("M19.95,11 A8,8 0 1 0 19.45,15 M19.95,20 V15 H14.95");
            g.Freeze();
            return new System.Collections.Generic.Dictionary<string, Geometry> { { RotateLeft, g } };
        }
        bool always;
        internal static double SegoeStroke = 1.0;

        // 1.28.0: the skip buttons as they are on the video bar (circle arrow with the seconds inside), for Settings and the
        // menus. The code alone shows the current amount; with digits after it (SkipBack + "5") that fixed number.
        internal const string SkipBack = "", SkipForward = "";
        static string skipBackText = "10", skipForwardText = "30";
        static readonly List<WeakReference> skipGlyphs = new List<WeakReference>();
        static readonly Geometry BackArc = Frozen("M4.07,7 A8,8 0 1 0 11,3"), BackHead = Frozen("M7.4,3 L11.2,0.4 L11.2,5.6 Z"),
            FwdArc = Frozen("M17.93,7 A8,8 0 1 1 11,3"), FwdHead = Frozen("M14.6,3 L10.8,0.4 L10.8,5.6 Z");
        static Geometry Frozen(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
        bool IsSkip { get { var c = Code; return !string.IsNullOrEmpty(c) && (c[0] == SkipBack[0] || c[0] == SkipForward[0]); } }

        // The amounts changed (Settings > Videos): every skip icon on screen shows the new one.
        internal static void SetSkips(int back, int forward)
        {
            skipBackText = back.ToString(CultureInfo.InvariantCulture);
            skipForwardText = forward.ToString(CultureInfo.InvariantCulture);
            lock (skipGlyphs)
            {
                skipGlyphs.RemoveAll(w => !w.IsAlive);
                foreach (var w in skipGlyphs) { var g = w.Target as Glyph; if (g != null) g.InvalidateVisual(); }
            }
        }

        void Hook()
        {
            var c = Code;
            if (IsSkip)
            {
                ClearValue(ShapeProperty);
                always = true;
                if (c.Length == 1) lock (skipGlyphs) skipGlyphs.Add(new WeakReference(this));
                return;
            }
            always = !string.IsNullOrEmpty(c) && Always.ContainsKey(c);
            if (string.IsNullOrEmpty(c)) ClearValue(ShapeProperty);
            else if (always) SetValue(ShapeProperty, Always[c]);
            else SetResourceReference(ShapeProperty, "Ic." + ((int)c[0]).ToString("X4"));
        }

        protected override Size MeasureOverride(Size available) { return new Size(Size, Size); }

        protected override void OnRender(DrawingContext dc)
        {
            var c = Code;
            if (string.IsNullOrEmpty(c)) return;
            double s = Size, w = ActualWidth, h = ActualHeight;
            if (IsSkip)
            {
                // The video bar's 22 x 22 drawing (line 1.6), as big as the line icons are drawn here.
                bool back = c[0] == SkipBack[0];
                string number = c.Length > 1 ? c.Substring(1) : back ? skipBackText : skipForwardText;
                double k = s * 1.3 / 22;
                dc.PushTransform(new TranslateTransform((w - 22 * k) / 2, (h - 22 * k) / 2));
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawGeometry(null, new Pen(Foreground, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, back ? BackArc : FwdArc);
                dc.DrawGeometry(Foreground, null, back ? BackHead : FwdHead);
                var digits = new SkipNumber { Text = number, FontFamily = (FontFamily)GetValue(TextElement.FontFamilyProperty) }.Outline();
                if (digits != null)
                {
                    dc.PushTransform(new TranslateTransform(11, 11));
                    dc.DrawGeometry(Foreground, null, digits);
                    dc.Pop();
                }
                dc.Pop();
                dc.Pop();
                return;
            }
            var shape = GetValue(ShapeProperty) as Geometry;
            if (shape != null)
            {
                double k = s * 1.3 / 24; // line icons look right a little bigger than the symbol font
                double stroke = GetValue(StrokeProperty) is double ? (double)GetValue(StrokeProperty) : 1.7;
                // In a look with Segoe icons, as heavy as those (their lines are 1 px at 16 px).
                if (always && (Looks.Current == null || (Looks.Current.Get("Look.Icons") as string) != "Lines")) stroke = SegoeStroke * s / 16;
                // Drawn on the icon's 24 x 24 grid, so the line width is divided by the same scale.
                var pen = new Pen(Foreground, stroke / k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.PushTransform(new TranslateTransform((w - 24 * k) / 2, (h - 24 * k) / 2));
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawGeometry(null, pen, shape);
                dc.Pop();
                dc.Pop();
                return;
            }
            var text = new FormattedText(c, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mdl2, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), s, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((w - text.WidthIncludingTrailingWhitespace) / 2, (h - text.Height) / 2));
        }
    }

    // The seconds inside a skip button (1.27.0). Drawn as the outline of the digits, whose box is put exactly in the
    // middle of the circle: a TextBlock centres its line (with room for accents above and below), which left the
    // number low and a little to one side. All amounts get the same digit height; two digits never touch the circle.
    public class SkipNumber : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(SkipNumber),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SkipNumber),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(SkipNumber),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text { get { return (string)GetValue(TextProperty); } set { SetValue(TextProperty, value); } }
        public Brush Foreground { get { return (Brush)GetValue(ForegroundProperty); } set { SetValue(ForegroundProperty, value); } }
        public FontFamily FontFamily { get { return (FontFamily)GetValue(FontFamilyProperty); } set { SetValue(FontFamilyProperty, value); } }

        // In the button's 22 x 22 box (circle of radius 8 round the middle, line 1.6): digit height and the widest a
        // number may be.
        public static double DigitHeight = 7.2, Widest = 9.6;

        public SkipNumber() { IsHitTestVisible = false; SnapsToDevicePixels = false; }

        // Made once per amount and font (drawing it again, e.g. on hover, costs nothing).
        static readonly Dictionary<string, Geometry> made = new Dictionary<string, Geometry>();

        // The outline centred on (0, 0) at the size it is drawn, or null.
        public Geometry Outline()
        {
            var t = Text;
            if (string.IsNullOrEmpty(t)) return null;
            string key = (FontFamily == null ? "" : FontFamily.Source) + "|" + t;
            Geometry known;
            if (made.TryGetValue(key, out known)) return known;
            known = Shape(t);
            if (known != null) { known.Freeze(); made[key] = known; }
            return known;
        }

        Geometry Shape(string t)
        {
            var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 100, Brushes.Black, 1.0);
            var g = ft.BuildGeometry(new Point(0, 0));
            var b = g.Bounds;
            if (b.IsEmpty || b.Height <= 0) return null;
            // The same height for every amount: the height of the digits 0-9 together (so a "1" or a "7" isn't blown up).
            var all = new FormattedText("0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 100, Brushes.Black, 1.0).BuildGeometry(new Point(0, 0)).Bounds;
            double k = DigitHeight / all.Height, kx = k;
            if (b.Width * kx > Widest) kx = Math.Max(Widest / b.Width, k * 0.8); // two digits: a little narrower, same height
            if (b.Width * kx > Widest) { k *= Widest / (b.Width * kx); kx = Widest / b.Width; } // (only a very wide number gets smaller)
            var tg = new TransformGroup();
            tg.Children.Add(new TranslateTransform(-(b.Left + b.Width / 2), -(all.Top + all.Height / 2)));
            tg.Children.Add(new ScaleTransform(kx, k));
            var shaped = g.Clone();
            shaped.Transform = tg;
            return shaped;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var g = Outline();
            if (g == null) return;
            dc.PushTransform(new TranslateTransform(ActualWidth / 2, ActualHeight / 2));
            dc.DrawGeometry(Foreground, null, g);
            dc.Pop();
        }
    }
}
