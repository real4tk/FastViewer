// FastViewer: Settings > Appearance (the look, dark or light) and Settings > Buttons (which buttons show), and
// how a look reaches the window: frame colour, see-through glass, rounded corners, floating bars, motion.
// Hidden buttons keep working through their keyboard shortcut and the "..." menus ("Hidden buttons").

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FastViewer
{
    partial class ViewerWindow
    {
        string lookId = Looks.DefaultId; // Settings > Appearance
        string lookMode = "Auto";        // Auto (like Windows) / Dark / Light
        string lookOverride;             // --look <id>: test runs in a look without touching settings
        bool barsFloat, seeThrough, springMotion, glassSlots, rounded;
        double fadeInMs = 120, fadeOutMs = 300;
        // Liquid Glass: the hidden players can't hide behind an opaque cover (that would hide the blurred desktop
        // too), so they stay drawn, ready to switch to instantly, at an opacity too low to see.
        const double HiddenSlotOpacity = 0.01;

        static double Number(Look look, string key, double fallback)
        {
            var v = look.Get(key);
            return v is double ? (double)v : fallback;
        }

        void ApplyLook()
        {
            var look = Looks.Find(lookId);
            Looks.Apply(look, lookMode);
            barsFloat = look.Flag("Look.BarsFloat");
            seeThrough = look.Flag("Look.SeeThrough");
            springMotion = look.Flag("Look.Spring");
            fadeInMs = Number(look, "Motion.FadeIn", 120);
            fadeOutMs = Number(look, "Motion.FadeOut", 300);
            if (root == null) return; // starting up: the window does the rest once it exists
            ApplyWindowLook();
            PlaceBars();
            UpdateControls();
            UpdateSettingsLens(false);
            RefreshLookCards();
        }

        // The parts of a look that live in the window itself rather than in its contents.
        void ApplyWindowLook()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            bool glass = seeThrough && !fullscreen && !mini;
            try
            {
                int dark = Looks.Light ? 0 : 1; // dark or light window frame (Windows 10 20H1+ uses 20, older builds 19)
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, 4);
            }
            catch { }
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null && src.CompositionTarget != null)
                src.CompositionTarget.BackgroundColor = glass ? Colors.Transparent : Colors.Black;
            chrome.GlassFrameThickness = glass ? new Thickness(-1) : new Thickness(1);
            if (glass && snapDir != null) Background = TestDesktop(); // tests: a stand-in for the desktop (never the real one)
            else if (glass) Background = Brushes.Transparent;
            else SetResourceReference(BackgroundProperty, "WindowBg");
            if (snapDir == null || !glass) Looks.SetBlurBehind(hwnd, glass);
            RoundCorners();
            UpdateSlotOpacity();
        }

        void RoundCorners()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var src = PresentationSource.FromVisual(this);
            if (hwnd == IntPtr.Zero || src == null || src.CompositionTarget == null) return;
            bool round = seeThrough && !fullscreen && !mini && WindowState == WindowState.Normal;
            if (!round && !rounded) return;
            var m = src.CompositionTarget.TransformToDevice;
            var r = Application.Current.Resources["Radius.Window"];
            int radius = round && r is double ? (int)Math.Round((double)r * m.M11) : 0;
            Looks.SetRoundCorners(hwnd, (int)Math.Round(ActualWidth * m.M11), (int)Math.Round(ActualHeight * m.M22), radius);
            rounded = round;
        }

        void UpdateSlotOpacity()
        {
            if (slots == null) return;
            glassSlots = seeThrough && !fullscreen && !mini;
            cover.Visibility = glassSlots ? Visibility.Collapsed : Visibility.Visible;
            foreach (var s in slots) s.Host.Opacity = !glassSlots || s == shown ? 1 : HiddenSlotOpacity;
        }

        static Brush testDesktop;

        static Brush TestDesktop()
        {
            if (testDesktop != null) return testDesktop;
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0x5B, 0xD8), 0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x8E, 0x3F, 0xC7), 0.45));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x7A, 0x41), 1));
            b.Freeze();
            return testDesktop = b;
        }

        // Liquid Glass motion: slides up a little and settles with a spring.
        static void Rise(FrameworkElement e, double from)
        {
            var move = e.RenderTransform as TranslateTransform;
            if (move == null) e.RenderTransform = move = new TranslateTransform();
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromSeconds(0.55)) { EasingFunction = new SpringEase() });
        }

        // ---------------------------------------------------------------- buttons

        class ButtonInfo
        {
            public string Id, Name, Glyph, Bar;
            public ButtonInfo(string id, string name, string glyph, string bar) { Id = id; Name = name; Glyph = glyph; Bar = bar; }
        }

        static readonly ButtonInfo[] AllButtons =
        {
            new ButtonInfo("OpenBtn", "Open", "", "Top bar"),
            new ButtonInfo("DeleteBtn", "Delete", "", "Top bar"),
            new ButtonInfo("CopyBtn", "Copy", "", "Top bar"),
            new ButtonInfo("RotateBtn", "Rotate", "", "Top bar"),
            new ButtonInfo("MiniBtn", "Mini player", "", "Top bar"),
            new ButtonInfo("OpenWithBtn", "Open with", "", "Top bar"),
            new ButtonInfo("SlideshowBtn", "Slideshow", "", "Bottom bar"),
            new ButtonInfo("GridBtn", "All files in folder (grid)", "", "Bottom bar"),
            new ButtonInfo("FileFacts", "File details (type, size, dimensions)", "", "Bottom bar"),
            new ButtonInfo("InfoBtn", "File info", "", "Bottom bar"),
            new ButtonInfo("FitBtn", "Fit to window", "", "Bottom bar"),
            new ButtonInfo("ZoomBtn", "Zoom level", "", "Bottom bar"),
            new ButtonInfo("ZoomGroup", "Zoom slider", "", "Bottom bar"),
            new ButtonInfo("FullBtn", "Full screen", "", "Bottom bar"),
            new ButtonInfo("PlayBtn", "Play / pause", "", "Video controls"),
            new ButtonInfo("BackBtn", "Skip back", "", "Video controls"),
            new ButtonInfo("FwdBtn", "Skip forward", "", "Video controls"),
            new ButtonInfo("VolBtn", "Volume", "", "Video controls"),
            new ButtonInfo("LoopBtn", "Repeat", "", "Video controls"),
            new ButtonInfo("Arrows", "Arrows on the sides", "", "On the picture"),
        };
        static readonly string[] BottomControls = { "SlideshowBtn", "GridBtn", "FileFacts", "InfoBtn", "FitBtn", "ZoomBtn", "ZoomGroup", "FullBtn" };
        static readonly string[] PresetNames = { "All buttons", "Essentials", "Minimal" };
        static readonly string[][] PresetHidden =
        {
            new string[0],
            new[] { "CopyBtn", "MiniBtn", "SlideshowBtn", "FileFacts", "FitBtn", "ZoomGroup", "BackBtn", "FwdBtn", "LoopBtn" },
            AllButtons.Select(b => b.Id).Where(id => id != "PlayBtn").ToArray(),
        };

        readonly HashSet<string> hiddenButtons = new HashSet<string>();

        bool Shown(string id) { return !hiddenButtons.Contains(id); }
        bool BottomControlsShown() { return BottomControls.Any(Shown); }

        int CurrentPreset()
        {
            for (int i = 0; i < PresetHidden.Length; i++)
                if (hiddenButtons.SetEquals(PresetHidden[i]) && centerButton == (i == 2 ? 1 : 0)) return i;
            return -1;
        }

        void ApplyButtons()
        {
            if (root == null) return;
            UpdateResponsive(); // the bars (they also drop buttons when the window is narrow)
            UpdateControls();   // the video controls and the arrows
            PlaceBars();
            RefreshLookCards(); // the "fewer buttons?" suggestion for Clean
        }

        void ApplyPreset(int preset)
        {
            hiddenButtons.Clear();
            foreach (var id in PresetHidden[preset]) hiddenButtons.Add(id);
            centerButton = preset == 2 ? 1 : 0; // Minimal: the big play button only shows on a paused video
            ApplyButtons();
            SaveSettings();
            foreach (var refresh in settingsRefresh) refresh();
        }

        // Menu items for buttons that are switched off, so they're never out of reach.
        List<Control> HiddenButtonItems(bool video)
        {
            var items = new List<Control>();
            if (!video)
            {
                if (!Shown("OpenBtn")) items.Add(Item("Open...", "", OpenDialog, KeyOf("open")));
                if (!Shown("OpenWithBtn")) items.Add(Item("Open with...", "", OpenWith, KeyOf("openWith")));
                if (!Shown("FitBtn") || !Shown("ZoomBtn") || !Shown("ZoomGroup"))
                {
                    items.Add(Item("Fit to window", "", delegate { SetZoom(1, StageCenter()); }, KeyOf("fit")));
                    items.Add(Item("Zoom in", "", delegate { ZoomBy(1.25, StageCenter()); }, KeyOf("zoomIn")));
                    items.Add(Item("Zoom out", "", delegate { ZoomBy(0.8, StageCenter()); }, KeyOf("zoomOut")));
                }
                if (!Shown("FullBtn")) items.Add(Item(fullscreen ? "Leave full screen" : "Full screen", "", ToggleFullscreen, KeyOf("fullscreen")));
            }
            else
            {
                if (!Shown("PlayBtn")) items.Add(Item(playing ? "Pause" : "Play", playing ? "" : "", TogglePlay, KeyOf("play")));
                if (!Shown("BackBtn")) items.Add(Item("Back " + skipBack + " seconds", "", delegate { SeekBy(-skipBack); }, KeyOf("skipBack")));
                if (!Shown("FwdBtn")) items.Add(Item("Forward " + skipForward + " seconds", "", delegate { SeekBy(skipForward); }, KeyOf("skipForward")));
                if (!Shown("VolBtn"))
                {
                    items.Add(Item(muted ? "Unmute" : "Mute", "", ToggleMute, KeyOf("mute")));
                    items.Add(Item("Volume up", "", delegate { SetVolume(volume + 0.1); }, KeyOf("volumeUp")));
                    items.Add(Item("Volume down", "", delegate { SetVolume(volume - 0.1); }, KeyOf("volumeDown")));
                }
            }
            if (items.Count > 0)
            {
                items.Insert(0, Heading("Hidden buttons"));
                items.Insert(0, new Separator());
            }
            return items;
        }

        // ---------------------------------------------------------------- Settings > Appearance and Buttons

        StackPanel appearancePage;
        WrapPanel lookCards;
        Border cleanHint;
        bool previewsBuilt, previewsLight;

        void AddAppearancePages()
        {
            AddGroup("Appearance");
            appearancePage = settingsPage;
            lookCards = new WrapPanel { Margin = new Thickness(0, 14, -12, -12) };
            AddWideCard("", "Look", "Each look changes colours, fonts, shapes, icons and motion. It switches right away.", lookCards);

            var use = new Button { Content = "Use Minimal buttons", MinWidth = 120, Height = 32 };
            use.SetResourceReference(StyleProperty, "AccentButton");
            use.Click += delegate { ApplyPreset(2); };
            cleanHint = AddCard("", "Clean looks best with fewer buttons",
                "Use the Minimal set of buttons (Settings > Buttons)? Everything stays in the \"...\" menus and on its keys.", use);

            AddCard("", "Dark or light",
                "\"Like Windows\" follows Windows' own setting (Personalization > Colors).",
                Dropdown(new[] { "Like Windows", "Dark", "Light" },
                    () => lookMode == "Dark" ? 1 : lookMode == "Light" ? 2 : 0,
                    i => { lookMode = new[] { "Auto", "Dark", "Light" }[i]; ApplyLook(); }));
            var reset = new Button { Content = "Reset", MinWidth = 120, Height = 32 };
            reset.SetResourceReference(StyleProperty, "Filled");
            reset.Click += delegate
            {
                lookId = Looks.DefaultId;
                lookMode = "Auto";
                ApplyLook();
                SaveSettings();
                foreach (var refresh in settingsRefresh) refresh();
            };
            AddCard("", "Reset to default", "Back to the Photos look, dark or light like Windows.", reset);

            AddGroup("Buttons");
            var presets = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < PresetNames.Length; i++)
            {
                int p = i;
                var b = new Button { Content = PresetNames[i], MinWidth = 104, Height = 32, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
                b.Click += delegate { ApplyPreset(p); };
                presets.Children.Add(b);
                settingsRefresh.Add(delegate { b.SetResourceReference(StyleProperty, CurrentPreset() == p ? "AccentButton" : "Filled"); });
                b.SetResourceReference(StyleProperty, "Filled");
            }
            AddCard("", "Choose your buttons",
                "Hidden buttons keep working: with their keyboard shortcut, and from the \"...\" menus under \"Hidden buttons\". " +
                "Essentials keeps the everyday ones; Minimal shows only the picture, \"...\" and play.", presets);
            string bar = null;
            foreach (var info in AllButtons)
            {
                if (info.Bar != bar)
                {
                    bar = info.Bar;
                    AddSubheading(bar);
                }
                var id = info.Id;
                AddCard(info.Glyph, info.Name, null, Toggle(() => Shown(id), v =>
                {
                    if (v) hiddenButtons.Remove(id);
                    else hiddenButtons.Add(id);
                    ApplyButtons();
                }));
                if (id == "Arrows")
                    AddCard("", "Play button in the middle",
                        "The big play/pause button over a video. \"Only when paused\" shows a play button on a paused video, like YouTube. " +
                        "Clicking the video pauses and plays either way.",
                        Dropdown(new[] { "Show", "Only when paused", "Hide" }, () => centerButton, i => { centerButton = i; UpdateControls(); }));
            }
            var resetButtons = new Button { Content = "Reset", MinWidth = 120, Height = 32 };
            resetButtons.SetResourceReference(StyleProperty, "Filled");
            resetButtons.Click += delegate { ApplyPreset(0); };
            AddCard("", "Reset to default", "Show all buttons again.", resetButtons);
            foreach (var refresh in settingsRefresh) refresh();
        }

        void AddSubheading(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 18, 0, 8) };
            settingsPage.Children.Add(t);
        }

        // The look cards: a small drawing of the viewer in each look (built the first time the page is shown).
        void BuildLookPreviews()
        {
            if (previewsBuilt) return;
            previewsBuilt = true;
            foreach (var look in Looks.All)
            {
                var l = look;
                var frame = new Border { BorderThickness = new Thickness(2), Padding = new Thickness(2), CornerRadius = new CornerRadius(10) };
                frame.Child = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = Mock(look, Looks.IsLight(look, lookMode)) };
                previewsLight = Looks.Light;
                var name = new TextBlock { Text = look.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 0, 0) };
                var text = new TextBlock { Text = look.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 0) };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var stack = new StackPanel { Width = 200 };
                stack.Children.Add(frame);
                stack.Children.Add(name);
                stack.Children.Add(text);
                var card = new Button { Content = stack, Padding = new Thickness(6), Height = double.NaN, Margin = new Thickness(0, 0, 12, 12), Tag = look.Id, ToolTip = look.Name, VerticalAlignment = VerticalAlignment.Top };
                card.SetResourceReference(StyleProperty, "Subtle");
                card.Click += delegate
                {
                    lookId = l.Id;
                    ApplyLook();
                    SaveSettings();
                };
                lookCards.Children.Add(card);
            }
            RefreshLookCards();
        }

        void RefreshLookCards()
        {
            bool redraw = previewsBuilt && previewsLight != Looks.Light; // dark or light changed: the drawings follow
            previewsLight = Looks.Light;
            if (lookCards != null)
                foreach (var card in lookCards.Children.OfType<Button>())
                {
                    var frame = (Border)((StackPanel)card.Content).Children[0];
                    if (redraw)
                    {
                        var look = Looks.Find((string)card.Tag);
                        ((Border)frame.Child).Child = Mock(look, Looks.IsLight(look, lookMode));
                    }
                    if ((string)card.Tag == lookId) frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
                    else frame.BorderBrush = Brushes.Transparent;
                }
            if (cleanHint != null)
                cleanHint.Visibility = lookId == "Clean" && CurrentPreset() != 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        // A miniature viewer drawn with a look's own colours (its values go into this drawing's resources).
        FrameworkElement Mock(Look look, bool light)
        {
            var g = new Grid { Width = 192, Height = 120 };
            var res = new ResourceDictionary();
            Looks.Fill(res, look, light, false);
            g.Resources = res;
            bool floating = look.Flag("Look.BarsFloat"), glass = look.Flag("Look.SeeThrough");
            if (glass) g.Children.Add(new Rectangle { Fill = TestDesktop() });
            g.Children.Add(MockRect("WindowBg"));
            double top = floating ? 0 : 14, bottom = floating ? 0 : 12;
            var stage = MockRect("StageBg");
            stage.Margin = new Thickness(0, top, 0, bottom);
            g.Children.Add(stage);
            // the "photo"
            var sky = new LinearGradientBrush(Color.FromRgb(0x5D, 0x9C, 0xEC), Color.FromRgb(0xF6, 0xC1, 0x8A), 90);
            var photo = new Grid { Width = 112, Height = floating ? 84 : 70, Margin = new Thickness(0, top, 0, bottom) };
            photo.Children.Add(new Rectangle { Fill = sky });
            photo.Children.Add(new Path
            {
                Data = Geometry.Parse("M0,60 L30,38 L52,52 L78,30 L112,58 L112,84 L0,84 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x4F)), VerticalAlignment = VerticalAlignment.Bottom
            });
            g.Children.Add(photo);
            // bars (floating looks: soft shades over the picture)
            var topBar = MockRect("TopBarBg");
            topBar.Height = floating ? 22 : 14;
            topBar.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(topBar);
            var bottomBar = MockRect("BottomBarBg");
            bottomBar.Height = floating ? 22 : 12;
            bottomBar.VerticalAlignment = VerticalAlignment.Bottom;
            g.Children.Add(bottomBar);
            // a few controls: "Open" + icons left, window buttons right, the video bar at the bottom
            var open = new Border { Width = 22, Height = 7, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 4, 0, 0) };
            open.SetResourceReference(Border.BackgroundProperty, "ControlFill");
            open.SetResourceReference(Border.BorderBrushProperty, "ControlEdge");
            open.BorderThickness = new Thickness(0.5);
            if (!floating) g.Children.Add(open);
            for (int i = 0; i < 3; i++)
            {
                var dot = MockRect("ControlText");
                dot.Width = dot.Height = 3;
                dot.HorizontalAlignment = HorizontalAlignment.Left;
                dot.VerticalAlignment = VerticalAlignment.Top;
                dot.Margin = new Thickness((floating ? 8 : 34) + i * 8, 6, 0, 0);
                g.Children.Add(dot);
                var win = MockRect("TextMain");
                win.Width = 4;
                win.Height = 1;
                win.HorizontalAlignment = HorizontalAlignment.Right;
                win.VerticalAlignment = VerticalAlignment.Top;
                win.Margin = new Thickness(0, 7, 6 + i * 9, 0);
                g.Children.Add(win);
            }
            var transport = new Border
            {
                Width = 96, Height = 10, VerticalAlignment = VerticalAlignment.Bottom, BorderThickness = new Thickness(0.5),
                Margin = new Thickness(0, 0, 0, bottom + (floating ? 8 : 5))
            };
            transport.SetResourceReference(Border.BackgroundProperty, "FloatBg");
            transport.SetResourceReference(Border.BorderBrushProperty, "FloatEdge");
            transport.SetResourceReference(Border.CornerRadiusProperty, "Radius.Float");
            var progress = MockRect("Accent");
            progress.Width = 40;
            progress.Height = 2;
            progress.HorizontalAlignment = HorizontalAlignment.Left;
            progress.Margin = new Thickness(12, 0, 0, 0);
            transport.Child = progress;
            g.Children.Add(transport);
            return g;
        }

        static Rectangle MockRect(string fill)
        {
            var r = new Rectangle();
            r.SetResourceReference(Shape.FillProperty, fill);
            return r;
        }

        // Liquid Glass: a glass lens slides behind the chosen page in the Settings side menu.
        void UpdateSettingsLens(bool animate)
        {
            var lens = F<Border>("SettingsLens");
            var current = settingsPages.FirstOrDefault(p => settingsList != null && settingsList.Children.Contains(p.Value)).Key;
            if (!springMotion || current == null || current.ActualHeight == 0)
            {
                lens.Opacity = 0;
                return;
            }
            var parent = (UIElement)lens.Parent;
            var p0 = current.TranslatePoint(new Point(0, 0), parent);
            lens.Width = current.ActualWidth;
            lens.Height = current.ActualHeight;
            var move = lens.RenderTransform as TranslateTransform;
            if (move == null) lens.RenderTransform = move = new TranslateTransform();
            lens.Opacity = 1;
            if (!animate)
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.Y = p0.Y;
                return;
            }
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(p0.Y, TimeSpan.FromSeconds(0.55)) { EasingFunction = new SpringEase() });
        }
    }
}
