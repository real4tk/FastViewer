// FastViewer 1.27.0: the subtitle editor. A pane on the right (where Info goes) for the subtitles of the video on
// screen, loaded .srt/.vtt or automatic captions (or none yet: start from nothing). Change a line's text, nudge its
// start and end, shift all lines, delete or merge lines, add a line at the current moment, undo, save as .srt.
// The original file stays as it is unless "Save" (which asks once more) is used. Changes show at once in the video.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool subEditOpen, subDirty, subConfirmSave;
        string subEditVideo;
        ListBox subList;
        TextBox subTextBox, subEnBox;   // the chosen line's text; its English (1.28.0, while the subtitles have English)
        TextBlock subEnLabel;
        bool SubTyping { get { return (subTextBox != null && subTextBox.IsKeyboardFocusWithin) || (subEnBox != null && subEnBox.IsKeyboardFocusWithin); } }
        TextBlock subFromText, subToText, subSourceText, subShiftText;
        Button subSaveBtn, subUndoBtn, subMergeBtn, subDeleteBtn;
        FrameworkElement subLineEditor;
        double subShiftTotal;
        bool subFilling;          // the editor is being filled in: its own change events are not edits
        DateTime subUserPicked;   // the list follows the video, except just after you picked a line
        readonly List<List<Cue>> subUndo = new List<List<Cue>>();
        readonly DispatcherTimer subFollow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        // Edited, not saved subtitles, per video (this session): seen again when you come back to that video.
        readonly Dictionary<string, Tuple<List<Cue>, string>> editedSubs = new Dictionary<string, Tuple<List<Cue>, string>>(StringComparer.OrdinalIgnoreCase);
        const double SubEditWidth = 360;

        // 360 wide, less in a narrow window (never under 260, the rows wrap), so the picture keeps some room.
        double SubPaneWidth()
        {
            double all = F<FrameworkElement>("ViewArea").ActualWidth;
            return all <= 0 ? SubEditWidth : Math.Max(260, Math.Min(SubEditWidth, Math.Floor(all * 0.45)));
        }

        int SubSel { get { return subList == null ? -1 : subList.SelectedIndex; } }

        void ToggleSubEditor()
        {
            if (subEditOpen) CloseSubEditor(true);
            else OpenSubEditor();
        }

        // 1.28.0: in a see-through Custom look the pane's own colour (Panels) keeps at least 85% cover, so the look's text
        // colour stays readable over a bright video or desktop behind it (like Settings, which keeps 55%).
        const double SubEditLeastCover = 0.85;

        void UpdateSubEditCover()
        {
            var pane = F<Border>("SubEditPanel");
            var bg = Application.Current.Resources["PanelBg"] as SolidColorBrush;
            if (pane == null) return;
            var text = Application.Current.Resources["TextMain"] as SolidColorBrush;
            Func<Color, double> light = x => (0.2126 * x.R + 0.7152 * x.G + 0.0722 * x.B) / 255;
            bool clash = bg != null && text != null && Math.Abs(light(bg.Color) - light(text.Color)) < 0.4;
            if (bg == null || (!clash && bg.Color.A / 255.0 * bg.Opacity >= SubEditLeastCover)) { pane.SetResourceReference(Border.BackgroundProperty, "PanelBg"); return; }
            var c = bg.Color;
            if (clash) c = light(text.Color) > 0.5 ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3); // (white text on a white panel: dark behind it)
            c.A = (byte)Math.Round(255 * Math.Max(SubEditLeastCover, bg.Color.A / 255.0 * bg.Opacity));
            pane.Background = new SolidColorBrush(c);
        }

        void OpenSubEditor()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed || mini) return;
            if (trimming || cropping) return;
            if (fullscreen) ToggleFullscreen();
            if (settingsOpen) CloseSettings();
            if (gridOpen) CloseGrid();
            if (subList == null) BuildSubEditor();
            if (!HasSubs) // nothing yet: start an empty list (Add line here, then Save as)
            {
                subsLookup++;
                SetCues(s.File, new List<Cue>(), "new");
            }
            if (!subsOn) { subsOn = true; SaveSettings(); UpdateSubButton(); }
            UpdateSubEditCover();
            subEditOpen = true;
            subEditVideo = s.File;
            subUndo.Clear();
            subShiftTotal = 0;
            subConfirmSave = false;
            FillSubList(Math.Max(0, CueAt(Position())));
            UpdateSubEditor();
            PlaceSubEditor(true);
            PlaceBars();
            subFollow.Start();
            if (snapDir != null) testLog.Add("subtitle editor open: " + cues.Count + " lines from " + (cuesFrom ?? "nothing"));
        }

        // edited: leaving with changes that aren't saved yet keeps them for this session and says so.
        void CloseSubEditor(bool tell)
        {
            if (!subEditOpen) return;
            subEditOpen = false;
            subFollow.Stop();
            if (Keyboard.FocusedElement is TextBox && SubTyping) Keyboard.Focus(this);
            PlaceSubEditor(false);
            PlaceBars();
            if (subDirty && subEditVideo != null && cues != null)
            {
                editedSubs[subEditVideo] = Tuple.Create(cues, cuesFrom);
                if (tell) ShowToast("Your subtitle changes aren't saved yet. They stay while FastViewer is open.", "Save as...", delegate { HideToast(null); SaveSubsAs(); }, 10, "info");
            }
        }

        double Position()
        {
            var s = shown;
            return s == null || !s.IsVideo ? 0 : (scrubbing ? TimeSpan.FromSeconds(seek.Value) : s.Video.Position).TotalSeconds;
        }

        int CueAt(double at)
        {
            if (cues == null) return -1;
            int found = -1;
            for (int i = 0; i < cues.Count; i++) { if (cues[i].From <= at + 0.001) found = i; else break; }
            return found;
        }

        // ---------------------------------------------------------------- building the pane

        void BuildSubEditor()
        {
            var pane = F<Border>("SubEditPanel");
            var dock = new DockPanel { Margin = new Thickness(16, 14, 10, 14) };
            // A short window: the pane scrolls as a whole (at least 440 high), the list inside it keeps its own scrolling.
            var paneScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = dock };
            paneScroll.SizeChanged += delegate { dock.Height = Math.Max(440, paneScroll.ActualHeight); };
            pane.Child = paneScroll;

            // Title, undo, close
            var head = new Grid { Margin = new Thickness(4, 0, 0, 2) };
            var title = new TextBlock { Text = "Edit subtitles", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Title");
            title.SetResourceReference(TextBlock.FontWeightProperty, "Weight.Title");
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            head.Children.Add(title);
            var headButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            subUndoBtn = IconButton("", "Undo (Ctrl+Z)", SubUndo);
            headButtons.Children.Add(subUndoBtn);
            headButtons.Children.Add(IconButton("", "Close (Esc)", delegate { CloseSubEditor(true); }));
            head.Children.Add(headButtons);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            subSourceText = SmallText("", 12);
            subSourceText.Margin = new Thickness(4, 0, 0, 10);
            subSourceText.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(subSourceText, Dock.Top);
            dock.Children.Add(subSourceText);

            // Shift all
            var shift = new WrapPanel { Margin = new Thickness(4, 0, 6, 10) }; // (in a narrow pane the buttons go under the label)
            var shiftLabel = SmallText("Shift all", 13);
            shiftLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            shiftLabel.VerticalAlignment = VerticalAlignment.Center;
            shift.Children.Add(shiftLabel);
            subShiftText = SmallText("", 12);
            subShiftText.VerticalAlignment = VerticalAlignment.Center;
            subShiftText.Margin = new Thickness(8, 0, 12, 0);
            subShiftText.MinWidth = 36;
            shift.Children.Add(subShiftText);
            var shiftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            foreach (var d in new[] { -1.0, -0.1, 0.1, 1.0 })
            {
                double by = d;
                var b = SmallButton((d > 0 ? "+" : "−") + Math.Abs(d).ToString("0.#", CultureInfo.InvariantCulture) + " s",
                    "Every line " + (d > 0 ? "later" : "earlier") + " by " + Math.Abs(d).ToString("0.#", CultureInfo.InvariantCulture) + " s", delegate { SubShiftAll(by); });
                b.Margin = new Thickness(0, 0, 4, 0);
                shiftButtons.Children.Add(b);
            }
            shift.Children.Add(shiftButtons);
            DockPanel.SetDock(shift, Dock.Top);
            dock.Children.Add(shift);

            // The chosen line, at the bottom
            var editor = new StackPanel { Margin = new Thickness(4, 10, 6, 0) };
            subLineEditor = editor;
            subTextBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, Padding = new Thickness(8, 6, 8, 6) };
            subTextBox.Style = (Style)Application.Current.Resources["SettingsTextBox"];
            subTextBox.VerticalContentAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(subTextBox, "Text of the line");
            subTextBox.TextChanged += delegate { SubTextEdited(); };
            subTextBox.GotKeyboardFocus += delegate { SubSnapshot(); }; // one undo step per edit of a line's text
            editor.Children.Add(subTextBox);
            subEnLabel = SecondaryText("English");
            subEnLabel.Margin = new Thickness(2, 6, 0, 2);
            editor.Children.Add(subEnLabel);
            subEnBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, Padding = new Thickness(8, 6, 8, 6) };
            subEnBox.Style = (Style)Application.Current.Resources["SettingsTextBox"];
            subEnBox.VerticalContentAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(subEnBox, "English of the line");
            subEnBox.TextChanged += delegate { SubEnglishEdited(); };
            subEnBox.GotKeyboardFocus += delegate { SubSnapshot(); };
            editor.Children.Add(subEnBox);
            editor.Children.Add(TimeRow("Start", out subFromText, true));
            editor.Children.Add(TimeRow("End", out subToText, false));
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            subDeleteBtn = TextIconButton("", "Delete", "Delete this line", SubDelete);
            subMergeBtn = TextIconButton("", "Merge with next", "This line and the next become one", SubMerge);
            actions.Children.Add(TextIconButton("", "Add line here", "A new line at this moment of the video", SubAdd));
            actions.Children.Add(subDeleteBtn);
            actions.Children.Add(subMergeBtn);
            editor.Children.Add(actions);
            var save = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var saveAs = new Button { Style = (Style)Application.Current.Resources["Outlined"], Content = "Save as...", Height = 32, MinWidth = 96 };
            saveAs.Click += delegate { SaveSubsAs(); };
            subSaveBtn = new Button { Style = (Style)Application.Current.Resources["AccentButton"], Content = "Save", Height = 32, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            subSaveBtn.Click += delegate { SaveSubs(); };
            save.Children.Add(saveAs);
            save.Children.Add(subSaveBtn);
            editor.Children.Add(save);
            DockPanel.SetDock(editor, Dock.Bottom);
            dock.Children.Add(editor);

            // The lines
            subList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            // 1.28.0: the rows are the list's own items, so their text takes its colour from the list (not the row's style):
            // without this it was Windows' black, hard to read on every dark look.
            subList.SetResourceReference(Control.ForegroundProperty, "TextMain");
            subList.ItemContainerStyle = (Style)Application.Current.Resources["SubLine"];
            ScrollViewer.SetHorizontalScrollBarVisibility(subList, ScrollBarVisibility.Disabled);
            VirtualizingStackPanel.SetIsVirtualizing(subList, true);
            VirtualizingStackPanel.SetVirtualizationMode(subList, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(subList, true);
            subList.SelectionChanged += delegate
            {
                if (subFilling) return;
                if (subList.IsMouseCaptureWithin || subList.IsKeyboardFocusWithin) // picked by you: the video goes there
                {
                    subUserPicked = DateTime.UtcNow;
                    int i = SubSel;
                    if (i >= 0 && i < cues.Count) SeekTo(cues[i].From + 0.01);
                }
                UpdateSubEditor();
            };
            dock.Children.Add(subList);
            MakeKeyboardReachable(pane);

            subFollow.Tick += delegate { SubFollowVideo(); };
            SizeChanged += delegate
            {
                if (!subEditOpen) return;
                double w = SubPaneWidth();
                F<FrameworkElement>("SubEditPanel").Width = w;
                infoCol.Width = new GridLength(w);
            };
        }

        TextBlock SmallText(string text, double size)
        {
            var t = new TextBlock { Text = text, FontSize = size };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return t;
        }

        Button IconButton(string code, string tip, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["Subtle"], Content = new Glyph { Code = code, Size = 14 }, ToolTip = tip, Width = 36, Height = 32 };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += delegate { click(); };
            return b;
        }

        Button SmallButton(string text, string tip, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["FilledSmall"], Content = text, ToolTip = tip, MinWidth = 44 };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += delegate { click(); };
            return b;
        }

        Button TextIconButton(string code, string text, string tip, Action click)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Glyph { Code = code, Size = 14, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)Application.Current.Resources["Subtle"], Content = row, ToolTip = tip, Height = 32, Padding = new Thickness(8, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(b, text);
            b.Click += delegate { click(); };
            return b;
        }

        // "Start  [-] 0:01:02.3 [+]  Now": the minus/plus repeat while held (0.1 s a step, Shift 0.5 s).
        FrameworkElement TimeRow(string name, out TextBlock value, bool start)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var label = SmallText(name, 13);
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            label.Width = 40;
            label.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var minus = Repeater("−", (start ? "Start" : "End") + " earlier by 0.1 s (Shift: 0.5 s)", delegate { SubNudge(start, Keyboard.Modifiers == ModifierKeys.Shift ? -0.5 : -0.1); });
            DockPanel.SetDock(minus, Dock.Left);
            row.Children.Add(minus);
            // label | - | the time (takes what's left, at least 56) | + | Now: in a narrow pane nothing overlaps.
            var now = SmallButton("Now", (start ? "Start" : "End") + " at this moment of the video", delegate { SubSetNow(start); });
            now.MinWidth = 40;
            now.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(now, Dock.Right);
            row.Children.Add(now);
            var plus = Repeater("+", (start ? "Start" : "End") + " later by 0.1 s (Shift: 0.5 s)", delegate { SubNudge(start, Keyboard.Modifiers == ModifierKeys.Shift ? 0.5 : 0.1); });
            DockPanel.SetDock(plus, Dock.Right);
            row.Children.Add(plus);
            value = new TextBlock { FontSize = 13, MinWidth = 56, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular); // (the time doesn't wobble as it changes)
            row.Children.Add(value); // (last: fills the middle)
            return row;
        }

        RepeatButton Repeater(string text, string tip, Action step)
        {
            var b = new RepeatButton { Content = text, ToolTip = tip, Width = 32, Height = 26, Delay = 400, Interval = 80, Focusable = true };
            b.Style = (Style)Application.Current.Resources["SubStep"];
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += delegate { step(); };
            return b;
        }

        // ---------------------------------------------------------------- showing

        static string Clock(double t)
        {
            if (t < 0) t = 0;
            var ts = TimeSpan.FromSeconds(Math.Round(t, 1));
            return (ts.Hours > 0 ? ts.Hours + ":" + ts.Minutes.ToString("00") : ts.Minutes.ToString()) + ":" + ts.Seconds.ToString("00") + "." + (ts.Milliseconds / 100);
        }

        object SubRow(Cue c)
        {
            var row = new DockPanel();
            var time = SmallText(Clock(c.From), 12);
            time.Width = 62;
            time.Margin = new Thickness(0, 1, 6, 0);
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(time);
            var text = new TextBlock { Text = c.Text.Length == 0 ? "(empty)" : c.Text.Replace("\r", "").Replace("\n", " / "), TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            if (c.Text.Length == 0) text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            row.Children.Add(text);
            return row;
        }

        void FillSubList(int select)
        {
            subFilling = true;
            subList.Items.Clear();
            foreach (var c in cues) subList.Items.Add(SubRow(c));
            if (cues.Count > 0)
            {
                subList.SelectedIndex = Math.Max(0, Math.Min(cues.Count - 1, select));
                subList.ScrollIntoView(subList.SelectedItem);
            }
            subFilling = false;
        }

        void RefreshSubRow(int i)
        {
            if (i < 0 || i >= cues.Count) return;
            subFilling = true;
            int sel = SubSel;
            subList.Items[i] = SubRow(cues[i]);
            subList.SelectedIndex = sel;
            subFilling = false;
        }

        void UpdateSubEditor()
        {
            if (subList == null) return;
            int i = SubSel;
            bool has = i >= 0 && cues != null && i < cues.Count;
            subFilling = true;
            subTextBox.Text = has ? cues[i].Text : "";
            bool english = cues != null && cues.Any(c => !string.IsNullOrEmpty(c.En));
            subEnLabel.Visibility = subEnBox.Visibility = english ? Visibility.Visible : Visibility.Collapsed;
            subEnBox.Text = has && english ? cues[i].En ?? "" : "";
            subEnBox.IsEnabled = has;
            subFilling = false;
            subTextBox.IsEnabled = subDeleteBtn.IsEnabled = has;
            subMergeBtn.IsEnabled = has && i + 1 < cues.Count;
            subFromText.Text = has ? Clock(cues[i].From) : "-";
            subToText.Text = has ? Clock(cues[i].To) : "-";
            subUndoBtn.IsEnabled = subUndo.Count > 0;
            bool file = cuesFrom != null && cuesFrom != "auto" && cuesFrom != "new" && File.Exists(cuesFrom);
            subSourceText.Text = (cuesFrom == "auto" ? "Automatic captions" : cuesFrom == "new" ? "New subtitles" : file ? Path.GetFileName(cuesFrom) : "Subtitles") +
                " · " + cues.Count + (cues.Count == 1 ? " line" : " lines") + (subDirty ? " · changed" : "");
            subSourceText.ToolTip = file ? cuesFrom : null;
            subSaveBtn.Content = subConfirmSave ? (file ? "Replace the file?" : "Replace them?") : "Save"; // (short: the pane can be narrow; the tip names the file)
            subSaveBtn.ToolTip = file ? "Saves over " + Path.GetFileName(cuesFrom) : cuesFrom == "auto" ? "Keeps these as this video's automatic captions" : "Saves them as a new .srt file";
            subSaveBtn.IsEnabled = subDirty || cuesFrom == "new";
            subShiftText.Text = Math.Abs(subShiftTotal) < 0.05 ? "" : (subShiftTotal > 0 ? "+" : "−") + Math.Abs(subShiftTotal).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        // While the video plays, the line being said is the chosen one (not while you type, or just after picking one).
        void SubFollowVideo()
        {
            if (!subEditOpen || cues == null || SubTyping || (DateTime.UtcNow - subUserPicked).TotalSeconds < 3 || !playing) return;
            int i = CueAt(Position());
            if (i < 0 || i == SubSel) return;
            subFilling = true;
            subList.SelectedIndex = i;
            subList.ScrollIntoView(subList.SelectedItem);
            subFilling = false;
            UpdateSubEditor();
        }

        // What shows over the video follows every change at once.
        void SubChanged(int select)
        {
            subDirty = true;
            subConfirmSave = false;
            cueShown = -2;
            if (select >= -1) FillSubList(select);
            UpdateSubEditor();
            UpdateSubButton();
            SubTick();
        }

        void SubSnapshot()
        {
            if (cues == null) return;
            subUndo.Add(cues.Select(c => new Cue { From = c.From, To = c.To, Text = c.Text, En = c.En }).ToList());
            if (subUndo.Count > 200) subUndo.RemoveAt(0);
            if (subUndoBtn != null) subUndoBtn.IsEnabled = true;
        }

        // ---------------------------------------------------------------- edits

        void SubTextEdited()
        {
            if (subFilling) return;
            int i = SubSel;
            if (i < 0 || i >= cues.Count) return;
            cues[i].Text = subTextBox.Text.Replace("\r\n", "\n").Trim('\n');
            RefreshSubRow(i);
            SubChanged(-2);
        }

        void SubEnglishEdited()
        {
            if (subFilling) return;
            int i = SubSel;
            if (i < 0 || i >= cues.Count) return;
            cues[i].En = subEnBox.Text.Replace("\r\n", "\n").Trim('\n');
            SubChanged(-2);
        }

        void SubNudge(bool start, double by)
        {
            int i = SubSel;
            if (i < 0 || i >= cues.Count) return;
            SubSnapshot();
            var c = cues[i];
            if (start) c.From = Math.Max(0, Math.Min(c.To - 0.2, c.From + by));
            else c.To = Math.Max(c.From + 0.2, c.To + by);
            SubResort(c);
            if (start) SeekTo(c.From + 0.01); // you see (and hear) where it starts now
        }

        void SubSetNow(bool start)
        {
            int i = SubSel;
            if (i < 0 || i >= cues.Count) return;
            SubSnapshot();
            var c = cues[i];
            double now = Position();
            if (start) { c.From = now; if (c.To < c.From + 0.2) c.To = c.From + Math.Max(0.2, Whisper.ReadingTime(c.Text)); }
            else c.To = Math.Max(c.From + 0.2, now);
            SubResort(c);
        }

        // A changed start can move a line past its neighbours: the list stays in order of starting time.
        void SubResort(Cue c)
        {
            cues.Sort((a, b) => a.From.CompareTo(b.From));
            SubChanged(cues.IndexOf(c));
        }

        void SubShiftAll(double by)
        {
            if (cues == null || cues.Count == 0) return;
            SubSnapshot();
            by = Math.Max(by, -cues.Min(c => c.From)); // nothing before the start of the video
            foreach (var c in cues) { c.From += by; c.To += by; }
            subShiftTotal += by;
            SubChanged(SubSel);
        }

        void SubDelete()
        {
            int i = SubSel;
            if (i < 0 || i >= cues.Count) return;
            SubSnapshot();
            cues.RemoveAt(i);
            SubChanged(Math.Min(i, cues.Count - 1));
        }

        void SubMerge()
        {
            int i = SubSel;
            if (i < 0 || i + 1 >= cues.Count) return;
            SubSnapshot();
            var a = cues[i];
            var b = cues[i + 1];
            string joined = (a.Text + " " + b.Text).Trim();
            a.Text = joined.Length > 42 && !a.Text.Contains("\n") && !b.Text.Contains("\n") ? a.Text.Trim() + "\n" + b.Text.Trim() : joined; // two lines when long
            if (!string.IsNullOrEmpty(a.En) || !string.IsNullOrEmpty(b.En)) a.En = ((a.En ?? "") + " " + (b.En ?? "")).Trim();
            a.To = Math.Max(a.To, b.To);
            cues.RemoveAt(i + 1);
            SubChanged(i);
        }

        void SubAdd()
        {
            if (cues == null) return;
            SubSnapshot();
            double now = Position();
            int after = CueAt(now);
            double next = after + 1 < cues.Count ? cues[after + 1].From : double.MaxValue;
            if (after >= 0 && cues[after].To > now) cues[after].To = Math.Max(cues[after].From + 0.2, now); // the line on screen ends here
            var c = new Cue { From = now, To = Math.Min(now + 2, Math.Max(now + 0.5, next)), Text = "" };
            cues.Insert(after + 1, c);
            SubChanged(after + 1);
            subTextBox.Focus(); // type what is said
        }

        void SubUndo()
        {
            if (subUndo.Count == 0) return;
            var back = subUndo[subUndo.Count - 1];
            subUndo.RemoveAt(subUndo.Count - 1);
            int sel = SubSel;
            cues.Clear();
            cues.AddRange(back);
            SubChanged(Math.Min(sel, cues.Count - 1));
        }

        void SeekTo(double seconds)
        {
            var s = shown;
            if (s == null || !s.IsVideo) return;
            s.Video.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
            UpdateTime();
        }

        // ---------------------------------------------------------------- saving

        // Save: over the file they came from (asks once more: the button says "Replace ...?"), as this video's
        // automatic captions, or (new ones) as a new file.
        void SaveSubs()
        {
            if (cues == null) return;
            bool file = cuesFrom != null && cuesFrom != "auto" && cuesFrom != "new" && File.Exists(cuesFrom);
            if (cuesFrom == "new" || (!file && cuesFrom != "auto")) { SaveSubsAs(); return; }
            if (!subConfirmSave) { subConfirmSave = true; UpdateSubEditor(); return; }
            subConfirmSave = false;
            try
            {
                if (file) WriteSrt(cuesFrom);
                else Whisper.Keep(subEditVideo, cues);
                Translator.Keep(subEditVideo, file ? cuesFrom : "auto", cues); // 1.28.0: the English keeps up with the edits
                SavedSubs(file ? cuesFrom : "auto");
                ShowToast(file ? "Saved " + Path.GetFileName(cuesFrom) : "Captions kept", null, null, 3, "done");
            }
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, null, null, 6, "error"); }
            UpdateSubEditor();
        }

        void SaveSubsAs()
        {
            if (cues == null || subEditVideo == null) return;
            string folder = Path.GetDirectoryName(subEditVideo), name = Path.GetFileNameWithoutExtension(subEditVideo);
            string suggest = name + ".srt";
            if (File.Exists(Path.Combine(folder, suggest)) || cuesFrom == Path.Combine(folder, suggest)) suggest = name + " (edited).srt";
            string target = SubSaveTarget; // tests
            if (target == null)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save subtitles", InitialDirectory = folder, FileName = suggest, DefaultExt = ".srt", Filter = "Subtitles (*.srt)|*.srt" };
                if (dlg.ShowDialog(this) != true) return;
                target = dlg.FileName;
            }
            try
            {
                WriteSrt(target);
                Translator.Keep(subEditVideo, target, cues);
                SavedSubs(target);
                chosenSubs[subEditVideo] = target; // from now on this video shows them
                ShowToast("Saved " + Path.GetFileName(target), null, null, 3, "done");
            }
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, null, null, 6, "error"); }
            UpdateSubEditor();
        }
        internal string SubSaveTarget; // tests: where Save as writes, without a dialog

        void WriteSrt(string path)
        {
            // Through a temporary file, so a failed save never leaves half a file.
            string temp = path + ".fvsubs.tmp";
            File.WriteAllText(temp, SubtitleFile.ToSrt(cues.Where(c => c.Text.Trim().Length > 0)), new System.Text.UTF8Encoding(true));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        void SavedSubs(string from)
        {
            cuesFrom = from;
            subDirty = false;
            if (subEditVideo != null) editedSubs.Remove(subEditVideo);
        }

        // Called when a file's subtitles are looked for: edited ones of this session come first.
        bool UseEditedSubs(string video)
        {
            Tuple<List<Cue>, string> kept;
            if (video == null || !editedSubs.TryGetValue(video, out kept)) return false;
            subsLookup++;
            SetCues(video, kept.Item1, kept.Item2);
            return true;
        }

        // Keys while the pane is open: 1 = done here, 2 = typing in the text box (not a shortcut), 0 = the usual keys.
        // Esc leaves the box, then the pane; Ctrl+Z undoes (outside the box, which has its own).
        int HandleSubEditKey(KeyEventArgs e)
        {
            if (!subEditOpen) return 0;
            bool typing = SubTyping;
            if (e.Key == Key.Escape) { if (typing) Keyboard.Focus(this); else CloseSubEditor(true); return 1; }
            if (typing) return 2;
            if (subList != null && subList.IsKeyboardFocusWithin)
            {
                if (e.Key == Key.Delete) { SubDelete(); return 1; } // the line, never the video
                if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.PageUp || e.Key == Key.PageDown || e.Key == Key.Home || e.Key == Key.End) return 2; // moving through the list
            }
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { SubUndo(); return 1; }
            return 0;
        }

        // The pane takes the Info pane's column (the picture moves left, nothing is covered) and slides in like it.
        void PlaceSubEditor(bool show)
        {
            var pane = F<FrameworkElement>("SubEditPanel");
            if (show && infoOpen) { infoOpen = false; F<FrameworkElement>("InfoPanel").Visibility = Visibility.Collapsed; }
            bool was = pane.Visibility == Visibility.Visible;
            double width = SubPaneWidth();
            if (show) pane.Width = width;
            infoCol.Width = new GridLength(show ? width : 0);
            var move = Motion.PartsOf(pane).Move;
            if (was == show || !Motion.Animates)
            {
                Motion.Set(move, TranslateTransform.XProperty, 0);
                pane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            double ms = Motion.Ms(Motion.Normal);
            if (!Motion.Moves)
            {
                Motion.Set(move, TranslateTransform.XProperty, 0);
                if (show) { pane.Visibility = Visibility.Visible; Motion.FromTo(pane, OpacityProperty, 0, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth); }
                else Motion.To(pane, OpacityProperty, 0, Motion.Ms(Motion.Fast, false), Motion.Smooth, delegate { pane.Visibility = Visibility.Collapsed; Motion.Set(pane, OpacityProperty, 1); });
                return;
            }
            Motion.Set(pane, OpacityProperty, 1);
            if (show) { pane.Visibility = Visibility.Visible; Motion.Set(move, TranslateTransform.XProperty, width); Motion.To(move, TranslateTransform.XProperty, 0, ms, Motion.In); }
            else Motion.To(move, TranslateTransform.XProperty, width, ms, Motion.Smooth, delegate { pane.Visibility = Visibility.Collapsed; });
            double shift = show ? width / 2 : -width / 2;
            foreach (var name in new[] { "Stage", "MessagePanel", "Scrim", "CenterPlayBtn", "Transport", "Toast" })
                SlideFrom(root.FindName(name) as FrameworkElement, shift, ms);
            SlideFrom(F<FrameworkElement>("NextBtn"), shift * 2, ms);
        }
    }
}