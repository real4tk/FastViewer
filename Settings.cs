// FastViewer: the Settings page, window-size-dependent layout, and file actions (Save as, Save a frame).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        // Options (saved in settings.ini next to volume etc.)
        bool playNextAtEnd;      // when a video ends (and Repeat is off): go to the next file
        bool autoplay = true, dimVideo = true, confirmDelete = true, wheelZooms, smallActualSize, wrapAround = true;
        double hideDelay = 2.5;   // seconds; 0 = never hide the controls
        int slideSeconds = 4;
        int skipBack = 10, skipForward = 30; // seconds for the skip buttons (and J / L)
        static readonly int[] SkipChoices = { 5, 10, 15, 30, 60 };
        int filter;               // 0 photos and videos, 1 only videos, 2 only photos
        int order;                // 0 like File Explorer, 1 name, 2 newest first, 3 oldest first

        bool settingsOpen, narrow;
        StackPanel settingsList;

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        void LoadOption(string key, string v)
        {
            switch (key)
            {
                case "autoplay": autoplay = v == "1"; break;
                case "dim": dimVideo = v == "1"; break;
                case "confirmDelete": confirmDelete = v == "1"; break;
                case "wheelZooms": wheelZooms = v == "1"; break;
                case "actualSize": smallActualSize = v == "1"; break;
                case "wrap": wrapAround = v == "1"; break;
                case "hideDelay": hideDelay = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "slideSeconds": slideSeconds = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "filter": filter = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "order": order = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "skipBack": skipBack = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "skipForward": skipForward = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "pinned": pinned = v == "1"; break;
                case "preload": preloadAhead = Math.Max(1, Math.Min(3, int.Parse(v, CultureInfo.InvariantCulture))); break;
                case "resume": resumeVideos = v == "1"; break;
                case "saveRotation": saveRotation = v == "1"; break;
                case "autoUpdate": autoUpdate = v == "1"; break;
                case "playNext": playNextAtEnd = v == "1"; break;
                case "keepReady": keepReady = v == "1"; break;
                case "strip": showStrip = v == "1"; break;
                case "gridSize": gridSize = Math.Max(0, Math.Min(2, int.Parse(v, CultureInfo.InvariantCulture))); break;
                default: if (key.StartsWith("key.")) savedKeys[key.Substring(4)] = v; break;
                case "lastUpdateCheck": lastUpdateCheck = new DateTime(long.Parse(v, CultureInfo.InvariantCulture)); break;
                case "miniLong": miniLong = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "miniLeft": miniLeft = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "miniTop": miniTop = double.Parse(v, CultureInfo.InvariantCulture); break;
            }
        }

        IEnumerable<string> OptionLines()
        {
            return new[]
            {
                "autoplay=" + (autoplay ? "1" : "0"),
                "dim=" + (dimVideo ? "1" : "0"),
                "confirmDelete=" + (confirmDelete ? "1" : "0"),
                "wheelZooms=" + (wheelZooms ? "1" : "0"),
                "actualSize=" + (smallActualSize ? "1" : "0"),
                "wrap=" + (wrapAround ? "1" : "0"),
                "hideDelay=" + hideDelay.ToString(CultureInfo.InvariantCulture),
                "slideSeconds=" + slideSeconds.ToString(CultureInfo.InvariantCulture),
                "filter=" + filter.ToString(CultureInfo.InvariantCulture),
                "order=" + order.ToString(CultureInfo.InvariantCulture),
                "skipBack=" + skipBack.ToString(CultureInfo.InvariantCulture),
                "skipForward=" + skipForward.ToString(CultureInfo.InvariantCulture),
                "pinned=" + (pinned ? "1" : "0"),
                "preload=" + preloadAhead.ToString(CultureInfo.InvariantCulture),
                "resume=" + (resumeVideos ? "1" : "0"),
                "saveRotation=" + (saveRotation ? "1" : "0"),
                "autoUpdate=" + (autoUpdate ? "1" : "0"),
                "playNext=" + (playNextAtEnd ? "1" : "0"),
                "keepReady=" + (keepReady ? "1" : "0"),
                "strip=" + (showStrip ? "1" : "0"),
                "gridSize=" + gridSize.ToString(CultureInfo.InvariantCulture),
                "lastUpdateCheck=" + lastUpdateCheck.Ticks.ToString(CultureInfo.InvariantCulture),
                "miniLong=" + miniLong.ToString(CultureInfo.InvariantCulture),
                "miniLeft=" + miniLeft.ToString(CultureInfo.InvariantCulture),
                "miniTop=" + miniTop.ToString(CultureInfo.InvariantCulture)
            };
        }

        bool PassesFilter(string f)
        {
            string ext = Path.GetExtension(f);
            if (filter == 1) return VideoExts.Contains(ext);
            if (filter == 2) return ImageExts.Contains(ext);
            return IsMedia(f);
        }

        void ApplyOrder(List<string> list)
        {
            if (order < 2) return; // 0: Explorer order is applied afterwards; 1: name order is already done
            var dates = list.ToDictionary(f => f, f => File.GetLastWriteTime(f), StringComparer.OrdinalIgnoreCase);
            var named = list.ToList(); // stable tie-break on name
            list.Sort((a, b) =>
            {
                int c = dates[a].CompareTo(dates[b]);
                if (c == 0) return named.IndexOf(a).CompareTo(named.IndexOf(b));
                return order == 2 ? -c : c;
            });
        }

        // Re-reads the current folder after a Show/Order change, staying on the same file.
        void ReloadFolder()
        {
            if (CurrentFile != null) OpenPath(CurrentFile);
        }

        // ---------------------------------------------------------------- layout

        // Darkens the video area (not the empty space around it) while controls are up, like Photos.
        void UpdateScrim()
        {
            if (scrim == null) return;
            var s = shown;
            bool show = dimVideo && s != null && s.IsVideo && s.Ready && !s.Failed;
            scrim.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            int w, h;
            DisplaySize(s, out w, out h);
            double sw = stage.ActualWidth, sh = stage.ActualHeight;
            double fit = FitScale(s) / Dpi();
            if (w <= 0 || fit <= 0 || s.Zoom.ScaleX > 1.001)
            {
                scrim.Margin = new Thickness(0);
                scrim.Width = sw;
                scrim.Height = sh;
                return;
            }
            scrim.Width = w * fit;
            scrim.Height = h * fit;
            scrim.Margin = new Thickness((sw - scrim.Width) / 2, (sh - scrim.Height) / 2, 0, 0);
        }

        // Narrow windows: drop button labels, then trim the file name, then hide extras, so nothing overlaps.
        void UpdateResponsive()
        {
            double w = ActualWidth;
            if (w <= 0 || titleText == null) return;
            bool compact = w < 900;
            F<FrameworkElement>("OpenLabel").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("OpenWithLabel").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("OpenWithChevron").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            F<FrameworkElement>("OpenWithIcon").Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

            var left = F<FrameworkElement>("TopLeft");
            var right = F<FrameworkElement>("TopRight");
            // Very narrow: these are also in the "..." menu.
            foreach (var name in new[] { "DeleteBtn", "CopyBtn", "OpenWithBtn" })
                F<FrameworkElement>(name).Visibility = w < 540 ? Visibility.Collapsed : Visibility.Visible;
            root.UpdateLayout(); // apply the label changes above before measuring
            // The name stays centred on the window, so it gets the space between the wider side and its mirror.
            double side = Math.Max(left.DesiredSize.Width, right.DesiredSize.Width) + 12;
            double room = w - root.Margin.Left - root.Margin.Right - 2 * side;
            titleText.MaxWidth = Math.Max(0, Math.Min(560, room));
            titleText.Visibility = room < 70 ? Visibility.Hidden : Visibility.Visible;

            F<FrameworkElement>("InfoBtn").Visibility = w < 480 ? Visibility.Collapsed : Visibility.Visible;
            narrow = w < 860;
            UpdateSettingsNav(w);
            F<FrameworkElement>("ZoomGroup").Visibility = w < 640 ? Visibility.Collapsed : Visibility.Visible;
            UpdateFacts();
        }

        // "View at actual size": small photos/videos aren't enlarged to fill the window.
        void ApplyStretch()
        {
            var dir = smallActualSize ? StretchDirection.DownOnly : StretchDirection.Both;
            foreach (var s in slots)
            {
                s.Video.StretchDirection = dir;
                s.Picture.StretchDirection = dir;
                FitPoster(s);
            }
            UpdateZoomUi();
            UpdateScrim();
        }

        // The first-frame picture is at most 1920 wide, so cap it at the video's own size to line up exactly.
        void FitPoster(Slot s)
        {
            bool cap = smallActualSize && s.Video.NaturalVideoWidth > 0;
            s.Poster.MaxWidth = cap ? s.Video.NaturalVideoWidth : double.PositiveInfinity;
            s.Poster.MaxHeight = cap ? s.Video.NaturalVideoHeight : double.PositiveInfinity;
        }

        void UpdateSkipButtons()
        {
            F<TextBlock>("BackText").Text = skipBack.ToString(CultureInfo.InvariantCulture);
            F<TextBlock>("FwdText").Text = skipForward.ToString(CultureInfo.InvariantCulture);
            F<Button>("BackBtn").ToolTip = "Back " + skipBack + " seconds (J)";
            F<Button>("FwdBtn").ToolTip = "Forward " + skipForward + " seconds (L)";
        }

        // ---------------------------------------------------------------- settings page

        void OpenSettings()
        {
            CloseGrid();
            if (playing) TogglePlay();
            settingsOpen = true;
            if (settingsList.Children.Count == 0 && settingsPages.Count > 0) ShowSettingsPage(settingsPages[0].Value);
            F<FrameworkElement>("SettingsPage").Visibility = Visibility.Visible;
        }

        void CloseSettings()
        {
            settingsOpen = false;
            F<FrameworkElement>("SettingsPage").Visibility = Visibility.Collapsed;
        }

        void BuildSettingsPage()
        {
            settingsList = F<StackPanel>("SettingsList");
            Topmost = pinned;
            OnClick("SettingsBackBtn", CloseSettings);

            AddGroup("Viewing");
            AddCard("", "Mouse wheel",
                "What the mouse wheel does when viewing a file. Ctrl + mouse wheel always zooms.",
                Dropdown(new[] { "View next or previous items", "Zoom in or out" },
                    () => wheelZooms ? 1 : 0, i => wheelZooms = i == 1));
            AddCard("", "Zoom preference",
                "How to show photos and videos that are smaller than the window.",
                Dropdown(new[] { "Fit to window", "View at actual size" },
                    () => smallActualSize ? 1 : 0, i => { smallActualSize = i == 1; ApplyStretch(); }));
            AddCard("", "Show",
                "Which files to go through when you press next or previous.",
                Dropdown(new[] { "Photos and videos", "Only videos", "Only photos" },
                    () => filter, i => { filter = i; ReloadFolder(); }));
            AddCard("", "Order",
                "\"Same as File Explorer\" follows how the folder is sorted in an open Explorer window.",
                Dropdown(new[] { "Same as File Explorer", "Name", "Date modified, newest first", "Date modified, oldest first" },
                    () => order, i => { order = i; ReloadFolder(); }));
            AddCard("", "Go back to the start after the last file",
                "When off, next and previous stop at the ends of the folder.",
                Toggle(() => wrapAround, v => { wrapAround = v; RequestStripUpdate(); }));
            AddCard("", "Thumbnail strip",
                "Shows small pictures of the files before and after this one in a band under the picture. Click one to jump to it. " +
                "The thumbnails are the ones File Explorer uses and load after you stop switching, so switching stays just as fast. " +
                "To see the whole folder at once, press G or click the grid button in the bottom bar (that works with the strip off too).",
                Toggle(() => showStrip, v => { showStrip = v; ApplyStrip(); }));
            AddCard("\uE840", "Keep on top",
                "Keep FastViewer above other windows, even when you click another app. The mini player (Ctrl+M) is always on top.",
                Toggle(() => pinned, v => { pinned = v; Topmost = pinned || mini; }));
            AddCard("", "Ask for permission to delete",
                "Show a confirmation before moving a file to the Recycle Bin.",
                Toggle(() => confirmDelete, v => confirmDelete = v));

            AddCard("\uE7AD", "When you rotate a photo",
                "\"Only how it's shown\" leaves the file alone. \"Also save it to the file\" turns the photo for good, like Photos does " +
                "(JPEGs without any quality loss). Videos are never changed; use \"Make compatible copy\" for an upright copy.",
                Dropdown(new[] { "Only how it's shown", "Also save it to the file" },
                    () => saveRotation ? 1 : 0, i => saveRotation = i == 1));

            AddGroup("Videos");
            AddCard("\uE7F2", "Continue where you left off",
                "Videos longer than a minute start again where you stopped watching. A message lets you start over instead.",
                Toggle(() => resumeVideos, v => resumeVideos = v));
            AddCard("", "Play videos automatically",
                "Start playing as soon as you open or switch to a video.",
                Toggle(() => autoplay, v => autoplay = v));
            AddCard("", "Repeat videos",
                "Start a video over when it ends.",
                Toggle(() => loop, v => { loop = v; UpdateControls(); }));
            AddCard("\uE893", "When a video ends",
                "\"Play the next file\" moves on by itself, handy for watching many clips in a row. " +
                "Repeat (R) still loops a video exactly as before and takes priority.",
                Dropdown(new[] { "Stop", "Play the next file" }, () => playNextAtEnd ? 1 : 0, i => playNextAtEnd = i == 1));
            AddCard("", "Dim video while controls are showing",
                "Darkens the video slightly so the controls are easier to see, like Photos.",
                Toggle(() => dimVideo, v => { dimVideo = v; UpdateScrim(); }));
            AddCard("", "Hide controls after",
                "How long the controls stay after you stop moving the mouse. Any mouse movement brings them back.",
                Dropdown(new[] { "1.5 seconds", "2.5 seconds", "5 seconds", "Never" },
                    () => hideDelay <= 0 ? 3 : hideDelay < 2 ? 0 : hideDelay < 4 ? 1 : 2,
                    i =>
                    {
                        hideDelay = new[] { 1.5, 2.5, 5, 0 }[i];
                        idleTimer.Interval = TimeSpan.FromSeconds(hideDelay > 0 ? hideDelay : 2.5);
                    }));
            var skipLabels = SkipChoices.Select(s => s + " seconds").ToArray();
            AddCard("", "Skip back",
                "How far the skip back button and the J key jump.",
                Dropdown(skipLabels, () => Math.Max(0, Array.IndexOf(SkipChoices, skipBack)),
                    i => { skipBack = SkipChoices[i]; UpdateSkipButtons(); }));
            AddCard("", "Skip forward",
                "How far the skip forward button and the L key jump.",
                Dropdown(skipLabels, () => Math.Max(0, Array.IndexOf(SkipChoices, skipForward)),
                    i => { skipForward = SkipChoices[i]; UpdateSkipButtons(); }));
            AddCard("", "Slideshow speed",
                "How long each photo shows in a slideshow. Videos play to the end.",
                Dropdown(new[] { "2 seconds", "4 seconds", "8 seconds" },
                    () => slideSeconds <= 2 ? 0 : slideSeconds <= 4 ? 1 : 2,
                    i =>
                    {
                        slideSeconds = new[] { 2, 4, 8 }[i];
                        slideTimer.Interval = TimeSpan.FromSeconds(slideSeconds);
                    }));

            AddGroup("Keyboard");
            AddShortcutCards();

            AddGroup("Speed");
            AddCard("\uE7E8", "Stay ready in the background",
                "When you close FastViewer it keeps waiting, hidden, so the next file you double-click opens instantly. " +
                "Files you open while a FastViewer window is open get a window of their own. " +
                "While waiting it lets go of all files and frees most memory. Icon next to the clock: " +
                "double-click to open, right-click > Quit. After restarting your PC it starts normally the first time.",
                Toggle(() => keepReady, v => keepReady = v));
            AddCard("\uE81E", "Preload",
                "How many files ahead and behind FastViewer gets ready, so switching is instant. With 1, clicking faster than about " +
                "5 times a second can make videos wait a moment; 2 keeps up with very fast clicking. More uses more memory. " +
                "Takes effect the next time you open FastViewer.",
                Dropdown(new[] { "1 file each way (least memory)", "2 files each way (recommended)", "3 files each way" },
                    () => preloadAhead - 1, i => preloadAhead = i + 1));
            var speedUp = new Button { Style = (Style)Application.Current.Resources["Filled"], Content = "Speed up", MinWidth = 120, Height = 32 };
            speedUp.Click += delegate { SpeedUpStartup(); };
            AddCard("\uE945", "Start even faster",
                "Lets Windows prepare FastViewer ahead of time, so it opens quicker (it already learns from earlier starts). " +
                "Needs your OK as administrator once. Do it again after updating FastViewer.",
                speedUp);

            AddGroup("Default app");
            var register = new Button { Style = (Style)Application.Current.Resources["Filled"], Content = "Add", MinWidth = 120, Height = 32 };
            register.Click += delegate { register.Content = RegisterOpenWith() ? "Added" : "Couldn't add"; };
            AddCard("", "Show FastViewer in \"Open with\"",
                "Lists FastViewer for photo and video files in Open with and in Windows' Default apps. " +
                "Keep FastViewer.exe in a permanent folder first, since this remembers where it is.",
                register);
            var defaults = new Button { Style = (Style)Application.Current.Resources["Filled"], Content = "Open Default apps", Height = 32 };
            defaults.Click += delegate { try { Process.Start("ms-settings:defaultapps"); } catch { } };
            AddCard("", "Make FastViewer the default",
                "In Windows Settings, choose \"Choose default apps by file type\" and pick FastViewer for .mp4, .jpg, and so on.",
                defaults);

            AddGroup("Updates");
            AddCard("\uE895", "Check for updates automatically",
                UpdateRepo.Length == 0
                    ? "Not set up in this version yet."
                    : "When FastViewer starts (at most once a day) it asks github.com/" + UpdateRepo + " if there's a newer version, " +
                      "and offers to update. Nothing is installed without you clicking \"Update now\"; your settings stay.",
                Toggle(() => autoUpdate, v => autoUpdate = v));
            var checkNow = new Button { Style = (Style)Application.Current.Resources["Filled"], Content = "Check now", MinWidth = 120, Height = 32 };
            checkNow.Click += delegate { CloseSettings(); CheckForUpdates(true); };
            AddCard("\uE72C", "Check for updates now", "You have version " + CurrentVersion.ToString(3) + ".", checkNow);

            AddGroup("About");
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            AddCard("\uE946", "FastViewer " + version.ToString(3),
                "A fast photo and video viewer. See \"What's new.txt\" for what changed in each version. " +
                "Settings are saved in " + SettingsFile, null);
        }

        // Settings categories: one page per category, picked from the side menu.
        readonly List<KeyValuePair<Button, StackPanel>> settingsPages = new List<KeyValuePair<Button, StackPanel>>();
        StackPanel settingsPage;

        static string CategoryIcon(string name)
        {
            switch (name)
            {
                case "Viewing": return "\uE8B9";
                case "Videos": return "\uE714";
                case "Keyboard": return "\uE765";
                case "Speed": return "\uE945";
                case "Updates": return "\uE895";
                case "Default app": return "\uE7AC";
                default: return "\uE946";
            }
        }

        void AddGroup(string name)
        {
            settingsPage = new StackPanel();
            settingsPage.Children.Add(new TextBlock { Text = name, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });

            var indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = (Brush)Application.Current.Resources["Accent"], Visibility = Visibility.Hidden };
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(indicator);
            row.Children.Add(new TextBlock { Text = CategoryIcon(name), FontFamily = Ui.Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            row.Children.Add(label);
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["Subtle"],
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = name
            };
            var page = settingsPage;
            button.Click += delegate { ShowSettingsPage(page); };
            F<StackPanel>("SettingsNav").Children.Add(button);
            settingsPages.Add(new KeyValuePair<Button, StackPanel>(button, page));
        }

        void ShowSettingsPage(StackPanel page)
        {
            settingsList.Children.Clear();
            settingsList.Children.Add(page);
            foreach (var p in settingsPages)
            {
                bool on = p.Value == page;
                p.Key.Background = on ? new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
                ((FrameworkElement)((StackPanel)p.Key.Content).Children[0]).Visibility = on ? Visibility.Visible : Visibility.Hidden;
            }
            F<System.Windows.Controls.ScrollViewer>("SettingsScroll").ScrollToTop();
        }

        // Narrow windows: the side menu shows icons only.
        void UpdateSettingsNav(double width)
        {
            bool slim = width < 760;
            F<ColumnDefinition>("SettingsNavCol").Width = new GridLength(slim ? 64 : 230);
            F<FrameworkElement>("SettingsTitle").Visibility = slim ? Visibility.Collapsed : Visibility.Visible;
            foreach (var p in settingsPages)
                ((FrameworkElement)((StackPanel)p.Key.Content).Children[2]).Visibility = slim ? Visibility.Collapsed : Visibility.Visible;
        }

        void AddCard(string glyph, string title, string description, FrameworkElement control)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = Ui.Icons, FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 20, 0)
            });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            if (description != null) texts.Children.Add(new TextBlock
            {
                Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextSecondary"]
            });
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            if (control != null)
            {
                control.Margin = new Thickness(24, 0, 0, 0);
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                grid.Children.Add(control);
            }

            settingsPage.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = description == null ? new Thickness(16, 6, 16, 6) : new Thickness(16, 12, 16, 12),
                MinHeight = description == null ? 48 : 68,
                Margin = new Thickness(0, 0, 0, 4),
                Child = grid
            });
        }

        CheckBox Toggle(Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Style = (Style)Application.Current.Resources["ToggleSwitch"], IsChecked = get() };
            cb.Click += delegate
            {
                set(cb.IsChecked == true);
                SaveSettings();
            };
            return cb;
        }

        Button Dropdown(string[] options, Func<int> get, Action<int> set)
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var chevron = new TextBlock
            {
                Text = "", FontFamily = Ui.Icons, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            var content = new Grid();
            content.Children.Add(text);
            content.Children.Add(chevron);
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["Filled"],
                Content = content,
                MinWidth = 240,
                Height = 32,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            text.Text = options[get()];
            b.Click += delegate
            {
                var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, MinWidth = b.ActualWidth, VerticalOffset = 2 };
                for (int i = 0; i < options.Length; i++)
                {
                    int choice = i;
                    menu.Items.Add(Item(options[i], get() == i ? "" : null, delegate
                    {
                        set(choice);
                        text.Text = options[choice];
                        SaveSettings();
                    }));
                }
                menu.IsOpen = true;
            };
            return b;
        }

        // ---------------------------------------------------------------- file actions

        // Rotation per file, in 90 degree steps. Only changes how it's shown; the file isn't modified.
        readonly Dictionary<string, int> rotations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Turns the current photo or video a quarter turn clockwise, with a short animation (like Telegram).
        void Rotate()
        {
            var s = shown;
            if (s == null || s.File == null || s.Failed || !s.Ready) return;
            string file = s.File;
            int from;
            rotations.TryGetValue(file, out from);
            int to = (from + 90) % 360;
            rotations[file] = to;
            ResetZoom(s);
            RequestStripUpdate(); // its thumbnail turns too

            var turn = new DoubleAnimation(from, from + 90, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            turn.Completed += delegate
            {
                int now;
                if (s.File != file || !rotations.TryGetValue(file, out now) || now != to) return; // turned again meanwhile
                s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                s.Rotation.Angle = to;
                UpdateZoomUi();
                UpdateScrim();
                if (mini) FitMiniWindow(false);
                AfterPhotoRotated();
            };
            s.Rotation.BeginAnimation(RotateTransform.AngleProperty, turn);
            UpdateZoomUi();
            UpdateScrim();
            ShowOverlay();
        }

        // Width and height as shown on screen (swapped when turned sideways).
        void DisplaySize(Slot s, out int w, out int h)
        {
            ContentSize(s, out w, out h);
            int angle;
            if (s != null && s.File != null && rotations.TryGetValue(s.File, out angle) && (angle == 90 || angle == 270))
            {
                int t = w;
                w = h;
                h = t;
            }
        }

        void SaveAs()
        {
            string f = CurrentFile;
            if (f == null) return;
            string ext = Path.GetExtension(f);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save as",
                FileName = Path.GetFileName(f),
                InitialDirectory = Path.GetDirectoryName(f),
                DefaultExt = ext,
                Filter = ext.TrimStart('.').ToUpperInvariant() + " file|*" + ext + "|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase)) return;
            try { File.Copy(f, dlg.FileName, true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't save", MessageBoxButton.OK); }
        }

        // Saves the video frame on screen as a PNG picture.
        void SaveFrame()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed || s.Video.NaturalVideoWidth == 0) return;
            if (playing) TogglePlay();
            var pos = s.Video.Position;
            double k = Math.Min(1.0, 3840.0 / s.Video.NaturalVideoWidth);
            BitmapSource frame = Grab(s.Video, (int)(s.Video.NaturalVideoWidth * k), (int)(s.Video.NaturalVideoHeight * k));
            int turn;
            if (rotations.TryGetValue(s.File, out turn) && turn != 0) // save it the way it's shown
                frame = new TransformedBitmap(frame, new RotateTransform(turn));
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save a frame",
                FileName = Path.GetFileNameWithoutExtension(s.File) + " frame " + pos.ToString(@"mm\-ss") + ".png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                DefaultExt = ".png",
                Filter = "PNG picture|*.png"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(frame));
                using (var fs = File.Create(dlg.FileName)) png.Save(fs);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't save", MessageBoxButton.OK); }
        }

        // Registers FastViewer for its file types (current user only), so it appears in "Open with"
        // and in Windows' Default apps list. Only runs when you press the button in Settings.
        bool RegisterOpenWith()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var app = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\FastViewer.exe"))
                {
                    app.SetValue("FriendlyAppName", "FastViewer");
                    app.SetValue("TypeOverlay", ""); // no FastViewer badge on thumbnails in File Explorer
                    using (var icon = app.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exe + "\",0");
                    using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                    using (var types = app.CreateSubKey("SupportedTypes"))
                        foreach (var ext in ImageExts.Concat(VideoExts)) types.SetValue(ext, "");
                }
                foreach (var ext in ImageExts.Concat(VideoExts))
                    using (Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithList\FastViewer.exe")) { }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
                return true;
            }
            catch { return false; }
        }
    }
}
