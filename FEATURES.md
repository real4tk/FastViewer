<!-- Read this first in every new conversation about FastViewer. Updated with every release (see the release routine). -->
# FastViewer: complete feature list (version 1.24.0)

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
  Trim.cs (the trim bar), CustomLook.cs (the custom look and its colour picker), Audio.cs (music files: the cover card, tags from Windows), Rotations.cs (remembered turns, rotations.txt; video turn saved into the MP4/MOV), GridSearch.cs (grid search and sort), Menus.cs (the customizable "..." menus), Gif.cs (own GIF writer), Share.cs (Make GIF, Send to Discord), Crop.cs (cropping photos and videos), HuntCrop.cs (their tests), Away.cs (1.23.0: hidden video players let go while the window is away, and its test), Addons.cs (1.24.0: optional one-time downloads, the Helper that runs programs out of sight), Tools.cs (Convert to JPG/PNG, Smoother video, the graphics card verdict), Subtitles.cs, Captions.cs (Whisper, the speech .wav), HuntAddons.cs (their tests);
  Theme.xaml, Window.xaml, Icons.Lines.xaml, Looks.Photos/Clean/Glass/Midnight.xaml; fonts\Inter-*.ttf.
- One portable FastViewer.exe (about 1.6 MB). Release zip FastViewer-<version>.zip contains FastViewer.exe,
  "How to use FastViewer.txt", "What's new.txt" and the Inter font license.
- Settings in %APPDATA%\FastViewer\settings.ini, resume places in positions.txt, remembered turns in rotations.txt, crash reasons in
  %TEMP%\FastViewer\crash.txt.

## File types
- Photos: .jpg .jpeg .jfif .jpe .png .bmp .gif .tif .tiff .ico .webp .heic .heif .jxr .wdp .dng
  (whatever Windows' imaging codecs can read).
- Videos: .mp4 .m4v .mov .wmv .avi .mkv .3gp .3g2 .mts .m2ts .ts .mpg .mpeg .asf .webm
  (played with Windows' own video engine; HEVC needs Microsoft's "HEVC Video Extensions").
- Animated GIFs play (frames composed correctly; only the GIF on screen keeps its frames in memory).
- Music (1.20.0): .mp3 .m4a .aac .wav .flac .wma, played in the slot's video player (Slot.IsVideo and IsAudio; ScrubbingEnabled
  off, or an audio-only file never finishes opening). Shown at once as a card (a DrawingImage, no bitmap work): the album
  cover from Windows' thumbnail (or a music note), title and artist from Windows' property system (System.Title,
  System.Music.Artist), in the look's colours and font, at most 640 px high and clear of the floating video bar. Normal
  video controls, speed, Repeat, A-B loop; resume only for files of 10 minutes or more. Copy/Save frame, frame steps,
  Trim, compatible copy, Rotate and the mini player are off for music. Grid/strip tiles show the cover or a music note;
  Open dialog and "Open with" registration include music.
- Taskbar thumbnail buttons (1.20.0, Taskbar.cs, WPF TaskbarItemInfo): Previous, Play/Pause, Next under the taskbar
  preview. Play/Pause only for a video or music (hidden on photos, in the grid and Settings), icon and tooltip follow
  the playing state (updated from UpdateControls, only when changed); Previous/Next off with one file. Icons are Segoe MDL2
  glyphs drawn at 32 px, white on a dark taskbar, black on a light one (SystemUsesLightTheme). Test: --case taskbar.
- Change cover (MP3, 1.20.0; Audio.cs ChangeCoverTo/WriteMp3Cover/ReadId3, no ffmpeg): right-click anywhere on the music view or music
  "..." > Change cover... The picture becomes a JPEG of at most 1000 px (see-through parts on white) in an APIC front-cover
  frame; old APIC frames go, all other ID3 frames are kept as they are; ID3 2.3/2.4 keep their version, untagged files get
  2.3; ID3 2.2, whole-tag unsynchronisation and extended headers are refused. Written to <song>.fvcover.tmp, checked
  (new cover present, sound length equal), then swapped in by two renames (original -> .fvcover.old, copy -> original;
  rolled back if the second fails), creation time/attributes kept, date + 10 ms (same place by date; Windows' thumbnails rebuilt like rotation). Players holding the song let go first and continue at
  the same place with the new cover; strip/grid and File Explorer thumbnails refresh. Other formats show "MP3 only for now".

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
- Show (Settings > Viewing, settings line show=, 1.20.0): Everything, Photos and videos, Only photos, Only videos,
  Only music. Older filter= lines (0 photos+videos, 1 videos, 2 photos) and the 1.20 test builds' music=0 are converted.
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
- File info pane (I): name, folder, dates, size, dimensions, video length. Since 1.23.0 the pane (InfoPanel, 320 wide)
  lies over the right edge of the viewing area instead of in the column (PlaceInfo in FastViewer.cs): the column takes its
  final width in one layout, the pane slides in/out with a transform, and the stage, messages, control bar and right arrow
  glide from where they were (SlideFrom). Collapsed when closed (the audit's old "Info cut off" false alarms are gone).
- Narrow windows: labels drop, extras tuck into the "..." menu, the Info button steps aside, the video bar
  keeps its time bar (skip/repeat buttons make room), the middle play button shrinks.
- Empty window: "Drop a photo, video or folder here or press Ctrl+O"; no leftover details.
- Message screens (1.23.0): `message` is a small MessageText object (not a TextBlock); setting message.Text runs
  ArrangeMessage, which splits it into MessageIcon (picture E8B9, folder E838 for "No ...", warning E7BA for problems),
  MessageTitle (first line; "Can't open this file" for failures), MessageBody (the rest, at most 520 wide) and the buttons
  MessageOpenBtn ("Open a file", accent, when nothing is open) / MessageBtn ("Open with another app"). Opening a file at
  start-up sets no empty message first (it cost about 15 ms of first-open time).
- Files that can't be opened: a plain explanation (empty file, damaged, incomplete download, path too long,
  in use by another app, HEVC codec missing...) plus an "Open with another app" button.

## "..." menus (top bar and video bar)
- Customizable (1.21.0, Menus.cs): Settings > Buttons > "..." menus lists every item of the top menu and the video menu
  with up/down buttons and a switch; settings lines topMenu= / videoMenu= (ids in order, "-" = off; written only when
  different from the usual menus; unknown or repeated ids skipped, items new in a later version added at the end, shown).
  Not part of look files. A line goes between groups (top: File/View, video: speeds / repeat+frames / steps+loop / cover /
  trim+copy); the top menu keeps its File/View headings while each group's items sit together. Settings always stays last in
  the top menu; "Stop A-B loop" follows the last loop item shown; an empty video menu offers "Choose what's here in Settings
  > Buttons". Reset the menus. Test: --case menus (with --settings).
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
  orientation tag when possible; PNG/BMP/TIFF re-saved), also "..." > Save rotation to file. The date moves by
  10 ms (keeps its place by date, but Windows' thumbnail cache, keyed by date, refreshes; 1.20.0 fix: with the exact old
  date Explorer, the strip and the grid kept the unturned thumbnail), then Thumbs.RebuildCachedThumbnails (IThumbnailCache
  GetThumbnail with WTS_FORCEEXTRACTION for 16-1280 px, on an STA thread), then SHCNE_UPDATEITEM and the strip refresh; rotations are saved even if
  you switch away right after rotating.
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
- Speed 0.25x to 5x (0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5; 1.20.0): Settings > Videos > "Speeds in the menu"
  picks which ones the video "..." menu shows (default 0.5/1/1.5/2; 1x always; settings line speeds=); the Slower/Faster keys
  step through the same ones. A speed picked before it was hidden still shows in the menu while it's in use.
- Frame by frame: , and . step one frame (uses the real frame length read from the file).
- A-B loop: [ start, ] end, \ stop; the looped part shows as a band on the time bar.
- Continue where you left off: videos over a minute resume where you stopped (300 most recent, shared
  safely between windows), with a "Start over" button.
- Trim (Ctrl+T, or "..." > Trim; scissors icon E8C6 since 1.22.0, Crop is E7A8): a trim bar takes the control bar's place, with a filmstrip of the video
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
- Every video save keeps all sound tracks in the file's order (1.22.0; Save clip, compatible copy, Send to Discord, Crop):
  Convert.cs encodes each sound stream as its own AAC track. Windows hands an MP4/MOV's sound streams out in an order of
  its own (Windows 10: last track first) and its stream numbers say nothing, so VideoConverter.FileOrder matches streams to
  tracks: a second reader delivers the undecoded sound packets and their sizes are compared with each track's own size
  table (VideoInfo.SoundPacketSizes: stsz, or the trun tables of a file made in pieces, first 20000 packets) until every
  stream fits one track; when that can't be told (MKV etc., tracks alike from start to end) Windows' order stays (right for
  MKV). Before 1.22 only one track was kept, for MP4 the last. Test: --case tracks (scratch folder with two.mp4, two sound
  tracks of 440 and 880 Hz; saves it all five ways into the output folder, the original of the in-place crop must be whole in
  the bin; then check each "out *.mp4" with ffmpeg: track 1 must be 440 Hz, track 2 880 Hz; --real-bin uses the real Recycle Bin).
- Make GIF (1.22.0, trim bar; Share.cs, Gif.cs): the part between the handles as an animated GIF, long side 320/480/640,
  about 15 pictures a second, at most a minute. Two passes with ConvertOptions.Frame (first picks 255 colours by median cut,
  then writes with LZW, storing only what changed); written to <name>.fvgif.tmp first. "Copy to paste" in the message.
- Send to Discord (1.22.0, trim bar; settings line discordMb= 20/50/500): an .mp4 of the part that fits under the size
  (megabytes x 990000 bytes, so it fits however Discord counts) and is copied as a file for Ctrl+V. SharePlan picks the
  bits a second (sound 128 or 96 kbit/s for each sound track), then a smaller picture (down to 240) and 30 instead of 60
  frames only when needed; up to 4 tries (more bits when a good deal of room is left, fewer when it came out too big). A
  whole H.264 .mp4 that already fits and is stored the way it's shown is copied as it is. Copies go to
  %TEMP%\FastViewer\Discord; week-old ones are removed. Test: --case share.
- Crop (1.22.0, C or "..." > Crop, Crop.cs; photos and videos, not music): a frame over the picture (drag corners/sides,
  inside to move, outside for a new one; Shape: Any shape, Original, Square, 16:9, 9:16, 4:3, 3:4; Reset; arrows off while
  cropping; Space still plays; Enter saves, Esc leaves). The crop is of the picture as shown (a remembered turn is saved in).
  "Save a copy" writes a new file. "Save" (photo types Windows can write back, .mp4/.m4v) writes <file>.fvcrop.tmp, checks it
  (photo: size; video: Windows reads it, size and length right), lets the players go, then SwapInFile(..., recycle: true):
  the original goes to the Recycle Bin under its own name (ViewerWindow.Recycle: SHFileOperation with allow-undo and
  FOF_WANTNUKEWARNING, so where there is no Recycle Bin Windows asks before deleting for good and "No" keeps the original),
  the new file takes its place with the creation time kept and the date + 10 ms; the message says "The original is in the
  Recycle Bin". Photos: JPEG quality 95, PNG, BMP, TIFF keep type, details and colour profile where Windows can write them;
  other types become a JPEG/PNG copy. Videos: H.264/AAC .mp4 with as many bits as the original spent on that part.
  Test: --case crop (photo.jpg, plain.png, other.webp, clip.mp4, turned.mp4 with a 90 degree tag; scratch copies only;
  test runs put "recycled" files in <output>\recycled unless --real-bin).
- Save a frame as PNG (as shown, including rotation).
- Copy frame (1.20.0, video "..." menu or Ctrl+Shift+C): the frame on screen (up to 4K wide, turned as shown) goes on the
  clipboard as a picture and as PNG; the video keeps playing.
- Phone rotation metadata in MP4/MOV is read and applied (videos filmed upright show upright).
- Rotate videos for viewing (Ctrl+R). Every turn not saved into the file is remembered in rotations.txt (1.21.0, Rotations.cs
  TurnMemory: "degrees|size|path", 1000 most recent, shared by windows, re-read at most once a second; the size must match;
  before 1.21 turns lived only in memory and were lost on an update or quit). Tests keep it in memory unless --rotations <file>.
- Save a video's turn into the file (1.21.0, "Also save it to the file" or "..." > Save rotation to file; .mp4 .m4v .mov .3gp .3g2):
  the track header (tkhd) display matrix of every video track is rewritten in place, phone style [cos sin 0 / -sin cos 0 / x y 1]
  with the move from the track's width/height (VideoInfo.WriteTurn); nothing else changes (streams byte-identical: ffmpeg -c copy
  md5 equal). Copy to <video>.fvturn.tmp, checked (same length, rotation reads back), swapped in with SwapInFile (shared with
  Change cover: two renames, rolled back on failure, creation time/attributes kept, date +10 ms), players let go first and
  reopen at the same place (still playing if they were); Windows' thumbnails rebuilt. A turn made while saving is saved after.
  The save thread is a foreground thread, so quitting waits for the swap. Test: --case vturn (run twice, --rotations).

## Navigation and keyboard (all keys can be changed, two per action)
- Default keys: Next Right/PageDown, Previous Left/PageUp, First Home, Last End, Shuffle X, Grid G,
  Play/pause Space/K, Skip back J, Skip forward L, Back 5 s Shift+Left, Forward 5 s Shift+Right,
  Previous frame ",", Next frame ".", Loop A "[", Loop B "]", Stop loop "\", Repeat R, Volume up Up,
  Volume down Down, Mute M, Trim Ctrl+T, Crop C, Rotate right Ctrl+R, Rotate left Ctrl+Shift+R, Zoom in +/Num+, Zoom out -/Num-, Fit 0/Num0,
  Full screen F/F11, Mini player Ctrl+M, Slideshow F5, File info I, Open Ctrl+O, Save as Ctrl+S,
  Copy Ctrl+C, Copy frame Ctrl+Shift+C, Subtitles S, Delete Delete, Settings Ctrl+,
- Actions without a default key (assignable): thumbnail strip, slower/faster/normal speed, save a frame,
  make compatible copy, make captions automatically, convert to JPG, convert to PNG, save rotation to file, actual size, keep on top, copy file path, show in folder,
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
  arrow keys + Enter, click to open, Esc or mouse back button to leave.
- Search (1.21.0, GridSearch.cs): box in the grid's header (Ctrl+F): file names containing every typed word, any case, 120 ms after
  the last key; "3 of 5 match ..." in the header; Up/Down/Page/Enter work the grid while typing, Esc clears then leaves the box;
  cleared each time the grid opens.
- Sort (1.21.0, settings line gridSort=): Folder order, Name (like File Explorer: 2 before 10), Newest/Oldest first, Largest/Smallest
  first, Longest/Shortest first (System.Media.Duration from Windows' property system, read once in the background, "getting
  lengths..."; photos last). Only the grid; the viewer keeps the folder order. Test: --case gridsearch (with --settings). Only visible rows are built, so
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
- Switches stay readable with any accent (1.21.0, Looks.KeepSwitchKnobVisible, also on live Custom colour changes): the On knob
  turns black or white when the look's knob colour is under 2.5:1 on the accent (a dark Custom accent drew a black pill with
  no knob), and the On track's outline (SwitchOnEdge) becomes the secondary text colour when the accent is under 1.4:1 on the
  cards. Test: --case switches (with --settings, --motion full): every look and a Custom look on each (plus accents #000000,
  #2A2A2A, #5A5A5A, #FFFFFF, #F2F2F2) x dark/light x accent palettes grey/blue/orange/Windows', drawn on a card and measured
  (knob, track, outline), then live in the Settings window with a real switch flipped; switches.png.
- Menu icons (1.21.0): Copy E8C8, Copy frame EB9F, Make compatible copy E895, Repeat E8EE, Loop A E892, Loop B E893,
  Change cover E8D6 (line versions in Icons.Lines.xaml).
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

## Polish release 1.23.0 (no new features)
- Motion.cs: a value's first step counts as at most one screen frame (Tween.Started, firstStepCap; SimulateFrame uses the
  simulated frame length), so an animation set up in a busy moment (Settings page built, window resized) starts at its
  beginning instead of skipping ahead. Measured (--case motion, back to back with 1.22.0): frame times equal; the audit's
  first run that showed 20+ late frames for the Info pane/full screen/look change did not repeat (busy-PC noise). The
  "video: play/pause morph" step of that test really measures a video starting; the pause morph itself is clean.
- Side arrows (NavArrow style): 32x56 (24x44 in a small viewing area), chevrons E76B/E76C at 13, the chevron leans 3 px
  on hover (PrevGlyph/NextGlyph). Midnight ArrowBg is 85% black/white.
- Small viewing area (stage under 560 wide or 330 high, e.g. the mini player): FitSmallControls shrinks the arrows and the
  middle play button (0.7); the control bar is stage width - 112 (80 when small) so it stays clear of the arrows.
- Settings is its own screen (SettingsChrome): title bar text "Settings", Delete/Copy/Rotate/"..."/Mini/Open with fade out
  while it's open, and in a see-through look the viewing area and bottom bar under the page are hidden (no ghost of the
  photo through Liquid Glass pages).
- Settings text: SecondaryText shows a long explanation's (110+ characters) first sentence with a "More" link; its tip holds
  the rest and a click opens the full text in the card ("Less" folds it). Nothing was shortened in the source.
- Look cards: previews clipped with rounded corners, inside a Viewbox; LayoutLookCards gives 5, 3, 2 or 1 per row at equal widths.
- Keyboard page: empty key box shows "+", key boxes use the look's OutlineEdge.
- New look colour OutlineEdge (all four look files, Dark and Light; = ControlEdge where that is visible, else SwitchEdge;
  Liquid Glass repeats its gradient) and button style Outlined (Theme.xaml): Cancel in the delete dialog, the message
  button (ToastBtn), "Open with another app", key boxes.
- Messages at the top: ToastIcon by kind (update E72C, resume E768, saving E74E, error E7BA, others E946, none for the
  short key/wheel notes); text wraps at 520. "Couldn't save" (Save as, Save a frame) is such a message, kind "error".
- Zoom controls (Fit, zoom %, -, slider, +) are disabled when there's nothing to zoom; the zoom box is empty, not "-".
- Menu icons are 16 px like the bar icons (14 drew heavier).
- Grid: length badge bottom right on video and music tiles of 96 px and up (Tile.SetLength; lengths from the same cache as
  the length sort, read on a background STA thread, newest request first); GridEmpty screen when nothing matches.
- Skip buttons drawn 1.14x so the seconds inside are about 9.7 pt.
- Memory while away (Away.cs): minimized for 5 s, or not the active window for 30 s, and not playing, in a slideshow,
  trimming, cropping or under the pointer: every hidden video slot is closed (file on screen, wanted, revealing and fading
  slots never). Activated, mouse enter/move or a key reloads the files around the current one (BackFromAway, same order as
  ShowIndex). Test windows never count as away unless the test says so. Test: --hunt --case away (folder with videos around
  the start file): measured 565 -> 471 MB private with three 1080p players let go, all ready again 322 ms after coming
  back, first switch after returning instant, also a switch in the same instant as coming back.
- Measured against 1.22.0 back to back (6 rounds each): first open median 84 vs 88 ms, switches median 1 / p90 4 vs 1 / 5 ms;
  torture and wheel equal. Pictures: Desktop\claude\visuals-1.23\before and \after.

## Subtitles, automatic captions, smoother video, convert (1.24.0)
- Subtitles (Subtitles.cs): .srt and .vtt (SubtitleFile.Parse: "start --> end" blocks, hours optional, "," or "." before the
  milliseconds, tags like <i> and {\an8} dropped, &amp; decoded, broken blocks skipped; UTF-8 with or without mark, UTF-16, else
  the PC's code page). Found by FindSubtitles on a thread-pool thread each time a video (or song) appears: a file picked by hand
  this session, else "name.srt"/"name.vtt" next to the video, else "name.*.srt" (a language), else remembered automatic captions.
  Nothing runs on the window's thread for it; measured first open and switching equal to 1.23.0 (5 rounds alternating, folder
  of videos with .srt files: 54 vs 60 ms, median 1 ms both).
  Shown in SubBox/SubText (white on #C0000000, every look; inside SubHolder, whose TranslateTransform glides it clear of the
  control bar: PlaceSubs from ShowOverlay/HideOverlay), font 2.7/3.5/4.6% of the viewing area's height (subSize= 0/1/2), at
  most 82% wide; a 33 ms timer only while playing with subtitles on, plus UpdateTime (paused, dragging the time bar); hidden
  while trimming, cropping, in the grid and Settings. CC button SubBtn (E7F0) on the video bar only while the video has
  subtitles, accent-coloured when on; S key (action "subtitles"); settings line subs=. Video menu items: Subtitles, Choose
  (other) subtitles..., Make captions automatically / Make the captions again + Save captions as a file...
  Test: --case subs (folder: clip.mp4 + clip.srt, lang.mp4 + lang.en.srt, plain.mp4, web.mp4 + web.vtt, extra.srt).
- Add-ons (Addons.cs): class Addon = optional download kept in %LOCALAPPDATA%\FastViewer\<Id> (installed.txt marks it whole).
  A part is a plain file, a whole zip of which named files are unpacked, or ONE ENTRY of a remote zip: the zip's list of
  contents is read from its end with HTTP range requests and only that entry's bytes are fetched and inflated (the 431 MB RIFE
  release costs 12 MB). Every part has a SHA-256 built in; a mismatch is refused and nothing of it is kept; 3 tries per part;
  Cancel; Remove deletes the add-on's folder. Settings card AddAddonCard (size on the button, "x of y MB", a filling line on
  the card, tick + "Ready, works offline", Remove; it looks at the disk only once it's on screen) and GetAddon (a message
  offers the download when a feature is used first, shows progress, then carries on). Tests: --addons <scratch root>,
  --addon-source <folder with stand-in downloads> (fingerprints not compared unless the test turns that on), --case addons
  (--pattern real downloads the real ones into --addons: the only test that uses the internet). Test runs otherwise see no
  add-ons and never download.
- Automatic captions (Captions.cs): add-on "Whisper" = whisper.cpp release b5454 whisper-bin-x64.zip (whisper-cli.exe + dlls,
  github.com/ggml-org/whisper.cpp) + ggml-small-q5_1.bin (huggingface.co/ggerganov/whisper.cpp at commit 5359861c), 190 MB.
  The fingerprint of a Hugging Face file is its X-Linked-ETag header (the plain ETag after the redirect is something else).
  VideoConverter.SpeechWav: every sound track decoded by Windows straight to 16 kHz mono 16-bit (own simple conversion as a
  fallback) and mixed by time into one .wav in %TEMP%\FastViewer; when the decoder reports a changed format (flag 0x20: some
  AAC has twice the samples its header says) the new one is used, or the sound comes out garbled. whisper-cli runs hidden at
  below-normal priority with half the logical processors, "-ml 84 -sow -np"; its "[from --> to] text" lines become cues as they
  are printed (shown live), [Music]-like lines and a line repeated three times are dropped; progress moves at the measured
  speed between its half-minute steps. Kept in %LOCALAPPDATA%\FastViewer\Captions\<hash of path, size, date>.srt. Settings >
  Videos: Automatic captions card, "Language spoken in your videos" (captionLang=, auto or one of 15). 64-bit Windows only.
  Measured (Ryzen 5 5600X): an 18 s clip in 7.5 s. Test: --case captions (--addons with the real add-on, speech.mp4).
- Smoother video (Tools.cs, class Smooth): add-on "Smooth" = rife-ncnn-vulkan 20221029 (exe, vcomp140.dll, rife-v4.6 model;
  github.com/nihui) + ffmpeg.exe from Gyan's 9.0.2 essentials build (github.com/GyanD/codexffmpeg), 47 MB. If the user's own
  FrameBoost tools folder exists (%USERPROFILE%\Tools\FrameBoost\tools or FrameBoost\tools next to FastViewer.exe) its
  programs are used and the card says "Using your FrameBoost". Steps (as FrameBoost.ps1 did): read size/rate/length from
  "ffmpeg -i"; every frame as JPG at a constant usual rate (HDR HLG/PQ tonemapped with zscale); RIFE makes frames x 2 or 4
  (progress = pictures counted, with time left); encode H.264 with the card's encoder (nvenc / amf / qsv) then libx264,
  sound copied when AAC; saved as <name>_<fps>fps.mp4 via a temporary name. Free disk space is checked first; the work
  folder %TEMP%\FastViewer\Smooth_* always goes. GpuVerdict (registry: display adapters' names and memory, vulkan-1.dll):
  level 2 fine, 1 slow (built-in graphics, 2-3.5 GB), 0 too weak/none/no Vulkan ("Try anyway"); the text shows on the card
  and before downloading. Tests: --gpu "name|MB", --frameboost <tools folder|none>, --case smooth (--pattern 4 for 4x).
  Measured (RTX 3060 Ti): 6 s 640x360 clip 2x in 3.8 s, 4x in 4.7 s.
- Convert to JPG / PNG (Tools.cs ConvertPhotoFile; top "..." menu, actions toJpg/toPng): any photo; uses Crop.cs CropPhoto
  with the whole picture (upright, remembered turn included; JPEG 95). Written as <name>.fvconvert.tmp, read back, then
  moved to <name>.jpg or "<name> (2).jpg"...: nothing is ever replaced, the original untouched, the copy gets the original's
  date. .jfif/.jpe to JPG is a plain copy. See-through parts: white in a JPG (CropPhoto onWhite), kept in a PNG. Windows'
  WebP decoder reports its pixel format as "Default": CropPhoto asks for Bgra32 then, or see-through came out black (also
  fixes cropping such a WebP). Test: --case convert.
- Rotate left (Settings.cs RotateBy(dir); Rotate() = 1, RotateLeft() = -1): action rotateLeft (Ctrl+Shift+R), menu choice
  "rotateLeft" (icon E7AD mirrored by Flipped()), right-click on RotateBtn = the other way, Settings > Viewing > "The rotate
  button turns" (rotateLeft= 0/1; UpdateRotateButton mirrors RotateGlyph with a LayoutTransform and writes the tip). Same
  animation (counter-turn of -90 x dir) and the same save-to-file path as Rotate. "Rotate" is named "Rotate right".
  Test: --case rotleft (scratch folder with photo.jpg and clip.mp4, --motion full).
- Top messages have a "done" kind (tick icon E73E). New line icons: Ic.E7F0, Ic.E720, Ic.E8AB, Ic.EC4A.

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
- Appearance (look, dark or light, Animations, Transition between files, mini player volume button), Custom look, Buttons (also the "..." menus), Viewing (zoom preference, show filter, order, wrap around, thumbnail strip,
  keep on top, ask before deleting, when you rotate a photo or video), Videos (continue where you left off,
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
  keys, dialog, rotate, loop, sizes, ghost, positions, saveclose, trim (drags, keys, play selection, saving), rates (every animation simulated at 60/144/240/500 Hz), toggles (menu buttons close their menu, keys undo themselves), scrub (fast time bar drags, the dot's smoothness), custom (the custom look end to end, with --settings), showcase (pictures of an example custom look), wheel (very fast switching through videos with the controls showing: delay per press, frame times, frame grabs), minivol (mini player volume number), tips (the tips switch, with --settings), miniclear (folder with a see-through PNG, a photo and a video: the mini player measured over white and black is fully clear around the PNG, normal for the rest, and no Windows frame in mini/full screen), preloadghost (every look: the window drawn with and without the hidden preloaded players must be pixel-identical, on a see-through PNG, photos and paused videos, in the window and the mini player), squishall (presses all ~220 buttons in the window and every Settings page: each must shrink and end exactly where it started; --look Glass --motion full), skipicons (saves the skip buttons at 4x per look), rotsave (1.20.0, scratch folder with plain.jpg 400x200 red square top-left, photo.png, photo.bmp; makes tag1.jpg, tag6.jpg and a 4000x3000 sideways phone.jpg with an embedded preview itself: rotates each with saving on, checks the file decoded fresh, orientation tag, date, Windows' thumbnail, Explorer's cache at 96 and 256 (Thumbs.FromCache; plain.jpg and photo.png skip the rebuild for comparison) and the reopened window), showmigrate (old Show lines -> new choice, what each choice lets through), taskbar, vturn (1.21.0, run twice with --rotations: video turns remembered and saved into MP4s at 90/180/270, Explorer thumbnail, date, playing position, leftovers), gridsearch (1.21.0), menus (1.21.0, also icons_<look>.png of the menu icons), switches (1.21.0), cover (1.20.0, scratch copies only: changes the cover of an ID3 2.3 song with a cover, a 2.4 song without, an untagged MP3 and a locked one; logs the cover read back by its own ID3 reader and by Windows, tags, playing position, leftover temp files, the music menu and Show music files; check the sound with ffmpeg -map 0:a -c copy -f md5 before/after), music (1.20.0: music shows at once, plays, tags/cover, 0.25x/5x on music and video, Copy frame logged, speed stepping, Speeds buttons saved; folder with song.mp3 (tagged, cover), plain.wav, clip.mp4, photo.jpg; use --settings), squish (Liquid Glass button squish frame by frame for taps of 0-278 ms, plus each Custom motion style's timing and curve; use --look Glass --motion full), share, crop, tracks (1.22.0, see Videos),
  away (1.23.0, see Polish release), convert, subs, addons, captions, smooth (1.24.0, see their section),
  overlap, audit (--pattern
  full|sizes|scales|contrast: renders every screen in all four looks and checks overlaps, clipped text, contrast,
  screen-reader names, Tab order), ux, updatemsg, motion (every animation's frame times and idle CPU).
- Options: --preload n, --look <Id>, --strip, --grab-always, --settings <scratch.ini>,
  --positions <scratch file>, --rotations <scratch file>, --real-bin (crop tests use the real Recycle Bin), --addons, --addon-source, --gpu, --frameboost, --speechwav <video> <wav>, --update-feed <local json or "refused">, --update-page <file>, --update-test,
  --count, --every, --pattern, --front, --motion full|reduced|off (tests run with animations Off unless
  given), --convert <src> <dst> <rotation> [from to].
- Any run with a "--" option never contacts GitHub.
