// FastViewer: checks GitHub for a newer version and updates itself.
//
// A new version is a "release" on github.com/<UpdateRepo> named like v1.8.0, with the file
// FastViewer-1.8.0.zip attached (the same zip that's shared with friends).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        // The GitHub page updates come from ("owner/FastViewer"). Empty = updates not set up yet.
        const string UpdateRepo = "real4tk/FastViewer";

        bool autoUpdate = true;          // check when FastViewer starts, at most about once a day (Settings)
        DateTime lastUpdateCheck;
        string updateFeed;               // test runs: --update-feed <local .json, or a file saying "refused" to act like GitHub saying no>
        string updatePage;               // test runs: --update-page <local file holding where releases/latest leads>
        bool updateTestAccept;           // test runs: --update-test (accepts the update by itself)
        bool testRun;                    // started with any --option: never contacts GitHub (see Feed)
        DispatcherTimer updateRetry;

        class Release
        {
            public Version Version;
            public string ZipUrl;
        }

        // GitHub said no: too many questions from this internet connection for now (it allows 60 an hour).
        class RefusedException : Exception
        {
            public RefusedException() : base("GitHub is busy right now") { }
        }

        static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        const string DownloadPrefix = "https://github.com/" + UpdateRepo + "/releases/download/";

        string Feed
        {
            get
            {
                if (updateFeed != null) return updateFeed;
                if (testRun) return null; // tests only ever use a local feed, so they can't use up GitHub's limit
                return UpdateRepo.Length == 0 ? null : "https://api.github.com/repos/" + UpdateRepo + "/releases/latest";
            }
        }

        // Called once FastViewer has opened.
        void StartupUpdateCheck()
        {
            // Left over from the last update (the old program can't delete itself while running).
            string exe = Assembly.GetExecutingAssembly().Location;
            try
            {
                foreach (var old in Directory.GetFiles(Path.GetDirectoryName(exe), Path.GetFileName(exe) + ".old*"))
                    try { File.Delete(old); } catch { } // still in use by an older window: next time
            }
            catch { }
            bool due = autoUpdate && (DateTime.Now - lastUpdateCheck).TotalHours >= 20;
            if (updateTestAccept) Log("startup: feed=" + Feed + ", due=" + due);
            if (Feed != null && (due || updateFeed != null)) After(3000, delegate { CheckForUpdates(false); });
        }

        void CheckForUpdates(bool manual)
        {
            string feed = Feed;
            if (feed == null)
            {
                if (manual) ShowToast(testRun ? "Test run: updates are only checked with a local test feed." : "Updates aren't set up in this version yet.", null, null, 4, null);
                return;
            }
            if (updateRetry != null) updateRetry.Stop();
            if (manual) ShowToast("Checking for updates...", null, null, 0, "update");
            var thread = new Thread(delegate()
            {
                Release latest = null;
                string error = null;
                bool busy = false;
                try { latest = FetchLatest(feed); }
                catch (Exception ex)
                {
                    // GitHub's API answers only 60 questions an hour per internet connection. The normal releases
                    // page has no such limit: ask that instead.
                    busy = IsRefusal(ex);
                    try
                    {
                        latest = FetchFromReleasesPage();
                        if (updateTestAccept) Log("API refused or failed (" + ex.Message + "), used the releases page");
                    }
                    catch (Exception ex2)
                    {
                        busy |= IsRefusal(ex2);
                        error = ex.Message;
                    }
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (updateTestAccept) Log("checked: " + (error != null ? (busy ? "busy: " : "error: ") + error : latest == null ? "no usable release" : latest.Version + " at " + latest.ZipUrl));
                    if (error != null)
                    {
                        // Not counted as a check: FastViewer tries again by itself in a while (and when it next starts).
                        RetryUpdateCheckLater();
                        if (manual)
                            ShowToast(busy ? "GitHub is busy right now. FastViewer will try again later."
                                           : "Couldn't reach GitHub to check for updates. FastViewer will try again later.", null, null, 6, "update");
                        return;
                    }
                    lastUpdateCheck = DateTime.Now;
                    SaveSettings();
                    if (latest == null || latest.Version <= CurrentVersion)
                    {
                        if (manual) ShowToast("You have the latest version (" + CurrentVersion.ToString(3) + ").", null, null, 4, "update");
                        else if (updateTestAccept) Log("update test: nothing newer");
                        return;
                    }
                    ShowToast("FastViewer " + latest.Version.ToString(3) + " is available.", "Update now",
                              delegate { InstallUpdate(latest); }, 0, "update");
                    if (updateTestAccept) InstallUpdate(latest);
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        // A quiet second try, half an hour later, while this window stays open.
        void RetryUpdateCheckLater()
        {
            if (updateRetry == null)
            {
                updateRetry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(updateTestAccept ? 0.05 : 30) };
                updateRetry.Tick += delegate { updateRetry.Stop(); if (autoUpdate || updateTestAccept) CheckForUpdates(false); };
            }
            updateRetry.Stop();
            updateRetry.Start();
        }

        static bool IsRefusal(Exception ex)
        {
            if (ex is RefusedException) return true;
            var web = ex as WebException;
            var response = web == null ? null : web.Response as HttpWebResponse;
            return response != null && ((int)response.StatusCode == 403 || (int)response.StatusCode == 429);
        }

        Release FetchLatest(string feed)
        {
            string json;
            if (File.Exists(feed)) json = File.ReadAllText(feed);
            else using (var web = NewWebClient()) json = web.DownloadString(feed);
            if (json.Trim() == "refused") throw new RefusedException(); // test runs: GitHub saying no

            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            Version version;
            if (!Version.TryParse(((string)data["tag_name"]).TrimStart('v', 'V'), out version)) return null;
            var release = new Release { Version = Normalize(version) };
            foreach (var item in (System.Collections.IEnumerable)data["assets"])
            {
                var asset = (Dictionary<string, object>)item;
                string name = (string)asset["name"], url = (string)asset["browser_download_url"];
                if (!name.StartsWith("FastViewer", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (Trusted(url)) release.ZipUrl = url;
            }
            return release.ZipUrl == null ? null : release;
        }

        // Only download from this FastViewer's own GitHub page (or a local file in test runs).
        bool Trusted(string url)
        {
            return updateFeed != null ? File.Exists(url) || url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)
                                      : url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // github.com/<repo>/releases/latest leads to .../releases/tag/v1.15.4; the zip of that release is always
        // FastViewer-1.15.4.zip (that's how releases are made), and it's only offered if it's really there.
        Release FetchFromReleasesPage()
        {
            string location;
            if (testRun)
            {
                if (updatePage == null) throw new Exception("no releases page in this test");
                location = File.ReadAllText(updatePage).Trim();
                if (location == "refused") throw new RefusedException();
            }
            else
            {
                var request = (HttpWebRequest)WebRequest.Create("https://github.com/" + UpdateRepo + "/releases/latest");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                request.AllowAutoRedirect = false;
                request.Method = "HEAD";
                request.UserAgent = "FastViewer/" + CurrentVersion.ToString(3);
                request.Timeout = 15000;
                using (var response = (HttpWebResponse)request.GetResponse()) location = response.Headers["Location"] ?? "";
            }
            const string marker = "/releases/tag/";
            int at = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string tag = Uri.UnescapeDataString(location.Substring(at + marker.Length).Trim('/'));
            Version version;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out version)) return null;
            version = Normalize(version);
            string zip = DownloadPrefix + Uri.EscapeDataString(tag) + "/FastViewer-" + version.ToString(3) + ".zip";
            if (!testRun)
            {
                var check = (HttpWebRequest)WebRequest.Create(zip);
                check.Method = "HEAD";
                check.UserAgent = "FastViewer/" + CurrentVersion.ToString(3);
                check.Timeout = 15000;
                using (check.GetResponse()) { } // throws if the release has no such zip
            }
            return new Release { Version = version, ZipUrl = zip };
        }

        static Version Normalize(Version v) // 1.8.0 and 1.8.0.0 are the same version
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        static WebClient NewWebClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var web = new WebClient();
            web.Headers[HttpRequestHeader.UserAgent] = "FastViewer/" + CurrentVersion.ToString(3);
            web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return web;
        }

        void InstallUpdate(Release release)
        {
            ShowToast("Downloading FastViewer " + release.Version.ToString(3) + "...", null, null, 0, "update");
            string exe = Assembly.GetExecutingAssembly().Location;
            var thread = new Thread(delegate()
            {
                string error = null;
                string temp = Path.Combine(Path.GetTempPath(), "FastViewer-update-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(temp);
                    string zip = Path.Combine(temp, "update.zip"), fresh = Path.Combine(temp, "FastViewer.exe");
                    if (File.Exists(release.ZipUrl)) File.Copy(release.ZipUrl, zip);
                    else if (testRun) throw new Exception("test run: would download " + release.ZipUrl); // tests never download from GitHub
                    else using (var web = NewWebClient()) web.DownloadFile(release.ZipUrl, zip);

                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("FastViewer.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new Exception("the download doesn't contain FastViewer.exe");
                        entry.ExtractToFile(fresh);
                    }
                    var downloaded = Normalize(AssemblyName.GetAssemblyName(fresh).Version);
                    if (downloaded <= CurrentVersion) throw new Exception("the download isn't newer than this version");

                    // Swap: a running program's file can be renamed, so move this one aside and put the new one in its place.
                    string old = exe + ".old";
                    if (File.Exists(old))
                    {
                        // Another FastViewer window (or the one waiting in the background) may still run from the last
                        // update's old file: it can't be deleted then, but it can be moved out of the way.
                        try { File.Delete(old); }
                        catch (UnauthorizedAccessException) { File.Move(old, exe + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8)); }
                        catch (IOException) { File.Move(old, exe + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8)); }
                    }
                    File.Move(exe, old);
                    try { File.Copy(fresh, exe); }
                    catch
                    {
                        File.Move(old, exe); // put things back as they were
                        throw;
                    }
                }
                catch (UnauthorizedAccessException) { error = "Windows doesn't allow changing files in FastViewer's folder. Download the new zip instead."; }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    try { Directory.Delete(temp, true); } catch { }
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (error != null)
                    {
                        ShowToast("Couldn't update: " + error, null, null, 8, "update");
                        Log("update failed: " + error);
                        return;
                    }
                    Log("updated to " + release.Version.ToString(3));
                    // Restart on the same file.
                    RememberPosition(shown);
                    SavePositions();
                    SaveSettings();
                    PrepareRestart(); // the new version takes over receiving files
                    if (!updateTestAccept)
                    {
                        // A FastViewer waiting in the background still runs the old version: it quits, and the new one
                        // opens in a window of its own (--updated: not handed to the old one on the way).
                        SingleInstance.AskBackgroundToQuit();
                        Process.Start(exe, (CurrentFile == null ? "" : "\"" + CurrentFile + "\" ") + "--updated");
                    }
                    Close();
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
