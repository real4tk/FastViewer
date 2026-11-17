// FastViewer 1.28.0 tests: the English translation (Translate.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        internal readonly List<int> translateLog = new List<int>(); // tests: the order lines got their English

        // --case skiprows [--look X]: Settings > Buttons, Keyboard and Videos scrolled to their skip rows (pictures), after
        // the skip amounts are set to 15 / 45: the icons there show the same numbers as the video bar.
        void HuntSkipRows()
        {
            After(1200, delegate
            {
                if (playing) TogglePlay();
                skipBack = 15; skipForward = 45; UpdateSkipButtons();
                OpenSettings();
                var pages = new[] { "Buttons", "Keyboard", "Videos" };
                int at = 0;
                Action next = null;
                next = delegate
                {
                    if (at == pages.Length) { CloseSettings(); After(500, HuntDone); return; }
                    string page = pages[at++];
                    ShowSettingsPage(settingsPages.First(p => ((System.Windows.Controls.TextBlock)p.Value.Children[0]).Text == page).Value);
                    Settle();
                    var row = FindTextBlock(F<FrameworkElement>("SettingsScroll"), page == "Keyboard" ? "Skip back (length in Videos)" : "Skip back");
                    if (row != null) row.BringIntoView(new Rect(0, -120, 10, 300));
                    After(500, delegate { HuntLog(page + ": skip row found " + (row != null)); Snap("skip_" + page.ToLowerInvariant()); next(); });
                };
                next();
            });
        }

        static System.Windows.Controls.TextBlock FindTextBlock(DependencyObject root, string text)
        {
            if (root == null) return null;
            var t = root as System.Windows.Controls.TextBlock;
            if (t != null && t.Text == text && t.IsVisible) return t;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindTextBlock(VisualTreeHelper.GetChild(root, i), text);
                if (found != null) return found;
            }
            return null;
        }

        // --case pkcaptions --addons <scratch root with junctions to the real Whisper folders> --addon-source <the two Parakeet
        // downloads> [--pattern 0|1|2 = caption engine]: Parakeet installed with its fingerprints checked, then the usual
        // captions test (speech.mp4 in the start folder), whose result names the engine and the language found.
        void HuntParakeetCaptions()
        {
            int engine;
            if (int.TryParse(huntPattern ?? "", out engine)) captionEngine = engine;
            Addon.TrustSource = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (!Parakeet.Addon.Installed) Parakeet.Addon.Install();
            Until(() => !Parakeet.Addon.Busy, 300000, delegate
            {
                HuntLog("parakeet installed " + Parakeet.Addon.Installed + " in " + clock.ElapsedMilliseconds + " ms" + (Parakeet.Addon.Error != null ? ", error " + Parakeet.Addon.Error : "") +
                    "; files: " + string.Join(" ", Directory.GetFiles(Parakeet.Addon.Dir).Select(Path.GetFileName)) + "; engine setting " + captionEngine);
                HuntCaptions();
            });
        }

        // --case lookcheck [--look X]: the subtitle editor (on a video with subtitles and their English) and the Send to Discord
        // menu (drawn inside the window, at 1.5x so both switches show), as pictures; the Burn switch row flips and saves.
        void HuntLookCheck()
        {
            After(1500, delegate
            {
                if (playing) TogglePlay();
                Until(() => HasSubs, 8000, delegate
                {
                    ToggleSubEditor();
                    After(1200, delegate
                    {
                        var pane = F<Border>("SubEditPanel");
                        var bg = pane.Background as SolidColorBrush;
                        HuntLog(Looks.Current.Id + ": editor pane " + (bg == null ? "?" : bg.Color.ToString()) + ", text " + Application.Current.Resources["TextMain"] +
                            ", English box " + subEnBox.Visibility);
                        var item = subList.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
                        if (item != null)
                        {
                            var texts = new List<TextBlock>();
                            Action<DependencyObject> walk = null;
                            walk = d => { if (d is TextBlock) texts.Add((TextBlock)d); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) walk(VisualTreeHelper.GetChild(d, i)); };
                            walk(item);
                            HuntLog("row: item fg " + item.Foreground + " (" + DependencyPropertyHelper.GetValueSource(item, Control.ForegroundProperty).BaseValueSource + "); texts " +
                                string.Join(", ", texts.Select(t => "'" + (t.Text.Length > 8 ? t.Text.Substring(0, 8) : t.Text) + "' " + t.Foreground + " (" + DependencyPropertyHelper.GetValueSource(t, TextBlock.ForegroundProperty).BaseValueSource + ")")));
                        }
                        Snap("editor_" + Looks.Current.Id);
                        ToggleSubEditor();
                        After(800, delegate
                        {
                            SetSpeed(1.5);
                            StartTrim();
                            After(1200, delegate
                            {
                                ContextMenu menu = null;
                                var hook = huntMenuHook;
                                huntMenuHook = (m, b) => menu = m;
                                ShowDiscordMenu();
                                huntMenuHook = hook;
                                var items = menu.Items.Cast<object>().ToList();
                                menu.Items.Clear();
                                Func<object, string> name = o => o is Separator ? "---" : o is MenuItem ? (((MenuItem)o).Header is string ? (string)((MenuItem)o).Header : "[" + System.Windows.Automation.AutomationProperties.GetName((MenuItem)o) + "]") : "?";
                                HuntLog("discord menu: " + string.Join(" | ", items.Select(name)));
                                var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                                    "<ControlTemplate TargetType='Menu' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                                    "<Border Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuEdge}' BorderThickness='1' " +
                                    "CornerRadius='{DynamicResource Radius.Float}' Padding='4' MinWidth='200'><StackPanel IsItemsHost='True'/></Border></ControlTemplate>");
                                var copy = new Menu { Template = template, FontSize = 14 };
                                copy.SetResourceReference(Control.ForegroundProperty, "TextMain");
                                copy.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
                                foreach (var it in items) copy.Items.Add(it);
                                var host = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = copy, Margin = new Thickness(0, 0, 40, 120) };
                                var root = (Grid)Content;
                                Grid.SetRowSpan(host, 9); Grid.SetColumnSpan(host, 9); Panel.SetZIndex(host, 100);
                                root.Children.Add(host);
                                host.UpdateLayout(); Settle();
                                Snap("discord_" + Looks.Current.Id);
                                var burn = items.OfType<MenuItem>().First(m => System.Windows.Automation.AutomationProperties.GetName(m).StartsWith("Burn in captions"));
                                bool before = discordBurn;
                                burn.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                var sw = ((Grid)burn.Header).Children.OfType<CheckBox>().First();
                                HuntLog("burn row clicked: " + before + " -> " + discordBurn + ", switch " + sw.IsChecked + ", saved line " + File.ReadAllLines(SettingsFile).Any(l => l == "discordBurn=" + (discordBurn ? "1" : "0")) +
                                    (discordBurn != before && sw.IsChecked == discordBurn ? " ok" : " FAIL"));
                                root.Children.Remove(host);
                                SetSpeed(1);
                                After(500, HuntDone);
                            });
                        });
                    });
                });
            });
        }

        // --case inforows: the Info pane of the start video, then of the next two files (frame rate and video format under
        // Dimensions, filled in from a pool thread), with a picture of the first.
        void HuntInfoRows()
        {
            Action<string> log = delegate(string what)
            {
                var rows = infoList.Children.OfType<System.Windows.Controls.StackPanel>().Select(p => string.Join(": ",
                    p.Children.OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text))).ToList();
                HuntLog(what + " " + Path.GetFileName(CurrentFile) + " -> " + string.Join(" | ", rows));
            };
            After(1500, delegate
            {
                ToggleInfo();
                After(1500, delegate
                {
                    log("first:"); Snap("info");
                    Go(1);
                    After(1500, delegate { log("next:"); Go(1); After(1500, delegate { log("next:"); ToggleInfo(); After(600, HuntDone); }); });
                });
            });
        }

        // --case translate --addons <scratch root> --addon-source <folder with the three downloads> [--translate-on gpu|cpu]
        // Start file: a video with Russian subtitles beside it (a few minutes). The add-on is installed from the local copies
        // with its fingerprints checked; then translation starts a minute in: the lines from there come first, each shows
        // with its English while the rest is still going, both languages can be switched off, the English is kept and comes
        // back when the video is opened again, is drawn into a burned-in copy, and shows in the subtitle editor.
        void HuntTranslate()
        {
            Addon.TrustSource = false;
            var a = Translator.Addon;
            a.Install();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Until(() => !a.Busy && HasSubs, 600000, delegate
            {
                HuntLog("installed " + a.Installed + " in " + clock.ElapsedMilliseconds + " ms" + (a.Error != null ? ", error " + a.Error : "") + (a.Installed ? "" : " FAIL"));
                lock (Addon.Log) foreach (var l in Addon.Log) HuntLog("  " + l);
                if (!a.Installed) { HuntDone(); return; }
                HuntLog("subtitles: " + cues.Count + " lines, English before " + cues.Count(c => !string.IsNullOrEmpty(c.En)));
                if (playing) TogglePlay();
                shown.Video.Position = TimeSpan.FromSeconds(60);
                After(500, delegate
                {
                    int here = cues.FindIndex(c => c.To > 60);
                    clock.Restart();
                    TranslateSubs();
                    bool sawLive = false;
                    Until(() =>
                    {
                        if (!sawLive && translating && cues.Count(c => !string.IsNullOrEmpty(c.En)) >= 3) { sawLive = true; HuntLog("while translating: " + cues.Count(c => !string.IsNullOrEmpty(c.En)) + " lines already have English; message '" + F<System.Windows.Controls.TextBlock>("ToastText").Text + "'"); }
                        return !translating;
                    }, 900000, delegate
                    {
                        HuntLog("result: " + lastTranslateResult + " (" + clock.ElapsedMilliseconds + " ms)");
                        var first = cues.Select((c, i) => i).Where(i => !string.IsNullOrEmpty(cues[i].En)).ToList();
                        HuntLog("first lines done: " + string.Join(", ", translateLog.Take(6)) + (translateLog.Take(4).All(i => i >= here && i < here + 12) ? " (from the line watched) ok" : " FAIL"));
                        HuntLog("line at 1:00 is #" + here + "; English for " + first.Count + " of " + cues.Count + (first.Count == cues.Count ? "" : " FAIL"));
                        // On screen: a line with its English
                        int pick = Math.Min(cues.Count - 1, here + 2);
                        shown.Video.Position = TimeSpan.FromSeconds(cues[pick].From + 0.2);
                        After(400, delegate
                        {
                            cueShown = -2; SubTick();
                            After(400, delegate
                            {
                                HuntLog("on screen: '" + subText.Text + "' (" + subText.Visibility + ") / '" + subTextEn.Text + "' (" + subTextEn.Visibility + ")" +
                                    (subText.Visibility == Visibility.Visible && subTextEn.Visibility == Visibility.Visible && subTextEn.Text == cues[pick].En ? "" : " FAIL"));
                                Snap("two_lines");
                                ToggleOriginal(); Settle();
                                HuntLog("original off: original " + subText.Visibility + ", English " + subTextEn.Visibility + (subText.Visibility != Visibility.Visible && subTextEn.Visibility == Visibility.Visible ? "" : " FAIL"));
                                Snap("english_only");
                                ToggleOriginal(); ToggleEnglish(); Settle();
                                HuntLog("English off: original " + subText.Visibility + ", English " + subTextEn.Visibility + (subText.Visibility == Visibility.Visible && subTextEn.Visibility != Visibility.Visible ? "" : " FAIL"));
                                ToggleEnglish(); Settle();
                                // Burned in: one picture of the copy at that line
                                var painter = MakePainter(shown.File, null);
                                var px = new int[1280 * 720];
                                for (int k = 0; k < px.Length; k++) px[k] = unchecked((int)0xFF304050);
                                painter.Paint(px, 1280, 720, (long)((cues[pick].From + 0.2) * 1e7));
                                var bmp = BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Bgra32, null, px, 1280 * 4);
                                using (var f = File.Create(Path.Combine(snapDir, "burned.png"))) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); enc.Save(f); }
                                HuntLog("burned in: lines drawn " + painter.Lines + (painter.Lines == 1 ? "" : " FAIL") + " (burned.png)");
                                // Kept: opened again, the English is there
                                string kept = Translator.FileFor(cuesFor, cuesFrom);
                                HuntLog("kept in " + (kept == null ? "nothing" : Path.GetFileName(kept)) + ", exists " + (kept != null && File.Exists(kept)));
                                FindSubtitles();
                                Until(() => HasSubs, 10000, delegate
                                {
                                    int back = cues.Count(c => !string.IsNullOrEmpty(c.En));
                                    HuntLog("opened again: English for " + back + " of " + cues.Count + (back == cues.Count ? "" : " FAIL"));
                                    // The editor shows it and changes it
                                    ToggleSubEditor();
                                    After(1200, delegate
                                    {
                                        HuntLog("editor: English box " + subEnBox.Visibility + ", text '" + subEnBox.Text + "'" + (subEnBox.Visibility == Visibility.Visible && subEnBox.Text.Length > 0 ? "" : " FAIL"));
                                        Snap("editor");
                                        ToggleSubEditor();
                                        After(800, HuntDone);
                                    });
                                });
                            });
                        });
                    });
                });
            });
        }
    }
}
