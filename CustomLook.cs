// FastViewer: the Custom look (Settings > Custom look). It starts as a copy of one of the built-in looks and keeps
// only what you change on top of it: colours (dark and light separately), font, corner roundness, bars,
// see-through, icons and motion. Every change shows on the whole window at once. A look can be exported to a
// small .fvlook text file and imported again (also by dropping the file on the window), so friends can share looks.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FastViewer
{
    partial class ViewerWindow
    {
        const string CustomId = "Custom";
        string customBase = Looks.DefaultId, customName = "My look";
        // What you changed: "Dark.Accent" / "Light.PageBg" = #AARRGGBB, and font, titleWeight, roundness, bars,
        // seeThrough, icons, motionStyle, motionSpeed. Anything not in here comes from the base look.
        readonly Dictionary<string, string> custom = new Dictionary<string, string>(StringComparer.Ordinal);

        // The main colours: one choice sets a few related parts at once (all of them are under "All colours").
        static readonly string[][] ColorGroups =
        {
            new[] { "Accent", "Highlights, switches, the time bar and selected items", "Accent" },
            new[] { "Background", "Around and behind the picture", "WindowBg", "StageBg", "CoverBg", "StripBg" },
            new[] { "Bars", "The top and bottom bars and the File info pane", "TopBarBg", "BottomBarBg", "PanelBg" },
            new[] { "Pages", "Settings and the grid of all files", "PageBg" },
            new[] { "Cards and menus", "Settings cards, menus, tooltips, messages, the video control bar and dialogs", "CardBg", "MenuBg", "TipBg", "FloatBg", "DialogBg" },
            new[] { "Text", "Text, button labels and icons", "TextMain", "ControlText", "ControlHoverText" },
            new[] { "Secondary text", "Descriptions and hints", "TextSecondary", "TextDim" },
        };

        static readonly Dictionary<string, string> ColorNames = new Dictionary<string, string>
        {
            { "WindowBg", "Window" }, { "StageBg", "Behind the picture" }, { "CoverBg", "Behind the picture while switching" },
            { "FullBg", "Full screen background" }, { "TopBarBg", "Top bar" }, { "BottomBarBg", "Bottom bar" },
            { "StripBg", "Thumbnail strip" }, { "PanelBg", "File info pane" }, { "PanelEdge", "File info pane edge" },
            { "PageBg", "Settings and grid pages" }, { "CardBg", "Settings cards" }, { "CardEdge", "Settings card edges" },
            { "NavSelected", "Chosen Settings page" }, { "TextMain", "Text" }, { "TextSecondary", "Secondary text" },
            { "TextDim", "Hints" }, { "Divider", "Dividers" }, { "HoverFill", "Button you point at" },
            { "ControlFill", "Buttons" }, { "ControlEdge", "Button edges" }, { "ControlText", "Button text and icons" },
            { "ControlHoverText", "Button text you point at" }, { "FloatBg", "Video control bar and messages" },
            { "FloatEdge", "Video control bar edges" }, { "MenuBg", "Menus" }, { "MenuHover", "Menu item you point at" },
            { "MenuEdge", "Menu edges" }, { "TipBg", "Tooltips" }, { "TipEdge", "Tooltip edges" }, { "ArrowBg", "Side arrows" },
            { "ArrowEdge", "Side arrow edges" }, { "CenterBg", "Middle play button" }, { "CenterHover", "Middle play button you point at" },
            { "CenterEdge", "Middle play button edge" }, { "SwitchTrack", "Switches (off)" }, { "SwitchEdge", "Switch edges" },
            { "SwitchKnob", "Switch knobs" }, { "SwitchKnobOn", "Switch knobs (on)" }, { "SliderRestFill", "Slider tracks" },
            { "SliderThumbBg", "Slider knobs" }, { "SliderThumbEdge", "Slider knob edges" }, { "ScrollThumb", "Scroll bars" },
            { "CaptionHover", "Window buttons you point at" }, { "CloseHover", "Close button you point at" }, { "Accent", "Accent" },
            { "AccentText", "Text on the accent colour" }, { "ScrimFill", "Shade behind the video controls" },
            { "DialogShade", "Shade behind dialogs" }, { "DialogBg", "Dialogs" }, { "DialogEdge", "Dialog edges" },
            { "DialogFooter", "Dialog button area" }, { "ThumbPlaceholder", "Thumbnails still loading" },
            { "ThumbHover", "Thumbnail you point at" }, { "BadgeBg", "Badges on thumbnails" }, { "LoopRangeFill", "A-B loop band" },
            { "LoopRangeEdge", "A-B loop band edge" }, { "LensBg", "Glass lens (springy motion)" }, { "LensEdge", "Glass lens edge" },
        };

        static readonly string[] FontChoices = { "Segoe UI", "Segoe UI Variable Text", "Inter", "Bahnschrift", "Calibri", "Verdana", "Georgia", "Trebuchet MS", "Consolas", "Comic Sans MS" };
        static readonly string[] WeightChoices = { "Light", "Normal", "SemiBold", "Bold" };
        static readonly string[] MotionStyles = { "windows", "subtle", "springy", "calm", "snappy" };
        static readonly string[] MotionSpeeds = { "0.4", "0.55", "0.7", "1", "1.4", "2", "3" }; // x the style's durations

        // ---------------------------------------------------------------- building the look

        // Looks.Custom: the base look's values with your changes on top.
        void BuildCustomLook()
        {
            var b = Looks.Find(customBase == CustomId ? Looks.DefaultId : customBase);
            var d = new ResourceDictionary();
            foreach (DictionaryEntry e in b.Source) d[e.Key] = e.Value;
            d["Look.Name"] = customName;
            d["Look.Description"] = "Your own look, based on " + b.Name + ".";
            foreach (var kv in custom)
            {
                try { ApplyCustomValue(d, b, kv.Key, kv.Value); }
                catch { } // a bad value (edited by hand, an odd file) is simply left out
            }
            ApplyMotionSpeed(d);
            Looks.Custom = new Look { Id = CustomId, Name = customName, Description = (string)d["Look.Description"], HasLight = b.HasLight, Order = 99, Source = d };
        }

        static void ApplyCustomValue(ResourceDictionary d, Look b, string key, string v)
        {
            if (key.StartsWith("Dark.") || key.StartsWith("Light."))
            {
                if (!ColorNames.ContainsKey(key.Substring(key.IndexOf('.') + 1))) return;
                var brush = new SolidColorBrush(ParseColor(v));
                brush.Freeze();
                d[key] = brush;
                return;
            }
            switch (key)
            {
                case "font":
                    d["Font.Ui"] = v == "Inter" ? "@Inter" : "@" + v;
                    d["Font.Title"] = v == "Inter" ? "@Inter Display" : "@" + v;
                    break;
                case "titleWeight":
                    d["Weight.Title"] = (FontWeight)new FontWeightConverter().ConvertFromInvariantString(v);
                    break;
                case "roundness":
                    double r = Math.Max(0, Math.Min(24, double.Parse(v, CultureInfo.InvariantCulture)));
                    d["Radius.Control"] = new CornerRadius(Math.Round(r * 0.55));
                    d["Radius.Float"] = new CornerRadius(r);
                    d["Radius.Card"] = new CornerRadius(Math.Round(r * 0.85));
                    d["Radius.Center"] = new CornerRadius(Math.Min(40, Math.Round(r * 2.2)));
                    d["Radius.Dialog"] = new CornerRadius(Math.Round(r * 1.15));
                    d["Radius.DialogFooter"] = new CornerRadius(0, 0, Math.Round(r * 1.15), Math.Round(r * 1.15));
                    d["Radius.Window"] = Math.Round(r * 0.65);
                    break;
                case "bars": d["Look.BarsFloat"] = v == "floating"; break;
                case "seeThrough": d["Look.SeeThrough"] = v == "on"; break;
                case "icons":
                    d["Look.Icons"] = v == "lines" ? "Lines" : "Segoe";
                    if (v == "lines" && !(b.Get("Look.IconStroke") is double)) d["Look.IconStroke"] = 1.6;
                    break;
                case "motionStyle":
                {
                    int i = Array.IndexOf(MotionStyles, v);
                    if (i < 0) return;
                    // Each style has its own timing, travel, curve and press, so they really feel different:
                    //            Fast  Normal Slow  Distance Press FadeIn FadeOut
                    var ms = new[] {
                        new[] { 167.0, 250, 333, 1.0, 1.00, 120, 300 },   // Smooth: Windows 11, decelerating
                        new[] { 120.0, 170, 220, 0.3, 1.00,  90, 200 },   // Subtle: quick, barely moves, even curve
                        new[] { 200.0, 300, 400, 1.8, 0.92, 150, 320 },   // Springy: travels far, overshoots and wobbles
                        new[] { 300.0, 450, 600, 0.2, 1.00, 260, 700 },   // Calm: slow fades, eases in and out, hardly moves
                        new[] { 100.0, 150, 200, 1.3, 0.96,  70, 160 },   // Snappy: there almost at once, short settle
                    }[i];
                    d["Motion.Fast"] = ms[0];
                    d["Motion.Normal"] = ms[1];
                    d["Motion.Slow"] = ms[2];
                    d["Motion.Distance"] = ms[3];
                    d["Motion.Press"] = ms[4];
                    d["Motion.FadeIn"] = ms[5];
                    d["Motion.FadeOut"] = ms[6];
                    d["Motion.Ease"] = new[] { "decelerate", "subtle", "bouncy", "drift", "snappy" }[i];
                    d["Look.Spring"] = v == "springy";
                    break;
                }
            }
        }

        // Done last, so the speed applies to whatever motion style is in use.
        void ApplyMotionSpeed(ResourceDictionary d)
        {
            string v;
            if (!custom.TryGetValue("motionSpeed", out v)) return;
            double k;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out k)) return;
            d["Motion.Speed"] = Math.Max(0.4, Math.Min(3, k)); // Motion.Ms multiplies every animation by it
        }

        static Color ParseColor(string v)
        {
            v = v.Trim();
            if (!v.StartsWith("#")) v = "#" + v;
            if (v.Length != 7 && v.Length != 9) throw new FormatException();
            return (Color)ColorConverter.ConvertFromString(v);
        }

        static string ColorText(Color c)
        {
            return c.A == 255 ? string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B) : string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }

        // The colour a part has right now in the custom look (your change, else the base look, else the Windows accent).
        Color CustomColor(string part, bool light)
        {
            string v;
            if (custom.TryGetValue((light ? "Light." : "Dark.") + part, out v))
                try { return ParseColor(v); } catch { }
            return BaseColor(part, light);
        }

        Color BaseColor(string part, bool light)
        {
            var b = Looks.Find(customBase);
            var brush = b.Get((light ? "Light." : "Dark.") + part) as Brush;
            if (brush == null) return part == "Accent" ? Ui.AccentColor(light) : Colors.Transparent;
            return BrushColor(brush);
        }

        static Color BrushColor(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            if (solid != null) return solid.Color;
            var gradient = brush as GradientBrush;
            if (gradient != null && gradient.GradientStops.Count > 0) return gradient.GradientStops[0].Color;
            return Colors.Transparent;
        }

        // ---------------------------------------------------------------- changing it

        DispatcherTimer customSaveTimer, customApplyTimer;

        // Editing starts using the custom look, so you see what you change.
        void UseCustomLook()
        {
            if (lookId == CustomId) return;
            CrossfadeWindow(250);
            lookId = CustomId;
            ApplyLook();
        }

        // A colour: straight into the window's resources (fast enough to follow a dragged colour picker).
        void SetCustomColors(string[] parts, Color c, bool light, bool save)
        {
            UseCustomLook();
            foreach (var part in parts) custom[(light ? "Light." : "Dark.") + part] = ColorText(c);
            if (parts.Contains("Accent") && !custom.ContainsKey((light ? "Light." : "Dark.") + "AccentText+"))
                custom[(light ? "Light." : "Dark.") + "AccentText"] = ColorText(ReadableOn(c)); // the text on it stays readable
            BuildCustomLook();
            if (Looks.Light == light)
            {
                var r = Application.Current.Resources;
                foreach (var part in parts.Concat(parts.Contains("Accent") ? new[] { "AccentText" } : new string[0]))
                    r[part] = Looks.Custom.Source[(light ? "Light." : "Dark.") + part];
                object knob = Looks.Custom.Source[(light ? "Light." : "Dark.") + "SwitchKnobOn"];
                if (knob != null) r["SwitchKnobOn"] = knob;
                Looks.KeepSwitchKnobVisible(r); // a dark accent with a dark knob: the knob turns light
            }
            CustomChanged(save, false);
        }

        void SetCustomValue(string key, string value)
        {
            UseCustomLook();
            if (value == null) custom.Remove(key);
            else custom[key] = value;
            CustomChanged(true, true);
        }

        // relayout: something beyond colours changed (fonts, shapes, bars...): the whole look is applied again, a
        // moment later, so dragging a slider doesn't redo it for every pixel.
        void CustomChanged(bool save, bool relayout)
        {
            BuildCustomLook();
            if (relayout)
            {
                if (customApplyTimer == null)
                {
                    customApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                    customApplyTimer.Tick += delegate { customApplyTimer.Stop(); ApplyLook(); };
                }
                customApplyTimer.Stop();
                customApplyTimer.Start();
            }
            if (customSaveTimer == null)
            {
                customSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                customSaveTimer.Tick += delegate
                {
                    customSaveTimer.Stop();
                    SaveSettings();
                    RefreshCustomCard();
                    UpdateReadability();
                };
            }
            if (save || relayout)
            {
                customSaveTimer.Stop();
                customSaveTimer.Start();
            }
            foreach (var refresh in customRefresh) refresh();
        }

        readonly List<Action> customRefresh = new List<Action>();

        void StartCustomOver()
        {
            custom.Clear();
            customName = "My look";
            CustomChanged(true, false);
            ApplyLook();
            RefreshCustomCard();
            foreach (var refresh in settingsRefresh) refresh();
        }

        // Black or white, whichever reads better on a colour.
        static Color ReadableOn(Color c)
        {
            return Contrast(Colors.White, c) >= Contrast(Colors.Black, c) ? Colors.White : Colors.Black;
        }

        static double Luminance(Color c)
        {
            Func<double, double> ch = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }

        static Color Over(Color top, Color under)
        {
            double a = top.A / 255.0;
            return Color.FromRgb((byte)(top.R * a + under.R * (1 - a)), (byte)(top.G * a + under.G * (1 - a)), (byte)(top.B * a + under.B * (1 - a)));
        }

        static double Contrast(Color a, Color b)
        {
            double l1 = Luminance(a), l2 = Luminance(b);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        // ---------------------------------------------------------------- saving, sharing

        IEnumerable<string> CustomLines()
        {
            yield return "customBase=" + customBase;
            yield return "customName=" + customName.Replace("\r", " ").Replace("\n", " ");
            foreach (var kv in custom.OrderBy(k => k.Key, StringComparer.Ordinal)) yield return "custom." + kv.Key + "=" + kv.Value;
        }

        bool LoadCustomOption(string key, string v)
        {
            if (key == "customBase") { customBase = v.Length > 0 && v != CustomId ? v : Looks.DefaultId; return true; } // (an unknown one: Find gives Photos)
            if (key == "customName") { customName = v.Length > 0 ? v.Substring(0, Math.Min(40, v.Length)) : "My look"; return true; }
            if (!key.StartsWith("custom.")) return false;
            if (IsCustomKey(key.Substring(7))) custom[key.Substring(7)] = v;
            return true;
        }

        static bool IsCustomKey(string key)
        {
            if (key.StartsWith("Dark.") || key.StartsWith("Light.")) return ColorNames.ContainsKey(key.Substring(key.IndexOf('.') + 1).TrimEnd('+'));
            return new[] { "font", "titleWeight", "roundness", "bars", "seeThrough", "icons", "motionStyle", "motionSpeed" }.Contains(key);
        }

        const string LookFileHeader = "FastViewer look";

        void ExportLook()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export look",
                FileName = string.Join("_", customName.Split(System.IO.Path.GetInvalidFileNameChars())) + ".fvlook",
                DefaultExt = ".fvlook",
                Filter = "FastViewer look|*.fvlook"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllLines(dlg.FileName, LookFileLines());
                ShowToast("Exported \"" + customName + "\". Send the file to a friend: they drop it on FastViewer.", "Show in folder",
                    delegate { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\""); HideToast(null); }, 6, null);
            }
            catch (Exception ex) { ShowToast("Couldn't export the look: " + ex.Message, null, null, 6, null); }
        }

        IEnumerable<string> LookFileLines()
        {
            yield return LookFileHeader;
            yield return "name=" + customName;
            yield return "base=" + customBase;
            foreach (var kv in custom.OrderBy(k => k.Key, StringComparer.Ordinal)) yield return kv.Key + "=" + kv.Value;
        }

        void ImportLookFromDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import look", Filter = "FastViewer look|*.fvlook|All files|*.*" };
            if (dlg.ShowDialog(this) == true) ImportLook(dlg.FileName);
        }

        // Reads a .fvlook file and uses it (with Undo). Only known parts with valid values are taken.
        void ImportLook(string file)
        {
            List<string> lines;
            try { lines = File.ReadAllLines(file).Take(500).ToList(); }
            catch (Exception ex) { ShowToast("Couldn't read the look: " + ex.Message, null, null, 6, null); return; }
            if (lines.Count == 0 || lines[0].Trim() != LookFileHeader)
            {
                ShowToast("That isn't a FastViewer look file.", null, null, 5, null);
                return;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string name = System.IO.Path.GetFileNameWithoutExtension(file), baseId = Looks.DefaultId;
            int skipped = 0;
            foreach (var line in lines.Skip(1))
            {
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                string key = kv[0].Trim(), v = kv[1].Trim();
                if (key == "name") { if (v.Length > 0) name = v.Substring(0, Math.Min(40, v.Length)); continue; }
                if (key == "base") { if (Looks.All.Any(l => l.Id == v)) baseId = v; continue; }
                bool ok = IsCustomKey(key);
                if (ok)
                    try { ApplyCustomValue(new ResourceDictionary(), Looks.Find(baseId), key, v); }
                    catch { ok = false; }
                if (ok) values[key] = v;
                else skipped++;
            }
            // Undo: everything as it was before.
            var before = new Dictionary<string, string>(custom);
            string beforeBase = customBase, beforeName = customName, beforeLook = lookId;
            CrossfadeWindow(250);
            custom.Clear();
            foreach (var kv in values) custom[kv.Key] = kv.Value;
            customBase = baseId;
            customName = name;
            lookId = CustomId;
            ApplyLook();
            CustomChanged(true, false);
            RefreshCustomCard();
            foreach (var refresh in settingsRefresh) refresh();
            ShowToast("Using the look \"" + name + "\"" + (skipped > 0 ? " (" + skipped + " unknown lines left out)" : "") + ".", "Undo", delegate
            {
                CrossfadeWindow(250);
                custom.Clear();
                foreach (var kv in before) custom[kv.Key] = kv.Value;
                customBase = beforeBase;
                customName = beforeName;
                lookId = beforeLook;
                ApplyLook();
                CustomChanged(true, false);
                RefreshCustomCard();
                foreach (var refresh in settingsRefresh) refresh();
                HideToast(null);
            }, 8, "importLook");
        }

        // ---------------------------------------------------------------- Settings > Custom look

        TextBlock readabilityText;
        Border readabilityCard;

        StackPanel customPage;
        bool customPageBuilt;

        // Only the page and its place in the side menu at start-up; its cards are made the first time it opens.
        void AddCustomLookPage()
        {
            AddGroup("Custom look");
            customPage = settingsPage;
        }

        void FillCustomLookPage()
        {
            if (customPageBuilt) return;
            customPageBuilt = true;
            var before = settingsPage;
            settingsPage = customPage;
            try { FillCustomLookCards(); }
            finally { settingsPage = before; }
            foreach (var refresh in settingsRefresh) refresh();
            UpdateReadability();
        }

        void FillCustomLookCards()
        {
            var page = customPage;
            AddWideCard("", "Make your own look",
                "Start from any look and change what you like: colours, font, shapes, bars and motion. Every change shows right " +
                "away on the whole window, and you can always go back with \"Start over\". Export it to share it with friends; " +
                "they import it here or drop the file on FastViewer.", new Border { Height = 0 });

            readabilityText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            readabilityCard = AddCard("", "Some text may be hard to read", null, null);
            ((StackPanel)((Grid)readabilityCard.Child).Children[1]).Children.Add(readabilityText);
            readabilityCard.Visibility = Visibility.Collapsed;

            var name = new TextBox { Width = 240, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 40, Text = customName };
            name.SetResourceReference(StyleProperty, "SettingsTextBox");
            name.TextChanged += delegate
            {
                if (name.Text == customName) return;
                customName = name.Text.Trim().Length > 0 ? name.Text.Trim() : "My look";
                CustomChanged(true, false);
            };
            settingsRefresh.Add(delegate { if (name.Text != customName) name.Text = customName; });
            AddCard("", "Name", "Shown on the look's card and in the file you export.", name);

            var bases = Looks.All.ToList();
            AddCard("", "Start from", "The look your changes are made on. Changing it keeps your changes.",
                Dropdown(bases.Select(l => l.Name).ToArray(), () => Math.Max(0, bases.FindIndex(l => l.Id == customBase)), i =>
                {
                    customBase = bases[i].Id;
                    UseCustomLook();
                    CustomChanged(true, true);
                }));

            AddCard("", "Colours for", "Dark and light each have their own colours. This also switches the window to the one you edit.",
                Dropdown(new[] { "Dark", "Light" }, () => Looks.Light ? 1 : 0, i =>
                {
                    CrossfadeWindow(250);
                    lookMode = i == 1 ? "Light" : "Dark";
                    UseCustomLook();
                    ApplyLook();
                    foreach (var refresh in customRefresh) refresh();
                }));

            AddSubheading("Main colours");
            foreach (var group in ColorGroups)
            {
                var g = group;
                var parts = g.Skip(2).ToArray();
                AddCard(null, g[0], g[1], ColorButton(g[0], () => CustomColor(parts[0], Looks.Light), (c, save) => SetCustomColors(parts, c, Looks.Light, save),
                    () => BaseColor(parts[0], Looks.Light)));
            }

            AddSubheading("Text");
            var fonts = new[] { "Like the look" }.Concat(FontChoices.Where(f => f == "Inter" || Fonts.SystemFontFamilies.Any(s => s.Source == f))).ToArray();
            AddCard("", "Font", "Used for all text. Inter comes with FastViewer.",
                Dropdown(fonts, () => { string v; return custom.TryGetValue("font", out v) ? Math.Max(0, Array.IndexOf(fonts, v)) : 0; },
                    i => SetCustomValue("font", i == 0 ? null : fonts[i])));
            var weights = new[] { "Like the look" }.Concat(WeightChoices).ToArray();
            AddCard("", "Page titles", "How heavy the big titles are, like \"Custom look\" above.",
                Dropdown(weights, () => { string v; return custom.TryGetValue("titleWeight", out v) ? Math.Max(0, Array.IndexOf(weights, v)) : 0; },
                    i => SetCustomValue("titleWeight", i == 0 ? null : weights[i])));

            AddSubheading("Shape");
            var round = new Slider { Minimum = 0, Maximum = 24, Width = 240, SmallChange = 1, LargeChange = 4, IsSnapToTickEnabled = true, TickFrequency = 1 };
            round.SetResourceReference(StyleProperty, "FluentSlider");
            Func<double> roundNow = () =>
            {
                string v;
                double r;
                if (custom.TryGetValue("roundness", out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
                var f = Looks.Find(customBase).Get("Radius.Float");
                return f is CornerRadius ? ((CornerRadius)f).TopLeft : 8;
            };
            round.Value = roundNow();
            bool settingRound = false;
            round.ValueChanged += delegate
            {
                if (settingRound) return;
                SetCustomValue("roundness", round.Value.ToString(CultureInfo.InvariantCulture));
            };
            customRefresh.Add(delegate { settingRound = true; round.Value = roundNow(); settingRound = false; });
            settingsRefresh.Add(delegate { settingRound = true; round.Value = roundNow(); settingRound = false; });
            AddCard("", "Corner roundness", "From square corners to very round ones: buttons, cards, menus, the control bar and dialogs.", round);
            AddChoiceCard("", "Bars", "Docked: always there. Floating: over the picture, they fade away with the controls.",
                "bars", new[] { "Like the look", "Docked", "Floating" }, new[] { null, "docked", "floating" });
            AddChoiceCard("", "See-through window", "Your desktop shows blurred through the window. Works best with see-through colours " +
                "(lower \"See-through\" in a colour).", "seeThrough", new[] { "Like the look", "On", "Off" }, new[] { null, "on", "off" });
            AddChoiceCard("", "Icons", "Windows' own icons, or thin line icons like on a Mac.",
                "icons", new[] { "Like the look", "Windows", "Lines" }, new[] { null, "segoe", "lines" });

            AddSubheading("Motion");
            AddChoiceCard("", "Style", "Smooth: like Windows 11. Subtle: quick and barely moves. Springy: travels far, overshoots and wobbles. " +
                "Calm: slow fades that ease in and out. Snappy: there almost at once. " +
                "Settings > Appearance > Animations still decides how much moves.",
                "motionStyle", new[] { "Like the look", "Smooth", "Subtle", "Springy", "Calm", "Snappy" }, new string[] { null }.Concat(MotionStyles).ToArray());
            AddChoiceCard("", "Speed", "How long animations take, from Fastest (0.4x) to Super slow (3x).", "motionSpeed",
                new[] { "Fastest", "Extra fast", "Fast", "Normal", "Slow", "Extra slow", "Super slow" },
                new[] { "0.4", "0.55", "0.7", null, "1.4", "2", "3" });

            AddSubheading("All colours");
            var all = new StackPanel { Visibility = Visibility.Collapsed };
            var showAll = new Button { Content = "Show all colours", MinWidth = 160, Height = 32 };
            showAll.SetResourceReference(StyleProperty, "Filled");
            showAll.Click += delegate
            {
                if (all.Children.Count == 0) FillAllColours(all);
                all.Visibility = all.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                showAll.Content = all.Visibility == Visibility.Visible ? "Hide all colours" : "Show all colours";
                MakeKeyboardReachable(all);
            };
            AddCard("", "Every part on its own", "Over 50 parts, from menu edges to the switch knobs, for when you want everything just so.", showAll);
            page.Children.Add(all);

            AddSubheading("Share and start over");
            var export = new Button { Content = "Export...", MinWidth = 120, Height = 32 };
            export.SetResourceReference(StyleProperty, "Filled");
            export.Click += delegate { ExportLook(); };
            var import = new Button { Content = "Import...", MinWidth = 120, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            import.SetResourceReference(StyleProperty, "Filled");
            import.Click += delegate { ImportLookFromDialog(); };
            var share = new StackPanel { Orientation = Orientation.Horizontal };
            share.Children.Add(export);
            share.Children.Add(import);
            AddCard("", "Share your look", "Saves a small .fvlook file. Importing one (or dropping it on the window) uses it right away, with Undo.", share);
            var over = new Button { Content = "Start over", MinWidth = 120, Height = 32 };
            over.SetResourceReference(StyleProperty, "Filled");
            over.Click += delegate { CrossfadeWindow(250); StartCustomOver(); };
            AddCard("", "Start over", "Forget your changes: the custom look becomes a plain copy of the look it starts from.", over);
        }

        // A dropdown for one setting; "Like the look" (null) leaves it to the base look.
        void AddChoiceCard(string glyph, string title, string description, string key, string[] labels, string[] values)
        {
            AddCard(glyph, title, description, Dropdown(labels, () =>
            {
                string v;
                int i = custom.TryGetValue(key, out v) ? Array.IndexOf(values, v) : -1;
                return Math.Max(0, i >= 0 ? i : Array.IndexOf(values, null)); // not set or unknown: the "null" choice (Normal sits mid-list)
            }, i => SetCustomValue(key, values[i])));
        }

        void FillAllColours(StackPanel all)
        {
            var b = Looks.Find(customBase);
            var parts = ColorNames.Keys.Where(p => b.Get("Dark." + p) is Brush || p == "Accent" || p == "AccentText").ToList();
            foreach (var p in parts)
            {
                var part = p;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = ColorNames[part], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var button = ColorButton(ColorNames[part], () => CustomColor(part, Looks.Light), (c, save) =>
                {
                    if (part == "AccentText") custom[(Looks.Light ? "Light." : "Dark.") + "AccentText+"] = "1"; // chosen by hand: keep it
                    SetCustomColors(new[] { part }, c, Looks.Light, save);
                }, () => BaseColor(part, Looks.Light));
                Grid.SetColumn(button, 1);
                row.Children.Add(button);
                all.Children.Add(Card(row, new Thickness(16, 6, 16, 6)));
            }
        }

        // Shows which text is hard to read with the current colours (contrast under 3:1).
        void UpdateReadability()
        {
            if (readabilityCard == null) return;
            if (lookId != CustomId) { readabilityCard.Visibility = Visibility.Collapsed; return; }
            bool light = Looks.Light;
            Func<string, Color> col = p => CustomColor(p, light);
            var under = Over(col("WindowBg"), light ? Colors.White : Colors.Black);
            Func<string, Color> bg = p => Over(col(p), under);
            var checks = new[]
            {
                new[] { "Text", "TextMain", "PageBg", "Settings pages" },
                new[] { "Text", "TextMain", "CardBg", "Settings cards" },
                new[] { "Text", "TextMain", "MenuBg", "menus" },
                new[] { "Button text", "ControlText", "TopBarBg", "the top bar" },
                new[] { "Secondary text", "TextSecondary", "CardBg", "Settings cards" },
                new[] { "Text on the accent colour", "AccentText", "Accent", "buttons" },
            };
            var bad = new List<string>();
            foreach (var c in checks)
            {
                double ratio = Contrast(Over(col(c[1]), bg(c[2])), bg(c[2]));
                if (ratio < 3) bad.Add(c[0] + " on " + c[3] + " (" + ratio.ToString("0.0", CultureInfo.InvariantCulture) + ":1)");
            }
            readabilityCard.Visibility = bad.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // The warning itself stays readable, whatever the text colour is.
            var cardBg = bg("CardBg");
            var readable = new SolidColorBrush(ReadableOn(cardBg));
            TextElement.SetForeground(readabilityCard, readable);
            readabilityText.Foreground = readable;
            readabilityText.Text = string.Join(", ", bad) + ". Easy reading needs about 4.5:1: make the text lighter or the background darker (or the other way round).";
        }

        // The Custom card on Settings > Appearance follows your changes.
        void RefreshCustomCard()
        {
            if (lookCards == null || Looks.Custom == null) return;
            foreach (var card in lookCards.Children.OfType<Button>().Where(c => (string)c.Tag == CustomId))
            {
                var stack = (StackPanel)card.Content;
                ((Border)((Border)stack.Children[0]).Child).Child = Mock(Looks.Custom, Looks.IsLight(Looks.Custom, lookMode));
                ((TextBlock)stack.Children[1]).Text = customName;
                ((TextBlock)stack.Children[2]).Text = Looks.Custom.Description;
                card.ToolTip = customName;
            }
        }

        // ---------------------------------------------------------------- the colour picker

        // A colour button. Clicking opens a small picker: a shade square, hue, see-through, the colour code, a few
        // ready colours, and "Original" (the base look's colour). Changes show while you drag; set(colour, done).
        Button ColorButton(string label, Func<Color> get, Action<Color, bool> set, Func<Color> original)
        {
            var swatch = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Background = Checkers() };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ControlEdge");
            var fill = new Border { CornerRadius = new CornerRadius(3) };
            swatch.Child = fill;
            var code = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(code);
            var b = new Button { Content = row, Height = 32, MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 0, 10, 0) };
            b.SetResourceReference(StyleProperty, "Filled");
            System.Windows.Automation.AutomationProperties.SetName(b, label + " colour");
            Action show = () =>
            {
                var c = get();
                fill.Background = new SolidColorBrush(c);
                code.Text = ColorText(c);
                b.ToolTip = label + ": " + ColorText(c);
            };
            show();
            customRefresh.Add(show);
            settingsRefresh.Add(show);
            b.Click += delegate
            {
                if (ClosingOwnMenu(b)) return;
                if (huntPickerInWindow) { OpenColorPicker(b, get, (c, done) => { set(c, done); show(); }, original); return; }
                if (snapDir != null || selfTestInterval > 0) { testLog.Add("colour picker for " + label + " (not opened in tests)"); return; }
                OpenColorPicker(b, get, (c, done) => { set(c, done); show(); }, original);
            };
            return b;
        }

        static Brush checkers;

        static Brush Checkers()
        {
            if (checkers != null) return checkers;
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
                new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
            var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
            brush.Freeze();
            return checkers = brush;
        }

        static readonly string[] Ready =
        {
            "#0078D4", "#0071E3", "#00B7C3", "#10893E", "#7FBA00", "#FFB900", "#F7630C", "#E81123",
            "#E3008C", "#8764B8", "#FFFFFF", "#C8C8C8", "#767676", "#2B2B2B", "#1C1C1C", "#000000"
        };

        void OpenColorPicker(Button owner, Func<Color> get, Action<Color, bool> set, Func<Color> original)
        {
            const double W = 240, H = 150;
            var start = get();
            double h, s, v, a = start.A / 255.0;
            ToHsv(start, out h, out s, out v);
            bool changed = false;

            var square = new Grid { Width = W, Height = H, ClipToBounds = true, Cursor = Cursors.Cross };
            var hueFill = new Rectangle();
            square.Children.Add(hueFill);
            square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
            square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
            var dotCanvas = new Canvas { IsHitTestVisible = false };
            var dot = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2 };
            var dotRing = new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), StrokeThickness = 1 };
            dotCanvas.Children.Add(dotRing);
            dotCanvas.Children.Add(dot);
            square.Children.Add(dotCanvas);

            Func<Brush, Grid> bar = brush =>
            {
                var g = new Grid { Height = 16, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand };
                g.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Checkers() });
                g.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = brush, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1) });
                var c = new Canvas { IsHitTestVisible = false };
                c.Children.Add(new Border { Width = 8, Height = 20, CornerRadius = new CornerRadius(4), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), BorderThickness = new Thickness(1), Margin = new Thickness(0, -2, 0, 0) });
                g.Children.Add(c);
                return g;
            };
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            foreach (var stop in new[] { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000" }.Select((x, i) => new GradientStop((Color)ColorConverter.ConvertFromString(x), i / 6.0)))
                rainbow.GradientStops.Add(stop);
            var hueBar = bar(rainbow);
            var alphaFill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            var alphaBar = bar(alphaFill);
            var hex = new TextBox { Width = 120, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
            hex.SetResourceReference(StyleProperty, "SettingsTextBox");
            System.Windows.Automation.AutomationProperties.SetName(hex, "Colour code");
            var orig = new Button { Content = "Original", Height = 30, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), ToolTip = "The colour of the look it starts from" };
            orig.SetResourceReference(StyleProperty, "Filled");
            var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            codeRow.Children.Add(hex);
            codeRow.Children.Add(orig);
            var ready = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Width = W };
            var seeLabel = new TextBlock { Text = "See-through", FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
            seeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            Action<bool> update = null;
            update = done =>
            {
                var c = FromHsv(h, s, v, a);
                hueFill.Fill = new SolidColorBrush(FromHsv(h, 1, 1, 1));
                Canvas.SetLeft(dotRing, s * W - 8); Canvas.SetTop(dotRing, (1 - v) * H - 8);
                Canvas.SetLeft(dot, s * W - 7); Canvas.SetTop(dot, (1 - v) * H - 7);
                Canvas.SetLeft(((Canvas)hueBar.Children[2]).Children[0], h / 360 * W - 4);
                alphaFill.GradientStops.Clear();
                alphaFill.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
                alphaFill.GradientStops.Add(new GradientStop(Color.FromArgb(255, c.R, c.G, c.B), 1));
                Canvas.SetLeft(((Canvas)alphaBar.Children[2]).Children[0], a * W - 4);
                if (!hex.IsKeyboardFocused) hex.Text = ColorText(c);
                changed = true;
                set(c, done);
            };
            Action<FrameworkElement, Action<Point>> drag = (el, at) =>
            {
                el.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { el.CaptureMouse(); at(e.GetPosition(el)); update(false); e.Handled = true; };
                el.MouseMove += delegate(object o, MouseEventArgs e) { if (el.IsMouseCaptured) { at(e.GetPosition(el)); update(false); } };
                el.MouseLeftButtonUp += delegate { if (el.IsMouseCaptured) { el.ReleaseMouseCapture(); update(true); } };
            };
            drag(square, p => { s = Math.Max(0, Math.Min(1, p.X / W)); v = Math.Max(0, Math.Min(1, 1 - p.Y / H)); });
            drag(hueBar, p => { h = Math.Max(0, Math.Min(359.9, p.X / W * 360)); });
            drag(alphaBar, p => { a = Math.Max(0, Math.Min(1, p.X / W)); });
            Action<Color> pick = c => { ToHsv(c, out h, out s, out v); a = c.A / 255.0; update(true); };
            foreach (var r in Ready)
            {
                var c = (Color)ColorConverter.ConvertFromString(r);
                var chip = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6), Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = r };
                chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));
                chip.BorderThickness = new Thickness(1);
                chip.MouseLeftButtonUp += delegate { pick(Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B)); };
                ready.Children.Add(chip);
            }
            orig.Click += delegate { pick(original()); };
            Action readHex = () =>
            {
                try { pick(ParseColor(hex.Text)); }
                catch { hex.Text = ColorText(FromHsv(h, s, v, a)); }
            };
            hex.KeyDown += delegate(object o, KeyEventArgs e) { if (e.Key == Key.Enter) { readHex(); e.Handled = true; } };
            hex.LostKeyboardFocus += delegate { readHex(); };

            var panel = new StackPanel { Width = W };
            panel.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = square });
            panel.Children.Add(hueBar);
            panel.Children.Add(seeLabel);
            alphaBar.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(alphaBar);
            panel.Children.Add(codeRow);
            panel.Children.Add(ready);
            var box = new Border { Padding = new Thickness(14), BorderThickness = new Thickness(1), Child = panel };
            box.SetResourceReference(Border.BackgroundProperty, "MenuBg");
            box.SetResourceReference(Border.BorderBrushProperty, "MenuEdge");
            box.SetResourceReference(Border.CornerRadiusProperty, "Radius.Float");
            box.SetResourceReference(TextElement.ForegroundProperty, "TextMain");
            var popup = new Popup { PlacementTarget = owner, Placement = PlacementMode.Bottom, VerticalOffset = 4, StaysOpen = false, AllowsTransparency = true, Child = box };
            // Closing fades like the menus do (Animations.cs): a click outside or Esc pictures it just before it goes.
            popup.PreviewMouseDown += delegate(object o, MouseButtonEventArgs e)
            {
                var p = e.GetPosition(box);
                if (p.X < 0 || p.Y < 0 || p.X > box.ActualWidth || p.Y > box.ActualHeight) PutGhost(popup, box, () => popup.IsOpen);
            };
            popup.Closed += delegate
            {
                FadeGhost(popup);
                if (changed) set(FromHsv(h, s, v, a), true);
                if (Mouse.LeftButton == MouseButtonState.Pressed && owner.IsMouseOver) MenuClosedBy(owner);
            };
            popup.KeyDown += delegate(object o, KeyEventArgs e) { if (e.Key == Key.Escape) { PutGhost(popup, box, () => popup.IsOpen); popup.IsOpen = false; e.Handled = true; } };
            update(false);
            changed = false;
            if (huntPickerInWindow)
            {
                // tests: drawn inside the (hidden) window under its button instead of as a pop-up on the screen
                var at = owner.TranslatePoint(new Point(0, owner.ActualHeight + 4), root);
                box.HorizontalAlignment = HorizontalAlignment.Left;
                box.VerticalAlignment = VerticalAlignment.Top;
                box.Margin = new Thickness(Math.Max(0, Math.Min(at.X - 60, root.ActualWidth - W - 60)), 64, 0, 0); // just under the top bar
                popup.Child = null;
                Grid.SetRowSpan(box, 3);
                Panel.SetZIndex(box, 3000);
                root.Children.Add(box);
                huntPicker = box;
                return;
            }
            popup.IsOpen = true;
            Motion.FromTo(box, OpacityProperty, 0, 1, Motion.Ms(Motion.Fast, false), Motion.Smooth);
        }

        bool huntPickerInWindow;   // tests: the picker is drawn in the window
        FrameworkElement huntPicker;

        static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        static Color FromHsv(double h, double s, double v, double a)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
