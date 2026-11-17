// FastViewer 1.28.0: the error report. Errors are written on each PC (%LOCALAPPDATA%\FastViewer\errors.txt), so a
// friend's errors never reach whoever looks after FastViewer. "Copy error report" (Settings > About, and a message right
// after an error) puts a short text on the clipboard: FastViewer's version, the Windows version, the processor and the
// most recent errors, grouped, with every file name and folder outside FastViewer's own folders taken out. Nothing is
// sent anywhere: the friend pastes it into a message (it fits one Discord message, 2000 characters).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FastViewer
{
    static class ErrorReport
    {
        public const int MostChars = 1900; // one Discord message is 2000 at most

        public class Entry { public DateTime When; public string Version; public bool Closed; public string Text; }

        static string LocalDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastViewer"); } }
        public static string File_ { get { return Program.ErrorsFile ?? Path.Combine(LocalDir, "errors.txt"); } }

        static readonly Regex Header = new Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) FastViewer (\S+) \((kept running|closed)\)\s*$", RegexOptions.Multiline);

        // Every entry of errors.txt (and errors.old.txt before it), oldest first.
        public static List<Entry> Read()
        {
            var list = new List<Entry>();
            string file = File_;
            foreach (var f in new[] { Path.ChangeExtension(file, ".old.txt"), file })
            {
                string text;
                try { if (!File.Exists(f)) continue; text = File.ReadAllText(f); } catch { continue; }
                var heads = Header.Matches(text);
                for (int i = 0; i < heads.Count; i++)
                {
                    var h = heads[i];
                    int end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
                    DateTime when;
                    if (!DateTime.TryParseExact(h.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out when)) continue;
                    list.Add(new Entry
                    {
                        When = when, Version = h.Groups[2].Value, Closed = h.Groups[3].Value == "closed",
                        Text = text.Substring(h.Index + h.Length, end - h.Index - h.Length).Trim()
                    });
                }
            }
            return list.OrderBy(e => e.When).ToList();
        }

        // ---------------------------------------------------------------- what the PC is

        public static string WindowsName()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string product = k.GetValue("ProductName") as string ?? "Windows";
                    string display = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string;
                    string build = k.GetValue("CurrentBuildNumber") as string ?? "";
                    object ubr = k.GetValue("UBR");
                    int b;
                    // Windows 11 still calls itself "Windows 10" here; its builds start at 22000.
                    if (int.TryParse(build, out b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                    return product + (display != null ? " " + display : "") + " (build " + build + (ubr != null ? "." + ubr : "") + ")" +
                        (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit");
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        static string Processor()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string name = (k.GetValue("ProcessorNameString") as string ?? "").Trim();
                    name = Regex.Replace(name, @"\s+", " ");
                    return (name.Length > 0 ? name + ", " : "") + Environment.ProcessorCount + " threads";
                }
            }
            catch { return Environment.ProcessorCount + " threads"; }
        }

        // ---------------------------------------------------------------- nothing personal

        static readonly Regex DrivePath = new Regex(@"(?<![A-Za-z0-9])[A-Za-z]:\\[^\r\n'""<>|*?]*");
        static readonly Regex SharePath = new Regex(@"\\\\[A-Za-z0-9_.$-]+\\[^\r\n'""<>|*?]*");
        static readonly Regex QuotedName = new Regex(@"'[^'\r\n\\/]{1,200}\.([A-Za-z0-9]{2,5})'");

        // FastViewer's own folders keep their place under a short name; any other file or folder becomes "<file .mp4>"
        // or "<folder>"; the Windows user name and the PC's name go too.
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var known = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (path, name) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) return;
                    path = path.TrimEnd('\\');
                    if (path.Length > 3) known.Add(new KeyValuePair<string, string>(path, name));
                    string full = Path.GetFullPath(path).TrimEnd('\\'); // the long form of a short (8.3) temp path
                    if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase)) known.Add(new KeyValuePair<string, string>(full, name));
                }
                catch { }
            };
            add(LocalDir, "%LOCALAPPDATA%\\FastViewer");
            add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastViewer"), "%APPDATA%\\FastViewer");
            add(Path.Combine(Path.GetTempPath(), "FastViewer"), "%TEMP%\\FastViewer");
            add(LongPath(Path.Combine(Path.GetTempPath(), "FastViewer")), "%TEMP%\\FastViewer");
            add(AppDomain.CurrentDomain.BaseDirectory, "<FastViewer's folder>");
            foreach (var k in known.OrderByDescending(k => k.Key.Length))
                text = Regex.Replace(text, Regex.Escape(k.Key), k.Value.Replace("$", "$$"), RegexOptions.IgnoreCase);
            MatchEvaluator hide = m => Hidden(m.Value);
            text = DrivePath.Replace(text, hide);
            text = SharePath.Replace(text, hide);
            text = QuotedName.Replace(text, m => "'<file ." + m.Groups[1].Value.ToLowerInvariant() + ">'");
            foreach (var name in new[] { Environment.UserName, Environment.MachineName })
                if (!string.IsNullOrEmpty(name) && name.Length >= 3)
                    text = Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(name) + @"(?![A-Za-z0-9])", "<name>", RegexOptions.IgnoreCase);
            return text;
        }

        static string LongPath(string path)
        {
            try
            {
                var sb = new StringBuilder(1024);
                return GetLongPathName(path, sb, sb.Capacity) > 0 ? sb.ToString() : path;
            }
            catch { return path; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetLongPathName(string shortPath, StringBuilder longPath, int size);

        // A path in a message: only what kind of file it was.
        static string Hidden(string path)
        {
            string last = path.TrimEnd(' ', '.', ',', ';', ')').Split('\\').Last();
            var ext = Regex.Match(last, @"\.([A-Za-z0-9]{2,5})(?=$|[\s:,;)])");
            return ext.Success ? "<file ." + ext.Groups[1].Value.ToLowerInvariant() + ">" : "<folder>";
        }

        // ---------------------------------------------------------------- the report

        // The newest errors of the last 30 days, the same error once (with how often), each with its message and where in
        // FastViewer it happened. Shortened step by step until it fits one message.
        public static string Build(List<Entry> all)
        {
            var since = DateTime.Now.AddDays(-30);
            var recent = all.Where(e => e.When >= since).ToList();
            var groups = recent.GroupBy(e => Signature(e.Text)).Select(g => g.OrderBy(e => e.When).ToList())
                .OrderByDescending(g => g.Last().When).ToList();

            var head = new StringBuilder();
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            head.AppendLine("FastViewer error report");
            head.AppendLine("FastViewer " + version.ToString(3) + (Environment.Is64BitProcess ? " (64-bit)" : " (32-bit)"));
            head.AppendLine(WindowsName());
            head.AppendLine("Processor: " + Processor());
            try
            {
                head.AppendLine("Screen: " + (int)SystemParameters.PrimaryScreenWidth + "x" + (int)SystemParameters.PrimaryScreenHeight +
                    " (" + System.Windows.Forms.Screen.AllScreens.Length + " screen" + (System.Windows.Forms.Screen.AllScreens.Length == 1 ? "" : "s") + ")");
            }
            catch { }
            head.AppendLine("Report made " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            if (groups.Count == 0)
            {
                head.AppendLine(all.Count == 0 ? "No errors written." : "No errors in the last 30 days (" + all.Count + " before).");
                return "```" + Environment.NewLine + Scrub(head.ToString()).TrimEnd() + Environment.NewLine + "```";
            }
            head.AppendLine(recent.Count + " error" + (recent.Count == 1 ? "" : "s") + " in the last 30 days" +
                (groups.Count > 1 ? ", " + groups.Count + " different ones" : "") + ", newest first:");

            string text = null;
            for (int frames = 8, most = 5; ; )
            {
                var sb = new StringBuilder(head.ToString());
                int shown = Math.Min(most, groups.Count);
                foreach (var g in groups.Take(shown))
                {
                    var last = g.Last();
                    sb.AppendLine();
                    sb.AppendLine("-- " + last.When.ToString("yyyy-MM-dd HH:mm:ss") + ", " + Regex.Replace(last.Version, @"^(\d+\.\d+\.\d+)\.0$", "$1") +
                        (last.Closed ? ", FastViewer closed" : ", kept running") + (g.Count > 1 ? " (" + g.Count + " times since " + g[0].When.ToString("MM-dd HH:mm") + ")" : ""));
                    foreach (var line in Short(last.Text, frames)) sb.AppendLine(line);
                }
                if (shown < groups.Count) sb.AppendLine().AppendLine("(" + (groups.Count - shown) + " older ones left out)");
                text = "```" + Environment.NewLine + Scrub(sb.ToString()).TrimEnd() + Environment.NewLine + "```";
                if (text.Length <= MostChars) break;
                if (frames > 3) frames--;
                else if (most > 1) most--;
                else { text = text.Substring(0, MostChars - 8) + Environment.NewLine + "...```"; break; }
            }
            return text;
        }

        // The message line(s), inner errors, and at most `frames` lines of where it happened: FastViewer's own first,
        // so the line that matters isn't crowded out by Windows' plumbing.
        static IEnumerable<string> Short(string text, int frames)
        {
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).ToList();
            var message = lines.Where(l => !l.TrimStart().StartsWith("at ") && !l.Contains("--- End of inner exception")).Take(6)
                .Select(l => l.Length > 300 ? l.Substring(0, 300) + "..." : l).ToList();
            var at = lines.Where(l => l.TrimStart().StartsWith("at ")).Select(l => "   " + l.Trim()).ToList();
            var own = at.Where(l => l.Contains(" FastViewer.")).ToList();
            var keep = new List<string>();
            if (at.Count > 0 && !at[0].Contains(" FastViewer.")) keep.Add(at[0]); // where it was thrown, when that's in Windows
            foreach (var l in own) { if (keep.Count >= frames) break; if (!keep.Contains(l)) keep.Add(l); }
            foreach (var l in at) { if (keep.Count >= Math.Min(frames, 3)) break; if (!keep.Contains(l)) keep.Add(l); }
            return message.Concat(at.Where(keep.Contains).Select(l => l.Length > 200 ? l.Substring(0, 200) + "..." : l));
        }

        // The same error: the same first line (numbers and paths left out) thrown from the same place.
        static string Signature(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            string first = lines.Length > 0 ? Regex.Replace(Scrub(lines[0]), @"\d+", "#") : "";
            string where = lines.FirstOrDefault(l => l.TrimStart().StartsWith("at FastViewer.")) ?? "";
            return first + "|" + where.Trim();
        }

        // ---------------------------------------------------------------- copying it

        public static bool ToClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetDataObject(text, true); return true; } // (another program may hold the clipboard for a moment)
                catch { System.Threading.Thread.Sleep(60); }
            }
            return false;
        }

        // errors.seen: the time of the newest error the user was already told about (so a "closed" error is offered once).
        public static string SeenFile { get { return Path.Combine(LocalDir, "errors.seen"); } }
    }

    partial class ViewerWindow
    {
        static DateTime errorOffered = DateTime.MinValue;

        // Right after an error was written: a message offering the report (at most every 5 minutes).
        internal void OfferErrorReport(bool closedLastTime)
        {
            if (!closedLastTime && (DateTime.Now - errorOffered).TotalMinutes < 5) return;
            errorOffered = DateTime.Now;
            ShowToast(closedLastTime ? "FastViewer closed unexpectedly last time." : "Something went wrong, but FastViewer kept going.",
                "Copy error report", CopyErrorReport, 12, "error");
        }

        // Made on a pool thread (errors.txt can be 1 MB), copied on the window's thread (the clipboard wants that).
        internal void CopyErrorReport()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string report = null, failed = null;
                try { report = ErrorReport.Build(ErrorReport.Read()); }
                catch (Exception ex) { failed = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (report == null) { ShowToast("Couldn't make the report: " + failed, null, null, 4, "error"); return; }
                    if (snapDir != null) { lastErrorReport = report; Log("error report " + report.Length + " characters"); } // tests never touch the user's clipboard
                    else if (!ErrorReport.ToClipboard(report)) { ShowToast("Couldn't copy: another program is using the clipboard. Try again.", "Copy error report", CopyErrorReport, 8, "error"); return; }
                    ShowToast("Error report copied. Paste it into a message (Ctrl+V).", null, null, 5, "done");
                }));
            });
        }

        internal string lastErrorReport; // tests: what would have been copied
        TextBlock errorReportDesc;      // Settings > About's card text

        // --case errorreport --errors <file seeded with old errors>: an error is thrown, the message offers the report, its
        // button "copies" it (tests: kept in lastErrorReport), and the report holds no user name, PC name or outside path,
        // fits one Discord message and names the version and Windows. Then Settings > About's card says how many errors.
        void HuntErrorReport()
        {
            After(1500, delegate { throw new InvalidOperationException("test: could not open '" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "holiday clip.mp4") + "'"); });
            After(2500, delegate
            {
                HuntLog("message: '" + F<TextBlock>("ToastText").Text + "', button '" + F<Button>("ToastBtn").Content + "', kind " + toastKind +
                    (toastKind == "error" && toastAction != null ? "" : " FAIL"));
                toastAction();
                Until(() => lastErrorReport != null, 5000, delegate
                {
                    string r = lastErrorReport ?? "";
                    var leaks = new List<string>();
                    foreach (var bad in new[] { Environment.UserName, Environment.MachineName, "C:\\", "holiday", "secret" })
                        if (r.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) leaks.Add(bad);
                    var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                    HuntLog("report " + r.Length + " characters" + (r.Length <= ErrorReport.MostChars ? "" : " FAIL too long") +
                        "; leaks: " + (leaks.Count == 0 ? "none" : string.Join(", ", leaks) + " FAIL") +
                        "; names version " + r.Contains("FastViewer " + v) + ", Windows " + r.Contains("Windows") + (r.Contains("FastViewer " + v) && r.Contains("Windows") ? "" : " FAIL"));
                    HuntLog("message after copying: '" + F<TextBlock>("ToastText").Text + "'");
                    File.WriteAllText(Path.Combine(snapDir, "report.txt"), r);
                    OpenSettings();
                    After(1500, delegate
                    {
                        HuntLog("About card: '" + (errorReportDesc == null ? "(not built)" : errorReportDesc.Text) + "'");
                        CloseSettings();
                        HuntDone();
                    });
                });
            });
        }

        // At start-up, after the first picture: if FastViewer closed because of an error last time, say so once.
        void CheckClosedLastTime()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var last = ErrorReport.Read().LastOrDefault();
                    if (last == null || !last.Closed || (DateTime.Now - last.When).TotalDays > 7) return;
                    string stamp = last.When.ToString("yyyy-MM-dd HH:mm:ss");
                    string seen = File.Exists(ErrorReport.SeenFile) ? File.ReadAllText(ErrorReport.SeenFile).Trim() : "";
                    if (string.CompareOrdinal(seen, stamp) >= 0) return;
                    File.WriteAllText(ErrorReport.SeenFile, stamp);
                    Dispatcher.BeginInvoke(new Action(delegate { OfferErrorReport(true); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                catch { }
            });
        }
    }
}
