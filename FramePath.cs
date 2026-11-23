// FastViewer 1.30.0: FastViewer draws the video's pictures itself (an experiment behind a switch, settings line ownFrames=).
//
// Windows' player (WPF's MediaElement) stands still for a moment whenever it starts over or changes speed. FastViewer's
// own sound doesn't: it loops without a gap and changes speed without a gap (SoundPlay.cs, Stretch.cs). So here the
// sound is the clock: a reader of our own (Media Foundation's source reader, decoding on the graphics card through a
// D3D11 device, converted to BGRA there) decodes the pictures ahead of time, the next loop's first pictures included,
// and every screen frame the picture that belongs to what is being heard right now is drawn over Windows' player.
// Windows' player keeps running underneath, unseen, for everything else (the seek bar, captions, pausing, frame steps,
// trimming, grabbing a frame): only what is seen while playing comes from here. Whenever this path has no picture for
// the moment (starting, after a jump, a file it can't read), Windows' player simply shows through as before.
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

            public static IntPtr Manager { get { lock (gate) { if (!tried) { tried = true; try { Make(); } catch (Exception ex) { Trouble = ex.Message; } } return manager; } } }

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
            const int Pool = 6;   // (round 2: was 10; 6 still keeps 5 pictures ready ahead)
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

            public FrameReader(string file, int maxWidth, int maxHeight, IntPtr window)
            {
                File = file;
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
                lock (gate) { askClock = clock; askLoop = loopLen; askPace = pace; }
                wake.Set();
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
            void Note(string what) { if (!Tracing) return; lock (Recent) { Recent.Add(what); if (Recent.Count > 60) Recent.RemoveAt(0); } }

            void Run()
            {
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
                    ra.SetUINT32(ref G.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
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
                    long rate;
                    if (native.GetUINT64(ref G.MF_MT_FRAME_RATE, out rate) >= 0 && (rate & 0xFFFFFFFF) > 0 && (rate >> 32) > 0)
                        FrameSeconds = Math.Max(1 / 240.0, Math.Min(1, (double)(rate & 0xFFFFFFFF) / (rate >> 32)));
                    Marshal.ReleaseComObject(native);
                    // Pictures bigger than the screen are made screen-sized on the graphics card (copying 4K pictures out
                    // 60 times a second costs too much); smaller ones keep their own size.
                    double longSide = Math.Max(maxW, maxH), shortSide = Math.Min(maxW, maxH);
                    double fit = Math.Min(1.0, Math.Min(longSide / Math.Max(nw, nh), shortSide / Math.Min(nw, nh)));
                    int ow = Math.Max(2, (int)Math.Round(nw * fit / 2) * 2), oh = Math.Max(2, (int)Math.Round(nh * fit / 2) * 2);
                    IMFMediaType want;
                    MFCreateMediaType(out want);
                    want.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Video);
                    // (Round 3: ARGB, so the 4th byte is filled in as fully opaque. With RGB32 it was 0, and WPF drew the picture
                    // see-through on a real screen: washed out, no deep blacks, the grey behind showing through.)
                    want.SetGUID(ref G.MF_MT_SUBTYPE, ref Argb32);
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
                    // Windows may have turned the picture by the file's tag already: the window turns it itself, so such
                    // files stay with Windows' player.
                    if ((Width > Height) != (nw > nh) && nw != nh) throw new Exception("the picture came out turned");
                    if (first) Scaled = Width < nw - 2;
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
                    bool jump = !positioned || Math.Abs(len - loopLen) > 0.0005 || back
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
                    if (count >= Pool - 1 || (count > 0 && posClock > c + Math.Max(0.35, 0.5 * pace))) { wake.WaitOne(4); continue; }
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
        internal static bool ownFrames = false;    // settings line ownFrames= (1.30.0, experimental: off by default since 1.30.0 round 4; --frames on|off for tests)
        internal static bool framesSwitchFixed;    // (tests: --frames decides, the settings file doesn't)
        VideoConverter.FrameReader frameReader;
        Image frameView;
        WriteableBitmap frameBitmap;
        object frameBound;
        bool frameHooked, frameVisible, frameClockOk;
        double frameClock, frameOnClock = double.NaN, frameOnFile;
        long frameClockTick, frameHoldUntil, frameIdleSince, frameLastOk;
        double framePace = 1, frameLoopLen;
        readonly HashSet<string> frameBad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Tests: what was drawn (per screen frame: Stopwatch tick, the clock, the frame's clock, drawn by us?).
        internal List<double[]> frameTrace;
        internal int framesDrawn, frameCopyWorstUs;

        // Can this video's pictures come from here? (A file with sound: the sound is the clock.)
        bool FramesUsable(Slot s)
        {
            return ownFrames && !ownSoundOff && s != null && s.IsVideo && !s.IsAudio && !s.Failed && s.Ready && s.File != null
                && !frameBad.Contains(s.File) && KnownTracks(s.File) >= 1;
        }

        bool FramesWanted(Slot s) { return s != null && s == shown && playing && IsVisible && !gridOpen && FramesUsable(s); }

        // (OwnSound: FastViewer's own sound is the clock, so it plays whenever the pictures come from here.)
        bool FramesWantSound(Slot s) { return s == shown && revealing != s && FramesWanted(s); }

        // From UpdateOwnSound / TellSound: starts drawing while the video plays (each screen frame, OnFrameTick).
        void CheckFrames()
        {
            if (!ownFrames && frameReader == null && !frameHooked) return;
            var s = shown;
            if (frameReader != null && (s == null || frameReader.File != s.File)) DropFrames();
            if (FramesWanted(s) || frameVisible)
            {
                if (!frameHooked) { CompositionTarget.Rendering += OnFrameTick; frameHooked = true; }
                frameIdleSince = 0;
            }
            else if (frameReader != null && frameIdleSince == 0) frameIdleSince = Stopwatch.GetTimestamp();
            // A paused video keeps its reader for a moment (playing on needs no new one); then its memory goes.
            if (frameReader != null && frameIdleSince != 0 && (Stopwatch.GetTimestamp() - frameIdleSince) / (double)Stopwatch.Frequency > 8) DropFrames();
        }

        void DropFrames()
        {
            if (frameReader != null) { frameReader.Quit(); frameReader = null; }
            ShowFrames(false, null);
            frameOnClock = double.NaN;
            frameLoopLen = 0;
            frameClockOk = false;
            frameIdleSince = 0;
        }

        void OnFrameTick(object sender, EventArgs e)
        {
            var s = shown;
            long now = Stopwatch.GetTimestamp();
            double freq = Stopwatch.Frequency;
            var r = frameReader;
            if (r != null && (s == null || r.File != s.File)) { DropFrames(); r = null; }
            if (!FramesWanted(s))
            {
                frameClockOk = false;
                if (frameParked) Unpark(s == parkSlot && playing);
                // Paused: the picture on screen stays while Windows' player is put on the same frame underneath.
                if (frameVisible && s != null && !playing && r != null && !double.IsNaN(frameOnClock))
                {
                    if (frameHoldUntil == 0)
                    {
                        frameHoldUntil = now + (long)(0.35 * freq);
                        double at = s.Video.Position.TotalSeconds;
                        if (Math.Abs(at - frameOnFile) > s.FrameSeconds * 0.5) { s.Video.Position = TimeSpan.FromSeconds(frameOnFile); frameMovedPlayerAt = now; }
                    }
                    if (now < frameHoldUntil) return;
                }
                frameHoldUntil = 0;
                ShowFrames(false, s);
                CompositionTarget.Rendering -= OnFrameTick;
                frameHooked = false;
                CheckFrames();
                return;
            }
            frameHoldUntil = 0;
            if (r == null)
            {
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).Bounds;
                frameReader = r = new VideoConverter.FrameReader(s.File, Math.Max(1280, screen.Width), Math.Max(720, screen.Height), new System.Windows.Interop.WindowInteropHelper(this).Handle);
            }
            if (r.Failed)
            {
                frameBad.Add(s.File);
                Log("own pictures off for " + Path.GetFileName(s.File) + ": " + r.Trouble);
                DropFrames();
                UpdateOwnSound();
                return;
            }
            // The clock: what is being heard right now, a screen frame on (the picture comes up at the next one).
            double at0 = 0, pace = 1, len = 0;
            long stamp = 0;
            var p = soundPlayer;
            bool ok = p != null && p.Clock(s.File, out at0, out stamp, out pace, out len);
            double c;
            if (ok)
            {
                c = at0 + Math.Min(0.1, (now - stamp) / freq) * pace + 0.012 * pace;
                // (Smoothed: the reckoning steps a little each time the sound thread goes round.)
                double pred = frameClockOk ? frameClock + (now - frameClockTick) / freq * pace : c;
                double err = c - pred;
                frameClock = !frameClockOk || Math.Abs(err) > 0.04 ? c : pred + err * 0.2;
                c = frameClock;
                frameClockOk = true;
                frameLastOk = now;
                frameClockReal = true;
                framePace = pace;
                frameLoopLen = len;
            }
            else if (frameClockOk && playing && (now - frameLastOk) / freq < 0.4)
            {
                // The sound thread was busy for a moment (seen right at a loop's seam): carry on from its last clock. (Falling
                // back to Windows' player here made the reader jump and lose its ready pictures: a hold at the seam.)
                pace = framePace;
                len = frameLoopLen;
                frameClock += (now - frameClockTick) / freq * pace;
                c = frameClock;
                ok = true;
                frameClockReal = false;
            }
            else
            {
                // (No sound yet: keep the reader near the picture, so it is ready when the sound comes.)
                c = s.Video.Position.TotalSeconds + soundLoopBase;
                len = loop ? (frameLoopLen > 0.3 ? frameLoopLen : LoopLenFor(s)) : 0; // (the sound's own loop length once known: Windows' player's differs a little, and a change makes the reader start over)
                pace = speed;
                frameClockOk = false;
            }
            frameClockTick = now;
            r.Ask(c, loop ? len : 0, pace);
            int skippedNow = 0; double skippedAt = double.NaN;
            var f = r.Opened ? r.Take(c, out skippedNow, out skippedAt) : null;
            if (f != null)
            {
                long t0 = Stopwatch.GetTimestamp();
                frameBusy = false;
                bool drawn = f.Tex != IntPtr.Zero ? ShowOnCard(f, r) : false;
                if (frameBusy) { r.PutBack(f); f = null; } // (WPF still had the surface: tried again at the next screen frame)
                else frameCopyWorstUs = Math.Max(frameCopyWorstUs, (int)((Stopwatch.GetTimestamp() - t0) * 1000000 / Stopwatch.Frequency));
            }
            if (f != null)
            {
                NoteFrameShown(f, skippedNow, skippedAt, c, r.FrameSeconds, pace);
                long t0 = Stopwatch.GetTimestamp();
                bool drawn = f.Tex != IntPtr.Zero;
                if (!drawn && f.Data != IntPtr.Zero)
                {
                    if (frameBitmap == null || frameBitmap.PixelWidth != r.Width || frameBitmap.PixelHeight != r.Height)
                        frameBitmap = new WriteableBitmap(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null);
                    frameBitmap.WritePixels(new Int32Rect(0, 0, r.Width, r.Height), f.Data, r.Width * r.Height * 4, r.Width * 4);
                    frameSource = frameBitmap;
                    if (frameView != null && frameView.Source != frameBitmap) frameView.Source = frameBitmap;
                }
                frameOnClock = f.Clock;
                frameOnFile = f.FileTime;
                r.Give(f);
                framesDrawn++;
                frameCopyWorstUs = Math.Max(frameCopyWorstUs, (int)((Stopwatch.GetTimestamp() - t0) * 1000000 / Stopwatch.Frequency));
            }
            // Shown once there is a picture for this moment; it stays while the sound plays (a late picture is still
            // better than switching to Windows' player, which would stand still at a loop).
            double behind = c - frameOnClock;
            bool near = !double.IsNaN(frameOnClock) && behind > -0.03 && behind < Math.Max(0.05, 2.5 * r.FrameSeconds) * Math.Max(1, pace);
            bool stays = frameVisible && !double.IsNaN(frameOnClock) && behind > -0.25 && behind < 0.6;
            bool show = ok && (near || stays) && !(r.Scaled && s.Zoom.ScaleX > 1.01) && s.Video.Visibility == Visibility.Visible;
            ShowFrames(show, s);
            // 1.30.0 round 2: once FastViewer's pictures have been on screen steadily for a moment, Windows' player under
            // them is paused ("parked"): it no longer decodes every frame for nothing (that was the second video decoding
            // in Task Manager). Its place is then the drawn picture's (VideoAt); any jump, pause, switch or the end of a
            // clip without Repeat gives it back its own playing.
            if (show) { if (frameShownSince == 0) frameShownSince = now; frameLastShown = now; } else frameShownSince = 0;
            if (frameParked)
            {
                var len0 = s.Video.NaturalDuration;
                if (now < parkSettle) parkedAt = s.Video.Position.TotalSeconds; // (just paused: its place settles for a moment)
                else if (Math.Abs(s.Video.Position.TotalSeconds - parkedAt) > 0.004) Unpark(true, false); // (someone moved it: a skip, the time bar, a frame step)
                else if (!show && (now - frameLastShown) / freq > 0.3) Unpark(true);
                else if (!loop && len0.HasTimeSpan && frameOnFile >= len0.TimeSpan.TotalSeconds - 0.12)
                {
                    // (The end without Repeat: as if Windows' player had played to its end.)
                    Unpark(false);
                    var ended = s;
                    Dispatcher.BeginInvoke(new Action(delegate { if (shown == ended) OnVideoEnded(ended); }));
                }
            }
            else if (show && frameClockReal && (now - frameShownSince) / freq > 0.6 && !scrubbing && trimDrag == '\0' && revealing != s
                && soundPlayer != null && soundPlayer.Sounding && !holdPending)
            {
                var len1 = s.Video.NaturalDuration;
                if (loop || !len1.HasTimeSpan || frameOnFile < len1.TimeSpan.TotalSeconds - 1.0) Park(s);
            }
            var trace = frameTrace;
            if (trace != null && trace.Count < 200000) trace.Add(new[] { now, c, frameOnClock, show ? 1 : 0, ok ? 1 : 0, s.Video.Position.TotalSeconds + soundLoopBase });
        }

        // Windows' player under the drawn pictures, paused while they are drawn (1.30.0 round 2).
        internal bool frameParked;
        Slot parkSlot;
        double parkedAt;
        long parkSettle;
        internal long frameMovedPlayerAt; // (when Windows' player was last put on the drawn picture's frame: it may still be finding it)
        bool frameClockReal;
        long frameShownSince, frameLastShown;

        void Park(Slot s)
        {
            s.Video.Pause();
            parkedAt = s.Video.Position.TotalSeconds;
            parkSlot = s;
            parkSettle = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4 / 10;
            frameParked = true;
            if (testRun) Log("Windows' player parked at " + parkedAt.ToString("0.000"));
        }

        // play: it plays on (else it stays paused); reposition: it is put on the picture on screen first (not when
        // someone else just moved it).
        void Unpark(bool play, bool reposition = true)
        {
            var ps = parkSlot;
            frameParked = false;
            parkSlot = null;
            if (ps == null || ps.Video == null) return;
            bool audio = parkedAudio;
            parkedAudio = false;
            double place = audio ? audioPlace : frameOnFile;
            if (reposition && (audio || !double.IsNaN(frameOnClock))) { ps.Video.Position = TimeSpan.FromSeconds(Math.Max(0, place)); frameMovedPlayerAt = Stopwatch.GetTimestamp(); }
            if (play) ps.Video.Play();
            if (testRun) Log("Windows' player unparked" + (play ? ", playing" : "") + (reposition ? " at " + frameOnFile.ToString("0.000") : ""));
        }

        // Where a video is: the drawn picture's place while Windows' player is parked, else Windows' player's.
        internal TimeSpan VideoAt(Slot s)
        {
            if (frameParked && s == parkSlot && parkedAudio) return TimeSpan.FromSeconds(Math.Max(0, audioPlace));
            if (frameParked && s == parkSlot && !double.IsNaN(frameOnClock)) return TimeSpan.FromSeconds(Math.Max(0, frameOnFile));
            return s.Video.Position;
        }

        // While parked the sound is its own clock: it is told its own place, so it never chases a picture.
        // 1.30.0 round 3: music too. While FastViewer plays a song's sound itself, Windows' player (it has no picture to
        // show) is parked as well: the sound loops by itself, and Windows' player is never started over at its very end
        // (that left an MP3 on Repeat at 2x and 200% failed for good: "the file can't be played").
        bool parkedAudio;
        double audioPlace;
        long audioClockAt;

        void CheckAudioPark(Slot s)
        {
            if (frameParked || s == null || !s.IsAudio || s.Failed || !playing || scrubbing || revealing == s || !soundGo) return;
            var p = soundPlayer;
            if (p == null || !p.Sounding) return;
            double at0, pace, len;
            long st;
            if (!p.Clock(s.File, out at0, out st, out pace, out len)) return;
            var dur = s.Video.NaturalDuration;
            double loopLen = loop ? (len > 0.3 ? len : LoopLenFor(s)) : 0;
            double place = loopLen > 0 ? at0 - Math.Floor(at0 / loopLen) * loopLen : at0;
            if (!loop && dur.HasTimeSpan && place > dur.TimeSpan.TotalSeconds - 1.0) return;
            audioPlace = place;
            audioClockAt = Stopwatch.GetTimestamp();
            Park(s);
            parkedAudio = true;
        }

        bool TellParkedSound(Slot s, SoundLayout layout)
        {
            if (frameParked && parkedAudio && s != parkSlot) { Unpark(false); return false; } // (another file: music has no frame tick to notice)
            if (!frameParked || s != parkSlot) return false;
            var p = soundPlayer;
            double at0, pace, len;
            long st;
            long now = Stopwatch.GetTimestamp();
            if (p == null || !p.Clock(s.File, out at0, out st, out pace, out len))
            {
                // (A moment without its clock: the last wish stands. Music: for long, Windows' player takes over again.)
                if (parkedAudio && (now - audioClockAt) / (double)Stopwatch.Frequency > 0.5) Unpark(playing);
                return true;
            }
            audioClockAt = now;
            double at = at0 + (now - st) / (double)Stopwatch.Frequency * pace;
            double loopLen = loop ? (len > 0.3 ? len : LoopLenFor(s)) : 0;
            double loopBase = loopLen > 0 ? Math.Floor(at / loopLen) * loopLen : 0;
            if (parkedAudio)
            {
                audioPlace = at - loopBase;
                var dur = s.Video.NaturalDuration;
                if (!playing) { Unpark(false); return false; }
                if (now < parkSettle) parkedAt = s.Video.Position.TotalSeconds;
                else if (Math.Abs(s.Video.Position.TotalSeconds - parkedAt) > 0.004) { Unpark(true, false); return false; } // (a skip, the time bar)
                if (!loop && dur.HasTimeSpan && audioPlace >= dur.TimeSpan.TotalSeconds - 0.12)
                {
                    Unpark(false);
                    var ended = s;
                    Dispatcher.BeginInvoke(new Action(delegate { if (shown == ended) OnVideoEnded(ended); }));
                    return true;
                }
            }
            soundLoopBase = loopBase;
            soundSeenAt = at;
            soundSeenTick = now;
            soundMovedAt = now;
            soundSeenFile = s.File;
            soundPlayer.Ask(new SoundWish
            {
                File = s.File,
                Gains = layout == null || layout.Count == 0 ? null : HeardGains(layout),
                Volume = muted ? 0 : volume * LevelOf(s),
                Position = at,
                Stamp = now,
                Playing = true,
                LoopLength = loopLen,
                LoopBase = loopBase,
                Speed = speed,
                PicturePace = speed,
                Silent = testRun && !ownSoundAloud
            });
            if (!s.Video.IsMuted) s.Video.IsMuted = true;
            return true;
        }

        // 1.30.0 round 2: showing a picture that is on the graphics card: copied (on the card) into the texture WPF
        // draws through a D3DImage. When that can't be done the pictures go through memory, as before.
        System.Windows.Interop.D3DImage frameD3D;
        ImageSource frameSource;
        IntPtr sharedTex, sharedTex9, sharedSurf;
        int sharedW, sharedH, sharedFmt;

        bool frameBusy;
        internal int statsBusy;

        bool ShowOnCard(VideoFrame f, VideoConverter.FrameReader r)
        {
            try
            {
                if (frameD3D == null)
                {
                    frameD3D = new System.Windows.Interop.D3DImage();
                    frameD3D.IsFrontBufferAvailableChanged += delegate { if (frameD3D.IsFrontBufferAvailable) sharedFmt = -1; }; // (made again after a lost screen)
                }
                if (!frameD3D.IsFrontBufferAvailable) return false;
                if (sharedTex == IntPtr.Zero || sharedW != r.Width || sharedH != r.Height || sharedFmt != r.TexFormat)
                {
                    ReleaseShared();
                    if (!VideoConverter.GpuShow.Shared(r.Width, r.Height, r.TexFormat, out sharedTex, out sharedTex9, out sharedSurf)) throw new Exception("no shared texture");
                    sharedW = r.Width; sharedH = r.Height; sharedFmt = r.TexFormat;
                    frameD3D.Lock();
                    frameD3D.SetBackBuffer(System.Windows.Interop.D3DResourceType.IDirect3DSurface9, sharedSurf, true);
                    frameD3D.Unlock();
                }
                // (Round 3: not waiting for WPF's render thread to let go of the surface: the window's thread would stand still,
                // fewer screen frames get drawn and pictures are skipped. Busy: the same picture next screen frame.)
                long lockFrom = Stopwatch.GetTimestamp();
                if (!frameD3D.TryLock(new Duration(TimeSpan.FromMilliseconds(1)))) { frameD3D.Unlock(); NoteLockWait(lockFrom); frameBusy = true; statsBusy++; return false; } // (a TryLock that timed out still has to be undone)
                NoteLockWait(lockFrom);
                try
                {
                    VideoConverter.GpuShow.Copy(sharedTex, f.Tex, 0, r.Width, r.Height);
                    VideoConverter.GpuShow.Finish();
                    frameD3D.AddDirtyRect(new Int32Rect(0, 0, r.Width, r.Height));
                }
                finally { frameD3D.Unlock(); }
                frameSource = frameD3D;
                if (frameView != null && frameView.Source != frameD3D) frameView.Source = frameD3D;
                return true;
            }
            catch (Exception ex)
            {
                // (Pictures through memory from now on: the reader starts again that way.)
                Log("pictures can't stay on the graphics card: " + ex.Message);
                VideoConverter.GpuShow.Off = true;
                Dispatcher.BeginInvoke(new Action(DropFrames));
                return false;
            }
        }

        void ReleaseShared()
        {
            if (sharedSurf != IntPtr.Zero) { Marshal.Release(sharedSurf); sharedSurf = IntPtr.Zero; }
            if (sharedTex9 != IntPtr.Zero) { Marshal.Release(sharedTex9); sharedTex9 = IntPtr.Zero; }
            if (sharedTex != IntPtr.Zero) { Marshal.Release(sharedTex); sharedTex = IntPtr.Zero; }
        }

        // The picture FastViewer is showing, as a WPF picture (Copy frame, Save frame).
        // (Tests: the average of the shared texture's 4th byte, which WPF may take as opacity.)
        internal double DrawnAlpha()
        {
            if (sharedTex == IntPtr.Zero) return -1;
            var b = VideoConverter.GpuShow.Read(sharedTex, sharedW, sharedH, sharedFmt, true);
            if (b == null) return -1;
            var px = new byte[b.PixelWidth * b.PixelHeight * 4];
            b.CopyPixels(px, b.PixelWidth * 4, 0);
            double sum = 0; long n = 0;
            for (int i = 3; i < px.Length; i += 4 * 13) { sum += px[i]; n++; }
            return sum / Math.Max(1, n);
        }

        internal BitmapSource DrawnPicture()
        {
            if (frameSource == frameD3D && sharedTex != IntPtr.Zero)
                try { return VideoConverter.GpuShow.Read(sharedTex, sharedW, sharedH, sharedFmt); } catch { return null; }
            return frameBitmap != null ? (BitmapSource)frameBitmap.Clone() : null;
        }

        void ShowFrames(bool on, Slot s)
        {
            if (on && s != null)
            {
                if (frameView == null)
                {
                    frameView = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Source = frameSource };
                    RenderOptions.SetBitmapScalingMode(frameView, BitmapScalingMode.Linear);
                }
                // Right above the slot's Windows player, exactly its size (it is turned, zoomed and panned with it).
                var parent = frameView.Parent as Panel;
                int vi = s.Rotor.Children.IndexOf(s.Video);
                if (parent != s.Rotor || s.Rotor.Children.IndexOf(frameView) != vi + 1)
                {
                    if (parent != null) parent.Children.Remove(frameView);
                    vi = s.Rotor.Children.IndexOf(s.Video);
                    s.Rotor.Children.Insert(vi + 1, frameView);
                }
                if (frameBound != s.Video)
                {
                    frameView.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = s.Video });
                    frameView.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = s.Video });
                    frameBound = s.Video;
                }
            }
            if (frameView != null)
            {
                var want = on ? Visibility.Visible : Visibility.Collapsed;
                if (frameView.Visibility != want) frameView.Visibility = want;
            }
            frameVisible = on && frameView != null;
        }
    }
}
