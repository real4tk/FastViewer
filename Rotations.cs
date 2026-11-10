// FastViewer: remembers how you turned photos and videos, across restarts and updates (rotations.txt),
// and saves a video's turn into the MP4/MOV itself ("Also save it to the file").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace FastViewer
{
    // rotations.txt in the settings folder, one line per file: "degrees|size in bytes|path", most recent last.
    // The size makes sure a different file later saved under the same name doesn't inherit the turn.
    // Shared by every FastViewer window: each change is merged into what's in the file at that moment.
    static class TurnMemory
    {
        public static string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastViewer", "rotations.txt");
        public static bool Overridden; // --rotations <file> (tests); other test runs keep turns in memory only
        const int Keep = 1000;

        static readonly Dictionary<string, KeyValuePair<int, long>> known = new Dictionary<string, KeyValuePair<int, long>>(StringComparer.OrdinalIgnoreCase);
        static DateTime readAt;
        static readonly System.Diagnostics.Stopwatch sinceCheck = System.Diagnostics.Stopwatch.StartNew();

        static List<string[]> ReadLines()
        {
            var list = new List<string[]>();
            if (Path == null || !File.Exists(Path)) return list;
            foreach (var line in File.ReadAllLines(Path))
            {
                var p = line.Split(new[] { '|' }, 3);
                int deg; long size;
                if (p.Length == 3 && int.TryParse(p[0], out deg) && long.TryParse(p[1], out size) && p[2].Length > 0) list.Add(p);
            }
            return list;
        }

        static void Reload()
        {
            try
            {
                // Asked on every file switch: look at the file at most once a second (another window may have changed it).
                if (Path == null || (readAt != DateTime.MinValue && sinceCheck.ElapsedMilliseconds < 1000)) return;
                sinceCheck.Restart();
                if (!File.Exists(Path)) return;
                var t = File.GetLastWriteTimeUtc(Path);
                if (t == readAt) return;
                readAt = t;
                known.Clear();
                foreach (var p in ReadLines()) known[p[2]] = new KeyValuePair<int, long>(int.Parse(p[0]), long.Parse(p[1]));
            }
            catch { }
        }

        static long SizeOf(string file) { try { return new FileInfo(file).Length; } catch { return -1; } }

        // The turn remembered for this file (clockwise degrees), or null.
        public static int? Get(string file)
        {
            if (file == null) return null;
            Reload();
            KeyValuePair<int, long> v;
            if (!known.TryGetValue(file, out v) || v.Value != SizeOf(file)) return null;
            return v.Key;
        }

        // degrees null = forget it.
        public static void Set(string file, int? degrees)
        {
            if (file == null) return;
            long size = SizeOf(file);
            if (degrees == null) known.Remove(file);
            else known[file] = new KeyValuePair<int, long>(degrees.Value, size);
            if (Path == null) return;
            try
            {
                var list = ReadLines();
                list.RemoveAll(p => string.Equals(p[2], file, StringComparison.OrdinalIgnoreCase));
                if (degrees == null && list.Count == 0 && !File.Exists(Path)) return;
                if (degrees != null) list.Add(new[] { degrees.Value.ToString(), size.ToString(), file });
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllLines(Path, list.Skip(Math.Max(0, list.Count - Keep)).Select(p => p[0] + "|" + p[1] + "|" + p[2]));
                readAt = DateTime.MinValue;
            }
            catch { }
        }
    }

    partial class ViewerWindow
    {
        // ---------------------------------------------------------------- save a video's turn into the file

        internal string lastVideoTurnResult; // tests
        readonly HashSet<string> videoTurnsSaving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool CanTurnInFile(string file)
        {
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            return ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".3gp" || ext == ".3g2";
        }

        // Writes the turn shown now into the video's track header (its "display matrix", which phones use too).
        // Nothing else in the file changes: no re-encoding, the picture and sound are copied byte for byte.
        // Like Change cover: written to a copy, checked, then swapped in; the players let go of it meanwhile.
        void SaveVideoTurnToFile(string file)
        {
            int turn;
            if (file == null || !rotations.TryGetValue(file, out turn)) return;
            string name = System.IO.Path.GetFileName(file);
            if (!CanTurnInFile(file)) { lastVideoTurnResult = "failed: type"; ShowToast("Saving the turn into the file works for MP4 and MOV videos. The turn still shows in FastViewer.", null, null, 5, null); return; }
            if (VideoInfo.Rotation(file) == turn) { TurnMemory.Set(file, null); return; }
            if (!videoTurnsSaving.Add(file)) return; // one at a time per file; the last turn is saved after it
            ShowToast("Saving the turn of " + name + "...", null, null, 30, "vturn");
            var held = new List<Tuple<Slot, TimeSpan, bool>>();
            foreach (var s in slots)
            {
                if (s == null || !string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
                held.Add(Tuple.Create(s, s.Video.Position, s == shown && playing));
                s.Video.Stop();
                s.Video.Source = null;
            }
            new Thread(delegate()
            {
                string error = null;
                try { WriteVideoTurn(file, turn); }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    videoTurnsSaving.Remove(file);
                    foreach (var h in held) // the players take it back where they were
                    {
                        var s = h.Item1;
                        if (!string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)) continue;
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
                    int now;
                    rotations.TryGetValue(file, out now);
                    if (error == null)
                    {
                        Thumbs.RebuildCachedThumbnails(file, delegate { Dispatcher.BeginInvoke(new Action(delegate { RefreshThumbnail(file); })); });
                        lastVideoTurnResult = "ok";
                        if (now != turn) { SaveVideoTurnToFile(file); return; } // turned again while saving
                        TurnMemory.Set(file, null); // the file says it now
                        ShowToast("Turn saved to " + name + " (no quality lost).", null, null, 3, "vturn");
                    }
                    else
                    {
                        lastVideoTurnResult = "failed: " + error;
                        ShowToast("Couldn't save the turn of " + name + ": " + error + " The turn still shows in FastViewer.", null, null, 6, "vturn");
                    }
                    UpdateControls();
                }));
            }) { IsBackground = false, Name = "Video turn save" }.Start(); // closing the window waits for it: never quit halfway through the swap
        }

        internal static void WriteVideoTurn(string file, int turn)
        {
            string temp = file + ".fvturn.tmp";
            try
            {
                long length;
                using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    length = src.Length;
                    src.CopyTo(dst, 1 << 20);
                    if (!VideoInfo.WriteTurn(dst, turn)) throw new IOException("this video has no track header to change");
                    dst.Flush(true);
                }
                if (new FileInfo(temp).Length != length || VideoInfo.Rotation(temp) != turn) throw new IOException("the new file didn't check out");
                SwapInFile(file, temp, ".fvturn.old");
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        // Puts temp in file's place with two renames (original -> backup name, temp -> original; rolled back if the
        // second fails). Creation time and attributes are kept; the date moves by 10 ms, so the file keeps its place
        // by date but Windows notices the change (its thumbnails are kept by file and date). Returns the 100 ms waits.
        internal static int SwapInFile(string file, string temp, string backupSuffix)
        {
            string old = file + backupSuffix;
            var created = File.GetCreationTimeUtc(file);
            var modified = File.GetLastWriteTimeUtc(file);
            var attributes = File.GetAttributes(file);
            if (File.Exists(old)) File.Delete(old);
            int attempt = 0;
            for (; ; attempt++) // right after Windows read the file (a thumbnail), renaming can wait a few seconds
            {
                try { File.Move(file, old); break; }
                catch (IOException) { if (attempt >= 50) throw; Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { if (attempt >= 50) throw; Thread.Sleep(100); }
            }
            try { File.Move(temp, file); }
            catch { File.Move(old, file); throw; }
            try { File.SetCreationTimeUtc(file, created); File.SetLastWriteTimeUtc(file, modified.AddMilliseconds(10)); File.SetAttributes(file, attributes); } catch { }
            try { File.Delete(old); } // (a player still closing it: it goes as soon as that's done)
            catch { try { File.SetAttributes(old, FileAttributes.Hidden); } catch { } }
            return attempt;
        }
    }
}
