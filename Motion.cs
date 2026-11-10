// FastViewer: one animation system for everything that moves (1.16.0).
//
// Timing and easing come from the look (Looks.<Id>.xaml: Motion.Fast / Normal / Slow, Motion.Ease,
// Motion.Distance, Motion.Press), so every part of the app moves the same way: Photos like Windows 11
// (decelerate, no overshoot), Clean subtle, Liquid Glass springy, Midnight calm. Settings > Appearance >
// Animations picks Full, Reduced (short fades only) or Off; "Like Windows" follows Windows' own
// "Show animations" switch.
//
// Every animation starts from wherever the thing is right now, so a new action never makes anything jump.
// Only opacity and render transforms are animated (no layout), and nothing ticks once things are still:
// finished animations hand their end value back as a plain value, and the per-frame mover unhooks itself.
// Switching files never waits for an animation.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FastViewer
{
    enum MotionLevel { Full, Reduced, Off }

    static class Motion
    {
        public static MotionLevel Level = MotionLevel.Full;
        public static double Fast = 167, Normal = 250, Slow = 333; // milliseconds, from the look
        public static double Distance = 1;                         // how far things travel (x the base distance)
        public static double Press = 1;                            // pressed buttons shrink to this (1 = they don't)
        // decelerate | spring | calm (the built-in looks); subtle | bouncy | drift | snappy (Custom look styles)
        public static string Style = "decelerate";
        public static bool Squish;                                 // Liquid Glass: buttons squish and wobble back like iOS
        // The Custom look's Speed (0.4x Fastest .. 3x Super slow): every animation's length is multiplied by it in
        // Ms(), including the fixed ones (menus, dialogs, messages), so everything that opens or closes follows it.
        // Things that follow your hand (zoom, panning, wheel scrolling) use Follows() and keep their pace.
        public static double Speed = 1;

        public static bool Moves { get { return Level == MotionLevel.Full; } }      // movement and scaling
        public static bool Animates { get { return Level != MotionLevel.Off; } }    // at least fades

        static readonly IEasingFunction decelerate = Frozen(new BezierEase(0, 0, 0, 1));   // Windows 11's "fast out, slow in"
        static readonly IEasingFunction accelerate = Frozen(new BezierEase(0.7, 0, 1, 0.5)); // for things leaving
        static readonly IEasingFunction calm = Frozen(new SineEase { EasingMode = EasingMode.EaseOut });
        static readonly IEasingFunction spring = Frozen(new SpringEase());
        // Custom look styles, each with its own character:
        static readonly IEasingFunction subtle = Frozen(new BezierEase(0.25, 0.1, 0.25, 1));  // gentle, even, no drama
        static readonly IEasingFunction bouncy = Frozen(new SpringEase(0.42, 13));           // ~23% overshoot, a visible wobble
        static readonly IEasingFunction drift = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut }); // eases in and out slowly
        static readonly IEasingFunction snappy = Frozen(new BezierEase(0.16, 1, 0.3, 1));    // almost there at once, then a short settle
        static readonly IEasingFunction squishBack = Frozen(new SpringEase(0.32, 16));       // a pressed Liquid Glass button wobbling back

        static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

        // Easing for things arriving or moving into place, per look.
        public static IEasingFunction In
        {
            get
            {
                switch (Style)
                {
                    case "spring": return spring;
                    case "calm": return calm;
                    case "subtle": return subtle;
                    case "bouncy": return bouncy;
                    case "drift": return drift;
                    case "snappy": return snappy;
                    default: return decelerate;
                }
            }
        }
        public static IEasingFunction Out { get { return accelerate; } }
        // Never overshoots (fades, colours).
        public static IEasingFunction Smooth
        {
            get
            {
                switch (Style)
                {
                    case "calm": return calm;
                    case "subtle": return subtle;
                    case "drift": return drift;
                    case "snappy": return snappy;
                    default: return decelerate;
                }
            }
        }

        // A duration in this level: springs need longer to settle; Reduced keeps only short fades.
        public static double Ms(double ms, bool moving = true)
        {
            if (Level == MotionLevel.Off) return 0;
            if (Level == MotionLevel.Reduced) return Math.Min(ms, 120);
            if (!moving) return ms * Speed;
            return (Style == "spring" ? ms * 1.7 : Style == "bouncy" ? ms * 2 : ms) * Speed;
        }

        // A settle time for things that follow the mouse or wheel (not scaled by Speed, so they stay responsive).
        public static double Follows(double ms)
        {
            if (Level == MotionLevel.Off) return 0;
            if (Level == MotionLevel.Reduced) return Math.Min(ms, 120);
            return Style == "spring" ? ms * 1.7 : Style == "bouncy" ? ms * 2 : ms;
        }

        // ---------------------------------------------------------------- squishy buttons (Liquid Glass)

        // Pressing squishes the button in quickly; letting go springs it back past its size and lets it wobble to
        // rest. A quick tap (released before the squish got deep) still plays the whole squish first, so even fast
        // clicks feel like pressing something soft. Each step starts from where the button is right now.
        public const double SquishDepth = 0.86;
        public const double SquishInMs = 70;

        public static void SquishIn(ScaleTransform s)
        {
            if (!Moves) return;
            To(s, ScaleTransform.ScaleXProperty, SquishDepth - 0.02, SquishInMs * Speed, Smooth); // a touch wider than tall: squashed
            To(s, ScaleTransform.ScaleYProperty, SquishDepth + 0.01, SquishInMs * Speed, Smooth);
        }

        public static void SquishOut(ScaleTransform s)
        {
            if (!Moves) { Set(s, ScaleTransform.ScaleXProperty, 1); Set(s, ScaleTransform.ScaleYProperty, 1); return; }
            double reached = 1 - s.ScaleX;
            Action back = delegate
            {
                double ms = Fast * 3.2 * Speed; // the wobble needs time to settle (~530 ms with Liquid Glass' timing)
                To(s, ScaleTransform.ScaleXProperty, 1, ms, squishBack);
                To(s, ScaleTransform.ScaleYProperty, 1, ms * 1.06, squishBack); // slightly out of step: jelly, not a zoom
            };
            if (reached < (1 - SquishDepth) * 0.7)
            {
                // A quick tap: finish the squish first (short, since part of it may already be done), then spring back.
                double left = SquishInMs * Speed * Math.Max(0.4, 1 - reached / (1 - SquishDepth));
                To(s, ScaleTransform.ScaleXProperty, SquishDepth - 0.02, left, Smooth, back);
                To(s, ScaleTransform.ScaleYProperty, SquishDepth + 0.01, left, Smooth);
            }
            else back();
        }

        // WPF runs its animations at 60 frames a second unless told otherwise; on a 120, 144, 240 or 500 Hz screen
        // that looks less smooth than everything else. Animations run at the screen's own rate instead (only while
        // something moves; nothing runs when still). With several screens: the fastest one, so a window on it
        // gets every frame (on a slower screen Windows simply shows fewer).
        public static void UseScreenRate()
        {
            int hz = 0;
            try
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                for (int i = 0; EnumDisplayDevices(null, i, ref device, 0); i++, device.Size = Marshal.SizeOf(typeof(DisplayDevice)))
                {
                    if ((device.Flags & 1) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                    var mode = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
                    if (EnumDisplaySettings(device.Name, -1, ref mode) && mode.Frequency > 1) hz = Math.Max(hz, mode.Frequency); // -1: the current mode
                }
                if (hz == 0)
                {
                    var mode = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
                    if (EnumDisplaySettings(null, -1, ref mode) && mode.Frequency > 1) hz = mode.Frequency;
                }
            }
            catch { }
            if (hz < 60) hz = 60;
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)Math.Min(1000, hz) });
            ScreenRate = hz;
        }

        public static int ScreenRate = 60;

        // Frames a second the last animation really got (median frame time), shown in Settings > About.
        public static double LastFps;
        static readonly List<double> frameTimes = new List<double>();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, int index, ref DisplayDevice displayDevice, int flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields, PositionX, PositionY, Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, Frequency;
            public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        // ---------------------------------------------------------------- animating one value

        // Every value Motion moves is stepped once per screen frame by the same frame hook as the movers below
        // (WPF's own animation clock often runs at 60 frames a second even on a 120/144 Hz screen). A value
        // being moved: where it started, where it's going, how long, and what to do when it gets there.
        class Tween
        {
            public DependencyObject Target;
            public DependencyProperty Property;
            public double From, To, Ms, Elapsed;
            public IEasingFunction Ease;
            public Action Done;
            public bool Started;
        }

        static readonly Dictionary<DependencyObject, Dictionary<DependencyProperty, Tween>> tweens =
            new Dictionary<DependencyObject, Dictionary<DependencyProperty, Tween>>();
        static readonly object tweenKey = new object();

        static Tween Running(DependencyObject d, DependencyProperty p)
        {
            Dictionary<DependencyProperty, Tween> map;
            Tween t;
            return tweens.TryGetValue(d, out map) && map.TryGetValue(p, out t) ? t : null;
        }

        static void Forget(DependencyObject d, DependencyProperty p)
        {
            Dictionary<DependencyProperty, Tween> map;
            if (!tweens.TryGetValue(d, out map)) return;
            map.Remove(p);
            if (map.Count == 0) tweens.Remove(d);
        }

        // True while Motion is moving any value of this object.
        public static bool Busy(DependencyObject d) { return tweens.ContainsKey(d); }

        // Tests: what is moving right now (movers by key, tweens by element and property).
        public static IEnumerable<string> Active()
        {
            foreach (var m in movers.Keys) if (m != tweenKey) yield return "mover " + m;
            foreach (var t in tweens)
                foreach (var p in t.Value.Keys)
                {
                    var fe = t.Key as FrameworkElement;
                    yield return "tween " + (fe != null && fe.Name.Length > 0 ? fe.Name : t.Key.GetType().Name) + "." + p.Name;
                }
        }

        // Moves one property to a value. Starts from where it is now (also mid-movement), so nothing ever jumps;
        // a newer move of the same value takes over (and the older one's `done` never runs). When it arrives the
        // value is just a plain value, and nothing keeps running.
        public static void To(DependencyObject d, DependencyProperty p, double to, double ms, IEasingFunction ease = null, Action done = null)
        {
            var wpf = d as IAnimatable;
            if (wpf != null && wpf.HasAnimatedProperties) wpf.BeginAnimation(p, null); // (a WPF animation left on it)
            if (ms <= 0)
            {
                Forget(d, p);
                d.SetValue(p, to);
                if (done != null) done();
                return;
            }
            Dictionary<DependencyProperty, Tween> map;
            if (!tweens.TryGetValue(d, out map)) tweens[d] = map = new Dictionary<DependencyProperty, Tween>();
            map[p] = new Tween { Target = d, Property = p, From = (double)d.GetValue(p), To = to, Ms = ms, Ease = ease, Done = done };
            Run(tweenKey, StepTweens);
        }

        static bool StepTweens(double dt)
        {
            var arrived = new List<Tween>();
            foreach (var map in new List<Dictionary<DependencyProperty, Tween>>(tweens.Values))
                foreach (var t in new List<Tween>(map.Values))
                {
                    // A value's first step is never more than one screen frame: whatever was set up in the same
                    // moment (a page being built, a window resizing) took time it shouldn't lose from its start.
                    double step = dt * 1000;
                    if (!t.Started) { t.Started = true; step = Math.Min(step, firstStepCap * 1000); }
                    t.Elapsed += step;
                    double k = Math.Min(1, t.Elapsed / t.Ms);
                    double eased = t.Ease == null ? k : t.Ease.Ease(k);
                    t.Target.SetValue(t.Property, k >= 1 ? t.To : t.From + (t.To - t.From) * eased);
                    if (k >= 1) arrived.Add(t);
                }
            foreach (var t in arrived)
            {
                if (Running(t.Target, t.Property) != t) continue; // replaced meanwhile
                Forget(t.Target, t.Property);
                if (t.Done != null) t.Done();
            }
            return tweens.Count > 0;
        }

        public static void FromTo(DependencyObject d, DependencyProperty p, double from, double to, double ms, IEasingFunction ease = null, Action done = null)
        {
            Forget(d, p);
            d.SetValue(p, ms > 0 ? from : to);
            To(d, p, to, ms, ease, done);
        }

        // Stops whatever moves the property and sets it.
        public static void Set(DependencyObject d, DependencyProperty p, double value)
        {
            Forget(d, p);
            var wpf = d as IAnimatable;
            if (wpf != null && wpf.HasAnimatedProperties) wpf.BeginAnimation(p, null);
            d.SetValue(p, value);
        }
        // ---------------------------------------------------------------- showing and hiding an element

        // The element's own scale + offset (set up once; RenderTransformOrigin decides where it scales from).
        public class Parts { public ScaleTransform Scale = new ScaleTransform(1, 1); public TranslateTransform Move = new TranslateTransform(); public TransformGroup Group; }
        static readonly ConditionalWeakTable<UIElement, Parts> parts = new ConditionalWeakTable<UIElement, Parts>();

        // An element's own scale + offset. A transform the element already has (from its XAML, or one its code
        // moves, like the side arrows' slide) is kept in the middle of the group, so both work together: scale
        // first (around RenderTransformOrigin), then its own transform, then Motion's offset. If the element's
        // code later puts a new transform on it, the group is rebuilt around that one (running moves continue).
        public static Parts PartsOf(UIElement e)
        {
            Parts p;
            if (parts.TryGetValue(e, out p) && e.RenderTransform == p.Group) return p;
            if (p == null) { p = new Parts(); parts.Add(e, p); }
            var g = new TransformGroup();
            g.Children.Add(p.Scale);
            var own = e.RenderTransform;
            if (own != null && own != Transform.Identity && !(own is MatrixTransform && ((MatrixTransform)own).Matrix.IsIdentity)) g.Children.Add(own);
            g.Children.Add(p.Move);
            p.Group = g;
            e.RenderTransform = g;
            return p;
        }

        class State { public bool Shown = true; public int Turn; }
        static readonly ConditionalWeakTable<UIElement, State> states = new ConditionalWeakTable<UIElement, State>();

        public static bool IsShown(UIElement e)
        {
            State s;
            return e.Visibility == Visibility.Visible && (!states.TryGetValue(e, out s) || s.Shown);
        }

        // Appears (or comes back, if it was leaving): fades in from `from` scale and offset (in the look's distance).
        public static void Show(FrameworkElement e, double dx = 0, double dy = 0, double scale = 1, double ms = 0)
        {
            var st = states.GetOrCreateValue(e);
            bool appearing = e.Visibility != Visibility.Visible || !st.Shown;
            bool wasCollapsed = e.Visibility != Visibility.Visible;
            st.Shown = true;
            st.Turn++;
            e.Visibility = Visibility.Visible;
            e.IsHitTestVisible = true;
            var p = PartsOf(e);
            if (ms <= 0) ms = Normal;
            if (!Animates || !appearing)
            {
                if (!Animates) { Set(e, UIElement.OpacityProperty, 1); Rest(p); }
                else if (!appearing && e.Opacity < 1) To(e, UIElement.OpacityProperty, 1, Ms(Fast, false), Smooth);
                return;
            }
            if (wasCollapsed) Set(e, UIElement.OpacityProperty, 0);
            To(e, UIElement.OpacityProperty, 1, Ms(Math.Min(ms, Normal), false), Smooth);
            if (!Moves) { Rest(p); return; }
            if (wasCollapsed)
            {
                Set(p.Move, TranslateTransform.XProperty, dx * Distance);
                Set(p.Move, TranslateTransform.YProperty, dy * Distance);
                Set(p.Scale, ScaleTransform.ScaleXProperty, scale);
                Set(p.Scale, ScaleTransform.ScaleYProperty, scale);
            }
            To(p.Move, TranslateTransform.XProperty, 0, Ms(ms), In);
            To(p.Move, TranslateTransform.YProperty, 0, Ms(ms), In);
            To(p.Scale, ScaleTransform.ScaleXProperty, 1, Ms(ms), In);
            To(p.Scale, ScaleTransform.ScaleYProperty, 1, Ms(ms), In);
        }

        // Leaves: fades out (moving a little toward dx/dy), then is collapsed (or `done` runs) unless it was
        // shown again meanwhile. It stops taking clicks right away.
        public static void Hide(FrameworkElement e, double dx = 0, double dy = 0, double scale = 1, double ms = 0, Action done = null, bool collapse = true)
        {
            var st = states.GetOrCreateValue(e);
            if (e.Visibility != Visibility.Visible) { st.Shown = false; if (done != null) done(); return; }
            st.Shown = false;
            int turn = ++st.Turn;
            var p = PartsOf(e);
            Action finish = delegate
            {
                if (st.Turn != turn) return; // shown again before it was gone
                if (collapse) e.Visibility = Visibility.Collapsed;
                e.IsHitTestVisible = true;
                Set(e, UIElement.OpacityProperty, 1);
                Rest(p);
                if (done != null) done();
            };
            if (!Animates) { finish(); return; }
            e.IsHitTestVisible = false;
            if (ms <= 0) ms = Fast;
            To(e, UIElement.OpacityProperty, 0, Ms(ms, false), Out, finish);
            if (!Moves) return;
            To(p.Move, TranslateTransform.XProperty, dx * Distance, Ms(ms, false), Out);
            To(p.Move, TranslateTransform.YProperty, dy * Distance, Ms(ms, false), Out);
            To(p.Scale, ScaleTransform.ScaleXProperty, scale, Ms(ms, false), Out);
            To(p.Scale, ScaleTransform.ScaleYProperty, scale, Ms(ms, false), Out);
        }

        static void Rest(Parts p)
        {
            Set(p.Move, TranslateTransform.XProperty, 0);
            Set(p.Move, TranslateTransform.YProperty, 0);
            Set(p.Scale, ScaleTransform.ScaleXProperty, 1);
            Set(p.Scale, ScaleTransform.ScaleYProperty, 1);
        }

        // A content change in place (a new settings page, a message that appears): fades and rises into place.
        public static void Enter(FrameworkElement e, double dy, double ms = 0)
        {
            var p = PartsOf(e);
            if (ms <= 0) ms = Normal;
            if (!Animates) { Set(e, UIElement.OpacityProperty, 1); Rest(p); return; }
            FromTo(e, UIElement.OpacityProperty, 0, 1, Ms(Math.Min(ms, Normal), false), Smooth);
            if (Moves) FromTo(p.Move, TranslateTransform.YProperty, dy * Distance, 0, Ms(ms), In);
        }

        // A small "something happened" bump (an icon that changed): scales from `from` back to 1.
        public static void Pulse(UIElement e, double from = 0.8)
        {
            if (!Moves) return;
            if (e.RenderTransformOrigin == default(Point)) e.RenderTransformOrigin = new Point(0.5, 0.5);
            var p = PartsOf(e);
            FromTo(p.Scale, ScaleTransform.ScaleXProperty, from, 1, Ms(Fast), In);
            FromTo(p.Scale, ScaleTransform.ScaleYProperty, from, 1, Ms(Fast), In);
        }

        // ---------------------------------------------------------------- per-frame movement (zoom, pan, scrolling, glides)

        // Things that follow a target (zoom toward where you're scrolling, panning with momentum...) step once
        // per frame while they move. The frame hook is only attached while at least one is moving.
        static readonly Dictionary<object, Func<double, bool>> movers = new Dictionary<object, Func<double, bool>>();
        static bool hooked;
        static TimeSpan lastFrame;
        static double firstStepCap = 1; // seconds: the most a value's first step may count (one frame of the screen)

        // step(seconds since the last frame) returns false once it's at rest. A new mover with the same key replaces the old.
        public static void Run(object key, Func<double, bool> step)
        {
            movers[key] = step;
            if (hooked) return;
            hooked = true;
            lastFrame = TimeSpan.Zero;
            frameTimes.Clear();
            CompositionTarget.Rendering += OnFrame;
        }

        public static void Stop(object key) { movers.Remove(key); }

        public static bool Running(object key) { return movers.ContainsKey(key); }

        public static bool AnyRunning { get { return movers.Count > 0; } }

        // Tests (--hunt --case rates): one frame of the given length, as a screen of any refresh rate would give it.
        public static void SimulateFrame(double dt)
        {
            firstStepCap = dt; // (a simulated screen: its own frame length)
            foreach (var m in new List<KeyValuePair<object, Func<double, bool>>>(movers))
            {
                bool more;
                try { more = m.Value(dt); }
                catch { more = false; }
                if (!more && movers.ContainsKey(m.Key) && movers[m.Key] == m.Value) movers.Remove(m.Key);
            }
        }

        static void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame) return; // WPF can call twice per frame
            // Each step moves by the real time since the last frame, so an animation takes as long at 500 Hz as
            // at 60 Hz, just in more, smaller steps. The very first frame counts as one frame of this screen.
            double dt = lastFrame == TimeSpan.Zero ? 1.0 / ScreenRate : Math.Min(0.05, (now - lastFrame).TotalSeconds);
            if (lastFrame != TimeSpan.Zero && frameTimes.Count < 2000) frameTimes.Add((now - lastFrame).TotalSeconds);
            lastFrame = now;
            firstStepCap = 1.0 / ScreenRate;
            foreach (var m in new List<KeyValuePair<object, Func<double, bool>>>(movers))
            {
                bool more;
                try { more = m.Value(dt); }
                catch { more = false; }
                if (!more && movers.ContainsKey(m.Key) && movers[m.Key] == m.Value) movers.Remove(m.Key);
            }
            if (movers.Count == 0)
            {
                CompositionTarget.Rendering -= OnFrame;
                hooked = false;
                if (frameTimes.Count >= 10)
                {
                    frameTimes.Sort();
                    LastFps = 1 / frameTimes[frameTimes.Count / 2];
                }
            }
        }

        // How far a follower moves toward its target this frame (smooth approach that settles in about `ms`).
        public static double Follow(double dt, double ms)
        {
            if (ms <= 0) return 1;
            return 1 - Math.Exp(-dt / (ms / 1000.0 / 4)); // ~98% of the way after `ms`
        }
    }

    // A CSS-style cubic-bezier curve (Windows' motion curves are written this way).
    public class BezierEase : EasingFunctionBase
    {
        public double X1, Y1, X2, Y2;

        public BezierEase() { EasingMode = EasingMode.EaseIn; }
        public BezierEase(double x1, double y1, double x2, double y2) : this() { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }

        static double Curve(double a, double b, double t) { return 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t; }
        static double Slope(double a, double b, double t) { return 3 * a * (1 - t) * (1 - t) + 6 * (b - a) * (1 - t) * t + 3 * (1 - b) * t * t; }

        protected override double EaseInCore(double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double t = x;
            for (int i = 0; i < 8; i++) // Newton: find t where the curve's x is our time
            {
                double d = Curve(X1, X2, t) - x, s = Slope(X1, X2, t);
                if (Math.Abs(d) < 1e-5) break;
                if (Math.Abs(s) < 1e-6) break;
                t = Math.Max(0, Math.Min(1, t - d / s));
            }
            return Curve(Y1, Y2, t);
        }

        protected override Freezable CreateInstanceCore() { return new BezierEase(X1, Y1, X2, Y2); }
    }

    // XAML helper: fv:Anim.Appear="True" fades an element in when it's created (the keyboard focus ring).
    public static class Anim
    {
        public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached("Appear", typeof(bool), typeof(Anim),
            new PropertyMetadata(false, (d, e) =>
            {
                var el = d as FrameworkElement;
                if (el == null || !(bool)e.NewValue) return;
                el.Loaded += delegate
                {
                    if (!Motion.Animates) return;
                    Motion.FromTo(el, UIElement.OpacityProperty, 0, 1, Motion.Ms(100, false), Motion.Smooth);
                };
            }));

        public static void SetAppear(DependencyObject d, bool v) { d.SetValue(AppearProperty, v); }
        public static bool GetAppear(DependencyObject d) { return (bool)d.GetValue(AppearProperty); }
    }
}
