// FastViewer: the thumbnail strip under the picture (Settings > Viewing, off by default) and the
// folder grid (G, or the grid button in the bottom bar).
//
// Both only touch files that are on screen: the strip has a fixed set of tiles that just get new
// pictures when you switch, and the grid only makes tiles for the rows you can see.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool showStrip;                // Settings > Viewing > Thumbnail strip
        bool stripOverride;            // --strip: test runs with the strip on, without touching settings
        int gridSize = 1;              // 0 small, 1 medium, 2 large
        static readonly double[] GridTileSizes = { 104, 150, 220 };
        static readonly string[] GridSizeNames = { "Small", "Medium", "Large" };
        const double StripTile = 54;   // tile with its ring: a 48 px picture

        Canvas strip, stripRow;
        List<string> stripFiles;
        int stripIndex = -1;
        readonly List<Tile> stripTiles = new List<Tile>();
        double stripWidth;
        bool stripQueued;

        Grid gridPage;
        ScrollViewer gridScroll;
        Canvas gridCanvas;
        bool gridOpen;
        List<string> gridFiles = new List<string>();
        int gridCols = 1, gridSelected;
        double gridTile;
        bool gridNames;                // file names under the grid tiles (the Names button)

        double GridRow { get { return gridTile + (gridNames ? Tile.NameHeight : 0); } }
        readonly Dictionary<int, Tile> gridShown = new Dictionary<int, Tile>(); // tiles on screen, by position
        readonly Stack<Tile> gridSpare = new Stack<Tile>();

        void InitGallery()
        {
            strip = F<Canvas>("Strip");
            gridPage = F<Grid>("GridPage");
            gridScroll = F<ScrollViewer>("GridScroll");
            gridCanvas = F<Canvas>("GridCanvas");
            strip.SizeChanged += delegate { RequestStripUpdate(); };
            OnClick("GridBtn", ToggleGrid);
            OnClick("GridBackBtn", CloseGrid);
            OnClick("GridSizeBtn", ShowGridSizeMenu);
            OnClick("GridNamesBtn", delegate
            {
                gridNames = !gridNames;
                UpdateGridNamesButton();
                SaveSettings();
                if (gridOpen) LayoutGrid(true);
            });
            UpdateGridNamesButton();
            gridScroll.ScrollChanged += delegate { RealizeGrid(); };
            gridScroll.SizeChanged += delegate(object o, SizeChangedEventArgs e) { if (gridOpen && e.WidthChanged) LayoutGrid(false); };
            gridScroll.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                SetGridSize(gridSize + (e.Delta > 0 ? 1 : -1)); // Ctrl + wheel: bigger or smaller, like File Explorer
                e.Handled = true;
            };
            F<TextBlock>("GridSizeText").Text = GridSizeNames[gridSize];
            ApplyStrip();
        }

        // Size of a thumbnail in screen pixels, in steps of 16 so a slightly different tile size reuses it.
        int ThumbPx(double size)
        {
            return Math.Max(16, (int)Math.Ceiling(size * Dpi() / 16) * 16);
        }

        double PhotoAngle(string file)
        {
            int angle;
            return !IsVideoFile(file) && rotations.TryGetValue(file, out angle) ? angle : 0;
        }

        // Shows `file` on a tile: from memory right away, otherwise asks the thumbnail thread.
        void BindTile(Tile t, int i, string file, int px, bool selected)
        {
            t.Index = i;
            t.Selected = selected;
            t.SetAngle(PhotoAngle(file));
            if (t.File == file && t.Px == px) return; // already showing it (or its thumbnail is on the way)
            if (t.Job != null) t.Job.Cancelled = true;
            t.Job = null;
            t.File = file;
            t.Px = px;
            t.ToolTip = Path.GetFileName(file);
            t.SetName(Path.GetFileName(file));
            bool video = IsVideoFile(file);
            BitmapSource image;
            if (Thumbs.TryGet(file, px, out image))
            {
                t.SetPicture(image, video);
                return;
            }
            t.SetLoading(video);
            t.Job = Thumbs.Request(file, px, Dispatcher, delegate(BitmapSource b)
            {
                if (t.File != file) return;
                t.Job = null;
                t.SetPicture(b, video);
                t.FadeInPicture();
            });
        }

        // ---------------------------------------------------------------- strip

        void ApplyStrip()
        {
            bool appearing = showStrip && strip.Visibility != Visibility.Visible;
            strip.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
            if (appearing && Motion.Animates && IsLoaded) { Motion.FromTo(strip, OpacityProperty, 0, 1, Motion.Ms(Motion.Normal, false), Motion.Smooth); Glide(strip, 0, 12); } // rises in under the picture
            if (!showStrip)
            {
                foreach (var t in stripTiles) t.Clear();
                strip.Children.Clear();
                stripTiles.Clear();
                stripWidth = 0;
            }
            PlaceBars(); // full screen: the video controls sit above the strip
            RequestStripUpdate();
        }

        // After a switch the strip catches up once the new file is on its way (never before it).
        void RequestStripUpdate()
        {
            if (!showStrip || stripQueued) return;
            stripQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateStrip));
        }

        void UpdateStrip()
        {
            stripQueued = false;
            if (!showStrip) return;
            double w = strip.ActualWidth;
            if (w <= 0) return;
            if (w != stripWidth) BuildStripTiles(w);
            int n = stripTiles.Count, c = n / 2, count = files.Count;
            // Which offsets from the current file have a tile: all of them, unless the folder is smaller than the strip.
            int lo = -c, hi = c;
            if (wrapAround && count < n)
            {
                lo = -((count - 1) / 2);
                hi = lo + count - 1;
            }
            int px = ThumbPx(StripTile - 6);
            // Moved on by a few files: the row glides over to the new current file (after it's already shown).
            int moved = stripFiles == files && stripIndex >= 0 ? index - stripIndex : 0;
            stripFiles = files;
            stripIndex = index;
            if (moved != 0 && Math.Abs(moved) <= 3 && Motion.Moves && stripRow != null)
            {
                var mv = Motion.PartsOf(stripRow).Move;
                Motion.FromTo(mv, TranslateTransform.XProperty, mv.X + moved * StripTile, 0, Motion.Ms(180), Motion.In);
            }
            // Current file first, then outwards, so the nearest thumbnails come first.
            for (int step = 0; step < n; step++)
            {
                int d = (step + 1) / 2 * (step % 2 == 1 ? 1 : -1);
                var t = stripTiles[c + d];
                int i = index + d;
                if (count == 0 || d < lo || d > hi || (!wrapAround && (i < 0 || i >= count)))
                {
                    t.Clear();
                    continue;
                }
                i = Wrap(i);
                t.Offset = d;
                BindTile(t, i, files[i], px, d == 0);
            }
        }

        void BuildStripTiles(double w)
        {
            foreach (var t in stripTiles) t.Clear();
            strip.Children.Clear();
            stripTiles.Clear();
            stripWidth = w;
            stripRow = new Canvas();
            strip.Children.Add(stripRow);
            int n = Math.Max(1, (int)((w - 16) / StripTile));
            if (n % 2 == 0) n--; // odd, so the current file is exactly in the middle
            double left = Math.Round((w - n * StripTile) / 2), top = Math.Round((strip.Height - StripTile) / 2);
            for (int k = 0; k < n; k++)
            {
                var t = new Tile(StripTile);
                Canvas.SetLeft(t, left + k * StripTile);
                Canvas.SetTop(t, top);
                t.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
                {
                    int d = ((Tile)o).Offset;
                    if (((Tile)o).File != null && d != 0 && !DialogOpen) ShowIndex(index + d, Math.Sign(d));
                    e.Handled = true;
                };
                stripRow.Children.Add(t);
                stripTiles.Add(t);
            }
        }

        void ClearStrip()
        {
            foreach (var t in stripTiles) t.Clear();
        }

        // The file itself changed (rotation saved into it): get a fresh thumbnail from Windows.
        void RefreshThumbnail(string file)
        {
            Thumbs.Forget(file);
            foreach (var t in stripTiles)
                if (string.Equals(t.File, file, StringComparison.OrdinalIgnoreCase)) t.Clear();
            RequestStripUpdate();
        }

        // ---------------------------------------------------------------- folder grid

        void ToggleGrid()
        {
            if (gridOpen) CloseGrid();
            else OpenGrid();
        }

        void OpenGrid()
        {
            if (files.Count == 0 || mini || gridOpen) return;
            if (settingsOpen) CloseSettings();
            if (playing) TogglePlay();
            StopSlideshow();
            gridOpen = true;
            gridPage.Visibility = Visibility.Visible;
            PlaceBars(); // looks with floating bars dock them while the grid is open
            Motion.Show(gridPage, 0, 0, 1, Motion.Normal);
            gridPage.UpdateLayout(); // so the grid knows its width
            FillGrid(true);
            UpdateControls();
            if (Motion.Moves) { gridScroll.UpdateLayout(); ShrinkIntoTile(); }
        }

        // Where the picture sits at "fit" (zoom 1), in the stage's coordinates.
        Rect FitRect(Slot s)
        {
            int w, h;
            DisplaySize(s, out w, out h);
            double fit = FitScale(s) / Dpi();
            if (w <= 0 || fit <= 0) return Rect.Empty;
            return new Rect((stage.ActualWidth - w * fit) / 2, (stage.ActualHeight - h * fit) / 2, w * fit, h * fit);
        }

        // The tile's picture, in the stage's coordinates.
        Rect TileRect(Tile t)
        {
            var p = t.TranslatePoint(new Point(3, 3), stage);
            return new Rect(p, new Size(Math.Max(1, t.Width - 6), Math.Max(1, t.Width - 6)));
        }

        // Moves the picture (zoom and position) so its fit rectangle lands on `to`, or from `to` back to fit.
        void FlyPicture(Slot s, Rect to, bool intoTile)
        {
            var r = FitRect(s);
            if (s == null || r.IsEmpty || to.IsEmpty) return;
            double z = Math.Min(to.Width / r.Width, to.Height / r.Height);
            double px = to.X + to.Width / 2 - z * (r.X + r.Width / 2), py = to.Y + to.Height / 2 - z * (r.Y + r.Height / 2);
            double ms = Motion.Ms(Motion.Slow);
            Motion.Stop("zoom");
            Motion.Stop("pan");
            if (intoTile)
            {
                Motion.To(s.Zoom, ScaleTransform.ScaleXProperty, z, ms, Motion.In);
                Motion.To(s.Zoom, ScaleTransform.ScaleYProperty, z, ms, Motion.In);
                Motion.To(s.Pan, TranslateTransform.XProperty, px, ms, Motion.In);
                Motion.To(s.Pan, TranslateTransform.YProperty, py, ms, Motion.In, delegate { if (gridOpen) ResetZoom(s); });
            }
            else
            {
                Motion.FromTo(s.Zoom, ScaleTransform.ScaleXProperty, z, 1, ms, Motion.In);
                Motion.FromTo(s.Zoom, ScaleTransform.ScaleYProperty, z, 1, ms, Motion.In);
                Motion.FromTo(s.Pan, TranslateTransform.XProperty, px, 0, ms, Motion.In);
                Motion.FromTo(s.Pan, TranslateTransform.YProperty, py, 0, ms, Motion.In, delegate { UpdateZoomUi(); });
            }
        }

        void ShrinkIntoTile()
        {
            Tile t;
            if (shown != null && gridShown.TryGetValue(gridSelected, out t)) FlyPicture(shown, TileRect(t), true);
        }

        string growFile;  // picked in the grid: grows out of its tile when it appears
        Rect growFrom;

        void CloseGrid()
        {
            if (!gridOpen) return;
            gridOpen = false;
            PlaceBars();
            if (shown != null) ResetZoom(shown); // (it may have shrunk into its tile)
            // Fades away; then lets go of the thumbnails (they stay in the memory cache).
            Motion.Hide(gridPage, 0, 0, 1, Motion.Fast, delegate
            {
                if (gridOpen) return;
                foreach (var t in gridShown.Values) t.Clear();
                gridShown.Clear();
                gridSpare.Clear();
                gridCanvas.Children.Clear();
            });
            UpdateControls();
        }

        // The folder in File Explorer order (also while shuffle is on: picking a file keeps shuffling from there).
        void FillGrid(bool scrollToCurrent)
        {
            string keep = scrollToCurrent ? CurrentFile : gridSelected < gridFiles.Count ? gridFiles[gridSelected] : CurrentFile;
            if (shuffled && sortedFiles != null)
            {
                var have = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                gridFiles = sortedFiles.Where(have.Contains).ToList();
                var listed = new HashSet<string>(gridFiles, StringComparer.OrdinalIgnoreCase);
                gridFiles.AddRange(files.Where(f => !listed.Contains(f)));
            }
            else gridFiles = new List<string>(files);
            gridSelected = Math.Max(0, gridFiles.FindIndex(f => string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)));

            string folder = CurrentFile == null ? "" : Path.GetDirectoryName(CurrentFile);
            F<Run>("GridFolder").Text = Path.GetFileName(folder.TrimEnd('\\')).Length > 0 ? Path.GetFileName(folder.TrimEnd('\\')) : folder;
            int n = gridFiles.Count;
            string kind = filter == 1 ? (n == 1 ? "video" : "videos") : filter == 2 ? (n == 1 ? "photo" : "photos") : (n == 1 ? "item" : "items");
            F<Run>("GridCount").Text = "    " + n + " " + kind + (shuffled ? "  ·  shuffle stays on" : "");

            foreach (var t in gridShown.Values)
            {
                t.Clear();
                gridSpare.Push(t);
            }
            gridShown.Clear();
            LayoutGrid(true);
        }

        void LayoutGrid(bool scrollToSelected)
        {
            double w = gridScroll.ViewportWidth - gridCanvas.Margin.Left - gridCanvas.Margin.Right;
            if (w <= 0) return;
            double target = GridTileSizes[gridSize];
            gridCols = Math.Max(1, (int)(w / target));
            double tile = Math.Floor(w / gridCols); // fill the width exactly
            if (tile != gridTile)
            {
                gridTile = tile;
                foreach (var t in gridShown.Values)
                {
                    t.Clear();
                    gridSpare.Push(t);
                }
                gridShown.Clear();
            }
            int rows = (gridFiles.Count + gridCols - 1) / gridCols;
            gridCanvas.Height = rows * GridRow;
            foreach (var p in gridShown) // columns (or names on/off) may have changed
            {
                p.Value.SetSize(gridTile, gridNames);
                Canvas.SetLeft(p.Value, (p.Key % gridCols) * gridTile);
                Canvas.SetTop(p.Value, (p.Key / gridCols) * GridRow);
            }
            if (scrollToSelected)
            {
                gridScroll.UpdateLayout(); // the new height has to be known before scrolling
                ScrollGridTo(gridSelected, true);
            }
            RealizeGrid();
        }

        // Makes tiles for the rows on screen (re-using the ones that scrolled away).
        void RealizeGrid()
        {
            if (!gridOpen || gridTile <= 0 || gridFiles.Count == 0) return;
            double top = gridScroll.VerticalOffset - gridCanvas.Margin.Top;
            double bottom = top + gridScroll.ViewportHeight;
            int first = Math.Max(0, (int)Math.Floor(top / GridRow)) * gridCols;
            int last = Math.Min(gridFiles.Count - 1, ((int)Math.Floor(bottom / GridRow) + 1) * gridCols - 1);
            foreach (var i in gridShown.Keys.Where(i => i < first || i > last).ToList())
            {
                var t = gridShown[i];
                t.Clear();
                gridShown.Remove(i);
                gridSpare.Push(t);
            }
            int px = ThumbPx(gridTile - 6);
            for (int i = first; i <= last; i++)
            {
                Tile t;
                if (!gridShown.TryGetValue(i, out t))
                {
                    t = gridSpare.Count > 0 ? gridSpare.Pop() : NewGridTile();
                    t.SetSize(gridTile, gridNames);
                    Canvas.SetLeft(t, (i % gridCols) * gridTile);
                    Canvas.SetTop(t, (i / gridCols) * GridRow);
                    gridShown[i] = t;
                }
                BindTile(t, i, gridFiles[i], px, false); // the selection is the gliding ring
            }
            PlaceGridRing(false);
        }

        // ---- the selection ring: one ring that glides from tile to tile

        Border gridRing;

        void PlaceGridRing(bool glide)
        {
            if (gridRing == null)
            {
                gridRing = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(7), IsHitTestVisible = false };
                gridRing.SetResourceReference(Border.BorderBrushProperty, "Accent");
                Panel.SetZIndex(gridRing, 1);
            }
            if (!gridCanvas.Children.Contains(gridRing)) gridCanvas.Children.Add(gridRing);
            gridRing.Width = gridTile;
            gridRing.Height = GridRow;
            var move = Motion.PartsOf(gridRing).Move;
            double x = (gridSelected % Math.Max(1, gridCols)) * gridTile, y = (gridSelected / Math.Max(1, gridCols)) * GridRow;
            Motion.To(move, TranslateTransform.XProperty, x, glide ? Motion.Ms(Motion.Fast) : 0, Motion.In);
            Motion.To(move, TranslateTransform.YProperty, y, glide ? Motion.Ms(Motion.Fast) : 0, Motion.In);
        }

        Tile NewGridTile()
        {
            var t = new Tile(gridTile);
            t.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
            {
                var tile = (Tile)o;
                if (tile.File != null) PickFromGrid(tile.Index);
                e.Handled = true;
            };
            gridCanvas.Children.Add(t);
            return t;
        }

        void PickFromGrid(int gi)
        {
            if (gi < 0 || gi >= gridFiles.Count) return;
            string f = gridFiles[gi];
            int i = files.FindIndex(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
            Tile tile;
            growFile = null;
            if (Motion.Moves && gridShown.TryGetValue(gi, out tile)) { growFile = f; growFrom = TileRect(tile); }
            CloseGrid();
            if (i >= 0 && i != index) ShowIndex(i, shuffled || i > index ? 1 : -1); // grows out of its tile once it's on screen (Reveal)
            else GrowPicked(shown);
        }

        // A file picked in the grid has appeared: it grows from its tile to its place.
        void GrowPicked(Slot s)
        {
            if (growFile == null || s == null || !string.Equals(s.File, growFile, StringComparison.OrdinalIgnoreCase)) return;
            growFile = null;
            FlyPicture(s, growFrom, false);
        }

        void ScrollGridTo(int i, bool center)
        {
            double row = GridRow, y = (i / gridCols) * row + gridCanvas.Margin.Top;
            double offset = gridScroll.VerticalOffset, h = gridScroll.ViewportHeight;
            if (center) gridScroll.ScrollToVerticalOffset(y - (h - row) / 2);
            else if (y < offset) gridScroll.ScrollToVerticalOffset(y - gridCanvas.Margin.Top);
            else if (y + row > offset + h) gridScroll.ScrollToVerticalOffset(y + row - h + gridCanvas.Margin.Bottom);
        }

        void UpdateGridNamesButton()
        {
            F<FrameworkElement>("GridNamesIcon").Visibility = gridNames ? Visibility.Visible : Visibility.Collapsed;
            F<Button>("GridNamesBtn").ToolTip = gridNames ? "Hide file names" : "Show file names under the thumbnails";
        }

        void SelectGridTile(int i)
        {
            gridSelected = i;
            PlaceGridRing(true);
            ScrollGridTo(i, false);
        }

        // Keys while the grid is open: arrows move, Enter opens, Esc (or the grid key) closes. Other
        // shortcuts wait until the grid is closed. False = not ours (so Alt+F4 still works).
        bool HandleGridKey(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            int n = gridFiles.Count, sel = gridSelected;
            int page = Math.Max(1, (int)(gridScroll.ViewportHeight / Math.Max(1, GridRow))) * gridCols;
            switch (key)
            {
                case Key.Escape: CloseGrid(); return true;
                case Key.Enter: case Key.Space: PickFromGrid(sel); return true;
                case Key.Left: sel--; break;
                case Key.Right: sel++; break;
                case Key.Up: if (sel - gridCols < 0) return true; sel -= gridCols; break;
                case Key.Down: if (sel + gridCols >= n) { if (sel / gridCols == (n - 1) / gridCols) return true; sel = n - 1; } else sel += gridCols; break;
                case Key.PageUp: sel -= page; break;
                case Key.PageDown: sel += page; break;
                case Key.Home: sel = 0; break;
                case Key.End: sel = n - 1; break;
                default:
                    Shortcut s;
                    if (shortcutByKey.TryGetValue(Code(key, Keyboard.Modifiers), out s) && s.Id == "grid")
                    {
                        CloseGrid();
                        return true;
                    }
                    return (Keyboard.Modifiers & ModifierKeys.Alt) == 0; // swallow the rest, but leave Alt+F4 alone
            }
            if (n > 0) SelectGridTile(Math.Max(0, Math.Min(n - 1, sel)));
            return true;
        }

        void ShowGridSizeMenu()
        {
            var b = F<Button>("GridSizeBtn");
            if (ClosingOwnMenu(b)) return;
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, VerticalOffset = 4, MinWidth = 160 };
            for (int i = 0; i < GridSizeNames.Length; i++)
            {
                int choice = i;
                menu.Items.Add(Item(GridSizeNames[i], gridSize == i ? "" : null, delegate { SetGridSize(choice); }));
            }
            OpenMenu(menu, b);
        }

        void SetGridSize(int size)
        {
            size = Math.Max(0, Math.Min(GridTileSizes.Length - 1, size));
            if (size == gridSize) return;
            gridSize = size;
            F<TextBlock>("GridSizeText").Text = GridSizeNames[gridSize];
            SaveSettings();
            if (!gridOpen) return;
            var before = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
            if (Motion.Moves) foreach (var t in gridShown.Values) if (t.File != null) before[t.File] = new Rect(t.TranslatePoint(new Point(0, 0), gridScroll), new Size(t.Width, t.Height));
            LayoutGrid(true);
            if (before.Count == 0) return;
            gridScroll.UpdateLayout();
            foreach (var t in gridShown.Values)
            {
                Rect was;
                if (t.File == null || !before.TryGetValue(t.File, out was)) continue;
                var now = t.TranslatePoint(new Point(0, 0), gridScroll);
                var p = Motion.PartsOf(t);
                t.RenderTransformOrigin = new Point(0, 0);
                double k = was.Width / Math.Max(1, t.Width), ms = Motion.Ms(Motion.Normal);
                Motion.FromTo(p.Scale, ScaleTransform.ScaleXProperty, k, 1, ms, Motion.In);
                Motion.FromTo(p.Scale, ScaleTransform.ScaleYProperty, k, 1, ms, Motion.In);
                Motion.FromTo(p.Move, TranslateTransform.XProperty, was.X - now.X, 0, ms, Motion.In);
                Motion.FromTo(p.Move, TranslateTransform.YProperty, was.Y - now.Y, 0, ms, Motion.In);
            }
        }
    }
}
