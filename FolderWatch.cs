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
            files = list;
            if (files.Count == 0)
            {
                message.Text = "No photos or videos in this folder";
                return;
            }
            int at2 = current == null ? -1 : files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
            index = at2 >= 0 ? at2 : Math.Min(oldIndex, files.Count - 1); // the file on screen was deleted elsewhere: show the next one
            ShowIndex(index, lastDir); // also gets the (possibly new) neighbours ready
            if (gridOpen) FillGrid(false);
            if (snapDir != null) testLog.Add("folder changed: " + files.Count + " files, now on " + Path.GetFileName(files[index]));
        }
    }
}
