// FastViewer: notices new, renamed and deleted files in the open folder (a download finishing,
// a clip you just saved) and quietly updates the list, in the folder's order.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        FileSystemWatcher watcher;
        string watchedFolder;
        DispatcherTimer rescanTimer;

        void WatchFolder(string folder)
        {
            if (watcher != null && string.Equals(watchedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            StopWatching();
            watchedFolder = folder;
            try
            {
                watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
                FileSystemEventHandler changed = delegate { Dispatcher.BeginInvoke(new Action(ScheduleRescan)); };
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += delegate { Dispatcher.BeginInvoke(new Action(ScheduleRescan)); };
                watcher.EnableRaisingEvents = true;
            }
            catch { watcher = null; }
        }

        void StopWatching()
        {
            if (watcher == null) return;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }

        // Several changes at once (a download renaming its temp file, many files copied) are handled together.
        void ScheduleRescan()
        {
            if (rescanTimer == null)
            {
                rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                rescanTimer.Tick += delegate { rescanTimer.Stop(); RescanFolder(); };
            }
            rescanTimer.Stop();
            rescanTimer.Start();
        }

        void RescanFolder()
        {
            string folder = watchedFolder, current = CurrentFile;
            if (folder == null || !Directory.Exists(folder)) return;
            List<string> list;
            try
            {
                list = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(fi => (fi.Attributes & FileAttributes.Hidden) == 0)
                    .Select(fi => fi.FullName)
                    .Where(f => PassesFilter(f) || string.Equals(f, current, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch { return; }
            list.Sort((a, b) => Ui.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
            ApplyOrder(list);
            if (order == 0 && !shuffled)
            {
                // "Same as File Explorer": until Explorer answers, the files already known keep the order they had (it
                // was Explorer's); new ones go in by name. Before 1.27 the list fell back to name order for a moment.
                var present = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                var kept = files.Where(present.Contains).ToList();
                var known = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
                foreach (var f in list) if (!known.Contains(f)) kept.Add(f);
                list = kept;
            }
            ApplyRescan(list, current);
            if (order != 0) return;

            // Same order as File Explorer: ask Explorer in the background (it takes a moment).
            int token = openToken;
            var snapshot = new List<string>(list);
            var thread = new Thread(delegate()
            {
                List<string> sorted = null;
                try { sorted = ExplorerOrder.Sort(folder, snapshot); }
                catch { }
                if (sorted == null) return;
                sorted = sorted.Where(File.Exists).ToList(); // (a file deleted while Explorer was asked doesn't come back)
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (token == openToken && string.Equals(folder, watchedFolder, StringComparison.OrdinalIgnoreCase))
                        ApplyRescan(sorted, CurrentFile);
                }));
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        void ApplyRescan(List<string> sortedList, string current)
        {
            List<string> list = sortedList;
            if (shuffled)
            {
                // Keep the shuffled order you're going through; new files join at random places after the current one.
                sortedFiles = sortedList;
                var have = new HashSet<string>(sortedList, StringComparer.OrdinalIgnoreCase);
                list = files.Where(have.Contains).ToList();
                var known = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                var random = new Random();
                // (inserting after the current file never moves it, so its place is looked up once)
                int at = Math.Max(0, list.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase))) + 1;
                foreach (var f in sortedList.Where(f => !known.Contains(f)))
                    list.Insert(Math.Min(list.Count, at + random.Next(Math.Max(1, list.Count - at + 1))), f);
            }
            if (list.SequenceEqual(files, StringComparer.OrdinalIgnoreCase)) return;
            int oldIndex = index;
            var old = files;
            files = list;
            if (files.Count == 0)
            {
                ShowWelcome(); // (1.27.0: the welcome screen; it keeps watching the folder, so a new file still shows)
                return;
            }
            int at2 = current == null ? -1 : files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
            if (at2 < 0 && current != null)
            {
                // The file on screen was deleted elsewhere: the next one that is still there in the order you were going
                // through, else the one before (1.27.0; before, the same number in the new list, which after a re-sort by
                // name was a random file of the folder).
                var here = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                int was = old.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
                if (was < 0) was = Math.Min(oldIndex, old.Count - 1);
                string pick = null;
                for (int k = was + 1; k < old.Count && pick == null; k++) if (here.Contains(old[k])) pick = old[k];
                for (int k = was - 1; k >= 0 && pick == null; k--) if (here.Contains(old[k])) pick = old[k];
                if (pick != null) at2 = files.FindIndex(f => string.Equals(f, pick, StringComparison.OrdinalIgnoreCase));
            }
            index = at2 >= 0 ? at2 : Math.Min(oldIndex, files.Count - 1);
            ShowIndex(index, lastDir); // also gets the (possibly new) neighbours ready
            if (gridOpen) FillGrid(false);
            if (snapDir != null) testLog.Add("folder changed: " + files.Count + " files, now on " + Path.GetFileName(files[index]));
        }
    }
}
