// FastViewer: music files (.mp3 .m4a .aac .wav .flac .wma). They play in the slot's video player (Slot.IsVideo is
// true too, so the control bar, speed, repeat, A-B loop and resume work as for videos) and show a card with the
// album cover (or a music note), the title and the artist. The card is ready at once, so switching never waits.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IOPath = System.IO.Path;

namespace FastViewer
{
    partial class ViewerWindow
    {
        const double AudioCardWidth = 400, AudioCardMaxHeight = 640;

        // knownCover: the picture just saved into the file (Windows may still have the old one cached for a moment).
        void LoadAudio(Slot s, string file, int token, BitmapSource knownCover = null)
        {
            s.AudioCover = knownCover;
            s.AudioTitle = IOPath.GetFileNameWithoutExtension(file);
            s.AudioArtist = null;
            s.Picture.Source = AudioCard(s);
            s.Picture.Visibility = Visibility.Visible;
            s.Picture.MaxHeight = AudioCardMaxHeight;
            s.Picture.Margin = new Thickness(0, 16, 0, 96); // clear of the video bar floating over the bottom
            s.Video.ScrubbingEnabled = false; // with it on, a file without pictures never finishes opening
            s.Video.Visibility = Visibility.Visible; // draws nothing (no picture in the file) but has to stay in the window to play
            s.Video.IsMuted = true;
            s.Video.Source = new Uri(file);
            s.Video.Pause();
            s.FrameSeconds = 1 / 30.0;
            s.Ready = true; // shown straight away: there's no first frame to wait for

            // The cover and the tags come from Windows (the same as File Explorer shows), on a thread of their own.
            var t = new Thread(delegate()
            {
                BitmapSource cover = knownCover ?? Thumbs.Make(file, 600);
                string title, artist;
                ReadMusicTags(file, out title, out artist);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (s.Token != token) return; // the slot holds another file by now
                    s.AudioCover = cover;
                    if (!string.IsNullOrWhiteSpace(title)) s.AudioTitle = title.Trim();
                    s.AudioArtist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
                    s.Picture.Source = AudioCard(s);
                }));
            }) { IsBackground = true, Name = "Music tags" };
            t.SetApartmentState(ApartmentState.STA); // the Windows shell needs this
            t.Start();
        }

        // Look changed: the cards are drawn in the look's colours and font, so draw them again.
        void RefreshAudioCards()
        {
            if (slots == null) return;
            foreach (var s in slots) if (s != null && s.IsAudio && s.File != null) s.Picture.Source = AudioCard(s);
        }

        Brush LookBrush(string key, Color fallback)
        {
            var b = TryFindResource(key) as Brush;
            return b ?? new SolidColorBrush(fallback);
        }

        // A drawing, not a bitmap: it costs nothing to make and stays sharp at any size.
        DrawingImage AudioCard(Slot s)
        {
            double w = AudioCardWidth, side = w;
            var font = TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
            var main = LookBrush("TextMain", Colors.White);
            var dim = LookBrush("TextSecondary", Color.FromRgb(200, 200, 200));
            double ppd = 1;
            try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }

            var g = new DrawingGroup();
            using (var dc = g.Open())
            {
                var square = new Rect(0, 0, side, side);
                var round = new RectangleGeometry(square, 14, 14);
                if (s.AudioCover != null)
                {
                    dc.PushClip(round);
                    dc.DrawImage(s.AudioCover, square);
                    dc.Pop();
                }
                else
                {
                    dc.DrawGeometry(LookBrush("ControlFill", Color.FromArgb(40, 255, 255, 255)), null, round);
                    var note = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe MDL2 Assets"), 128, dim, ppd);
                    dc.DrawText(note, new Point((side - note.Width) / 2, (side - note.Height) / 2));
                }
                double y = side + 22;
                var title = new FormattedText(s.AudioTitle ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 22, main, ppd)
                    { MaxTextWidth = w, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
                dc.DrawText(title, new Point(0, y));
                y += title.Height + 4;
                if (s.AudioArtist != null)
                {
                    var artist = new FormattedText(s.AudioArtist, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16, dim, ppd)
                        { MaxTextWidth = w, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
                    dc.DrawText(artist, new Point(0, y));
                    y += artist.Height;
                }
                // Always the same width and a little room below, so the card's size never depends on the text.
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, Math.Max(y, side + 90) + 8));
            }
            g.Freeze();
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        // ---------------------------------------------------------------- changing a song's cover (MP3)
        // (right-click on the music view, or music "..." > Change cover...)

        string lastCoverResult; // tests: how the last cover change ended

        void OnStageRightUp(object sender, MouseButtonEventArgs e)
        {
            var s = shown;
            if (s == null || !s.IsAudio || s.Failed || trimming || settingsOpen || gridOpen) return; // anywhere on the music view (another layer sits over the cover itself)
            e.Handled = true;
            if (huntMenuHook != null) { Log("right-click on the cover: Change cover menu"); return; } // tests: never a real menu
            var menu = new ContextMenu { PlacementTarget = stage, Placement = PlacementMode.MousePoint };
            menu.Items.Add(Item("Change cover...", "\uE8D6", ChangeCover));
            openMenus++;
            menu.Closed += delegate { openMenus--; };
            menu.IsOpen = true;
        }

        static bool CanChangeCover(string file) { return file != null && IOPath.GetExtension(file).Equals(".mp3", StringComparison.OrdinalIgnoreCase); }

        void ChangeCover()
        {
            var s = shown;
            if (s == null || !s.IsAudio || s.Failed) return;
            if (!CanChangeCover(s.File))
            {
                ShowToast("Changing the cover works for MP3 files for now.", null, null, 4, "cover");
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick a picture for the cover",
                Filter = "Pictures|" + string.Join(";", ImageExts.Select(x => "*" + x)) + "|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            ChangeCoverTo(s.File, dlg.FileName);
        }

        // Saves picture as the song's front cover. The song is written to a temporary copy next to it first and only
        // swapped in once that copy checks out, so a failure never harms the original. The sound itself is copied
        // byte for byte; only the tag at the start of the file changes.
        void ChangeCoverTo(string file, string picture)
        {
            string name = IOPath.GetFileName(file);
            lastCoverResult = null;
            ShowToast("Saving the cover of " + name + "...", null, null, 30, "cover");
            var t = new Thread(delegate()
            {
                byte[] jpeg = null;
                BitmapSource cover = null;
                string error = null;
                try { jpeg = CoverJpeg(picture, out cover); }
                catch (Exception ex) { error = "Couldn't read that picture: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Log("cover picture ready");
                    if (error != null) { CoverDone(error, false); return; }
                    // Every player holding the song lets go of it (Windows won't swap a file that's open).
                    var held = new List<Tuple<Slot, TimeSpan, bool>>();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var s in slots)
                    {
                        if (s == null || !string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                        bool wasPlaying = s == shown && playing;
                        held.Add(Tuple.Create(s, s.Video.Position, wasPlaying));
                        s.Video.Stop();
                        s.Video.Source = null;
                    }
                    Log("cover: players let go in " + clock.ElapsedMilliseconds + " ms");
                    new Thread(delegate() // its own thread: the shared ones can be busy for seconds right after starting
                    {
                        string writeError = null;
                        try { long started = clock.ElapsedMilliseconds; int waits = WriteMp3Cover(file, jpeg); long took = clock.ElapsedMilliseconds; Dispatcher.BeginInvoke(new Action(delegate { Log("cover written: thread started at " + started + " ms, done at " + took + " ms, waited for the file " + waits + " x 100 ms, steps" + coverSteps); })); }
                        catch (Exception ex) { writeError = ex.Message; }
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            foreach (var h in held) // the players take the song back (with the new cover, or the old file)
                            {
                                var s = h.Item1;
                                if (!string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                                int token = ++s.Token;
                                s.SeekOnOpen = h.Item2;
                                LoadAudio(s, file, token, writeError == null ? cover : s.AudioCover);
                                if (h.Item3 && s == shown)
                                {
                                    s.Video.Volume = volume;
                                    s.Video.IsMuted = muted;
                                    s.Video.Play();
                                    s.Video.SpeedRatio = speed;
                                }
                            }
                            if (writeError == null)
                            {
                                Thumbs.RebuildCachedThumbnails(file, delegate { Dispatcher.BeginInvoke(new Action(delegate { RefreshThumbnail(file); })); }); // File Explorer: new thumbnail
                                CoverDone("Cover saved to " + name + ".", true);
                            }
                            else CoverDone("Couldn't save the cover of " + name + ": " + writeError, false);
                        }));
                    }) { IsBackground = true, Name = "Cover save" }.Start();
                }));
            }) { IsBackground = true, Name = "Cover" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void CoverDone(string message, bool ok)
        {
            lastCoverResult = (ok ? "ok: " : "failed: ") + message;
            Log("cover " + lastCoverResult);
            ShowToast(message, null, null, ok ? 3 : 6, "cover");
            UpdateControls();
        }

        // The picture as a JPEG of at most 1000 x 1000 (see-through parts on white), plus a copy to show right away.
        static byte[] CoverJpeg(string picture, out BitmapSource shownCover)
        {
            int w, h;
            BitmapSource src = DecodeImage(picture, 1000, out w, out h);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
                dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
            }
            var flat = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flat.Render(dv);
            var enc = new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(flat));
            byte[] jpeg;
            using (var ms = new MemoryStream()) { enc.Save(ms); jpeg = ms.ToArray(); }
            var copy = new CachedBitmap(flat, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            copy.Freeze();
            shownCover = copy;
            return jpeg;
        }

        // ID3v2 (the tag at the start of an MP3). Versions 2.3 and 2.4 are kept as they are, other frames untouched;
        // an untagged file gets a 2.3 tag. Unusual tags (2.2, whole-tag unsynchronisation, extended header) are left alone.
        internal static string coverSteps;
        internal static int WriteMp3Cover(string file, byte[] jpeg)
        {
            string temp = file + ".fvcover.tmp";
            var sw = System.Diagnostics.Stopwatch.StartNew(); coverSteps = "";
            try
            {
                long audioStart, length;
                int version;
                var kept = new MemoryStream();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    length = fs.Length;
                    ReadId3(fs, out version, out audioStart, (id, frame) => { if (id != "APIC") kept.Write(frame, 0, frame.Length); }, null);
                    if (version == 0) version = 3;
                    coverSteps += " read " + sw.ElapsedMilliseconds;

                    var apic = new MemoryStream();
                    apic.WriteByte(0); // text encoding: Latin-1
                    var mime = Encoding.ASCII.GetBytes("image/jpeg");
                    apic.Write(mime, 0, mime.Length); apic.WriteByte(0);
                    apic.WriteByte(3); // front cover
                    apic.WriteByte(0); // no description
                    apic.Write(jpeg, 0, jpeg.Length);
                    var body = apic.ToArray();

                    const int Padding = 2048; // room for later tag edits by other apps
                    long tagSize = kept.Length + 10 + body.Length + Padding;
                    if (tagSize >= 1 << 28) throw new InvalidOperationException("the picture is too big");
                    using (var outFs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        outFs.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', (byte)version, 0, 0 }, 0, 6);
                        outFs.Write(SyncSafe((int)tagSize), 0, 4);
                        kept.WriteTo(outFs);
                        outFs.Write(Encoding.ASCII.GetBytes("APIC"), 0, 4);
                        outFs.Write(version == 4 ? SyncSafe(body.Length) : BigEndian(body.Length), 0, 4);
                        outFs.Write(new byte[2], 0, 2);
                        outFs.Write(body, 0, body.Length);
                        outFs.Write(new byte[Padding], 0, Padding);
                        fs.Position = audioStart;
                        fs.CopyTo(outFs); // the sound, byte for byte
                        coverSteps += " copied " + sw.ElapsedMilliseconds;
                        outFs.Flush(true);
                        coverSteps += " flushed " + sw.ElapsedMilliseconds;
                    }
                    coverSteps += " closed " + sw.ElapsedMilliseconds;
                    // Check the copy before it replaces anything: the new cover is there and the sound is all there.
                    using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        int v; long start; byte[] found = null;
                        ReadId3(check, out v, out start, null, (id, frame) => { if (id == "APIC") found = frame; });
                        if (found == null || check.Length - start != length - audioStart || !EndsWith(found, jpeg))
                            throw new IOException("the new file didn't check out");
                    }
                }
                coverSteps += " checked " + sw.ElapsedMilliseconds;
                int attempt = SwapInFile(file, temp, ".fvcover.old"); // two renames; date + 10 ms (see Rotations.cs)
                coverSteps += " swapped " + sw.ElapsedMilliseconds;
                return attempt;
            }            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        // Walks an ID3v2 tag: each whole frame (header included) goes to keep, and (id, frame body) to look.
        // version 0 = no tag; audioStart = where the sound begins.
        internal static void ReadId3(Stream fs, out int version, out long audioStart, Action<string, byte[]> keep, Action<string, byte[]> look)
        {
            version = 0; audioStart = 0;
            fs.Position = 0;
            var head = new byte[10];
            if (fs.Read(head, 0, 10) < 10 || head[0] != 'I' || head[1] != 'D' || head[2] != '3') return;
            version = head[3];
            int flags = head[5];
            int size = FromSyncSafe(head, 6);
            if (version < 3 || version > 4) throw new NotSupportedException("this MP3 has an old kind of tag (ID3 2." + version + ")");
            if ((flags & 0xC0) != 0) throw new NotSupportedException("this MP3's tag is stored in an unusual way");
            audioStart = 10 + size + ((flags & 0x10) != 0 ? 10 : 0);
            var tag = new byte[size];
            int got = 0;
            while (got < size) { int n = fs.Read(tag, got, size - got); if (n <= 0) throw new IOException("the tag is cut off"); got += n; }
            int pos = 0;
            while (pos + 10 <= size && tag[pos] != 0)
            {
                string id = Encoding.ASCII.GetString(tag, pos, 4);
                int fsize = version == 4 ? FromSyncSafe(tag, pos + 4) : (tag[pos + 4] << 24 | tag[pos + 5] << 16 | tag[pos + 6] << 8 | tag[pos + 7]);
                if (fsize < 0 || pos + 10 + fsize > size) break;
                if (keep != null) { var whole = new byte[10 + fsize]; Array.Copy(tag, pos, whole, 0, whole.Length); keep(id, whole); }
                if (look != null) { var body = new byte[fsize]; Array.Copy(tag, pos + 10, body, 0, fsize); look(id, body); }
                pos += 10 + fsize;
            }
        }

        static byte[] SyncSafe(int n) { return new[] { (byte)(n >> 21 & 0x7F), (byte)(n >> 14 & 0x7F), (byte)(n >> 7 & 0x7F), (byte)(n & 0x7F) }; }
        static byte[] BigEndian(int n) { return new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }; }
        static int FromSyncSafe(byte[] b, int i) { return (b[i] & 0x7F) << 21 | (b[i + 1] & 0x7F) << 14 | (b[i + 2] & 0x7F) << 7 | (b[i + 3] & 0x7F); }

        static bool EndsWith(byte[] a, byte[] tail)
        {
            if (a.Length < tail.Length) return false;
            int off = a.Length - tail.Length;
            for (int i = 0; i < tail.Length; i++) if (a[off + i] != tail[i]) return false;
            return true;
        }

        // ---------------------------------------------------------------- tags from Windows' property system

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid Format; public int Id; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort Type, R1, R2, R3; public IntPtr A, B; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid riid, out IPropertyStore store);
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant value);

        static readonly Guid SummaryInfo = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"); // System.Title = 2
        static readonly Guid MusicInfo = new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6");   // System.Music.Artist = 2

        static void ReadMusicTags(string file, out string title, out string artist)
        {
            title = artist = null;
            IPropertyStore store = null;
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(file, IntPtr.Zero, 0, ref iid, out store) != 0 || store == null) return;
                title = ReadText(store, SummaryInfo, 2);
                artist = ReadText(store, MusicInfo, 2);
            }
            catch { }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        // A text property, or the first ones of a list of texts joined with ", " (artists are a list).
        static string ReadText(IPropertyStore store, Guid format, int id)
        {
            var key = new PropertyKey { Format = format, Id = id };
            PropVariant v;
            if (store.GetValue(ref key, out v) != 0) return null;
            try
            {
                const ushort Text = 31, TextList = 0x1000 | 31;
                if (v.Type == Text) return Marshal.PtrToStringUni(v.A);
                if (v.Type == TextList)
                {
                    int count = (int)(v.A.ToInt64() & 0xFFFFFFFF);
                    IntPtr list = v.B;
                    var parts = new string[Math.Min(count, 4)];
                    for (int i = 0; i < parts.Length; i++) parts[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                    return string.Join(", ", parts);
                }
                return null;
            }
            finally { PropVariantClear(ref v); }
        }
    }
}
