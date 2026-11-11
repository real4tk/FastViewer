// FastViewer tests for 1.24.0 (--hunt <out> --case convert | subs | addons | captions | smooth).
//   convert:  folder with clear.png (see-through corner), photo.jpg, odd.jfif, other.webp. Scratch copies only.
//   subs:     folder with clip.mp4 + clip.srt, lang.mp4 + lang.en.srt, plain.mp4, web.mp4 + web.vtt, extra.srt.
//   addons:   --addons <scratch> --addon-source <folder with stand-in downloads>: the Settings cards while
//             downloading, a wrong file refused, Remove, the graphics card verdicts. With --pattern real it
//             downloads the real add-ons into --addons (the only test that uses the internet).
//   captions: --addons <folder holding the real Whisper add-on>, folder with speech.mp4.
//   smooth:   --addons <real Smooth add-on> or --frameboost <tools folder>, folder with clip.mp4.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastViewer
{
    partial class ViewerWindow
    {
        static string FileHash(string f) { using (var sha = SHA256.Create()) using (var s = File.OpenRead(f)) return string.Concat(sha.ComputeHash(s).Take(6).Select(b => b.ToString("x2"))); }

        static string Headers(IEnumerable<Control> items) { return string.Join(" | ", items.OfType<MenuItem>().Select(m => m.Header + (m.Icon == null ? "" : "*"))); }

        // --case rotleft: scratch folder with photo.jpg (wider than high) and clip.mp4. Use --settings and --motion full.
        void HuntRotateLeft()
        {
            Func<int> turn = () => { int a; rotations.TryGetValue(shown.File, out a); return a; };
            Func<string> tip = () => "" + F<Button>("RotateBtn").ToolTip;
            Func<string, string> onDisk = file =>
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var f = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    object o = null;
                    try { o = ((BitmapMetadata)f.Metadata).GetQuery("System.Photo.Orientation"); } catch { }
                    return f.PixelWidth + "x" + f.PixelHeight + " orientation tag " + (o ?? "none");
                }
            };
            HuntOpen("photo.jpg", delegate
            {
                string photo = shown.File;
                HuntLog("key for Rotate left: '" + KeyOf("rotateLeft") + "', for Rotate right: '" + KeyOf("rotate") + "'; top menu: " + Headers(TopMenuItem("rotate").Concat(TopMenuItem("rotateLeft"))));
                HuntLog("tip: " + tip());
                // Settings: the "..." menus list and the Keyboard page show the mirrored arrow for Rotate left only.
                OpenSettings();
                foreach (var pageName in new[] { "Buttons", "Keyboard" })
                {
                    var page = settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == pageName).Value;
                    ShowSettingsPage(page);
                    Settle();
                    foreach (var name in new[] { "Rotate left", "Rotate right" })
                    {
                        var card = FindAll<Border>(page).FirstOrDefault(b => b.Child is Grid && FindAll<TextBlock>(b).Any(t => t.Text == name));
                        var g = card == null ? null : ((Grid)card.Child).Children[0] as Glyph;
                        bool flipped = g != null && g.LayoutTransform is ScaleTransform && ((ScaleTransform)g.LayoutTransform).ScaleX < 0;
                        HuntLog(pageName + " page, '" + name + "': " + (g == null ? "NOT FOUND FAIL" : "arrow mirrored " + flipped + (flipped == (name == "Rotate left") ? " ok" : " FAIL")));
                        if (card != null && name == "Rotate left") { card.BringIntoView(); Settle(); Snap("settings_" + pageName.ToLowerInvariant() + "_rotate_left"); }
                    }
                }
                CloseSettings();
                RotateLeft();
                bool moving = Motion.AnyRunning || Motion.Active().Any();
                HuntLog("Rotate left: " + turn() + (turn() == 270 ? " ok" : " FAIL") + ", animation running " + moving);
                After(700, delegate
                {
                    Snap("rotated_left");
                    Rotate();
                    HuntLog("then Rotate right: " + turn() + (turn() == 0 ? " ok" : " FAIL"));
                    After(600, delegate
                    {
                        RotateButton(false); int a = turn();
                        RotateButton(true); int b = turn();
                        HuntLog("button (set to right): click -> " + a + ", right-click -> " + b + (a == 90 && b == 0 ? " ok" : " FAIL"));
                        rotateLeft = true; UpdateRotateButton();
                        HuntLog("set to left, tip: " + tip() + "; arrow mirrored " + (F<Glyph>("RotateGlyph").LayoutTransform is ScaleTransform));
                        RotateButton(false); a = turn();
                        RotateButton(true); b = turn();
                        HuntLog("button (set to left): click -> " + a + ", right-click -> " + b + (a == 270 && b == 0 ? " ok" : " FAIL"));
                        SaveSettings();
                        HuntLog("settings line: " + string.Join(", ", File.ReadAllLines(SettingsFile).Where(l => l.StartsWith("rotateLeft"))));
                        // Saved into the file, like the normal rotate.
                        After(600, delegate
                        {
                            HuntLog("file before: " + onDisk(photo));
                            saveRotation = true;
                            RotateLeft();
                            After(2500, delegate
                            {
                                HuntLog("file after Rotate left with saving on: " + onDisk(photo) + ", shown turn " + turn() + ", remembered " + (TurnMemory.Get(photo) == null ? "nothing (it's in the file)" : "" + TurnMemory.Get(photo)));
                                saveRotation = false;
                                HuntOpen("clip.mp4", delegate
                                {
                                    if (playing) TogglePlay();
                                    RotateLeft();
                                    HuntLog("video Rotate left: " + turn() + (turn() == 270 ? " ok" : " FAIL") + ", remembered " + TurnMemory.Get(shown.File));
                                    After(700, delegate { Snap("video_rotated_left"); HuntDone(); });
                                });
                            });
                        });
                    });
                });
            });
        }

        void HuntConvert()
        {
            string dir = Path.GetDirectoryName(files[0]);
            var names = files.Select(Path.GetFileName).ToList();
            var before = files.ToDictionary(f => f, FileHash);
            int step = 0;
            Action next = null;
            next = delegate
            {
                if (step >= names.Count)
                {
                    foreach (var f in before.Keys) HuntLog("original " + Path.GetFileName(f) + (File.Exists(f) && FileHash(f) == before[f] ? " untouched ok" : " CHANGED FAIL"));
                    var left = Directory.GetFiles(dir, "*.tmp");
                    HuntLog("temporary files left: " + left.Length + (left.Length == 0 ? " ok" : " FAIL"));
                    // Through the window once: the message and its button.
                    HuntOpen(names.First(n => n.EndsWith(".webp") || n.EndsWith(".png")), delegate
                    {
                        ConvertPhoto(".jpg");
                        Until(() => toastKind == "done" || toastKind == "error", 15000, delegate
                        {
                            HuntLog("window: message '" + F<TextBlock>("ToastText").Text + "', button '" + F<Button>("ToastBtn").Content + "', kind " + toastKind + (toastKind == "done" ? " ok" : " FAIL"));
                            Settle(); Snap("convert_message");
                            HuntDone();
                        });
                    });
                    return;
                }
                string name = names[step++];
                HuntOpen(name, delegate
                {
                    HuntLog(name + ": menu [" + Headers(ConvertMenuItems()) + "]");
                    foreach (var ext in new[] { ".jpg", ".png" })
                    {
                        if (Path.GetExtension(name).ToLowerInvariant() == ext) continue;
                        try
                        {
                            string made = ConvertPhotoFile(shown.File, ext, 0), again = ConvertPhotoFile(shown.File, ext, 0);
                            using (var a = File.OpenRead(shown.File))
                            using (var b = File.OpenRead(made))
                            {
                                var src = BitmapDecoder.Create(a, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                                var dst = BitmapDecoder.Create(b, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                                var px = new byte[4];
                                new FormatConvertedBitmap(dst, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), px, 4, 0);
                                bool same = src.PixelWidth == dst.PixelWidth && src.PixelHeight == dst.PixelHeight;
                                HuntLog("  -> " + Path.GetFileName(made) + " (source read as " + src.Format + ", copy as " + dst.Format + ") " + dst.PixelWidth + "x" + dst.PixelHeight + " (" + (new FileInfo(made).Length / 1024) + " KB), corner B" + px[0] + " G" + px[1] + " R" + px[2] + " A" + px[3] +
                                    ", second time -> " + Path.GetFileName(again) + (same && made != again && File.Exists(again) ? " ok" : " FAIL"));
                            }
                        }
                        catch (Exception ex) { HuntLog("  -> " + ext + " " + ex.Message + " FAIL"); }
                    }
                    next();
                });
            };
            After(1500, next);
        }

        void HuntSubs()
        {
            // The reader on its own.
            var a = SubtitleFile.Parse("﻿1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> {\\an8}there\r\nsecond line\r\n\r\n2\r\n00:00:03,000 --> 00:00:02,000\r\nbackwards\r\n\r\nrubbish\r\n\r\n3\r\n01:00:00,5 --> 01:00:01,000\r\nA &amp; B\r\n");
            HuntLog("srt: " + a.Count + " lines; '" + (a.Count > 0 ? a[0].Text.Replace("\n", " / ") : "") + "' " + (a.Count > 0 ? a[0].From + "-" + a[0].To : "") + "; last '" + (a.Count > 1 ? a[1].Text + "' at " + a[1].From : "") +
                (a.Count == 2 && a[0].Text == "Hello there\nsecond line" && a[0].From == 1 && a[0].To == 2.5 && a[1].Text == "A & B" && a[1].From == 3600.5 ? " ok" : " FAIL"));
            var v = SubtitleFile.Parse("WEBVTT\n\nNOTE a note\n\ncue-1\n00:01.000 --> 00:02.000 align:start\n<v Fred>Hi\n\n00:00:00.200 --> 00:00:00.900\nFirst\n");
            HuntLog("vtt: " + v.Count + " lines, in order '" + string.Join("', '", v.Select(c => c.Text)) + "'" + (v.Count == 2 && v[0].Text == "First" && v[1].Text == "Hi" && v[1].From == 1 ? " ok" : " FAIL"));
            var back = SubtitleFile.Parse(SubtitleFile.ToSrt(a));
            HuntLog("written and read back: " + back.Count + " lines" + (back.Count == 2 && back[1].From == 3600.5 && back[0].Text == a[0].Text ? " ok" : " FAIL"));

            string dir = Path.GetDirectoryName(files[0]);
            Func<string> state = () => "button " + (F<Button>("SubBtn").Visibility == Visibility.Visible ? "shown" : "hidden") + ", on " + subsOn + ", has " + HasSubs + (HasSubs ? " (" + cues.Count + " from " + (cuesFrom == "auto" ? "auto" : Path.GetFileName(cuesFrom)) + ")" : "") +
                ", text " + (subBox.Visibility == Visibility.Visible ? "'" + subText.Text.Replace("\n", " / ") + "'" : "none");
            Action<double> at = seconds => { shown.Video.Position = TimeSpan.FromSeconds(seconds); };
            HuntOpen("clip.mp4", delegate
            {
                if (playing) TogglePlay();
                ShowOverlay();
                at(1.5);
                After(700, delegate
                {
                    UpdateTime();
                    HuntLog("clip.mp4 at 1.5 s: " + state() + (subBox.Visibility == Visibility.Visible && HasSubs ? " ok" : " FAIL"));
                    double shownLift = subLift.Y;
                    Settle(); Snap("subs_bar_showing");
                    var box = subBox.TransformToAncestor(root).TransformBounds(new Rect(0, 0, subBox.ActualWidth, subBox.ActualHeight));
                    var bar = transport.TransformToAncestor(root).TransformBounds(new Rect(0, 0, transport.ActualWidth, transport.ActualHeight));
                    HuntLog("text bottom " + box.Bottom.ToString("0") + ", control bar top " + bar.Top.ToString("0") + (box.Bottom <= bar.Top ? " clear ok" : " OVERLAP FAIL") + ", font " + subText.FontSize.ToString("0.0"));
                    overlayVisible = false; PlaceSubs(false);
                    HuntLog("lift with the bar " + shownLift.ToString("0") + ", without " + subLift.Y.ToString("0") + (shownLift < subLift.Y ? " ok" : " FAIL"));
                    overlayVisible = true; PlaceSubs(false);
                    at(2.6); // between two lines
                    After(500, delegate
                    {
                        UpdateTime();
                        HuntLog("between lines (2.6 s): " + state() + (subBox.Visibility != Visibility.Visible || Motion.Busy(subBox) ? " ok" : " FAIL"));
                        at(3.5);
                        After(500, delegate
                        {
                            UpdateTime();
                            HuntLog("second line (3.5 s): " + state());
                            ToggleSubtitles();
                            HuntLog("switched off: " + state() + (!subsOn && cueShown == -1 ? " ok" : " FAIL"));
                            ToggleSubtitles();
                            HuntLog("switched on: " + state() + (subsOn && cueShown >= 0 ? " ok" : " FAIL"));
                            HuntLog("video menu: " + string.Join(" | ", new[] { "subtitles", "smooth" }.SelectMany(id => VideoMenuItem(id)).OfType<MenuItem>().Select(m => m.Header + " > " + string.Join(", ", m.Items.OfType<MenuItem>().Select(k => k.Header)))));
                            subSize = 2; SizeSubs(); double large = subText.FontSize; subSize = 0; SizeSubs(); double small = subText.FontSize; subSize = 1; SizeSubs();
                            HuntLog("sizes small " + small.ToString("0.0") + " medium " + subText.FontSize.ToString("0.0") + " large " + large.ToString("0.0") + (small < subText.FontSize && subText.FontSize < large ? " ok" : " FAIL"));
                            HuntOpen("plain.mp4", delegate
                            {
                                After(600, delegate
                                {
                                    HuntLog("plain.mp4: " + state() + (!HasSubs && F<Button>("SubBtn").Visibility != Visibility.Visible ? " ok" : " FAIL"));
                                    ToggleSubtitles();
                                    HuntLog("  S without subtitles: message '" + F<TextBlock>("ToastText").Text + "' button '" + F<Button>("ToastBtn").Content + "'");
                                    HuntLog("  video menu: " + Headers(new[] { "subtitles", "subFile", "autoCaptions" }.SelectMany(id => VideoMenuItem(id))));
                                    UseSubtitleFile(shown.File, Path.Combine(dir, "extra.srt"));
                                    if (playing) TogglePlay();
                                    at(1.5);
                                    After(500, delegate
                                    {
                                        UpdateTime();
                                        HuntLog("  after choosing extra.srt: " + state() + (HasSubs ? " ok" : " FAIL"));
                                        UseSubtitleFile(shown.File, files[0]);
                                        HuntLog("  choosing a file that isn't subtitles: '" + F<TextBlock>("ToastText").Text + "'");
                                        HuntOpen("lang.mp4", delegate
                                        {
                                            After(600, delegate
                                            {
                                                HuntLog("lang.mp4 (lang.en.srt): " + state() + (HasSubs ? " ok" : " FAIL"));
                                                HuntOpen("web.mp4", delegate
                                                {
                                                    After(600, delegate
                                                    {
                                                        HuntLog("web.mp4 (web.vtt): " + state() + (HasSubs ? " ok" : " FAIL"));
                                                        HuntOpen("plain.mp4", delegate
                                                        {
                                                            After(600, delegate
                                                            {
                                                                HuntLog("back on plain.mp4: " + state() + (HasSubs && Path.GetFileName(cuesFrom) == "extra.srt" ? " (the chosen file is remembered) ok" : " FAIL"));
                                                                // Playing: the lines come and go by themselves.
                                                                HuntOpen("clip.mp4", delegate
                                                                {
                                                                    lock (subLog) subLog.Clear();
                                                                    at(0);
                                                                    if (!playing) TogglePlay();
                                                                    After(4600, delegate
                                                                    {
                                                                        if (playing) TogglePlay();
                                                                        lock (subLog) HuntLog("played 4.6 s, timer " + (subTimer.IsEnabled ? "STILL RUNNING FAIL" : "stopped with the pause ok") + ": " + string.Join("; ", subLog));
                                                                        ToggleMiniSmoothly();
                                                                        After(900, delegate
                                                                        {
                                                                            at(1.5);
                                                                            After(500, delegate { UpdateTime(); Settle(); Snap("subs_mini"); HuntLog("mini player: " + state() + ", font " + subText.FontSize.ToString("0.0")); HuntDone(); });
                                                                        });
                                                                    });
                                                                });
                                                            });
                                                        });
                                                    });
                                                });
                                            });
                                        });
                                    });
                                });
                            });
                        });
                    });
                });
            });
        }

        void HuntAddons()
        {
            foreach (var g in new[] { "NVIDIA GeForce RTX 3060 Ti|8192", "NVIDIA GeForce GTX 1050|2048", "Intel(R) UHD Graphics 630|1024", "AMD Radeon(TM) Graphics|512", "NVIDIA GeForce GT 710|1024", "Radeon RX 6600|8176", "none", "novulkan|x" })
            {
                GpuVerdict.Override = g; GpuVerdict.Reset();
                var v = GpuVerdict.Get();
                HuntLog("graphics '" + g + "': level " + v.Level + ", encoder for '" + v.Vendor + "': " + v.Text);
            }
            GpuVerdict.Override = "NVIDIA GeForce GTX 1050|2048"; GpuVerdict.Reset();
            bool real = huntPattern == "real";
            var page = settingsPages.First(p => ((TextBlock)p.Value.Children[0]).Text == "Videos").Value;
            Func<Addon, string> card = a2 =>
            {
                foreach (var b in FindAll<Border>(page))
                {
                    var texts = FindAll<TextBlock>(b).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    if (texts.Count == 0 || texts[0] != (a2 == Smooth.Addon ? "Make videos smoother" : "Automatic captions")) continue;
                    var button = FindAll<Button>(b).LastOrDefault();
                    return "button '" + (button == null || button.Visibility != Visibility.Visible ? "-" : button.Content) + "', texts: " + string.Join(" / ", texts.Skip(1).Where(t => t.Length < 70 || t.StartsWith("Your") || t.StartsWith("The download") || t.StartsWith("Couldn't")));
                }
                return "CARD NOT FOUND FAIL";
            };
            After(1500, delegate
            {
                if (playing) TogglePlay();
                OpenSettings(); ShowSettingsPage(page);
                foreach (var refresh in settingsRefresh) refresh();
                Settle();
                HuntLog("smooth card, nothing installed: " + card(Smooth.Addon));
                HuntLog("captions card, nothing installed: " + card(Whisper.Addon));
                F<ScrollViewer>("SettingsScroll").ScrollToEnd(); Settle(); Snap("addons_before");
                var queue = new List<Addon> { Smooth.Addon, Whisper.Addon }.Where(x => !x.Installed).ToList();
                if (real) queue.AddRange(new[] { Vocals.Addon, Whisper.Large }.Where(x => !x.Installed)); // (1.25.0; no stand-ins for these)
                Action run = null;
                run = delegate
                {
                    if (queue.Count == 0)
                    {
                        Settle(); Snap("addons_installed");
                        lock (Addon.Log) foreach (var l in Addon.Log) HuntLog("  " + l);
                        if (real) { HuntDone(); return; }
                        // A download that isn't the expected file is refused and nothing of it stays.
                        Smooth.Addon.Remove();
                        HuntLog("after Remove: installed " + Smooth.Addon.Installed + ", folder there " + Directory.Exists(Smooth.Addon.Dir) + (!Smooth.Addon.Installed ? " ok" : " FAIL") + "; " + card(Smooth.Addon));
                        Addon.TrustSource = false;
                        Smooth.Addon.Install();
                        Until(() => !Smooth.Addon.Busy, 60000, delegate
                        {
                            After(300, delegate
                            {
                                HuntLog("stand-in files with the fingerprints checked: installed " + Smooth.Addon.Installed + (!Smooth.Addon.Installed && Smooth.Addon.Error != null ? " (refused) ok" : " FAIL") + "; " + card(Smooth.Addon));
                                var parts = Directory.Exists(Smooth.Addon.Dir) ? Directory.GetFiles(Smooth.Addon.Dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray() : new string[0];
                                HuntLog("files kept from the refused download: " + parts.Length + (parts.Length == 0 ? " ok" : " (" + string.Join(", ", parts) + ") FAIL"));
                                Settle(); Snap("addons_refused");
                                // Asked for from the menu before it's there: the message offers the download.
                                CloseSettings();
                                GpuVerdict.Override = "NVIDIA GeForce GT 710|1024"; GpuVerdict.Reset();
                                SmoothVideo(2);
                                HuntLog("smooth on a weak card: '" + F<TextBlock>("ToastText").Text + "' button '" + F<Button>("ToastBtn").Content + "'");
                                GpuVerdict.Override = "NVIDIA GeForce RTX 3060 Ti|8192"; GpuVerdict.Reset();
                                SmoothVideo(2);
                                HuntLog("smooth, not downloaded yet: '" + F<TextBlock>("ToastText").Text + "' button '" + F<Button>("ToastBtn").Content + "'");
                                Settle(); Snap("addons_offer");
                                Addon.TrustSource = true;
                                Whisper.Addon.Remove();
                                MakeCaptions();
                                HuntLog("captions, not downloaded yet: '" + F<TextBlock>("ToastText").Text + "' button '" + F<Button>("ToastBtn").Content + "'");
                                HuntDone();
                            });
                        });
                        return;
                    }
                    var a = queue[0];
                    queue.RemoveAt(0);
                    var seen = new List<string>();
                    bool pictured = false;
                    a.Install();
                    Until(delegate
                    {
                        if (a.Busy)
                        {
                            string s = Megabytes(a.Loaded);
                            if (seen.Count == 0 || seen[seen.Count - 1] != s) seen.Add(s);
                            if (!pictured && a.Loaded > a.Size / 3) { pictured = true; Settle(); HuntLog("  while downloading: " + card(a)); Snap("addons_downloading_" + a.Id); }
                        }
                        return !a.Busy;
                    }, real ? 1800000 : 120000, delegate
                    {
                        After(400, delegate
                        {
                            HuntLog(a.Id + ": installed " + a.Installed + (a.Installed ? " ok" : " FAIL " + a.Error) + ", progress seen " + seen.Count + " steps (" + string.Join(" ", seen.Take(4)) + " ... " + seen.LastOrDefault() + " of " + Megabytes(a.Size) + "); " + card(a));
                            HuntLog("  files: " + string.Join(", ", Directory.GetFiles(a.Dir, "*", SearchOption.AllDirectories).Select(f => f.Substring(a.Dir.Length + 1) + " " + new FileInfo(f).Length)));
                            run();
                        });
                    });
                };
                run();
            });
        }

        void HuntCaptions()
        {
            HuntLog("whisper installed: " + Whisper.Addon.Installed + " in " + Whisper.Addon.Dir);
            HuntOpen("speech.mp4", delegate
            {
                if (playing) TogglePlay();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var texts = new List<string>();
                MakeCaptions();
                Until(delegate
                {
                    string t = F<TextBlock>("ToastText").Text;
                    if (converting && (texts.Count == 0 || texts[texts.Count - 1] != t)) texts.Add(t);
                    return !converting;
                }, 900000, delegate
                {
                    After(500, delegate
                    {
                        HuntLog("result: " + lastCaptionResult + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s for a " + shown.Video.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.0") + " s video; message '" + F<TextBlock>("ToastText").Text + "'");
                        HuntLog("progress messages: " + texts.Count + " (" + string.Join(" > ", texts.Where((x, i) => i % Math.Max(1, texts.Count / 6) == 0)) + ")");
                        if (cues != null) foreach (var c in cues.Take(40)) HuntLog("  " + c.From.ToString("0.00") + " - " + c.To.ToString("0.00") + "  " + c.Text);
                        lock (Whisper.Log) foreach (var l in Whisper.Log.Take(12)) HuntLog("  whisper said: " + l);
                        string kept = Whisper.CaptionsFile(shown.File);
                        HuntLog("kept for next time: " + (kept != null && File.Exists(kept)) + ", button " + F<Button>("SubBtn").Visibility + ", temporary sound files left: " + Directory.GetFiles(Path.Combine(Path.GetTempPath(), "FastViewer"), "speech_*.wav").Length);
                        if (HasSubs) { shown.Video.Position = TimeSpan.FromSeconds((cues[0].From + cues[0].To) / 2); }
                        After(600, delegate
                        {
                            UpdateTime(); ShowOverlay(); Settle(); Snap("captions");
                            // Another file and back: they are simply there again.
                            Go(1);
                            After(1500, delegate
                            {
                                Go(-1);
                                After(1500, delegate { HuntLog("after leaving and coming back: has " + HasSubs + " from " + cuesFrom + (HasSubs && cuesFrom == "auto" ? " ok" : " FAIL") + "; menu: " + string.Join(" | ", VideoMenuItem("subtitles").OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).Select(m => m.Header))); HuntDone(); });
                            });
                        });
                    });
                });
            });
        }

        void HuntSmooth()
        {
            var v = GpuVerdict.Get();
            HuntLog("graphics card: " + v.Name + ", " + v.Megabytes + " MB, level " + v.Level + "; own FrameBoost: " + (Smooth.Own ?? "no") + "; add-on installed: " + Smooth.Addon.Installed);
            HuntOpen("clip.mp4", delegate
            {
                if (playing) TogglePlay();
                string dir = Path.GetDirectoryName(shown.File);
                int times = huntPattern == "4" ? 4 : 2;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var texts = new List<string>();
                SmoothVideo(times);
                bool pictured = false;
                Until(delegate
                {
                    string t = F<TextBlock>("ToastText").Text;
                    if (converting && (texts.Count == 0 || texts[texts.Count - 1] != t)) texts.Add(t);
                    if (converting && !pictured && t.Contains("new frames") && t.Contains("left")) { pictured = true; Snap("smooth_progress"); }
                    return !converting;
                }, 1800000, delegate
                {
                    After(500, delegate
                    {
                        HuntLog("took " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s; message '" + F<TextBlock>("ToastText").Text + "' button '" + F<Button>("ToastBtn").Content + "'");
                        HuntLog("progress messages: " + texts.Count + " (" + string.Join(" > ", texts.Where((x, i) => i % Math.Max(1, texts.Count / 8) == 0)) + ")");
                        lock (Smooth.Log) foreach (var l in Smooth.Log.Where(x => !x.StartsWith("frame=")).Take(25)) HuntLog("  " + l);
                        HuntLog("files in the folder: " + string.Join(", ", Directory.GetFiles(dir).Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length)));
                        var temp = Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "FastViewer"), "Smooth_*");
                        HuntLog("work folders left: " + temp.Length + (temp.Length == 0 ? " ok" : " FAIL"));
                        Settle(); Snap("smooth_done");
                        HuntDone();
                    });
                });
            });
        }
        // --case captionsall (1.25.0): captions for every video and song in the folder, one after the other, the way
        // the menu item makes them (whatever add-ons are in --addons: small or large model, voice isolation or not).
        // Each file's text goes to <out>\<name>.txt, for counting word mistakes against the real words.
        void HuntCaptionsAll()
        {
            HuntLog("whisper " + Whisper.Addon.Installed + ", large model " + Whisper.UsesLarge + ", voice isolation " + Vocals.Addon.Installed);
            var names = files.Where(f => IsVideoFile(f) || IsAudioFile(f)).Select(f => Path.GetFileName(f)).ToList();
            Action<int> one = null;
            one = delegate(int i)
            {
                if (i >= names.Count) { HuntDone(); return; }
                HuntOpen(names[i], delegate
                {
                    if (playing) TogglePlay();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    lastCaptionResult = null;
                    MakeCaptions();
                    Until(() => !converting && lastCaptionResult != null, 3600000, delegate
                    {
                        double seconds = shown.Video.NaturalDuration.HasTimeSpan ? shown.Video.NaturalDuration.TimeSpan.TotalSeconds : 0;
                        HuntLog(names[i] + ": " + lastCaptionResult + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s for " + seconds.ToString("0.0") + " s");
                        File.WriteAllText(Path.Combine(snapDir, Path.GetFileNameWithoutExtension(names[i]) + ".txt"), HasSubs ? string.Join("\r\n", cues.Select(c => c.Text)) : "");
                        After(300, delegate { one(i + 1); });
                    });
                });
            };
            one(0);
        }

        // --case burn (1.25.0): scratch folder with two.mp4 (two sound tracks) + two.srt, and tall.mp4 + tall.srt (an
        // upright video). Saves a copy with captions of each and a Send to Discord copy with "Burn in captions"
        // into the output folder; check the text and the sound tracks of each "out *.mp4" with ffmpeg.
        void HuntBurn()
        {
            After(1500, () => HuntOpen("two.mp4", () => Until(() => HasSubs, 5000, () =>
            {
                if (playing) TogglePlay();
                string file = shown.File;
                int w = shown.Video.NaturalVideoWidth, h = shown.Video.NaturalVideoHeight, tracks = VideoConverter.SoundTracks(file);
                double fps = 1 / shown.FrameSeconds, length = shown.Video.NaturalDuration.TimeSpan.TotalSeconds;
                Func<string, string> facts = name =>
                {
                    int pw, ph, found = VideoConverter.SoundTracks(name);
                    double sec;
                    VideoConverter.Probe(name, out pw, out ph, out sec);
                    return pw + "x" + ph + " " + sec.ToString("0.00") + " s, " + found + " sound tracks" + (found == tracks ? "" : " FAIL");
                };
                HuntLog("two.mp4: " + facts(file) + ", " + cues.Count + " lines from " + Path.GetFileName(cuesFrom) + "; video menu: " + string.Join(" | ", VideoMenuItem("subtitles").OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).Select(m => m.Header)));
                string copy = Path.Combine(snapDir, "out captions.mp4"), discord = Path.Combine(snapDir, "out discord.mp4"), tall = Path.Combine(snapDir, "out tall.mp4");
                var painter = MakePainter(file, null);
                RunConversion(file, copy, null, null, "Saving a copy with captions", new ConvertOptions { Paint = painter.Paint });
                Until(() => !converting, 300000, () =>
                {
                    HuntLog("copy with captions: " + facts(copy) + ", text on " + painter.Painted + " pictures, " + painter.Lines + " lines" + (painter.Lines == cues.Count ? "" : " FAIL"));
                    discordBurn = true;
                    lastShareResult = null;
                    ShareToDiscord(file, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 1, w, h, fps, true, length);
                    Until(() => !converting && lastShareResult != null, 300000, () =>
                    {
                        string made = lastShareResult.StartsWith("made ") ? lastShareResult.Substring(lastShareResult.LastIndexOf(", ") + 2) : null;
                        if (made != null && File.Exists(made)) File.Copy(made, discord, true);
                        HuntLog("Send to Discord 1-3 s with Burn in captions: " + lastShareResult + "; " + (File.Exists(discord) ? facts(discord) : "no copy FAIL") + "; message '" + F<TextBlock>("ToastText").Text + "'");
                        // Without captions the small whole file is copied as it is; with the tick it must be made anew.
                        discordBurn = false;
                        HuntOpen("tall.mp4", () => Until(() => HasSubs, 5000, () =>
                        {
                            if (playing) TogglePlay();
                            var p2 = MakePainter(shown.File, null);
                            RunConversion(shown.File, tall, null, null, "Saving a copy with captions", new ConvertOptions { Paint = p2.Paint });
                            Until(() => !converting, 300000, () =>
                            {
                                HuntLog("upright copy with captions: text on " + p2.Painted + " pictures, " + p2.Lines + " lines");
                                HuntOpen("two.mp4", () => { HuntLog("a video without subtitles offers: " + (HasSubs ? "(has subtitles)" : "")); HuntDone(); });
                            });
                        }));
                    });
                });
            })));
        }

        // --case videomenu (1.25.0): scratch folder with two.mp4 + two.srt. Lists the video "..." menu, works its speed
        // slider, and saves a picture of the menu with its two smaller menus drawn beside it (run once per --look).
        void HuntVideoMenu()
        {
            After(1500, () => HuntOpen("two.mp4", () => Until(() => HasSubs, 5000, () =>
            {
                if (playing) TogglePlay();
                ShowOverlay();
                var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate TargetType='Menu' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                    "<Border Background='{DynamicResource MenuBg}' BorderBrush='{DynamicResource MenuEdge}' BorderThickness='1' " +
                    "CornerRadius='{DynamicResource Radius.Float}' Padding='4' MinWidth='200'><StackPanel IsItemsHost='True'/></Border></ControlTemplate>");
                Func<IEnumerable<object>, double, double, Border> draw = (list, x, y) =>
                {
                    var copy = new Menu { Template = template, FontSize = 14 };
                    copy.SetResourceReference(Control.ForegroundProperty, "TextMain");
                    copy.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
                    foreach (var it in list) copy.Items.Add(it);
                    var host = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = copy, Margin = new Thickness(x, y, 0, 0) };
                    Grid.SetRowSpan(host, 3);
                    Panel.SetZIndex(host, 100);
                    root.Children.Add(host);
                    host.UpdateLayout();
                    return host;
                };
                Func<MenuItem, string> name = m => (m.Header is string ? (string)m.Header : "[" + System.Windows.Automation.AutomationProperties.GetName(m) + " row]")
                    + (m.InputGestureText.Length > 0 ? " (" + m.InputGestureText + ")" : "") + (m.HasItems ? " >" : "");
                var items = ChosenItems(true, VideoMenuItem);
                HuntLog("video menu, " + items.OfType<MenuItem>().Count() + " items: " + string.Join(" | ", items.OfType<MenuItem>().Select(name)));
                HuntLog("Settings > Buttons list: " + string.Join(" | ", VideoChoices.Select(c => c.Name)));
                var main = draw(items.Cast<object>(), 16, 48);
                double beside = 16 + main.ActualWidth + 8, down = 48;
                foreach (var id in new[] { "subtitles" })
                {
                    var parent = VideoMenuItem(id).OfType<MenuItem>().FirstOrDefault();
                    if (parent == null) { HuntLog("  " + id + ": not offered"); continue; }
                    var kids = parent.Items.Cast<object>().ToList();
                    parent.Items.Clear();
                    HuntLog("  " + parent.Header + " > " + string.Join(" | ", kids.OfType<MenuItem>().Select(name)));
                    var side = draw(kids, beside, down);
                    down += side.ActualHeight + 8;
                }
                var row = items.OfType<MenuItem>().First(m => m.Header is Grid);
                var slider = ((Grid)row.Header).Children.OfType<Slider>().First();
                var button = ((Grid)row.Header).Children.OfType<Button>().First();
                var seen = new List<string>();
                for (int i = 0; i <= (int)slider.Maximum; i++) { slider.Value = i; seen.Add(SpeedText(speed) + "=" + button.Content); }
                HuntLog("slider stops: " + string.Join(" ", seen) + (seen.Count == AllSpeeds.Length && speed == 5 ? " ok" : " FAIL"));
                slider.Value = 4.4; // let go between two stops
                HuntLog("let go at 4.4: stop " + slider.Value + ", speed " + SpeedText(speed) + (speed == 1.25 ? " ok" : " FAIL"));
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                HuntLog("click on the speed: " + SpeedText(speed) + ", slider at " + slider.Value + (speed == 1 && slider.Value == 3 ? " ok" : " FAIL"));
                StepSpeed(1); StepSpeed(1);
                HuntLog("Faster key twice: " + SpeedText(speed) + (speed == 1.5 ? " ok" : " FAIL"));
                StepSpeed(0);
                slider.Value = 5;
                HuntLog("row " + row.ActualWidth.ToString("0") + " wide, slider " + slider.ActualWidth.ToString("0") + ", menu " + main.ActualWidth.ToString("0") + " x " + main.ActualHeight.ToString("0"));
                Settle(); Snap("videomenu_" + Looks.Current.Id);
                SetSpeed(1);
                var smoothRow = items.OfType<MenuItem>().FirstOrDefault(m => System.Windows.Automation.AutomationProperties.GetName(m) == "Make smoother");
                if (smoothRow == null) HuntLog("smoother row: not offered");
                else
                {
                    var sb = ((Grid)smoothRow.Header).Children.OfType<Button>().ToList();
                    HuntLog("smoother row: '" + ((Grid)smoothRow.Header).Children.OfType<TextBlock>().First().Text + "' buttons " + string.Join(" ", sb.Select(b => b.Content + " (" + b.ActualWidth.ToString("0") + "x" + b.ActualHeight.ToString("0") + ")"))
                        + ", side menu " + smoothRow.HasItems + (sb.Count == 2 && !smoothRow.HasItems ? " ok" : " FAIL"));
                }
                // Reaching for a side menu, on made-up rectangles: menu 300x500, its item a row in the middle, side menu right or left of it.
                foreach (bool onRight in new[] { true, false })
                {
                    Func<MenuAim> fresh = () => new MenuAim { Menu = new Rect(600, 100, 300, 500), Row = new Rect(604, 300, 292, 38),
                        Side = onRight ? new Rect(903, 295, 220, 200) : new Rect(377, 295, 220, 200) };
                    double dir = onRight ? 1 : -1, x0 = 750, endX = onRight ? 910 : 590;
                    var aim = fresh();
                    long ms = 0; bool closed = false, held = true, shield = true;
                    aim.Step(new Point(x0, 319), ms);
                    for (int i = 1; i <= 20; i++) // down across three other rows, toward the side menu's far end
                    {
                        var c = new Point(x0 + (endX - x0) * i / 20, 319 + (470 - 319) * i / 20.0);
                        ms += 15; closed |= aim.Step(c, ms); held &= aim.Hold;
                        if (aim.Menu.Contains(c) && !aim.Row.Contains(c) && c.Y > 340 && Math.Abs(c.X - endX) > 12) shield &= aim.Shield;
                    }
                    HuntLog("aim, side menu " + (onRight ? "right" : "left") + ", diagonal to it: held " + held + ", other rows quiet " + shield + ", closed " + closed + (held && shield && !closed ? " ok" : " FAIL"));
                    aim = fresh(); ms = 0; closed = false;
                    aim.Step(new Point(x0, 319), ms);
                    for (int i = 1; i <= 6; i++) { ms += 15; aim.Step(new Point(x0 + dir * 8 * i, 319 + 8 * i), ms); }
                    bool quiet = aim.Shield; long stopped = ms, at = -1;
                    for (int i = 0; i < 40 && at < 0; i++) { ms += 15; if (aim.Step(new Point(x0 + dir * 48, 367), ms)) at = ms - stopped; }
                    HuntLog("  stops on another row on the way: quiet " + quiet + ", then switches after " + at + " ms" + (quiet && at >= 300 && at <= 400 && !aim.Hold ? " ok" : " FAIL"));
                    aim = fresh(); ms = 0;
                    aim.Step(new Point(x0, 319), ms);
                    for (int i = 1; i <= 6; i++) { ms += 15; aim.Step(new Point(x0, 319 + 12 * i), ms); }
                    bool goesDown = !aim.Shield && !aim.Hold;
                    for (int i = 1; i <= 6; i++) { ms += 15; aim.Step(new Point(x0 - dir * 10 * i, 391), ms); }
                    HuntLog("  straight down, and away from it: rows react at once " + (goesDown && !aim.Shield && !aim.Hold) + (goesDown && !aim.Shield && !aim.Hold ? " ok" : " FAIL"));
                    aim = fresh();
                    bool safe = true;
                    foreach (var c in new[] { new Point(onRight ? 901 : 598, 400), new Point(onRight ? 1000 : 480, 480), new Point(onRight ? 899 : 600, 330) })
                    { safe &= !aim.Step(c, 5000) && aim.Hold && !aim.Shield; }
                    HuntLog("  over the gap, the side menu, the item: never closes " + safe + (safe ? " ok" : " FAIL"));
                }
                // The real item with its real side menu (drawn in the window's menu above), the pointer played by the test.
                var real = items.OfType<SideMenuItem>().FirstOrDefault();
                if (real == null) { HuntLog("real side menu: none in the menu FAIL"); HuntDone(); return; }
                var others = items.OfType<MenuItem>().Where(m => m != real && m.IsHitTestVisible).ToList();
                Point cursor = real.PointToScreen(new Point(real.ActualWidth / 2, real.ActualHeight / 2));
                SideMenuItem.TestCursor = () => cursor;
                real.IsSubmenuOpen = true;
                After(500, () =>
                {
                    var popup = (System.Windows.Controls.Primitives.Popup)real.Template.FindName("PART_Popup", real);
                    var sideEl = (FrameworkElement)popup.Child;
                    Point s0 = sideEl.PointToScreen(new Point(0, 0)), s1 = sideEl.PointToScreen(new Point(sideEl.ActualWidth, sideEl.ActualHeight));
                    bool isRight = s0.X > cursor.X;
                    HuntLog("real side menu: open " + real.IsSubmenuOpen + ", " + sideEl.ActualWidth.ToString("0") + " x " + sideEl.ActualHeight.ToString("0") + ", on the " + (isRight ? "right" : "left")
                        + ", " + (isRight ? s0.X - real.PointToScreen(new Point(real.ActualWidth, 0)).X : real.PointToScreen(new Point(0, 0)).X - s1.X).ToString("0") + " px from the item");
                    real.Tick();
                    real.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false);
                    HuntLog("  pointer on the item, WPF asks to close: open " + real.IsSubmenuOpen + (real.IsSubmenuOpen ? " ok" : " FAIL"));
                    // toward the side menu's bottom corner, across the rows below the item
                    Point start = cursor, goal = new Point(isRight ? s0.X + 6 : s1.X - 6, s1.Y - 6);
                    bool allQuiet = true, stayed = true; int n = 0;
                    Action step = null;
                    step = () =>
                    {
                        n++;
                        cursor = new Point(start.X + (goal.X - start.X) * n / 12, start.Y + (goal.Y - start.Y) * n / 12);
                        real.Tick();
                        real.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false); // what WPF's own timer does
                        stayed &= real.IsSubmenuOpen;
                        bool overOther = real.Aim != null && real.Aim.Menu.Contains(cursor) && !real.Aim.Row.Contains(cursor) && n > 2 && n < 10;
                        if (overOther) allQuiet &= others.All(m => !m.IsHitTestVisible);
                        if (n < 12) { After(20, step); return; }
                        HuntLog("  12 steps to its far corner, WPF asking to close each time: stayed open " + stayed + ", other rows quiet on the way " + allQuiet
                            + ", rows back once there " + others.All(m => m.IsHitTestVisible) + (stayed && allQuiet && others.All(m => m.IsHitTestVisible) ? " ok" : " FAIL"));
                        // back to the item, then head for it again but stop on a row halfway
                        cursor = start; real.Tick();
                        cursor = new Point(start.X + (goal.X - start.X) * 0.25, start.Y + (goal.Y - start.Y) * 0.25); real.Tick();
                        cursor = new Point(start.X + (goal.X - start.X) * 0.4, start.Y + (goal.Y - start.Y) * 0.4); real.Tick();
                        bool q = others.All(m => !m.IsHitTestVisible);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        Until(() => !real.IsSubmenuOpen, 2000, () =>
                        {
                            long took = watch.ElapsedMilliseconds;
                            HuntLog("  stopped on a row halfway: rows quiet " + q + ", side menu closed " + !real.IsSubmenuOpen + " after " + took + " ms, rows back " + others.All(m => m.IsHitTestVisible)
                                + (q && !real.IsSubmenuOpen && took >= 300 && took <= 600 && others.All(m => m.IsHitTestVisible) ? " ok" : " FAIL"));
                            SideMenuItem.TestCursor = null;
                            HuntDone();
                        });
                    };
                    After(20, step);
                });
            })));
        }

    }
}
