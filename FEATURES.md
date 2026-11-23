<!-- Read this first in every new conversation about FastViewer. Updated with every release (see the release routine). -->
# FastViewer: complete feature list (version 1.30.0)

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
  Trim.cs (the trim bar), CustomLook.cs (the custom look and its colour picker), Audio.cs (music files: the cover card, tags from Windows), Rotations.cs (remembered turns, rotations.txt; video turn saved into the MP4/MOV), GridSearch.cs (grid search and sort), Menus.cs (the customizable "..." menus), Gif.cs (own GIF writer), Share.cs (Make GIF, Send to Discord), Crop.cs (cropping photos and videos), HuntCrop.cs (their tests), Away.cs (1.23.0: hidden video players let go while the window is away, and its test), Addons.cs (1.24.0: optional one-time downloads, the Helper that runs programs out of sight), Tools.cs (Convert to JPG/PNG, Smoother video, the graphics card verdict), Subtitles.cs, Captions.cs (Whisper, the speech .wav), HuntAddons.cs (their tests), Burn.cs (1.25.0: captions drawn into a copy), Vocals.cs (1.25.0: voice isolation, built but not offered), SideMenu.cs, Tracks.cs (1.26.0: what a file's sound tracks are, the mixer, the trim bar's Sound menu), SoundFix.cs (1.26.0: sound that stops a file from playing, the playable copy), SoundPlay.cs (1.26.0: FastViewer's own sound for multi-track videos and volume over 100%), Welcome.cs (1.27.0: the welcome screen, recent.txt, default viewer), SubEditor.cs (1.27.0: the subtitle editor), Hunt127.cs (1.27.0 tests), Report.cs (1.28.0: the error report), Translate.cs (1.28.0: English translation), Hunt128.cs (1.28.0 tests), Parakeet.cs (1.28.0: Parakeet captions), Loudness.cs (1.29.0: Even out volume), Hold.cs (1.29.0: hold for 2x), Stretch.cs (1.29.0: same pitch at any speed (WSOLA), speed changes timed with the picture), TestSilence.cs (1.29.0: test runs at Windows volume 0), Hunt129.cs (1.29.0: pitch / let-go / Keep speed / loops / decodeinfo tests), FramePath.cs (1.30.0: FastViewer draws the video itself), Hunt130.cs (1.30.0 tests: frames, skiphold, narrowsettings, clicks, usage, clickspam, colours, skipjam, jamrepair, fps), Jam.cs (1.30.0: a jammed video player is made again), Stats.cs (1.30.0: playback stats, Ctrl+Shift+P);
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
- Empty window = the welcome screen (1.27.0, see its section); no leftover details.
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
- Delete to the Recycle Bin (Delete key) with an optional confirmation ("Ask for permission to delete"); then the next file of the order you're in shows (1.27.0, see its section);
  the dialog always deletes the file it names and nothing moves on while it's open.
- Copy (Ctrl+C copies the file), Copy file path, Show in folder, Open with another app, Save as (Ctrl+S).

## Videos
- Floating control bar: play/pause, skip back/forward (5/10/15/30/60 s, shown inside the icons: since 1.19.0 an open circle with an arrow pointing back or forward, drawn the same in every look),
  volume (point at the speaker for a slider, click to mute, wheel changes volume; on a touch screen a tap
  opens the slider), time, seek bar, duration, Repeat, video "..." menu. The seek bar's dot moves every screen
  frame while playing (with animations on); dragging it holds the video still and shows the frames passed (at
  most ~20 jumps a second, ending exactly where you let go), then plays on if it was playing.
- Play/pause button in the middle (Show / Only when paused / Hide); click the video to play/pause (since 1.29.0 a click on a playing video pauses when the button comes up, so holding can mean 2x; a paused one starts at the press as before)
  (configurable); double-click for full screen (1.30.0: can be turned off in Settings > Viewing).
- Video dims slightly while controls show (optional); controls can also fade on a paused video (optional).
- Short notes when a key or the wheel changes something while the controls are hidden ("Volume 45%",
  "+30 s 1:42 / 5:00").
- Autoplay (optional), Repeat (R), "When a video ends": stop or play the next file.
- Speed 0.25x to 5x (0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5): since 1.25.0 a slider row at the top of the video "..." menu (see the 1.25.0 section); the Slower/Faster keys step through all of them. The 1.20-1.24 setting "Speeds in the menu" (speeds=) is gone. Hold the left mouse button on the video for 2x while held (1.29.0, see its section).
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
- Since 1.26.0 every video save mixes several sound tracks into one (see "Sound" below); what follows is how the tracks are told apart, and what "Keep the tracks separate" still does. Every video save keeps all sound tracks in the file's order (1.22.0; Save clip, compatible copy, Send to Discord, Crop):
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
  make compatible copy, make captions automatically, save a copy with captions, convert to JPG, convert to PNG, save rotation to file, actual size, keep on top, copy file path, show in folder,
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

## Captions on songs, burned-in captions, tidier video menu (1.25.0)
- Whisper options (Captions.cs Transcribe): "-ml 84 -sow -np -sns -mc 0". -sns forbids the non-speech answers ("[MUSIC]",
  a note sign) that Whisper gave for whole half minutes of singing; -mc 0 writes each half minute without the text before
  it (with it, small looped one line through most of a 3.5 minute song: 16 different lines out of 46, 48 of 50 without).
  Lines Whisper makes up when it hears no words are dropped by the Made pattern (subtitle credits such as "Субтитры сделал
  DimaTorzok", "Редактор субтитров ...", "Subtitles by", "Thanks for watching", a bare "music" in a few languages); the
  repeated-line filter now only leaves out a line after four identical ones in a row (a chorus repeats).
- Large model (Whisper.Large, add-on "WhisperLarge" = ggml-large-v3-turbo-q5_0.bin, 547 MB, same Hugging Face commit as the
  small one): Settings > Videos > "Better quality captions"; when installed it is the model used (Whisper.UsesLarge).
- Measured (word error rate, 9 Creative Commons songs from the JamendoLyrics set, github.com/f90/jamendolyrics: 4 English,
  2 German, 1 French, 2 Spanish; the first 60 s of singing of each, 1088 words; language found by itself; Ryzen 5 5600X,
  6 threads, whisper-cli on the processor):
    small as in 1.24.0            42.1%   18 s per minute of sound
    small -sns                    30.1%   17 s
    small -sns -mc 0 (shipped)    33.5%   16 s   (through the app itself: 30%)
    large-v3-turbo -sns           22.5%   58 s
    large-v3-turbo -sns -mc 0     22.6%   57 s   (shipped; through the app itself: 25%)
    small, voice isolated first   42.3% with -sns (40.7% without); large, voice isolated first 22.8%
  ggml-large-v3-q5_0 (1.08 GB) was not measured to the end: over two minutes per minute of sound on the processor.
  Whisper is erratic on songs: the same clip scored 16% or 52% wrong after a tiny change in the sound, so only totals
  over several songs mean anything.
- Voice isolation (Vocals.cs) is built but NOT offered (Vocals.Offered = false; tests switch it on with --vocals): add-on
  "Vocals" = sherpa-onnx-c-api.dll + onnxruntime.dll (two entries out of the NuGet package org.k2fsa.sherpa.onnx.runtime.win-x64
  2.0.1) + UVR_MDXNET_9482.onnx (k2-fsa/sherpa-onnx release "source-separation-models"), 36 MB. A hidden second FastViewer
  ("FastViewer.exe --isolate <44.1 kHz stereo wav> <target wav> <add-on folder>", Vocals.Child, first thing in Main) separates
  the voice in 20 s pieces (1 s crossfade), mixes 16% of the original back under it and writes 16 kHz mono (own windowed-sinc
  resampler); VideoConverter.SpeechWav takes a rate and channel count for it. On the processor: 18 s per minute of sound.
  It made some songs better (rap, pop: 20% -> 10%, 28% -> 21%) and others worse, no gain in total, and with it the large
  model lost its place in a long song. Heavier UVR models (Voc_FT) need minutes per minute on a processor (a graphics card
  path, e.g. ONNX Runtime DirectML, would be needed); Spleeter was fast but clearly worse (59%); UVR_MDXNET_KARA_2 returns
  no voice as used here.
- The user's own TikTok clips (10-15 s, loud edited music): still mostly nothing usable; before the Made filter they got
  invented credits. Song recognition + lyrics (see the project notes) remains the idea for those.
- Burned-in captions (Burn.cs): ConvertOptions.Paint gets every finished picture before it is encoded (this also switches
  off the pass-the-sample-through shortcut). CaptionPainter finds the line for the picture's time (start of the part +
  time, same rule as SubTick), draws it once per line on an STA thread (FormattedText in the look's Font.Ui, SemiBold,
  white on #C0000000 with the look's corner radius, at most 4 lines and 88% wide; size = the Subtitle size setting x
  min(height, 1.1 x width), so an upright phone video gets text sized for its width) and blends it 4.5% above the bottom.
  "Save a copy with captions..." (video "..." > Subtitles; action burnCaptions, no default key; AskSaveVideo " (captions)",
  RunConversion with the options). Send to Discord: when the video has subtitles the size menu gets a line and a ticked
  item "Burn in captions" (discordBurn=; ticking opens the menu again so a size can be picked); with it the "already small
  enough, copied as it is" shortcut is skipped and the message says "with captions". Test: --case burn (two.mp4 with two
  sound tracks + two.srt, tall.mp4 + tall.srt): text on the right number of pictures, both tracks still 440 / 880 Hz.
- Video "..." menu: "speeds" is one row (Menus.cs SpeedRow: a MenuItem with StaysOpenOnClick whose header holds "Speed", a
  FluentSlider with a stop per AllSpeeds entry and a Filled button showing the speed that goes back to 1x; wheel and
  Left/Right step it; icon EC49, line icon Ic.EC49). "subtitles" is one item "Subtitles" (key shown) with a submenu: Show
  subtitles (tick), Choose (other) subtitles..., Make captions automatically / Make the captions again, Save captions as a
  file..., Save a copy with captions... "smooth" is one row "Make smoother" with two Filled buttons 2x / 4x (Tools.cs
  SmoothMenuItems; StaysOpenOnClick row, the buttons close the menu themselves; keys 2 / Enter and 4), no submenu, since
  those are its only two choices. The submenu parent is a SideMenuItem (SideMenu.cs, made by Menus.cs SubItem): plain WPF
  closed the submenu when the pointer crossed other rows on the way to it. MenuAim decides from the pointer (polled
  every 15 ms): over the item, the submenu or the gap between them it never closes; over another row while moving
  toward the submenu (inside the triangle from the last pointer position to the submenu's two near corners, 12 px slop)
  the other rows are made deaf (IsHitTestVisible) and the submenu is held; 350 ms without heading there closes it and
  hands over to the row under the pointer; a pointer that never headed there leaves WPF's own behaviour. Holding is a
  coerce on IsSubmenuOpen (so WPF's own close timer can't win), switched off for 400 ms after any click or key and when
  the menu closes. Works with the submenu on either side (flipped at the screen edge). Submenus are new in
  the MenuItem template (Theme.xaml: chevron when HasItems, PART_Popup with the menu's own border). A click that leaves
  the menu open (speed row, submenu parent) gets no closing picture (Animations.cs OnMenuClick). Menu ids subFile and
  autoCaptions are gone (old videoMenu= lines just skip them). "Speeds in the menu" and the speeds= line are gone;
  Speeds = AllSpeeds, also for the Slower / Faster keys. Test: --case videomenu (run per --look; picture videomenu_<look>.png
  with the submenu drawn beside the menu; it also walks a made-up pointer through MenuAim for both sides and through the
  real item with its real popup). The real pointer on a real menu can only be checked by hand.
- More tests: --case captionsall (captions for every file in the folder through the app, text to <out>\<name>.txt, for
  scoring), --case addons --pattern real also fetches Vocals and WhisperLarge; --speechwav <video> <wav> [rate channels].

## Sound: several tracks, volume over 100%, files that won't play (1.26.0)
- What Medal clips hold (measured on the user's clips, 120 newest: 32 with 2 sound tracks, 44 with 3, 44 with 4): track 1
  is Medal's own ready mix (exactly track 2 x 1.00 + track 3 x 0.25 in a raw clip), track 2 the game, track 3 the
  microphone; 2-track clips are the ready mix + the game (same sound), 4-track ones add silent tracks. Clips cut in Medal's
  editor have the same tracks with different skipped starts (edit lists: 21 ms for track 1, 243 ms for the others).
  Windows' player (MediaElement) plays one track, for an .mp4 the last (the microphone).
- SoundLayout (Tracks.cs, SoundMixing.Layout, VideoConverter.FindLayout): Count, MixTrack (the track that is the others
  mixed, -1 if none), Sources (the tracks that make up the sound), Names ("Game", "Mic" for sources 1 and 2 when track 1 is
  the mix, else "Track n"), Streams (Windows' stream number per track, via FileOrder), Offsets (seconds to add to each
  track's time stamps to get the picture's clock: 2 x (the picture's skipped start - the track's own)). Found by listening:
  the first 12 s of every track at 8 kHz mono (Listen), then MixFit: least squares of track 1 against the others, each
  moved up to 25 ms to line up; under 6% of its energy left = it is the mix. With the file's order unknown (MKV...) every
  track is tried. Remembered per file, size and date in memory and in %LOCALAPPDATA%\FastViewer\tracks.txt ("count|mix|
  streams|offsets|file <key>", 5000 at most; tests keep nothing unless --tracks <file>). Takes 0.1-0.3 s the first time.
- Saving (Convert.cs): ConvertOptions.KeepTracks = every track on its own; otherwise SoundMixing.For(file) gives a volume
  per track (the mix track 0, each source its Sound volume; two tracks with the same sound: just one) and TrackMixer adds
  the decoded tracks (all asked for as 48 kHz stereo 16-bit) by time, in floats, and hands out pieces every track has
  reached; over full scale bends over smoothly (Limit: tanh above 26000) instead of clipping. Tracks with volume 0 aren't
  decoded. One AAC track is written. Each track's own skipped start is used (before 1.26 every track got the first one's:
  tracks 2 and 3 of a Medal edit came out 0.44 s late). Send to Discord counts one sound track in its plan and doesn't take
  the "already small enough, copied as it is" shortcut for a file with several tracks. Settings lines soundMix= (1 = mix),
  soundVol= (percent per source, 0-200), soundChoice=. Test hook: --convert <src> <dst> <rot> <from> <to> [keep | volumes].
  Checked with ffmpeg + a least-squares fit: saved sound = 0.99 x game + 0.99 x mic (residual -72 dB), 0/200 -> 1.99 x mic.
- Trim bar > Sound (TrimSoundBtn, only for a video with 2+ sources, hidden under 370 px of bar; the hint makes room, its
  word goes under 800): a row per source (SoundRow: slider 0-200 in steps of 5, the number is a button back to 100%, wheel
  and arrow keys work) and "Keep the tracks separate" (tick; rows greyed). Test: --case sound (with --settings; folder
  with clip.mp4 = three tracks, the first the mix, and one.mp4).
- Playing (SoundPlay.cs): the picture stays in the MediaElement, exactly as before. FastViewer plays the sound itself only
  when it has to (OwnSound): a file with 2+ sources, or volume over 100%; the MediaElement is silenced then (IsMuted, also
  at every place that used to set it). SoundPlayer = one thread: a source reader with only the heard tracks (48 kHz
  stereo), TrackMixer (volumes of the moment), SpeakerOut (waveOut, about 0.1 s queued; waveOutGetPosition says what was
  heard). The window tells it 30 times a second (soundTimer, only while own sound is on) where the picture is
  (MediaElement.Position + a Stopwatch stamp), whether it plays, the speed and the volume (SoundWish). The player keeps
  "heard" = expected: within 8 ms nothing, up to 120 ms a nudge of at most 3% in how fast it reads (resampled, linear),
  beyond that (a jump, another speed) it starts again from where the picture is (no second start for 350 ms). A picture
  that stands still for 120 ms (looking for the place after a jump) has its sound wait. It starts only once the picture is
  on screen (revealing != slot), so opening is never slowed. Other speeds are plain resampling: the pitch changes.
  Volume 0-200% (MaxVolume 2; the slider's middle mark is 100% and a drag catches on it); over 100% goes through Limit.
  LearnSound: each loaded video's track count (VideoInfo.SoundTrackCount, read from the MP4's track list) and, for 2+,
  its layout are found by one BelowNormal thread: the wanted file at once, the neighbours 0.6 s after the last switch.
  Track button TracksBtn (E9E9, next to the speaker, only for 2+ sources, accent-coloured when not everything is heard):
  "Game and mic" / "Only game" / "Only mic" (soundChoice). Hearing everything uses the Sound volumes, so the trim bar's
  sliders are heard at once. Tests run silent (SoundWish.Silent). Test: --case ownsound (folder with clip.mp4 = a real
  Medal clip, one.mp4): first sound 44-53 ms after the clip shows, 8-25 ms from the picture while playing, one restart
  after a jump, own sound off for a one-track video at 100% or less.
- While trimming, HeardGains ignores soundChoice: you hear every source at its Sound volume, i.e. what will be saved
  (round 2: with the track button on "Only game" the sliders did nothing). Every track at 0 is silence, not an error.
  SoundPlayer.Level = running loudness of the mix (tests). Test: --case trimsound (with --settings; two.mp4 with a 440
  and an 880 Hz track): slider changes measured 0.5 s later as 1.41 x / 2.24 x / 1.00 x one track.
- Keep speed (round 2; Share.cs keepSpeed, settings line keepSpeed=, ExportSpeed = the viewing speed when ticked and
  not 1x, else 1): AddKeepSpeed puts a line and the ticked item "Keep speed (1.25x)" into the GIF and Discord menus
  (ticking reopens the menu); Save clip (SaveClipClicked) is one click at 1x and at another speed opens a menu "Save
  clip at 1.25x" / "Save clip at normal speed" + the item; Enter saves at once. ConvertOptions.Speed: a picture's time
  is divided by the speed; pictures a second = min(original x speed, max(original, 60)), then MaxFps as usual, and a
  picture is kept when it lands in a new slot of that rate (others left out evenly); Paint (burned captions) still
  gets the original time, Frame (GIF) the new one. Sound: every kept track goes through a TrackMixer (one mixer per
  track when they stay separate) and SoundStretch reads the mixed pieces at the speed's pace with straight lines
  between samples, the place between two samples carried from piece to piece: the same plain resampling as the player,
  so the same pitch. Send to Discord plans with the new length and rate and skips the "copied as it is" shortcut; the
  GIF's one-minute limit counts the new length; a kept-speed clip is named "(clip 1.25x)". Test hook: --convert ...
  [keep | volumes | -] [speed]. Checked: two.mp4 6 s at 1.25x -> 4.80 s, tones 440/880 -> 550/1100 Hz; 0.5x with
  separate tracks -> 12 s, two tracks, 220 Hz; 2x of a 30-a-second clip -> 60 a second. Test: --case keepspeed (with
  --settings; clip.mp4): menus at 1x / 1.25x, the setting, GIF 4.84 s, clip 4.82 s, Discord copy in 1 MB 970606 bytes.
- Speed changes (round 3, SoundPlay.cs). Found with --case speedstorm: (a) Windows' player (MediaElement) locks up for
  good when SpeedRatio is set every ~12 ms (picture frozen, later speeds ignored; also in 1.25.0, where the menu's speed
  slider made it reachable); (b) the sound thread ended on an exception (a jump Windows refused near the end of a clip,
  0xC00D36E5) and was never started again, and closing with the X only hides the window (ready in the background), so it
  stayed dead; (c) every speed change restarted the sound (up to 0.77 s of silence). Now:
  SetSpeed -> ApplySpeedSoon: the MediaElement gets a speed at most every 250 ms, the last one asked for always arrives.
  WatchPicture (rateTimer, 0.4 s, only while a video plays): picture still for 0.8 s (not at the end, not scrubbing or
  revealing, not within 0.6 s of a speed change) -> WakePicture (Pause, same Position, Play, SpeedRatio); a third time in
  a row -> ReopenPicture (Close, Source again, SeekOnOpen); moving at over 1.4 x or (up to 2x) under 0.7 x of the set
  speed twice -> the speed is set again (via a slightly different value, the same number is ignored). pictureRate (what
  the picture really does) is what the sound follows when it is clearly not the set speed.
  OwnSound is also true at any speed other than 1x, and for that file after it went back to 1x (soundSpeedFile): the
  sound's speed changes inside SoundPlayer without stopping (runSpeed follows the wish; "handed" remembers frames and
  picture-seconds of what was given to the speakers, so "heard" stays right across speeds). For 2.5 s after a change,
  and until it is within 30 ms again (at most 9 s), the sound may be up to 0.5 s off before it starts again and the nudge
  goes up to 20% (the MediaElement stands still 0.1-0.8 s at a change, which puts the sound ahead); a picture standing
  still counts as stuck only after 1.2 s then. Pieces from the mixer are about 60 ms of heard sound at any speed.
  SoundPlayer.Step runs inside a try: any error -> LetGo (reader and speakers released), 250 ms, again (Failures,
  Trouble). Hand gives up after 1.5 s of speakers taking nothing. Sounding = real sound handed over in the last 0.35 s;
  BeatAge = seconds since the thread went round. TellSound: BeatAge over 2 s -> the thread is abandoned and a new
  SoundPlayer made (soundRecoveries); own sound away for 1.5 s while the picture plays -> the MediaElement is unmuted
  until it returns (soundFallback); the MediaElement is silenced when own sound is there, at once for 2+ tracks.
  EndOwnSound (from GoToBackground after Hide): timers stopped, player quit, all of this state cleared.
  Test: --case speedstorm --pattern <file> (310 changes in 8 bursts, one every 600 down to 12 ms, as slider / keys /
  back to 1x; then the picture paused behind FastViewer's back, the sound thread ended, EndOwnSound). Before: frozen from
  burst 5 on, sound thread dead. After, normal clip and Medal clip: every burst ends at the set speed with sound, no
  silence, restarts of 5-27 ms (0.27 s once, at the test's own jump), nothing woken or replaced; the forced failures: picture woken
  once and moving again within 3 s, thread replaced once with Windows' player sounding meanwhile.
- Round 4 (replaces parts of the round 3 text above; this is how it is now). The user's findings: a fast slider drag
  stopped the video (every speed given to the MediaElement makes it stand still 0.1-0.8 s and start again from where it
  was; four a second plus the watchdog's "same Position" wake meant it never got anywhere), and after a loop the sound
  sped up and slowed down within half a second (the sound followed pictureRate, a wrong reading at a loop, and that also
  switched on the 20% catch-up).
  ApplySpeedSoon: a change with none in the 400 ms before goes to the MediaElement at once; in a run of changes only
  the one that is still standing 140 ms after the last (speedApplies counts them: 0 during a 6 s drag, 1 after).
  WatchPicture: off while SpeedChanging (asked or applied within 2 s, or one pending) and for 2.5 s after it acted;
  still means under 5% of the set speed for 2 s; WakePicture = Play again (second time Pause + Play), never a Position;
  third time ReopenPicture; "faster than set by 1.4 x" -> speed set again, at most twice per speed; the "slower than
  set" repair and the sound following pictureRate are gone (the sound follows SpeedRatio only).
  OwnSound at 1x only for the 2 s a speed change counts as going on (soundSpeedFile + SpeedChanging); after that Windows'
  player has the sound again (no own sound at 1x and 100% for a one-track file, as before round 3).
  Starting in step: TellSound lets the sound go (soundGo) only after the place moved on three ticks in a row at a
  steady pace (0.6-1.5 x of what the speed makes likely); a jump (back, or over 0.25 s too far) or standing still
  (0.12 s; 1.2 s around a speed change) stops it at once. After a loop (OnVideoEnded sets soundLoopTick) the MediaElement
  moves its clock on by about 65 ms some 0.3 s in, every time: the sound waits for that step (at most 0.6 s) and begins
  about 0.44 s into the loop. SoundPlayer: the first sound after a start drops whole pieces until one reaches the picture.
  Staying in step: outside the moments after a speed change the reading pace is bent by at most 0.4% (MaxNudge); 25 ms
  or more off -> a sliver of at most 30 ms is left out (skipLeft) or played twice, at most every 200 ms, with a 3 ms
  glide at the joint (smooth; Splices counts them). Render makes a stretch of a piece for the speakers. Trace (tests):
  how far off every 50 ms after each start.
  Round 4b: WatchPicture also wakes a frozen picture while the speed is being changed (still 2 s; waking only says
  play), the "faster than set" repair waits for the change to end; "at the end" = the last max(1.5 s, 0.75 x speed),
  where standing still 0.8 s counts as ended (Windows' player sometimes stalls there after a speed change and never
  says it ended: 2 of 8 drag runs on a 20 s clip). Known: a 11 s clip at 5x with Repeat is silent for up to 1.4 s after
  each loop (the sound waits for the picture to settle; the clip loops every 2.2 s).
  Round 5 (loops): the sound loops by itself. The window counts the picture's clock on across loops (soundLoopBase,
  NoteLoop once per loop: OnVideoEnded or TellSound seeing position 0 at the end, which Windows' player shows just before
  MediaEnded) and sends LoopLength/LoopBase/Looping; the end (last 0.5 x speed s) and the first second after a loop never
  stop the sound. SoundPlayer wraps its reading at LoopLength (atWrap, wrapBase), keeps the next loop's first piece
  (held) until the picture's clock moves past the loop start, then aligns it (pendingLead). Pieces never cross the wrap
  (TrackMixer.Position). Within 1.2 s after that a drift over 0.11 s re-syncs at once (other speeds: the picture stands
  still 0.12-0.17 s after moving for an instant); otherwise splices are 10 ms equal-power crossfades (CrossFade).
  The soundLoopTick wait of round 4 is gone. Measured, 6 loops each: user's clip 1x no gap/no re-sync (12 crossfades
  for Windows' ~70 ms clock step), 0.75x / 1.5x one re-sync per loop (gaps 7-8 ms), Medal 1x perfect, 1.25x / 2x gaps
  11-12 ms; all within 13-60 ms after. A StartPlayback-free loop restart was tried: no difference.
  Tests: --case speeddrag --pattern <file> (6 s of slider dragging, a change every 16 ms, then let go at 1.5x, then
  eight single steps, then back to 1x): the video never went back, stood still 0-124 ms, was at the last speed 0.35 s
  after letting go, single steps arrive at once; 3.5 s back at 1x the sound is Windows' player's (FastViewer's for a
  Medal clip). --case loopsound --pattern <file> (Repeat on, volume 120% so own sound plays at 1x; six loops, two
  jumps): the user's 11 s HE-AAC clip: 9-19 ms off at worst, no restarts, no slivers, pace bent 0.10-0.19%. (Before the
  step was waited for: 80 ms off after each loop, two slivers.) That clip is also the first real HE-AAC file tested: it
  plays in Windows' player and through FastViewer's own sound.
- Measured against 1.25.0 back to back (--selftest 150, old and new taking turns, 6-7 rounds; folder of 4 real Medal
  clips of 54-190 MB + 1 video + 3 photos): switches median 6 ms / p90 13 ms in both; first open 532 vs 528 ms with the
  layouts remembered, 560 vs 564 ms without. Ordinary folder: median 2 vs 1 ms, p90 10 ms both. (Before the sound waited
  for the picture and the neighbours' analysis waited for quiet, first open of a Medal clip was 60-100 ms slower.)
- Sound that stops a file from playing (SoundFix.cs): VideoInfo.SoundProblem reads each sound track's description: an
  mp4a entry whose esds has no AudioSpecificConfig (the user's VK download: AAC-LC 44.1 kHz stereo, fragmented, written
  by Lavf; Windows refuses the whole file, Photos and ffmpeg guess), rare AAC kinds Windows 10 can't decode (Main, SSR,
  LTP, LD, ELD, xHE-AAC), Opus / DTS / Dolby Digital Plus / AC-4 / TrueHD in an MP4. FriendlyError names it. The missing
  description is fixable: the same descriptors written with short lengths leave room for the 2 config bytes (AAC-LC, rate
  and channels from the sample entry) inside the same box, so nothing in the file moves. "Make a playable copy"
  (MessageFixBtn, accent, next to "Open with another app"): copies the file to "<name> (playable).<ext>", writes those
  bytes and, when the header says length 0 in a file made of pieces, the length worked out from the pieces (LengthPatch:
  tfdt + trun of each moof), checks it (description reads back, Windows finds the sound), keeps the date + 10 ms, then
  shows the copy. Streams are byte-identical (ffmpeg -c copy md5 equal). Also for .m4a. WatchForSilentFailure: Windows'
  player sometimes never reports that it gave up on such a file (the old file stayed on screen for good): a video or song
  still not open 0.7 s after it was asked for gets its sound description read off the window's thread and, if that is
  the problem, counts as failed. Test: --case soundfix (scratch folder with broken.mp4, broken.m4a, good.mp4).
- Not done / not verified: playing such a file without making a copy (the picture's player is still Windows' own);
  HE-AAC: one real file tested in round 4 (plays both ways); the real sound and lip-sync can only be
  judged by ear on the user's PC (tests measure clocks, silently).

## Fixes, welcome screen, better captions, subtitle editor (1.27.0)
- Settings no longer pauses (OpenSettings used to call TogglePlay): a video or song plays on behind the page. Show/Order
  (ReloadFolder -> OpenPath(CurrentFile, remember: false) keeps the slot of the file on screen, so it isn't reloaded),
  Preload (SetPreload never touches shown/wanted), looks and "View at actual size" don't interrupt it either. Test:
  --case settingsplay (a video and a song: position moves on through every step; paused stays paused).
- Welcome screen (Welcome.cs): the empty window's message is "Open a photo, video or song / Drag a file or folder here,
  or press <Open key>." (EmptyMessage); ArrangeMessage -> ArrangeWelcome(WelcomeShowing = no files, not mini, that text)
  shows WelcomeSettingsBtn next to "Open a file", WelcomeDefaultBtn ("Make FastViewer my default viewer", icon E7AC) only
  while IsDefaultViewer() is false (AssocQueryString ASSOCSTR_EXECUTABLE for .jpg .png .mp4 = this exe), and up to 5
  recent files/folders (WelcomeRecent rows: 40 px shell thumbnail or a type icon, name, folder; click = OpenPath).
  Recent list: %APPDATA%\FastViewer\recent.txt, newest first, 20 kept; OpenPath(file or folder) adds it 1.5 s later on a
  pool thread (never on the way of the first picture); the existence checks and the default check run on a pool thread
  too; re-filled on Activated. Making it the default: RegisterOpenWith now also writes HKCU\Software\Classes\FastViewer.File
  (open command, icon, TypeOverlay ""), Software\FastViewer\Capabilities (FileAssociations -> FastViewer.File) and
  Software\RegisteredApplications\FastViewer, then opens ms-settings:defaultapps with a message which entries to pick
  (Windows 10/11 don't let an app set itself). ShowWelcome() (also when a folder becomes empty by deleting or from outside)
  lets every player go like LetGoOfFiles but keeps watching the folder. Test: --case welcome (no file; --recent <scratch>,
  --default yes|no; pictures welcome.png / welcome_again.png).
- Delete goes to the next file (FastViewer.cs TryDelete, FolderWatch.cs): the file after the deleted one in the list as it
  is (sorted, shuffled, filtered), or the one before when it was the last (NeighbourAfter); an empty folder -> welcome. A
  gone file counts as deleted whatever SHFileOperation returns (before: a non-zero result retried 10 x, "Couldn't delete",
  and a folder rescan in between had picked "the same number" in a re-sorted list = a random file). Rescans in "Same as
  File Explorer" order keep the known files' order until Explorer answers (before: name order for a moment), the answer
  leaves out files deleted meanwhile, and a file deleted from outside is replaced by its nearest surviving neighbour in
  the old order. Test runs "delete" into <out>\recycled. Test: --case deletenext (scratch copy of a folder with dates
  that differ from the names: name order, the last file, newest first, shuffled, only videos, emptying it).
- Skip buttons: the seconds are a SkipNumber (Looks.cs): the digits' outline (bold, the look's font) centred exactly in the
  circle by the digits' own box (all digits 0-9 for the height, so every amount sits at the same height: 7.2 of the 22
  grid); two digits are narrowed to at most 9.6 wide (at most 0.8 x) so they never touch the circle. Test: --case
  skipnumbers (skipnumbers_dark/light.png: every look x 5/10/15/30/60, plus menu icons).
- Rotate icons in the menus, Settings' "..." menu list and the Keyboard page: Tabler Icons "rotate" (MIT) for Rotate left,
  mirrored for Rotate right (Glyph.RotateLeft = U+F1A0, Glyph.Always: drawn as lines in every look; in a Segoe look with
  a 1 px line at 16 px like the Segoe icons, in a line-icon look with Look.IconStroke). The top bar's Rotate button keeps E7AD.
- Automatic captions, measured on the film "Духless" (1:45:25, AAC 5.1, large-v3-turbo; reference = an .srt with right
  timing, its words not used; measure.py in the work chat's scratch folder): spoken lines with no text 9.8% -> 4.7%, start more
  than 3 s off 6.9% -> 1.6% (p90 2.1 -> 1.7 s), longest line 30 s -> 6.7 s, lines over 7 s 32 -> 0, blocks written twice 1 -> 0,
  a label repeated within 5 s 3 -> 0; 1057 -> 1150 lines; 57.5 min for the film through the app (Ryzen 5 5600X, 6 threads;
  the 1.26.0 run took about as long). Left: 4 stretches of 3-5 missed lines (40:28, 1:08:32, 1:16:49, 1:39:19).
  What changed: (1) VideoConverter.SpeechWav makes mono from 5.1/7.1 itself (asks Windows for 16 kHz with all channels):
  centre + 0.3 x (front left + front right); Windows' own downmix buried the dialogue (on six hard stretches, 9 min: 17% of
  the spoken lines without text -> 4%; over 1:30-12:00 continuous 14.5% -> 4.5%; same Whisper time). (2) Whisper.Tidy, on
  the finished list and on captions made before when they're loaded: a block of 2-10 lines written twice in a row keeps
  the second copy (when its times are made-up whole-second steps, its lines are spread by reading time from that copy's own start up to the next line; round 2); the same ALL-CAPS label again within
  5 s becomes one line; a line longer than its reading time (ReadingTime = 1.5 s + 0.06 s a character, 7 s at most) keeps
  its END and starts later (Whisper's long lines start early in the quiet before them: on the film 11.9% vs 14.0% of the
  spoken lines without text when cutting the end instead); every line ends when the next begins; one of under 1.2 s gets
  up to 1.2 s when there's room. Lines shown while writing are capped the same way. Tried and left out: whisper's -dtw
  (token timing; output identical), VAD (needs a separate model download). Test hook: FastViewer.exe --tidy <in.srt> <out.srt>.
- Subtitle editor (SubEditor.cs; video "..." > Subtitles > Edit subtitles... / Write subtitles..., action editSubs, Ctrl+E):
  pane SubEditPanel (360 wide) in the Info pane's column (Info closes; the picture moves left; slides like Info). Header
  with Undo (E7A7) and close, the source ("clip.srt / Automatic captions / New subtitles · n lines · changed"), Shift all
  -1/-0.1/+0.1/+1 s (total shown), a virtualized ListBox (style SubLine) of "m:ss.f  text" rows, and the chosen line:
  text box, Start/End with -/+ RepeatButtons (style SubStep; 0.1 s, Shift 0.5 s) and "Now", Add line here (at the playing
  moment; the line on screen ends there), Delete, Merge with next (joined, two lines when long), Save as... and Save.
  Picking a row seeks there; while playing the chosen row follows the video (not while typing or within 3 s of a pick).
  Every change goes to `cues` at once (what shows in the video); Undo = snapshots (200). Save as -> SaveFileDialog
  ("<video>.srt", or "(edited)" when that exists), the file is then this video's subtitles (chosenSubs). Save: a file ->
  first click turns the button into "Replace <name>?", the second writes (temp file + File.Replace); automatic captions ->
  Whisper.Keep; new subtitles -> Save as. Written UTF-8 with BOM, empty lines left out. Keys: typing goes to the box (2 =
  not a shortcut), Esc leaves the box then the pane, Ctrl+Z undo, Delete/arrows act on the list when it has focus (never
  delete the video there). Next/previous file is off while the pane is open; full screen, mini player, Settings, grid and
  another file close it. Unsaved changes stay for the session (editedSubs, used by FindSubtitles) with a message offering
  Save as. Test: --case subedit (clip.mp4 + clip.srt with 5 lines, plain.mp4; SubSaveTarget instead of the dialog).
- Round 2 (user's tests on the real screen): (1) RefreshCustomCard cast the look card's preview holder as a Border, but since 1.23.0 it
  is Border > Viewbox > Border: InvalidCastException from the customSaveTimer 0.5 s after any Custom change once Settings >
  Appearance had built its cards (1.23.0-1.26.0 too). Now PreviewHolder(card) with safe casts. Test: --case customcard (92
  steps: every dropdown item via huntMenuHook, roundness, name, see-through colours per group and per part, Show all colours,
  Start over; logs that 1.26.0's cast fails on the same card). Safety net (Program.Main): DispatcherUnhandledException is
  handled outside test runs: Program.LogUiError appends to %LOCALAPPDATA%\FastViewer\errors.txt (30 a minute at most, over
  1 MB -> errors.old.txt) besides %TEMP% crash.txt, and the window carries on (errors on other threads still end the
  process, as .NET 4.8 decides). Test: --case safetynet --errors <file>. (2) Settings follows a see-through Custom look:
  SettingsSeeThrough = lowest alpha of WindowBg / TopBarBg / PageBg (Custom look only); UpdateSettingsSeeThrough gives the
  SettingsPage PageBg with that alpha but at least 0.55 (SettingsLeastCover); cards keep their colour; the viewer stays
  visible then (also in a see-through window) and its Overlay (bars, arrows) is Hidden while Settings is open; updated live
  from CustomChanged. Test: --case settingsclear. (3) OpenFromOutside: null (tray icon) reopens lastOpened, "" (FastViewer.exe
  started without a file, handed over the pipe to the background copy) shows the welcome screen; before, it opened and
  played the last video while the welcome screen showed for a moment. Test: --case handover. (4) RotateBtn uses
  Glyph.RotateLeft too (mirrored unless "The rotate button turns" is left). (5) Block timing as above: the film's 49:08 block
  now 49:16.8-49:24.8 (speech 49:18-49:30); in the 1.27.0 run itself it was never repeated (Садись at 49:18.5). Of the old
  captions' lines that tidying moves over 1.5 s, 36 end up nearer a spoken line, 13 further (long lines whose start was
  right; neither a gap before the line nor a whole-second start tells which end to keep, keeping the end wins about 2:1).
- Round 3: (1) Hidden preloaded files: non-glass looks kept them at opacity 1 behind the "cover" (CoverBg), so a Custom look
  whose background (CoverBg is in that group) is see-through showed them (user's #17121212: --case preloadghost found 821062
  differing pixel values on photos). UpdateSlotOpacity: glassSlots also when BrushAlpha(cover.Background) < 0.999 (hidden slots
  at opacity 0, cover collapsed, as in Liquid Glass); CustomChanged calls it. Test: scratch ghost.ps1 = preloadghost with those
  colours (0 differences after, also based on Glass and in the mini player). (2) Subtitle editor: Floating is false while
  subEditOpen (floating bars put the window buttons over the pane's header); pane width SubPaneWidth = 45% of the viewing area,
  260-360, kept on resize; Shift all row is a WrapPanel; Start/End rows: label | - | time (fills) | + | Now docked right;
  the pane scrolls as a whole when the window is short (DockPanel at least 440 high); Save's confirm text is "Replace the
  file?" (the tip names it). Test: --case subeditfit (Custom with floating bars, Clean, Photos x 1440x760 / 900x600 / 640x480 /
  520x420: every enabled control gets its click, nothing sticks out, window buttons free). (3) Welcome: FileButtons (Delete,
  Copy, Rotate, Mini, Open with) are collapsed while WelcomeShowing (UpdateResponsive's show() checks fileButtonsHidden; only
  on a change); right-click a Recent row -> menu Remove from Recent / Clear recent (RecentMenu, huntMenuHook in tests), and
  WelcomeClearBtn "Clear recent" beside the heading; ChangeRecent/WriteRecent on a pool thread. Test: --case welcome.
- Part D: the 1.20 music test's last step clicked the Settings speed buttons removed in 1.25.0 (crash "Sequence contains
  no matching element"); it now checks that they and speeds= are gone and lists the speed row's stops. The changed pitch
  at other speeds stays as it is. VK-style AAC without its description still needs "Make a playable copy": Windows' player
  (the picture) refuses the whole file, and playing it as it is would mean patching the user's file or a hidden copy.

## English translation, error report, frame rate (1.28.0)
- English translation (Translate.cs; video "..." > Subtitles > Translate to English / Finish the English translation, Show
  original, Show English, Save English as a file...): every Cue has `En` beside `Text` (same timing). Add-on "Translate":
  llama.cpp b12418 win-vulkan-x64 zip (llama-server.exe + llama-server-impl/llama/llama-common/mtmd/ggml*/libomp dlls; Unpack
  patterns now take one * anywhere, Addons.cs Like) + tencent/Hy-MT2-1.8B-GGUF Q4_K_M at commit a0c709d9 (Apache 2.0, its
  LICENSE.txt is part of the add-on; checked: no territory terms, unlike HY-MT1.5) = 1.17 GB. Translator.Run starts
  llama-server hidden on 127.0.0.1 at a free port (--offline, --no-webui), in a kill-on-close job object so it never
  outlives FastViewer; graphics card when GpuVerdict.Level >= 2 (-ngl 99, 8 slots), else processor (-ngl 0 -dev none,
  4 slots, half the logical processors, below-normal priority); a card that fails (to start or halfway) hands over to the
  processor. One line per request with Hy-MT2's own prompt, greedy (temperature 0, top_k 1), cache_prompt; Clean() drops
  quotes it added, keeps the line count, caps runaways. Order: from the line being watched to the end, then the start;
  each line shows when done (cueShown reset). Kept: Captions\<hash>.en.srt (automatic captions: the captions' hash; a
  file: SHA1 of its path), written also after Cancel, Attach() puts it back by timing (0.05 s) when subtitles load
  (FindSubtitles, UseSubtitleFile). On screen: SubTextEn under SubText in SubBox (#E6E6E6, regular, 0.88 size); subsOrig=
  / subsEn= (one always on); Settings > Videos: add-on card + "Subtitles in two languages" dropdown. Burned in:
  CaptionPainter(original, english) draws both. Subtitle editor: English box under the text (Undo, Merge, Save keep it;
  Translator.Keep on Save / Save as).
  Measured (film "Духless", 1150 lines of automatic captions): RTX 3060 Ti 62 s; Ryzen 5 5600X 6 threads 341 s (in the app
  51 lines in 17.5 s with start-up). Tried and left out: 16 lines per request with delimiters (376 s, 5 groups came back
  with the wrong count), the two lines before as context (CPU 726 s, quality mixed: 3 of 10 better, 2 worse), Q4_0 (no
  gain), more slots or 12 threads (no gain), Tencent's 2-bit GGUF (needs unmerged llama.cpp PR 19357), Qwen3-0.6B (228 s
  but 124 of 1150 lines echoed the instruction). Test: --case translate --addons <scratch> --addon-source <3 downloads>
  [--translate-on gpu|cpu] (start file: a video with an .srt beside it).
- Error report (Report.cs): Settings > About "Error report" card (count and date of the last 30 days' errors, Copy error
  report), a toast after an error is written (at most every 5 min) and "FastViewer closed unexpectedly last time." once
  (fatal errors now also go to errors.txt as "(closed)"; errors.seen remembers the one told). ErrorReport.Build: version,
  Windows (registry; build >= 22000 = Windows 11), processor, screen, newest errors of 30 days grouped (same message and
  FastViewer frame), at most 1900 characters inside ``` for one Discord message; Scrub(): FastViewer's folders by name,
  any other path -> "<file .mp4>"/"<folder>", the user and PC name -> "<name>". Made on a pool thread, copied on the
  window's. Test: --case errorreport --errors <file>.
- Info pane: "Frame rate" and "Video format" under Dimensions, from a pool thread (cached per file). MP4/MOV:
  VideoInfo.FrameRate reads the whole stts (variable when frames more than 1% off the usual length make up over 2%;
  steady -> the usual frame length, snapped to 23.976/24/25/29.97/30/.../120/144 within 0.5%; variable -> "about 95.5 fps
  (up to 120)" when the usual rate is over 2 off the average) and the stsd code (avc1 H.264, hvc1/hev1 HEVC, av01, vp09,
  ProRes...). Other files (MKV, WebM, AVI, MPEG-TS named .mp4): VideoConverter.NativeVideo via Media Foundation (subtype
  four-letter code, H.264 elementary stream GUID, MPEG-2), the rate from the first 120 stored frame times (mean of the
  normal gaps, snapped). Test hook: --fps <video> <out.txt>; --case inforows.
- Make smoother (Tools.cs): the new file gets the original's creation and modified dates minus 10 ms.
- Captions in 2-minute pieces (cut at the quietest half second) were built and measured on the whole film, then taken
  out: 58 instead of 42 spoken lines without text, 9 instead of 4 missed stretches (new ones near the cuts). Test hook:
  --transcribe <wav> <out.srt> <language> (the installed add-on).

- Parakeet captions (Parakeet.cs): add-on "Parakeet" = transcribe.cpp v0.4.2 windows-x86_64-cpu-vulkan.tar.gz (only
  transcribe.dll, ggml.dll, ggml-base.dll, ggml-cpu-*.dll; Addons.cs now unpacks .tar.gz: UntarGz) + handy-computer/
  parakeet-tdt-0.6b-v3-gguf Q8_0 at commit d8d8c32c (NVIDIA, CC BY 4.0) = 758 MB; card "Faster captions (Parakeet)" in
  Settings > Videos (needs Automatic captions), "Caption engine" Automatic / Parakeet / Whisper (captionEngine= 0/1/2).
  Parakeet.Choose: Whisper when chosen or not installed; Parakeet when chosen; Automatic: language set -> Parakeet if it's
  one of its 25 (bg hr cs da nl en et fi fr de el hu it lv lt mt pl pt ro ru sk sl es sv uk), "auto" -> DetectLanguage
  first: six 5 s bits (15%...90%; a video under 60 s: its first 30 s) put into one 30 s .wav, whisper-cli -dl with the
  small model (about 3 s; p < 0.3 = not found -> Whisper as before), and the language found goes to Whisper too.
  (whisper-cli -dl ignores -ot: it always listened to the film's first 30 s and said "en".) Whisper.Transcribe(...,
  parakeet) runs the hidden "FastViewer.exe --parakeet <wav> <add-on folder> <threads>" with Helper.Run: Parakeet.Child
  P/Invokes transcribe.dll (init_backends, open with backend CPU and n_threads, run with word timestamps; parameter structs
  filled by their own _init, fields written at their offsets), cuts the sound into ~20 s pieces at the quietest half
  second (15-25 s; sound up to 5 minutes in one go), groups words into lines like Whisper's -ml 84 -sow (pause > 0.8 s,
  84 characters, sentence end after 20) and prints "[from --> to]  text" lines: the same reading, filters, live lines
  and Tidy as Whisper; "E <reason>" when it fails. captionSteps names the engine and the language found.
  Measured (whole film "Духless", Ryzen 5 5600X, 6 threads): 1.27 large Whisper 57.5 min, 4.7% of spoken lines without
  text, 1.6% more than 3 s late, 4 missed stretches; Parakeet 2-min pieces 9.4 min, 10.1%, 1.9%, 15; 30 s 9.3 min, 5.9%,
  1.4%, 5; 20 s 10.2 min, 5.5%, 1.1%, 5 (kept; median start 0.27 s against 0.33 s). Language found in 3.2 s (ru 0.99; the Dutch Medal clip nl 0.82; that 30 s
  clip captioned in 7 s against about 69 s with the large model). Songs (9, lyrics known; raw output): small 43.7%,
  large 24.0%, Parakeet 27.5% of the words wrong; Parakeet about 5.5 s per minute of sound against 37 (large) and 15
  (small). Through the app (Tidy drops repeated choruses, so higher for every engine): large 26.6%, Parakeet 28.1%. The
  child writes UTF-8 itself (Console.SetOut): the console code page had turned Cyrillic and accents into "?". Louder sound or
  shorter pieces for songs didn't help. Tests: --case pkcaptions --addons <scratch with junctions to the real Whisper
  folders> --addon-source <the 2 downloads> [--pattern engine] (speech.mp4 in the start folder); --transcribe <wav>
  <out.srt> <language> <engine> <add-ons folder>.
- Voice activity detection (whisper.cpp --vad, Silero 5.1.2) measured and left out: on 9 film stretches (121 spoken lines)
  it left 29-42 lines without text (thresholds 0.5/0.35/0.2) against 18-25 without it, though 2-3x faster.
- Skip icons (Looks.cs Glyph.SkipBack / SkipForward): the video bar's drawing (arc, arrowhead, SkipNumber digits) as a
  Glyph code, with the current amount (Glyph.SetSkips from UpdateSkipButtons redraws them) or a fixed one after the code
  (SkipBack + "5"); used in Settings > Buttons, Keyboard (also Back/Forward 5 s), Videos and the Hidden-buttons menu items.
  The Buttons page's Rotate row has Glyph.RotateLeft, mirrored like RotateBtn. Test: --case skiprows [--look X].
- Settings > About: "Credits" card (Whisper/whisper.cpp, Parakeet CC BY 4.0 with the link and "converted", transcribe.cpp,
  Hy-MT2 Apache 2.0, llama.cpp, RIFE, FFmpeg, sherpa-onnx, Tabler Icons, Inter).
- Round 2 (user's tests): (1) Subtitle editor rows were Windows' black on every dark look: the rows are the ListBox's own
  items, so their text inherits from the list, not from SubLine's Foreground; subList now takes TextMain. The pane keeps
  at least 85% of PanelBg in a see-through Custom look (UpdateSubEditCover; a neutral dark/light behind it when the panel
  colour is as light as the text), English box 64 high. (2) Send to Discord (Share.cs): SwitchRow (menu row with the
  ToggleSwitch at its end, the whole row flips it, menu stays open, saved at once) for "Burn in captions" and "Keep
  speed" at the top; sizes "20 MB, Discord without Nitro" / "50 MB, Nitro Basic" / "500 MB, Nitro" as send rows (icon
  E724, no tick). AddKeepSpeed (Make GIF) and Save clip use the same switch. Test: --case lookcheck [settings with a
  look] (pictures editor_<look>.png, discord_<look>.png; logs the row text colour and the Burn switch).

## Hold for 2x, Even out volume, same pitch at any speed (1.29.0)
- Hold for 2x (Hold.cs): holding the left mouse button on a video or song (not photos, not while cropping, in the grid
  or Settings) for 0.3 s without moving more than 6 px plays it at 2x until the button comes up, then SetSpeed(the speed
  it had) (only if the speed is still 2x: a key or the menu during the hold wins; at 2x already nothing changes). Hooked
  into OnStageDown/Move/Up: ArmHold (holdTimer 300 ms, the stage captures the mouse so the release always arrives),
  HoldMoved (a drag cancels), HoldTick (also asks Windows: Control.MouseButtons and Cursor.Position, because in the mini
  player DragMove runs Windows' own move loop and the window gets no mouse messages; the timer still ticks there),
  HoldReleased, EndHoldAfterDrag (mini player, after DragMove returns), CancelHold (a double-click). LostMouseCapture
  and Deactivated end a hold. Clicks: a paused video still starts at the press; a playing one pauses when the button
  comes up (holdClickPauses) instead of at the press, so a hold never pauses first; a double-click still undoes the
  first click's pause/play. Zoomed in: holding still is 2x, dragging pans. Mini player: a still hold is 2x (on a
  paused video it starts playing), dragging moves the window, a click plays/pauses as before. Hint: HoldHint (Window.xaml,
  next to Toast, same FloatBg/MenuEdge/Radius.Float, "2x" + two triangles, not hit-testable, below a floating top bar
  like the Toast; hides a "feedback" note). Sound: holdWarm: while the button is down on a playing video (where letting
  go pauses anyway) OwnSound is true, so FastViewer's own sound is already playing when 2x starts (Windows' player
  stands still ~0.1 s at a speed change, its sound too). Test: --case hold [--pattern <file>[@speed]] (simulated button
  events, holdTestPressed; per file in the window and then the mini player: short click while playing / paused, a
  2.3 s hold, letting go, hold on a paused video, double-click; a photo last; every 20 ms: picture still?, sound
  there? = own sound Sounding or Windows' player unmuted while its picture moves). Measured: 60 s clip and Medal
  clips (3 and 2 tracks), from 1x and 1.5x: 2x 0.3 s after the press, back 0.2 s after letting go, sound away 0 ms
  and no sound restarts during holds and releases; the picture stands still 60-130 ms at each speed change (Windows'
  player, as with the keys). (Round 1: at 1x Windows' player got the sound back 2 s after letting go, a 30 ms dip, and
  the sound was bent to wait for the picture: both replaced in round 2, below.)
- Even out volume (Loudness.cs, Settings > Videos card under Repeat videos, settings line evenVolume=, on by default):
  Loudness.Measure decodes all of a file's heard sound (SoundMixing.Layout; the sources at their Sound volumes, "Game and
  mic", mixed by a TrackMixer exactly as SoundPlayer does; 48 kHz stereo) and measures BS.1770: K-weighting (48 kHz
  biquads), 100 ms sums, 400 ms blocks every 100 ms, gates -70 LUFS and -10 LU; plus the sample peak and the seconds
  that passed the gates. GainOf: target -18 LUFS (Medal clips are about -23 to -25, songs -6), cut at most 15 dB,
  boost at most 6 dB, falling to 0 dB between -30 and -40 LUFS (hiss, room noise stay as they are), never past a peak
  of -1 dBFS; silent / under 1 s of real sound: 1. Kept per file (path, size, date) in memory and in
  %LOCALAPPDATA%\FastViewer\loudness.txt ("lufs|peak|seconds|file <key>", 5000 lines, then it starts over; tests keep
  nothing unless --loudness <file>). Measured against ffmpeg's ebur128: within 0.05 LU (generated pink noise -9/-16/
  -28/-45, a sine, an MP3; real Medal clips mixed game+mic: -23.1/-23.9/-24.7 both ways). Time: a 30 s 190 MB Medal
  clip 170-280 ms, a 5 min MP3 about 1 s, a 12 s clip 50 ms (the first file of a run also starts Windows' media tools).
  Playing: PlayVolume(slot) = min(1, volume x LevelOf(slot)) everywhere Windows' player got Math.Min(1, volume);
  OwnSound when volume x level > 1 (so a boost over 100% plays through FastViewer's sound, as volume over 100% does);
  SoundWish.Volume = volume x level. LevelStart (where a file starts on screen) takes the known level, or 1 when it
  isn't measured yet (levelLate): then the file on screen is measured first and LevelArrived glides (levelTimer, 3 dB
  a second, steps of 0.15 dB) to its level, and not over 100% unless FastViewer's sound is playing already (no change
  of players mid-sound). Background: one thread "Loudness" in Windows' background mode (THREAD_MODE_BACKGROUND_BEGIN:
  lowest processor, disk and memory priority), started only once the first picture is up; queue = every loaded
  (preloaded) video/song (LevelWanted from Load); the file on screen first once its picture is up (levelUp, set by
  StopRevealWatch and UpdateOwnSound), the others 0.6 s after the last switch; files with 2+ tracks wait until their
  layout is known; a neighbour being measured gives way to the file on screen. Turning the setting on/off takes effect
  at once. Send to Discord: ConvertOptions.Level = ExportLevel(file) (the level it plays at, if measured) multiplies the
  mix gains (a one-track file then goes through the mixer too); the "already small enough, copied as it is" shortcut
  stays (no level then). Checked: copies with -9.2 / -1.9 / +6.0 dB measure -18.2 / -18.2 / -22.3 LUFS (expected
  -18.0 / -18.0 / -22.1). Tests: --case loudness [--pattern save] (every file measured, again from memory, from the
  file; save: copies with the level), --case even (with --settings: what each file plays at, a late file's glide,
  volume 50%, the setting off and on).
- Same pitch at any speed (round 2, Stretch.cs TimeStretch; setting "Change pitch with speed" in Settings > Videos, off,
  settings line speedPitch=1 for the old way; TimeStretch.KeepPitch): WSOLA like Chrome's audio renderer: 20 ms periodic
  Hann windows (w[i] + w[i+480] = 1), 10 ms steps, each window taken near speed x 10 ms further on in the input, at the
  place within +-15 ms that best continues the last window (normalized cross-correlation of L+R: coarse every 6th place
  on every 4th sample, then +-6 places on every 2nd sample; silence: no search). At exactly 1x it hands the input
  through untouched (b = the natural next place). Time on the picture's clock is counted at the even pace (ana), not
  at the window found, and input pushed with its seconds (a sliver played twice = 0 s): PendingSeconds = what is
  inside the stretcher. Player (SoundPlayer.Step): stretchMode follows the setting (a change restarts the sound);
  heard = mediaEnd - Waiting(true) - PendingSeconds, where Waiting(true) now also counts the ~50 ms inside Windows
  and the card through the handed list (at the speed it was made at; before it was latency x the new speed: 50 ms off
  at each change). At 1x with a pace correction of at most 0.5% the stretcher passes through and the old Render
  (straight lines between samples, 1.28 behaviour, slivers at 25 ms) bends it; any real speed or a bigger correction
  goes through WSOLA. Keep-speed saves: SoundStretch uses a TimeStretch (plain clamp, no soft limiter) and Finish
  writes the last half window. Tests (--case pitch, folder with tone.mp4 = a 1 kHz tone, r1.mp4 = a Medal clip;
  SoundPlayer.Tap records the sound before the volume): same pitch at 2/1.5/1.25/0.75/0.5/3x: 999.8-1000.3 Hz,
  96-100% of the energy one clean sine (least-squares fit); the old way: 500/750/1250/1500/2000 Hz as expected
  (67-100% clean, its 3x was always rough); saved 8 s at 2x / 0.5x: 1000.0 Hz, 100% clean, 4.017 / 16.067 s; the old
  way 2000 / 500 Hz.
- Letting go of a hold (round 2). Cause of the "slowed-down sound": for 2.5 s after any speed change the player read
  the sound up to 20% slower or faster to meet Windows' player, which stands still ~0.12 s at every speed change and
  then goes on from where it stood; with plain resampling the pitch dropped too. Now (TimeSpeedChange in the window,
  ApplySpeedNow -> SetPictureSpeed): the sound is never bent in the first 0.9 s after a change; the two changes are
  timed against each other instead. Going slower (b < a): the sound gets b at once (SoundPace, SoundWish.Speed; the
  picture's own pace goes in SoundWish.PicturePace for Now()), and the picture gets it after OutputDelay (what is
  already queued plays at the old speed: speaker queue + 50 ms, reported by the player) + b x T / (a - b). Going faster:
  the picture at once, the sound after b x T / (b - a) - OutputDelay. T = the time Windows' player loses at a change,
  learnt per file and direction (WatchStall: 0.8 s after a change, elapsed - moved / speed; default 0.1 s; measured
  0.12 s on the user's Medal clip and on small clips). What is left after 0.9 s (a few hundredths: T varies) is closed
  at the same pitch at most 3% faster or slower (closeGap), within a change only above 12 ms, at other speeds
  outside a change above 25 ms; slivers only at 1x (25 ms, as before) or beyond 0.25 s. After a speed change own
  sound stays for that file until it is paused or another file shows (soundSpeedFile + playing): no hand-back to
  Windows' player mid-sound (was a ~30 ms dip 2 s after letting go). Fallback CatchUpPicture: only when the sound is
  over 0.15 s ahead, the picture is moved forward by the gap plus what a move costs that file (learnt; Medal 1080p60:
  0.4-0.6 s of standing still, so it is a last resort; at most 3 per change, never with Repeat).
  Measured (--case pitch, letting go of a 2.5 s hold at 1x; 2 x tone.mp4, 3 x Medal r1.mp4): sound read at exactly
  1.000x from 0.15 to 0.9 s after letting go, then 0.988-1.013x; sound and picture within 40 ms from 0.35-0.44 s on
  (before that the picture runs ahead up to ~0.15 s by design while it waits to stand still), then within +-15 ms;
  no picture moves, no missing sound; during the 2x hold within -44..+20 ms (was 160-300 ms). --case hold: sound away
  0 ms through holds and 4 s after letting go. Loops (--case loopsound r1.mp4 at 120%): as 1.28 (worst 24-31 ms per
  loop, a sliver brings it to 0). Slider drag (--case speeddrag): no gaps, no restarts during the drag.
- 1.29.0 round 3 (the user's real-screen test). (1) Mini player: letting go of a hold paused the video, because the press went
  straight into DragMove (Windows' move loop) and the click was decided after it returned. Now (FastViewer.cs OnStageDown
  mini branch, Hold.cs MiniDrag): a press in the mini player is armed like in the window (capture, hold timer, holdWarm
  when playing); the window move starts only once the cursor moved 4 px (MiniDrag: release capture, keep own sound via
  soundSpeedFile, DragMove); HoldReleased: a hold always ends in EndHold (plays on at the speed it had), only a short
  click (miniClick) plays/pauses when the button comes up. --case hold now also goes through this path in the mini pass.
  (2) Repeat with own sound: measured with --case loops (Hunt129.cs; folder of noise clips n2 = 2 s, n7 = 7 s, m6 = 6 s
  with 3 tracks mixed like Medal; each at 1x after a change, 1.5x, 2x, same pitch and pitch with speed, 5-8 loops; per
  loop: silence in what the speakers got, speakers running dry (SoundPlayer.LongestStarve), doubled sound = a 20 ms
  block of the changes between samples matching anything 30-300 ms before it at over 0.9, sound/picture apart 0.15-1.2 s
  after the picture started over; the comparing runs off the window's thread). Before: 105 of 105 loops bad: speakers dry
  120-1200 ms at each loop (the sound waited for Windows' player to say "ended" and start over), 100-200 ms played twice
  (re-sync within 1.2 s after a loop, 2 per loop), 150-340 ms apart. Causes and changes: SoundPlayer.LoopWaits = false:
  the sound never waits at the loop (no held piece, pieces may cross the wrap); PlanLoop / RestartPicture (Stretch.cs):
  the picture is started over (Position = 0, NoteLoop) just before its end, early by its learnt restart cost per
  file+speed (LoopCost, 0.8 s later: elapsed - position / speed; measured 0-10 ms), at the picture clock dur + drift -
  cost x speed (at most dur - 0.03 x speed, so Windows' player never reaches its end; OnVideoEnded stays the fallback),
  only while own sound plays; tracks start up to 141 ms after the picture (AAC, edit lists: n2 112 ms, Medal r1 game
  track 141 ms) and run that far past its end: the part past the end is kept (Stash, up to 0.3 s) and placed into the
  next loop's opening hole, cut exactly where that track's new sound starts (TrackMixer.Add exact / cutAt / fadeInFrom,
  2.5 ms fades), no sample moved (before: dropped, and the next loop's sound slid up to 50 ms earlier when the hole was
  small); at the seam the sound skips forward by how far it is behind (drift + SeamExtra, the after-loop offset learnt per
  file+speed 0.9 s after each restart, not within 3 s of a speed change); "looping" protection only 0.2 s after a restart
  and the last 0.05 x speed s. Sound corrections everywhere: no slivers any more (repeating 30 ms was heard as doubled):
  closeGap from 15 ms (12 ms within a change) at most 3% (same pitch) or 1% (pitch with speed, the pitch moves with it);
  slivers only beyond 0.25 s; a restart from the picture's place only when the sound is over 0.5 s ahead (it replays
  what was heard) or 0.25 s behind. After: silence 0-4 ms (the joint fade), no doubled sound (closest match 0.2-0.3 on
  noise, 0.3-0.66 on game sound), speakers never dry, no restarts, no slivers; Medal r1 (3 tracks, 30 s) at 1x/1.5x/2x
  both pitch modes: 18 of 18 loops within 29 ms; noise clips: steady loops mostly within +-40 ms; the first loop right
  after a speed change can be 70-280 ms off and settles over the next one or two loops; on a 2 s clip at 2x (a loop every
  second) up to ~60 ms. --case loopsound r1 (1x, 120%): worst 14-24 ms per loop, no slivers (1.28: 24-32 ms + slivers).
  Tests found on the way: hunt default pattern is "mixed" (not null); a test that blocks the window's thread for seconds
  makes the sound think the picture stopped (it restarted ~130 ms back = heard as doubled): never in normal use.
- 1.29.0 round 4 (replaces parts of round 3 above; this is how loops work now). The user: loops cut off the end of the sound; the
  first loop after a speed change wasn't in step; after quick holds the video briefly ran slower than 1x.
  (1) The sound is the master. SoundPlayer.ProbeSpans (when a reader opens): each heard track's first and last sample
  (stamp + edit-list offset; reading from 0 and from 1.5 s before the end) -> SoundFrom / SoundTo (doubles), LoopSpan =
  SoundTo - SoundFrom. Every track is read to its own end (no cut at the picture's end, no tails, no seam skips);
  wrap when all ended; wrapBase += LoopSpan; each track's first sample of a new loop placed exactly (TrackMixer.Add
  exact, no sliding), so the next loop's sound starts right after the last sample. The window: LoopLenFor(s) = LoopSpan
  when known (SoundWish.LoopLength, NoteLoop adds it to soundLoopBase); a restart mid-loop finds its place from
  w.LoopBase. PlanLoop: the picture is started over when the sound reaches the next loop (heard = position - drift,
  wall time = (len - heard) / sound pace) minus the learnt restart cost (per file + speed + pitch mode, learnt from how
  far apart they are 0.9 s after each start-over, not within 3 s of a speed change). The picture never reaches its own
  end: starting Windows' player over in the moment it finishes the file jammed its media pipeline (found: the next
  MFCreateSourceReaderFromURL in the process hung for good, own sound stopped); when the sound is longer than the
  picture it is paused 0.08 x speed s before its end (pictureHeld) and started over from there (Position 0, Play).
  OnVideoEnded with a planned loop: an "ended" within 1 s after a start-over only means Play; otherwise the picture
  waits and a fallback starts it over the old way. Measured (--case loops, which now also counts per loop the frames
  of each heard track put in, TrackMixer.AddedFrames, against the file's own count, plus frames ever left out,
  SoundPlayer.DroppedFrames; at 1x one-track it also matches what the speakers got against the file in 20 ms blocks:
  the last 500 ms and the first 100 ms of the next loop right after it): every whole loop got every sample of every
  track, 0 frames left out; next loop's start right after the end matched 1.000; no silence, no doubled sound.
  (2) First loop after a change: --case loopchange (1x->2x 0.8 s before the end, 2x->1x 0.5 s, 1x->1.5x 0.3 s, a hold
  pressed 1.1 s before, let go 0.4 s before; 3 times each). Tried and dropped: predicting the sound's pace through the
  change (worse), saving the change for the loop start with the picture's speed given at its start-over (much worse:
  Windows' player stands still for an unknown time then); both reverted (SoundWish.PaceAt stays unused).
  (3) Quick holds: --case holdstorm (12 holds of 0.35-0.9 s, 0.15-0.6 s apart, window + mini player): the sound is
  never read faster or slower than it should (0.3-0.4% at most once a change has settled; the 3% closeGap of round 3
  is gone: closeGap = false, only the 0.4% nudge). The picture still stands still 0.1-0.16 s at each of its speed
  changes (for a let-go about 0.27 s after it, as timed): Windows' player's own, caused by its sound pipeline (a
  video-only file shows none: picture 100% of its speed throughout); pausing around the change doesn't help.
  Final state of round 4 (after the measurements): (a) Encoder padding: an AAC track decodes to priming + sound +
  padding (n7: 21 + 7000 + 19 ms); Windows' decoder hands all of it out. VideoInfo.EditDuration (sum of the edit
  list's segments, mvhd scale) gives the real length: each track's span ends at its first sample + EditStart +
  EditDuration, the rest is cut exactly (TrackMixer.Add cutAt, no fade): every sample of the real sound, no padding
  silence at the seam (was 7-20 ms). (b) The closing of what is left after a change: at most 1.5% at the same pitch
  (0.5% the old way), only within 4 s of a speed change and only above 20 ms; otherwise the 0.4% nudge (round 3's 3%
  was gone for a moment and made let-go residuals stay 70-90 ms). (c) TimeSpeedChange learns a per-file, per-direction
  bias of its delay (WatchChange, 0.9 s after a change: delay -= drift / speed difference, slowly). Measured on the
  final build: --case loops (3 clips x 3 speeds x 2 modes, 106 loops): every whole loop got every sample of the real
  sound of every track, 0 frames left out, silence at the seam 0-2 ms, no doubled sound, no restarts; 89 of 106 loops
  within 40 ms, the rest 41-52 ms (the restart moment of Windows' player varies). Medal r1 18 loops: all samples
  (game 30016 ms, mic 30123 ms), mostly 7-25 ms. --case loopchange: holds pressed 1.1 s / let go 0.4 s before the
  end 14-30 ms (one 50), 2x->1x 0.5 s before 17-44 ms, 1x->2x 0.8 s before 13-55 ms, 1x->1.5x only 0.3 s before 15-121
  ms (Windows' stand-still at its speed change and its start-over overlap). --case pitch let-go: exactly 1.000x from
  0.15 to 0.9 s, within 40 ms from ~0.4 s in 3 of 5, the other 2 close at 1.5% over a few seconds.
  Tests: hunt logs are also written as they go (log.partial.txt) and a watchdog writes the window thread's stack when
  it stops answering for 2 s, the sound thread's when it doesn't go round for 3 s, and a test analysis's.
- Test silence (round 2, TestSilence.cs): every "--" run sets this process's own Windows volume to 0 and mute (its
  audio sessions on every active render device, IAudioSessionManager2; the default session first, so it is already
  0 when anything plays), checked again every 0.5 s from 3 s on; RunHunt waits for it (5 s, else the test stops) and
  every hunt log ends with "test silence: n sound session(s) ... all at volume 0 and muted: True". Done off the
  window's thread: on it, it held up the first video's opening by 0.6-0.8 s. Windows remembers a program's volume by
  its path: test copies keep 0; the FastViewer.exe registered for "Open with" gets volume 1 back at the end of a test.
  The same 1.28 build opens ~80 ms slower from a path Windows remembers as muted (563 vs 644 ms median), so first-
  open comparisons of test runs carry that; switching (median 11-12 ms, p90 16-17 ms) is the same old and new.
- Not verified: how even it sounds by ear on the user's speakers (WPF's volume is taken as linear, as FastViewer
  always has); real mouse holds (tests simulate the button).

## Smooth loops and speed changes: FastViewer draws the video (1.30.0)
- FramePath.cs. Setting "Smooth loops and speed changes" (Settings > Videos, after "Change pitch with speed"; settings
  line ownFrames=, EXPERIMENTAL and OFF by default since 1.30.0 round 4 (card title "... (experimental)"; a proper one-player
  rebuild is planned for 1.31: it still swaps players on pause/play (grey haze), short loops feel late, memory high); tests: --frames on|off). Windows' player (MediaElement) stands still ~0.1-0.18 s at
  every start-over and speed change. Now, while the video on screen plays, FastViewer decodes the pictures itself and
  draws them over Windows' player (which keeps playing underneath, unseen, for everything else: seek bar, captions,
  frame steps, trim, crop, grabbing a frame, pausing).
- Decoding (VideoConverter.FrameReader, thread "Frames"): Media Foundation source reader with a D3D11 device
  (VideoConverter.Gpu: D3D11CreateDevice with video support, ID3D10Multithread protected, MFCreateDXGIDeviceManager,
  ResetDevice) + MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING: decoded and converted to RGB32 on the graphics
  card, copied out with IMF2DBuffer.Lock2D into a pool of 10 frames (unmanaged memory). Pictures bigger than the
  screen are made screen-sized on the card (zooming in past 100% on such a file shows Windows' player). It reads
  ahead of the clock (at most 0.35 s or 0.5 x speed, 9 frames); at high speeds frames closer than a screen frame
  are decoded but not copied. A file whose picture comes out turned, or that fails, goes back to Windows' player
  (frameBad, logged "own pictures off").
- The clock is FastViewer's own sound (SoundPlayer.Clock: what is being heard right now on the picture's counted
  clock, published every round of the sound thread; plus the loop length the sound wraps at). Every screen frame
  (CompositionTarget.Rendering, OnFrameTick, hooked only while needed) the newest picture at or before the clock
  (+12 ms, smoothed) is copied into a WriteableBitmap (Image "frameView", Stretch Fill, bound to the slot's
  MediaElement ActualWidth/ActualHeight, right above it in the Rotor: turned, zoomed and panned with it). It shows
  once a picture for the moment is there and stays while the sound plays; when the sound thread is busy for a moment
  (seen at a seam) the clock carries on from its last reckoning for up to 0.4 s. So sound and picture can't drift.
  OwnSound = OwnSoundBase || FramesWantSound: whenever the pictures are drawn, FastViewer's sound plays (files with
  sound only; StartPlayback uses OwnSoundBase so Windows' player sounds at the start and is silenced once FastViewer's
  sound is really heard: SoundPlayer.HeardFor, no gap).
- Repeat: frames are read straight on into the next loop at the sound's loop length (SoundPlayer.LoopSpan). A fresh
  reader is opened beside, shortly before the end, and takes over at the seam (jumping the old one back to 0 lost the
  first frames of some files). Files made by x264/ffmpeg with B-frames carry an edit list that Windows' MP4 reader
  ignores for the picture (its times run 2 frames late and it never hands out the first 2-4 frames): the reader
  measures this on the first frame (VideoInfo.EditStart "vide") and corrects the times (FrameReader.Shift); the first
  frame it gets of a new loop is shown from the loop's start. A jump Windows refuses (0xC00D36E5 on a 2 s clip, the
  sound's loop a moment longer than the picture) opens the file again; jumps stay inside the picture's length.
- Pausing: the picture on screen stays 0.35 s while Windows' player is put on that same frame underneath. The reader
  is kept 8 s while paused (playing on needs no new one), then its memory goes.
- Measured (--case frames, hidden test window, simulated holds; stand-still = how long the picture stays the same
  beyond a normal frame; own pictures: how much older the shown frame is than the sound's clock): Medal clip (1080p
  120 fps, 2 tracks): loop seams 0-12 ms (Windows' player 60-182 ms at 1.5-2x), speed changes and holds 0-36 ms
  (111-139 ms); ffmpeg clips 60 fps: seams 2-32 ms (36-99 ms), 30 fps 2 s clip: 8-65 ms (the frames Windows never
  hands out); seen vs heard 95% within 18-39 ms (Windows' player: up to 220-430 ms around changes). Every sound sample
  still played at every loop (--case loops: 18 of 18). Switching unchanged (--selftest 400: median 8-11 ms, p90
  16-33 ms both ways); the reader starts only once a video plays on screen, off the window's thread. Costs in round 1: processor 23-60% of one core, the graphics card decoding twice, about 450 MB more memory: all cut in round 2 (below).
- Not verified: how it looks on a real 144 Hz screen (the hidden test window gets irregular screen frames), real
  mouse holds, 4K and HDR files, Glass look over a real desktop.

## 1.30.0 round 2 (the user's real-screen test: crackling sound, too much memory/GPU, a grey haze)
- Crackling (SoundPlay.cs, in every version with FastViewer's own sound, so also 1.28/1.29): Render (the plain reading
  at a pace, used at 1x with the 0.4% corrections in both pitch modes and at every speed with "Change pitch with
  speed") started every ~60 ms piece fresh at its first sample and dropped the part of a sample left over: at any pace
  but exactly 1.000 the sound jumped a little at every piece. Now the reading place is carried over (renderAt, and the
  last sample of the piece before is kept to read between the two; ResetRender at every real break: a restart, a stop,
  a pitch-mode change, after the time-stretcher took over). Found with --case clicks (Hunt130.cs): tone clips whose
  every track is a 440 Hz sine (the mix stays one clean sine, so x[n] = 2cos(w)x[n-1] - x[n-2]; a jump over 400 is a
  click), plus clipping counts and how close the speakers came to running dry (SoundPlayer.MinQueuedMs, LongestStepGap,
  Starves; they never ran dry: always 81-94 ms queued, the sound thread paused at most 19 ms). Before: 27 clicks in 6 s
  at 1x same pitch, 18 at 1x pitch with speed, 113 at 2x pitch with speed on a -2.5 dBFS tone (same with the frame
  path on or off); after: none away from loop seams.
- Limiter (PeakLimiter, SoundPlay.cs): the sound is made at 1/2.5 of its level (Render and the stretcher get the volume
  / PeakLimiter.Headroom) and Hand brings it back up through a 2 ms look-ahead limiter: the lowest gain any sample in
  the next 2 ms needs (ceiling 32000), averaged over the same 2 ms (no step in the gain), release ~80 ms. Before, each
  sample over -2 dBFS was squeezed on its own (TrackMixer.Limit's curve): at 200% or with an Even out volume boost every
  loud peak was bent. The heard clock counts the 95 frames of look-ahead (latency + 95/48000). Tests record after it.
- Loop seams: the first sound of each track in a new loop fades in over 2.5 ms and the old loop's end fades out over the
  2.5 ms before its cut (TrackMixer.Add fadeInFrom / cutFade): every sample still plays, but a seam where the end and the
  start don't meet is no longer a step.
- Memory and graphics card (FramePath.cs): (1) Windows' player is "parked" (paused) under the drawn pictures once they
  have been on screen steadily for 0.6 s (not while scrubbing, trimming, revealing, a press that may become a hold, or in
  the last second without Repeat): it no longer decodes every frame unseen. While parked: the sound is its own clock
  (TellParkedSound: the window tells it its own place, so it never chases a picture; speed changes reach it at once),
  the watchdog (WatchPicture) leaves it alone, and VideoAt(slot) (the drawn picture's place, else Windows' player's) is
  read by the time bar, skip keys, frame steps, A-B loop, trim, captions, subtitle editor, translation, resume places,
  rotation/crop/cover reopening and Save frame. Unparked (Unpark): when someone moves it (a skip, the time bar, a frame
  step: its place changes by more than 4 ms after a 0.4 s settle), on pause (it is put on the shown frame), when the
  drawn picture is gone for 0.3 s, when another file shows or the setting goes off (put on the shown frame and played),
  and at the end without Repeat (put on the last shown frame, then OnVideoEnded as if Windows' player had ended).
  (2) The pictures stay on the graphics card (GpuShow): the reader copies each decoded picture (MF's D3D11 texture,
  IMFDXGIBuffer) into a texture of its own pool on the card (6 frames, was 10; textures, not memory); to show one it is
  copied into a D3D11 texture shared with a Direct3D 9Ex surface that WPF draws (D3DImage, SetBackBuffer with software
  fallback), then Flush + an event query so WPF never reads it half done. Copy frame / Save frame read that texture back
  once (GpuShow.Read). If the card path can't be made (no D3D9Ex, another format than BGRA/BGRX, a lost device), the
  pictures go through memory as in round 1 (one staging texture of our own, not Windows' per-picture copies;
  --frames-cpu-show forces that). (3) No second reader per loop (round 1 opened one beside before each loop end; the
  time Shift is what fixed those seams). (4) Native call wrappers are made once and kept (Gpu.Call). Measured with
  --case usage (one video at 1x with Repeat, 8 s after 3 s; Task Manager's memory = the process's private working
  set): Medal r1 / generated n7: 1.29 mode 235 / 209 MB, CPU 28 / 20% of one core, video decode 13.1 / 9.2%, 3D 2.0 /
  1.8%; round 1: 423 / 379 MB, 82 / 47%, decode 2x; round 2: 358 / 306 MB, 34 / 26%, decode 15.4 / 8.9%, 3D 3.5 / 2.1%.
  Loops after it (--case frameloops): Medal r1 0-4 ms, n7 1-26 ms, the 2 s 30 fps clip 8-64 ms (its missing frames);
  every picture through the card (log: "pictures stayed on the graphics card").
- Grey haze when clicking play/pause quickly (double-click off): every "play" took a snapshot of Windows' player for the
  cover (Poster) and kept it up until the video was seen moving, which never happened while clicking; with the frame
  path Windows' player was paused/parked or just being put on the shown frame. StartPlayback now takes no snapshot (and
  clears an old one) while the drawn picture is up, while parked, or within 1.5 s of putting Windows' player on the
  drawn frame (frameMovedPlayerAt). --case clickspam (clicks every 70-160 ms for 4 s): the cover was up in every check
  before, 2 of ~39 after.
- Also checked after round 2: switching --selftest 400, 3 rounds each: median 8-9 ms, p90 13-14 ms with the frame path
  (8-10 / 23-34 ms without); --case hold: the picture never stands still during a hold, 0-42 ms after letting go (1.29
  mode 97-109 ms). Test silence: Windows' player gets volume 0 in tests (PlayVolume), FastViewer's own sound goes to a
  pretend speaker (SpeakerOut.Pretend, same timing); tests that record now get the real volume (200% too), silenced
  after recording. Not verified: the grey haze on the real screen (the hidden test window can't show it), real mouse
  clicks, Glass look over a real desktop, 4K/HDR files, a device lost while drawing (screen lock).

## 1.30.0 round 3 (the user's second real-screen test: washed-out colours, a jam, choppy 120 fps, MP3 Repeat)
- Colours (FramePath.cs): the reader asked for RGB32 (BGRX), whose 4th byte comes out 0; shared with Direct3D 9 as
  X8R8G8B8, WPF drew it partly see-through on a real screen (washed out, greyish, no deep blacks; a volume change or a
  click showed Windows' player's picture for a moment, in the right colours). Now the reader asks for ARGB32 (falls back
  to RGB32): the 4th byte is 255, the shared texture is B8G8R8A8 / A8R8G8B8. --case colours (Hunt130.cs): pauses a
  playing video, keeps the drawn frame and Windows' player's picture of the same frame, and compares (per-channel fit,
  1/50/99% brightness, the 4th byte's average: 0 before, 255 after; pictures of both and of the window). Against
  ffmpeg's decode of the same frame (BT.709, limited range expanded to full): the drawn frame 45.3 dB PSNR (the same),
  Windows' player's picture as grabbed (RenderTargetBitmap) 22.5 dB: the grab comes out in limited range
  (drawn = 1.16 x grab - 18), so it isn't what a real screen shows and isn't the reference.
- The jam: SeekBy clamped a skip past the end to the exact end; skipping forward again and again there put Windows'
  player at its end while it was ending and starting over, which hung Windows' media pipeline for the whole process
  (no file opened any more, MFCreateSourceReaderFromURL never returned; only ending the process helped). --case skipjam
  (Repeat on, skip to the last second, "skip forward" every 60-160 ms for 3 s, then: does it play on, does a new reader
  open within 4 s): 12 of 12 rounds jammed with the frame path off, 3 of 12 with it on. Now: with Repeat a skip past
  the end goes on into the next loop (modulo the length), without it stops min(0.25 s, a quarter) before the end, and
  a forward skip on a player whose length isn't known does nothing. After: 0 of 12 both ways.
- Self-repair (Jam.cs): once a second while a video shows, if it should play but its place (VideoAt) hasn't moved for
  3.5 s, or it hasn't opened 6 s after being asked, a test reader of the file is opened off the window's thread. It
  opens: the slot gets a new Windows player (NewVideoPlayer), the file afresh at the same place, playing on
  (RebuildPlayer). It doesn't open in 4 s (or a player was made again twice within a minute): the pipeline is stuck for
  the whole program; FastViewer saves places and settings, starts itself again with "<file> --after-jam <pid>
  --resume-at <s> --resume-playing <0/1>" (Program.AfterJam waits for the old one to end before the single-instance
  check; ResumeAfterJam goes on at the same place) and ends (Environment.Exit, Kill after 2 s). Both go to errors.txt.
  Tests count instead of restarting. --case jamrepair (a player kept paused behind FastViewer's back): noticed after
  5.3 s and made again, plays on; with --jam-probe-fail: noticed after 11.3 s, restart asked; both in errors.txt.
  With the frame path drawing, a stuck hidden player doesn't stop what is seen, so nothing is done.
- Playback stats (Stats.cs): Ctrl+Shift+P (shortcut "stats", Settings > Keyboard) shows top left (in the window's video row,
  Grid row 1: in row 0, the 48 px top bar, it was cut off until 1.30.0 round 4; --case fps logs its size and place), once a second: who
  draws (FastViewer on the graphics card / through memory / Windows' player), the video's frame rate (measured from
  the pictures' own times, the median gap of the last 60: FrameReader.MeasuredFps), screen frames per second
  (CompositionTarget.Rendering, counted only while the stats are on), pictures shown per second of those possible,
  skipped (passed over unseen: FrameReader.Take reports how many), late (shown more than 1.5 pictures after their
  moment), surface busy, the longest drawing and the longest wait for the surface. While on, each second goes to
  %LOCALAPPDATA%\FastViewer\playback.txt with the file, place and where pictures were skipped or late (1 MB, then anew).
  Windows' player's pictures can't be counted (it doesn't tell); its screen frames per second are still shown.
- 120 fps: showing a picture locked the D3DImage, which waits for WPF's render thread to let go of the surface (up to
  4 ms a frame), holding up the window's thread and so the next screen frames. Now TryLock (1 ms); busy: the picture goes
  back to the front of the queue (FrameReader.PutBack) and is shown next screen frame (a timed-out TryLock must still be
  Unlocked: without that the surface stayed locked for good and nothing was drawn). --case fps (each clip 7 s at 1x with
  the stats on; the user's two 120 fps clips: a 576x720 phone clip and a 1280x720 Medal clip): 111-118 of 120 pictures
  a second, 1-6 skipped, none late, in a hidden test window whose own screen frames wobble between 147 and 155 a second
  (before: 101-116, 4-19 skipped). The decoder always had the picture ready (late 0). Not verified: the user's real
  144 Hz screen; the stats are there to show it.
- Music on Repeat (MP3 at 2x / 200%): PlanLoop started Windows' player over "at end", which left the MP3 failed for good
  (s.Failed: from then on Windows' player played it, no volume over 100%). Now a song whose sound FastViewer plays itself
  parks Windows' player too (CheckAudioPark at the end of TellSound; TellParkedSound un-parks a song on pause, a skip,
  another file, the end without Repeat, or 0.5 s without the sound's clock; VideoAt gives the sound's place). --case
  clicks on an MP3 tone and an NCS song: every run at 200% and 2x is FastViewer's sound, none failed (before: from the
  third run on "Windows' player"). The MP3's own seam keeps a step (the encoder's padding, which Windows doesn't trim).
- Also: a build that silently failed while a test ran from the same folder, and two shell edits that went wrong (perl
  with "|" as delimiter and \|\| in the pattern inserts at the file's start; GNU sed's \| is "or" and matched the empty
  string, which wrecked FramePath.cs: restored from Claude Code's file history). Lesson kept in the project notes.

## Settings fixes (1.30.0)
- Narrow windows: AddCard's control (switch, menu, row of buttons) moves under the card's text when the text would
  keep under ~150 px beside it (FitCardControl: the control's natural width measured once, decided again only when
  the card's width changes, after layout); the Buttons presets are a WrapPanel. Every page checked at 480, 600 and
  760 px wide (--case narrowsettings, pictures of every page).
- Settings > Viewing > "Double-click for full screen" (on): off sets the mouse action "double" to Nothing (the same
  choice as Settings > Mouse > Double-click the picture). Settings pages refresh their controls when shown.
- Tests: Windows' player gets volume 0 in every test run (PlayVolume), FastViewer's own sound goes to a pretend
  speaker (SpeakerOut.Pretend: same timing, no sound device opened), on top of the test process's Windows volume 0.
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
  autoplay, repeat, even out volume (1.29.0), change pitch with speed (1.29.0), when a video ends, dim video, hide controls on paused video, hide controls after,
  skip back/forward, slideshow speed), Keyboard (every action, two keys each, Reset all), Mouse,
  Speed (stay ready in the background, Preload, "Start even faster" = NGen with admin approval),
  Default app (add to "Open with", open Windows Default apps), Updates (automatic check on/off, Check now),
  About (version, where the settings file is, Copy error report, screen rate).
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
- 1.30.0: --case clicks, --case usage, --case clickspam, --case frameloops (round 2), --case colours, --case skipjam, --case jamrepair [--jam-probe-fail], --case fps (round 3), --frames-cpu-show (pictures through memory);
- 1.30.0: --frames on|off (FastViewer draws the video, FramePath.cs; overrides the setting), --frames-cpu (its decoding without the graphics card), --case frames (loops, speed changes, holds, skip-and-hold: stand-stills, seen vs heard, CPU/GPU/memory; run once with --frames on and once off), --case skiphold (only the skip-and-hold part: the skip keys and the time bar; listens to both players for heard sound going back), --case narrowsettings (every Settings page at 480/600/760 px, pictures). Every test: Windows' player at volume 0, FastViewer's sound to a pretend speaker (no sound device).
- --selftest <ms> (switching speed log), --features, --ui, --gallery, --controls, --tweaks, --memory,
  --stall (freeze detector), --volume, --looks, --dim, --sync, --toast, --fadetest, --snap <dir>.
- --hunt <dir> --case <name>: torture (thousands of fast switches with checks), edge, churn, big, dump,
  keys, dialog, rotate, loop, sizes, ghost, positions, saveclose, trim (drags, keys, play selection, saving), rates (every animation simulated at 60/144/240/500 Hz), toggles (menu buttons close their menu, keys undo themselves), scrub (fast time bar drags, the dot's smoothness), custom (the custom look end to end, with --settings), showcase (pictures of an example custom look), wheel (very fast switching through videos with the controls showing: delay per press, frame times, frame grabs), minivol (mini player volume number), tips (the tips switch, with --settings), miniclear (folder with a see-through PNG, a photo and a video: the mini player measured over white and black is fully clear around the PNG, normal for the rest, and no Windows frame in mini/full screen), preloadghost (every look: the window drawn with and without the hidden preloaded players must be pixel-identical, on a see-through PNG, photos and paused videos, in the window and the mini player), squishall (presses all ~220 buttons in the window and every Settings page: each must shrink and end exactly where it started; --look Glass --motion full), skipicons (saves the skip buttons at 4x per look), rotsave (1.20.0, scratch folder with plain.jpg 400x200 red square top-left, photo.png, photo.bmp; makes tag1.jpg, tag6.jpg and a 4000x3000 sideways phone.jpg with an embedded preview itself: rotates each with saving on, checks the file decoded fresh, orientation tag, date, Windows' thumbnail, Explorer's cache at 96 and 256 (Thumbs.FromCache; plain.jpg and photo.png skip the rebuild for comparison) and the reopened window), showmigrate (old Show lines -> new choice, what each choice lets through), taskbar, vturn (1.21.0, run twice with --rotations: video turns remembered and saved into MP4s at 90/180/270, Explorer thumbnail, date, playing position, leftovers), gridsearch (1.21.0), menus (1.21.0, also icons_<look>.png of the menu icons), switches (1.21.0), cover (1.20.0, scratch copies only: changes the cover of an ID3 2.3 song with a cover, a 2.4 song without, an untagged MP3 and a locked one; logs the cover read back by its own ID3 reader and by Windows, tags, playing position, leftover temp files, the music menu and Show music files; check the sound with ffmpeg -map 0:a -c copy -f md5 before/after), music (1.20.0: music shows at once, plays, tags/cover, 0.25x/5x on music and video, Copy frame logged, speed stepping, Speeds buttons saved; folder with song.mp3 (tagged, cover), plain.wav, clip.mp4, photo.jpg; use --settings), squish (Liquid Glass button squish frame by frame for taps of 0-278 ms, plus each Custom motion style's timing and curve; use --look Glass --motion full), share, crop, tracks (1.22.0, see Videos),
  away (1.23.0, see Polish release), convert, subs, addons, captions, smooth (1.24.0, see their section), burn, videomenu, captionsall (1.25.0),
  sound, soundfix, ownsound, trimsound, keepspeed, speedstorm, speeddrag, loopsound (1.26.0, see Sound),
  settingsplay, welcome, deletenext, skipnumbers, subedit, subeditfit, customcard, safetynet (--errors <file>), settingsclear, handover (1.27.0),
  overlap, audit (--pattern
  full|sizes|scales|contrast: renders every screen in all four looks and checks overlaps, clipped text, contrast,
  screen-reader names, Tab order), ux, updatemsg, motion (every animation's frame times and idle CPU).
- Options: --preload n, --look <Id>, --strip, --grab-always, --settings <scratch.ini>,
  --positions <scratch file>, --rotations <scratch file>, --real-bin (crop tests use the real Recycle Bin), --addons, --addon-source, --vocals, --gpu, --frameboost, --speechwav <video> <wav>, --own-sound-off (sound as before 1.26), --own-sound-aloud, --recent <scratch recent.txt>, --default yes|no, --tidy <in.srt> <out.srt> (1.27.0), --tracks <scratch file>, --update-feed <local json or "refused">, --update-page <file>, --update-test,
  --count, --every, --pattern, --front, --motion full|reduced|off (tests run with animations Off unless
  given), --convert <src> <dst> <rotation> [from to].
- Any run with a "--" option never contacts GitHub.
