// FastViewer 1.31.0: FastViewer's own player, for every video and song. Windows' player (WPF's MediaElement) is only
// the last fallback, for a file this player can't open.
//
// One player, one clock. OwnVideo looks like a MediaElement to the rest of FastViewer (Position, SpeedRatio, Play,
// Pause, MediaOpened...), so everything built on Windows' player keeps working, but nothing in it waits for anything:
// - The clock is its own (the Stopwatch times the speed): a pause, a jump, a new speed or a loop start takes effect the
//   moment it is asked for. FastViewer's own sound (SoundPlay.cs) follows this clock, as it followed Windows' player.
// - The pictures are decoded on the graphics card by Windows' own Media Foundation (FrameReader, FramePath.cs), ahead of
//   the clock and on into the next loop, and copied (on the card) into a DXGI swap chain.
// - The swap chain is shown by DirectComposition, outside WPF: a thread presents the right picture at every screen
//   refresh (waiting for the vertical blank), whatever WPF is doing. A window's own drawing always lies UNDER its
//   DirectComposition content, so the pictures live in a borderless window of their own right behind FastViewer's window
//   (its owner: Windows keeps the two together, FastViewer's always on top). FastViewer's window is see-through exactly
//   where the video is (its backgrounds get a hole there), so the controls, captions, menus, trim bar and stats are
//   still drawn over the video by WPF as before. In the Glass look the blur behind moves to the video window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    // DirectComposition and DXGI, called by hand (no extra libraries).
    static class Comp
    {
        [DllImport("dcomp.dll")] static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Fn0(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnP(IntPtr self, IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnI(IntPtr self, int a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnOut(IntPtr self, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnIOut(IntPtr self, int i, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGuidOut(IntPtr self, ref Guid iid, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnTarget(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnSwap(IntPtr self, IntPtr device, IntPtr desc, IntPtr restrict, out IntPtr swap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnPresent(IntPtr self, int sync, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnGetBuf(IntPtr self, int i, ref Guid iid, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnAdd(IntPtr self, IntPtr visual, int above, IntPtr reference);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnCreateTex(IntPtr self, IntPtr desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FnCopy(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FnUpdate(IntPtr self, IntPtr dst, int sub, IntPtr box, IntPtr src, int row, int depth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FnStats(IntPtr self, IntPtr stats);
        static T Fn<T>(IntPtr o, int slot) where T : class { return VideoConverter.Gpu.Call<T>(o, slot); }

        static readonly object gate = new object();
        static bool tried;
        public static IntPtr Device, Factory, Adapter; // IDCompositionDevice, IDXGIFactory2, IDXGIAdapter
        public static string Trouble;
        static Guid Tex2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        public static bool Ready
        {
            get
            {
                lock (gate)
                {
                    if (!tried) { tried = true; try { Make(); } catch (Exception e) { Trouble = e.Message; Device = IntPtr.Zero; } }
                    return Device != IntPtr.Zero;
                }
            }
        }

        static void Check(int hr, string what) { if (hr < 0) throw new Exception(what + " (0x" + hr.ToString("X8") + ")"); }

        static void Make()
        {
            if (VideoConverter.Gpu.Manager == IntPtr.Zero || VideoConverter.Gpu.Device == IntPtr.Zero) throw new Exception(VideoConverter.Gpu.Trouble ?? "no D3D11 device");
            Guid iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), iidFactory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0"), iidDComp = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
            IntPtr dxgi;
            Check(Marshal.QueryInterface(VideoConverter.Gpu.Device, ref iidDxgiDevice, out dxgi), "no DXGI device");
            try
            {
                IntPtr adapter, factory, dev;
                Check(Fn<FnOut>(dxgi, 7)(dxgi, out adapter), "no graphics adapter");     // IDXGIDevice::GetAdapter
                Check(Fn<FnGuidOut>(adapter, 6)(adapter, ref iidFactory2, out factory), "no DXGI 1.2"); // IDXGIObject::GetParent
                Check(DCompositionCreateDevice(dxgi, ref iidDComp, out dev), "no DirectComposition");
                Adapter = adapter; Factory = factory; Device = dev;
            }
            finally { Marshal.Release(dxgi); }
        }

        // A flip-model swap chain for composition: BGRA, opaque (its 4th byte is never taken as see-through), 2 buffers.
        public static IntPtr SwapChain(int w, int h)
        {
            IntPtr d = Marshal.AllocHGlobal(48);
            try
            {
                Marshal.Copy(new[] { w, h, 87, 0, 1, 0, 0x20, 2, 0, 3, 3, 0 }, 0, d, 12); // size, B8G8R8A8, mono, 1 sample, render target, 2 buffers, stretch, flip sequential, alpha ignored
                IntPtr sc;
                return Fn<FnSwap>(Factory, 24)(Factory, VideoConverter.Gpu.Device, d, IntPtr.Zero, out sc) < 0 ? IntPtr.Zero : sc;
            }
            finally { Marshal.FreeHGlobal(d); }
        }

        public static IntPtr Texture(int w, int h)
        {
            IntPtr d = Marshal.AllocHGlobal(44);
            try
            {
                Marshal.Copy(new[] { w, h, 1, 1, 87, 1, 0, 0, 0x8, 0, 0 }, 0, d, 11);
                IntPtr t;
                return Fn<FnCreateTex>(VideoConverter.Gpu.Device, 5)(VideoConverter.Gpu.Device, d, IntPtr.Zero, out t) < 0 ? IntPtr.Zero : t;
            }
            finally { Marshal.FreeHGlobal(d); }
        }

        public static IntPtr BackBuffer(IntPtr swap) { IntPtr t; return Fn<FnGetBuf>(swap, 9)(swap, 0, ref Tex2D, out t) < 0 ? IntPtr.Zero : t; }
        public static void CopyAll(IntPtr dst, IntPtr src) { Fn<FnCopy>(VideoConverter.Gpu.Context, 47)(VideoConverter.Gpu.Context, dst, src); }
        public static void Upload(IntPtr dst, IntPtr data, int pitch) { Fn<FnUpdate>(VideoConverter.Gpu.Context, 48)(VideoConverter.Gpu.Context, dst, 0, IntPtr.Zero, data, pitch, 0); }
        public static int Present(IntPtr swap) { return Fn<FnPresent>(swap, 8)(swap, 0, 0); }
        // (Present count and the screen refresh it was shown at, from DXGI_FRAME_STATISTICS; false when Windows doesn't say.)
        public static bool Stats(IntPtr swap, out int presents, out int refreshes)
        {
            presents = refreshes = 0;
            IntPtr st = Marshal.AllocHGlobal(32);
            try
            {
                if (Fn<FnStats>(swap, 16)(swap, st) < 0) return false;
                presents = Marshal.ReadInt32(st, 0); refreshes = Marshal.ReadInt32(st, 4);
                return true;
            }
            finally { Marshal.FreeHGlobal(st); }
        }

        public static IntPtr Target(IntPtr hwnd) { IntPtr t; return Fn<FnTarget>(Device, 6)(Device, hwnd, 1, out t) < 0 ? IntPtr.Zero : t; }
        public static IntPtr Visual() { IntPtr v; return Fn<FnOut>(Device, 7)(Device, out v) < 0 ? IntPtr.Zero : v; }
        public static void SetRoot(IntPtr target, IntPtr visual) { Fn<FnP>(target, 3)(target, visual); }
        public static void Add(IntPtr parent, IntPtr child) { Fn<FnAdd>(parent, 16)(parent, child, 1, IntPtr.Zero); }
        public static void Remove(IntPtr parent, IntPtr child) { Fn<FnP>(parent, 17)(parent, child); }
        public static void Content(IntPtr visual, IntPtr content) { Fn<FnP>(visual, 15)(visual, content); }
        public static void Linear(IntPtr visual) { Fn<FnI>(visual, 11)(visual, 1); }
        public static void Commit() { if (Device != IntPtr.Zero) Fn<Fn0>(Device, 3)(Device); }
        // (IDCompositionVisual's overloads sit in the table in reverse: SetTransform(matrix) is 8, SetClip(rect) is 14.)
        public static void Matrix(IntPtr visual, Matrix m)
        {
            IntPtr p = Marshal.AllocHGlobal(24);
            try { Marshal.Copy(new[] { (float)m.M11, (float)m.M12, (float)m.M21, (float)m.M22, (float)m.OffsetX, (float)m.OffsetY }, 0, p, 6); Fn<FnP>(visual, 8)(visual, p); }
            finally { Marshal.FreeHGlobal(p); }
        }
        public static void Clip(IntPtr visual, Rect r)
        {
            IntPtr p = Marshal.AllocHGlobal(16);
            try { Marshal.Copy(new[] { (float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom }, 0, p, 4); Fn<FnP>(visual, 14)(visual, p); }
            finally { Marshal.FreeHGlobal(p); }
        }

        // The screen (DXGI output) a window is on, for waiting for its vertical blank.
        public static IntPtr OutputFor(IntPtr monitor)
        {
            if (Adapter == IntPtr.Zero) return IntPtr.Zero;
            IntPtr first = IntPtr.Zero, desc = Marshal.AllocHGlobal(128);
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    IntPtr o;
                    if (Fn<FnIOut>(Adapter, 7)(Adapter, i, out o) < 0 || o == IntPtr.Zero) break; // IDXGIAdapter::EnumOutputs
                    if (Fn<FnP>(o, 7)(o, desc) >= 0 && Marshal.ReadIntPtr(desc, 88) == monitor) { if (first != IntPtr.Zero) Marshal.Release(first); return o; }
                    if (first == IntPtr.Zero) first = o; else Marshal.Release(o);
                }
                return first;
            }
            finally { Marshal.FreeHGlobal(desc); }
        }
        public static bool WaitVBlank(IntPtr output) { return Fn<Fn0>(output, 10)(output) >= 0; }
    }

    // One thread for every playing OwnVideo: at each vertical blank of the screen the window is on, each shows the picture
    // that belongs to the moment it will be on screen. Nothing here waits for WPF or the window's thread.
    static class Presenter
    {
        static readonly List<OwnVideo> active = new List<OwnVideo>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Thread thread;
        public static volatile IntPtr Monitor;   // the screen FastViewer's window is on (VideoBack)
        static IntPtr output, outputMonitor;
        public static double Period = 1 / 60.0;  // seconds between screen refreshes, measured
        public static int Refreshes;             // counted (stats)

        public static void Add(OwnVideo v)
        {
            lock (active)
            {
                if (!active.Contains(v)) active.Add(v);
                if (thread == null) { thread = new Thread(Run) { IsBackground = true, Name = "Present", Priority = ThreadPriority.Highest }; thread.SetApartmentState(ApartmentState.MTA); thread.Start(); }
            }
            wake.Set();
        }

        public static void Remove(OwnVideo v) { lock (active) active.Remove(v); }
        public static bool Has(OwnVideo v) { lock (active) return active.Contains(v); }

        static void Run()
        {
            ThreadIds.Note("present");
            long last = 0;
            double freq = Stopwatch.Frequency;
            while (true)
            {
                OwnVideo[] list;
                lock (active) list = active.ToArray();
                if (list.Length == 0) { wake.WaitOne(500); last = 0; continue; }
                IntPtr mon = Monitor;
                if (mon != outputMonitor || output == IntPtr.Zero)
                {
                    if (output != IntPtr.Zero) { Marshal.Release(output); output = IntPtr.Zero; }
                    try { output = Comp.OutputFor(mon); } catch { output = IntPtr.Zero; }
                    outputMonitor = mon;
                }
                long before = Stopwatch.GetTimestamp();
                bool waited = false;
                try { waited = output != IntPtr.Zero && Comp.WaitVBlank(output); } catch { }
                long now = Stopwatch.GetTimestamp();
                // (A screen that is off, or a remote session, may not wait at all: then about 240 times a second.)
                if (!waited || (now - before) / freq < 0.0015) { Thread.Sleep(waited ? 2 : 4); now = Stopwatch.GetTimestamp(); }
                if (last != 0)
                {
                    double dt = (now - last) / freq;
                    if (waited && dt > 0.003 && dt < 0.04) Period = Period * 0.95 + dt * 0.05;
                }
                last = now;
                Refreshes++;
                foreach (var v in list)
                {
                    try { v.Tick(now, Period); }
                    catch (Exception e) { v.Broke(e); }
                }
            }
        }
    }

    // The borderless window behind FastViewer's window that the pictures are shown in (see the top of this file).
    sealed class VideoBack
    {
        static readonly Dictionary<Window, VideoBack> all = new Dictionary<Window, VideoBack>();
        readonly Window win;
        readonly IntPtr hwnd;
        HwndSource owner;
        IntPtr target, root;
        readonly List<OwnVideo> showing = new List<OwnVideo>();
        public FrameworkElement ClipTo;              // the stage: the video is never drawn outside it
        public Action<List<Rect>> Holes;             // where FastViewer's window must be see-through (window units)
        public Action<bool> Showing;                 // the video window came up / went (Glass: the blur moves)
        public static bool Cloak;                    // tests: drawn but never seen
        public bool Blur;                            // the Glass look's blur behind, while a video is up
        bool blurOn;
        int dx, dy, dw, dh;                          // client area minus window rectangle (for moves under way)

        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        const int NoSize = 1, NoMove = 2, NoZ = 4, NoActivate = 0x10, Show = 0x40, NoOwnerZ = 0x200;

        public static VideoBack For(Window w)
        {
            VideoBack b;
            if (!all.TryGetValue(w, out b)) { b = new VideoBack(w); all[w] = b; }
            return b;
        }

        VideoBack(Window w)
        {
            win = w;
            hwnd = new WindowInteropHelper(w).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(Hook);
            // (1.31.0 fix: the window is owned by the video window from the first video on, and Windows' taskbar drops owned
            // windows: WS_EX_APPWINDOW keeps its button. Set before the window first shows; set after, the taskbar only
            // noticed it on the next activation, so the button vanished until Alt+Tab.)
            if (w.ShowInTaskbar && hwnd != IntPtr.Zero) SetWindowLongPtr(hwnd, -20, (IntPtr)((long)GetWindowLongPtr(hwnd, -20) | 0x40000));
            w.Closed += delegate { Gone(); };
        }

        public IntPtr Handle { get { return owner != null ? owner.Handle : IntPtr.Zero; } }

        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        // (Tests: where the video window is, and whether FastViewer's window is right above it.)
        public string Describe()
        {
            if (owner == null) return "no video window";
            IntPtr o = owner.Handle;
            RECT r; GetWindowRect(o, out r);
            IntPtr above = GetWindow(o, 3); // GW_HWNDPREV
            int n = 0;
            while (above != IntPtr.Zero && above != hwnd && n < 50) { above = GetWindow(above, 3); n++; }
            return "video window " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + " at " + r.Left + "," + r.Top + ", visible " + IsWindowVisible(o) + ", topmost " + (((long)GetWindowLongPtr(o, -20) & 8) != 0) + " (FastViewer's " + (((long)GetWindowLongPtr(hwnd, -20) & 8) != 0) + "), windows between it and FastViewer's: " + (above == hwnd ? n.ToString() : "? (not found above)") + ", owner set " + (GetWindowLongPtr(hwnd, -8) == o) + ", Windows frame " + (frameOff ? "off" : "drawn");
        }
        public bool Up { get { return owner != null && IsWindowVisible(owner.Handle); } }
        public int Count { get { return showing.Count; } }

        bool Make()
        {
            if (owner != null) return target != IntPtr.Zero;
            if (hwnd == IntPtr.Zero || !Comp.Ready) return false;
            // A pop-up without a surface of its own (only its DirectComposition content), never activated, not in the taskbar.
            var p = new HwndSourceParameters("FastViewer video") { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x00200000 | 0x80 | 0x08000000, Width = 1, Height = 1, PositionX = -32000, PositionY = -32000 };
            owner = new HwndSource(p);
            if (Cloak) { int on = 1; DwmSetWindowAttribute(owner.Handle, 13, ref on, 4); }
            target = Comp.Target(owner.Handle);
            root = Comp.Visual();
            if (target == IntPtr.Zero || root == IntPtr.Zero) { Comp.Trouble = "no composition target"; return false; }
            Comp.SetRoot(target, root);
            Comp.Commit();
            // FastViewer's window is owned by it from now on: Windows keeps the two together, FastViewer's always just above.
            // (An owned window keeps its taskbar button only with WS_EX_APPWINDOW.)
            if (win.ShowInTaskbar) SetWindowLongPtr(hwnd, -20, (IntPtr)((long)GetWindowLongPtr(hwnd, -20) | 0x40000));
            SetWindowLongPtr(hwnd, -8, owner.Handle);
            if (win.ShowInTaskbar && IsWindowVisible(hwnd)) TaskbarTab.Add(hwnd); // (and the taskbar is told straight away)
            return true;
        }

        void Gone()
        {
            all.Remove(win);
            if (owner == null) return;
            try { SetWindowLongPtr(hwnd, -8, IntPtr.Zero); } catch { }
            var o = owner;
            owner = null;
            try { o.Dispose(); } catch { }
        }

        public bool Attach(OwnVideo v, IntPtr visual)
        {
            if (!Make()) return false;
            if (!showing.Contains(v)) { showing.Add(v); Comp.Add(root, visual); Comp.Commit(); }
            Sync();
            return true;
        }

        public void Detach(OwnVideo v, IntPtr visual)
        {
            if (!showing.Remove(v)) return;
            Comp.Remove(root, visual);
            Comp.Commit();
            Sync();
            UpdateHoles();
        }

        public void UpdateHoles()
        {
            var list = new List<Rect>();
            foreach (var v in showing) if (!v.Hole.IsEmpty) list.Add(v.Hole);
            if (Holes != null) Holes(list);
        }

        // Puts the video window exactly under FastViewer's window's client area; shown while a video is up.
        public void Sync()
        {
            if (owner == null) return;
            IntPtr o = owner.Handle;
            bool want = showing.Count > 0 && IsWindowVisible(hwnd) && !IsIconic(hwnd);
            if (want)
            {
                RECT c, w;
                GetClientRect(hwnd, out c);
                GetWindowRect(hwnd, out w);
                var pt = new POINT();
                ClientToScreen(hwnd, ref pt);
                dx = pt.X - w.Left; dy = pt.Y - w.Top; dw = c.Right - (w.Right - w.Left); dh = c.Bottom - (w.Bottom - w.Top);
                bool top = ((long)GetWindowLongPtr(hwnd, -20) & 8) != 0, otop = ((long)GetWindowLongPtr(o, -20) & 8) != 0;
                if (top != otop) SetWindowPos(o, (IntPtr)(top ? -1 : -2), 0, 0, 0, 0, NoMove | NoSize | NoActivate);
                bool was = IsWindowVisible(o);
                // (Right below FastViewer's window, always: being its owner keeps it below, not next to it, and any window
                // in between would show through the hole.)
                bool adjacent = GetWindow(hwnd, 2) == o; // GW_HWNDNEXT
                SetWindowPos(o, adjacent ? IntPtr.Zero : hwnd, pt.X, pt.Y, Math.Max(1, c.Right), Math.Max(1, c.Bottom), (adjacent ? NoZ : 0) | NoActivate | NoOwnerZ | (was ? 0 : Show));
                if (!was) { FrameDrawn(false); OwnPreviews(true); if (Showing != null) Showing(true); }
            }
            else if (IsWindowVisible(o)) { ShowWindow(o, 0); FrameDrawn(true); OwnPreviews(false); if (Showing != null) Showing(false); }
            bool blur = Blur && want;
            if (blur != blurOn) { blurOn = blur; try { Looks.SetBlurBehind(o, blur); } catch { } }
            Presenter.Monitor = MonitorFromWindow(hwnd, 2);
        }

        // (1.31.0 fix: on Windows 10 a resizable window gets its frame colour drawn behind every see-through spot, so the
        // hole showed black or grey instead of the video. While the video is up FastViewer's window gets a plain window region
        // (Frame: the window does it, Appearance.RoundCorners; a see-through look keeps its round one): Windows then draws no
        // frame behind it, and its own frame drawing stays on, so the old basic frame can never appear (it did with
        // DWMWA_NCRENDERING_POLICY off: on activation and while resizing). Resizing and snapping come from the style: kept.)
        bool frameOff;
        public Action<bool> Frame;                   // true: the video is up, no Windows frame wanted
        void FrameDrawn(bool on)
        {
            if (frameOff == !on) return;
            frameOff = !on;
            if (Frame != null) Frame(!on);
        }

        // ---- taskbar and Alt+Tab previews (1.31.0 fix): Windows' own picture of FastViewer's window can't hold the video (it
        // lies in this other window), so while it is up FastViewer hands Windows its own picture: the window drawn over the
        // video's current frame. Made only when Windows asks (hovering the taskbar button, Alt+Tab), never while playing.
        [DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr h, IntPtr bmp, int flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetIconicLivePreviewBitmap(IntPtr h, IntPtr bmp, IntPtr offset, int flags);
        [DllImport("dwmapi.dll")] static extern int DwmInvalidateIconicBitmaps(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, byte[] bmi, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        bool previewsOwn;
        long refreshAt; // (when the last refresh was asked for)
        long thumbAskedAt; // (when Windows last asked for the thumbnail)
        bool thumbWasOpen;
        RenderTargetBitmap lookCache; long lookAt; // (thumbnails: the window's own drawing, reused for half a second)
        BitmapSource lastLive; // (the big preview takes ~170 ms: the once-a-second refresh reuses it, a new peek makes it again)
        System.Windows.Threading.DispatcherTimer previewTimer;
        public static string LastPreview;            // (tests)

        void OwnPreviews(bool on)
        {
            if (previewsOwn == on) return;
            previewsOwn = on;
            int v = on ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 7, ref v, 4); DwmSetWindowAttribute(hwnd, 10, ref v, 4); } catch { } // FORCE_ICONIC_REPRESENTATION, HAS_ICONIC_BITMAP
            if (previewTimer == null)
            {
                // (A preview that stays up follows the video: Windows asks again only while one is showing.)
                previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                previewTimer.Tick += delegate
                {
                    // (25 a second while Windows keeps asking for the thumbnail (it is showing), else once a second; when it
                    // closes the card's small copies are let go.)
                    bool open = Stopwatch.GetTimestamp() - thumbAskedAt < Stopwatch.Frequency * 3 / 2;
                    if (!open && thumbWasOpen) foreach (var sv in showing) sv.ReleaseThumbPublic();
                    thumbWasOpen = open;
                    var want = TimeSpan.FromMilliseconds(open ? 40 : 1000);
                    if (previewTimer.Interval != want) previewTimer.Interval = want;
                    refreshAt = Stopwatch.GetTimestamp();
                    try { DwmInvalidateIconicBitmaps(hwnd); } catch { }
                };
            }
            if (on) previewTimer.Start(); else { previewTimer.Stop(); lastLive = null; }
        }

        // The window as it looks now, at most maxW x maxH pixels (0: full size), premultiplied 32-bit.
        internal BitmapSource Preview(int maxW, int maxH)
        {
            RECT c; GetClientRect(hwnd, out c);
            int cw = c.Right, ch = c.Bottom;
            if (cw < 1 || ch < 1) return null;
            var src = HwndSource.FromHwnd(hwnd);
            if (src == null || src.CompositionTarget == null) return null;
            var dev = src.CompositionTarget.TransformToDevice;
            double scale = maxW > 0 && maxH > 0 ? Math.Min(1.0, Math.Min(maxW / (double)cw, maxH / (double)ch)) : 1.0;
            int w = Math.Max(1, (int)Math.Round(cw * scale)), h = Math.Max(1, (int)Math.Round(ch * scale));
            RenderTargetBitmap look;
            bool small = scale < 1;
            if (small && lookCache != null && lookCache.PixelWidth == w && lookCache.PixelHeight == h && Stopwatch.GetTimestamp() - lookAt < Stopwatch.Frequency / 2) look = lookCache;
            else
            {
                look = new RenderTargetBitmap(w, h, 96 * dev.M11 * scale, 96 * dev.M22 * scale, PixelFormats.Pbgra32);
                look.Render(win);
                if (small) { lookCache = look; lookAt = Stopwatch.GetTimestamp(); }
            }
            // (1.31.0 fix: the thumbnail is exactly the size Windows asked for. Windows works that size out from the window's size
            // when it asks; a picture of another shape (the window was still settling, being resized, maximized, going full
            // screen) was stretched by Windows to fill it. Now the window's picture is scaled evenly and centred in that size,
            // with the look's background around it if the shapes differ.)
            int cwOut = small && maxW > 0 ? maxW : w, chOut = small && maxH > 0 ? maxH : h;
            double ox = Math.Floor((cwOut - w) / 2.0), oy = Math.Floor((chOut - h) / 2.0);
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality); // (the last step down: Fant, not the blurry default)
            using (var dc = dv.RenderOpen())
            {
                if (cwOut != w || chOut != h)
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, cwOut, chOut));
                    var bg = win.TryFindResource("WindowBg") as Brush;
                    if (bg != null) dc.DrawRectangle(bg, null, new Rect(0, 0, cwOut, chOut));
                }
                dc.PushTransform(new TranslateTransform(ox, oy));
                dc.PushTransform(new ScaleTransform(scale, scale));
                foreach (var v in showing) v.DrawPreview(dc, scale);
                dc.Pop();
                dc.DrawImage(look, new Rect(0, 0, w, h));
                dc.Pop();
            }
            var all = new RenderTargetBitmap(cwOut, chOut, 96, 96, PixelFormats.Pbgra32);
            all.Render(dv);
            all.Freeze();
            return all;
        }

        void SendPreview(bool live, int maxW, int maxH)
        {
            var sw = Stopwatch.StartNew();
            BitmapSource b = null;
            if (live && lastLive != null && Stopwatch.GetTimestamp() - refreshAt < Stopwatch.Frequency / 3) b = lastLive;
            else try { b = Preview(live ? 0 : maxW, live ? 0 : maxH); } catch { }
            if (live) lastLive = b; else thumbAskedAt = Stopwatch.GetTimestamp();
            if (b == null) return;
            int w = b.PixelWidth, h = b.PixelHeight;
            var bmi = new byte[40];
            BitConverter.GetBytes(40).CopyTo(bmi, 0); BitConverter.GetBytes(w).CopyTo(bmi, 4); BitConverter.GetBytes(h).CopyTo(bmi, 8); // (bottom-up: the thumbnail refuses top-down)
            BitConverter.GetBytes((short)1).CopyTo(bmi, 12); BitConverter.GetBytes((short)32).CopyTo(bmi, 14);
            IntPtr bits, dib = CreateDIBSection(IntPtr.Zero, bmi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;
            try
            {
                var px = new byte[w * h * 4];
                b.CopyPixels(px, w * 4, 0);
                for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, bits + (h - 1 - y) * w * 4, w * 4);
                int hr = live ? DwmSetIconicLivePreviewBitmap(hwnd, dib, IntPtr.Zero, 0) : DwmSetIconicThumbnail(hwnd, dib, 0);
                LastPreview = (live ? "live preview " : "thumbnail ") + w + "x" + h + " in " + sw.ElapsedMilliseconds + " ms, " + (hr >= 0 ? "taken" : "refused " + hr.ToString("X"));
            }
            finally { DeleteObject(dib); }
        }

        IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (previewsOwn && msg == 0x323) { long l = (long)lp; SendPreview(false, (int)((l >> 16) & 0xFFFF), (int)(l & 0xFFFF)); handled = true; return IntPtr.Zero; } // WM_DWMSENDICONICTHUMBNAIL
            if (previewsOwn && msg == 0x326) { SendPreview(true, 0, 0); handled = true; return IntPtr.Zero; } // WM_DWMSENDICONICLIVEPREVIEWBITMAP
            if (owner == null) return IntPtr.Zero;
            if (msg == 0x46 && IsWindowVisible(owner.Handle)) // WM_WINDOWPOSCHANGING: moved in the same moment, not a frame later
            {
                int b = IntPtr.Size * 2;
                int x = Marshal.ReadInt32(lp, b), y = Marshal.ReadInt32(lp, b + 4), cx = Marshal.ReadInt32(lp, b + 8), cy = Marshal.ReadInt32(lp, b + 12), flags = Marshal.ReadInt32(lp, b + 16);
                if ((flags & (NoMove | NoSize)) != (NoMove | NoSize) && (flags & NoMove) == 0 && (flags & NoSize) == 0)
                    SetWindowPos(owner.Handle, IntPtr.Zero, x + dx, y + dy, Math.Max(1, cx + dw), Math.Max(1, cy + dh), NoZ | NoActivate | NoOwnerZ);
                else if ((flags & NoMove) == 0 && (flags & NoSize) != 0)
                    SetWindowPos(owner.Handle, IntPtr.Zero, x + dx, y + dy, 0, 0, NoZ | NoActivate | NoOwnerZ | NoSize);
            }
            else if (msg == 0x47 || msg == 0x18 || msg == 0x05)
            {
                Sync(); // WM_WINDOWPOSCHANGED, WM_SHOWWINDOW, WM_SIZE
                if (msg == 0x05 && previewsOwn) { lookCache = null; try { DwmInvalidateIconicBitmaps(hwnd); } catch { } } // (a new size: Windows asks again, for the new shape)
            }
            return IntPtr.Zero;
        }
    }

    // A video (or song) player: FastViewer's own when it can open the file, else Windows' player inside it.
    class OwnVideo : FrameworkElement
    {
        public static bool Enabled = true;           // settings line ownPlayer= (on by default since 1.31.0) (Settings > Videos), tests --player own|windows
        public static bool TestSilent;               // tests: Windows' player (the fallback) stays at volume 0
        static readonly HashSet<string> ownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // At start: the graphics device, Media Foundation and DirectComposition made in the background, so the first video
        // doesn't wait for them (about 120-140 ms on the user's PC).
        public static void Warm()
        {
            if (!Enabled) return;
            var t = new Thread(delegate() { try { VideoConverter.Reading.Begin(); var m = VideoConverter.Gpu.Manager; bool ok = Comp.Ready; } catch { } }) { IsBackground = true, Name = "Warm" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public static bool IsOwnFile(string f) { if (f == null) return false; lock (ownFiles) return ownFiles.Contains(f); }
        public static Action<string> LogLine;        // (the window's log)
        public static Func<string, double[]> HeardClock; // (the window: FastViewer's sound's clock for a file, {at, stamp, pace}, or null)
        public volatile int HeardFollows;            // (tests)
        static readonly SemaphoreSlim OpenGate = new SemaphoreSlim(1);

        // ---- what the window sees (as on a MediaElement)
        public event EventHandler MediaOpened, MediaEnded;
        public event Action<Exception> MediaFailed;
        public MediaState LoadedBehavior { get; set; }
        public MediaState UnloadedBehavior { get; set; }
        public Stretch Stretch { get; set; }
        public StretchDirection StretchDirection { get; set; }
        bool scrub = true;
        public bool ScrubbingEnabled { get { return scrub; } set { scrub = value; if (inner != null) inner.ScrubbingEnabled = value; } }
        public Func<double> LoopSource;              // (the window: Repeat's loop length for this player, 0 = none)
        public UIElement Host;                       // the slot's layer in the stage (on top of the cover: ZIndex 2 and up)

        Uri source;
        MediaElement inner;                          // Windows' player, only when this file can't be played here
        int mode;                                    // 0 nothing, 1 opening, 2 own, 3 Windows' player
        int openToken;
        double volume = 0.5;
        bool muted;
        double speed = 1;

        public bool IsOwn { get { return mode == 2; } }
        public bool IsFallback { get { return mode == 3; } }
        public string File { get { return source == null ? null : source.LocalPath; } }

        public OwnVideo()
        {
            LoadedBehavior = MediaState.Manual;
            UnloadedBehavior = MediaState.Manual;
            Stretch = Stretch.Uniform;
            StretchDirection = StretchDirection.Both;
            IsVisibleChanged += delegate { Unsettle(); };
            SizeChanged += delegate { Unsettle(); };
        }

        public Uri Source
        {
            get { return inner != null ? inner.Source : source; }
            set
            {
                if (value == source && value != null) return;
                CloseOwn();
                source = value;
                openToken++;
                if (value == null) { if (inner != null) inner.Source = null; mode = 0; return; }
                if (!Enabled || !value.IsFile) { UseWindows(false); return; }
                if (inner != null) { inner.Source = null; RemoveVisualChild(inner); inner = null; InvalidateMeasure(); }
                mode = 1;
                Open(value.LocalPath, openToken);
            }
        }

        public TimeSpan Position
        {
            get
            {
                if (inner != null) return inner.Position;
                return TimeSpan.FromSeconds(Math.Max(0, FilePos(CountedNow())));
            }
            set
            {
                if (inner != null) { inner.Position = value; return; }
                SeekTo(value.TotalSeconds);
            }
        }

        public Duration NaturalDuration
        {
            get
            {
                if (inner != null) return inner.NaturalDuration;
                return mode == 2 && duration > 0 ? new Duration(TimeSpan.FromSeconds(duration)) : Duration.Automatic;
            }
        }

        public double SpeedRatio
        {
            get { return inner != null ? inner.SpeedRatio : speed; }
            set
            {
                if (inner != null) { inner.SpeedRatio = value; speed = value; return; }
                if (value <= 0 || Math.Abs(value - speed) < 1e-9) return;
                long now = Stopwatch.GetTimestamp();
                lock (clockGate) { Rebase(now); speed = value; }
                Changes++;
            }
        }

        public double Volume
        {
            get { return inner != null ? inner.Volume : volume; }
            set { volume = value; if (inner != null) inner.Volume = TestSilent ? 0 : value; }
        }

        public bool IsMuted
        {
            get { return inner != null ? inner.IsMuted : muted; }
            set { muted = value; if (inner != null) inner.IsMuted = value; }
        }

        public int NaturalVideoWidth { get { return inner != null ? inner.NaturalVideoWidth : (mode == 2 ? natW : 0); } }
        public int NaturalVideoHeight { get { return inner != null ? inner.NaturalVideoHeight : (mode == 2 ? natH : 0); } }
        public bool HasAudio { get { return inner != null ? inner.HasAudio : hasAudio; } }
        public bool HasVideo { get { return inner != null ? inner.HasVideo : natW > 0; } }

        public void Play()
        {
            if (inner != null) { inner.Play(); innerPlays = true; return; }
            wantPlay = true;
            if (mode != 2) return;
            UpdateLoop();
            if (reader == null && natW > 0) Wake();
            var r = reader;
            long now = Stopwatch.GetTimestamp();
            lock (clockGate)
            {
                if (playing) return;
                Rebase(now);
                heardPace = -1;
                if (!(loopLen > 0) && duration > 0 && basePos >= duration - 0.0005) return; // (at the end: as Windows' player, it stays there)
                playing = true;
                // (A reader still opening, or one that has nothing ready: the clock starts with the first picture.)
                if (r != null && (!r.Opened || r.Waiting == 0)) { holding = true; holdSince = now; }
            }
            Changes++;
            Presenter.Add(this);
            StartLoopTimer();
        }

        public void Pause()
        {
            if (inner != null) { inner.Pause(); innerPlays = false; return; }
            wantPlay = false;
            if (mode != 2) return;
            long now = Stopwatch.GetTimestamp();
            lock (clockGate) { if (!playing) return; Rebase(now); playing = false; holding = false; }
            Changes++;
        }

        public void Stop()
        {
            if (inner != null) { inner.Stop(); innerPlays = false; return; }
            wantPlay = false;
            if (mode != 2) return;
            lock (clockGate) { playing = false; holding = false; basePos = 0; baseTick = Stopwatch.GetTimestamp(); }
            AskFrame(0, true);
        }

        public void Close()
        {
            if (inner != null) { inner.Close(); return; }
            CloseOwn();
            source = null;
            mode = 0;
        }

        // ---------------------------------------------------------------- the clock
        readonly object clockGate = new object();
        double basePos;           // the clock (seconds, counted on across loops) at baseTick
        long baseTick;
        bool playing, holding, wantPlay;
        long holdSince;
        double loopLen;           // Repeat: the clock wraps at this length (the sound's own loop); 0 = no Repeat
        double heardPace = -1;    // while following FastViewer's sound: the pace being heard (else -1: the speed asked for)
        double duration;
        long wrappedAt;
        public volatile int Changes; // (tests: pauses, plays, speed changes taken)
        static readonly double Freq = Stopwatch.Frequency;

        void Rebase(long now) { basePos = Counted(now); baseTick = now; }

        double Counted(long tick)
        {
            if (!playing || holding) return basePos;
            double c = basePos + (tick - baseTick) / Freq * (heardPace > 0 ? heardPace : speed); // (following the sound: the pace being heard)
            if (!(loopLen > 0) && duration > 0 && c > duration) c = duration;
            return c;
        }

        public double CountedNow() { lock (clockGate) return Counted(Stopwatch.GetTimestamp()); }

        double FilePos(double counted)
        {
            double L = loopLen;
            if (L > 0) { double k = Math.Floor(counted / L); double p = counted - k * L; return p < 0 ? 0 : p; }
            return counted;
        }

        public bool Playing { get { lock (clockGate) return playing; } }
        bool innerPlays;
        // (1.31.0: playing, or going to as soon as it is open; Windows' player inside it too. The window's watch: only the
        // file on screen may.)
        public bool WantsToPlay { get { return inner != null ? innerPlays : wantPlay || Playing; } }
        public bool Moving { get { lock (clockGate) return playing && !holding; } }   // (its clock runs: the sound goes with it)
        public double MeasuredFps { get { var r = reader; return r == null ? 0 : r.MeasuredFps > 0 ? r.MeasuredFps : 1 / r.FrameSeconds; } }
        public double LoopLen { get { return loopLen; } }

        void SeekTo(double t)
        {
            if (mode != 2) { pendingSeek = t; return; }
            if (double.IsNaN(t) || t < 0) t = 0;
            if (duration > 0) t = Math.Min(t, duration);
            long now = Stopwatch.GetTimestamp();
            double target;
            bool back;
            lock (clockGate)
            {
                double cur = Counted(now), L = loopLen, pos = FilePos(cur);
                // The window starting a loop over (Repeat) where this player already does it by itself, seamlessly: the clock
                // goes on into the next loop rather than jumping. (Just before the wrap: on into it; just after: nothing to do.)
                if (L > 0 && t < 0.25 && playing)
                {
                    if (pos > L - 0.35) { basePos = (Math.Floor(cur / L) + 1) * L + t; baseTick = now; wrappedAt = now; return; }
                    if (pos < 0.35 && (now - wrappedAt) / Freq < 1.0) return;
                }
                double k = L > 0 ? Math.Floor(cur / L) : 0;
                target = k * L + t;
                back = target < cur - 0.0005;
                basePos = target;
                heardPace = -1;
                baseTick = now;
                if (playing && hasPicture) { holding = true; holdSince = now; } // (the picture and the sound go on together once the place is decoded)
            }
            Seeks++;
            if (reader == null && natW > 0) Wake();
            AskFrame(target, back || double.IsNaN(shownClock) || Math.Abs(target - shownClock) > 0.75);
        }

        void AskFrame(double clock, bool jump)
        {
            var r = reader;
            if (r != null) { r.Ask(clock, loopLen, speed); if (jump) r.Jump(); }
            needFrame = r != null;
            Presenter.Add(this);
        }

        // ---- resting: a video off screen keeps only its picture (in a texture on the card), not its decoder
        // (each decoder costs 20-45 MB, and memory Windows' decoders took is rarely given back). Waking takes ~25 ms;
        // the picture on screen meanwhile is the kept one and the clock waits for the first new picture.
        int maxW = 3840, maxH = 2160;
        bool everOnTop;
        long hiddenSince;

        void Rest()
        {
            var r = reader;
            if (r == null) return;
            lock (gpuGate) { reader = null; }
            r.Quit();
            needFrame = false;
            Rests++;
        }

        void Wake()
        {
            if (mode != 2 || reader != null || natW <= 0 || File == null) return;
            var r = new VideoConverter.FrameReader(File, maxW, maxH, IntPtr.Zero, true);
            double c;
            lock (clockGate) c = Counted(Stopwatch.GetTimestamp());
            r.Ask(c, loopLen, speed);
            lock (gpuGate) reader = r;
            shownClock = double.NaN; // (the new reader's first picture is shown whatever its time: it is the place asked for)
            Wakes++;
        }

        // From the watch timer: a video that is not on screen and not playing rests (at once when it never was on screen:
        // a neighbour made ready ahead; 3 s after leaving the screen, for going back and forth quickly).
        void CheckRest()
        {
            if (reader == null || mode != 2) return;
            if (attached) { everOnTop = true; hiddenSince = 0; return; }
            if (Playing || needFrame || !hasPicture) { hiddenSince = 0; return; }
            long now = Stopwatch.GetTimestamp();
            if (hiddenSince == 0) hiddenSince = now;
            if (!everOnTop || (now - hiddenSince) / Freq > 3) Rest();
        }

        public volatile int Rests, Wakes; // (tests)

        public string Debug()
        {
            var r = reader;
            return "mode " + mode + ", reader " + (r == null ? "none" : (r.Opened ? "open" : "opening") + (r.Failed ? " FAILED " + r.Trouble : "") + ", " + r.Waiting + " waiting from " + r.NextClock.ToString("0.000") + (r.Ended ? ", ended" : "") + ", seeks " + r.Seeks + ", wraps " + r.Wraps)
                + ", clock " + CountedNow().ToString("0.000") + ", loop " + loopLen.ToString("0.000") + ", shown " + shownClock.ToString("0.000") + ", placed " + attached + " " + lastM + " clip " + lastClip + " presents " + Presents + ", presenter " + Presenter.Has(this) + ", need " + needFrame + ", playing " + Playing + ", holding " + holding
                + (r != null && VideoConverter.FrameReader.Tracing ? ", recent: " + string.Join(" ", r.Recent.Skip(Math.Max(0, r.Recent.Count - 12))) : "");
        }

        // Repeat's loop length, from the window (its own Repeat and the sound's loop).
        void UpdateLoop()
        {
            double L = 0;
            try { if (LoopSource != null) L = LoopSource(); } catch { }
            if (L < 0.3 || double.IsNaN(L)) L = 0;
            if (Math.Abs(L - loopLen) < 0.0005) return;
            long now = Stopwatch.GetTimestamp();
            lock (clockGate)
            {
                // (Same place, same loop number: the sound's counted clock moves by next to nothing.)
                double cnt = Counted(now), pos = FilePos(cnt), k = loopLen > 0 ? Math.Floor(cnt / loopLen) : 0;
                if (L > 0 && pos > L) { pos = 0; k++; }
                loopLen = L;
                basePos = L > 0 ? k * L + pos : pos;
                baseTick = now;
            }
        }

        DispatcherTimer loopTimer;
        void StartLoopTimer()
        {
            if (loopTimer == null)
            {
                loopTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                loopTimer.Tick += delegate { if (mode != 2 || !Playing) { loopTimer.Stop(); return; } UpdateLoop(); };
            }
            if (!loopTimer.IsEnabled) loopTimer.Start();
        }

        // ---------------------------------------------------------------- opening
        VideoConverter.FrameReader reader;
        int natW, natH, picW, picH, turn; // (turn: the file's tag, already applied by Windows to the pictures: undone here)
        bool hasAudio;
        double pendingSeek = double.NaN;
        public string Trouble;

        void Open(string file, int token)
        {
            var disp = Dispatcher;
            bool music = ViewerWindow.IsAudioFile(file);
            try
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                maxW = Math.Max(1920, screen.Width); maxH = Math.Max(1080, screen.Height);
            }
            catch { }
            var t = new Thread(delegate()
            {
                VideoConverter.FrameReader r = null;
                string why = null;
                double len = 0; bool audio = false, video = false;
                var watch = Stopwatch.StartNew();
                // (One file opens at a time: the decoders of four neighbours made at once cost memory that stays, and the
                // one asked for first, the file you go to, is ready soonest. A file that was moved on from never waits.)
                bool gated = OpenGate.Wait(3000);
                long gateMs = watch.ElapsedMilliseconds, probeMs = 0, readerMs = 0;
                if (token != openToken) { if (gated) OpenGate.Release(); return; }
                try
                {
                    Probe(file, out len, out audio, out video);
                    probeMs = watch.ElapsedMilliseconds - gateMs;
                    if (video && !music)
                    {
                        r = new VideoConverter.FrameReader(file, maxW, maxH, IntPtr.Zero, true);
                        r.Ask(0, 0, 1);
                        while (!r.Opened && !r.Failed && watch.ElapsedMilliseconds < 8000) Thread.Sleep(2);
                        if (!r.Opened || r.Failed) { why = r.Trouble ?? "the picture didn't open"; r.Quit(); r = null; }
                        else
                        {
                            while (r.Waiting == 0 && !r.Failed && !r.Ended && watch.ElapsedMilliseconds < 8000) Thread.Sleep(2);
                            if (r.Failed) { why = r.Trouble; r.Quit(); r = null; }
                            else if (!r.OnGpu && r.Waiting > 0 && !Comp.Ready) { why = "no graphics card composition: " + Comp.Trouble; r.Quit(); r = null; }
                        }
                    }
                    else if (!audio) why = "nothing to play";
                    if (r == null && why == null && video && !music) why = "the picture can't be read";
                    if (r != null && !Comp.Ready) { why = "no DirectComposition: " + Comp.Trouble; r.Quit(); r = null; }
                }
                catch (Exception e) { why = e.Message; if (r != null) { r.Quit(); r = null; } }
                finally { if (gated) OpenGate.Release(); }
                int ms = (int)watch.ElapsedMilliseconds;
                readerMs = ms - gateMs - probeMs;
                OpenNote = Path.GetFileName(file) + ": waited " + gateMs + " ms for the file before, probe " + probeMs + " ms, first picture " + readerMs + " ms (reader opening " + (r != null ? r.OpenMs : -1) + " ms, graphics device made in " + VideoConverter.Gpu.MakeMs + " ms)";
                disp.BeginInvoke(new Action(delegate
                {
                    if (token != openToken) { if (r != null) r.Quit(); return; }
                    if (why != null && !(music && audio)) { Note("own player can't play " + Path.GetFileName(file) + ": " + why + " (Windows' player)"); UseWindows(true); return; }
                    Opened(file, r, len, audio, ms);
                }));
            }) { IsBackground = true, Name = "Open" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        // Length and what streams a file has, with Media Foundation's own reader (no graphics card needed).
        static void Probe(string file, out double len, out bool audio, out bool video)
        {
            len = 0; audio = video = false;
            VideoConverter.Reading.Begin();
            var rd = VideoConverter.Reading.Open(file);
            if (rd == null) throw new Exception("Windows can't open the file");
            try
            {
                len = VideoConverter.Reading.Length(rd);
                for (int i = 0; i < 32; i++)
                {
                    bool a, v;
                    if (!VideoConverter.Reading.Kind(rd, i, out a, out v)) break;
                    audio |= a; video |= v;
                }
            }
            finally { Marshal.ReleaseComObject(rd); }
        }

        IntPtr swap, curTex, outer, innerVis;
        readonly object gpuGate = new object();
        public volatile bool hasPicture;
        public int OpenMs;
        public string OpenNote = "";

        void Opened(string file, VideoConverter.FrameReader r, double len, bool audio, int ms)
        {
            OpenMs = ms;
            Note("own player opened " + OpenNote);
            reader = r;
            duration = len > 0 ? len : (r != null ? r.Length : 0);
            hasAudio = audio;
            if (r != null)
            {
                natW = r.NativeWidth; natH = r.NativeHeight; picW = r.Width; picH = r.Height; turn = r.Turn;
                lock (gpuGate)
                {
                    swap = Comp.SwapChain(picW, picH);
                    curTex = Comp.Texture(picW, picH);
                    outer = Comp.Visual();
                    innerVis = Comp.Visual();
                }
                if (swap == IntPtr.Zero || curTex == IntPtr.Zero || outer == IntPtr.Zero || innerVis == IntPtr.Zero)
                {
                    Note("own player: no swap chain for " + Path.GetFileName(file) + " (Windows' player)");
                    CloseOwn();
                    UseWindows(true);
                    return;
                }
                Comp.Content(innerVis, swap);
                Comp.Linear(innerVis);
                Comp.Add(outer, innerVis);
                Comp.Commit();
            }
            else { natW = natH = 0; }
            mode = 2;
            lock (ownFiles) ownFiles.Add(file);
            lock (clockGate) { basePos = 0; baseTick = Stopwatch.GetTimestamp(); playing = false; holding = false; }
            InvalidateMeasure();
            if (r != null) AskFrame(0, false);
            if (!double.IsNaN(pendingSeek)) { double p = pendingSeek; pendingSeek = double.NaN; SeekTo(p); }
            var h = MediaOpened;
            if (h != null) h(this, EventArgs.Empty);
            if (wantPlay && mode == 2) Play();
            Unsettle();
        }

        void CloseOwn()
        {
            wantPlay = false;
            pendingSeek = double.NaN;
            Presenter.Remove(this);
            if (attached) Detach();
            var r = reader;
            reader = null;
            if (r != null) r.Quit();
            string f = File;
            if (f != null) lock (ownFiles) ownFiles.Remove(f);
            lock (gpuGate)
            {
                if (swap != IntPtr.Zero) { Marshal.Release(swap); swap = IntPtr.Zero; }
                ReleaseThumb();
                if (curTex != IntPtr.Zero) { Marshal.Release(curTex); curTex = IntPtr.Zero; }
                if (innerVis != IntPtr.Zero) { Marshal.Release(innerVis); innerVis = IntPtr.Zero; }
                if (outer != IntPtr.Zero) { Marshal.Release(outer); outer = IntPtr.Zero; }
                hasPicture = false;
            }
            lock (clockGate) { playing = false; holding = false; basePos = 0; loopLen = 0; }
            duration = 0; natW = natH = 0; hasAudio = false;
            shownClock = double.NaN;
            if (mode == 2) mode = 0;
        }

        // Windows' player inside this one: for a file FastViewer can't play (or with the switch off).
        void UseWindows(bool late)
        {
            if (inner == null)
            {
                inner = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch, StretchDirection = StretchDirection, ScrubbingEnabled = scrub };
                inner.MediaOpened += delegate
                {
                    if (late && !double.IsNaN(pendingSeek)) { inner.Position = TimeSpan.FromSeconds(pendingSeek); pendingSeek = double.NaN; }
                    var h = MediaOpened; if (h != null) h(this, EventArgs.Empty);
                };
                inner.MediaEnded += delegate { var h = MediaEnded; if (h != null) h(this, EventArgs.Empty); };
                inner.MediaFailed += delegate(object o, ExceptionRoutedEventArgs e) { var h = MediaFailed; if (h != null) h(e.ErrorException); };
                AddVisualChild(inner);
            }
            mode = 3;
            inner.IsMuted = muted;
            inner.Volume = TestSilent ? 0 : volume;
            inner.Source = source;
            inner.SpeedRatio = speed;
            if (late) { innerPlays = wantPlay; if (wantPlay) inner.Play(); else inner.Pause(); }
            InvalidateMeasure();
        }

        protected override int VisualChildrenCount { get { return inner != null ? 1 : 0; } }
        protected override Visual GetVisualChild(int index) { if (inner == null) throw new ArgumentOutOfRangeException(); return inner; }

        Size Fit(Size box)
        {
            double w = natW, h = natH;
            if (w <= 0 || h <= 0) return new Size(0, 0);
            double sx = double.IsInfinity(box.Width) ? double.PositiveInfinity : box.Width / w;
            double sy = double.IsInfinity(box.Height) ? double.PositiveInfinity : box.Height / h;
            double k = Stretch == Stretch.None ? 1 : Math.Min(sx, sy);
            if (double.IsInfinity(k)) k = 1;
            if (StretchDirection == StretchDirection.DownOnly) k = Math.Min(1, k);
            else if (StretchDirection == StretchDirection.UpOnly) k = Math.Max(1, k);
            return new Size(w * k, h * k);
        }

        protected override Size MeasureOverride(Size avail)
        {
            if (inner != null) { inner.Stretch = Stretch; inner.StretchDirection = StretchDirection; inner.Measure(avail); return inner.DesiredSize; }
            return Fit(avail);
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (inner != null) { inner.Arrange(new Rect(final)); return final; }
            return Fit(final);
        }

        // ---------------------------------------------------------------- showing the pictures (the "Present" thread)
        volatile bool needFrame;
        public double shownClock = double.NaN, shownFile = double.NaN;
        public volatile int Presents, Skipped, Late, Seeks;   // (stats, tests)
        public long LastPresentTick;
        public List<double[]> Trace;   // (tests: every picture handed to the screen: Stopwatch tick, time in the file, clock)
        Exception broken;

        internal void Broke(Exception e)
        {
            if (broken != null) return;
            broken = e;
            Presenter.Remove(this);
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Note("own player stopped: " + e.Message + " (Windows' player takes over)");
                double at = Position.TotalSeconds;
                CloseOwn();
                pendingSeek = at;
                broken = null;
                UseWindows(true);
            }));
        }

        internal void Tick(long now, double period)
        {
            bool ended = false, stillNeeded;
            lock (gpuGate)
            {
                double c, target, pace, L;
                bool play, hold;
                // While FastViewer's sound plays this file, the clock is what is being heard: the picture can't drift from
                // the sound, whatever the speed changes (the sound takes a new speed only after what it already queued).
                double[] heard = null;
                var hc = HeardClock;
                if (hc != null) try { heard = hc(File); } catch { }
                lock (clockGate)
                {
                    if (heard == null || !playing || holding) { if (heardPace > 0) { Rebase(now); heardPace = -1; } }
                    else
                    {
                        double h = heard[0] + Math.Min(0.1, (now - (long)heard[1]) / Freq) * heard[2], mine = Counted(now), err = h - mine;
                        if (Math.Abs(err) < 0.3)
                        {
                            basePos = Math.Abs(err) > 0.04 ? h : mine + err * 0.2; // (smoothed: the reckoning steps a little each round of the sound thread)
                            heardPace = heard[2] > 0.05 && heard[2] < 20 ? heard[2] : -1;
                            baseTick = now;
                            HeardFollows++;
                        }
                        else if (heardPace > 0) { Rebase(now); heardPace = -1; } // (far apart, as right after a jump: the sound comes to the picture)
                    }
                    c = Counted(now);
                    play = playing; hold = holding; pace = speed; L = loopLen;
                    if (play && !hold && !(L > 0) && duration > 0 && c >= duration - 0.0005)
                    {
                        // The end (no Repeat): stands on its last picture, as Windows' player does.
                        Rebase(now);
                        playing = false;
                        ended = true;
                    }
                    target = play && !hold ? Counted(now + (long)(period * Freq)) : c;
                }
                var r = reader;
                if (r != null && swap != IntPtr.Zero)
                {
                    if (r.Failed) throw new Exception(r.Trouble ?? "the picture stopped");
                    r.Ask(c, L, pace);
                    int skipped = 0; double firstSkipped = 0;
                    var f = r.Opened ? r.Take(target + 0.0005, out skipped, out firstSkipped) : null;
                    // (After a jump or a start: the decoder's first picture may be a frame or two past the place asked for.)
                    if (f == null && r.Opened && (needFrame || hold)) f = r.TakeFirst(target + Math.Max(0.1, 4 * r.FrameSeconds) * Math.Max(1, pace));
                    if (f != null)
                    {
                        if (f.Tex != IntPtr.Zero) Comp.CopyAll(curTex, f.Tex);
                        else if (f.Data != IntPtr.Zero) Comp.Upload(curTex, f.Data, picW * 4);
                        IntPtr back = Comp.BackBuffer(swap);
                        if (back != IntPtr.Zero) { Comp.CopyAll(back, curTex); Marshal.Release(back); }
                        Comp.Present(swap);
                        shownClock = f.Clock;
                        shownFile = f.FileTime;
                        r.Give(f);
                        Presents++;
                        Skipped += skipped;
                        LastPresentTick = now;
                        if (play && !hold && target - shownClock > Math.Max(0.05, 2.5 * r.FrameSeconds) * Math.Max(1, pace)) Late++;
                        bool first = !hasPicture;
                        hasPicture = true;
                        needFrame = false;
                        if (hold) lock (clockGate) { if (holding) { holding = false; baseTick = now; if (f.Clock > basePos && f.Clock < basePos + 0.3) basePos = f.Clock; } } // (the clock goes on from the picture that is up)
                        var tr = Trace;
                        if (tr != null) lock (tr) if (tr.Count < 100000) tr.Add(new[] { (double)now, shownFile, shownClock });
                        if (first) Dispatcher.BeginInvoke(new Action(Unsettle));
                    }
                    else if (hold && (now - holdSince) / Freq > 0.6) lock (clockGate) { if (holding) { holding = false; baseTick = now; } } // (never stuck waiting)
                }
                else { needFrame = false; if (hold) lock (clockGate) holding = false; }
                lock (clockGate) stillNeeded = playing || needFrame || holding;
            }
            if (!stillNeeded) Presenter.Remove(this);
            if (ended) Dispatcher.BeginInvoke(new Action(delegate { var h = MediaEnded; if (h != null) h(this, EventArgs.Empty); }));
        }

        // The picture on screen as a WPF picture (posters, Copy frame, Save frame, tests).
        // (Taskbar and Alt+Tab previews, VideoBack: the picture shown now, where it lies in the window, in its pixels.)
        // scale: preview pixels per window pixel (1: the full-size preview).
        internal void DrawPreview(DrawingContext dc, double scale)
        {
            if (!attached || inner != null) return;
            BitmapSource b = null;
            double shown = picW * Math.Sqrt(lastM.M11 * lastM.M11 + lastM.M12 * lastM.M12) * scale; // (the picture's width in the preview)
            if (scale < 1 && shown < picW * 0.75) { try { b = SmallPicture(shown); } catch { b = null; } }
            if (b == null)
                lock (gpuGate)
                {
                    if (curTex == IntPtr.Zero || !hasPicture) return;
                    try { b = VideoConverter.GpuShow.Read(curTex, picW, picH, 87); } catch { return; }
                }
            if (b == null) return;
            dc.PushClip(new RectangleGeometry(lastClip));
            dc.PushTransform(new MatrixTransform(lastM));
            dc.DrawImage(b, new Rect(0, 0, picW, picH));
            dc.Pop(); dc.Pop();
        }

        // ---- the taskbar thumbnail's picture (1.31.0): made smaller on the graphics card (its mipmaps: halved again and again,
        // each step averaging 4 pixels, so it stays sharp), and only that small picture is read back, one refresh later, so
        // nothing ever waits for the card; the presenting thread's lock is never taken.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexFn(IntPtr self, IntPtr desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSrvFn(IntPtr self, IntPtr res, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, int dstSub, int x, int y, int z, IntPtr src, int srcSub, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GenMipsFn(IntPtr self, IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, int sub, int type, int flags, IntPtr mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, int sub);
        readonly object thumbGate = new object();
        IntPtr thumbTex, thumbView;
        readonly IntPtr[] thumbStage = new IntPtr[2];
        readonly bool[] thumbFilled = new bool[2];
        int thumbLevel = -1, thumbLW, thumbLH, thumbNext, thumbForW, thumbForH;
        BitmapSource thumbLast;

        static T Vt<T>(IntPtr obj, int slot) where T : class { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T; }

        static IntPtr TexDesc(int w, int h, int mips, int usage, int bind, int cpu, int misc)
        {
            IntPtr d = Marshal.AllocHGlobal(44);
            int[] v = { w, h, mips, 1, 87, 1, 0, usage, bind, cpu, misc }; // BGRA
            for (int i = 0; i < v.Length; i++) Marshal.WriteInt32(d, i * 4, v[i]);
            return d;
        }

        BitmapSource SmallPicture(double wantW)
        {
            IntPtr dev = VideoConverter.Gpu.Device, ctx = VideoConverter.Gpu.Context, src = curTex;
            if (dev == IntPtr.Zero || ctx == IntPtr.Zero || src == IntPtr.Zero || !hasPicture || wantW < 8) return null;
            lock (thumbGate)
            {
                // The smallest halving still at least as big as the preview needs (the last step is made in WPF, high quality).
                int level = 0, lw = picW, lh = picH;
                while (lw / 2 >= wantW && lh / 2 >= 8 && level < 6) { lw /= 2; lh /= 2; level++; }
                if (level == 0) return null;
                if (thumbTex == IntPtr.Zero || thumbForW != picW || thumbForH != picH || thumbLevel != level)
                {
                    ReleaseThumb();
                    IntPtr d = TexDesc(picW, picH, level + 1, 0, 0x8 | 0x20, 0, 0x1); // shader resource + render target, GENERATE_MIPS
                    try { if (Vt<CreateTexFn>(dev, 5)(dev, d, IntPtr.Zero, out thumbTex) < 0) { thumbTex = IntPtr.Zero; return null; } }
                    finally { Marshal.FreeHGlobal(d); }
                    if (Vt<CreateSrvFn>(dev, 7)(dev, thumbTex, IntPtr.Zero, out thumbView) < 0) { thumbView = IntPtr.Zero; ReleaseThumb(); return null; }
                    for (int k = 0; k < 2; k++)
                    {
                        d = TexDesc(lw, lh, 1, 3, 0, 0x20000, 0); // staging, CPU reads
                        try { if (Vt<CreateTexFn>(dev, 5)(dev, d, IntPtr.Zero, out thumbStage[k]) < 0) { thumbStage[k] = IntPtr.Zero; ReleaseThumb(); return null; } }
                        finally { Marshal.FreeHGlobal(d); }
                        thumbFilled[k] = false;
                    }
                    thumbForW = picW; thumbForH = picH; thumbLevel = level; thumbLW = lw; thumbLH = lh; thumbNext = 0;
                }
                // This refresh's picture goes to one staging texture; the other (filled last time) is read if the card is done.
                int now = thumbNext, prev = 1 - now;
                Vt<CopyRegionFn>(ctx, 46)(ctx, thumbTex, 0, 0, 0, 0, src, 0, IntPtr.Zero);
                Vt<GenMipsFn>(ctx, 54)(ctx, thumbView);
                Vt<CopyRegionFn>(ctx, 46)(ctx, thumbStage[now], 0, 0, 0, 0, thumbTex, level, IntPtr.Zero);
                thumbFilled[now] = true;
                thumbNext = prev;
                if (!thumbFilled[prev]) return thumbLast;
                IntPtr mapped = Marshal.AllocHGlobal(32);
                try
                {
                    if (Vt<MapFn>(ctx, 14)(ctx, thumbStage[prev], 0, 1, 0x100000, mapped) < 0) return thumbLast; // READ, DO_NOT_WAIT
                    try
                    {
                        IntPtr p = Marshal.ReadIntPtr(mapped);
                        int pitch = Marshal.ReadInt32(mapped, IntPtr.Size);
                        var b = BitmapSource.Create(thumbLW, thumbLH, 96, 96, PixelFormats.Bgr32, null, p, pitch * thumbLH, pitch);
                        b.Freeze();
                        thumbLast = b;
                    }
                    finally { Vt<UnmapFn>(ctx, 15)(ctx, thumbStage[prev], 0); }
                }
                finally { Marshal.FreeHGlobal(mapped); }
                return thumbLast;
            }
        }

        internal void ReleaseThumbPublic() { ReleaseThumb(); }
        void ReleaseThumb()
        {
            lock (thumbGate)
            {
                if (thumbView != IntPtr.Zero) { Marshal.Release(thumbView); thumbView = IntPtr.Zero; }
                if (thumbTex != IntPtr.Zero) { Marshal.Release(thumbTex); thumbTex = IntPtr.Zero; }
                for (int k = 0; k < 2; k++) { if (thumbStage[k] != IntPtr.Zero) { Marshal.Release(thumbStage[k]); thumbStage[k] = IntPtr.Zero; } thumbFilled[k] = false; }
                thumbLevel = -1; thumbLast = null;
            }
        }

        public BitmapSource Snapshot()
        {
            if (inner != null) return null;
            lock (gpuGate)
            {
                if (curTex == IntPtr.Zero || !hasPicture) return null;
                BitmapSource b;
                try { b = VideoConverter.GpuShow.Read(curTex, picW, picH, 87); } catch { return null; }
                if (b == null || turn == 0) return b;
                var tb = new TransformedBitmap(b, new RotateTransform(-turn)); // (as stored: the window turns it as shown)
                tb.Freeze();
                return tb;
            }
        }

        // ---------------------------------------------------------------- where it is on screen (the window's thread)
        bool attached, hooked, wantCheck;
        int calmFrames;
        Matrix lastM = new Matrix(0, 0, 0, 0, 0, 0);
        Rect lastClip = Rect.Empty;
        public Rect Hole = Rect.Empty;               // window units: where FastViewer's window is see-through for it
        DispatcherTimer watchTimer;
        System.ComponentModel.DependencyPropertyDescriptor zWatch, opWatch;

        // Something may have moved: placed again at every screen frame until it is still for half a second.
        public void Unsettle()
        {
            if (mode != 2 || outer == IntPtr.Zero) { if (attached) Detach(); return; }
            calmFrames = 0;
            if (!hooked) { CompositionTarget.Rendering += OnFrame; hooked = true; }
            if (watchTimer == null)
            {
                watchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                watchTimer.Tick += delegate
                {
                    if (mode != 2) { watchTimer.Stop(); return; }
                    Place();
                    CheckRest();
                    if (reader == null && !attached) watchTimer.Stop(); // (resting off screen: nothing to watch until Unsettle)
                };
            }
            if (!watchTimer.IsEnabled) watchTimer.Start();
            if (zWatch == null && Host != null)
            {
                zWatch = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Panel.ZIndexProperty, typeof(UIElement));
                zWatch.AddValueChanged(Host, delegate { if (attached && Panel.GetZIndex(Host) < 2) Detach(); Unsettle(); }); // (going behind the cover: off screen in the same frame)
                opWatch = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
                opWatch.AddValueChanged(Host, delegate { Unsettle(); });
                // (Zoom, pan, turns and slides are transforms on the slot's layers; the window and the stage may change size.
                // Only these move the video: anything else in the window that lays itself out again doesn't wake this up.)
                for (DependencyObject d = this; d != null && !(d is Window); d = VisualTreeHelper.GetParent(d))
                {
                    var u = d as UIElement;
                    if (u == null) continue;
                    foreach (var tf in new[] { u.RenderTransform, u is FrameworkElement ? ((FrameworkElement)u).LayoutTransform : null })
                        if (tf != null && !tf.IsFrozen) tf.Changed += delegate { Unsettle(); };
                    if (u == Host) break;
                }
                var win = Window.GetWindow(this);
                if (win != null) win.SizeChanged += delegate { Unsettle(); };
            }
        }

        public static int FrameCalls; // (tests: how often the window placed the video, per screen frame while unsettled)
        void OnFrame(object sender, EventArgs e)
        {
            FrameCalls++;
            if (Place()) calmFrames = 0;
            else if (++calmFrames > 30) { CompositionTarget.Rendering -= OnFrame; hooked = false; }
        }

        bool OnTop()
        {
            if (Host == null) return true;
            if (Panel.GetZIndex(Host) < 2) return false;
            double op = 1;
            for (DependencyObject d = this; d != null; d = VisualTreeHelper.GetParent(d))
            {
                var u = d as UIElement;
                if (u != null) op *= u.Opacity;
                if (d is Window) break;
            }
            return op > 0.5;
        }

        // Returns true when something changed.
        bool Place()
        {
            wantCheck = false;
            var win = Window.GetWindow(this);
            bool want = mode == 2 && outer != IntPtr.Zero && hasPicture && IsVisible && win != null && win.IsVisible && ActualWidth > 0.5 && ActualHeight > 0.5 && OnTop();
            if (!want) { if (attached) { Detach(); return true; } return false; }
            var src = PresentationSource.FromVisual(win);
            if (src == null || src.CompositionTarget == null) return false;
            Matrix dev = src.CompositionTarget.TransformToDevice;
            var gt = TransformToAncestor(win) as Transform;
            if (gt == null) return false;
            Matrix m = gt.Value;
            m.Append(dev);
            // The swap chain (picW x picH) onto this element's box, then to the window's pixels.
            var full = Matrix.Identity;
            if (turn != 0)
            {
                // (Turned back to the picture as stored; the element's box is the stored shape.)
                full.Rotate(-turn);
                var tr = new Rect(0, 0, picW, picH);
                tr.Transform(full);
                full.Translate(-tr.Left, -tr.Top);
                full.Scale(ActualWidth / tr.Width, ActualHeight / tr.Height);
            }
            else full = new Matrix(ActualWidth / picW, 0, 0, ActualHeight / picH, 0, 0);
            full.Append(m);
            Rect bounds = new Rect(0, 0, picW, picH);
            bounds.Transform(full);
            bool square = (Math.Abs(full.M12) < 1e-6 && Math.Abs(full.M21) < 1e-6) || (Math.Abs(full.M11) < 1e-6 && Math.Abs(full.M22) < 1e-6);
            if (square && bounds.Width > 0 && bounds.Height > 0)
            {
                // (Turned by quarters: snapped to whole pixels, so the hole in the window and the picture meet exactly.)
                var snapped = new Rect(new Point(Math.Round(bounds.Left), Math.Round(bounds.Top)), new Point(Math.Round(bounds.Right), Math.Round(bounds.Bottom)));
                full.Translate(-bounds.Left, -bounds.Top);
                full.Scale(snapped.Width / bounds.Width, snapped.Height / bounds.Height);
                full.Translate(snapped.Left, snapped.Top);
                bounds = snapped;
            }
            Rect clip = new Rect(0, 0, 1e5, 1e5);
            var back = VideoBack.For(win);
            var stage = back.ClipTo;
            if (stage != null && stage.IsVisible)
            {
                var st = stage.TransformToAncestor(win) as Transform;
                if (st != null)
                {
                    Matrix sm = st.Value; sm.Append(dev);
                    var sr = new Rect(0, 0, stage.ActualWidth, stage.ActualHeight);
                    sr.Transform(sm);
                    clip = new Rect(new Point(Math.Round(sr.Left), Math.Round(sr.Top)), new Point(Math.Round(sr.Right), Math.Round(sr.Bottom)));
                }
            }
            Rect vis = Rect.Intersect(bounds, clip);
            if (vis.IsEmpty || vis.Width < 1 || vis.Height < 1) { if (attached) { Detach(); return true; } return false; }
            bool changed = !attached || full != lastM || clip != lastClip;
            if (!changed) return false;
            lastM = full; lastClip = clip;
            Comp.Matrix(innerVis, full);
            Comp.Clip(outer, clip);
            if (!attached) { if (!back.Attach(this, outer)) return false; attached = true; Note("own player shows " + Path.GetFileName(File)); }
            else Comp.Commit();
            Matrix inv = dev; inv.Invert();
            vis.Transform(inv);
            Hole = vis;
            back.UpdateHoles();
            back.Sync();
            return true;
        }

        void Detach()
        {
            attached = false;
            Hole = Rect.Empty;
            lastM = new Matrix(0, 0, 0, 0, 0, 0);
            var win = Window.GetWindow(this);
            if (win != null) VideoBack.For(win).Detach(this, outer);
        }

        static void Note(string what) { var l = LogLine; if (l != null) l(what); }
    }

    partial class ViewerWindow
    {
        internal static bool playerSwitchFixed;   // (tests: --player decides, the settings file doesn't)
        internal bool videoUp;                    // the own player's video window is up behind this one
        internal bool videoNoFrame;               // (1.31.0 fix: while it is up the window has a plain region: no Windows frame behind the video)

        // Settings > Videos > FastViewer's own video player switched: every video and song is opened again by the player
        // now chosen, the one on screen at the same place.
        void ReloadPlayers()
        {
            if (shown != null && shown.IsVideo) RememberPosition(shown);
            foreach (var s in slots)
            {
                if (s == null || !s.IsVideo) continue;
                s.Token++;
                try { s.Video.Stop(); s.Video.Source = null; } catch { }
                s.File = null;
                s.Ready = false;
            }
            ApplyWindowLook();
            if (files.Count > 0) ShowIndex(index, 1);
        }

        void SetUpVideoBack()
        {
            VideoBack.Cloak = testRun && !testInFront;
            var back = VideoBack.For(this);
            back.ClipTo = stage;
            back.Holes = ApplyVideoHoles;
            back.Showing = delegate(bool up) { videoUp = up; ApplyVideoBlur(); };
            back.Frame = delegate(bool off) { videoNoFrame = off; RoundCorners(); };
            OwnVideo.LogLine = Log;
            OwnVideo.HeardClock = delegate(string f)
            {
                var p = soundPlayer;
                double at, pace, len; long st;
                if (p == null || f == null || !p.Sounding || !p.Clock(f, out at, out st, out pace, out len)) return null;
                return new[] { at, (double)st, pace };
            };
        }

        // Glass: Windows blurs what is behind a window, so while the video window is up the blur is its job (the video
        // itself must stay sharp, and FastViewer's window would blur it).
        void ApplyVideoBlur()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            bool glass = seeThrough && !fullscreen && !mini;
            var back = VideoBack.For(this);
            back.Blur = glass;
            if (snapDir == null || !glass) Looks.SetBlurBehind(hwnd, glass && !videoUp);
            back.Sync();
        }

        // ---- the hole: FastViewer's window is see-through where the own player's video is (it lies behind)
        readonly Dictionary<FrameworkElement, Brush> holeMade = new Dictionary<FrameworkElement, Brush>(), holeBase = new Dictionary<FrameworkElement, Brush>();
        readonly Dictionary<FrameworkElement, object> holeSaved = new Dictionary<FrameworkElement, object>();
        internal List<Rect> videoHoles = new List<Rect>();

        void ApplyVideoHoles(List<Rect> holes)
        {
            videoHoles = holes;
            foreach (var e in new FrameworkElement[] { this, root, stage, cover }) if (e != null) HoleIn(e, holes);
            ClipHiddenSlots();
        }

        // The slots waiting behind the cover (the files ahead and behind) are drawn there all the time, so they are ready at
        // once; the hole would show them over the video: they are cut out of it.
        readonly HashSet<UIElement> clipWatched = new HashSet<UIElement>();
        readonly Dictionary<UIElement, Geometry> clipMade = new Dictionary<UIElement, Geometry>();

        void ClipHiddenSlots()
        {
            if (slots == null) return;
            var holes = videoHoles;
            foreach (var s in slots)
            {
                if (s == null) continue;
                var h = s.Host;
                if (clipWatched.Add(h))
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Panel.ZIndexProperty, typeof(UIElement)).AddValueChanged(h, delegate { ClipHiddenSlots(); });
                Geometry made;
                clipMade.TryGetValue(h, out made);
                bool cut = holes.Count > 0 && Panel.GetZIndex(h) < 2 && h.IsVisible && h.ActualWidth > 0;
                if (!cut) { if (made != null && h.Clip == made) h.Clip = null; clipMade.Remove(h); continue; }
                if (h.Clip != null && h.Clip != made) continue; // (someone else's clip: left alone)
                GeneralTransform t;
                try { t = TransformToDescendant(h); } catch { continue; }
                if (t == null) continue;
                var group = new GeometryGroup();
                foreach (var r in holes) group.Children.Add(new RectangleGeometry(t.TransformBounds(r)));
                var geo = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-1e5, -1e5, 2e5, 2e5)), group);
                geo.Freeze();
                h.Clip = geo;
                clipMade[h] = geo;
            }
        }

        static DependencyProperty BackgroundOf(FrameworkElement e)
        {
            if (e is Control) return Control.BackgroundProperty;
            if (e is Panel) return Panel.BackgroundProperty;
            if (e is Border) return Border.BackgroundProperty;
            return null;
        }

        void HoleIn(FrameworkElement e, List<Rect> holes)
        {
            var dp = BackgroundOf(e);
            if (dp == null) return;
            Brush made;
            holeMade.TryGetValue(e, out made);
            var cur = e.GetValue(dp) as Brush;
            if (made != null && cur != made) { holeMade.Remove(e); holeSaved.Remove(e); holeBase.Remove(e); made = null; } // (the look set a new background meanwhile)
            if (holes.Count == 0) { if (made != null) RestoreBackground(e, dp); return; }
            if (made == null) { holeSaved[e] = e.ReadLocalValue(dp); holeBase[e] = cur; }
            var b = holeBase[e];
            var solid = b as SolidColorBrush;
            if (b == null || (solid != null && solid.Color.A == 0)) return; // (nothing drawn: nothing to cut)
            double w = e.ActualWidth, h = e.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var bounds = new Rect(0, 0, w, h);
            GeneralTransform t = e == this ? null : TransformToDescendant(e);
            var group = new GeometryGroup();
            foreach (var r in holes) group.Children.Add(new RectangleGeometry(t == null ? r : t.TransformBounds(r)));
            var geo = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), group);
            var brush = new DrawingBrush(new GeometryDrawing(b, null, geo))
            {
                Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds
            };
            e.SetValue(dp, brush);
            holeMade[e] = brush;
        }

        void RestoreBackground(FrameworkElement e, DependencyProperty dp)
        {
            object saved;
            holeSaved.TryGetValue(e, out saved);
            holeMade.Remove(e); holeSaved.Remove(e); holeBase.Remove(e);
            if (saved == null || saved == DependencyProperty.UnsetValue) { e.ClearValue(dp); return; }
            if (saved is Expression)
            {
                // (A DynamicResource: set again by its key.)
                var p = saved.GetType().GetProperty("ResourceKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (p != null) { e.SetResourceReference(dp, p.GetValue(saved, null)); return; }
            }
            e.SetValue(dp, saved);
        }
    }
}

namespace FastViewer
{
    // (1.31.0) Windows' taskbar list: adds a window's button directly (ITaskbarList.AddTab).
    static class TaskbarTab
    {
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList { void HrInit(); void AddTab(System.IntPtr hwnd); void DeleteTab(System.IntPtr hwnd); void ActivateTab(System.IntPtr hwnd); void SetActiveAlt(System.IntPtr hwnd); }
        static ITaskbarList list;
        public static void Add(System.IntPtr hwnd)
        {
            try
            {
                if (list == null) { list = (ITaskbarList)System.Activator.CreateInstance(System.Type.GetTypeFromCLSID(new System.Guid("56FDF344-FD6D-11d0-958A-006097C9A090"))); list.HrInit(); }
                list.AddTab(hwnd);
            }
            catch { }
        }
    }
}
