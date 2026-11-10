// FastViewer: search the grid by name and sort it by name, date, size or length.
// Only the grid changes: the viewer keeps going through the folder in its own order.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        // 0 folder order, 1 name, 2 newest, 3 oldest, 4 largest, 5 smallest, 6 longest, 7 shortest (settings line gridSort=)
        int gridSort;
        static readonly string[] GridSortNames = { "Folder order", "Name", "Newest first", "Oldest first", "Largest first", "Smallest first", "Longest first", "Shortest first" };
        static readonly string[] GridSortShort = { "Folder order", "Name", "Newest", "Oldest", "Largest", "Smallest", "Longest", "Shortest" };
        TextBox gridSearch;
        string gridQuery = "";
        readonly DispatcherTimer gridSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        // Lengths of videos and songs from Windows' property system (also what File Explorer shows), read once.
        static readonly Dictionary<string, double> lengthCache = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool lengthsReading;

        void InitGridSearch()
        {
            gridSearch = F<TextBox>("GridSearch");
            gridSearch.TextChanged += delegate
            {
                F<UIElement>("GridSearchHint").Visibility = gridSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                F<UIElement>("GridSearchClear").Visibility = gridSearch.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                gridSearchTimer.Stop();
                gridSearchTimer.Start(); // typing fast filters once
            };
            gridSearchTimer.Tick += delegate
            {
                gridSearchTimer.Stop();
                string q = gridSearch.Text.Trim();
                if (q == gridQuery) return;
                gridQuery = q;
                if (gridOpen) FillGrid(false);
            };
            OnClick("GridSearchClear", delegate { gridSearch.Text = ""; ApplyGridSearchNow(); gridSearch.Focus(); });
            OnClick("GridSortBtn", ShowGridSortMenu);
            F<TextBlock>("GridSortText").Text = GridSortShort[gridSort];
        }

        void ApplyGridSearchNow()
        {
            gridSearchTimer.Stop();
            string q = gridSearch.Text.Trim();
            if (q == gridQuery) return;
            gridQuery = q;
            if (gridOpen) FillGrid(false);
        }

        // Keys while typing in the search box: Up/Down/Page/Enter work the grid, Esc clears (then closes), the rest types.
        // Null = not a search key (the grid handles it).
        bool? HandleGridSearchKey(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && key == Key.F) { gridSearch.Focus(); gridSearch.SelectAll(); return true; }
            if (!gridSearch.IsKeyboardFocusWithin) return null;
            switch (key)
            {
                case Key.Escape:
                    if (gridSearch.Text.Length > 0) { gridSearch.Text = ""; ApplyGridSearchNow(); }
                    else Keyboard.ClearFocus();
                    return true;
                case Key.Enter: ApplyGridSearchNow(); return null; // opens the picked file
                case Key.Up: case Key.Down: case Key.PageUp: case Key.PageDown: ApplyGridSearchNow(); return null;
                case Key.Tab: return false;
                default: return false; // typing: the box gets it
            }
        }

        // Search and sort on top of the folder's own list.
        List<string> SearchAndSort(List<string> list)
        {
            if (gridQuery.Length > 0)
            {
                var words = gridQuery.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                list = list.Where(f => { string n = Path.GetFileName(f); return words.All(w => n.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0); }).ToList();
            }
            if (gridSort == 0) return list;
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++) order[list[i]] = i;
            Comparison<string> same = (a, b) => order[a].CompareTo(order[b]); // ties keep folder order
            Func<string, long> size = f => { try { return new FileInfo(f).Length; } catch { return 0; } };
            Func<string, DateTime> date = f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } };
            List<string> sorted;
            switch (gridSort)
            {
                case 1: sorted = list.OrderBy(f => Path.GetFileName(f), new NaturalOrder()).ToList(); break;
                case 2: sorted = list.OrderByDescending(date).ThenBy(f => order[f]).ToList(); break;
                case 3: sorted = list.OrderBy(date).ThenBy(f => order[f]).ToList(); break;
                case 4: sorted = list.OrderByDescending(size).ThenBy(f => order[f]).ToList(); break;
                case 5: sorted = list.OrderBy(size).ThenBy(f => order[f]).ToList(); break;
                default:
                    ReadLengthsFor(list);
                    // Photos (no length) go last, in folder order.
                    Func<string, double> len = f => { double d; lock (lengthCache) return lengthCache.TryGetValue(f, out d) ? d : -1; };
                    sorted = gridSort == 6
                        ? list.OrderBy(f => len(f) < 0 ? 1 : 0).ThenByDescending(len).ThenBy(f => order[f]).ToList()
                        : list.OrderBy(f => len(f) < 0 ? 1 : 0).ThenBy(len).ThenBy(f => order[f]).ToList();
                    break;
            }
            return sorted;
        }

        // Reads the lengths that aren't known yet on a background thread, then sorts the grid again.
        void ReadLengthsFor(List<string> list)
        {
            List<string> missing;
            lock (lengthCache) missing = list.Where(f => IsVideoFile(f) && !lengthCache.ContainsKey(f)).ToList();
            if (missing.Count == 0 || lengthsReading) return;
            lengthsReading = true;
            UpdateGridCount();
            var t = new Thread(delegate()
            {
                foreach (var f in missing)
                {
                    double d = MediaLength(f);
                    lock (lengthCache) lengthCache[f] = d;
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    lengthsReading = false;
                    if (gridOpen && gridSort >= 6) FillGrid(false); else UpdateGridCount();
                }));
            }) { IsBackground = true, Name = "Grid lengths" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        static readonly Guid MediaInfo = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"); // System.Media.Duration = 3 (100 ns units)

        internal static double MediaLength(string file)
        {
            IPropertyStore store = null;
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(file, IntPtr.Zero, 0, ref iid, out store) != 0 || store == null) return 0;
                var key = new PropertyKey { Format = MediaInfo, Id = 3 };
                PropVariant v;
                if (store.GetValue(ref key, out v) != 0) return 0;
                try
                {
                    const ushort UInt64 = 21;
                    if (v.Type != UInt64) return 0;
                    long ticks = IntPtr.Size == 8 ? v.A.ToInt64() : ((long)(uint)v.A.ToInt32() | ((long)v.B.ToInt32() << 32));
                    return ticks / 1e7;
                }
                finally { PropVariantClear(ref v); }
            }
            catch { return 0; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        void ShowGridSortMenu()
        {
            var b = F<Button>("GridSortBtn");
            if (ClosingOwnMenu(b)) return;
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, VerticalOffset = 4, MinWidth = 180 };
            for (int i = 0; i < GridSortNames.Length; i++)
            {
                int choice = i;
                if (i == 1 || i == 2 || i == 4 || i == 6) menu.Items.Add(new Separator());
                menu.Items.Add(Item(GridSortNames[i], gridSort == i ? "" : null, delegate { SetGridSort(choice); }));
            }
            if (huntMenuHook != null) { huntMenuHook(menu, b); return; }
            OpenMenu(menu, b);
        }

        internal void SetGridSort(int sort)
        {
            sort = Math.Max(0, Math.Min(GridSortNames.Length - 1, sort));
            if (sort == gridSort) return;
            gridSort = sort;
            F<TextBlock>("GridSortText").Text = GridSortShort[gridSort];
            SaveSettings();
            if (gridOpen) FillGrid(false);
        }

        void UpdateGridCount()
        {
            int n = gridFiles.Count;
            string kind = filter == 3 ? (n == 1 ? "video" : "videos") : filter == 2 ? (n == 1 ? "photo" : "photos") : filter == 4 ? (n == 1 ? "song" : "songs") : (n == 1 ? "item" : "items");
            string text = "    " + n + " " + kind;
            if (gridQuery.Length > 0) text = "    " + n + " of " + files.Count + " match “" + gridQuery + "”";
            // Nothing to show: say so in the middle instead of leaving a blank page.
            var empty = F<FrameworkElement>("GridEmpty");
            empty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (n == 0)
            {
                F<TextBlock>("GridEmptyTitle").Text = gridQuery.Length > 0 ? "No names contain “" + gridQuery + "”" : "Nothing to show here";
                F<TextBlock>("GridEmptyText").Text = gridQuery.Length > 0 ? "Try fewer letters, or press Esc to clear the search." : "Settings > Viewing > Show decides which files are listed.";
            }
            if (lengthsReading && gridSort >= 6) text += "  ·  getting lengths...";
            if (shuffled) text += "  ·  shuffle stays on";
            F<System.Windows.Documents.Run>("GridCount").Text = text;
        }

        // Names with numbers in them sort like File Explorer: photo 2 before photo 10.
        class NaturalOrder : IComparer<string>
        {
            [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
            static extern int StrCmpLogicalW(string a, string b);
            public int Compare(string a, string b) { return StrCmpLogicalW(a, b); }
        }
    }
}
