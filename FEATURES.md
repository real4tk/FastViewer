<!-- Read this first in every new conversation about FastViewer. Updated with every release (see the release routine). -->
# FastViewer: complete feature list (version 1.19.0)

FastViewer is a fast photo and video viewer for Windows 10/11 that looks like the Windows 11 Photos app.
Its main point is that switching between files is instant (median about 1 ms, p90 about 11 ms per switch
in the built-in stress test). Public on GitHub: github.com/real4tk/FastViewer (MIT license).

## How it's built
- C# / WPF on .NET Framework 4.8. Only C# 5 language features (built with the csc.exe that ships with
  Windows; no SDK). Build with build.cmd; the XAML, icons and the Inter font are embedded resources.
- Source files: FastViewer.cs (Program, ExplorerOrder, Slot, core window), Settings.cs, Mini.cs, VideoInfo.cs,
  AssemblyInfo.cs, Playback.cs, Convert.cs, Extras.cs, FeatureTest.cs, Updates.cs, Shortcuts.cs,
  FolderWatch.cs, Background.cs, Thumbs.cs, Gallery.cs, Looks.cs, Appearance.cs, BugHunt.cs, Motion.cs
  (the shared animation system), Animations.cs (the window's animations and the Animations setting),
  Trim.cs (the trim bar), CustomLook.cs (the custom look and its colour picker);
  Theme.xaml, Window.xaml, Icons.Lines.xaml, Looks.Photos/Clean/Glass/Midnight.xaml; fonts\Inter-*.ttf.
- One portable FastViewer.exe (about 1.2 MB). Release zip FastViewer-<version>.zip contains FastViewer.exe,
  "How to use FastViewer.txt", "What's new.txt" and the Inter font license.
- Settings in %APPDATA%\FastViewer\settings.ini, resume places in positions.txt, crash reasons in
  %TEMP%\FastViewer\crash.txt.

## File types
- Photos: .jpg .jpeg .jfif .jpe .png .bmp .gif .tif .tiff .ico .webp .heic .heif .jxr .wdp .dng
  (whatever Windows' imaging codecs can read).
- Videos: .mp4 .m4v .mov .wmv .avi .mkv .3gp .3g2 .mts .m2ts .ts .mpg .mpeg .asf .webm
  (played with Windows' own video engine; HEVC needs Microsoft's "HEVC Video Extensions").
- Animated GIFs play (frames composed correctly; only the GIF on screen keeps its frames in memory).

## Instant switching (the core)
- The current file plus 1, 2 or 3 files each way (Settings > Speed > Preload, default 2) are loaded ahead
  into hidden player "slots", so next/previous just swaps which slot is visible.
- The old picture stays on screen until the new one is really drawing: no black flashes between files,
  also when clicking 10+ times a second.
- Videos: a first-frame snapshot ("poster") is prepared for each preloaded video so it appears instantly;
  playback starts behind it and takes over once frames are drawn. Resuming a paused video shows no black flash.
- Slow-PC protection: if grabbing a video frame takes over 400 ms, or three grabs take over 150 ms within
  30 s, frame grabbing is switched off for the session so the window never freezes (a single hiccup of a busy
  PC doesn't count). The time bar's dot doesn't redraw the window while you click through files.
- Photos are decoded at screen size (a 12 MP photo takes about 10 MB); zooming in past that loads the
  full photo by itself. Unused pictures are handed back to Windows once you pause.
- A skipped video that was starting never keeps playing out of sight; failed players stop and are retried
  when you go back to them.
- Preload changes take effect immediately (players are added or closed live).

## Opening files
- Double-click in File Explorer (FastViewer can register itself under "Open with": Settings > Default app),
  drag and drop a file or folder onto the window, the Open button (Ctrl+O), or a folder path.
- Goes through only that folder (not subfolders), skipping hidden files.
- Order: "Same as File Explorer" (asks the open Explorer window how that folder is sorted and grouped,
  e.g. Downloads newest first), Name, Date modified newest first, Date modified oldest first.
- Show filter: photos and videos, only videos, or only photos.
- "Go back to the start after the last file" (wrap around), on by default.
- Folder watching: new, renamed and deleted files in the open folder appear/disappear by themselves.
- Each file opened from Explorer while a FastViewer window is open gets its own window.
- File Explorer thumbnails of photos and videos opened with FastViewer show no FastViewer badge (it sets
  TypeOverlay="" on its own "Open with" entry, also repairing older installs by itself).

## Viewer screen (Windows 11 Photos layout)
- Top bar (also the title bar): Open, Delete, Copy, Rotate, "..." menu on the left; file name in the middle
  (full name as a tooltip when shortened; drag the title to move the window, double-click to maximize);
  Mini player, "Open with", minimize, maximize, close on the right.
- Bottom bar: Slideshow, All files (grid) and file details (type, dimensions, size) on the left; File info
  in the middle; Fit to window, zoom percentage menu, zoom slider with -/+, Full screen on the right.
- Side arrows for previous/next; the controls fade away after you stop moving the mouse (1.5 / 2.5 / 5 s or
  never) and as soon as the pointer leaves the window; the mouse pointer hides over a video.
- File info pane (I): name, folder, dates, size, dimensions, video length.
- Narrow windows: labels drop, extras tuck into the "..." menu, the Info button steps aside, the video bar
  keeps its time bar (skip/repeat buttons make room), the middle play button shrinks.
- Empty window: "Drop a photo, video or folder here or press Ctrl+O"; no leftover details.
- Files that can't be opened: a plain explanation (empty file, damaged, incomplete download, path too long,
  in use by another app, HEVC codec missing...) plus an "Open with another app" button.

## "..." menu (top bar)
- File: Save as, Copy, Copy file path, Show in folder, Delete.
- View: Rotate, Save rotation to file, Mini player, Keep on top, Slideshow, Shuffle, All files in folder,
  File info; "Hidden buttons" section for buttons switched off in Settings > Buttons; Settings.
- Every item shows its current keyboard shortcut (punctuation keys as the character on your keyboard layout,
  e.g. ` instead of Oem3). Clicking a menu's button again (or anywhere else), Esc or an item closes the menu with a fade that follows the look's style and Speed (1.19.0: a picture of the menu goes on top just before it closes, then fades and shrinks a little; also the colour picker) (all menus,
  also the zoom %, grid size and Settings drop-downs).

## Photos
- Zoom: mouse wheel (if set to zoom) or Ctrl+wheel, +/- keys and buttons, zoom slider, zoom menu (Fit,
  Actual size 100%, 200%, 400%), up to 20 times the fit-to-window size; drag to pan when zoomed.
- "Zoom preference": fit to window, or view small photos at actual size.
- EXIF orientation is applied (phone photos show upright).
- Rotate 90 degrees (Ctrl+R) with a short animation; optional "Also save it to the file" (JPEG: lossless
  orientation tag when possible; PNG/BMP/TIFF re-saved), also "..." > Save rotation to file. Keeps the
  file's date; rotations are saved even if you switch away right after rotating.
- Delete to the Recycle Bin (Delete key) with an optional confirmation ("Ask for permission to delete");
  the dialog always deletes the file it names and nothing moves on while it's open.
- Copy (Ctrl+C copies the file), Copy file path, Show in folder, Open with another app, Save as (Ctrl+S).

## Videos
- Floating control bar: play/pause, skip back/forward (5/10/15/30/60 s, shown inside the icons: since 1.19.0 an open circle with an arrow pointing back or forward, drawn the same in every look),
  volume (point at the speaker for a slider, click to mute, wheel changes volume; on a touch screen a tap
  opens the slider), time, seek bar, duration, Repeat, video "..." menu. The seek bar's dot moves every screen
  frame while playing (with animations on); dragging it holds the video still and shows the frames passed (at
  most ~20 jumps a second, ending exactly where you let go), then plays on if it was playing.
- Play/pause button in the middle (Show / Only when paused / Hide); click the video to play/pause
  (configurable); double-click for full screen.
- Video dims slightly while controls show (optional); controls can also fade on a paused video (optional).
- Short notes when a key or the wheel changes something while the controls are hidden ("Volume 45%",
  "+30 s 1:42 / 5:00").
- Autoplay (optional), Repeat (R), "When a video ends": stop or play the next file.
- Speed 0.5x / 1x / 1.5x / 2x (video "..." menu, optional keys).
- Frame by frame: , and . step one frame (uses the real frame length read from the file).
- A-B loop: [ start, ] end, \ stop; the looped part shows as a band on the time bar.
- Continue where you left off: videos over a minute resume where you stopped (300 most recent, shared
  safely between windows), with a "Start over" button.
- Trim (Ctrl+T, or "..." > Trim): a trim bar takes the control bar's place, with a filmstrip of the video
  (small pictures made in the background, turned like the video) and a handle at each end of the part to
  keep; the rest is dimmed. Dragging a handle shows that frame in the viewer; clicking the strip shows that
  moment. The start and end times (tenths of a second) sit under the handles and the clip length at the
  top. "Play selection" plays just that part over and over. Keys: left/right move the chosen handle by a
  frame (Shift: a second), Tab picks the other handle, [ and ] put a handle at the current moment, Space
  plays, Enter saves, Esc leaves; next/previous file is off while trimming. Handles can't cross; the
  shortest clip is 0.2 s. "Save clip" saves the part as a new .mp4. A narrow window keeps every button
  (icons only). Trimming the same video again shows its filmstrip at once.
- Make compatible copy: re-encodes to a standard H.264/AAC .mp4 that plays everywhere, upright.
  (Trim and compatible copy use Windows' Media Foundation; progress and Cancel in a message;
  closing the window while saving finishes the save first.)
- Save a frame as PNG (as shown, including rotation).
- Phone rotation metadata in MP4/MOV is read and applied (videos filmed upright show upright).
- Rotate videos for viewing (Ctrl+R).

## Navigation and keyboard (all keys can be changed, two per action)
- Default keys: Next Right/PageDown, Previous Left/PageUp, First Home, Last End, Shuffle X, Grid G,
  Play/pause Space/K, Skip back J, Skip forward L, Back 5 s Shift+Left, Forward 5 s Shift+Right,
  Previous frame ",", Next frame ".", Loop A "[", Loop B "]", Stop loop "\", Repeat R, Volume up Up,
  Volume down Down, Mute M, Trim Ctrl+T, Rotate Ctrl+R, Zoom in +/Num+, Zoom out -/Num-, Fit 0/Num0,
  Full screen F/F11, Mini player Ctrl+M, Slideshow F5, File info I, Open Ctrl+O, Save as Ctrl+S,
  Copy Ctrl+C, Delete Delete, Settings Ctrl+,
- Actions without a default key (assignable): thumbnail strip, slower/faster/normal speed, save a frame,
  make compatible copy, save rotation to file, actual size, keep on top, copy file path, show in folder,
  open with, close window.
- Esc always goes back: leaves full screen, mini player, slideshow, grid or Settings. A key pressed again
  undoes itself: G closes the grid, I File info, Ctrl+T leaves trimming, Ctrl+, closes Settings, Ctrl+M/F/F5
  leave the mini player, full screen, slideshow. Entering the mini player ends trimming.
- Mouse wheel: next/previous (default) or zoom; Ctrl+wheel always zooms. Over the speaker: volume;
  over the time bar: jump 5 s.
- Mouse buttons (Settings > Mouse): back/forward side buttons, middle button, double-click and click on a
  video can each be set to any of ~18 actions (defaults: previous, next, nothing, full screen, play/pause).
- Shuffle (X): random order through the folder; new files join at random places after the current one.

## Grid view and thumbnail strip
- All files in the folder (G or the grid button): thumbnails from Windows' own thumbnail cache, in folder
  order with your Show filter; Small / Medium / Large (or Ctrl+wheel); Names button shows file names;
  arrow keys + Enter, click to open, Esc or mouse back button to leave. Only visible rows are built, so
  thousands of files are fine. With shuffle on the grid stays in folder order.
- Thumbnail strip (Settings > Viewing, off by default): small pictures of the files before and after the
  current one under the picture; click to jump; shows the shuffled order when shuffling. Thumbnails load
  only after you stop switching, so switching stays just as fast.

## Full screen, mini player, slideshow, keep on top
- Full screen (F/F11, double-click): bars float and fade in when you move the mouse; only Close shows in
  the corner.
- Mini player (Ctrl+M or the top-bar button): just the video in a small always-on-top window shaped like
  the video (reshapes on the next file or rotation); drag to move, drag edges to resize (shape kept),
  remembers size and place; double-click or Esc to go back. Optional volume button in its top bar
  (Settings > Appearance > "Volume button in the mini player", off by default): click mutes, the wheel
  changes the volume, and the level shows as a number next to it (no slider or message over the small
  window). The wheel over the mini player's buttons never changes the file.
  Photos with see-through parts (1.19.0: alpha channel or a transparent palette colour, e.g. PNG, GIF) make the
  mini player itself see-through: no background behind the picture, and the preloaded hidden files are fully
  hidden (opacity 0; since 1.19.0 also in the Liquid Glass window, where 1% showed as faint ghost shapes beside the picture; switching speed measured unchanged).
  Clicks on clear parts still go to the mini player. Other photos and videos keep the black background.
- No Windows frame in full screen and the mini player (1.19.0): a plain square window region (Windows 10 then
  draws no frame, border or shadow), glass frame 0, and DWMWA_BORDER_COLOR = none on Windows 11. Before, the
  1-pixel Windows border showed as accent-coloured (e.g. blue) lines around the mini player and on the screen
  edges in full screen when "Show accent colour on title bars and window borders" was on.
- Slideshow (F5): full screen, photos every 2/4/8 s, videos play to the end; the button turns into
  "Stop slideshow"; a short message says how to stop it.
- Keep on top (pin) for the normal window.

## Looks and buttons (Settings > Appearance / Buttons)
- Four looks, each Dark / Light / Like Windows, switching instantly, with previews:
  Photos (Windows 11 Photos style, default), Clean (just the picture, floating bars that fade),
  Liquid Glass (frosted blur-behind window, spring motion, line icons, Inter font),
  Midnight (pure black or pure white, no boxes, thin text, Inter font).
- Windows accent colour is used for highlights (a grey accent falls back to blue).
- Tips when pointing at a button (Settings > Buttons, on by default): off stops every tooltip; screen-reader
  names and the keys in menus stay.
- Choose your buttons: switch any top/bottom/video/side button on or off; presets All buttons,
  Essentials, Minimal; hidden buttons stay available on their keys and under "Hidden buttons" in the menus.
- Reset to default for both.

## Animations (1.16.0)
- One shared animation system (Motion.cs) for everything that moves. Each look has its own motion:
  Photos like Windows 11 (167/250/333 ms, decelerating, no overshoot), Clean subtle (smaller movements),
  Liquid Glass springy (a little overshoot that settles; buttons squish when pressed and wobble back, iOS-like,
  since 1.19.0: Motion.Squish in the look, inherited by a Custom look based on it; every ButtonBase (toggle/radio buttons, switches, side arrows, Settings) except slider and scroll bar parts, also when a parent handles the press; a button's own transform is kept inside Motion.PartsOf; a quick tap still plays the
  whole squish), Midnight calm (mostly fades).
- Settings > Appearance > Animations: Like Windows (default: Full, or Reduced when Windows' "Show animations"
  is off), Full, Reduced (only short fades), Off.
- Every animation starts from where things are right now (can be interrupted without jumping), runs at the
  screen's refresh rate (60 up to 500 Hz and more; with several screens the fastest one's rate), uses only opacity
  and transforms, and nothing runs while still. Each frame moves by the real time since the last one, so an
  animation takes the same time and follows the same curve at any rate (pan momentum uses the exact formula, so it
  glides equally far). The trim bar's playhead also moves every frame. Settings > About > Screen shows the refresh
  rate and the frames a second the last animation really got.
- What moves: hover/press on every button, sliding switches, menus open from their button and fade out,
  Settings opens/closes/changes page with a sliding side-menu marker, messages drop in and leave, the
  Delete dialog settles in, error messages rise in, controls/bars/arrows fade and slide in, play/pause
  morphs and the middle button dips, the volume slider grows from the speaker, zoom glides (wheel notches
  merge), panning keeps momentum, rotation turns and re-fits in one move, the grid opens by shrinking the
  photo into its tile and a picked file grows out of its tile, grid tiles glide on size changes, the
  selection ring glides, thumbnails fade in, the strip glides after each switch, smooth wheel scrolling
  (grid, Settings), the Info pane slides in, looks and dark/light crossfade, full screen and the mini
  player fade through, the time bar glides, the A-B band grows, saving shows a filling line, a drop outline
  shows when dragging a file over the window, and a deleted photo fades away.
- Switching between files stays instant. Optional "Transition between files: Crossfade" (off by default)
  fades the old file out over the new one, which shows right away (keeps up with fast clicking).

## Custom look (1.17.1, Settings > Custom look)
- A fifth look built from any of the four (Start from) plus only what you change; "Like the look" leaves a
  setting to the base look. Stored in settings.ini (customBase, customName, custom.<part>=value) and synced
  between windows.
- Groups: Name; Start from; Colours for Dark / Light (also switches the window to it); Main colours (Accent,
  Background, Bars, Pages, Cards and menus, Text, Secondary text: each sets a few related parts); Text (font:
  Segoe UI, Segoe UI Variable, Inter, Bahnschrift, Calibri, Verdana, Georgia, Trebuchet MS, Consolas, Comic
  Sans MS when installed; page title weight); Shape (corner roundness 0-24 for buttons, cards, menus, the
  control bar, dialogs and the window; docked or floating bars; see-through window; Windows or line icons);
  Motion: styles Smooth (Windows 11 decelerate, 167/250/333 ms), Subtle (quick, even curve, 0.3x travel), Springy
  (bouncy spring with ~23% overshoot, 1.8x travel, press 0.92), Calm (slow sine in-out, 300/450/600 ms, 0.2x travel),
  Snappy (100/150/200 ms, expo-out); speed Fastest 0.4x, Extra fast 0.55x, Fast 0.7x, Normal, Slow 1.4x, Extra slow
  2x, Super slow 3x (1.19.0: a factor in Motion.Ms applied to every animation incl. fixed ones; zoom, pan and wheel scrolling use Motion.Follows and keep their pace); All colours (every part on its own, 58);
  Share and start over.
- Live: colours go straight into the window's resources while a picker is dragged; shapes, fonts and motion
  re-apply 60 ms later; saved half a second after the last change.
- Colour picker: shade square, hue bar, see-through bar, colour code (#RRGGBB or #AARRGGBB), 16 ready colours,
  "Original". Text on the accent picks black or white by itself unless set by hand.
- Readability warning when text on pages, cards, menus, the top bar or the accent drops under 3:1 contrast.
- Export / Import of .fvlook files ("FastViewer look" header, name, base, parts); unknown or invalid lines are
  left out (and counted); dropping a .fvlook on the window imports it; Undo in the message.
- The page's cards are only built the first time it opens, so starting stays quick.

## Settings (side menu pages)
- Appearance (look, dark or light, Animations, Transition between files, mini player volume button), Custom look, Buttons, Viewing (zoom preference, show filter, order, wrap around, thumbnail strip,
  keep on top, ask before deleting, when you rotate a photo), Videos (continue where you left off,
  autoplay, repeat, when a video ends, dim video, hide controls on paused video, hide controls after,
  skip back/forward, slideshow speed), Keyboard (every action, two keys each, Reset all), Mouse,
  Speed (stay ready in the background, Preload, "Start even faster" = NGen with admin approval),
  Default app (add to "Open with", open Windows Default apps), Updates (automatic check on/off, Check now),
  About (version and where the settings file is).
- Usable with the keyboard alone (Tab, Space/Enter, arrow keys in menus, Esc), with a focus ring; every
  control has a screen-reader name. In a shortcut box: press a key to set it, Delete removes it, Tab moves on.
- Narrow windows: the side menu shows icons only and scrolls.
- Several FastViewer windows share settings: a change in one reaches the others when you switch to them.
- A damaged or hand-edited settings.ini never stops FastViewer from starting (bad lines skipped, values
  kept in range).

## Ready in the background (Settings > Speed, on by default)
- Closing the last window hides FastViewer instead of quitting: it lets go of all files, frees memory,
  shows an icon by the clock (double-click to open, right-click > Quit) and waits on a private pipe.
- The next double-clicked file is handed over and appears instantly. Only one FastViewer waits in the
  background; other windows quit when closed. A one-time notice explains this the first time.

## Updates
- Checks github.com/real4tk/FastViewer at start-up (at most about once a day, can be switched off) and
  offers "Update now"; nothing installs without your click; settings stay.
- If GitHub's API refuses (60 requests/hour limit), it reads the version from the normal releases page;
  if that fails too: "GitHub is busy right now. FastViewer will try again later." and a quiet retry.
- Downloads only from https://github.com/real4tk/FastViewer/releases/download/; the new exe replaces the
  old one (the old file is moved aside even if another window still runs it) and FastViewer restarts on
  the same file; the background copy makes way for the new version.

## Reliability
- Crash reasons are written to %TEMP%\FastViewer\crash.txt.
- Handles: empty, corrupt, truncated and misnamed files, unicode/emoji names, '#', '%', '&' in names,
  huge images (20000 px), read-only and locked files, 5000+ file folders (opens in well under 0.5 s),
  files added/renamed/deleted while open.

## Built-in test modes (for development; tests run muted, never steal focus, and their windows are cloaked: drawn but never seen, never on top)
- --selftest <ms> (switching speed log), --features, --ui, --gallery, --controls, --tweaks, --memory,
  --stall (freeze detector), --volume, --looks, --dim, --sync, --toast, --fadetest, --snap <dir>.
- --hunt <dir> --case <name>: torture (thousands of fast switches with checks), edge, churn, big, dump,
  keys, dialog, rotate, loop, sizes, ghost, positions, saveclose, trim (drags, keys, play selection, saving), rates (every animation simulated at 60/144/240/500 Hz), toggles (menu buttons close their menu, keys undo themselves), scrub (fast time bar drags, the dot's smoothness), custom (the custom look end to end, with --settings), showcase (pictures of an example custom look), wheel (very fast switching through videos with the controls showing: delay per press, frame times, frame grabs), minivol (mini player volume number), tips (the tips switch, with --settings), miniclear (folder with a see-through PNG, a photo and a video: the mini player measured over white and black is fully clear around the PNG, normal for the rest, and no Windows frame in mini/full screen), preloadghost (every look: the window drawn with and without the hidden preloaded players must be pixel-identical, on a see-through PNG, photos and paused videos, in the window and the mini player), squishall (presses all ~220 buttons in the window and every Settings page: each must shrink and end exactly where it started; --look Glass --motion full), skipicons (saves the skip buttons at 4x per look), squish (Liquid Glass button squish frame by frame for taps of 0-278 ms, plus each Custom motion style's timing and curve; use --look Glass --motion full),
  overlap, audit (--pattern
  full|sizes|scales|contrast: renders every screen in all four looks and checks overlaps, clipped text, contrast,
  screen-reader names, Tab order), ux, updatemsg, motion (every animation's frame times and idle CPU).
- Options: --preload n, --look <Id>, --strip, --grab-always, --settings <scratch.ini>,
  --positions <scratch file>, --update-feed <local json or "refused">, --update-page <file>, --update-test,
  --count, --every, --pattern, --front, --motion full|reduced|off (tests run with animations Off unless
  given), --convert <src> <dst> <rotation> [from to].
- Any run with a "--" option never contacts GitHub.
