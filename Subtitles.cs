// FastViewer: subtitles (1.24.0). A .srt or .vtt file with the video's name next to it is picked up by itself
// (also "name.en.srt"), or you choose one; captions made by Captions.cs come in the same way. The text sits
// over the bottom of the video and moves up while the control bar shows. Nothing here runs unless the video
// on screen has subtitles and they are switched on, and the look-up happens off the window's thread, so
// switching files stays exactly as fast.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastViewer
{
    class Cue
    {
        public double From, To; // seconds
        public string Text;
    }

    static class SubtitleFile
    {
        static readonly Regex Times = new Regex(@"(?:(\d+):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})", RegexOptions.Compiled);
        static readonly Regex Tags = new Regex(@"<[^>]{0,40}>|\{\\[^}]*\}", RegexOptions.Compiled);

        // SubRip (.srt) and WebVTT (.vtt): blocks of "start --> end" and the lines under it. Anything else
        // (numbers, WEBVTT headers, notes, styles, a broken block) is skipped, never an error.
        public static List<Cue> Parse(string text)
        {
            var cues = new List<Cue>();
            Cue open = null;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                var m = Times.Match(line);
                if (m.Success)
                {
                    open = new Cue { From = Seconds(m, 1), To = Seconds(m, 5), Text = "" };
                    if (open.To > open.From) cues.Add(open); else open = null;
                    continue;
                }
                if (line.Length == 0) { open = null; continue; }
                if (open == null) continue;
                line = WebUtilityDecode(Tags.Replace(line, "")).Trim();
                if (line.Length > 0) open.Text = open.Text.Length == 0 ? line : open.Text + "\n" + line;
            }
            cues.RemoveAll(c => c.Text.Length == 0);
            return cues.OrderBy(c => c.From).ToList(); // (a stable sort: files that are in order stay as they are)
        }

        static double Seconds(Match m, int g)
        {
            int h = m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
            string ms = m.Groups[g + 3].Value.PadRight(3, '0');
            return h * 3600 + int.Parse(m.Groups[g + 1].Value) * 60 + int.Parse(m.Groups[g + 2].Value) + int.Parse(ms) / 1000.0;
        }

        static string WebUtilityDecode(string s) { return s.IndexOf('&') < 0 ? s : System.Net.WebUtility.HtmlDecode(s); }

        // UTF-8 (with or without a mark), UTF-16, or, when the bytes aren't valid UTF-8, this PC's own code page
        // (older subtitle files).
        public static List<Cue> Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 20 * 1024 * 1024) throw new Exception("That file is too big to be subtitles.");
            string text;
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                text = (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
            else
            {
                int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                try { text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
                catch (DecoderFallbackException) { text = Encoding.Default.GetString(bytes); }
            }
            return Parse(text);
        }

        public static string ToSrt(IEnumerable<Cue> cues)
        {
            var sb = new StringBuilder();
            int n = 1;
            foreach (var c in cues)
                sb.Append(n++).Append("\r\n").Append(Stamp(c.From)).Append(" --> ").Append(Stamp(c.To)).Append("\r\n")
                  .Append(c.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
            return sb.ToString();
        }

        static string Stamp(double seconds)
        {
            var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + "," + t.Milliseconds.ToString("000");
        }

        // Subtitle files that belong to a video: "video.srt" first, then "video.<anything>.srt" (a language), .vtt after .srt.
        public static List<string> Beside(string video)
        {
            var found = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(video), stem = Path.GetFileNameWithoutExtension(video);
                foreach (var ext in new[] { ".srt", ".vtt" })
                {
                    string exact = Path.Combine(dir, stem + ext);
                    if (File.Exists(exact)) found.Add(exact);
                }
                if (found.Count == 0 && stem.IndexOfAny(new[] { '*', '?', '[' }) < 0)
                    foreach (var ext in new[] { ".srt", ".vtt" })
                        found.AddRange(Directory.GetFiles(dir, stem + ".*" + ext).Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch { }
            return found;
        }
    }

    partial class ViewerWindow
    {
        bool subsOn = true;          // settings line subs=
        int subSize = 1;             // 0 small, 1 medium, 2 large (subSize=)
        static readonly double[] SubSizes = { 0.027, 0.035, 0.046 }; // of the viewing area's height

        List<Cue> cues;              // the subtitles of the video on screen (null: it has none)
        string cuesFor, cuesFrom;    // that video; where they came from (a file's path, or "auto")
        int cueShown = -2;           // index of the cue on screen (-1: none; -2: nothing set yet)
        int subsLookup;              // the newest look-up wins
        readonly Dictionary<string, string> chosenSubs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // picked by hand, this session
        readonly DispatcherTimer subTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        Border subBox;
        TextBlock subText;
        Glyph subIcon;
        TranslateTransform subLift;
        internal readonly List<string> subLog = new List<string>(); // tests

        bool HasSubs { get { return cues != null && cues.Count > 0 && shown != null && shown.File == cuesFor; } }

        void InitSubtitles()
        {
            subBox = F<Border>("SubBox");
            subText = F<TextBlock>("SubText");
            subIcon = F<Glyph>("SubIcon");
            subLift = (TranslateTransform)F<FrameworkElement>("SubHolder").RenderTransform;
            OnClick("SubBtn", ToggleSubtitles);
            subTimer.Tick += delegate { SubTick(); };
            stage.SizeChanged += delegate { SizeSubs(); PlaceSubs(false); };
            transport.IsVisibleChanged += delegate { PlaceSubs(true); };
        }

        // Called whenever a different file appears on screen: its subtitles are looked for in the background.
        void FindSubtitles()
        {
            int ticket = ++subsLookup;
            cues = null;
            cuesFor = cuesFrom = null;
            ShowCue(-1, false);
            UpdateSubButton();
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            string video = s.File, chosen;
            chosenSubs.TryGetValue(video, out chosen);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Cue> list = null;
                string from = null;
                try
                {
                    string file = chosen != null && File.Exists(chosen) ? chosen : SubtitleFile.Beside(video).FirstOrDefault();
                    if (file != null) { list = SubtitleFile.Load(file); from = file; }
                    else
                    {
                        string made = Whisper.CaptionsFile(video);
                        if (made != null && File.Exists(made)) { list = SubtitleFile.Load(made); from = "auto"; }
                    }
                }
                catch { list = null; }
                if (list == null || list.Count == 0) return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (ticket != subsLookup || shown == null || shown.File != video) return; // moved on meanwhile
                    SetCues(video, list, from);
                }));
            });
        }

        void SetCues(string video, List<Cue> list, string from)
        {
            cues = list;
            cuesFor = video;
            cuesFrom = from;
            cueShown = -2;
            if (snapDir != null) lock (subLog) subLog.Add("subtitles for " + Path.GetFileName(video) + ": " + list.Count + " lines from " + (from == "auto" ? "automatic captions" : Path.GetFileName(from)));
            UpdateSubButton();
            SubTick();
        }

        // The CC button: there while this video has subtitles, in the accent colour while they show.
        void UpdateSubButton()
        {
            var b = F<Button>("SubBtn");
            if (b == null || subIcon == null) return;
            bool want = HasSubs && Shown("SubBtn") && transport.Width >= 440;
            bool was = b.Visibility == Visibility.Visible;
            b.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
            if (want && !was && Motion.Animates && !Switching && transport.IsVisible) Motion.Pulse(subIcon, 0.6); // it arrives with a little bump
            if (subsOn) subIcon.SetResourceReference(Glyph.ForegroundProperty, "Accent");
            else subIcon.ClearValue(Glyph.ForegroundProperty);
            bool run = HasSubs && subsOn && playing;
            if (run != subTimer.IsEnabled) { if (run) subTimer.Start(); else subTimer.Stop(); }
            if (!HasSubs || !subsOn) ShowCue(-1, true);
        }

        void ToggleSubtitles()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            if (!HasSubs)
            {
                ShowToast("This " + (s.IsAudio ? "song" : "video") + " has no subtitles yet.", "Choose a file", delegate { HideToast(null); ChooseSubtitles(); }, 5, "info");
                return;
            }
            subsOn = !subsOn;
            SaveSettings();
            Motion.Pulse(subIcon);
            cueShown = -2;
            UpdateSubButton();
            SubTick();
            if (!overlayVisible || !transport.IsVisible) ShowFeedback(subsOn ? "Subtitles on" : "Subtitles off");
            foreach (var refresh in settingsRefresh) refresh();
        }

        void ChooseSubtitles()
        {
            var s = shown;
            if (s == null || !s.IsVideo || s.Failed) return;
            bool wasPlaying = playing;
            if (playing) TogglePlay();
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose subtitles for " + Path.GetFileName(s.File),
                InitialDirectory = Path.GetDirectoryName(s.File),
                Filter = "Subtitles (*.srt, *.vtt)|*.srt;*.vtt|All files|*.*"
            };
            bool picked = dlg.ShowDialog(this) == true;
            if (picked) UseSubtitleFile(s.File, dlg.FileName);
            if (wasPlaying && !playing && shown == s) TogglePlay();
        }

        internal void UseSubtitleFile(string video, string file)
        {
            List<Cue> list;
            try { list = SubtitleFile.Load(file); }
            catch (Exception ex) { ShowToast("Couldn't read those subtitles: " + ex.Message, null, null, 6, "error"); return; }
            if (list.Count == 0) { ShowToast("No subtitles found in " + Path.GetFileName(file) + " (FastViewer reads .srt and .vtt).", null, null, 6, "error"); return; }
            chosenSubs[video] = file;
            if (shown == null || shown.File != video) return;
            subsLookup++;
            if (!subsOn) { subsOn = true; SaveSettings(); }
            SetCues(video, list, file);
            ShowToast("Subtitles: " + Path.GetFileName(file), null, null, 3, "done");
        }

        // ---------------------------------------------------------------- on screen

        void SubTick()
        {
            var s = shown;
            if (!HasSubs || !subsOn || s == null || s.Failed || trimming || cropping || gridOpen || settingsOpen) { ShowCue(-1, true); return; }
            double at = (scrubbing ? TimeSpan.FromSeconds(seek.Value) : s.Video.Position).TotalSeconds;
            // The last cue that has started (they are in order of starting time); it shows until its own end.
            int lo = 0, hi = cues.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cues[mid].From <= at) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found >= 0 && cues[found].To <= at) found = -1;
            ShowCue(found, true);
        }

        void ShowCue(int i, bool animate)
        {
            if (subBox == null || i == cueShown) return;
            bool first = cueShown < 0;
            cueShown = i;
            if (i < 0)
            {
                if (subBox.Visibility != Visibility.Visible) return;
                if (animate && Motion.Animates) Motion.Hide(subBox, 0, 0, 1, 120);
                else { Motion.Set(subBox, OpacityProperty, 0); subBox.Visibility = Visibility.Collapsed; }
                return;
            }
            subText.Text = cues[i].Text;
            if (snapDir != null) lock (subLog) if (subLog.Count < 300) subLog.Add("shows \"" + cues[i].Text.Replace("\n", " / ") + "\"");
            if (!first && subBox.Visibility == Visibility.Visible && !Motion.Busy(subBox)) return; // the next line simply takes its place
            SizeSubs();
            PlaceSubs(false);
            if (animate && Motion.Animates) Motion.Show(subBox, 0, 0, 1, 90);
            else { subBox.Visibility = Visibility.Visible; Motion.Set(subBox, OpacityProperty, 1); }
        }

        void SizeSubs()
        {
            if (subText == null) return;
            double h = stage.ActualHeight, w = stage.ActualWidth;
            if (h <= 0 || w <= 0) return;
            subText.FontSize = Math.Max(12, Math.Min(56, h * SubSizes[Math.Max(0, Math.Min(2, subSize))]));
            subText.MaxWidth = Math.Max(120, w * 0.82 - 24);
        }

        // The text keeps clear of the control bar: it glides up when the bar appears and back down when it goes.
        void PlaceSubs(bool animate)
        {
            if (subBox == null) return;
            bool bar = overlayVisible && transport.Visibility == Visibility.Visible;
            double lift = bar ? -(transport.ActualHeight + transport.Margin.Bottom + 10) : -Math.Max(10, stage.ActualHeight * 0.035);
            if (animate && Motion.Moves && subBox.Visibility == Visibility.Visible) Motion.To(subLift, TranslateTransform.YProperty, lift, Motion.Ms(Motion.Normal), Motion.Smooth);
            else Motion.Set(subLift, TranslateTransform.YProperty, lift);
        }

        // ---------------------------------------------------------------- menu and Settings

        IEnumerable<Control> SubtitleMenuItem(string id)
        {
            var s = shown;
            var none = new Control[0];
            if (s == null || !s.IsVideo || s.Failed) return none;
            switch (id)
            {
                case "subtitles": return HasSubs ? One(Item("Subtitles", subsOn ? "" : null, ToggleSubtitles, KeyOf("subtitles"))) : none;
                case "subFile": return One(Item(HasSubs ? "Choose other subtitles..." : "Choose subtitles...", "", ChooseSubtitles));
                case "autoCaptions":
                    var list = new List<Control> { Item(HasSubs && cuesFrom == "auto" ? "Make the captions again" : "Make captions automatically", "", MakeCaptions, KeyOf("autoCaptions")) };
                    if (HasSubs && cuesFrom == "auto") list.Add(Item("Save captions as a file...", "", SaveCaptionsAs));
                    return list;
            }
            return none;
        }

        void AddSubtitleSettings()
        {
            AddSubheading("Subtitles");
            AddCard("", "Show subtitles",
                "A subtitle file (.srt or .vtt) with the same name as the video, next to it, is used by itself. The CC button on the " +
                "video bar and the " + (KeyOf("subtitles").Length > 0 ? KeyOf("subtitles") + " key" : "video \"...\" menu") + " switch them on and off; \"...\" > Choose subtitles picks any other file.",
                Toggle(() => subsOn, v => { subsOn = v; cueShown = -2; UpdateSubButton(); SubTick(); }));
            AddCard("", "Subtitle size", null,
                Dropdown(new[] { "Small", "Medium", "Large" }, () => subSize, i => { subSize = i; SizeSubs(); }));
            AddCaptionSettings();
        }
    }
}
