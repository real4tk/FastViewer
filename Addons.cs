// FastViewer: optional one-time downloads (1.24.0). Two features need big helper programs that would make
// FastViewer.exe 100 times its size: automatic captions (Whisper) and smoother video (RIFE + FFmpeg). Each is
// an "add-on": downloaded once from its makers' own release pages when you ask for it (Settings > Videos, or
// the first time you use the feature), checked against a SHA-256 fingerprint built into FastViewer, kept in
// %LOCALAPPDATA%\FastViewer\<name>, and used offline from then on. Only the files needed are fetched: out of a
// large release zip just the wanted entries are read (the web server hands out any part of a file).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FastViewer
{
    class AddonPart
    {
        public string Url;      // a file, or a .zip
        public string Entry;    // zip: the one entry wanted (matched at the end of its path); null: the whole download
        public string[] Unpack; // whole zip: the file names to take out of it (into the folder of Save)
        public string Save;     // where it goes inside the add-on's folder
        public string Sha256;   // of the saved file (Entry / plain file) or of the zip (Unpack)
        public long Size;       // bytes that travel over the internet
    }

    class Addon
    {
        public string Id, Name;
        public AddonPart[] Parts;
        public string[] Needs;  // files that must be there for it to count as installed

        // Tests: --addons <folder> keeps every add-on in a scratch folder (and may point at local copies of the
        // downloads with --addon-source <folder>); any other test run has none installed and never downloads.
        public static string RootOverride, SourceOverride;
        public static bool TestRun, TrustSource = true;
        public static readonly List<string> Log = new List<string>();

        public string Dir
        {
            get
            {
                string root = RootOverride ?? (TestRun ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FastViewer", "no-addons")
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer"));
                return System.IO.Path.Combine(root, Id);
            }
        }

        public string File(string relative) { return System.IO.Path.Combine(Dir, relative); }
        public long Size { get { return Parts.Sum(p => p.Size); } }

        public bool Installed
        {
            get
            {
                try { return System.IO.File.Exists(File("installed.txt")) && Needs.All(n => System.IO.File.Exists(File(n))); }
                catch { return false; }
            }
        }

        // While downloading (shared by every card and message that shows it).
        public volatile bool Busy;
        public long Loaded;
        public string Error;
        public CancelFlag Cancel;
        public event Action Changed; // raised on the download thread

        void Raise() { var c = Changed; if (c != null) c(); }

        public void Install()
        {
            if (Busy) return;
            Busy = true;
            Error = null;
            Loaded = 0;
            Cancel = new CancelFlag();
            Raise();
            var thread = new Thread(delegate()
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    try { System.IO.File.Delete(File("installed.txt")); } catch { }
                    long before = 0;
                    foreach (var part in Parts)
                    {
                        long start = before;
                        Exception last = null;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try { Fetch(part, n => { Loaded = start + Math.Min(part.Size, n); Raise(); }); last = null; break; }
                            catch (OperationCanceledException) { throw; }
                            catch (InvalidDataException) { throw; } // the wrong contents won't get better by asking again
                            catch (Exception ex) { last = ex; Thread.Sleep(800); }
                        }
                        if (last != null) throw last;
                        before += part.Size;
                        Loaded = before;
                        Raise();
                    }
                    foreach (var n in Needs) if (!System.IO.File.Exists(File(n))) throw new InvalidDataException("a file is missing after downloading (" + n + ")");
                    System.IO.File.WriteAllText(File("installed.txt"), Name + " for FastViewer, downloaded " + DateTime.Now.ToString("yyyy-MM-dd") + "\r\n" +
                        string.Join("\r\n", Parts.Select(p => p.Url + (p.Entry != null ? " : " + p.Entry : ""))) + "\r\n");
                }
                catch (OperationCanceledException) { Error = null; TidyUp(); }
                catch (InvalidDataException ex) { Error = "The download didn't check out, so it wasn't used (" + ex.Message + ")."; TidyUp(); }
                catch (Exception ex) { Error = "Couldn't download it. Check the internet connection and try again. (" + Short(ex) + ")"; TidyUp(); }
                lock (Log) Log.Add(Id + ": " + (Installed ? "installed" : Cancel.Cancelled ? "cancelled" : "failed: " + Error));
                Busy = false;
                Raise();
            });
            thread.IsBackground = true;
            thread.Start();
        }

        static string Short(Exception ex)
        {
            var web = ex as WebException;
            if (web != null && web.Response is HttpWebResponse) return "the server answered " + (int)((HttpWebResponse)web.Response).StatusCode;
            return ex.Message.Length > 120 ? ex.Message.Substring(0, 120) + "..." : ex.Message;
        }

        void TidyUp()
        {
            try { foreach (var f in Directory.GetFiles(Dir, "*.part", SearchOption.AllDirectories)) System.IO.File.Delete(f); } catch { }
        }

        // Takes the add-on off this PC again (only what FastViewer downloaded into its own folder).
        public void Remove()
        {
            if (Busy) return;
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            Raise();
        }

        // ---------------------------------------------------------------- fetching

        void Fetch(AddonPart part, Action<long> progress)
        {
            string target = File(part.Save), temp = target + ".part";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            string hash;
            string local = SourceOverride == null ? null : System.IO.Path.Combine(SourceOverride, System.IO.Path.GetFileName(new Uri(part.Url).LocalPath));
            if (local != null && !System.IO.File.Exists(local)) throw new FileNotFoundException("test source missing: " + local);
            if (part.Entry != null)
            {
                Func<long, long, Stream> range = (from, to) => local != null ? (Stream)new Slice(local, from, to) : Web(part.Url, from, to);
                long total = local != null ? new FileInfo(local).Length : Length(part.Url);
                hash = ZipEntry(range, total, part.Entry, temp, progress);
            }
            else
            {
                using (var input = local != null ? System.IO.File.OpenRead(local) : Web(part.Url, -1, -1))
                using (var output = System.IO.File.Create(temp))
                    hash = Copy(input, output, long.MaxValue, progress);
            }
            if (part.Unpack == null)
            {
                Verify(part, hash);
                if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                System.IO.File.Move(temp, target);
                return;
            }
            Verify(part, hash);
            using (var zip = ZipFile.OpenRead(temp))
                foreach (var e in zip.Entries)
                {
                    if (!part.Unpack.Any(w => Like(e.Name, w))) continue;
                    string to = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target), e.Name); // (Name has no folders: nothing can land outside)
                    e.ExtractToFile(to, true);
                }
            System.IO.File.Delete(temp);
        }

        static bool Like(string name, string pattern)
        {
            return pattern.StartsWith("*") ? name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase) : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        void Verify(AddonPart part, string hash)
        {
            lock (Log) Log.Add(Id + ": " + (part.Entry ?? System.IO.Path.GetFileName(part.Save)) + " sha256 " + hash);
            if (SourceOverride != null && TrustSource) return; // tests with stand-in files
            if (string.IsNullOrEmpty(part.Sha256)) { if (TestRun) return; throw new InvalidDataException("no fingerprint to check it against"); }
            if (!hash.Equals(part.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("it isn't the file FastViewer expects");
        }

        string Copy(Stream input, Stream output, long limit, Action<long> progress)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var told = Stopwatch.StartNew();
                while (done < limit)
                {
                    if (Cancel.Cancelled) throw new OperationCanceledException();
                    int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - done));
                    if (n <= 0) break;
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    output.Write(buffer, 0, n);
                    done += n;
                    if (progress != null && told.ElapsedMilliseconds > 100) { told.Restart(); progress(done); }
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                if (progress != null) progress(done);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "FastViewer";
            r.Timeout = 20000;
            r.ReadWriteTimeout = 30000;
            r.AutomaticDecompression = DecompressionMethods.None;
            return r;
        }

        static Stream Web(string url, long from, long to)
        {
            var r = Request(url);
            if (from >= 0) r.AddRange(from, to);
            var response = (HttpWebResponse)r.GetResponse();
            if (from >= 0 && response.StatusCode != HttpStatusCode.PartialContent) { response.Close(); throw new IOException("the server doesn't hand out parts of the file"); }
            return response.GetResponseStream();
        }

        static long Length(string url)
        {
            var r = Request(url);
            r.AddRange(0, 0);
            using (var response = (HttpWebResponse)r.GetResponse())
            {
                string range = response.Headers["Content-Range"]; // "bytes 0-0/431540241"
                long total;
                if (range != null && long.TryParse(range.Substring(range.IndexOf('/') + 1), out total)) return total;
                throw new IOException("the server doesn't hand out parts of the file");
            }
        }

        // One entry out of a zip that is somewhere else: reads the zip's list of contents (at its end), then just
        // that entry's bytes, unpacking them as they arrive. Returns the SHA-256 of the unpacked file.
        string ZipEntry(Func<long, long, Stream> range, long total, string wanted, string target, Action<long> progress)
        {
            int tailSize = (int)Math.Min(total, 65557);
            var tail = ReadAll(range(total - tailSize, total - 1), tailSize);
            int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 5 && tail[i + 3] == 6) { end = i; break; }
            if (end < 0) throw new InvalidDataException("not a zip file");
            long listSize = BitConverter.ToUInt32(tail, end + 12), listAt = BitConverter.ToUInt32(tail, end + 16);
            if (listAt == 0xFFFFFFFF || listSize > 64 * 1024 * 1024) throw new InvalidDataException("a kind of zip FastViewer can't read");
            long tailAt = total - tailSize;
            byte[] list = listAt >= tailAt ? tail.Skip((int)(listAt - tailAt)).Take((int)listSize).ToArray() : ReadAll(range(listAt, listAt + listSize - 1), (int)listSize);
            for (int p = 0; p + 46 <= list.Length && BitConverter.ToUInt32(list, p) == 0x02014B50; )
            {
                int method = BitConverter.ToUInt16(list, p + 10), nameLength = BitConverter.ToUInt16(list, p + 28);
                long packed = BitConverter.ToUInt32(list, p + 20), at = BitConverter.ToUInt32(list, p + 42);
                string name = Encoding.UTF8.GetString(list, p + 46, nameLength);
                p += 46 + nameLength + BitConverter.ToUInt16(list, p + 30) + BitConverter.ToUInt16(list, p + 32);
                if (!(name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))) continue;
                if (method != 8 && method != 0) throw new InvalidDataException("packed in a way FastViewer can't read");
                var head = ReadAll(range(at, at + 29), 30);
                long dataAt = at + 30 + BitConverter.ToUInt16(head, 26) + BitConverter.ToUInt16(head, 28);
                using (var counted = new Counted(range(dataAt, dataAt + packed - 1), progress, Cancel))
                using (var output = System.IO.File.Create(target))
                using (var plain = method == 8 ? (Stream)new DeflateStream(counted, CompressionMode.Decompress) : counted)
                    return Copy(plain, output, long.MaxValue, null);
            }
            throw new InvalidDataException(wanted + " isn't in the download");
        }

        static byte[] ReadAll(Stream s, int count)
        {
            using (s)
            {
                var data = new byte[count];
                int got = 0, n;
                while (got < count && (n = s.Read(data, got, count - got)) > 0) got += n;
                if (got < count) throw new IOException("the download stopped early");
                return data;
            }
        }

        // Counts the bytes that came over the wire (progress is in download size, not unpacked size).
        class Counted : Stream
        {
            readonly Stream inner; readonly Action<long> progress; readonly CancelFlag cancel; long done; readonly Stopwatch told = Stopwatch.StartNew();
            public Counted(Stream inner, Action<long> progress, CancelFlag cancel) { this.inner = inner; this.progress = progress; this.cancel = cancel; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (cancel.Cancelled) throw new OperationCanceledException();
                int n = inner.Read(buffer, offset, count);
                done += n;
                if (progress != null && (told.ElapsedMilliseconds > 100 || n == 0)) { told.Restart(); progress(done); }
                return n;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { return done; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }

        // Tests: a part of a local file, read slowly enough for a progress bar to be seen moving.
        class Slice : Stream
        {
            readonly FileStream file; long left;
            public Slice(string path, long from, long to) { file = System.IO.File.OpenRead(path); file.Position = from; left = to - from + 1; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int n = file.Read(buffer, offset, (int)Math.Min(Math.Min(count, left), 1 << 18));
                left -= n;
                return n;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { return file.Position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        }
    }

    // Runs helper programs out of sight.
    static class Helper
    {
        public static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + p.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false });
                if (kill != null) kill.WaitForExit(5000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        // Hands each piece of the program's output (split at line ends and carriage returns, so progress that
        // rewrites its own line arrives too) to onText; onIdle runs about 4 times a second meanwhile.
        // Returns the exit code; throws OperationCanceledException when cancelled (the program is stopped).
        public static int Run(string exe, string arguments, Action<string> onText, CancelFlag cancel, bool utf8 = false, Action onIdle = null, bool gentle = false)
        {
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)
            };
            if (utf8) info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.UTF8;
            using (var p = Process.Start(info))
            {
                if (gentle) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // the PC stays pleasant to use meanwhile
                var threads = new List<Thread>();
                foreach (var reader in new[] { p.StandardOutput, p.StandardError })
                {
                    var r = reader;
                    var t = new Thread(delegate()
                    {
                        var piece = new StringBuilder();
                        try
                        {
                            int c;
                            while ((c = r.Read()) >= 0)
                            {
                                if (c != '\r' && c != '\n') { piece.Append((char)c); continue; }
                                if (piece.Length > 0) { string text = piece.ToString(); piece.Clear(); lock (info) onText(text); }
                            }
                            if (piece.Length > 0) lock (info) onText(piece.ToString());
                        }
                        catch { }
                    });
                    t.IsBackground = true;
                    t.Start();
                    threads.Add(t);
                }
                try { p.StandardInput.Close(); } catch { }
                while (!p.WaitForExit(250))
                {
                    if (cancel != null && cancel.Cancelled) { KillTree(p); throw new OperationCanceledException(); }
                    if (onIdle != null) onIdle();
                }
                foreach (var t in threads) t.Join(3000);
                return p.ExitCode;
            }
        }
    }

    partial class ViewerWindow
    {
        static string Megabytes(long bytes) { return Math.Max(1, Math.Round(bytes / 1048576.0)).ToString("0") + " MB"; }

        // A Settings card for an add-on: what it is, its size, one button (Download / Cancel / Remove), and a
        // line along the bottom that fills while it downloads. verdict: an extra line (e.g. about the graphics
        // card), or null; ownCopy: text shown instead of the button when a copy outside FastViewer is used.
        void AddAddonCard(Addon a, string glyph, string title, string description, Func<string> verdict, Func<string> ownCopy)
        {
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 12, 0), TextAlignment = TextAlignment.Right, MaxWidth = 220, TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var tick = new Glyph { Code = "", Size = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            tick.SetResourceReference(Glyph.ForegroundProperty, "Accent");
            var button = new Button { MinWidth = 120, Height = 32 };
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(tick);
            controls.Children.Add(status);
            controls.Children.Add(button);
            System.Windows.Automation.AutomationProperties.SetName(controls, title);
            var card = AddCard(glyph, title, description, controls);
            var grid = (Grid)card.Child;
            var texts = (StackPanel)grid.Children[1];
            var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
            texts.Children.Add(note);
            // The filling line: on the card's bottom edge, like the one under a saving message.
            var line = new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-8, 0, -8, -9), Visibility = Visibility.Collapsed, IsHitTestVisible = false, RenderTransform = new ScaleTransform(0, 1) };
            line.SetResourceReference(Shape.FillProperty, "Accent");
            Grid.SetColumnSpan(line, 3);
            grid.Children.Add(line);

            bool wasBusy = false;
            Action paint = delegate
            {
                string own = ownCopy == null ? null : ownCopy();
                string v = verdict == null ? null : verdict();
                bool busy = a.Busy, ready = own != null || a.Installed;
                note.Text = a.Error ?? v ?? "";
                note.Visibility = note.Text.Length > 0 && (!ready || a.Error != null) && !busy ? Visibility.Visible : Visibility.Collapsed;
                tick.Visibility = ready && !busy ? Visibility.Visible : Visibility.Collapsed;
                status.Text = busy ? Megabytes(a.Loaded) + " of " + Megabytes(a.Size) : own ?? (ready ? "Ready, works offline" : "");
                button.Visibility = own != null && !busy ? Visibility.Collapsed : Visibility.Visible;
                button.Content = busy ? "Cancel" : ready ? "Remove" : "Download (" + Megabytes(a.Size) + ")";
                button.SetResourceReference(StyleProperty, busy || ready ? "Outlined" : "AccentButton");
                System.Windows.Automation.AutomationProperties.SetName(button, (busy ? "Cancel downloading " : ready ? "Remove " : "Download ") + title);
                var fill = (ScaleTransform)line.RenderTransform;
                if (busy)
                {
                    if (!wasBusy) { Motion.Set(fill, ScaleTransform.ScaleXProperty, 0); Motion.Set(line, OpacityProperty, 1); }
                    line.Visibility = Visibility.Visible;
                    Motion.To(fill, ScaleTransform.ScaleXProperty, a.Size > 0 ? Math.Min(1, (double)a.Loaded / a.Size) : 0, Motion.Ms(200, false), Motion.Smooth);
                }
                else if (wasBusy)
                {
                    // Done: the line fills up and fades; stopped: it just fades.
                    if (a.Installed) Motion.To(fill, ScaleTransform.ScaleXProperty, 1, Motion.Ms(150, false), Motion.Smooth);
                    Motion.To(line, OpacityProperty, 0, Motion.Ms(400, false), Motion.Smooth, delegate { if (!a.Busy) line.Visibility = Visibility.Collapsed; });
                    if (a.Installed && Motion.Animates) Motion.Pulse(tick, 0.4);
                }
                wasBusy = busy;
            };
            button.Click += delegate
            {
                if (a.Busy) a.Cancel.Cancelled = true;
                else if (a.Installed) a.Remove();
                else a.Install();
            };
            a.Changed += delegate { Dispatcher.BeginInvoke(new Action(paint)); };
            // Looking at the disk and the graphics card waits until the card is on screen: starting FastViewer
            // (which builds these pages) must not get slower for it.
            settingsRefresh.Add(delegate { if (card.IsVisible) paint(); });
            card.IsVisibleChanged += delegate { if (card.IsVisible) paint(); };
            button.Content = "Download (" + Megabytes(a.Size) + ")";
            button.SetResourceReference(StyleProperty, "AccentButton");
        }

        // Used when a feature is asked for before its add-on is there: a message offers the download, shows its
        // progress, and then carries on with what was asked.
        void GetAddon(Addon a, string needs, string caution, Action then)
        {
            if (a.Installed) { then(); return; }
            if (a.Busy) { ShowToast("Still downloading, one moment...", null, null, 3, "info"); return; }
            ShowToast(needs + " a one-time download of " + Megabytes(a.Size) + "." + (caution == null ? "" : " " + caution), "Download", delegate
            {
                Action changed = null;
                var line = F<FrameworkElement>("ToastProgress");
                ShowToast("Downloading... 0%", "Cancel", delegate { a.Cancel.Cancelled = true; }, 0, "convert");
                Motion.Set((ScaleTransform)line.RenderTransform, ScaleTransform.ScaleXProperty, 0);
                line.Visibility = Visibility.Visible;
                changed = delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (a.Busy)
                        {
                            if (toastKind != "convert") return;
                            double part = a.Size > 0 ? Math.Min(1, (double)a.Loaded / a.Size) : 0;
                            F<TextBlock>("ToastText").Text = "Downloading... " + (int)(part * 100) + "% (" + Megabytes(a.Loaded) + " of " + Megabytes(a.Size) + ")";
                            Motion.To((ScaleTransform)line.RenderTransform, ScaleTransform.ScaleXProperty, part, Motion.Ms(200, false), Motion.Smooth);
                            return;
                        }
                        a.Changed -= changed;
                        if (a.Installed) { HideToast("convert"); then(); }
                        else if (a.Error != null) ShowToast(a.Error, null, null, 9, "error");
                        else ShowToast("Download cancelled.", null, null, 4, null);
                    }));
                };
                a.Changed += changed;
                a.Install();
            }, 12, "info");
        }
    }
}
