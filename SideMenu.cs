// FastViewer: a menu item that opens a smaller menu beside it (1.25.0), and stays put while you reach for it.
// On its own WPF closes the side menu as soon as the pointer crosses another row on the way there. Here, like
// in Windows' and Chrome's menus: while the pointer is moving toward the side menu (inside the triangle from
// the pointer to the side menu's two near corners) the other rows don't react; they take over only once the
// pointer has stopped heading there for a moment (MenuAim.Wait). Over the side menu, the gap between the two,
// or the item itself, it never closes. Works with the side menu on either side.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastViewer
{
    // The deciding part on its own (plain rectangles in screen pixels), so tests can walk a pointer through it.
    class MenuAim
    {
        public const int Wait = 350;  // ms without heading for the side menu before another row may take over
        const double Slop = 12;       // the triangle reaches this far past the side menu's corners
        public Rect Row, Menu, Side;  // the item, the menu it's in, its side menu
        public bool Hold;             // the side menu may not close now
        public bool Shield;           // the other rows don't react now
        Point from;
        bool hasFrom;
        long lastGood;

        // One look at the pointer. True: time's up, close the side menu and let the row under the pointer have it.
        public bool Step(Point c, long ms)
        {
            bool right = Side.Left + Side.Width / 2 > Menu.Left + Menu.Width / 2;
            double nearX = right ? Side.Left : Side.Right, menuX = right ? Menu.Right : Menu.Left;
            var gap = new Rect(new Point(Math.Min(nearX, menuX) - 2, Math.Min(Side.Top, Row.Top)), new Point(Math.Max(nearX, menuX) + 2, Math.Max(Side.Bottom, Row.Bottom)));
            if (Side.Contains(c) || gap.Contains(c) || Row.Contains(c))
            {
                Hold = true; Shield = false; from = c; hasFrom = true; lastGood = ms;
                return false;
            }
            if (!Menu.Contains(c)) // off the menus altogether: everything stays as it is, as in Windows
            {
                Hold = true; Shield = false; from = c; hasFrom = true; lastGood = ms;
                return false;
            }
            // Over another row.
            if (!hasFrom) { from = c; hasFrom = true; }
            if (Math.Abs(c.X - from.X) + Math.Abs(c.Y - from.Y) >= 3)
            {
                bool toward = right ? c.X > from.X : c.X < from.X;
                bool aiming = toward && InTriangle(c, from, new Point(nearX, Side.Top - Slop), new Point(nearX, Side.Bottom + Slop));
                from = c;
                if (aiming) { Hold = true; Shield = true; lastGood = ms; return false; }
            }
            if (!Shield) { Hold = false; return false; } // never headed for it: the other rows work as usual
            if (ms - lastGood < Wait) return false;      // stopped or turned away: give it a moment
            Hold = false; Shield = false;
            return true;
        }

        static double Side2(Point p, Point a, Point b) { return (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y); }

        static bool InTriangle(Point p, Point a, Point b, Point c)
        {
            double d1 = Side2(p, a, b), d2 = Side2(p, b, c), d3 = Side2(p, c, a);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
    }

    class SideMenuItem : MenuItem
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Pt { public int X, Y; }
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out Pt p);

        internal static Func<Point> TestCursor; // tests: where the pointer "is"
        internal MenuAim Aim;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        DispatcherTimer timer;
        ItemsControl owner;
        bool hooked, shielded;
        readonly System.Collections.Generic.List<UIElement> off = new System.Collections.Generic.List<UIElement>();
        long quietUntil, lastTick = -1000;

        static SideMenuItem()
        {
            IsSubmenuOpenProperty.OverrideMetadata(typeof(SideMenuItem), new FrameworkPropertyMetadata { CoerceValueCallback = Coerce });
        }

        public SideMenuItem()
        {
            SetResourceReference(StyleProperty, typeof(MenuItem));
            SubmenuOpened += delegate(object o, RoutedEventArgs e) { if (e.OriginalSource == this) Start(); };
            SubmenuClosed += delegate(object o, RoutedEventArgs e) { if (e.OriginalSource == this) Stop(); };
        }

        // WPF wants to close the side menu (its own timer, the pointer on another row): not while it's being reached for.
        static object Coerce(DependencyObject d, object value)
        {
            var m = (SideMenuItem)d;
            if ((bool)value) return m.IsLoaded;
            return m.Holding();
        }

        internal bool Holding()
        {
            long now = clock.ElapsedMilliseconds;
            if (Aim == null || !Aim.Hold || timer == null || now < quietUntil || now - lastTick > 200) return false;
            var menu = owner as ContextMenu;
            return menu == null || menu.IsOpen;
        }

        void Start()
        {
            owner = ItemsControl.ItemsControlFromItemContainer(this);
            if (owner == null) return;
            if (!hooked)
            {
                hooked = true;
                // A click or a key decides for itself what closes; so does the whole menu closing.
                owner.PreviewMouseDown += delegate { Quiet(); };
                owner.PreviewMouseUp += delegate { Quiet(); };
                owner.PreviewKeyDown += delegate { Quiet(); };
                var menu = owner as ContextMenu;
                if (menu != null) menu.Closed += delegate { Stop(); if (IsSubmenuOpen) SetCurrentValue(IsSubmenuOpenProperty, false); };
            }
            Aim = new MenuAim();
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
                timer.Tick += delegate { Tick(); };
            }
            timer.Start();
            Tick();
        }

        void Stop()
        {
            if (timer != null) timer.Stop();
            timer = null;
            Aim = null;
            SetShield(false);
        }

        void Quiet()
        {
            quietUntil = clock.ElapsedMilliseconds + 400;
            SetShield(false);
            if (Aim != null) Aim.Shield = false;
        }

        internal void Tick()
        {
            if (Aim == null) return;
            if (!IsSubmenuOpen) { Stop(); return; }
            long now = clock.ElapsedMilliseconds;
            Point c;
            if (TestCursor != null) c = TestCursor();
            else { Pt p; if (!GetCursorPos(out p)) return; c = new Point(p.X, p.Y); }
            var popup = Template == null ? null : Template.FindName("PART_Popup", this) as Popup;
            var side = popup == null ? null : popup.Child as FrameworkElement;
            if (side == null || !side.IsVisible || !RectOf(this, out Aim.Row) || !RectOf(owner, out Aim.Menu) || !RectOf(side, out Aim.Side)) return;
            lastTick = now;
            if (now < quietUntil) return;
            bool close = Aim.Step(c, now);
            SetShield(Aim.Shield);
            if (!close) return;
            SetCurrentValue(IsSubmenuOpenProperty, false);
            Mouse.Synchronize(); // the row under the pointer lights up now
        }

        // The other rows stop (or start again) noticing the pointer.
        void SetShield(bool on)
        {
            if (shielded == on || owner == null) return;
            shielded = on;
            if (!on)
            {
                foreach (var el in off) el.IsHitTestVisible = true;
                off.Clear();
                return;
            }
            foreach (var item in owner.Items)
            {
                var el = item as UIElement;
                if (el == null || el == this || !el.IsHitTestVisible) continue;
                el.IsHitTestVisible = false;
                off.Add(el);
            }
        }

        static bool RectOf(FrameworkElement el, out Rect r)
        {
            r = Rect.Empty;
            try
            {
                if (el == null || PresentationSource.FromVisual(el) == null) return false;
                r = new Rect(el.PointToScreen(new Point(0, 0)), el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight)));
                return r.Width > 0 && r.Height > 0;
            }
            catch { return false; }
        }
    }
}
