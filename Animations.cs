// FastViewer: the window's side of the animation system (Motion.cs): the Animations setting, sliding
// switches, menus that open from their button, pressed buttons, the whole-window crossfade (looks,
// dark/light, bars docking) and the fade-through for full screen and the mini player.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        string animations = "Auto";   // Settings > Appearance > Animations: Auto (like Windows) / Full / Reduced / Off
        string motionOverride;        // --motion full|reduced|off (test runs are Off unless they say otherwise)
        bool crossfadeFiles;          // Settings > Appearance > Transition between files (off: instant)
        static bool motionHandlersInstalled;

        static readonly string[] AnimationChoices = { "Auto", "Full", "Reduced", "Off" };

        // Which level applies now: the setting, or Windows' "Show animations" switch for "Like Windows".
        void ApplyMotionLevel()
        {
            string mode = motionOverride ?? (testRun ? "Off" : animations);
            Motion.Level = mode == "Full" ? MotionLevel.Full : mode == "Reduced" ? MotionLevel.Reduced : mode == "Off" ? MotionLevel.Off
                : (SystemParameters.ClientAreaAnimation ? MotionLevel.Full : MotionLevel.Reduced);
            var res = Application.Current.Resources;
            res["Tpl.Button"] = res[Motion.Animates ? "ButtonTemplateAnimated" : "ButtonTemplate"];
            res["Tpl.Caption"] = res[Motion.Animates ? "CaptionTemplateAnimated" : "CaptionTemplate"];
            InstallMotionHandlers();
        }

        // Once per program: behaviour every switch, menu and button shares.
        static void InstallMotionHandlers()
        {
            if (motionHandlersInstalled) return;
            motionHandlersInstalled = true;
            EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler(OnSwitchFlipped));
            EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSwitchFlipped));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnMenuOpened));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnMenuClosed));
            EventManager.RegisterClassHandler(typeof(ContextMenu), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMenuClick));
            EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnButtonPressed));
            EventManager.RegisterClassHandler(typeof(Button), UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnButtonReleased));
        }

        // ---------------------------------------------------------------- switches

        // The knob slides to its new side.
        static void OnSwitchFlipped(object sender, RoutedEventArgs e)
        {
            var cb = (CheckBox)sender;
            if (!Motion.Animates || cb.Template == null || !cb.IsLoaded) return;
            var move = cb.Template.FindName("KnobMove", cb) as TranslateTransform;
            if (move == null) return;
            bool on = cb.IsChecked == true;
            // The template has already put the knob on its new side; it starts from the old side and slides over.
            Motion.FromTo(move, TranslateTransform.XProperty, on ? -20 : 20, 0, Motion.Ms(Motion.Fast), Motion.Moves ? Motion.In : Motion.Smooth);
        }

        // ---------------------------------------------------------------- menus

        // Opens with a fade and a small scale from the button's side (6 px slide); closes with a quick fade.
        static void OnMenuOpened(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            var rootBorder = menu.Template == null ? null : menu.Template.FindName("MenuRoot", menu) as FrameworkElement;
            if (rootBorder == null) return;
            bool up = menu.Placement == PlacementMode.Top;
            rootBorder.RenderTransformOrigin = new Point(0.5, up ? 1 : 0);
            if (Motion.Animates)
            {
                var p = Motion.PartsOf(rootBorder);
                if (Motion.Moves)
                {
                    Motion.FromTo(p.Scale, ScaleTransform.ScaleXProperty, Motion.Press < 1 ? 0.94 : 0.96, 1, Motion.Ms(Motion.Fast), Motion.In);
                    Motion.FromTo(p.Scale, ScaleTransform.ScaleYProperty, Motion.Press < 1 ? 0.94 : 0.96, 1, Motion.Ms(Motion.Fast), Motion.In);
                    Motion.FromTo(p.Move, TranslateTransform.YProperty, (up ? 6 : -6) * Math.Max(0.5, Motion.Distance), 0, Motion.Ms(Motion.Fast), Motion.In);
                }
                Motion.FromTo(rootBorder, UIElement.OpacityProperty, 0, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth);
            }
            // A picture of the menu for its closing fade (a menu disappears the moment it closes).
            menu.Dispatcher.BeginInvoke(new Action(delegate { menuGhost = Capture(rootBorder, out menuGhostAt); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        static BitmapSource menuGhost;
        static Point menuGhostAt;

        // Clicking an item: take the picture now, with that item highlighted.
        static void OnMenuClick(object sender, MouseButtonEventArgs e)
        {
            var menu = (ContextMenu)sender;
            var rootBorder = menu.Template == null ? null : menu.Template.FindName("MenuRoot", menu) as FrameworkElement;
            if (rootBorder != null) menuGhost = Capture(rootBorder, out menuGhostAt);
        }

        static void OnMenuClosed(object sender, RoutedEventArgs e)
        {
            var ghost = menuGhost;
            menuGhost = null;
            if (ghost == null || !Motion.Animates) return;
            double dpiX = ghost.DpiX / 96, dpiY = ghost.DpiY / 96;
            var img = new Image { Source = ghost, Width = ghost.PixelWidth / dpiX, Height = ghost.PixelHeight / dpiY, IsHitTestVisible = false };
            var pop = new Popup
            {
                Child = img, AllowsTransparency = true, Placement = PlacementMode.AbsolutePoint,
                HorizontalOffset = menuGhostAt.X / dpiX, VerticalOffset = menuGhostAt.Y / dpiY, IsHitTestVisible = false
            };
            pop.IsOpen = true;
            Motion.To(img, UIElement.OpacityProperty, 0, Motion.Ms(83, false), Motion.Out, delegate { pop.IsOpen = false; });
        }

        static BitmapSource Capture(FrameworkElement el, out Point screenPx)
        {
            screenPx = default(Point);
            try
            {
                if (el.ActualWidth < 1 || !el.IsVisible) return null;
                var src = PresentationSource.FromVisual(el);
                double k = src == null || src.CompositionTarget == null ? 1 : src.CompositionTarget.TransformToDevice.M11;
                screenPx = el.PointToScreen(new Point(0, 0));
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * k), (int)Math.Ceiling(el.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
                rtb.Render(el);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- pressed buttons (Liquid Glass presses in and springs back)

        static void OnButtonPressed(object sender, MouseButtonEventArgs e)
        {
            if (Motion.Press >= 1 || !Motion.Moves) return;
            var b = (Button)sender;
            if (!PressShrinks(b)) return;
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            var p = Motion.PartsOf(b);
            Motion.To(p.Scale, ScaleTransform.ScaleXProperty, Motion.Press, 90, Motion.Smooth);
            Motion.To(p.Scale, ScaleTransform.ScaleYProperty, Motion.Press, 90, Motion.Smooth);
        }

        static void OnButtonReleased(object sender, MouseEventArgs e)
        {
            var b = (Button)sender;
            if (!PressShrinks(b)) return;
            var p = Motion.PartsOf(b);
            if (p.Scale.ScaleX == 1 && !Motion.Busy(p.Scale)) return;
            Motion.To(p.Scale, ScaleTransform.ScaleXProperty, 1, Motion.Ms(Motion.Normal), Motion.In);
            Motion.To(p.Scale, ScaleTransform.ScaleYProperty, 1, Motion.Ms(Motion.Normal), Motion.In);
        }

        // Buttons with a transform of their own (the middle play button, sliders' parts) keep it.
        static bool PressShrinks(Button b)
        {
            return b.RenderTransform == null || b.RenderTransform == Transform.Identity || b.RenderTransform is TransformGroup;
        }

        // ---------------------------------------------------------------- smooth scrolling

        // Wheel notches glide (about 220 ms) instead of jumping, and several notches add up to one movement.
        static void SmoothWheel(ScrollViewer sv)
        {
            double target = 0;
            sv.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e)
            {
                if (e.Handled || !Motion.Moves || (Keyboard.Modifiers & ModifierKeys.Control) != 0 || sv.ScrollableHeight <= 0) return;
                e.Handled = true;
                double from = Motion.Running(sv) ? target : sv.VerticalOffset;
                target = Math.Max(0, Math.Min(sv.ScrollableHeight, from - e.Delta * 0.6));
                Motion.Run(sv, dt =>
                {
                    double now = sv.VerticalOffset, next = now + (target - now) * Motion.Follow(dt, Motion.Ms(220));
                    bool done = Math.Abs(target - next) < 0.5;
                    sv.ScrollToVerticalOffset(done ? target : next);
                    return !done;
                });
            };
        }
        // ---------------------------------------------------------------- whole-window crossfade

        Image crossfadeImage;

        // Covers the window with a picture of how it looks now, then fades that picture away over the change
        // that follows (a look, dark/light, the bars docking). Taken from what's on screen (the same way
        // tests take pictures), never by redrawing a video, so it can't wait on the video decoder.
        void CrossfadeWindow(double ms)
        {
            if (!Motion.Animates || !IsVisible || WindowState == WindowState.Minimized || root == null || (snapDir != null && !huntMotion)) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            double k = Dpi();
            int w = (int)Math.Round(ActualWidth * k), h = (int)Math.Round(ActualHeight * k);
            if (w < 8 || h < 8) return;
            BitmapSource shot;
            try
            {
                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(hwnd, hdc, 2);
                        g.ReleaseHdc(hdc);
                    }
                    IntPtr hb = bmp.GetHbitmap();
                    try { shot = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
                    finally { DeleteObject(hb); }
                    shot.Freeze();
                }
            }
            catch { return; }
            if (crossfadeImage != null) root.Children.Remove(crossfadeImage);
            var img = new Image { Source = shot, Stretch = Stretch.Fill, IsHitTestVisible = false, Margin = new Thickness(-root.Margin.Left, -root.Margin.Top, -root.Margin.Right, -root.Margin.Bottom) };
            Grid.SetRowSpan(img, 3);
            Panel.SetZIndex(img, 2000);
            root.Children.Add(img);
            crossfadeImage = img;
            Motion.To(img, UIElement.OpacityProperty, 0, Motion.Ms(ms, false), Motion.Smooth, delegate
            {
                root.Children.Remove(img);
                if (crossfadeImage == img) crossfadeImage = null;
            });
        }

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);

        // ---------------------------------------------------------------- fade-through (full screen, mini player)

        Border veil;

        // The picture fades to the background, the change happens hidden, then it fades back: no visible jump
        // while the window changes size. Off: the change happens right away.
        void FadeThrough(Action change)
        {
            if (!Motion.Animates || !IsVisible || (snapDir != null && !huntMotion) || root == null)
            {
                change();
                return;
            }
            if (veil == null)
            {
                veil = new Border { IsHitTestVisible = false, Opacity = 0 };
                veil.SetResourceReference(Border.BackgroundProperty, "FullBg");
                Grid.SetRowSpan(veil, 3);
                Panel.SetZIndex(veil, 2001);
                root.Children.Add(veil);
            }
            veil.Visibility = Visibility.Visible;
            Motion.To(veil, UIElement.OpacityProperty, 1, Motion.Ms(90, false), Motion.Out, delegate
            {
                change();
                // after the new size is laid out and drawn
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Motion.To(veil, UIElement.OpacityProperty, 0, Motion.Ms(150, false), Motion.Smooth, delegate { veil.Visibility = Visibility.Collapsed; });
                }), System.Windows.Threading.DispatcherPriority.Render);
            });
        }
    }
}
