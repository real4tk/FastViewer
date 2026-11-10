// FastViewer: the top "..." menu and the video "..." menu, with the items and order you pick
// (Settings > Buttons > "..." menus). Saved in settings.ini (topMenu=, videoMenu=), not in look files,
// so sharing a look never rearranges a friend's menus.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FastViewer
{
    partial class ViewerWindow
    {
        class MenuChoice
        {
            public string Id, Name, Glyph;
            public int Group;
            public MenuChoice(string id, string name, string glyph, int group) { Id = id; Name = name; Glyph = glyph; Group = group; }
        }

        static readonly string[] TopGroups = { "File", "View" };
        static readonly MenuChoice[] TopChoices =
        {
            new MenuChoice("saveAs", "Save as", "", 0),
            new MenuChoice("copy", "Copy", "", 0),
            new MenuChoice("copyPath", "Copy file path", "", 0),
            new MenuChoice("showInFolder", "Show in folder", "", 0),
            new MenuChoice("delete", "Delete", "", 0),
            new MenuChoice("rotate", "Rotate", "", 1),
            new MenuChoice("saveRotation", "Save rotation to file", "", 1),
            new MenuChoice("crop", "Crop", "", 1),
            new MenuChoice("mini", "Mini player", "", 1),
            new MenuChoice("pin", "Keep on top", "", 1),
            new MenuChoice("slideshow", "Slideshow", "", 1),
            new MenuChoice("shuffle", "Shuffle", "", 1),
            new MenuChoice("grid", "All files in folder", "", 1),
            new MenuChoice("info", "File info", "", 1),
        };
        static readonly MenuChoice[] VideoChoices =
        {
            new MenuChoice("speeds", "Speeds", "", 0),
            new MenuChoice("repeat", "Repeat", "", 1),
            new MenuChoice("copyFrame", "Copy frame", "", 1),
            new MenuChoice("saveFrame", "Save a frame", "", 1),
            new MenuChoice("frameBack", "Previous frame", "", 2),
            new MenuChoice("frameForward", "Next frame", "", 2),
            new MenuChoice("loopA", "Loop from here (A)", "", 2),
            new MenuChoice("loopB", "Loop until here (B)", "", 2),
            new MenuChoice("changeCover", "Change cover (music)", "", 3),
            new MenuChoice("trim", "Trim", "", 4),
            new MenuChoice("crop", "Crop", "", 4),
            new MenuChoice("compatible", "Make compatible copy", "", 4),
        };

        // Every item in the chosen order; hidden ones are kept in their place so switching them back on puts them there.
        List<string> topMenuOrder = TopChoices.Select(c => c.Id).ToList(), videoMenuOrder = VideoChoices.Select(c => c.Id).ToList();
        readonly HashSet<string> topMenuHidden = new HashSet<string>(), videoMenuHidden = new HashSet<string>();

        static MenuChoice[] ChoicesOf(bool video) { return video ? VideoChoices : TopChoices; }
        List<string> OrderOf(bool video) { return video ? videoMenuOrder : topMenuOrder; }
        HashSet<string> HiddenOf(bool video) { return video ? videoMenuHidden : topMenuHidden; }

        // settings.ini: ids in order, hidden ones with a "-" in front. Null = the default (no line written).
        string MenuLine(bool video)
        {
            var order = OrderOf(video);
            var hidden = HiddenOf(video);
            if (hidden.Count == 0 && order.SequenceEqual(ChoicesOf(video).Select(c => c.Id))) return null;
            return (video ? "videoMenu=" : "topMenu=") + string.Join(",", order.Select(id => hidden.Contains(id) ? "-" + id : id));
        }

        void LoadMenuLine(bool video, string value)
        {
            var known = ChoicesOf(video).Select(c => c.Id).ToList();
            var order = new List<string>();
            var hidden = HiddenOf(video);
            hidden.Clear();
            foreach (var raw in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim(), id = token.TrimStart('-');
                if (!known.Contains(id) || order.Contains(id)) continue; // unknown or twice: left out
                order.Add(id);
                if (token.StartsWith("-")) hidden.Add(id);
            }
            foreach (var id in known) if (!order.Contains(id)) order.Add(id); // items added in a later version: at the end, shown
            if (video) videoMenuOrder = order; else topMenuOrder = order;
        }

        void ResetMenuLayout()
        {
            topMenuOrder = TopChoices.Select(c => c.Id).ToList();
            videoMenuOrder = VideoChoices.Select(c => c.Id).ToList();
            topMenuHidden.Clear();
            videoMenuHidden.Clear();
        }

        // The chosen items, in order. A line goes between groups; the top menu shows its "File"/"View" headings
        // as long as each group's items still sit together.
        List<Control> ChosenItems(bool video, Func<string, IEnumerable<Control>> make)
        {
            var choices = ChoicesOf(video).ToDictionary(c => c.Id);
            var hidden = HiddenOf(video);
            var parts = new List<Tuple<int, List<Control>>>();
            foreach (var id in OrderOf(video))
            {
                if (hidden.Contains(id)) continue;
                var made = make(id).Where(c => c != null).ToList();
                if (made.Count > 0) parts.Add(Tuple.Create(choices[id].Group, made));
            }
            var runs = new List<int>();
            foreach (var p in parts) if (runs.Count == 0 || runs[runs.Count - 1] != p.Item1) runs.Add(p.Item1);
            bool headings = !video && runs.Distinct().Count() == runs.Count;
            var items = new List<Control>();
            int last = -1;
            foreach (var p in parts)
            {
                if (p.Item1 != last)
                {
                    if (last != -1) items.Add(new Separator());
                    if (headings) items.Add(Heading(TopGroups[p.Item1]));
                    last = p.Item1;
                }
                items.AddRange(p.Item2);
            }
            return items;
        }

        IEnumerable<Control> TopMenuItem(string id)
        {
            switch (id)
            {
                case "saveAs": return One(Item("Save as", "", SaveAs, KeyOf("saveAs")));
                case "copy": return One(Item("Copy", "", CopyFile, KeyOf("copy")));
                case "copyPath": return One(Item("Copy file path", "", CopyPath, KeyOf("copyPath")));
                case "showInFolder": return One(Item("Show in folder", "", ShowInFolder, KeyOf("showInFolder")));
                case "delete": return One(Item("Delete", "", AskDelete, KeyOf("delete")));
                case "rotate": return One(Item("Rotate", "", Rotate, KeyOf("rotate")));
                case "saveRotation": return One(Enabled(Item("Save rotation to file", "", SaveRotationToFile, KeyOf("saveRotation")), CanSaveRotation()));
                case "crop": return shown != null && shown.IsAudio ? new Control[0] : One(Item("Crop...", "", StartCrop, KeyOf("crop")));
                case "mini": return One(Item("Mini player", "", ToggleMini, KeyOf("mini")));
                case "pin": return One(Item("Keep on top", pinned ? "" : "", TogglePin, KeyOf("pin")));
                case "slideshow": return One(Item("Slideshow", "", ToggleSlideshow, KeyOf("slideshow")));
                case "shuffle": return One(Item("Shuffle", shuffled ? "" : "", ToggleShuffle, KeyOf("shuffle")));
                case "grid": return One(Item("All files in folder", "", OpenGrid, KeyOf("grid")));
                case "info": return One(Item("File info", "", ToggleInfo, KeyOf("info")));
            }
            return new Control[0];
        }

        IEnumerable<Control> VideoMenuItem(string id)
        {
            bool music = shown != null && shown.IsAudio; // no picture to save, step through or trim
            var none = new Control[0];
            switch (id)
            {
                case "speeds":
                    var speedList = Speeds;
                    if (!speedList.Any(x => Math.Abs(x - speed) < 0.01)) speedList = speedList.Concat(new[] { speed }).OrderBy(x => x).ToArray(); // a speed picked before it was hidden stays visible
                    return speedList.Select(s => { double value = s; return (Control)Item("Speed " + SpeedText(value), Math.Abs(speed - value) < 0.01 ? "" : null, delegate { SetSpeed(value); }); }).ToList();
                case "repeat": return One(Item("Repeat", loop ? "" : null, ToggleLoop, KeyOf("repeat")));
                case "copyFrame": return music ? none : One(Item("Copy frame", "", CopyFrame, KeyOf("copyFrame")));
                case "saveFrame": return music ? none : One(Item("Save a frame", "", SaveFrame, KeyOf("saveFrame")));
                case "frameBack": return music ? none : One(Item("Previous frame", "", delegate { StepFrame(-1); }, KeyOf("frameBack")));
                case "frameForward": return music ? none : One(Item("Next frame", "", delegate { StepFrame(1); }, KeyOf("frameForward")));
                case "loopA": return WithLoopStop("loopA", Item("Loop from here (A)", "", SetLoopStart, KeyOf("loopA")));
                case "loopB": return WithLoopStop("loopB", Item("Loop until here (B)", "", SetLoopEnd, KeyOf("loopB")));
                case "changeCover": return music ? One(Item("Change cover...", "", ChangeCover)) : none;
                case "trim": return music ? none : One(Item("Trim...", "", StartTrim, KeyOf("trim")));
                case "crop": return music ? none : One(Item("Crop...", "", StartCrop, KeyOf("crop")));
                case "compatible": return music ? none : One(Item("Make compatible copy...", "", MakeCompatibleCopy, KeyOf("compatible")));
            }
            return none;
        }

        // "Stop A-B loop" follows whichever loop item comes last in the menu, while a loop is set.
        IEnumerable<Control> WithLoopStop(string id, MenuItem item)
        {
            var list = new List<Control> { item };
            if (loopA == null && loopB == null) return list;
            string lastLoop = videoMenuOrder.LastOrDefault(x => (x == "loopA" || x == "loopB") && !videoMenuHidden.Contains(x));
            if (lastLoop == id) list.Add(Item("Stop A-B loop", "", ClearLoop, KeyOf("loopStop")));
            return list;
        }

        static IEnumerable<Control> One(Control c) { return new[] { c }; }

        void ShowMoreMenu()
        {
            var items = ChosenItems(false, TopMenuItem);
            items.AddRange(HiddenButtonItems(false)); // buttons switched off in Settings > Buttons
            if (items.Count > 0) items.Add(new Separator());
            items.Add(Item("Settings", "", OpenSettings, KeyOf("settings"))); // always there: the way back to this choice
            ShowMenu("MoreBtn", PlacementMode.Bottom, items.ToArray());
        }

        void ShowVideoMenu()
        {
            var items = ChosenItems(true, VideoMenuItem);
            var hiddenButtons = HiddenButtonItems(true);
            if (items.Count == 0 && hiddenButtons.Count == 0) items.Add(Enabled(Item("Choose what's here in Settings > Buttons", null, OpenSettings), true));
            items.AddRange(hiddenButtons);
            ShowMenu("VideoMoreBtn", PlacementMode.Top, items.ToArray());
        }

        // ---------------------------------------------------------------- Settings > Buttons > "..." menus

        StackPanel menuRows;

        void AddMenuSettings()
        {
            AddSubheading("\"...\" menus");
            AddCard("", "Choose and order the menu items",
                "Switch items off to keep the menus short, and move them up or down. Switched-off items keep their keyboard shortcuts. " +
                "Settings always stays at the bottom of the top menu. This isn't part of a look, so sharing a look never changes someone's menus.", null);
            menuRows = new StackPanel();
            settingsPage.Children.Add(menuRows);
            BuildMenuRows();
            var reset = new Button { Content = "Reset", MinWidth = 120, Height = 32 };
            reset.SetResourceReference(StyleProperty, "Filled");
            reset.Click += delegate { ResetMenuLayout(); SaveSettings(); BuildMenuRows(); };
            AddCard("", "Reset the menus", "All items back, in the usual order.", reset);
            settingsRefresh.Add(BuildMenuRows); // another window changed them
        }

        void BuildMenuRows()
        {
            if (menuRows == null) return;
            menuRows.Children.Clear();
            foreach (var video in new[] { false, true })
            {
                var t = new TextBlock { Text = video ? "Video \"...\" menu" : "Top \"...\" menu", FontSize = 13, Margin = new Thickness(2, 10, 0, 6) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                menuRows.Children.Add(t);
                var order = OrderOf(video);
                var hidden = HiddenOf(video);
                var choices = ChoicesOf(video).ToDictionary(c => c.Id);
                for (int i = 0; i < order.Count; i++)
                {
                    string id = order[i];
                    bool vid = video;
                    int at = i;
                    var c = choices[id];
                    var controls = new StackPanel { Orientation = Orientation.Horizontal };
                    controls.Children.Add(MoveButton("", "Move " + c.Name + " up", at > 0, delegate { MoveMenuItem(vid, at, -1); }));
                    controls.Children.Add(MoveButton("", "Move " + c.Name + " down", at < order.Count - 1, delegate { MoveMenuItem(vid, at, 1); }));
                    // (not Toggle(): that one stays in settingsRefresh, and these rows are rebuilt from it)
                    var toggle = new CheckBox { Style = (Style)Application.Current.Resources["ToggleSwitch"], IsChecked = !hidden.Contains(id) };
                    toggle.Click += delegate { if (toggle.IsChecked == true) HiddenOf(vid).Remove(id); else HiddenOf(vid).Add(id); SaveSettings(); };
                    toggle.Margin = new Thickness(12, 0, 0, 0);
                    System.Windows.Automation.AutomationProperties.SetName(toggle, "Show " + c.Name + " in the " + (vid ? "video" : "top") + " menu");
                    controls.Children.Add(toggle);
                    var card = AddCard(c.Glyph, c.Name, null, controls);
                    settingsPage.Children.Remove(card); // AddCard puts it on the page: it belongs in this list
                    card.Margin = new Thickness(card.Margin.Left, 0, card.Margin.Right, 2);
                    menuRows.Children.Add(card);
                }
            }
            MakeKeyboardReachable(menuRows);
        }

        Button MoveButton(string glyph, string name, bool enabled, Action action)
        {
            var b = new Button { Content = new Glyph { Code = glyph, Size = 12 }, Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), IsEnabled = enabled, ToolTip = name };
            b.SetResourceReference(StyleProperty, "Subtle");
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            b.Click += delegate { action(); };
            return b;
        }

        internal void MoveMenuItem(bool video, int index, int by)
        {
            var order = OrderOf(video);
            int to = index + by;
            if (index < 0 || index >= order.Count || to < 0 || to >= order.Count) return;
            var id = order[index];
            order.RemoveAt(index);
            order.Insert(to, id);
            SaveSettings();
            BuildMenuRows();
            // Keep the keyboard on the same item's button, so pressing again keeps moving it.
            int row = 1 + (video ? TopChoices.Length + 1 : 0) + to;
            if (menuRows != null && row < menuRows.Children.Count)
            {
                var card = menuRows.Children[row] as FrameworkElement;
                var btn = card == null ? null : FindAll<Button>(card).Skip(by < 0 ? 0 : 1).FirstOrDefault();
                if (btn != null && btn.IsEnabled) btn.Focus();
            }
        }
    }
}
