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

        // A plain square region the size of the window (no shadow or frame drawn around it).
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

        void Hook()
        {
            var c = Code;
            if (string.IsNullOrEmpty(c)) ClearValue(ShapeProperty);
            else SetResourceReference(ShapeProperty, "Ic." + ((int)c[0]).ToString("X4"));
        }

        protected override Size MeasureOverride(Size available) { return new Size(Size, Size); }

        protected override void OnRender(DrawingContext dc)
        {
            var c = Code;
            if (string.IsNullOrEmpty(c)) return;
            double s = Size, w = ActualWidth, h = ActualHeight;
            var shape = GetValue(ShapeProperty) as Geometry;
            if (shape != null)
            {
                double k = s * 1.3 / 24; // line icons look right a little bigger than the symbol font
                double stroke = GetValue(StrokeProperty) is double ? (double)GetValue(StrokeProperty) : 1.7;
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
}
