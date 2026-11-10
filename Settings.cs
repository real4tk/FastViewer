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
using System.Windows.Input;
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
        int centerButton;         // play/pause in the middle of a video: 0 show, 1 only while paused, 2 hide
        bool fadePaused;          // let the controls fade on a paused video too
        int filter;              // Settings > Viewing > Show: 0 everything, 1 photos and videos, 2 only photos, 3 only videos, 4 only music
        static readonly string[] ShowChoices = { "Everything", "Photos and videos", "Only photos", "Only videos", "Only music" };
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
                case "hideDelay": hideDelay = Clamp(Num(v), 0, 60, 2.5); break;
                case "slideSeconds": slideSeconds = (int)Clamp(Num(v), 1, 60, 4); break;
                case "show": filter = (int)Clamp(Num(v), 0, 4, 0); break;
                // Before 1.20 (and its test builds): filter 0 photos and videos, 1 only videos, 2 only photos, plus music=0/1.
                case "filter": filter = new[] { 0, 3, 2 }[(int)Clamp(Num(v), 0, 2, 0)]; break;
                case "order": order = (int)Clamp(Num(v), 0, 3, 0); break;
                case "skipBack": skipBack = (int)Clamp(Num(v), 1, 600, 10); break;
                case "skipForward": skipForward = (int)Clamp(Num(v), 1, 600, 30); break;
                case "pinned": pinned = v == "1"; break;
                case "preload": preloadAhead = Math.Max(1, Math.Min(3, int.Parse(v, CultureInfo.InvariantCulture))); break;
                case "resume": resumeVideos = v == "1"; break;
                case "saveRotation": saveRotation = v == "1"; break;
                case "autoUpdate": autoUpdate = v == "1"; break;
                case "playNext": playNextAtEnd = v == "1"; break;
                case "keepReady": keepReady = v == "1"; break;
                case "backgroundTold": toldAboutBackground = v == "1"; break;
                case "miniVolume": miniVolume = v == "1"; break;
                case "tips": showTips = v != "0"; break;
                case "speeds": LoadSpeeds(v); break;
                case "music": if (v == "0" && filter == 0) filter = 1; break; // (1.20 test builds; comes after filter=)
                case "animations": animations = AnimationChoices.Contains(v) ? v : "Auto"; break;
                case "fileTransition": crossfadeFiles = v == "Crossfade"; break;
                case "strip": showStrip = v == "1"; break;
                case "centerPlay": centerButton = Math.Max(0, Math.Min(2, int.Parse(v, CultureInfo.InvariantCulture))); break;
                case "fadePaused": fadePaused = v == "1"; break;
                case "arrows": if (v == "0") hiddenButtons.Add("Arrows"); break; // 1.12's switch, now in Settings > Buttons
                case "look": lookId = v; break;
                case "lookMode": lookMode = v == "Dark" || v == "Light" ? v : "Auto"; break;
                case "hide":
                    hiddenButtons.Clear();
                    foreach (var id in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) hiddenButtons.Add(id.Trim());
                    break;
                case "gridNames": gridNames = v == "1"; break;
                case "topMenu": LoadMenuLine(false, v); break;
                case "videoMenu": LoadMenuLine(true, v); break;
                case "gridSize": gridSize = Math.Max(0, Math.Min(2, int.Parse(v, CultureInfo.InvariantCulture))); break;
                case "gridSort": gridSort = Math.Max(0, Math.Min(GridSortNames.Length - 1, int.Parse(v, CultureInfo.InvariantCulture))); break;
                case "discordMb": { int mb = int.Parse(v, CultureInfo.InvariantCulture); discordMb = mb == 50 || mb == 500 ? mb : 20; } break;
                default:
                    if (LoadCustomOption(key, v)) { } // the custom look (CustomLook.cs)
                    else if (key.StartsWith("key.")) savedKeys[key.Substring(4)] = v;
                    else if (key.StartsWith("mouse.") && mouseActions.ContainsKey(key.Substring(6))) mouseActions[key.Substring(6)] = v;
                    break;
                case "lastUpdateCheck": lastUpdateCheck = new DateTime(long.Parse(v, CultureInfo.InvariantCulture)); break;
                case "miniLong": miniLong = Clamp(Num(v), 120, 4000, 480); break;
                case "miniLeft": miniLeft = Num(v); break;
                case "miniTop": miniTop = Num(v); break;
            }
        }

        static double Num(string v) { return double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture); }

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
                "show=" + filter.ToString(CultureInfo.InvariantCulture),
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
                "backgroundTold=" + (toldAboutBackground ? "1" : "0"),
                "miniVolume=" + (miniVolume ? "1" : "0"),
                "tips=" + (showTips ? "1" : "0"),
                SpeedsLine(),
                "animations=" + animations,
                "fileTransition=" + (crossfadeFiles ? "Crossfade" : "None"),
                "strip=" + (showStrip ? "1" : "0"),
                "centerPlay=" + centerButton.ToString(CultureInfo.InvariantCulture),
                "fadePaused=" + (fadePaused ? "1" : "0"),
                "look=" + lookId,
                "lookMode=" + lookMode,
                "hide=" + string.Join(",", hiddenButtons),
                "gridNames=" + (gridNames ? "1" : "0"),
                "gridSize=" + gridSize.ToString(CultureInfo.InvariantCulture),
                "gridSort=" + gridSort.ToString(CultureInfo.InvariantCulture),
                "discordMb=" + discordMb.ToString(CultureInfo.InvariantCulture),
                "lastUpdateCheck=" + lastUpdateCheck.Ticks.ToString(CultureInfo.InvariantCulture),
                "miniLong=" + miniLong.ToString(CultureInfo.InvariantCulture),
                "miniLeft=" + miniLeft.ToString(CultureInfo.InvariantCulture),
                "miniTop=" + miniTop.ToString(CultureInfo.InvariantCulture)
            }.Concat(new[] { MenuLine(false), MenuLine(true) }.Where(x => x != null)).Concat(CustomLines());
        }

        bool PassesFilter(string f)
        {
            string ext = Path.GetExtension(f);
            if (filter == 1) return ImageExts.Contains(ext) || VideoExts.Contains(ext);
            if (filter == 2) return ImageExts.Contains(ext);
            if (filter == 3) return VideoExts.Contains(ext);
            if (filter == 4) return AudioExts.Contains(ext);
            return IsMedia(f);
        }

        void ApplyOrder(List<string> list)
        {
            if (order < 2) return; // 0: Explorer order is applied afterwards; 1: name order is already done
            var dates = list.ToDictionary(f => f, f => File.GetLastWriteTime(f), StringComparer.OrdinalIgnoreCase);
            var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // stable tie-break on name
            for (int i = 0; i < list.Count; i++) named[list[i]] = i;
            list.Sort((a, b) =>
            {
                int c = dates[a].CompareTo(dates[b]);
                if (c == 0) return named[a].CompareTo(named[b]); // (copied or unzipped files often share a time)
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
            bool show = dimVideo && s != null && s.IsVideo && s.Ready && !s.Failed && !cropping;
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
            // Buttons switched off in Settings > Buttons, and in very narrow windows a few extras (also in the "..." menu).
            Action<string, bool> show = (name, fits) => F<FrameworkElement>(name).Visibility = Shown(name) && fits ? Visibility.Visible : Visibility.Collapsed;
            foreach (var name in new[] { "DeleteBtn", "CopyBtn", "OpenWithBtn" }) show(name, w >= 540);
            foreach (var name in new[] { "OpenBtn", "RotateBtn", "MiniBtn" }) show(name, true);
            root.UpdateLayout(); // apply the label changes above before measuring
            // The name stays centred on the window, so it gets the space between the wider side and its mirror.
            double side = Math.Max(left.DesiredSize.Width, right.DesiredSize.Width) + 12;
            double room = w - root.Margin.Left - root.Margin.Right - 2 * side;
            titleText.MaxWidth = Math.Max(0, Math.Min(560, room));
            titleText.Visibility = room < 70 ? Visibility.Hidden : Visibility.Visible;

            // The Info button sits in the middle of the bottom bar: only where it doesn't run into the groups on either side.
            var leftGroup = F<FrameworkElement>("BottomLeft"); var rightGroup = F<FrameworkElement>("BottomRight");
            leftGroup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            rightGroup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double half = (w - root.Margin.Left - root.Margin.Right) / 2;
            show("InfoBtn", w >= 480 && half - 26 > 8 + leftGroup.DesiredSize.Width && half + 26 < 2 * half - 6 - rightGroup.DesiredSize.Width);
            narrow = w < 860;
            UpdateSettingsNav(w);
            show("ZoomGroup", w >= 640);
            foreach (var name in new[] { "SlideshowBtn", "GridBtn", "FitBtn", "ZoomBtn", "FullBtn" }) show(name, true);
            F<FrameworkElement>("ZoomDivider").Visibility = Shown("FullBtn") && (Shown("FitBtn") || Shown("ZoomBtn") || (Shown("ZoomGroup") && w >= 640))
                ? Visibility.Visible : Visibility.Collapsed;
            F<RowDefinition>("BottomControlsRow").Height = new GridLength(BottomControlsShown() ? 32 : 0); // Minimal: no bottom bar
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
            F<Button>("BackBtn").ToolTip = WithKey("Back " + skipBack + " seconds", KeyOf("skipBack"));
            F<Button>("FwdBtn").ToolTip = WithKey("Forward " + skipForward + " seconds", KeyOf("skipForward"));
            System.Windows.Automation.AutomationProperties.SetName(F<Button>("BackBtn"), "Back " + skipBack + " seconds");
            System.Windows.Automation.AutomationProperties.SetName(F<Button>("FwdBtn"), "Forward " + skipForward + " seconds");
        }

        // ---------------------------------------------------------------- settings page

        void OpenSettings()
        {
            CloseGrid();
            SetVolumeSlider(false);
            if (playing) TogglePlay();
            settingsOpen = true;
            foreach (var refresh in settingsRefresh) refresh(); // things changed by a key or menu since last time (Keep on top...)
            if (settingsList.Children.Count == 0 && settingsPages.Count > 0) ShowSettingsPage(settingsPages[0].Value);
            var page = F<FrameworkElement>("SettingsPage");
            Motion.Show(page, 0, 24); // rises into place (Liquid Glass with a spring)
            PlaceBars(); // looks with floating bars dock them while Settings is open
            UpdateControls(); // the file buttons are off while Settings is open
            SettingsChrome(true);
            Dispatcher.BeginInvoke(new Action(delegate
            {
                UpdateSettingsLens(false);
                // Keyboard: start on the page in the side menu (Tab goes on into the page, Esc goes back).
                var current = settingsPages.FirstOrDefault(p => settingsList.Children.Contains(p.Value)).Key;
                if (settingsOpen && current != null) Keyboard.Focus(current);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Settings can be used with the keyboard: its buttons, switches and menus take part in Tab
        // (the viewer's buttons don't, so its keys keep working as shortcuts).
        static void MakeKeyboardReachable(object d)
        {
            var dep = d as DependencyObject;
            if (dep == null) return;
            var c = dep as Control;
            if (c is Button || c is CheckBox)
            {
                c.Focusable = true;
                c.IsTabStop = true;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(dep)) MakeKeyboardReachable(child);
        }

        void CloseSettings()
        {
            settingsOpen = false;
            UpdateControls();
            if (IsKeyboardFocusWithin) Keyboard.Focus(this); // back to the viewer's keys
            if (capturing != null) // a key box was still waiting: leaving Settings cancels it
            {
                capturing = null;
                RefreshShortcutEditors();
            }
            Motion.Hide(F<FrameworkElement>("SettingsPage"), 0, 8, 1, 120); // fades away, sinking a little
            PlaceBars();
            SettingsChrome(false);
        }

        // Settings is a screen of its own: the title bar says "Settings" and the buttons that act on a file
        // (which isn't on screen) step out instead of sitting there greyed out. In a see-through look the
        // viewer under the page is hidden too, so the page is frosted glass and not a ghost of the photo.
        void SettingsChrome(bool open)
        {
            titleText.Text = open ? "Settings" : CurrentFile != null ? Path.GetFileName(CurrentFile) : "";
            double ms = Motion.Ms(Motion.Fast, false);
            foreach (var name in new[] { "DeleteBtn", "CopyBtn", "RotateBtn", "MoreBtn", "MiniBtn", "OpenWithBtn" })
                Motion.To(F<FrameworkElement>(name), OpacityProperty, open ? 0 : 1, ms, Motion.Smooth);
            var view = F<FrameworkElement>("ViewArea");
            if (open && seeThrough) Motion.To(view, OpacityProperty, 0, Motion.Ms(Motion.Normal, false), Motion.Smooth);
            else Motion.Set(view, OpacityProperty, 1);
        }

        void BuildSettingsPage()
        {
            settingsList = F<StackPanel>("SettingsList");
            Topmost = pinned;
            OnClick("SettingsBackBtn", CloseSettings);

            AddAppearancePages(); // Appearance and Buttons come first
            AddGroup("Viewing");
            AddCard("", "Zoom preference",
                "How to show photos and videos that are smaller than the window.",
                Dropdown(new[] { "Fit to window", "View at actual size" },
                    () => smallActualSize ? 1 : 0, i => { smallActualSize = i == 1; ApplyStretch(); }));
            AddCard("", "Show",
                "Which files to go through when you press next or previous.",
                Dropdown(ShowChoices,
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

            AddCard("\uE7AD", "When you rotate a photo or video",
                "\"Only how it's shown\" leaves the file alone. \"Also save it to the file\" turns it for good, like Photos does " +
                "(JPEGs, MP4 and MOV videos without any quality loss). Either way FastViewer remembers the turn.",
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
            AddCard("", "Hide controls on a paused video too",
                "Normally the controls stay while a video is paused. Turn this on to let them (and the dimming) fade after the " +
                "usual time, so you can look at the paused picture. Moving the mouse brings them back.",
                Toggle(() => fadePaused, v => fadePaused = v));
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
            AddWideCard("", "Speeds in the menu",
                "Pick the playback speeds the video \"...\" menu shows. The Slower and Faster keys step through the same ones. " +
                "Normal (1x) is always there.", SpeedChips());
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

            AddGroup("Mouse");
            AddMouseCards();

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
                "Changes right away.",
                Dropdown(new[] { "1 file each way (least memory)", "2 files each way (recommended)", "3 files each way" },
                    () => preloadAhead - 1, i => SetPreload(i + 1)));
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
            var screen = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            screen.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            AddCard("\uE7F4", "Screen", "Animations follow your screen's refresh rate (with several screens, the fastest one). " +
                "After something has moved, this shows how many frames a second it really got.", screen);
            settingsRefresh.Add(delegate
            {
                screen.Text = Motion.ScreenRate + " Hz" + (Motion.LastFps > 0 ? ", animations at " + Math.Round(Motion.LastFps) + " fps" : "");
            });
        }

        // Settings categories: one page per category, picked from the side menu.
        readonly List<KeyValuePair<Button, StackPanel>> settingsPages = new List<KeyValuePair<Button, StackPanel>>();
        StackPanel settingsPage;

        static string CategoryIcon(string name)
        {
            switch (name)
            {
                case "Appearance": return "\uE790";
                case "Custom look": return "\uE771";
                case "Buttons": return "\uE8B9";
                case "Viewing": return "\uE91B";
                case "Videos": return "\uE714";
                case "Keyboard": return "\uE765";
                case "Mouse": return "\uE962";
                case "Speed": return "\uE945";
                case "Updates": return "\uE895";
                case "Default app": return "\uE7AC";
                default: return "\uE946";
            }
        }

        void AddGroup(string name)
        {
            settingsPage = new StackPanel();
            var heading = new TextBlock { Text = name, Margin = new Thickness(0, 0, 0, 16) };
            heading.SetResourceReference(TextBlock.FontSizeProperty, "Size.PageTitle");
            heading.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Title");
            heading.SetResourceReference(TextBlock.FontWeightProperty, "Weight.Title");
            settingsPage.Children.Add(heading);

            var indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Visibility = Visibility.Hidden };
            indicator.SetResourceReference(Border.BackgroundProperty, "Accent");
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(indicator);
            row.Children.Add(new Glyph { Code = CategoryIcon(name), Size = 16, Margin = new Thickness(10, 0, 0, 0) });
            row.Children.Add(label);
            var button = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = name
            };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.SetResourceReference(StyleProperty, "Subtle");
            var page = settingsPage;
            button.Click += delegate { ShowSettingsPage(page); };
            F<StackPanel>("SettingsNav").Children.Add(button);
            settingsPages.Add(new KeyValuePair<Button, StackPanel>(button, page));
        }

        void ShowSettingsPage(StackPanel page)
        {
            if (page == appearancePage) BuildLookPreviews(); // drawn the first time they're needed, not at start-up
            if (page == customPage) FillCustomLookPage();
            var before = settingsPages.FirstOrDefault(p => settingsList.Children.Contains(p.Value)).Key;
            settingsList.Children.Clear();
            settingsList.Children.Add(page);
            MakeKeyboardReachable(page);
            MakeKeyboardReachable(F<FrameworkElement>("SettingsNav"));
            MakeKeyboardReachable(F<FrameworkElement>("SettingsBackBtn"));
            foreach (var p in settingsPages)
            {
                bool on = p.Value == page;
                if (on) p.Key.SetResourceReference(Button.BackgroundProperty, "NavSelected");
                else p.Key.Background = Brushes.Transparent;
                ((FrameworkElement)((StackPanel)p.Key.Content).Children[0]).Visibility = on && !springMotion ? Visibility.Visible : Visibility.Hidden;
            }
            var after = settingsPages.FirstOrDefault(p => p.Value == page).Key;
            if (before != null && after != null && before != after && !springMotion && before.IsVisible)
            {
                var marker = (FrameworkElement)((StackPanel)after.Content).Children[0];
                Glide(marker, 0, before.TranslatePoint(new Point(0, 0), after).Y / Math.Max(0.01, Motion.Distance)); // slides from the old item
            }
            F<System.Windows.Controls.ScrollViewer>("SettingsScroll").ScrollToTop();
            Motion.Enter(page, 16); // the new page rises into place
            UpdateSettingsLens(true);
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

        Border AddCard(string glyph, string title, string description, FrameworkElement control)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new Glyph { Code = glyph, Size = 16, Margin = new Thickness(2, 0, 20, 0) });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            if (description != null) texts.Children.Add(SecondaryText(description));
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            if (control != null)
            {
                if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control)))
                    System.Windows.Automation.AutomationProperties.SetName(control, title); // what a screen reader says for the switch or menu
                control.Margin = new Thickness(24, 0, 0, 0);
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                grid.Children.Add(control);
            }

            var card = Card(grid, description == null ? new Thickness(16, 6, 16, 6) : new Thickness(16, 12, 16, 12));
            card.MinHeight = description == null ? 48 : 68;
            settingsPage.Children.Add(card);
            return card;
        }

        // A card whose control sits under its text (the look cards).
        Border AddWideCard(string glyph, string title, string description, FrameworkElement content)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(new Glyph { Code = glyph, Size = 16, Margin = new Thickness(2, 0, 20, 0) });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(SecondaryText(description));
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            Grid.SetRow(content, 1);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            var card = Card(grid, new Thickness(16, 12, 16, 12));
            settingsPage.Children.Add(card);
            return card;
        }

        // A card's explanation. A long one shows its first sentence with "More" behind it: pointing at More shows
        // the rest as a tip, clicking it (or Enter on it) opens the whole text in the card, "Less" folds it again.
        // Nothing is left out, the pages are just a lot calmer to look at.
        static TextBlock SecondaryText(string text)
        {
            var t = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            int cut = SummaryEnd(text);
            if (cut < 0)
            {
                t.Text = text;
                return t;
            }
            string first = text.Substring(0, cut), rest = text.Substring(cut).Trim();
            Action<bool, bool> fill = null;
            fill = (open, focus) =>
            {
                t.Inlines.Clear();
                t.Inlines.Add(new System.Windows.Documents.Run(open ? text : first));
                t.Inlines.Add(new System.Windows.Documents.Run("  "));
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(open ? "Less" : "More")) { Cursor = Cursors.Hand };
                if (!open) link.ToolTip = new TextBlock { Text = rest, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
                link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextMain");
                System.Windows.Automation.AutomationProperties.SetName(link, open ? "Show less" : "Show more: " + rest);
                link.Click += delegate { fill(!open, link.IsKeyboardFocused); };
                t.Inlines.Add(link);
                if (focus) link.Focus();
            };
            fill(false, false);
            return t;
        }

        // Where a long explanation's first sentence ends (-1: short enough to show whole).
        internal static int SummaryEnd(string text)
        {
            if (text == null || text.Length < 110) return -1;
            for (int i = 25; i < text.Length - 1; i++)
            {
                if ((text[i] != '.' && text[i] != '?' && text[i] != '!') || text[i + 1] != ' ') continue;
                string before = text.Substring(i - 3, 3);
                if (before == "e.g" || before == "i.e") continue;
                return text.Length - (i + 1) < 40 ? -1 : i + 1;
            }
            return -1;
        }

        static Border Card(UIElement child, Thickness padding)
        {
            var card = new Border { BorderThickness = new Thickness(1), Padding = padding, Margin = new Thickness(0, 0, 0, 4), Child = child };
            card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            card.SetResourceReference(Border.BorderBrushProperty, "CardEdge");
            card.SetResourceReference(Border.CornerRadiusProperty, "Radius.Card");
            return card;
        }

        readonly List<Action> settingsRefresh = new List<Action>();

        // One button per speed: highlighted ones are shown in the menu. Click to add or remove.
        FrameworkElement SpeedChips()
        {
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var value in AllSpeeds)
            {
                double sp = value;
                var b = new Button { Content = SpeedText(sp), MinWidth = 56, Height = 32, Margin = new Thickness(0, 0, 6, 6) };
                System.Windows.Automation.AutomationProperties.SetName(b, "Speed " + SpeedText(sp));
                Action paint = delegate
                {
                    bool on = sp == 1 || shownSpeeds.Contains(sp);
                    b.Style = (Style)Application.Current.Resources[on ? "AccentButton" : "Filled"];
                    b.ToolTip = sp == 1 ? "Always shown" : on ? "Shown in the menu: click to hide" : "Hidden: click to show it in the menu";
                };
                paint();
                settingsRefresh.Add(paint);
                b.Click += delegate
                {
                    if (sp == 1) return;
                    if (!shownSpeeds.Remove(sp)) shownSpeeds.Add(sp);
                    paint();
                    SaveSettings();
                };
                row.Children.Add(b);
            }
            return row;
        }

        CheckBox Toggle(Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Style = (Style)Application.Current.Resources["ToggleSwitch"], IsChecked = get() };
            settingsRefresh.Add(delegate { cb.IsChecked = get(); });
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
            var chevron = new Glyph { Code = "\uE70D", Size = 10, HorizontalAlignment = HorizontalAlignment.Right };
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
            Func<int> at = () => Math.Max(0, Math.Min(options.Length - 1, get())); // never past the list, whatever settings.ini says
            text.Text = options[at()];
            settingsRefresh.Add(delegate { text.Text = options[at()]; });
            b.Click += delegate
            {
                if (ClosingOwnMenu(b)) return;
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
                OpenMenu(menu, b);
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
            if (s == null || s.File == null || s.Failed || !s.Ready || s.IsAudio) return;
            if (cropping) EndCrop(); // the frame was for the picture as it lay
            string file = s.File;
            int from;
            rotations.TryGetValue(file, out from);
            int to = (from + 90) % 360;
            Motion.Stop("zoom");
            ResetZoom(s);
            double before = FitScale(s);       // screen pixels per picture pixel, before turning
            rotations[file] = to;
            // Remembered across restarts and updates, unless it's how the file itself is stored anyway.
            TurnMemory.Set(file, to == (s.IsVideo ? VideoInfo.Rotation(file) : 0) ? (int?)null : to);
            AfterPhotoRotated(file); // saved into the file a moment later (if that's on), even if you switch away first
            RequestStripUpdate(); // its thumbnail turns too

            // The layout takes the new orientation at once (one re-fit, not one per frame); a counter-turn on top
            // makes it look as if it turns and re-sizes from where it was, settling in the look's own way.
            s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            s.Rotation.Angle = to;
            s.Rotor.UpdateLayout();
            double after = FitScale(s);
            if (Motion.Moves && before > 0 && after > 0)
            {
                s.Rotor.RenderTransformOrigin = new Point(0.5, 0.5);
                var spin = s.Rotor.RenderTransform as TransformGroup;
                if (spin == null || spin.Children.Count != 2)
                {
                    spin = new TransformGroup();
                    spin.Children.Add(new ScaleTransform(1, 1));
                    spin.Children.Add(new RotateTransform(0));
                    s.Rotor.RenderTransform = spin;
                }
                var size = (ScaleTransform)spin.Children[0];
                var angle = (RotateTransform)spin.Children[1];
                double k = before / after;
                double turnMs = Motion.Ms(Motion.Normal);
                Motion.FromTo(angle, RotateTransform.AngleProperty, angle.Angle - 90, 0, turnMs, Motion.In);
                Motion.FromTo(size, ScaleTransform.ScaleXProperty, size.ScaleX * k, 1, turnMs, Motion.In);
                Motion.FromTo(size, ScaleTransform.ScaleYProperty, size.ScaleY * k, 1, turnMs, Motion.In);
            }
            if (mini) FitMiniWindow(false);
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
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, null, null, 8, "error"); } // (an in-app message, not a Windows box)
        }

        // The video frame on screen at its own size (at most 4K wide), turned the way it's shown.
        BitmapSource FrameAsShown(Slot s)
        {
            double k = Math.Min(1.0, 3840.0 / s.Video.NaturalVideoWidth);
            BitmapSource frame = Grab(s.Video, (int)(s.Video.NaturalVideoWidth * k), (int)(s.Video.NaturalVideoHeight * k));
            int turn;
            if (rotations.TryGetValue(s.File, out turn) && turn != 0) frame = new TransformedBitmap(frame, new RotateTransform(turn));
            return frame;
        }

        // Puts the video frame on screen on the clipboard (as a picture and as PNG, so Discord, Paint, Word... all
        // take it). The video keeps playing; the frame is the one showing when you pressed.
        void CopyFrame()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed || s.Video.NaturalVideoWidth == 0) return;
            try
            {
                var frame = new CachedBitmap(FrameAsShown(s), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                var png = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame));
                enc.Save(png);
                var data = new DataObject();
                data.SetImage(frame);
                data.SetData("PNG", png, false);
                if (snapDir == null) Clipboard.SetDataObject(data, true); // tests never touch the user's clipboard
                else Log("copy frame " + frame.PixelWidth + "x" + frame.PixelHeight + " png " + png.Length + " bytes");
                ShowToast("Frame copied", null, null, 2, "copyFrame");
            }
            catch (Exception ex) { ShowToast("Couldn't copy the frame: " + ex.Message, null, null, 3, "copyFrame"); }
        }

        // Saves the video frame on screen as a PNG picture.
        void SaveFrame()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.IsAudio || s.Failed || s.Video.NaturalVideoWidth == 0) return;
            if (playing) TogglePlay();
            var pos = s.Video.Position;
            BitmapSource frame = FrameAsShown(s);
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
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, null, null, 8, "error"); } // (an in-app message, not a Windows box)
        }

        // Windows draws the default app's icon as a small badge on every thumbnail in File Explorer unless the app's
        // entry says not to (TypeOverlay). Making FastViewer the default through "Open with > Always" creates that
        // entry without it, so FastViewer adds it to its own entry when it starts (once; nothing else is touched).
        static void HideThumbnailBadge()
        {
            try
            {
                using (var app = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications\FastViewer.exe", true))
                {
                    if (app == null || app.GetValue("TypeOverlay") != null) return;
                    app.SetValue("TypeOverlay", "");
                }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: Explorer re-reads it
            }
            catch { }
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
                        foreach (var ext in ImageExts.Concat(VideoExts).Concat(AudioExts)) types.SetValue(ext, "");
                }
                foreach (var ext in ImageExts.Concat(VideoExts).Concat(AudioExts))
                    using (Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithList\FastViewer.exe")) { }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
                return true;
            }
            catch { return false; }
        }
    }
}
