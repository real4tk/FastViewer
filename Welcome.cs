// FastViewer 1.27.0: the welcome screen. What an empty window shows (started without a file, or the folder became
// empty): Open a file (drag and drop works too), Settings, "Make FastViewer my default viewer" while it isn't, and
// the files opened most recently. Built only when that screen shows, so opening a file the normal way never pays for it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        // %APPDATA%\FastViewer\recent.txt: files and folders opened, newest first (test runs: memory only unless --recent).
        static string RecentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastViewer", "recent.txt");
        static bool recentOverride, testRunStatic;
        static readonly object recentGate = new object();
        static List<string> recentMemory = new List<string>();
        const int RecentKept = 20, RecentShown = 5;
        int welcomeToken;
        internal static bool? defaultOverride; // tests: --default yes|no

        bool WelcomeShowing { get { return files.Count == 0 && !mini && message.Text == EmptyMessage(); } }

        // The empty window, also after the last file of the folder was deleted (the folder is still watched, so a file
        // that appears there shows).
        void ShowWelcome()
        {
            // Every player lets go (the files are gone, and one may still show the last picture), as LetGoOfFiles does.
            StopRevealWatch();
            gifTimer.Stop();
            foreach (var s in slots)
            {
                s.Token++;
                s.Video.Stop();
                s.Video.Close();
                s.Video.Source = null;
                s.Picture.Source = null;
                s.Poster.Source = null;
                s.Gif = null;
                s.File = null;
                s.Ready = false;
                Panel.SetZIndex(s.Host, 0);
            }
            EndOwnSound();
            shown = wanted = null;
            playing = false;
            ClearStrip();
            titleText.Text = settingsOpen ? "Settings" : "";
            Title = "FastViewer";
            message.Text = EmptyMessage();
            F<FrameworkElement>("MessageBtn").Visibility = Visibility.Collapsed;
            F<FrameworkElement>("MessageFixBtn").Visibility = Visibility.Collapsed;
            UpdateControls();
            UpdateFacts();
            UpdateZoomUi();
        }

        // Called from ArrangeMessage: shows or hides the welcome parts.
        void ArrangeWelcome(bool welcome)
        {
            var extras = F<FrameworkElement>("WelcomeExtras");
            F<FrameworkElement>("WelcomeSettingsBtn").Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
            extras.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
            WelcomeChrome(welcome);
            if (!welcome) { welcomeToken++; F<StackPanel>("WelcomeRecent").Children.Clear(); return; }
            FillWelcome();
        }

        // No file: the top bar's buttons for a file (Delete, Copy, Rotate, mini player, Open with) step out, as on the
        // Settings screen.
        bool fileButtonsHidden;
        static readonly string[] FileButtons = { "DeleteBtn", "CopyBtn", "RotateBtn", "MiniBtn", "OpenWithBtn" };
        void WelcomeChrome(bool welcome)
        {
            if (root == null || welcome == fileButtonsHidden) return; // (only when it changes: not on every switch)
            fileButtonsHidden = welcome;
            UpdateResponsive(); // collapses them, so "..." sits next to Open; with a file they come back as Settings > Buttons says
        }

        void InitWelcome()
        {
            OnClick("WelcomeSettingsBtn", OpenSettings);
            OnClick("WelcomeDefaultBtn", MakeDefaultViewer);
            OnClick("WelcomeClearBtn", ClearRecent);
            Activated += delegate { if (WelcomeShowing) FillWelcome(); }; // back from Windows' Default apps, or files opened elsewhere
        }

        // Recent files and the default-viewer check are looked up off the window's thread (a file on a network drive
        // that is gone can take seconds to answer), then put on screen.
        void FillWelcome()
        {
            int token = ++welcomeToken;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<string>();
                foreach (var p in ReadRecent())
                {
                    if (found.Count >= RecentShown) break;
                    try { if (File.Exists(p) || Directory.Exists(p)) found.Add(p); } catch { }
                }
                bool isDefault = IsDefaultViewer();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (token != welcomeToken || !WelcomeShowing) return;
                    F<FrameworkElement>("WelcomeDefaultBtn").Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
                    ShowRecent(found);
                    if (snapDir != null) testLog.Add("welcome: default button " + !isDefault + ", recent " + found.Count + (found.Count > 0 ? " (" + string.Join(", ", found.Select(Path.GetFileName)) + ")" : ""));
                }));
            });
        }

        void ShowRecent(List<string> found)
        {
            var list = F<StackPanel>("WelcomeRecent");
            var head = F<FrameworkElement>("WelcomeRecentHead");
            var have = list.Children.OfType<Button>().Select(b => b.Tag as string).ToList();
            head.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (have.SequenceEqual(found, StringComparer.OrdinalIgnoreCase)) return; // (Activated: nothing changed)
            list.Children.Clear();
            foreach (var p in found) list.Children.Add(RecentRow(p));
        }

        Button RecentRow(string path)
        {
            bool folder = Directory.Exists(path);
            string name = folder ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            string where = folder ? path : Path.GetDirectoryName(path);

            var picture = new Image { Width = 40, Height = 40, Stretch = Stretch.UniformToFill };
            var frame = new Border { Width = 40, Height = 40, ClipToBounds = true, Child = picture, VerticalAlignment = VerticalAlignment.Center };
            frame.SetResourceReference(Border.BackgroundProperty, "ControlFill");
            frame.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
            frame.Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 6, 6); // the picture's corners round off like its frame
            var glyph = new Glyph { Code = folder ? "" : IsAudioFile(path) ? "" : IsVideoFile(path) ? "" : "", Size = 18 };
            glyph.SetResourceReference(Glyph.ForegroundProperty, "TextSecondary");
            var holder = new Grid();
            holder.Children.Add(frame);
            holder.Children.Add(glyph);

            var title = new TextBlock { Text = name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            var sub = new TextBlock { Text = where, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(title);
            words.Children.Add(sub);
            var row = new DockPanel();
            DockPanel.SetDock(holder, Dock.Left);
            row.Children.Add(holder);
            row.Children.Add(words);

            var b = new Button { Style = (Style)Application.Current.Resources["Subtle"], Height = 56, Padding = new Thickness(8, 0, 8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = row, Tag = path, ToolTip = path };
            System.Windows.Automation.AutomationProperties.SetName(b, "Open " + name);
            b.Click += delegate { OpenPath(path); };
            b.MouseRightButtonUp += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) { e.Handled = true; RecentMenu(b, path); }; // Remove from Recent, Clear recent

            if (!folder && !string.IsNullOrEmpty(path))
            {
                int px = (int)Math.Ceiling(40 * Dpi());
                BitmapSource image;
                if (Thumbs.TryGet(path, px, out image)) { picture.Source = image; glyph.Visibility = Visibility.Collapsed; }
                else Thumbs.Request(path, px, Dispatcher, delegate(BitmapSource got)
                {
                    if (got == null) return;
                    picture.Source = got;
                    glyph.Visibility = Visibility.Collapsed;
                    if (Motion.Animates) Motion.FromTo(picture, OpacityProperty, 0, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth);
                });
            }
            return b;
        }

        // Right-click on a recent file: take it off the list, or empty the list (the files themselves stay where they are).
        void RecentMenu(Button row, string path)
        {
            var menu = new ContextMenu { PlacementTarget = row, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            menu.Items.Add(Item("Remove from Recent", "\uE711", delegate { ForgetRecent(path); }));
            menu.Items.Add(Item("Clear recent", "\uE74D", ClearRecent));
            if (huntMenuHook != null) { huntMenuHook(menu, row); return; } // tests: never a real menu
            OpenMenu(menu, row);
        }

        void ForgetRecent(string path)
        {
            ChangeRecent(list => { list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); return list; });
        }

        void ClearRecent()
        {
            ChangeRecent(list => new List<string>());
        }

        // Reads, changes and writes the list off the window's thread, then shows it again.
        void ChangeRecent(Func<List<string>, List<string>> change)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (recentGate) WriteRecent(change(ReadRecent()));
                Dispatcher.BeginInvoke(new Action(delegate { if (WelcomeShowing) FillWelcome(); }));
            });
        }

        static void WriteRecent(List<string> list)
        {
            if (testRunStatic && !recentOverride) { recentMemory = list; return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecentPath));
                string temp = RecentPath + ".tmp";
                File.WriteAllLines(temp, list, new UTF8Encoding(false));
                if (File.Exists(RecentPath)) File.Replace(temp, RecentPath, null); else File.Move(temp, RecentPath);
            }
            catch { }
        }

        // ---------------------------------------------------------------- the list itself

        static List<string> ReadRecent()
        {
            lock (recentGate)
            {
                if (testRunStatic && !recentOverride) return new List<string>(recentMemory);
                try { return File.Exists(RecentPath) ? File.ReadAllLines(RecentPath, Encoding.UTF8).Where(l => l.Length > 0).ToList() : new List<string>(); }
                catch { return new List<string>(); }
            }
        }

        // A file or folder you opened goes to the top of the list. Written off the window's thread a moment later, so
        // opening isn't slowed down; several windows share the file (read, change, write under one lock per process).
        static void RememberRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1500);
                lock (recentGate)
                {
                    var list = ReadRecent();
                    list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    list.Insert(0, path);
                    if (list.Count > RecentKept) list.RemoveRange(RecentKept, list.Count - RecentKept);
                    WriteRecent(list);
                }
            });
        }

        // ---------------------------------------------------------------- default viewer

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder output, ref int size);

        // FastViewer counts as the default viewer when Windows opens photos (.jpg, .png) and videos (.mp4) with it.
        static bool IsDefaultViewer()
        {
            if (defaultOverride != null) return defaultOverride.Value;
            string me = System.Reflection.Assembly.GetExecutingAssembly().Location;
            foreach (var ext in new[] { ".jpg", ".png", ".mp4" })
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, ext, "open", sb, ref size) != 0) return false;
                    if (!string.Equals(Path.GetFullPath(sb.ToString()), me, StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        // Windows only lets you choose the default yourself: FastViewer adds itself to the lists (current user only)
        // and opens Windows' Default apps, where Photo viewer and Video player can be set to FastViewer.
        void MakeDefaultViewer()
        {
            if (testRun)
            {
                testLog.Add("make default: would add FastViewer to Default apps and open ms-settings:defaultapps");
                ShowToast(DefaultAppsHint, null, null, 8, null);
                return;
            }
            bool added = RegisterOpenWith();
            try { System.Diagnostics.Process.Start("ms-settings:defaultapps"); } catch { }
            ShowToast(added ? DefaultAppsHint : "Couldn't add FastViewer to Windows' lists. Try Settings > Default app.", null, null, 10, added ? null : "error");
        }

        const string DefaultAppsHint = "In Windows' Default apps, click Photo viewer and Video player and pick FastViewer.";
    }
}
