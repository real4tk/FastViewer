// FastViewer 1.29.0: every test run ("--" options) is silent through the speakers, whatever a test does with
// FastViewer's own mute and volume: this process's own Windows volume (its audio sessions, as in the Volume
// mixer) is set to 0 and muted before anything plays, and kept there (checked 5 times a second, also for sessions
// that appear later, on every sound device). Test media is often loud noise; the user is often in calls.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FastViewer
{
    static class TestSilence
    {
        public static volatile int Sessions, Fixes; // tests: sessions of this process seen at volume 0; times one had to be turned down
        public static volatile bool Ok;             // every session of this process found at volume 0 and muted
        static Thread thread;
        static readonly ManualResetEvent ready = new ManualResetEvent(false);
        public static bool WaitReady(int ms) { return thread == null || ready.WaitOne(ms); }

        public static void Start()
        {
            if (thread != null) return;
            // (The first check runs beside the window's start: done on the window's own thread it held up opening the first video by
            // 0.6-0.8 s (Windows' audio service, six sound devices). Until it is done the test is muted the usual way, and
            // the tests that turn that off wait for it first: WaitReady.)
            // Windows remembers a program's volume by its path. A test copy may keep 0; the FastViewer.exe that Windows
            // opens files with gets its volume back when the test ends (by then nothing plays any more).
            if (IsTheRealOne()) AppDomain.CurrentDomain.ProcessExit += delegate { restoring = true; Once(); };
            // (The first check sets this process's default sound session to 0 on every device, which is what both
            // Windows' player and FastViewer's own sound use; the re-checks start 3 s later, so they never weigh on the
            // opening that the speed tests measure.)
            thread = new Thread(() => { Once(); ready.Set(); Thread.Sleep(3000); while (true) { Once(); Thread.Sleep(500); } }) { IsBackground = true, Name = "Test silence", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        static void Once()
        {
            int pid = Process.GetCurrentProcess().Id, seen = 0;
            bool ok = true;
            IMMDeviceEnumerator en = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDeviceCollection all;
                if (en.EnumAudioEndpoints(0, 1, out all) < 0) { Ok = false; return; } // render, active
                int n;
                all.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (all.Item(i, out dev) < 0) continue;
                    try
                    {
                        object o;
                        var iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) < 0) continue;
                        var mgr = (IAudioSessionManager2)o;
                        // The process's default session (made now if it doesn't exist yet, so it starts at 0).
                        ISimpleAudioVolume def;
                        if (mgr.GetSimpleAudioVolume(IntPtr.Zero, 0, out def) >= 0 && def != null) { ok &= Quiet(def); seen++; Marshal.ReleaseComObject(def); }
                        IAudioSessionEnumerator list;
                        if (mgr.GetSessionEnumerator(out list) >= 0)
                        {
                            int count;
                            list.GetCount(out count);
                            for (int k = 0; k < count; k++)
                            {
                                IAudioSessionControl2 c;
                                if (list.GetSession(k, out c) < 0 || c == null) continue;
                                int owner;
                                if (c.GetProcessId(out owner) >= 0 && owner == pid) { ok &= Quiet((ISimpleAudioVolume)c); seen++; }
                                Marshal.ReleaseComObject(c);
                            }
                            Marshal.ReleaseComObject(list);
                        }
                        Marshal.ReleaseComObject(mgr);
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
                Marshal.ReleaseComObject(all);
            }
            catch { ok = false; }
            finally { if (en != null) Marshal.ReleaseComObject(en); }
            Sessions = seen;
            Ok = ok && seen > 0;
        }

        static volatile bool restoring;

        static bool IsTheRealOne()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications\FastViewer.exe\shell\open\command"))
                {
                    string cmd = k == null ? null : k.GetValue("") as string;
                    string me = Process.GetCurrentProcess().MainModule.FileName;
                    return cmd != null && cmd.IndexOf(me, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool Quiet(ISimpleAudioVolume v)
        {
            float level;
            bool mute;
            var ctx = Guid.Empty;
            if (restoring) { v.SetMasterVolume(1, ref ctx); v.SetMute(false, ref ctx); return true; }
            v.GetMasterVolume(out level);
            v.GetMute(out mute);
            if (level != 0 || !mute)
            {
                Fixes++;
                v.SetMasterVolume(0, ref ctx);
                v.SetMute(true, ref ctx);
                v.GetMasterVolume(out level);
                v.GetMute(out mute);
            }
            return level == 0 && mute;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out ISimpleAudioVolume volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator list);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl (not used, but they hold their places)
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid group);
            [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out int pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, ref Guid ctx);
            [PreserveSig] int GetMute(out bool mute);
        }
    }
}
