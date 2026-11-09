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
        bool miniClear; // the mini player is on a photo with see-through parts: the window is clear around it
        double fadeInMs = 120, fadeOutMs = 300;
        // Liquid Glass: the hidden players can't hide behind an opaque cover (that would hide the blurred desktop
        // too), so they stay drawn, ready to switch to instantly, at an opacity too low to see.
        const double HiddenSlotOpacity = 0;
        // A see-through mini player hides them completely: even 1% shows as a faint box over a light background.
        double HiddenOpacity { get { return miniClear ? 0 : HiddenSlotOpacity; } }

        static double Number(Look look, string key, double fallback)
        {
            var v = look.Get(key);
            return v is double ? (double)v : fallback;
        }

        void ApplyLook()
        {
            if (lookId == CustomId) BuildCustomLook(); // (otherwise only when Settings shows it: starting stays quick)
            var look = Looks.Find(lookId);
            Looks.Apply(look, lookMode);
            barsFloat = look.Flag("Look.BarsFloat");
            RefreshAudioCards();
            seeThrough = look.Flag("Look.SeeThrough");
            springMotion = look.Flag("Look.Spring");
            fadeInMs = Number(look, "Motion.FadeIn", 120);
            fadeOutMs = Number(look, "Motion.FadeOut", 300);
            Motion.Fast = Number(look, "Motion.Fast", 167);
            Motion.Normal = Number(look, "Motion.Normal", 250);
            Motion.Slow = Number(look, "Motion.Slow", 333);
            Motion.Distance = Number(look, "Motion.Distance", 1);
            Motion.Press = Number(look, "Motion.Press", 1);
            Motion.Speed = Number(look, "Motion.Speed", 1); // only a Custom look sets it (Speed: Fastest .. Super slow)
            Motion.Squish = look.Flag("Motion.Squish"); // Liquid Glass (and a Custom look based on it, whatever its motion style)
            Motion.Style = look.Get("Motion.Ease") as string ?? "decelerate";
            ApplyMotionLevel();
            if (root == null) return; // starting up: the window does the rest once it exists
            ApplyWindowLook();
            PlaceBars();
            UpdateControls();
            UpdateSettingsLens(false);
            RefreshLookCards();
            UpdateReadability();
            if (settingsOpen) foreach (var refresh in settingsRefresh) refresh(); // e.g. "Colours for" on Custom look follows dark/light
        }

        // The parts of a look that live in the window itself rather than in its contents.
        void ApplyWindowLook()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            bool glass = seeThrough && !fullscreen && !mini;
            miniClear = MiniSeeThrough();
            bool clear = glass || miniClear; // the window itself is see-through
            try
            {
                int dark = Looks.Light ? 0 : 1; // dark or light window frame (Windows 10 20H1+ uses 20, older builds 19)
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, 4);
            }
            catch { }
            // Full screen and the mini player get no Windows frame: its 1-pixel border is drawn in the Windows
            // accent colour when "Show accent colour on title bars and window borders" is on (blue lines on the
            // screen edges / around the mini player). Windows 11 is told directly; both also get a square window
            // region (see RoundCorners), which stops Windows 10 drawing the frame and shadow.
            bool frameless = fullscreen || mini;
            try
            {
                int border = frameless ? unchecked((int)0xFFFFFFFE) : unchecked((int)0xFFFFFFFF); // DWMWA_BORDER_COLOR: none / default
                DwmSetWindowAttribute(hwnd, 34, ref border, 4); // (fails harmlessly before Windows 11)
            }
            catch { }
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null && src.CompositionTarget != null)
                src.CompositionTarget.BackgroundColor = clear ? Colors.Transparent : Colors.Black;
            chrome.GlassFrameThickness = clear ? new Thickness(-1) : frameless ? new Thickness(0) : new Thickness(1);
            if (glass && snapDir != null) Background = TestDesktop(); // tests: a stand-in for the desktop (never the real one)
            else if (clear) Background = Brushes.Transparent;
            else SetResourceReference(BackgroundProperty, "WindowBg");
            if (mini)
            {
                // Mini player on a photo with see-through parts (PNG, GIF...): nothing behind the picture.
                if (miniClear) stage.Background = Brushes.Transparent;
                else stage.SetResourceReference(Panel.BackgroundProperty, "FullBg");
            }
            var rootGrid = Content as Panel;
            if (rootGrid != null)
            {
                if (miniClear) rootGrid.Background = Brushes.Transparent;
                else rootGrid.SetResourceReference(Panel.BackgroundProperty, "WindowBg");
            }
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
            bool square = mini || fullscreen;
            if (!round && !square && !rounded) return;
            var m = src.CompositionTarget.TransformToDevice;
            var r = Application.Current.Resources["Radius.Window"];
            int radius = round && r is double ? (int)Math.Round((double)r * m.M11) : 0;
            int w = (int)Math.Round(ActualWidth * m.M11), h = (int)Math.Round(ActualHeight * m.M22);
            // Full screen and the mini player get a plain square region: Windows then draws no frame, border or
            // shadow around them (no accent-coloured lines; no outline around a see-through mini player).
            if (square) Looks.SetSquareRegion(hwnd, w, h);
            else Looks.SetRoundCorners(hwnd, w, h, radius);
            rounded = round || square;
        }

        // Mini player showing a photo whose pixels can be see-through (an alpha channel or a transparent colour).
        bool MiniSeeThrough()
        {
            if (!mini || fullscreen || shown == null || shown.IsVideo || shown.Failed) return false;
            var b = shown.Picture.Source as System.Windows.Media.Imaging.BitmapSource;
            if (b == null) return false;
            var f = b.Format;
            if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
                || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float) return true;
            return b.Palette != null && b.Palette.Colors.Any(c => c.A < 255);
        }

        void UpdateSlotOpacity()
        {
            if (slots == null) return;
            glassSlots = (seeThrough && !fullscreen && !mini) || miniClear;
            cover.Visibility = glassSlots ? Visibility.Collapsed : Visibility.Visible;
            foreach (var s in slots) s.Host.Opacity = !glassSlots || s == shown ? 1 : HiddenOpacity;
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
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(550 * Motion.Speed)) { EasingFunction = new SpringEase() });
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
        bool showTips = true; // Settings > Buttons: the little notes that appear when you point at a button

        // Tips off: every tooltip in the window is stopped as it is about to open (names for screen readers stay).
        static bool tipSwitchInstalled;

        static void InstallTipSwitch()
        {
            if (tipSwitchInstalled) return;
            tipSwitchInstalled = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnTipOpening));
        }

        static void OnTipOpening(object sender, ToolTipEventArgs e)
        {
            var w = Window.GetWindow((DependencyObject)sender) as ViewerWindow;
            if (w != null && !w.showTips) e.Handled = true;
        }

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
                    i => { CrossfadeWindow(250); lookMode = new[] { "Auto", "Dark", "Light" }[i]; ApplyLook(); }));
            AddCard("\uE916", "Animations",
                "Full: menus, Settings, messages, zoom and the rest move smoothly, in the look's own style. " +
                "Reduced: only short fades, nothing slides, scales or bounces. Off: everything changes at once. " +
                "\"Like Windows\" is Full, or Reduced when \"Show animations in Windows\" is off (Windows Settings > " +
                "Accessibility > Visual effects). Switching between files is always instant.",
                Dropdown(new[] { "Like Windows", "Full", "Reduced", "Off" },
                    () => Array.IndexOf(AnimationChoices, animations),
                    i => { animations = AnimationChoices[i]; ApplyMotionLevel(); }));
            AddCard("\uE8AB", "Transition between files",
                "\"Crossfade\" fades the last file out over the next one (120 ms). The next file still shows right away, " +
                "also when you click through quickly.",
                Dropdown(new[] { "None (instant)", "Crossfade" }, () => crossfadeFiles ? 1 : 0, i => crossfadeFiles = i == 1));
            var reset = new Button { Content = "Reset", MinWidth = 120, Height = 32 };
            reset.SetResourceReference(StyleProperty, "Filled");
            reset.Click += delegate
            {
                CrossfadeWindow(250); // the whole window fades from the old look to the new one
                lookId = Looks.DefaultId;
                lookMode = "Auto";
                ApplyLook();
                SaveSettings();
                foreach (var refresh in settingsRefresh) refresh();
            };
            AddCard("", "Reset to default", "Back to the Photos look, dark or light like Windows. Your custom look is kept.", reset);

            AddCustomLookPage();

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
            InstallTipSwitch();
            AddCard("\uE82F", "Tips when pointing at a button",
                "Short notes with the button's name and its key. Off: no notes. Screen readers still read every button's name, " +
                "and the keys stay listed in the \"...\" menus and Settings > Keyboard.", Toggle(() => showTips, v => showTips = v));            string bar = null;
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
            AddSubheading("Mini player");
            AddCard("\uE995", "Volume button in the mini player",
                "Shows a speaker next to the mini player's buttons: click to mute, scroll on it to change the volume, " +
                "point at it for the volume slider.", Toggle(() => miniVolume, v => { miniVolume = v; UpdateControls(); }));
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
            if (Looks.Custom == null) BuildCustomLook();
            foreach (var look in Looks.All.Concat(new[] { Looks.Custom }))
            {
                var l = look;
                var frame = new Border { BorderThickness = new Thickness(2), Padding = new Thickness(2), CornerRadius = new CornerRadius(10) };
                frame.Child = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = Mock(look, Looks.IsLight(look, lookMode)) };
                previewsLight = Looks.Light;
                var name = new TextBlock { Text = look.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                var text = new TextBlock { Text = look.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 0) };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var stack = new StackPanel { Width = 200 };
                stack.Children.Add(frame);
                stack.Children.Add(name);
                stack.Children.Add(text);
                var card = new Button { Content = stack, Padding = new Thickness(6), Height = double.NaN, Margin = new Thickness(0, 0, 12, 12), Tag = look.Id, ToolTip = look.Name, VerticalAlignment = VerticalAlignment.Top };
                card.SetResourceReference(StyleProperty, "Subtle");
                System.Windows.Automation.AutomationProperties.SetName(card, look.Id == CustomId ? "Custom look: " + look.Name : look.Name + " look");
                card.Click += delegate
                {
                    CrossfadeWindow(250); // the whole window fades from the old look to the new one
                    lookId = l.Id;
                    ApplyLook();
                    SaveSettings();
                };
                // The preview lifts a little under the pointer.
                card.MouseEnter += delegate { if (Motion.Moves) { card.RenderTransformOrigin = new Point(0.5, 0.5); var p = Motion.PartsOf(card); Motion.To(p.Scale, ScaleTransform.ScaleXProperty, 1.02, Motion.Ms(Motion.Fast), Motion.Smooth); Motion.To(p.Scale, ScaleTransform.ScaleYProperty, 1.02, Motion.Ms(Motion.Fast), Motion.Smooth); } };
                card.MouseLeave += delegate { var p = Motion.PartsOf(card); Motion.To(p.Scale, ScaleTransform.ScaleXProperty, 1, Motion.Ms(Motion.Fast), Motion.Smooth); Motion.To(p.Scale, ScaleTransform.ScaleYProperty, 1, Motion.Ms(Motion.Fast), Motion.Smooth); };
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
            if (!animate || !Motion.Moves)
            {
                Motion.Set(move, TranslateTransform.YProperty, p0.Y);
                return;
            }
            Motion.To(move, TranslateTransform.YProperty, p0.Y, Motion.Ms(Motion.Normal), Motion.In);
        }
    }
}
