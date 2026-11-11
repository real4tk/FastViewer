// FastViewer: cropping a photo or a video. A frame lies over the picture: drag its corners or sides, drag
// inside it to move it, or drag outside it for a new one; "Shape" holds a width:height. Save replaces the
// file safely (written to a copy next to it, checked, then swapped in while the original goes to the Recycle
// Bin; the date moves by 10 ms); Save a copy leaves the original alone. Photos keep their type (JPEG at quality 95, PNG, BMP, TIFF;
// other types become a JPEG or PNG copy). Videos are encoded again as .mp4 with Windows' own video tools.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        bool cropping;
        double cropX0, cropY0, cropX1 = 1, cropY1 = 1; // the part to keep, 0..1 of the picture as it's shown
        double cropRatio;           // width : height to hold while dragging; 0 = any
        int cropZone;               // what the mouse holds: 1 left, 2 top, 4 right, 8 bottom (a corner: two), 16 the frame, 32 a new frame
        Point cropGrab;             // where the mouse took hold, 0..1 of the picture
        double[] cropFrom;          // the frame at that moment
        Rect cropPic;               // where the picture is on the stage
        readonly List<System.Windows.Shapes.Rectangle> cropHandles = new List<System.Windows.Shapes.Rectangle>();
        internal string lastCropResult; // tests

        void InitCrop()
        {
            OnClick("CropSaveBtn", delegate { SaveCrop(false); });
            OnClick("CropCopyBtn", delegate { SaveCrop(true); });
            OnClick("CropCancelBtn", EndCrop);
            OnClick("CropRatioBtn", ShowCropShapeMenu);
            OnClick("CropResetBtn", delegate
            {
                cropX0 = cropY0 = 0;
                cropX1 = cropY1 = 1;
                SetCropShape("Any shape", 0);
            });
            var layer = F<Canvas>("CropLayer");
            for (int i = 0; i < 8; i++)
            {
                var handle = new System.Windows.Shapes.Rectangle
                {
                    Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), StrokeThickness = 1, IsHitTestVisible = false
                };
                cropHandles.Add(handle);
                layer.Children.Add(handle);
            }
            layer.MouseLeftButtonDown += CropMouseDown;
            layer.MouseMove += CropMouseMove;
            layer.MouseLeftButtonUp += delegate
            {
                if (cropZone == 0) return;
                cropZone = 0;
                layer.ReleaseMouseCapture();
            };
            layer.LostMouseCapture += delegate { cropZone = 0; };
            layer.MouseRightButtonUp += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; };
            layer.SizeChanged += delegate
            {
                if (!cropping) return;
                SizeCropBar();
                Dispatcher.BeginInvoke(new Action(PlaceCrop), DispatcherPriority.Loaded); // once the picture has been fitted again
            };
            var bar = F<FrameworkElement>("CropBar");
            bar.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; };
            bar.MouseRightButtonUp += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; };
        }

        // The type a cropped photo is saved as: its own when Windows can write that, otherwise PNG (types that can
        // have see-through parts) or JPEG.
        static string CropSaveType(string ext)
        {
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".jfif": case ".jpe": case ".png": case ".bmp": case ".tif": case ".tiff": return ext;
                case ".gif": case ".webp": case ".ico": return ".png";
                default: return ".jpg";
            }
        }

        void ToggleCrop()
        {
            if (cropping) EndCrop(); else StartCrop();
        }

        void StartCrop()
        {
            var s = shown;
            if (s == null || s.File == null || s.Failed || !s.Ready || s.IsAudio || cropping || settingsOpen || gridOpen) return;
            if (!s.IsVideo && s.Gif != null) { ShowToast("Moving GIFs can't be cropped.", null, null, 4, null); return; }
            if (s.IsVideo && s.Video.NaturalVideoWidth == 0) return;
            if (trimming) EndTrim();
            if (slideshow) StopSlideshow();
            if (mini) ToggleMini();
            if (playing) TogglePlay();
            Motion.Stop("zoom");
            ResetZoom(s);
            var spin = s.Rotor.RenderTransform as TransformGroup; // a turn still settling
            if (spin != null) foreach (var part in spin.Children) { if (part is RotateTransform) Motion.Set(part, RotateTransform.AngleProperty, 0); else { Motion.Set(part, ScaleTransform.ScaleXProperty, 1); Motion.Set(part, ScaleTransform.ScaleYProperty, 1); } }
            cropping = true;
            cropZone = 0;
            cropX0 = cropY0 = 0;
            cropX1 = cropY1 = 1;
            cropRatio = 0;
            F<TextBlock>("CropRatioText").Text = "Any shape";
            string ext = Path.GetExtension(s.File).ToLowerInvariant();
            bool replaceable = s.IsVideo ? ext == ".mp4" || ext == ".m4v" : CropSaveType(ext) == ext;
            F<FrameworkElement>("CropSaveBtn").Visibility = replaceable ? Visibility.Visible : Visibility.Collapsed;
            HideToast(null);
            s.Host.Margin = new Thickness(24, 60, 24, 96); // the picture makes room for the crop bar, the handles and a message at the top
            UpdateControls();
            UpdateZoomUi();
            F<FrameworkElement>("CropLayer").Visibility = Visibility.Visible;
            SizeCropBar();
            Motion.Show(F<FrameworkElement>("CropBar"), 0, 12);
            stage.UpdateLayout();
            PlaceCrop();
        }

        void EndCrop()
        {
            if (!cropping) return;
            cropping = false;
            cropZone = 0;
            var layer = F<Canvas>("CropLayer");
            layer.ReleaseMouseCapture();
            layer.Visibility = Visibility.Collapsed;
            foreach (var s in slots) if (s != null) s.Host.Margin = new Thickness(0);
            Motion.Hide(F<FrameworkElement>("CropBar"), 0, 8, 1, 150);
            UpdateControls();
            UpdateZoomUi();
        }

        // As wide as the window allows; a narrow one drops the hint, then Reset, then shortens "Save a copy".
        void SizeCropBar()
        {
            var bar = F<FrameworkElement>("CropBar");
            double width = Math.Max(300, Math.Min(860, stage.ActualWidth - 32));
            bar.Width = width;
            F<FrameworkElement>("CropHint").Visibility = width >= 800 ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("CropResetBtn").Visibility = width >= 600 ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("CropIcon").Visibility = width >= 520 ? Visibility.Visible : Visibility.Collapsed;
            F<Button>("CropCopyBtn").Content = width >= 520 ? "Save a copy" : "Copy";
        }

        // Where the picture is on the stage right now.
        Rect CropPictureRect()
        {
            var s = shown;
            if (s == null) return Rect.Empty;
            var el = s.IsVideo ? (FrameworkElement)s.Video : s.Picture;
            if (el.ActualWidth < 1 || el.ActualHeight < 1) return Rect.Empty;
            try { return el.TransformToAncestor(stage).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
            catch { return Rect.Empty; }
        }

        // The size of the part in the frame, in the picture's own pixels (videos: even numbers, as they're saved).
        void CropPixels(out int w, out int h)
        {
            int fullW, fullH;
            DisplaySize(shown, out fullW, out fullH);
            w = Math.Max(1, (int)Math.Round((cropX1 - cropX0) * fullW));
            h = Math.Max(1, (int)Math.Round((cropY1 - cropY0) * fullH));
            if (shown != null && shown.IsVideo) { w = Math.Max(2, w & ~1); h = Math.Max(2, h & ~1); }
        }

        // Puts the frame, its handles, the lines and the dimmed surroundings where they belong.
        void PlaceCrop()
        {
            if (!cropping || shown == null) return;
            var layer = F<Canvas>("CropLayer");
            var pic = CropPictureRect();
            if (pic.IsEmpty || pic.Width < 4 || pic.Height < 4) return;
            cropPic = pic;
            var r = new Rect(pic.X + cropX0 * pic.Width, pic.Y + cropY0 * pic.Height,
                             Math.Max(1, (cropX1 - cropX0) * pic.Width), Math.Max(1, (cropY1 - cropY0) * pic.Height));
            F<System.Windows.Shapes.Path>("CropDim").Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, Math.Max(1, layer.ActualWidth), Math.Max(1, layer.ActualHeight))), new RectangleGeometry(r));
            foreach (var name in new[] { "CropShade", "CropFrame" })
            {
                var box = F<FrameworkElement>(name);
                Canvas.SetLeft(box, r.X);
                Canvas.SetTop(box, r.Y);
                box.Width = r.Width;
                box.Height = r.Height;
            }
            var lines = new GeometryGroup();
            for (int i = 1; i <= 2; i++) // thirds, to line things up by
            {
                lines.Children.Add(new LineGeometry(new Point(r.X + r.Width * i / 3, r.Y), new Point(r.X + r.Width * i / 3, r.Bottom)));
                lines.Children.Add(new LineGeometry(new Point(r.X, r.Y + r.Height * i / 3), new Point(r.Right, r.Y + r.Height * i / 3)));
            }
            F<System.Windows.Shapes.Path>("CropLines").Data = lines;
            for (int i = 0; i < 8; i++) // corners first, then the middles of the sides
            {
                double hx = i < 4 ? (i % 2 == 0 ? r.Left : r.Right) : i == 4 || i == 5 ? r.Left + r.Width / 2 : i == 6 ? r.Left : r.Right;
                double hy = i < 4 ? (i < 2 ? r.Top : r.Bottom) : i == 4 ? r.Top : i == 5 ? r.Bottom : r.Top + r.Height / 2;
                Canvas.SetLeft(cropHandles[i], hx - 6);
                Canvas.SetTop(cropHandles[i], hy - 6);
                bool room = i < 4 || (i < 6 ? r.Width : r.Height) >= 56; // side handles only where they don't crowd the corners
                cropHandles[i].Visibility = room ? Visibility.Visible : Visibility.Collapsed;
            }
            int w, h;
            CropPixels(out w, out h);
            F<TextBlock>("CropText").Text = (F<FrameworkElement>("CropBar").Width >= 520 ? "Crop: " : "") + w + " x " + h;
        }

        // What's under the mouse (a point on the crop layer): a corner, a side, the frame itself, or outside it.
        int CropZoneAt(Point p)
        {
            var r = new Rect(cropPic.X + cropX0 * cropPic.Width, cropPic.Y + cropY0 * cropPic.Height,
                             (cropX1 - cropX0) * cropPic.Width, (cropY1 - cropY0) * cropPic.Height);
            const double reach = 12;
            if (p.X < r.Left - reach || p.X > r.Right + reach || p.Y < r.Top - reach || p.Y > r.Bottom + reach) return 32;
            double dl = Math.Abs(p.X - r.Left), dr = Math.Abs(p.X - r.Right), dt = Math.Abs(p.Y - r.Top), db = Math.Abs(p.Y - r.Bottom);
            int zone = 0;
            if (dl <= reach && dl <= dr) zone |= 1; else if (dr <= reach) zone |= 4;
            if (dt <= reach && dt <= db) zone |= 2; else if (db <= reach) zone |= 8;
            return zone != 0 ? zone : 16;
        }

        static Cursor CropCursor(int zone)
        {
            switch (zone)
            {
                case 3: case 12: return Cursors.SizeNWSE;
                case 6: case 9: return Cursors.SizeNESW;
                case 1: case 4: return Cursors.SizeWE;
                case 2: case 8: return Cursors.SizeNS;
                case 16: return Cursors.SizeAll;
                default: return Cursors.Cross;
            }
        }

        void CropMouseDown(object sender, MouseButtonEventArgs e)
        {
            var layer = (Canvas)sender;
            e.Handled = true; // not a click on the picture under it
            if (!cropping || cropPic.IsEmpty || cropPic.Width < 4) return;
            var p = e.GetPosition(layer);
            CropTakeHold(CropZoneAt(p), new Point((p.X - cropPic.X) / cropPic.Width, (p.Y - cropPic.Y) / cropPic.Height));
            layer.CaptureMouse();
        }

        void CropTakeHold(int zone, Point at)
        {
            cropZone = zone;
            cropGrab = at;
            cropFrom = new[] { cropX0, cropY0, cropX1, cropY1 };
        }

        void CropMouseMove(object sender, MouseEventArgs e)
        {
            var layer = (Canvas)sender;
            if (!cropping || cropPic.IsEmpty || cropPic.Width < 4) return;
            var p = e.GetPosition(layer);
            if (cropZone == 0 || !layer.IsMouseCaptured) { layer.Cursor = CropCursor(CropZoneAt(p)); return; }
            CropDragTo(new Point((p.X - cropPic.X) / cropPic.Width, (p.Y - cropPic.Y) / cropPic.Height));
        }

        // Moves what the mouse holds to m (0..1 of the picture; it may be outside). Worked out in the picture's own
        // pixels, so a held shape (16:9, square...) is right in the saved file, not just on screen.
        void CropDragTo(Point m)
        {
            int fullW, fullH;
            DisplaySize(shown, out fullW, out fullH);
            if (fullW <= 0 || fullH <= 0 || cropFrom == null) return;
            double W = fullW, H = fullH, r = cropRatio;
            double mx = Math.Max(0, Math.Min(1, m.X)) * W, my = Math.Max(0, Math.Min(1, m.Y)) * H;
            double x0 = cropFrom[0] * W, y0 = cropFrom[1] * H, x1 = cropFrom[2] * W, y1 = cropFrom[3] * H;
            // No smaller than 24 pixels on screen or 16 of the picture's own.
            double min = Math.Min(Math.Min(W, H), Math.Max(16, 24 * W / Math.Max(1, cropPic.Width)));
            double minW = r > 0 ? Math.Max(min, min * r) : min, minH = r > 0 ? minW / r : min;
            bool L = (cropZone & 1) != 0, T = (cropZone & 2) != 0, R = (cropZone & 4) != 0, B = (cropZone & 8) != 0;
            if (cropZone == 16)
            {
                double dx = Math.Max(-x0, Math.Min(W - x1, (m.X - cropGrab.X) * W)), dy = Math.Max(-y0, Math.Min(H - y1, (m.Y - cropGrab.Y) * H));
                x0 += dx; x1 += dx; y0 += dy; y1 += dy;
            }
            else if (cropZone == 32 || (r > 0 && (L || R) && (T || B)))
            {
                // A corner (or a new frame): the opposite corner stays where it is.
                double ax, ay;
                if (cropZone == 32)
                {
                    ax = Math.Max(0, Math.Min(1, cropGrab.X)) * W;
                    ay = Math.Max(0, Math.Min(1, cropGrab.Y)) * H;
                    if (Math.Abs(mx - ax) * cropPic.Width / W < 4 && Math.Abs(my - ay) * cropPic.Height / H < 4) return; // just a click
                    L = mx < ax;
                    T = my < ay;
                }
                else { ax = L ? x1 : x0; ay = T ? y1 : y0; }
                double roomW = L ? ax : W - ax, roomH = T ? ay : H - ay;
                double w = Math.Abs(mx - ax), h = Math.Abs(my - ay);
                if (r > 0)
                {
                    w = Math.Min(Math.Max(Math.Max(w, h * r), minW), Math.Min(roomW, roomH * r));
                    h = w / r;
                }
                else
                {
                    w = Math.Min(Math.Max(w, minW), roomW);
                    h = Math.Min(Math.Max(h, minH), roomH);
                }
                x0 = L ? ax - w : ax; x1 = x0 + w;
                y0 = T ? ay - h : ay; y1 = y0 + h;
            }
            else if (r <= 0)
            {
                if (L) x0 = Math.Max(0, Math.Min(mx, x1 - min));
                if (R) x1 = Math.Min(W, Math.Max(mx, x0 + min));
                if (T) y0 = Math.Max(0, Math.Min(my, y1 - min));
                if (B) y1 = Math.Min(H, Math.Max(my, y0 + min));
            }
            else if (L || R)
            {
                // A side with a held shape: the opposite side stays, the height follows around the middle.
                double ax = L ? x1 : x0, cy = (y0 + y1) / 2;
                double w = Math.Min(Math.Max(L ? ax - mx : mx - ax, minW), Math.Min(L ? ax : W - ax, 2 * Math.Min(cy, H - cy) * r));
                double h = w / r;
                x0 = L ? ax - w : ax; x1 = x0 + w;
                y0 = cy - h / 2; y1 = cy + h / 2;
            }
            else
            {
                double ay = T ? y1 : y0, cx = (x0 + x1) / 2;
                double h = Math.Min(Math.Max(T ? ay - my : my - ay, minH), Math.Min(T ? ay : H - ay, 2 * Math.Min(cx, W - cx) / r));
                double w = h * r;
                y0 = T ? ay - h : ay; y1 = y0 + h;
                x0 = cx - w / 2; x1 = cx + w / 2;
            }
            cropX0 = Math.Max(0, x0 / W);
            cropY0 = Math.Max(0, y0 / H);
            cropX1 = Math.Min(1, x1 / W);
            cropY1 = Math.Min(1, y1 / H);
            PlaceCrop();
        }

        void ShowCropShapeMenu()
        {
            int w, h;
            DisplaySize(shown, out w, out h);
            string now = F<TextBlock>("CropRatioText").Text;
            Func<string, double, Control> shape = (name, ratio) => Item(name, now == name ? "" : null, delegate { SetCropShape(name, ratio); });
            ShowMenu("CropRatioBtn", PlacementMode.Top,
                shape("Any shape", 0), shape("Original", h > 0 ? (double)w / h : 0), shape("Square", 1),
                shape("16:9", 16 / 9.0), shape("9:16", 9 / 16.0), shape("4:3", 4 / 3.0), shape("3:4", 3 / 4.0));
        }

        // Holds a width:height from now on; the frame takes that shape around its middle, as big as fits in it.
        void SetCropShape(string name, double ratio)
        {
            cropRatio = ratio;
            F<TextBlock>("CropRatioText").Text = name;
            int fullW, fullH;
            DisplaySize(shown, out fullW, out fullH);
            if (ratio > 0 && fullW > 0 && fullH > 0)
            {
                double w = (cropX1 - cropX0) * fullW, h = (cropY1 - cropY0) * fullH;
                double cx = (cropX0 + cropX1) / 2 * fullW, cy = (cropY0 + cropY1) / 2 * fullH;
                w = Math.Min(w, h * ratio);
                h = w / ratio;
                cropX0 = (cx - w / 2) / fullW;
                cropX1 = (cx + w / 2) / fullW;
                cropY0 = (cy - h / 2) / fullH;
                cropY1 = (cy + h / 2) / fullH;
            }
            PlaceCrop();
        }

        // Keys while cropping (before the normal shortcuts): Enter saves, Esc leaves; a video can still be played
        // and stepped through to check the frame against other moments. Everything else waits.
        bool HandleCropKey(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { EndCrop(); return true; }
            if (key == Key.Enter) { SaveCrop(F<FrameworkElement>("CropSaveBtn").Visibility != Visibility.Visible); return true; }
            Shortcut sc;
            if (!shortcutByKey.TryGetValue(Code(key, Keyboard.Modifiers), out sc)) return false;
            switch (sc.Id)
            {
                case "crop": EndCrop(); return true;
                case "play": case "mute": case "volumeUp": case "volumeDown": case "skipBack": case "skipForward":
                case "back5": case "forward5": case "frameBack": case "frameForward": case "close":
                    return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- saving

        void SaveCrop(bool copy)
        {
            var s = shown;
            if (!cropping || s == null || s.File == null) return;
            if (cropX0 < 0.002 && cropY0 < 0.002 && cropX1 > 0.998 && cropY1 > 0.998)
            {
                ShowToast("This is still the whole picture: drag the frame's corners first.", null, null, 4, null);
                return;
            }
            var part = new[] { cropX0, cropY0, cropX1 - cropX0, cropY1 - cropY0 };
            string file = s.File, ext = Path.GetExtension(file).ToLowerInvariant();
            bool video = s.IsVideo;
            string saveExt = video ? (ext == ".m4v" ? ext : ".mp4") : CropSaveType(ext);
            if (saveExt != ext) copy = true; // Windows can't write this type: a copy in a type it can
            if (video && converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            string target = file;
            if (copy)
            {
                if (video && playing) TogglePlay();
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save a cropped copy",
                    FileName = Path.GetFileNameWithoutExtension(file) + " (cropped)" + saveExt,
                    InitialDirectory = Path.GetDirectoryName(file),
                    DefaultExt = saveExt,
                    Filter = saveExt.TrimStart('.').ToUpperInvariant() + " file|*" + saveExt
                };
                if (dlg.ShowDialog(this) != true) return;
                if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    ShowToast("Pick a different name for the copy (Save is what replaces the original).", null, null, 5, null);
                    return;
                }
                target = dlg.FileName;
            }
            EndCrop();
            if (video) CropVideoFile(file, target, part);
            else CropPhotoFile(file, target, saveExt, part);
        }

        void CropPhotoFile(string file, string target, string saveExt, double[] part)
        {
            int turn;
            rotations.TryGetValue(file, out turn); // saved the way it's shown
            bool replace = string.Equals(file, target, StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(file);
            ShowToast("Cropping " + name + "...", null, null, 30, "crop");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null, size = null, temp = target + ".fvcrop.tmp";
                try
                {
                    size = CropPhoto(file, temp, saveExt, part, turn);
                    if (replace) SwapInFile(file, temp, ".fvcrop.old", true); // the original goes to the Recycle Bin
                    else
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(temp, target);
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (error != null)
                    {
                        lastCropResult = "failed: " + error;
                        ShowToast("Couldn't crop " + name + ": " + error, null, null, 6, "crop");
                        return;
                    }
                    lastCropResult = "ok " + size;
                    if (replace)
                    {
                        rotations.Remove(file); // the turn is in the saved picture now
                        TurnMemory.Set(file, null);
                        ShowSavedPhoto(file);
                        Thumbs.RebuildCachedThumbnails(file, delegate { Dispatcher.BeginInvoke(new Action(delegate { RefreshThumbnail(file); })); });
                        ShowToast("Cropped to " + size + ". The original is in the Recycle Bin.", null, null, 5, "crop");
                    }
                    else
                        ShowToast("Saved " + Path.GetFileName(target) + " (" + size + ")", "Show in folder", delegate
                        {
                            Process.Start("explorer.exe", "/select,\"" + target + "\"");
                            HideToast(null);
                        }, 8, "crop");
                }));
            });
        }

        // The players holding this photo show the file as it is now.
        void ShowSavedPhoto(string file)
        {
            try
            {
                foreach (var s in slots)
                {
                    if (s == null || s.IsVideo || s.Failed || !s.Ready || !string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                    s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                    s.Rotation.Angle = 0;
                    ResetZoom(s);
                    int fullWidth, fullHeight;
                    var picture = DecodeImage(file, Math.Min(ScreenSide, MaxDecodeSide), out fullWidth, out fullHeight);
                    s.Picture.Source = picture;
                    s.FullWidth = fullWidth;
                    s.FullHeight = fullHeight;
                    s.Reduced = Math.Max(picture.PixelWidth, picture.PixelHeight) < Math.Min(MaxDecodeSide, Math.Max(fullWidth, fullHeight));
                }
            }
            catch { }
            UpdateZoomUi();
            UpdateFacts();
            UpdateScrim();
        }

        // Writes the part (x, y, width, height, each 0..1 of the photo as shown, turn included) to temp as a file of
        // type ext, then reads it back to check it. Returns its size, "1200 x 800".
        internal static string CropPhoto(string file, string temp, string ext, double[] part, int turn, bool onWhite = false)
        {
            BitmapEncoder encoder;
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".jfif": case ".jpe": encoder = new JpegBitmapEncoder { QualityLevel = 95 }; break;
                case ".png": encoder = new PngBitmapEncoder(); break;
                case ".bmp": encoder = new BmpBitmapEncoder(); break;
                case ".tif": case ".tiff": encoder = new TiffBitmapEncoder(); break;
                default: throw new Exception("Windows can't save this file type.");
            }
            int w, h;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                BitmapSource picture = frame;
                // Windows' WebP (and some other add-on) decoders don't name their pixel format; asked for colours
                // plus see-through-ness they give both, where otherwise the see-through parts would come out black.
                if (picture.Format == PixelFormats.Default || picture.Format.ToString() == "Default") picture = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
                BitmapMetadata meta = null;
                int orientation = 1;
                try
                {
                    var m = frame.Metadata as BitmapMetadata;
                    object q = m == null ? null : m.GetQuery("System.Photo.Orientation");
                    if (q != null) orientation = Convert.ToInt32(q);
                    if (m != null && encoder is JpegBitmapEncoder && Path.GetExtension(file).ToLowerInvariant() == ext)
                    {
                        // Date taken, camera, place... stay; the picture is stored upright, and the small preview
                        // inside the file (still of the whole photo) goes.
                        meta = m.Clone();
                        try { meta.SetQuery("System.Photo.Orientation", (ushort)1); } catch { }
                        try { meta.RemoveQuery("/app1/thumb/"); } catch { }
                    }
                }
                catch { meta = null; }
                Transform t = null; // as when showing it (DecodeImage)
                switch (orientation)
                {
                    case 2: t = new ScaleTransform(-1, 1); break;
                    case 3: t = new RotateTransform(180); break;
                    case 4: t = new ScaleTransform(1, -1); break;
                    case 5: t = Group(new RotateTransform(90), new ScaleTransform(-1, 1)); break;
                    case 6: t = new RotateTransform(90); break;
                    case 7: t = Group(new RotateTransform(270), new ScaleTransform(-1, 1)); break;
                    case 8: t = new RotateTransform(270); break;
                }
                if (t != null) picture = new TransformedBitmap(picture, t);
                if (turn != 0) picture = new TransformedBitmap(picture, new RotateTransform(turn));
                int fullW = picture.PixelWidth, fullH = picture.PixelHeight;
                int x = Math.Max(0, Math.Min(fullW - 1, (int)Math.Round(part[0] * fullW)));
                int y = Math.Max(0, Math.Min(fullH - 1, (int)Math.Round(part[1] * fullH)));
                w = Math.Max(1, Math.Min(fullW - x, (int)Math.Round(part[2] * fullW)));
                h = Math.Max(1, Math.Min(fullH - y, (int)Math.Round(part[3] * fullH)));
                BitmapSource cut = new CachedBitmap(new CroppedBitmap(picture, new Int32Rect(x, y, w, h)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (onWhite && (cut.Format == PixelFormats.Bgra32 || cut.Format == PixelFormats.Pbgra32 || cut.Format == PixelFormats.Rgba64 || cut.Format == PixelFormats.Prgba64 ||
                                cut.Format == PixelFormats.Rgba128Float || cut.Format == PixelFormats.Prgba128Float || cut.Palette != null))
                {
                    // See-through parts on white (Convert to JPG): with the colours stored times their see-through-ness,
                    // putting a pixel on white is just adding what's missing to each colour.
                    var pre = new FormatConvertedBitmap(cut, PixelFormats.Pbgra32, null, 0);
                    int stride = w * 4;
                    var px = new byte[stride * h];
                    pre.CopyPixels(px, stride, 0);
                    for (int i = 0; i < px.Length; i += 4)
                    {
                        int rest = 255 - px[i + 3];
                        if (rest == 0) continue;
                        px[i] = (byte)Math.Min(255, px[i] + rest); px[i + 1] = (byte)Math.Min(255, px[i + 1] + rest); px[i + 2] = (byte)Math.Min(255, px[i + 2] + rest); px[i + 3] = 255;
                    }
                    cut = BitmapSource.Create(w, h, cut.DpiX, cut.DpiY, PixelFormats.Bgr32, null, px, stride);
                }
                System.Collections.ObjectModel.ReadOnlyCollection<ColorContext> colour = null;
                try { colour = frame.ColorContexts; } catch { }
                BitmapFrame made;
                try { made = BitmapFrame.Create(cut, null, meta, colour); }
                catch { made = BitmapFrame.Create(cut); } // details Windows can't write back: the picture matters more
                encoder.Frames.Add(made);
                try
                {
                    using (var output = File.Create(temp)) encoder.Save(output);
                }
                catch
                {
                    if (meta == null && colour == null) throw;
                    BitmapEncoder plain = encoder is JpegBitmapEncoder ? new JpegBitmapEncoder { QualityLevel = 95 } : (BitmapEncoder)Activator.CreateInstance(encoder.GetType());
                    plain.Frames.Add(BitmapFrame.Create(cut));
                    using (var output = File.Create(temp)) plain.Save(output);
                }
            }
            using (var fs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var check = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0];
                if (check.PixelWidth != w || check.PixelHeight != h) throw new IOException("the new file didn't check out");
            }
            return w + " x " + h;
        }

        void CropVideoFile(string file, string target, double[] part)
        {
            if (converting) { ShowToast("Already saving a video, one moment...", null, null, 3, null); return; }
            int turn;
            rotations.TryGetValue(file, out turn); // saved the way it's shown
            bool replace = string.Equals(file, target, StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(file), output = replace ? file + ".fvcrop.tmp" : target, made = "";
            var held = new List<Tuple<Slot, TimeSpan, bool>>();
            RunJob("Cropping " + name, delegate(Action<string, double> progress, CancelFlag cancel)
            {
                int sw, sh;
                double seconds;
                bool probed = VideoConverter.Probe(file, out sw, out sh, out seconds) && seconds > 0;
                var o = new ConvertOptions { Crop = part };
                if (probed)
                {
                    // As many bits as the original spent on this part of the picture, and never fewer than a good copy needs.
                    double frames = 1 / Math.Max(1.0 / 240, VideoInfo.FrameSeconds(file));
                    double usual = Math.Max(2e6, sw * (double)sh * part[2] * part[3] * frames * 0.12);
                    o.VideoBitrate = (int)Math.Min(40e6, Math.Max(usual, new FileInfo(file).Length * 8.0 / seconds * part[2] * part[3]));
                }
                VideoConverter.Convert(file, output, null, null, turn, p => progress(null, p), cancel, o);
                if (cancel.Cancelled) return;
                made = o.OutWidth + " x " + o.OutHeight;
                // Checked before it takes the original's place: Windows reads it, the size is right, nothing is cut short.
                int nw, nh;
                double ns;
                if (!VideoConverter.Probe(output, out nw, out nh, out ns) || nw < o.OutWidth || nw >= o.OutWidth + 16 || nh < o.OutHeight || nh >= o.OutHeight + 16
                    || (probed && Math.Abs(ns - seconds) > Math.Max(0.5, seconds * 0.02)))
                    throw new IOException("the new video didn't check out (" + nw + " x " + nh + ", " + ns.ToString("0.0") + " s)");
                if (!replace) return;
                Dispatcher.Invoke(new Action(delegate // the players let go of the file
                {
                    foreach (var s in slots)
                    {
                        if (s == null || !string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                        held.Add(Tuple.Create(s, s.Video.Position, s == shown && playing));
                        s.Video.Stop();
                        s.Video.Source = null;
                    }
                }));
                Thread.CurrentThread.IsBackground = false; // never quit halfway through the swap
                try { SwapInFile(file, output, ".fvcrop.old", true); } // the original goes to the Recycle Bin
                finally { Thread.CurrentThread.IsBackground = true; }
            }, delegate(string error, bool cancelled)
            {
                bool ok = error == null && !cancelled;
                if (replace || !ok) try { if (File.Exists(output)) File.Delete(output); } catch { }
                if (ok && replace)
                {
                    rotations.Remove(file); // the new file is stored upright
                    TurnMemory.Set(file, null);
                }
                foreach (var h in held) // the players take it back where they were
                {
                    var s = h.Item1;
                    if (!string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ok)
                    {
                        s.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                        s.Rotation.Angle = 0;
                    }
                    ++s.Token;
                    s.Ready = false;
                    s.SeekOnOpen = h.Item2;
                    s.Video.IsMuted = true;
                    s.Video.Source = new Uri(file);
                    s.Video.Pause();
                    if (h.Item3 && s == shown)
                    {
                        s.Video.Volume = volume;
                        s.Video.IsMuted = muted;
                        s.Video.Play();
                        s.Video.SpeedRatio = speed;
                    }
                }
                if (!ok)
                {
                    lastCropResult = cancelled ? "cancelled" : "failed: " + error;
                    ShowToast(cancelled ? "Cancelled." : "Couldn't crop " + name + ": " + error, null, null, 6, null);
                    return;
                }
                lastCropResult = "ok " + made;
                if (replace)
                {
                    Thumbs.RebuildCachedThumbnails(file, delegate { Dispatcher.BeginInvoke(new Action(delegate { RefreshThumbnail(file); })); });
                    ShowToast("Cropped to " + made + ". The original is in the Recycle Bin.", null, null, 5, null);
                }
                else
                    ShowToast("Saved " + Path.GetFileName(target) + " (" + made + ")", "Show in folder", delegate
                    {
                        Process.Start("explorer.exe", "/select,\"" + target + "\"");
                        HideToast(null);
                    }, 8, null);
                UpdateControls();
                UpdateFacts();
            });
        }
    }
}
