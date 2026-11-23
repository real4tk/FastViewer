// FastViewer: decoding a video's pictures on the graphics card (Media Foundation's source reader with a D3D11 device),
// ahead of a clock and on into the next loop with Repeat. Made in 1.30.0 for an experiment that drew these pictures over
// Windows' player; since 1.31.0 it is the picture half of FastViewer's own player (Player.cs), and the experiment is gone.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FastViewer
{
    // One decoded picture (BGRA in unmanaged memory) and where it belongs on the sound's clock.
    sealed class VideoFrame
    {
        public IntPtr Data;
        public IntPtr Tex;      // 1.30.0 round 2: the picture in a texture on the graphics card (else in Data)
        public double Clock;    // on the sound's clock (counted on across loops: the file's time + the loops before)
        public double FileTime; // in the file
    }

    static partial class VideoConverter
    {
        // One D3D11 device for decoding on the graphics card, shared by every frame reader (made once, on first use).
        internal static class Gpu
        {
            static readonly object gate = new object();
            static IntPtr manager;
            static bool tried;
            public static string Trouble;
            public static IntPtr Device, Context; // (the D3D11 device and its immediate context, kept)

            public static int MakeMs = -1; // (tests)
            public static IntPtr Manager { get { lock (gate) { if (!tried) { tried = true; var mw = Stopwatch.StartNew(); try { Make(); } catch (Exception ex) { Trouble = ex.Message; } MakeMs = (int)mw.ElapsedMilliseconds; } return manager; } } }

            [DllImport("d3d11.dll")]
            static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, int flags, IntPtr levels, int count, int sdk, out IntPtr device, out int level, out IntPtr context);
            [DllImport("mfplat.dll")] static extern int MFCreateDXGIDeviceManager(out int token, out IntPtr manager);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResetDeviceFn(IntPtr self, IntPtr device, int token);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetProtectedFn(IntPtr self, int on);

            // (Wrappers made once per function and kept: making one for every call cost processor time at every picture.)
            static readonly Dictionary<IntPtr, Delegate> calls = new Dictionary<IntPtr, Delegate>();
            internal static T Call<T>(IntPtr obj, int slot) where T : class
            {
                IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
                Delegate d;
                lock (calls)
                {
                    if (!calls.TryGetValue(fn, out d) || !(d is T)) { d = Marshal.GetDelegateForFunctionPointer(fn, typeof(T)); calls[fn] = d; }
                }
                return d as T;
            }

            static T Method<T>(IntPtr obj, int slot) where T : class
            {
                IntPtr table = Marshal.ReadIntPtr(obj);
                return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T)) as T;
            }

            static void Make()
            {
                IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
                try
                {
                    int level;
                    // Hardware, with video support (decoding, the video processor) and BGRA.
                    int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800 | 0x20, IntPtr.Zero, 0, 7, out device, out level, out context);
                    if (hr < 0) { Trouble = "no D3D11 video device (0x" + hr.ToString("X8") + ")"; return; }
                    // Media Foundation uses the device from its own threads.
                    Guid iid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"); // ID3D10Multithread
                    IntPtr mt;
                    if (Marshal.QueryInterface(device, ref iid, out mt) >= 0) { Method<SetProtectedFn>(mt, 5)(mt, 1); Marshal.Release(mt); }
                    int token;
                    IntPtr m;
                    hr = MFCreateDXGIDeviceManager(out token, out m);
                    if (hr < 0) { Trouble = "no DXGI device manager (0x" + hr.ToString("X8") + ")"; return; }
                    hr = Method<ResetDeviceFn>(m, 7)(m, device, token); // IMFDXGIDeviceManager::ResetDevice
                    if (hr < 0) { Marshal.Release(m); Trouble = "the device manager refused the device (0x" + hr.ToString("X8") + ")"; return; }
                    manager = m;
                    Device = device; Context = context; device = IntPtr.Zero; context = IntPtr.Zero; // (kept: pictures are copied off the card with them)
                }
                finally
                {
                    if (context != IntPtr.Zero) Marshal.Release(context);
                    if (device != IntPtr.Zero) Marshal.Release(device); // (the manager keeps its own reference)
                }
            }
        }

        // 1.30.0 round 2: the pictures stay on the graphics card all the way. The reader copies each decoded picture into a
        // texture of its own pool (a copy on the card); to show one it is copied into a texture shared with a Direct3D 9Ex
        // surface that WPF draws itself (D3DImage). No picture passes through the processor's memory any more (before:
        // card -> memory -> WPF -> card again, 8 MB per picture, 60-120 times a second).
        internal static class GpuShow
        {
            [DllImport("d3d9.dll")] static extern int Direct3DCreate9Ex(int sdk, out IntPtr d3d);
            [StructLayout(LayoutKind.Sequential)]
            struct PresentParams
            {
                public int BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount, MultiSampleType, MultiSampleQuality, SwapEffect;
                public IntPtr DeviceWindow;
                public int Windowed, EnableAutoDepthStencil, AutoDepthStencilFormat, Flags, RefreshRate, PresentationInterval;
            }
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDeviceExFn(IntPtr self, int adapter, int type, IntPtr focus, int flags, ref PresentParams pp, IntPtr mode, out IntPtr device);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture9Fn(IntPtr self, int w, int h, int levels, int usage, int format, int pool, out IntPtr tex, ref IntPtr shared);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSurfaceLevelFn(IntPtr self, int level, out IntPtr surface);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTex11Fn(IntPtr self, IntPtr desc, IntPtr init, out IntPtr tex);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSharedHandleFn(IntPtr self, out IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, int dstSub, int x, int y, int z, IntPtr src, int srcSub, IntPtr box);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FlushFn(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateQueryFn(IntPtr self, IntPtr desc, out IntPtr query);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void EndFn(IntPtr self, IntPtr async);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDataFn(IntPtr self, IntPtr async, IntPtr data, int size, int flags);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, int sub, int type, int flags, IntPtr mapped);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, int sub);
            static T Fn<T>(IntPtr obj, int slot) where T : class { return Gpu.Call<T>(obj, slot); }

            static readonly object gate = new object();
            static IntPtr d3d9, device9, query;
            static bool tried;
            public static string Trouble;
            public static bool Off;  // (tests: --frames-cpu-show, the old way through memory)

            // The Direct3D 9Ex device WPF's surfaces are made with (once, any thread).
            public static bool Ready(IntPtr hwnd)
            {
                lock (gate)
                {
                    if (Off) return false;
                    if (tried) return device9 != IntPtr.Zero;
                    tried = true;
                    try
                    {
                        if (Gpu.Manager == IntPtr.Zero || Gpu.Device == IntPtr.Zero) { Trouble = "no D3D11 device"; return false; }
                        if (Direct3DCreate9Ex(32, out d3d9) < 0) { Trouble = "no Direct3D 9Ex"; return false; }
                        var pp = new PresentParams { BackBufferWidth = 1, BackBufferHeight = 1, BackBufferCount = 1, SwapEffect = 1, DeviceWindow = hwnd, Windowed = 1 };
                        // HAL, hardware vertex processing + multithreaded + keep the FPU as it is
                        if (Fn<CreateDeviceExFn>(d3d9, 20)(d3d9, 0, 1, hwnd, 0x40 | 0x4 | 0x2, ref pp, IntPtr.Zero, out device9) < 0) { device9 = IntPtr.Zero; Trouble = "no Direct3D 9Ex device"; return false; }
                        IntPtr desc = Marshal.AllocHGlobal(8);
                        Marshal.WriteInt32(desc, 0, 0); Marshal.WriteInt32(desc, 4, 0); // D3D11_QUERY_EVENT
                        if (Fn<CreateQueryFn>(Gpu.Device, 24)(Gpu.Device, desc, out query) < 0) query = IntPtr.Zero;
                        Marshal.FreeHGlobal(desc);
                        return true;
                    }
                    catch (Exception ex) { Trouble = ex.Message; device9 = IntPtr.Zero; return false; }
                }
            }

            static IntPtr Desc(int w, int h, int format, int usage, int bind, int cpu, int misc)
            {
                IntPtr d = Marshal.AllocHGlobal(44);
                int[] v = { w, h, 1, 1, format, 1, 0, usage, bind, cpu, misc };
                for (int i = 0; i < v.Length; i++) Marshal.WriteInt32(d, i * 4, v[i]);
                return d;
            }

            // A texture of a reader's pool (on the card).
            public static IntPtr PoolTexture(int w, int h, int format)
            {
                IntPtr d = Desc(w, h, format, 0, 0x8, 0, 0), tex;
                try { return Fn<CreateTex11Fn>(Gpu.Device, 5)(Gpu.Device, d, IntPtr.Zero, out tex) < 0 ? IntPtr.Zero : tex; }
                finally { Marshal.FreeHGlobal(d); }
            }

            // Copies a region of one texture into another (both on the card).
            public static void Copy(IntPtr dst, IntPtr src, int srcSub, int w, int h)
            {
                IntPtr box = Marshal.AllocHGlobal(24);
                try
                {
                    int[] b = { 0, 0, 0, w, h, 1 };
                    for (int i = 0; i < 6; i++) Marshal.WriteInt32(box, i * 4, b[i]);
                    Fn<CopyRegionFn>(Gpu.Context, 46)(Gpu.Context, dst, 0, 0, 0, 0, src, srcSub, box);
                }
                finally { Marshal.FreeHGlobal(box); }
            }

            // Everything asked of the card so far is done (WPF's Direct3D 9 must not read the shared texture before).
            public static void Finish()
            {
                Fn<FlushFn>(Gpu.Context, 111)(Gpu.Context);
                if (query == IntPtr.Zero) return;
                Fn<EndFn>(Gpu.Context, 28)(Gpu.Context, query);
                var watch = Stopwatch.StartNew();
                while (Fn<GetDataFn>(Gpu.Context, 29)(Gpu.Context, query, IntPtr.Zero, 0, 0) == 1 && watch.ElapsedMilliseconds < 50) Thread.SpinWait(50);
            }

            // A D3D11 texture shared with a Direct3D 9 surface for D3DImage. format: DXGI 87 (BGRA) or 88 (BGRX).
            public static bool Shared(int w, int h, int format, out IntPtr tex11, out IntPtr tex9, out IntPtr surface9)
            {
                tex11 = tex9 = surface9 = IntPtr.Zero;
                int fmt9 = format == 87 ? 21 : format == 88 ? 22 : 0; // D3DFMT_A8R8G8B8 / X8R8G8B8
                if (fmt9 == 0 || device9 == IntPtr.Zero) return false;
                IntPtr d = Desc(w, h, format, 0, 0x20 | 0x8, 0, 0x2); // render target + shader resource, shared
                try
                {
                    if (Fn<CreateTex11Fn>(Gpu.Device, 5)(Gpu.Device, d, IntPtr.Zero, out tex11) < 0) { tex11 = IntPtr.Zero; return false; }
                    Guid iid = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b"); // IDXGIResource
                    IntPtr res, handle;
                    if (Marshal.QueryInterface(tex11, ref iid, out res) < 0) return Fail(ref tex11);
                    int hr = Fn<GetSharedHandleFn>(res, 8)(res, out handle);
                    Marshal.Release(res);
                    if (hr < 0 || handle == IntPtr.Zero) return Fail(ref tex11);
                    if (Fn<CreateTexture9Fn>(device9, 23)(device9, w, h, 1, 1, fmt9, 0, out tex9, ref handle) < 0) { tex9 = IntPtr.Zero; return Fail(ref tex11); }
                    if (Fn<GetSurfaceLevelFn>(tex9, 18)(tex9, 0, out surface9) < 0) { Marshal.Release(tex9); tex9 = IntPtr.Zero; surface9 = IntPtr.Zero; return Fail(ref tex11); }
                    return true;
                }
                finally { Marshal.FreeHGlobal(d); }
            }

            static bool Fail(ref IntPtr tex11) { if (tex11 != IntPtr.Zero) Marshal.Release(tex11); tex11 = IntPtr.Zero; return false; }

            // The picture in a texture as a WPF picture (Copy frame, Save frame): through a staging texture, once.
            public static BitmapSource Read(IntPtr tex, int w, int h, int format, bool keepAlpha = false)
            {
                IntPtr d = Desc(w, h, format, 3, 0, 0x20000, 0), staging, mapped = Marshal.AllocHGlobal(32);
                try
                {
                    if (Fn<CreateTex11Fn>(Gpu.Device, 5)(Gpu.Device, d, IntPtr.Zero, out staging) < 0) return null;
                    try
                    {
                        Fn<CopyResourceFn>(Gpu.Context, 47)(Gpu.Context, staging, tex);
                        if (Fn<MapFn>(Gpu.Context, 14)(Gpu.Context, staging, 0, 1, 0, mapped) < 0) return null;
                        try
                        {
                            IntPtr p = Marshal.ReadIntPtr(mapped);
                            int pitch = Marshal.ReadInt32(mapped, IntPtr.Size);
                            var bmp = BitmapSource.Create(w, h, 96, 96, keepAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, p, pitch * h, pitch);
                            bmp.Freeze();
                            return bmp;
                        }
                        finally { Fn<UnmapFn>(Gpu.Context, 15)(Gpu.Context, staging, 0); }
                    }
                    finally { Marshal.Release(staging); }
                }
                finally { Marshal.FreeHGlobal(d); Marshal.FreeHGlobal(mapped); }
            }
        }

        // Decodes a video's pictures ahead of the clock it is asked for, on its own thread, into a small pool of frames.
        // With Repeat on it reads straight on from the end into the start (the sound's loop length), so the next loop's
        // first pictures are ready before the old loop ends.
        internal sealed class FrameReader
        {
            public readonly string File;
            public volatile bool Opened, Failed;
            public volatile string Trouble;
            public int Width, Height;        // of the frames handed out
            public bool Scaled;              // smaller than the file's own picture (zooming in shows Windows' player)
            public double FrameSeconds = 1 / 30.0;
            // (Round 3: the frame rate as measured from the pictures' own times: the median gap of the last 60. Files with a
            // varying rate, like Medal's, give a header rate that says little.)
            public volatile float MeasuredFps;
            readonly double[] gaps = new double[60];
            int gapAt, gapCount;
            double lastPts = double.NaN;
            public volatile int OpenMs, Seeks, Wraps, Decoded, Kept, LongestWrapMs;
            int Pool = 6;   // (round 2: was 10; 6 still keeps 5 pictures ready ahead. 1.31.0, own player: 8 up to 1080p, 5 above)
            public readonly bool Own;        // 1.31.0: for FastViewer's own player (Player.cs): pictures go to a swap chain, not to WPF
            public int NativeWidth, NativeHeight;
            public int Turn;              // (1.31.0) the file's "turn me" tag, already applied to the pictures handed out (0, 90, 180, 270)
            volatile bool forceJump;
            public void Jump() { forceJump = true; wake.Set(); } // (1.31.0: read from the clock asked for, even for a small step back)
            readonly int maxW, maxH;
            readonly object gate = new object();
            readonly List<VideoFrame> ready = new List<VideoFrame>(); // in clock order
            readonly Stack<VideoFrame> spare = new Stack<VideoFrame>();
            readonly List<IntPtr> allocated = new List<IntPtr>();
            readonly List<IntPtr> textures = new List<IntPtr>();
            readonly IntPtr hwnd;
            public volatile bool OnGpu;   // the pictures stay on the graphics card (decided at the first one)
            public int TexFormat;
            bool decided;
            double askClock = double.NaN, askLoop, askPace = 1;
            readonly AutoResetEvent wake = new AutoResetEvent(false);
            volatile bool quit;
            bool ended;

            public FrameReader(string file, int maxWidth, int maxHeight, IntPtr window, bool own = false)
            {
                File = file;
                Own = own;
                hwnd = window;
                maxW = maxWidth;
                maxH = maxHeight;
                var t = new Thread(Run) { IsBackground = true, Name = "Frames", Priority = ThreadPriority.AboveNormal };
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
            }

            // Where the picture should be now (the sound's clock), Repeat's loop length (0: none) and the pace.
            public void Ask(double clock, double loopLen, double pace)
            {
                bool news;
                lock (gate)
                {
                    // (1.31.0: asked at every screen refresh; the decoding thread is woken only for news. It is woken anyway
                    // whenever a picture is taken, which is what lets it decode the next.)
                    news = !Own || double.IsNaN(askClock) || Math.Abs(loopLen - askLoop) > 0.0005 || Math.Abs(pace - askPace) > 0.0005 || clock < askClock - 0.05 || clock > askClock + 0.2;
                    askClock = clock; askLoop = loopLen; askPace = pace;
                }
                if (news) wake.Set();
            }

            // The newest frame at or before `clock`, or null when there is none new; the window copies it and gives it back.
            public VideoFrame Take(double clock) { int skipped; double first; return Take(clock, out skipped, out first); }

            // (1.30.0 round 3: also how many frames were passed over unseen, and the first of them, for the stats.)
            public VideoFrame Take(double clock, out int skipped, out double firstSkipped)
            {
                VideoFrame f = null;
                skipped = 0; firstSkipped = double.NaN;
                lock (gate)
                {
                    int i = -1;
                    for (int j = 0; j < ready.Count; j++) { if (ready[j].Clock <= clock) i = j; else break; }
                    if (i >= 0)
                    {
                        f = ready[i];
                        skipped = i;
                        if (i > 0) firstSkipped = ready[0].FileTime;
                        for (int j = 0; j < i; j++) spare.Push(ready[j]);
                        ready.RemoveRange(0, i + 1);
                    }
                }
                if (f != null) wake.Set();
                return f;
            }

            // 1.31.0: after a jump Windows' decoder often hands out the first picture a frame or two after the place asked
            // for: that one, when it is no later than `limit`.
            public VideoFrame TakeFirst(double limit)
            {
                VideoFrame f = null;
                lock (gate) { if (ready.Count > 0 && ready[0].Clock <= limit) { f = ready[0]; ready.RemoveAt(0); } }
                if (f != null) wake.Set();
                return f;
            }

            public void Give(VideoFrame f) { if (f == null) return; lock (gate) spare.Push(f); wake.Set(); }
            public void PutBack(VideoFrame f) { if (f == null) return; lock (gate) ready.Insert(0, f); }

            // The clock of the next frame waiting (tests), NaN when none.
            public double NextClock { get { lock (gate) return ready.Count > 0 ? ready[0].Clock : double.NaN; } }
            public int Waiting { get { lock (gate) return ready.Count; } }
            public bool Ended { get { return ended; } }

            public void Quit() { quit = true; wake.Set(); }

            [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void Copy(IntPtr to, IntPtr from, IntPtr bytes);
            static Guid Argb32 = new Guid("00000015-0000-0010-8000-00AA00389B71"); // MFVideoFormat_ARGB32
            static Guid D3DManagerKey = new Guid("ec822da2-e1e9-4b29-a0d8-563c719f5269"); // MF_SOURCE_READER_D3D_MANAGER

            IMFSourceReader cur; // (replaced when Windows refuses a jump: the file is opened again)
            public volatile int Reopens;
            public double Length; // of the file, seconds (0: unknown)
            public double Shift;  // seconds Windows' reader times the picture late (an edit list it ignored), taken off
            bool startOfLoop;
            public readonly List<string> Recent = new List<string>(); // tests: what was decoded lately
            public static bool Tracing; // (tests)
            public static bool NoGpu; // (tests: --frames-cpu, decoding without the graphics card)
            public static bool TestNoAdvanced; public static int TestPool; // (1.31.0 tests: memory probes)
            void Note(string what) { if (!Tracing) return; lock (Recent) { Recent.Add(what); if (Recent.Count > 60) Recent.RemoveAt(0); } }

            void Run()
            {
                ThreadIds.Note("frames");
                var watch = Stopwatch.StartNew();
                try
                {
                    MFStartup(0x00020070, 0);
                    cur = OpenReader(true);
                    OpenMs = (int)watch.ElapsedMilliseconds;
                    Opened = true;
                    Loop();
                }
                catch (Exception ex) { Trouble = ex.Message; Failed = true; }
                finally
                {
                    if (cur != null) try { Marshal.ReleaseComObject(cur); } catch { }
                    if (staging != IntPtr.Zero) { try { Marshal.Release(staging); } catch { } staging = IntPtr.Zero; }
                    lock (gate)
                    {
                        ready.Clear();
                        spare.Clear();
                        foreach (var p in allocated) Marshal.FreeHGlobal(p); // (the window only holds a frame while copying it, on its thread, before Quit)
                        foreach (var t in textures) try { Marshal.Release(t); } catch { }
                        textures.Clear();
                        allocated.Clear();
                    }
                }
            }

            IMFSourceReader OpenReader(bool first)
            {
                IMFSourceReader r = null;
                try
                {
                    IntPtr manager = NoGpu ? IntPtr.Zero : Gpu.Manager;
                    if (manager == IntPtr.Zero && !NoGpu) throw new Exception(Gpu.Trouble ?? "no graphics card decoding");
                    IMFAttributes ra;
                    Check(MFCreateAttributes(out ra, 3), "setting up");
                    if (manager != IntPtr.Zero) ra.SetUnknown(ref D3DManagerKey, manager);
                    if (!TestNoAdvanced) ra.SetUINT32(ref G.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                    ra.SetUINT32(ref G.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                    Check(MFCreateSourceReaderFromURL(File, ra, out r), "opening the video");
                    Marshal.ReleaseComObject(ra);
                    r.SetStreamSelection(ALL_STREAMS, false);
                    Check(r.SetStreamSelection(FIRST_VIDEO, true), "finding the picture");
                    IMFMediaType native;
                    Check(r.GetNativeMediaType(FIRST_VIDEO, 0, out native), "reading the picture's format");
                    long packed;
                    Check(native.GetUINT64(ref G.MF_MT_FRAME_SIZE, out packed), "reading the picture's size");
                    int nw = (int)(packed >> 32), nh = (int)(packed & 0xFFFFFFFF);
                    NativeWidth = nw; NativeHeight = nh;
                    int nativeTurn;
                    if (native.GetUINT32(ref G.MF_MT_VIDEO_ROTATION, out nativeTurn) < 0) nativeTurn = 0;
                    long rate;
                    if (native.GetUINT64(ref G.MF_MT_FRAME_RATE, out rate) >= 0 && (rate & 0xFFFFFFFF) > 0 && (rate >> 32) > 0)
                        FrameSeconds = Math.Max(1 / 240.0, Math.Min(1, (double)(rate & 0xFFFFFFFF) / (rate >> 32)));
                    Marshal.ReleaseComObject(native);
                    // Pictures bigger than the screen are made screen-sized on the graphics card (copying 4K pictures out
                    // 60 times a second costs too much); smaller ones keep their own size.
                    double longSide = Math.Max(maxW, maxH), shortSide = Math.Min(maxW, maxH);
                    double fit = Math.Min(1.0, Math.Min(longSide / Math.Max(nw, nh), shortSide / Math.Min(nw, nh)));
                    int ow = Math.Max(2, (int)Math.Round(nw * fit / 2) * 2), oh = Math.Max(2, (int)Math.Round(nh * fit / 2) * 2);
                    // (1.31.0 fix: Windows' picture processing always turns the picture by the file's "turn me" tag (Rotate saves one,
                    // phones write one) and squeezes it into the asked size, so a turned file is asked for in its turned size; the
                    // own player turns it back (Turn) before the window turns it as shown (rotations), so it isn't turned twice.)
                    nativeTurn = ((nativeTurn % 360) + 360) % 360;
                    if (nativeTurn != 0 && nativeTurn != 90 && nativeTurn != 180 && nativeTurn != 270) throw new Exception("the picture is turned by " + nativeTurn + " degrees");
                    Turn = nativeTurn;
                    if (nativeTurn == 90 || nativeTurn == 270) { int t = ow; ow = oh; oh = t; }
                    IMFMediaType want;
                    MFCreateMediaType(out want);
                    want.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Video);
                    // (Round 3: ARGB, so the 4th byte is filled in as fully opaque. With RGB32 it was 0, and WPF drew the picture
                    // see-through on a real screen: washed out, no deep blacks, the grey behind showing through.)
                    if (TestNoAdvanced) { var nv = new Guid("3231564E-0000-0010-8000-00AA00389B71"); want.SetGUID(ref G.MF_MT_SUBTYPE, ref nv); } else want.SetGUID(ref G.MF_MT_SUBTYPE, ref Argb32);
                    want.SetUINT64(ref G.MF_MT_FRAME_SIZE, ((long)ow << 32) | (uint)oh);
                    if (r.SetCurrentMediaType(FIRST_VIDEO, IntPtr.Zero, want) < 0)
                    {
                        want.DeleteItem(ref G.MF_MT_FRAME_SIZE);
                        if (r.SetCurrentMediaType(FIRST_VIDEO, IntPtr.Zero, want) < 0)
                        {
                            want.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFVideoFormat_RGB32);
                            want.SetUINT64(ref G.MF_MT_FRAME_SIZE, ((long)ow << 32) | (uint)oh);
                            if (r.SetCurrentMediaType(FIRST_VIDEO, IntPtr.Zero, want) < 0) { want.DeleteItem(ref G.MF_MT_FRAME_SIZE); Check(r.SetCurrentMediaType(FIRST_VIDEO, IntPtr.Zero, want), "decoding the picture"); }
                        }
                    }
                    Marshal.ReleaseComObject(want);
                    ReadShape(r);
                    if (first && Own) Pool = TestPool > 0 ? TestPool : Width * Height <= 2200000 ? 8 : 5;
                    // (The picture must come out in the asked shape, else the turn is unknown: such files stay with Windows' player.)
                    bool quarter = nativeTurn == 90 || nativeTurn == 270;
                    if ((Width > Height) != (quarter ? nh > nw : nw > nh) && nw != nh) throw new Exception("the picture came out turned");
                    if (first) Scaled = Math.Max(Width, Height) < Math.Max(nw, nh) - 2;
                    if (first) Length = Duration(r) / 1e7;
                    if (first)
                    {
                        // B-frame files made by x264/ffmpeg start their picture with an edit list. Windows' MP4 reader ignores
                        // it for the picture: its times run that much late (2 frames) and it leaves out the first frames. When the
                        // first frame it hands out is that late, the times are corrected (the sound is read with the edit list).
                        double edit = VideoInfo.EditStart(File, "vide");
                        if (edit > 0.001 && edit < 1)
                        {
                            int ix, fl; long tm; IMFSample sm;
                            if (r.ReadSample(FIRST_VIDEO, 0, out ix, out fl, out tm, out sm) >= 0)
                            {
                                if (sm != null) { Marshal.ReleaseComObject(sm); if (tm / 1e7 >= edit - FrameSeconds / 2) Shift = edit; }
                                Seek(r, 0);
                            }
                        }
                    }
                    return r;
                }
                catch
                {
                    if (r != null) try { Marshal.ReleaseComObject(r); } catch { }
                    throw;
                }
            }

            // A jump in the file. Windows sometimes refuses one (seen: 0xC00D36E5 jumping back to the start of a 2 s clip):
            // then the file is opened again and the jump made there.
            void SeekTo(long time)
            {
                try { Seek(cur, time); return; }
                catch { }
                try { Marshal.ReleaseComObject(cur); } catch { }
                cur = null;
                cur = OpenReader(false);
                Reopens++;
                try { Seek(cur, time); } catch { Seek(cur, 0); } // (still refused: from the start, it finds its place by reading on)
            }

            int stride;
            void ReadShape(IMFSourceReader r)
            {
                IMFMediaType got;
                Check(r.GetCurrentMediaType(FIRST_VIDEO, out got), "reading the picture's format");
                long packed;
                Check(got.GetUINT64(ref G.MF_MT_FRAME_SIZE, out packed), "reading the picture's size");
                int w = (int)(packed >> 32), h = (int)(packed & 0xFFFFFFFF);
                if (got.GetUINT32(ref G.MF_MT_DEFAULT_STRIDE, out stride) < 0) stride = w * 4;
                Marshal.ReleaseComObject(got);
                if (Opened && (w != Width || h != Height)) throw new Exception("the picture changed size");
                Width = w;
                Height = h;
            }

            void Loop()
            {
                int k = 0;                 // the loop being read
                double loopLen = 0, posClock = double.NegativeInfinity;
                bool positioned = false;
                double lastAsk = double.NegativeInfinity;
                long lastSeek = 0;
                long wrapStart = 0;
                int bytes = Width * Height * 4;
                while (!quit)
                {
                    double c, len, pace;
                    lock (gate) { c = askClock; len = askLoop; pace = askPace; }
                    if (double.IsNaN(c)) { wake.WaitOne(20); continue; }
                    if (len < 0.3) len = 0;
                    double first;
                    int count;
                    lock (gate) { first = ready.Count > 0 ? ready[0].Clock : posClock; count = ready.Count; }
                    double sinceSeek = (Stopwatch.GetTimestamp() - lastSeek) / (double)Stopwatch.Frequency;
                    // A jump (back, or far ahead of what is decoded), another loop length, or the very start: read from there.
                    // (Backwards: the clock itself went back. Not "before the first frame waiting": some files have a hole at each
                    // loop's start, frames Windows' decoder never hands out, and that is no reason to start over.)
                    bool back = c < lastAsk - 0.1;
                    lastAsk = c;
                    bool forced = forceJump; forceJump = false;
                    bool jump = forced || !positioned || Math.Abs(len - loopLen) > 0.0005 || back
                        || (c > posClock + Math.Max(1.0, 0.6 * pace) && sinceSeek > 0.3);
                    if (jump)
                    {
                        lock (gate) { foreach (var f in ready) spare.Push(f); ready.Clear(); }
                        loopLen = len;
                        k = len > 0 ? Math.Max(0, (int)Math.Floor(c / len)) : 0;
                        double t = Math.Max(0, c - k * len);
                        if (Length > 0) t = Math.Min(t, Math.Max(0, Length - 2 * FrameSeconds)); // (the sound may run a moment past the picture: its last frame)
                        SeekTo((long)((t + Shift) * 1e7));
                        Note("jump to " + t.ToString("0.000") + " k" + k + " c" + c.ToString("0.000"));
                        posClock = c - 1;
                        positioned = true;
                        ended = false;
                        lastSeek = Stopwatch.GetTimestamp();
                        Seeks++;
                    }
                    if (ended) { wake.WaitOne(15); continue; }
                    // Far enough ahead: wait for the window to use some.
                    if (count >= Pool - 1 || (count > 0 && posClock > c + Math.Max(0.35, 0.5 * pace))) { wake.WaitOne(Own ? 25 : 4); continue; }
                    int index, flags;
                    long time;
                    IMFSample sample;
                    Check(cur.ReadSample(FIRST_VIDEO, 0, out index, out flags, out time, out sample), "decoding the picture");
                    if ((flags & 0x20) != 0) ReadShape(cur); // (the format changed: same size or it fails)
                    double raw = time / 1e7, pts = raw - Shift;
                    bool past = loopLen > 0 && sample != null && pts >= loopLen - 0.0005; // (beyond the sound's loop: the next loop starts here)
                    if ((flags & 2) != 0 || past)
                    {
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        if (loopLen > 0)
                        {
                            // Straight on into the next loop.
                            k++;
                            // (Round 2: back to the start of the same reader. A second, fresh reader opened beside for each loop
                            // cost a second decoder's memory and gave nothing: the frames some files lack at the start are
                            // missing from a fresh reader too; the time correction, Shift, is what fixed those seams.)
                            SeekTo(0);
                            Note("wrap k" + k + " c" + c.ToString("0.000"));
                            Wraps++;
                            startOfLoop = true;
                            wrapStart = Stopwatch.GetTimestamp();
                            continue;
                        }
                        ended = true;
                        continue;
                    }
                    if (sample == null) continue;
                    Decoded++;
                    if (!double.IsNaN(lastPts) && raw > lastPts && raw - lastPts < 0.5)
                    {
                        gaps[gapAt] = raw - lastPts; gapAt = (gapAt + 1) % gaps.Length; if (gapCount < gaps.Length) gapCount++;
                        if (gapCount >= 10 && Decoded % 15 == 0) { var g = new double[gapCount]; Array.Copy(gaps, g, gapCount); Array.Sort(g); MeasuredFps = (float)(1 / g[gapCount / 2]); }
                    }
                    lastPts = raw;
                    double clock = pts + k * loopLen;
                    // (Some files' first frames don't come out of Windows' reader: the first one that does starts the loop, no hole.)
                    if (startOfLoop) { if (pts < 0.25 && k > 0) clock = k * loopLen; startOfLoop = false; }
                    if (wrapStart != 0) { LongestWrapMs = Math.Max(LongestWrapMs, (int)((Stopwatch.GetTimestamp() - wrapStart) * 1000 / Stopwatch.Frequency)); wrapStart = 0; }
                    // Behind the clock (after a jump the reader starts at the key frame before it), or closer to the last
                    // kept one than a screen frame at a high speed: not copied out (decoding alone is cheap).
                    double latest;
                    lock (gate) latest = ready.Count > 0 ? ready[ready.Count - 1].Clock : double.NegativeInfinity;
                    lock (gate) c = askClock;
                    double minGap = pace > 1.3 ? pace / 130.0 : 0;
                    bool keep = clock >= c - 1.5 * FrameSeconds * Math.Max(1, pace) && clock - latest >= minGap;
                    Note(pts.ToString("0.000") + (keep ? "" : "x") + "@" + c.ToString("0.000"));
                    posClock = clock;
                    if (!keep) { Marshal.ReleaseComObject(sample); continue; }
                    if (!decided) Decide(sample); // (the first picture: does it stay on the graphics card?)
                    VideoFrame frame = null;
                    lock (gate)
                    {
                        if (spare.Count > 0) frame = spare.Pop();
                        else if (allocated.Count + textures.Count < Pool) frame = NewFrame(bytes);
                    }
                    if (frame == null) Note("no frame free");
                    if (frame == null) { Marshal.ReleaseComObject(sample); continue; }
                    bool copied = frame.Tex != IntPtr.Zero ? CopyToTexture(sample, frame.Tex) : CopyOut(sample, frame.Data);
                    Marshal.ReleaseComObject(sample);
                    if (!copied) { lock (gate) spare.Push(frame); continue; }
                    frame.Clock = clock;
                    frame.FileTime = raw; // (Windows' player's own time: where it is put when pausing)
                    lock (gate) ready.Add(frame);
                    Kept++;
                }
            }

            // 1.30.0 round 2: pictures are copied off the graphics card through one staging texture of our own. (Locking
            // Windows' buffers made a CPU copy for every picture in its pool, kept for as long as the reader lived.)
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetResourceFn(IntPtr self, ref Guid iid, out IntPtr res);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSubFn(IntPtr self, out int sub);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, IntPtr desc);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexFn(IntPtr self, IntPtr desc, IntPtr init, out IntPtr tex);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, int dstSub, int x, int y, int z, IntPtr src, int srcSub, IntPtr box);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, int sub, int type, int flags, IntPtr mapped);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, int sub);
            static T Fn<T>(IntPtr obj, int slot) where T : class { return Gpu.Call<T>(obj, slot); }
            static Guid DxgiBufferIid = new Guid("e7174cfa-1c9e-48b1-8866-626226bfc258"), Texture2DIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            IntPtr staging;
            int stagingFormat;
            bool noStaging;
            public volatile int StagedCopies; // (tests)

            // A pool frame: a texture on the graphics card when the pictures stay there, else memory (inside the lock).
            VideoFrame NewFrame(int bytes)
            {
                if (OnGpu)
                {
                    IntPtr t = GpuShow.PoolTexture(Width, Height, TexFormat);
                    if (t != IntPtr.Zero) { textures.Add(t); return new VideoFrame { Tex = t }; }
                    OnGpu = false; // (the card is out of room: memory from here on)
                }
                var f = new VideoFrame { Data = Marshal.AllocHGlobal(bytes) };
                allocated.Add(f.Data);
                return f;
            }

            // The decoded picture's texture on the graphics card (Windows hands its pictures out in textures there).
            bool SourceTexture(IMFSample sample, out IntPtr tex, out int sub, out int format)
            {
                tex = IntPtr.Zero; sub = 0; format = 0;
                IMFMediaBuffer buffer;
                if (sample.GetBufferByIndex(0, out buffer) < 0) return false;
                IntPtr unk = Marshal.GetIUnknownForObject(buffer), dx = IntPtr.Zero, desc = Marshal.AllocHGlobal(64);
                try
                {
                    if (Marshal.QueryInterface(unk, ref DxgiBufferIid, out dx) < 0) return false;
                    if (Fn<GetResourceFn>(dx, 3)(dx, ref Texture2DIid, out tex) < 0 || tex == IntPtr.Zero) { tex = IntPtr.Zero; return false; }
                    if (Fn<GetSubFn>(dx, 4)(dx, out sub) < 0) sub = 0;
                    Fn<GetDescFn>(tex, 10)(tex, desc);
                    format = Marshal.ReadInt32(desc, 16);
                    return true;
                }
                catch { if (tex != IntPtr.Zero) Marshal.Release(tex); tex = IntPtr.Zero; return false; }
                finally
                {
                    if (dx != IntPtr.Zero) Marshal.Release(dx);
                    Marshal.Release(unk);
                    Marshal.FreeHGlobal(desc);
                    Marshal.ReleaseComObject(buffer);
                }
            }

            // At the first picture: it stays on the graphics card if WPF can show such a texture there (BGRA/BGRX).
            void Decide(IMFSample sample)
            {
                decided = true;
                IntPtr tex; int sub, format;
                if (!SourceTexture(sample, out tex, out sub, out format)) return;
                Marshal.Release(tex);
                if (Own) { if (format == 87 && Gpu.Device != IntPtr.Zero) { TexFormat = format; OnGpu = true; } return; } // (1.31.0: copied into a swap chain, no WPF device)
                if ((format == 87 || format == 88) && GpuShow.Ready(hwnd)) { TexFormat = format; OnGpu = true; }
            }

            public volatile int CardCopies; // (tests)

            bool CopyToTexture(IMFSample sample, IntPtr to)
            {
                IntPtr tex; int sub, format;
                if (!SourceTexture(sample, out tex, out sub, out format)) return false;
                try
                {
                    if (format != TexFormat) return false;
                    GpuShow.Copy(to, tex, sub, Width, Height);
                    CardCopies++;
                    return true;
                }
                finally { Marshal.Release(tex); }
            }

            bool CopyStaged(IMFMediaBuffer buffer, IntPtr to)
            {
                if (noStaging || Gpu.Context == IntPtr.Zero) return false;
                IntPtr unk = Marshal.GetIUnknownForObject(buffer), dx = IntPtr.Zero, tex = IntPtr.Zero;
                IntPtr desc = Marshal.AllocHGlobal(64), box = Marshal.AllocHGlobal(24), mapped = Marshal.AllocHGlobal(32);
                try
                {
                    if (Marshal.QueryInterface(unk, ref DxgiBufferIid, out dx) < 0) { noStaging = true; return false; }
                    // IMFDXGIBuffer: GetResource (3), GetSubresourceIndex (4)
                    if (Fn<GetResourceFn>(dx, 3)(dx, ref Texture2DIid, out tex) < 0 || tex == IntPtr.Zero) return false;
                    int sub;
                    if (Fn<GetSubFn>(dx, 4)(dx, out sub) < 0) sub = 0;
                    // ID3D11Texture2D::GetDesc (10): Width, Height, MipLevels, ArraySize, Format, SampleDesc(2), Usage, BindFlags, CPUAccessFlags, MiscFlags
                    Fn<GetDescFn>(tex, 10)(tex, desc);
                    int format = Marshal.ReadInt32(desc, 16);
                    if (staging == IntPtr.Zero || format != stagingFormat)
                    {
                        if (staging != IntPtr.Zero) { Marshal.Release(staging); staging = IntPtr.Zero; }
                        Marshal.WriteInt32(desc, 0, Width); Marshal.WriteInt32(desc, 4, Height);
                        Marshal.WriteInt32(desc, 8, 1); Marshal.WriteInt32(desc, 12, 1);
                        Marshal.WriteInt32(desc, 20, 1); Marshal.WriteInt32(desc, 24, 0);
                        Marshal.WriteInt32(desc, 28, 3);        // D3D11_USAGE_STAGING
                        Marshal.WriteInt32(desc, 32, 0);        // no binding
                        Marshal.WriteInt32(desc, 36, 0x20000);  // CPU read
                        Marshal.WriteInt32(desc, 40, 0);
                        if (Fn<CreateTexFn>(Gpu.Device, 5)(Gpu.Device, desc, IntPtr.Zero, out staging) < 0) { staging = IntPtr.Zero; noStaging = true; return false; }
                        stagingFormat = format;
                    }
                    Marshal.WriteInt32(box, 0, 0); Marshal.WriteInt32(box, 4, 0); Marshal.WriteInt32(box, 8, 0);
                    Marshal.WriteInt32(box, 12, Width); Marshal.WriteInt32(box, 16, Height); Marshal.WriteInt32(box, 20, 1);
                    IntPtr ctx = Gpu.Context;
                    Fn<CopyRegionFn>(ctx, 46)(ctx, staging, 0, 0, 0, 0, tex, sub, box);
                    if (Fn<MapFn>(ctx, 14)(ctx, staging, 0, 1, 0, mapped) < 0) return false; // D3D11_MAP_READ
                    try { CopyRows(to, Marshal.ReadIntPtr(mapped), Marshal.ReadInt32(mapped, IntPtr.Size), Width * 4); }
                    finally { Fn<UnmapFn>(ctx, 15)(ctx, staging, 0); }
                    StagedCopies++;
                    return true;
                }
                catch { noStaging = true; return false; }
                finally
                {
                    if (tex != IntPtr.Zero) Marshal.Release(tex);
                    if (dx != IntPtr.Zero) Marshal.Release(dx);
                    Marshal.Release(unk);
                    Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(box); Marshal.FreeHGlobal(mapped);
                }
            }

            bool CopyOut(IMFSample sample, IntPtr to)
            {
                IMFMediaBuffer buffer;
                if (sample.GetBufferByIndex(0, out buffer) < 0) return false;
                try
                {
                    int row = Width * 4;
                    if (CopyStaged(buffer, to)) return true;
                    var two = buffer as IMF2DBuffer;
                    IntPtr scan;
                    int pitch;
                    if (two != null && two.Lock2D(out scan, out pitch) >= 0)
                    {
                        try { CopyRows(to, scan, pitch, row); }
                        finally { two.Unlock2D(); }
                        return true;
                    }
                    int max, cur;
                    if (buffer.Lock(out scan, out max, out cur) < 0) return false;
                    try
                    {
                        pitch = stride != 0 ? stride : row;
                        if (pitch < 0) scan = scan + (-pitch) * (Height - 1); // (bottom-up rows)
                        CopyRows(to, scan, pitch, row);
                    }
                    finally { buffer.Unlock(); }
                    return true;
                }
                finally { Marshal.ReleaseComObject(buffer); }
            }

            void CopyRows(IntPtr to, IntPtr scan, int pitch, int row)
            {
                if (pitch == row) { Copy(to, scan, (IntPtr)((long)row * Height)); return; }
                for (int y = 0; y < Height; y++) Copy(to + y * row, scan + y * pitch, (IntPtr)row);
            }
        }
    }

    [ComImport, Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMF2DBuffer
    {
        [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
        [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
        [PreserveSig] int GetContiguousLength(out int length);
        [PreserveSig] int ContiguousCopyTo(IntPtr destination, int length);
        [PreserveSig] int ContiguousCopyFrom(IntPtr source, int length);
    }

    partial class ViewerWindow
    {
        // Where a video is. (1.30.0 kept a second answer here for its experimental drawn pictures; since 1.31.0 there is
        // one player, Player.cs, and its Position is the place.)
        internal TimeSpan VideoAt(Slot s) { return s.Video.Position; }
    }
}
