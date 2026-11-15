// FastViewer 1.27.0 tests: --hunt <out> --case settingsplay | welcome | deletenext | skipnumbers (see FEATURES.md).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        double HuntPos() { return shown != null && shown.IsVideo ? shown.Video.Position.TotalSeconds : -1; }

        // Settings must not stop what plays: open it, change settings that reload the folder, the preload and the look,
        // close it; the video (or song) plays on all the time and never goes back. A paused one stays paused.
        void HuntSettingsPlay()
        {
            string start = null;
            double pos = 0;
            Func<string, string> step = what =>
            {
                double now = HuntPos();
                bool same = shown != null && string.Equals(shown.File, start, StringComparison.OrdinalIgnoreCase);
                bool ok = same && playing && now > pos + 0.5;
                string line = what + ": " + (shown == null ? "nothing" : Path.GetFileName(shown.File)) + ", playing " + playing +
                    ", at " + now.ToString("0.00") + " s (was " + pos.ToString("0.00") + ")" + (ok ? "" : " FAIL");
                pos = now;
                return line;
            };
            Until(() => shown != null && shown.Ready, 15000, delegate
            {
                start = shown.File;
                if (!playing) TogglePlay();
                Until(() => HuntPos() > 1, 15000, delegate
                {
                    pos = HuntPos();
                    OpenSettings();
                    After(1200, delegate
                    {
                        HuntLog(step("Settings open"));
                        filter = 3; ReloadFolder(); // Show: only videos (the file on screen stays)
                        After(1200, delegate
                        {
                            HuntLog(step("Show changed"));
                            order = 2; ReloadFolder();
                            After(1200, delegate
                            {
                                HuntLog(step("Order changed"));
                                preloadOverride = 0; SetPreload(1);
                                After(1200, delegate
                                {
                                    HuntLog(step("Preload 1"));
                                    SetPreload(3);
                                    lookId = "Glass"; ApplyLook();
                                    After(1200, delegate
                                    {
                                        HuntLog(step("Preload 3 + Liquid Glass"));
                                        lookId = "Midnight"; lookMode = "Light"; ApplyLook();
                                        smallActualSize = true; ApplyStretch();
                                        After(1200, delegate
                                        {
                                            HuntLog(step("Midnight light + actual size"));
                                            CloseSettings();
                                            After(1200, delegate
                                            {
                                                HuntLog(step("Settings closed"));
                                                TogglePlay(); // paused by hand: stays paused through Settings
                                                double paused = HuntPos();
                                                OpenSettings();
                                                After(1000, delegate
                                                {
                                                    CloseSettings();
                                                    After(600, delegate
                                                    {
                                                        double now = HuntPos();
                                                        HuntLog("paused, Settings opened and closed: playing " + playing + ", at " + now.ToString("0.00") + " (paused at " + paused.ToString("0.00") + ")" +
                                                            (!playing && Math.Abs(now - paused) < 0.1 ? "" : " FAIL"));
                                                        HuntDone();
                                                    });
                                                });
                                            });
                                        });
                                    });
                                });
                            });
                        });
                    });
                });
            });
        }

        // --case skipnumbers: one sheet (skipnumbers_<dark|light>.png) with the skip buttons of every look at every amount,
        // 6x, and a row of menu icons per look: Copy, Rotate right, Rotate left (new), the old rotate, compatible copy.
        void HuntSkipNumbers()
        {
            After(1500, delegate
            {
                if (playing) TogglePlay();
                ShowOverlay();
                foreach (var mode in new[] { "Dark", "Light" })
                {
                    var looks = new[] { "Photos", "Clean", "Glass", "Midnight" };
                    var amounts = new[] { 5, 10, 15, 30, 60 };
                    const int z = 6;
                    var cells = new List<Tuple<int, int, BitmapSource>>();
                    int cellW = 0, cellH = 0, iconW = 0;
                    for (int li = 0; li < looks.Length; li++)
                    {
                        lookId = looks[li]; lookMode = mode; ApplyLook(); ShowOverlay(); Settle();
                        for (int ai = 0; ai < amounts.Length; ai++)
                        {
                            skipBack = skipForward = amounts[ai]; UpdateSkipButtons(); ShowOverlay(); UpdateLayout(); Settle();
                            var back = F<FrameworkElement>("BackBtn");
                            var bar = (FrameworkElement)VisualTreeHelper.GetParent(back);
                            var a = back.TranslatePoint(new Point(0, 0), bar);
                            var fwd = F<FrameworkElement>("FwdBtn");
                            double w = fwd.TranslatePoint(new Point(fwd.ActualWidth, 0), bar).X - a.X, h = back.ActualHeight;
                            cells.Add(Tuple.Create(li, ai, Cell(bar, new Rect(a.X, a.Y, w, h), z)));
                            cellW = Math.Max(cellW, (int)(w * z)); cellH = Math.Max(cellH, (int)(h * z));
                        }
                        var icons = new StackPanel { Orientation = Orientation.Horizontal };
                        icons.SetResourceReference(Panel.BackgroundProperty, "MenuBg");
                        foreach (var code in new[] { "", Glyph.RotateLeft + "R", Glyph.RotateLeft, "", "" })
                        {
                            var g = new Glyph { Code = code.Substring(0, 1), Size = 16, Margin = new Thickness(6) };
                            if (code.Length > 1) g.LayoutTransform = new ScaleTransform(-1, 1);
                            g.SetResourceReference(Glyph.ForegroundProperty, "TextMain");
                            icons.Children.Add(g);
                        }
                        icons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        icons.Arrange(new Rect(icons.DesiredSize));
                        icons.UpdateLayout();
                        cells.Add(Tuple.Create(li, amounts.Length, Cell(icons, new Rect(icons.DesiredSize), z)));
                        iconW = Math.Max(iconW, (int)(icons.DesiredSize.Width * z));
                    }
                    var dv = new DrawingVisual();
                    int cols = amounts.Length + 1, gap = 12;
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, cols * (cellW + gap) + iconW, looks.Length * (cellH + gap)));
                        foreach (var c in cells)
                            dc.DrawImage(c.Item3, new Rect(c.Item2 * (cellW + gap), c.Item1 * (cellH + gap), c.Item3.PixelWidth, c.Item3.PixelHeight));
                    }
                    var rtb = new RenderTargetBitmap(cols * (cellW + gap) + iconW, looks.Length * (cellH + gap), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var f = File.Create(Path.Combine(snapDir, "skipnumbers_" + mode.ToLowerInvariant() + ".png"))) enc.Save(f);
                }
                var n = F<SkipNumber>("BackText");
                var o = n.Outline();
                HuntLog("number '" + n.Text + "' box " + o.Bounds + " (centre " + (o.Bounds.Left + o.Bounds.Width / 2).ToString("0.00") + ", " + (o.Bounds.Top + o.Bounds.Height / 2).ToString("0.00") +
                    "), element " + n.ActualWidth + "x" + n.ActualHeight + ", font " + n.FontFamily);
                HuntDone();
            });
        }

        BitmapSource Cell(Visual v, Rect area, int z)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("FloatBg"), null, new Rect(0, 0, area.Width, area.Height));
                dc.DrawRectangle(new VisualBrush(v) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, new Rect(0, 0, area.Width, area.Height));
            }
            var rtb = new RenderTargetBitmap((int)(area.Width * z), (int)(area.Height * z), 96 * z, 96 * z, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // --case subedit (scratch folder: clip.mp4 + clip.srt with 5 lines, plain.mp4 without): every edit of the subtitle
        // editor, what shows over the video, Save as, Save over the original (only on the second click), new subtitles.
        void HuntSubEdit()
        {
            string original = null, before = null;
            Func<string> state = () => cues == null ? "no subtitles" : cues.Count + " lines: " + string.Join(" | ", cues.Select(c => c.From.ToString("0.0") + "-" + c.To.ToString("0.0") + " " + c.Text.Replace("\n", "/"))) + (subDirty ? " (changed)" : "");
            Action<int> pick = i => { subList.SelectedIndex = i; UpdateSubEditor(); };
            HuntOpen("clip.mp4", delegate
            {
                Until(() => HasSubs, 5000, delegate
                {
                    original = cuesFrom; before = File.ReadAllText(original);
                    if (playing) TogglePlay();
                    ToggleSubEditor();
                    Settle();
                    HuntLog("opened: " + state() + "; pane " + F<FrameworkElement>("SubEditPanel").Visibility + ", column " + infoCol.Width.Value + ", source '" + subSourceText.Text + "', save '" + subSaveBtn.Content + "' enabled " + subSaveBtn.IsEnabled);
                    Snap("subedit_open");
                    pick(1); subTextBox.Text = "Changed text"; HuntLog("text: " + state());
                    SeekTo(cues[1].From + 0.2); Settle(); SubTick(); HuntLog("on screen at " + Position().ToString("0.0") + " s: '" + subText.Text + "' (shown " + (subBox.Visibility == Visibility.Visible) + ")");
                    SubNudge(true, 0.1); SubNudge(true, 0.1); SubNudge(true, 0.1); SubNudge(false, -0.1); HuntLog("start +0.3, end -0.1: " + state());
                    SubShiftAll(0.5); HuntLog("all +0.5: " + state() + ", shown as '" + subShiftText.Text + "'");
                    pick(3); SubDelete(); HuntLog("deleted line 4: " + state());
                    pick(0); SubMerge(); HuntLog("merged 1 and 2: " + state());
                    SeekTo(12); Settle(); SubAdd(); subTextBox.Text = "Added here"; HuntLog("added at 12 s: " + state() + ", chosen " + SubSel);
                    SubUndo(); HuntLog("undo: " + state());
                    SubUndo(); HuntLog("undo again: " + state());
                    Snap("subedit_edited");
                    SubSaveTarget = Path.Combine(snapDir, "saved.srt");
                    SaveSubsAs();
                    HuntLog("saved as: " + File.ReadAllText(SubSaveTarget).Replace("\r\n", "|") + "; now from " + Path.GetFileName(cuesFrom) + ", changed " + subDirty);
                    HuntLog("original untouched: " + (File.ReadAllText(original) == before) + (File.ReadAllText(original) == before ? "" : " FAIL"));
                    cuesFrom = original; pick(0); subTextBox.Text = "Over the original";
                    SaveSubs(); HuntLog("Save clicked once: button '" + subSaveBtn.Content + "', original untouched " + (File.ReadAllText(original) == before) + (File.ReadAllText(original) == before ? "" : " FAIL"));
                    SaveSubs(); HuntLog("Save clicked again: original now " + File.ReadAllText(original).Replace("\r\n", "|"));
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    OnKey(this, key); HuntLog("Right key while editing: still on " + Path.GetFileName(shown.File));
                    CloseSubEditor(true); Settle();
                    HuntLog("closed: pane " + F<FrameworkElement>("SubEditPanel").Visibility + ", column " + infoCol.Width.Value);
                    HuntOpen("plain.mp4", delegate
                    {
                        if (playing) TogglePlay();
                        ToggleSubEditor(); Settle();
                        HuntLog("plain.mp4: " + state() + ", source '" + subSourceText.Text + "', save enabled " + subSaveBtn.IsEnabled);
                        SeekTo(3); Settle(); SubAdd(); subTextBox.Text = "First line"; SeekTo(8); Settle(); SubAdd(); subTextBox.Text = "Second line";
                        SubSaveTarget = Path.Combine(snapDir, "plain new.srt");
                        SaveSubs();
                        HuntLog("new subtitles saved: " + (File.Exists(SubSaveTarget) ? File.ReadAllText(SubSaveTarget).Replace("\r\n", "|") : "nothing FAIL"));
                        Snap("subedit_new");
                        CloseSubEditor(true);
                        HuntDone();
                    });
                });
            });
        }

        // --case customcard (start on a video, --settings scratch with look=Custom): Settings > Appearance first (its look
        // cards are what crashed), then every control of the Custom look page, waiting after each so the half-second
        // refresh (RefreshCustomCard) runs: every choice of every dropdown, the roundness slider, the name, a
        // see-through colour for each colour group and each single part, Show all colours, Start over.
        void HuntCustomCard()
        {
            Func<string, StackPanel> pageOf = n => settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == n).Value;
            var steps = new List<Tuple<string, Action>>();
            int done = 0;
            Action next = null;
            next = () =>
            {
                if (done >= steps.Count)
                {
                    var holder = lookCards == null ? null : PreviewHolder(lookCards.Children.OfType<Button>().FirstOrDefault(c => c.Tag as string == CustomId));
                    string old;
                    try
                    {
                        var card = lookCards.Children.OfType<Button>().First(c => c.Tag as string == CustomId);
                        var stack = (StackPanel)card.Content;
                        var unused = ((Border)((Border)stack.Children[0]).Child); // 1.17.1-1.26.0 RefreshCustomCard
                        old = "works";
                    }
                    catch (InvalidCastException ex) { old = "InvalidCastException (" + ex.Message.Split('.')[0] + ")"; }
                    HuntLog("1.26.0's RefreshCustomCard cast on this card: " + old);
                    HuntLog("all " + steps.Count + " steps done; still running, video playing " + playing + ", custom card preview " + (holder != null && holder.Child != null) + ", custom values " + custom.Count);
                    Snap("customcard_end");
                    HuntDone();
                    return;
                }
                var s = steps[done++];
                s.Item2();
                After(650, delegate { testLog.Add(s.Item1); next(); }); // (the refresh runs 500 ms after a change)
            };
            Until(() => shown != null && shown.Ready, 15000, delegate
            {
                if (!playing) TogglePlay();
                lookId = CustomId; ApplyLook();
                OpenSettings();
                ShowSettingsPage(pageOf("Appearance")); Settle();
                HuntLog("Appearance page: look cards " + (lookCards == null ? 0 : lookCards.Children.Count));
                ShowSettingsPage(pageOf("Custom look")); Settle();
                var page = pageOf("Custom look");
                // Every dropdown: its menu is handed to the hook instead of popping up; each item is clicked in turn.
                var drops = FindAll<Button>(page).Where(b => FindAll<Glyph>(b).Any(g => g.Code == "")).ToList();
                foreach (var d in drops)
                {
                    var drop = d;
                    var labels = new List<string>();
                    ContextMenu menu = null;
                    huntMenuHook = (m, b) => { menu = m; };
                    drop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    huntMenuHook = null;
                    if (menu == null) continue;
                    int count = menu.Items.OfType<MenuItem>().Count();
                    for (int i = 0; i < count; i++)
                    {
                        int k = i;
                        steps.Add(Tuple.Create("dropdown item " + k, (Action)delegate
                        {
                            ContextMenu m2 = null;
                            huntMenuHook = (m, b) => { m2 = m; };
                            drop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            huntMenuHook = null;
                            var item = m2 == null ? null : m2.Items.OfType<MenuItem>().ElementAtOrDefault(k);
                            if (item != null) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        }));
                    }
                }
                var round = FindAll<Slider>(page).FirstOrDefault();
                foreach (var v in new[] { 0.0, 24, 7 }) { double val = v; steps.Add(Tuple.Create("roundness " + v, (Action)delegate { if (round != null) round.Value = val; })); }
                var name = FindAll<TextBox>(page).FirstOrDefault();
                steps.Add(Tuple.Create("name", (Action)delegate { if (name != null) name.Text = "Test look"; }));
                foreach (var g in ColorGroups)
                {
                    var parts = g.Skip(2).ToArray();
                    foreach (var light in new[] { false, true })
                        foreach (byte alpha in new byte[] { 0x40, 0xFF })
                        {
                            bool l = light; byte a = alpha;
                            steps.Add(Tuple.Create(g[0] + (l ? " light" : " dark") + " alpha " + a, (Action)delegate { SetCustomColors(parts, Color.FromArgb(a, 0x30, 0x60, 0x90), l, false); SetCustomColors(parts, Color.FromArgb(a, 0x20, 0x40, 0x80), l, true); }));
                        }
                }
                var showAll = FindAll<Button>(page).FirstOrDefault(b => b.Content as string == "Show all colours");
                steps.Add(Tuple.Create("show all colours", (Action)delegate { if (showAll != null) showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }));
                foreach (var p in ColorNames.Keys.ToList())
                {
                    string part = p;
                    steps.Add(Tuple.Create("part " + part, (Action)delegate { SetCustomColors(new[] { part }, Color.FromArgb(0x80, 0x90, 0x30, 0x30), Looks.Light, true); }));
                }
                var over = FindAll<Button>(page).FirstOrDefault(b => b.Content as string == "Start over");
                steps.Add(Tuple.Create("start over", (Action)delegate { if (over != null) over.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }));
                HuntLog("Custom look page: " + drops.Count + " dropdowns, slider " + (round != null) + ", name box " + (name != null) + ", " + ColorGroups.Length + " colour groups, " + ColorNames.Count + " parts: " + steps.Count + " steps");
                next();
            });
        }

        // --case settingsclear (start on a video; --settings with look=Custom and any customBase): the background made
        // 25% see-through (as with the colour picker's See-through bar), Settings open over the playing video.
        void HuntSettingsClear()
        {
            Until(() => shown != null && shown.Ready, 15000, delegate
            {
                if (!playing) TogglePlay();
                lookId = CustomId; ApplyLook();
                Func<string> state = () => "page background " + F<Panel>("SettingsPage").Background + ", viewer opacity " + F<FrameworkElement>("ViewArea").Opacity.ToString("0.##") + ", playing " + playing;
                OpenSettings(); Settle();
                HuntLog("not see-through: " + state()); Snap("settings_solid");
                CloseSettings();
                foreach (var g in ColorGroups.Where(x => x[0] == "Background"))
                    SetCustomColors(g.Skip(2).ToArray(), Color.FromArgb(0x40, 0x10, 0x10, 0x14), Looks.Light, true);
                OpenSettings();
                After(800, delegate
                {
                    HuntLog("background 25% covered: " + state()); Snap("settings_seethrough");
                    CloseSettings(); HuntDone();
                });
            });
        }

        // --case handover (start on a video): what the background copy does when it is handed "" (FastViewer.exe started
        // without a file: the welcome screen, nothing plays) and null (the icon by the clock: the last file again).
        void HuntHandOver()
        {
            Until(() => shown != null && shown.Ready, 15000, delegate
            {
                string last = shown.File;
                LetGoOfFiles(); // as when it went to the background
                lastOpened = last;
                OpenFromOutside("");
                After(1500, delegate
                {
                    HuntLog("started on its own: welcome " + WelcomeShowing + ", on screen " + (shown == null ? "nothing" : Path.GetFileName(shown.File)) + ", playing " + playing +
                        (WelcomeShowing && shown == null && !playing ? "" : " FAIL"));
                    OpenFromOutside(null);
                    Until(() => shown != null && shown.Ready, 10000, delegate
                    {
                        HuntLog("icon by the clock: on screen " + Path.GetFileName(shown.File) + " (the last file " + Path.GetFileName(last) + ")" + (shown.File == last ? "" : " FAIL"));
                        HuntDone();
                    });
                });
            });
        }

        // --case subeditfit (clip.mp4 + clip.srt): the subtitle editor in a Custom look with floating bars, Clean and Photos, at
        // four window sizes: every button, box and the list in the pane must get a click at its middle (after scrolling the
        // pane to it), nothing may stick out of the pane, and the window's own buttons must still get theirs.
        void HuntSubEditFit()
        {
            var cases = new List<Tuple<string, string, Size>>();
            foreach (var look in new[] { "floating", "Clean", "Photos" })
                foreach (var size in new[] { new Size(1440, 760), new Size(900, 600), new Size(640, 480), new Size(520, 420) })
                    cases.Add(Tuple.Create(look, look, size));
            int at = 0;
            Action next = null;
            next = () =>
            {
                if (at >= cases.Count) { CloseSubEditor(false); HuntDone(); return; }
                var c = cases[at++];
                CloseSubEditor(false);
                if (c.Item1 == "floating") { lookId = CustomId; custom["bars"] = "floating"; BuildCustomLook(); }
                else lookId = c.Item1;
                ApplyLook();
                WindowState = WindowState.Normal;
                Width = c.Item3.Width; Height = c.Item3.Height;
                After(400, delegate
                {
                    ToggleSubEditor();
                    After(Motion.Animates ? 700 : 300, delegate
                    {
                        Settle();
                        var pane = F<FrameworkElement>("SubEditPanel");
                        var scroll = FindAll<ScrollViewer>(pane).FirstOrDefault();
                        var paneBox = new Rect(pane.TranslatePoint(new Point(0, 0), root), new Size(pane.ActualWidth, pane.ActualHeight));
                        var controls = FindAll<Control>(pane).Where(x => (x is ButtonBase || x is TextBox || x is ListBox) && x.IsVisible && x.IsEnabled).ToList(); // (a disabled button lets clicks through by design)
                        var blocked = new List<string>();
                        int outside = 0;
                        foreach (var x in controls)
                        {
                            x.BringIntoView();
                            Settle();
                            var box = new Rect(x.TranslatePoint(new Point(0, 0), root), new Size(x.ActualWidth, x.ActualHeight));
                            if (box.Right > paneBox.Right + 0.5 || box.Left < paneBox.Left - 0.5) outside++;
                            var mid = new Point(box.Left + box.Width / 2, box.Top + Math.Min(box.Height / 2, 12));
                            var hit = root.InputHitTest(mid) as DependencyObject;
                            bool mine = false;
                            for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)) if (d == x) { mine = true; break; }
                            if (!mine) blocked.Add(Describe(x) + " under " + Describe(hit as FrameworkElement));
                        }
                        if (scroll != null) scroll.ScrollToTop();
                        var windowButtons = new[] { "MiniBtn", "MinBtn", "MaxBtn", "CloseBtn" }.Where(n => !Shown(n) || F<FrameworkElement>(n).IsVisible == false ? false : true).Where(n =>
                        {
                            var b = F<FrameworkElement>(n);
                            var p = b.TranslatePoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2), root);
                            var hit = root.InputHitTest(p) as DependencyObject;
                            for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d)) if (d == b) return false;
                            return true;
                        }).ToList();
                        HuntLog("   window " + ActualWidth + "x" + ActualHeight + ", viewing area " + F<FrameworkElement>("ViewArea").ActualWidth + ", pane Width " + pane.Width);
                        HuntLog(string.Format("{0,-9} {1}x{2}: pane {3:0} wide, bars {4}, {5} controls, {6} not clickable{7}, {8} sticking out, window buttons blocked: {9}{10}",
                            c.Item1, c.Item3.Width, c.Item3.Height, pane.ActualWidth, Floating ? "floating" : "docked", controls.Count, blocked.Count,
                            blocked.Count > 0 ? " (" + string.Join("; ", blocked.Take(4)) + ")" : "", outside, windowButtons.Count == 0 ? "none" : string.Join(", ", windowButtons),
                            blocked.Count + outside + windowButtons.Count > 0 ? " FAIL" : ""));
                        if (c.Item3.Width == 520 || c.Item3.Width == 1440) Snap("subeditfit_" + c.Item1 + "_" + c.Item3.Width);
                        next();
                    });
                });
            };
            HuntOpen("clip.mp4", delegate { if (playing) TogglePlay(); next(); });
        }

        static string Describe(FrameworkElement e)
        {
            if (e == null) return "nothing";
            string name = System.Windows.Automation.AutomationProperties.GetName(e);
            return e.GetType().Name + (e.Name.Length > 0 ? " " + e.Name : "") + (string.IsNullOrEmpty(name) ? "" : " '" + name + "'");
        }

        // --case safetynet --errors <file>: an error thrown on the window's thread (twice, also from a timer) is written to
        // the file and FastViewer keeps running; the video still plays and a switch still works afterwards.
        void HuntSafetyNet()
        {
            After(1500, delegate { if (!playing) TogglePlay(); throw new InvalidCastException("test: an unexpected error in the interface"); });
            Dispatcher.BeginInvoke(new Action(delegate { }), System.Windows.Threading.DispatcherPriority.Background);
            After(2500, delegate { throw new NullReferenceException("test: a second one"); });
            After(3500, delegate { Go(1); });
            After(4500, delegate
            {
                string file = Program.ErrorsFile;
                string text = file != null && File.Exists(file) ? File.ReadAllText(file) : "";
                int logged = text.Split(new[] { "(kept running)" }, StringSplitOptions.None).Length - 1;
                HuntLog("still running after two errors; errors logged " + logged + " in " + Path.GetFileName(file) + (logged == 2 ? "" : " FAIL") +
                    "; now on " + Path.GetFileName(shown.File) + ", playing " + playing);
                HuntDone();
            });
        }

        // The welcome screen: what it shows (run with no file, --recent <scratch file> and --default yes|no), then a
        // file opened from it: the screen goes and the file goes to the top of the list.
        void HuntWelcome()
        {
            After(1500, delegate
            {
                HuntLog(WelcomeState("started without a file"));
                Snap("welcome");
                var rows = F<StackPanel>("WelcomeRecent").Children.OfType<Button>().ToList();
                if (rows.Count == 0) { HuntDone(); return; }
                string pick = rows.Last().Tag as string;
                var clicked = System.Diagnostics.Stopwatch.StartNew();
                rows.Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => shown != null && shown.Ready, 15000, delegate
                {
                    HuntLog("the picked file was on screen after " + clicked.ElapsedMilliseconds + " ms");
                    HuntLog(WelcomeState("opened " + Path.GetFileName(pick)) + ", on screen " + (shown == null ? "nothing" : Path.GetFileName(shown.File)));
                    System.Threading.Thread.Sleep(2000); // (the list is written 1.5 s after opening)
                    var list = ReadRecent();
                    HuntLog("recent now: " + string.Join(" | ", list.Select(Path.GetFileName)) + (list.Count > 0 && list[0] == pick ? "" : " FAIL"));
                    OpenSettings();
                    After(800, delegate
                    {
                        CloseSettings();
                        After(800, delegate
                        {
                            // Every file of the folder gone (here: shown as if deleted) brings the screen back.
                            files.Clear();
                            ShowWelcome();
                            After(1000, delegate
                            {
                                HuntLog(WelcomeState("folder empty")); Snap("welcome_again");
                                var rows2 = F<StackPanel>("WelcomeRecent").Children.OfType<Button>().ToList();
                                string first = rows2.Count > 0 ? rows2[0].Tag as string : null;
                                ContextMenu menu = null;
                                huntMenuHook = (m, b) => menu = m;
                                if (rows2.Count > 0) rows2[0].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                                huntMenuHook = null;
                                HuntLog("right-click on " + Path.GetFileName(first) + ": menu " + (menu == null ? "none FAIL" : string.Join(" | ", menu.Items.OfType<MenuItem>().Select(i => i.Header))));
                                if (menu != null) menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                After(800, delegate
                                {
                                    var now = ReadRecent();
                                    HuntLog(WelcomeState("removed " + Path.GetFileName(first)) + "; list " + string.Join(" | ", now.Select(Path.GetFileName)) + (now.Contains(first) ? " FAIL" : ""));
                                    F<Button>("WelcomeClearBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                    After(800, delegate
                                    {
                                        HuntLog(WelcomeState("cleared") + "; list has " + ReadRecent().Count + (ReadRecent().Count == 0 ? "" : " FAIL") + ", heading " + F<FrameworkElement>("WelcomeRecentHead").Visibility);
                                        HuntDone();
                                    });
                                });
                            });
                        });
                    });
                });
            });
        }

        string WelcomeState(string when)
        {
            Func<string, string> vis = n => F<FrameworkElement>(n).Visibility == Visibility.Visible ? "shown" : "hidden";
            var rows = F<StackPanel>("WelcomeRecent").Children.OfType<Button>().Select(b => Path.GetFileName(b.Tag as string));
            return when + ": welcome " + WelcomeShowing + ", title '" + F<TextBlock>("MessageTitle").Text + "', body '" + F<TextBlock>("MessageBody").Text +
                "', Open " + vis("MessageOpenBtn") + ", Settings " + vis("WelcomeSettingsBtn") + ", default " + vis("WelcomeDefaultBtn") +
                ", recent [" + string.Join(", ", rows) + "], window title '" + Title + "'" +
                ", file buttons " + string.Join("/", FileButtons.Select(n => F<FrameworkElement>(n).Visibility.ToString()));
        }

        // Delete moves on to the next file of the order you're in (the one before at the end), also sorted by date,
        // shuffled and with only videos shown; deleting the last one shows the welcome screen. Scratch folder only!
        void HuntDeleteNext()
        {
            confirmDelete = false;
            var plan = new List<Action<Action>>();
            Action<string, Action> deleteNow = null;
            deleteNow = (what, then) =>
            {
                var before = new List<string>(files);
                int i = index;
                string expect = NeighbourAfter(before, i);
                string gone = files[i];
                AskDelete();
                After(1800, delegate // (the folder watcher has looked again by then)
                {
                    string now = files.Count == 0 ? null : Path.GetFileName(CurrentFile);
                    bool welcome = WelcomeShowing;
                    bool ok = expect == null ? welcome && !File.Exists(gone) : now != null && string.Equals(now, Path.GetFileName(expect), StringComparison.OrdinalIgnoreCase)
                        && shown != null && string.Equals(shown.File, CurrentFile, StringComparison.OrdinalIgnoreCase) && !File.Exists(gone);
                    HuntLog(what + ": deleted " + Path.GetFileName(gone) + " (" + (i + 1) + " of " + before.Count + ": " + string.Join(" ", before.Select(Path.GetFileName)) +
                        "), now " + (now ?? (welcome ? "welcome screen" : "nothing")) + ", expected " + (expect == null ? "welcome screen" : Path.GetFileName(expect)) + (ok ? "" : " FAIL"));
                    then();
                });
            };
            Action<string, Action> goTo = (name, then) => { int k = files.FindIndex(f => Path.GetFileName(f) == name); if (k >= 0) ShowIndex(k, 1); After(900, then); };

            plan.Add(next => { order = 1; ReloadFolder(); After(900, () => goTo("p2.jpg", () => deleteNow("name order", next))); });
            plan.Add(next => { ShowIndex(files.Count - 1, 1); After(900, () => deleteNow("name order, last file", next)); });
            plan.Add(next => { order = 2; ReloadFolder(); After(900, () => { ShowIndex(1, 1); After(900, () => deleteNow("newest first", next)); }); });
            plan.Add(next => { order = 1; ReloadFolder(); After(600, () => { ToggleShuffle(); After(900, () => deleteNow("shuffled (" + shuffled + ")", next)); }); });
            plan.Add(next => { if (shuffled) ToggleShuffle(); filter = 3; ReloadFolder(); After(900, () => { ShowIndex(0, 1); After(900, () => deleteNow("only videos", next)); }); });
            plan.Add(next => { filter = 0; ReloadFolder(); After(900, () => deleteNow("everything again", next)); });
            Action rest = null;
            rest = () => { if (files.Count == 0) { HuntLog(WelcomeState("all deleted")); Snap("deleted_all"); HuntDone(); } else deleteNow("emptying", rest); };
            plan.Add(next => rest());
            int at = 0;
            Action run = null;
            run = () => { if (at < plan.Count) plan[at++](run); };
            After(1500, run);
        }
    }
}
