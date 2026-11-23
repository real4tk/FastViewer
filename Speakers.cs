// FastViewer 1.31.0: sound to the speakers through WASAPI, Windows' own low-latency path (shared mode).
// waveOut (SoundPlay.cs, the way before) kept 100-170 ms of sound waiting, so a new volume, a pause or a new speed was heard
// that much later. Here about 45 ms wait, and the volume (up to 100%) is set on the stream itself, which Windows applies
// within its next 10 ms period: turning the wheel is heard at once. Louder than 100% stays FastViewer's own gain.
// The stream follows the default speakers: when Windows switches them (headphones plugged in), it is made again there.
using System;
using System.Runtime.InteropServices;

namespace FastViewer
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int frames, int flags);
    }

    [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClock
    {
        [PreserveSig] int GetFrequency(out long frequency);
        [PreserveSig] int GetPosition(out long position, out long qpc);
        [PreserveSig] int GetCharacteristics(out int characteristics);
    }

    [ComImport, Guid("93014887-242D-4068-8A15-CF5E93B90FE3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioStreamVolume
    {
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetChannelVolume(int index, float level);
        [PreserveSig] int GetChannelVolume(int index, out float level);
        [PreserveSig] int SetAllVolumes(int count, [MarshalAs(UnmanagedType.LPArray)] float[] levels);
        [PreserveSig] int GetAllVolumes(int count, [MarshalAs(UnmanagedType.LPArray)] float[] levels);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public int Id; }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    // Says when Windows' default speakers change (any time, from Windows' own threads).
    [ComVisible(true)]
    sealed class SpeakerWatch : IMMNotificationClient
    {
        public static volatile int Changes;
        public int OnDeviceStateChanged(string id, int state) { return 0; }
        public int OnDeviceAdded(string id) { return 0; }
        public int OnDeviceRemoved(string id) { return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string id) { if (flow == 0 && role == 0) Changes++; return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }
    }

    sealed class Wasapi : IDisposable
    {
        public static bool Off;               // (--waveout: the old way, for comparing)
        static IMMDeviceEnumerator enumerator;
        static SpeakerWatch watch;
        IAudioClient client;
        IAudioRenderClient render;
        IAudioClock clock;
        IAudioStreamVolume volume;
        int bufferFrames, changesAtStart;
        long written, frequency;
        bool started;
        float level = -1;
        public double Latency;                // seconds after the device position until it is heard (Windows' own stream latency)
        public string Trouble;

        public static Wasapi Open()
        {
            var w = new Wasapi();
            try { w.Make(); return w; }
            catch (Exception e) { w.Trouble = e.Message; w.Dispose(); LastTrouble = e.Message; return null; }
        }
        public static string LastTrouble;

        static void Check(int hr, string what) { if (hr < 0) throw new Exception(what + " (0x" + hr.ToString("X8") + ")"); }

        void Make()
        {
            if (enumerator == null)
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                watch = new SpeakerWatch();
                try { enumerator.RegisterEndpointNotificationCallback(watch); } catch { }
            }
            changesAtStart = SpeakerWatch.Changes;
            IMMDevice device;
            Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device), "no speakers"); // eRender, eConsole
            Guid iid = typeof(IAudioClient).GUID;
            object o;
            Check(device.Activate(ref iid, 1 | 2 | 4 | 0x10, IntPtr.Zero, out o), "the speakers can't be used"); // CLSCTX_ALL
            Marshal.ReleaseComObject(device);
            client = (IAudioClient)o;
            IntPtr format = Marshal.AllocHGlobal(18);
            try
            {
                var f = new byte[18];
                BitConverter.GetBytes((short)1).CopyTo(f, 0);
                BitConverter.GetBytes((short)2).CopyTo(f, 2);
                BitConverter.GetBytes(TrackMixer.Rate).CopyTo(f, 4);
                BitConverter.GetBytes(TrackMixer.Rate * 4).CopyTo(f, 8);
                BitConverter.GetBytes((short)4).CopyTo(f, 12);
                BitConverter.GetBytes((short)16).CopyTo(f, 14);
                Marshal.Copy(f, 0, format, 18);
                // Shared, Windows converts 48 kHz 16-bit to the speakers' own format; an 80 ms buffer (about 45 ms is kept in it).
                Check(client.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 800000, 0, format, IntPtr.Zero), "the speakers refused the sound");
            }
            finally { Marshal.FreeHGlobal(format); }
            Check(client.GetBufferSize(out bufferFrames), "no buffer");
            long lat;
            Latency = client.GetStreamLatency(out lat) >= 0 ? Math.Min(0.1, lat / 1e7) : 0.02;
            Guid ri = typeof(IAudioRenderClient).GUID, ci = typeof(IAudioClock).GUID, vi = typeof(IAudioStreamVolume).GUID;
            Check(client.GetService(ref ri, out o), "no render client"); render = (IAudioRenderClient)o;
            Check(client.GetService(ref ci, out o), "no clock"); clock = (IAudioClock)o;
            if (client.GetService(ref vi, out o) >= 0) volume = (IAudioStreamVolume)o;
            Check(clock.GetFrequency(out frequency), "no clock frequency");
        }

        public int Buffer { get { return bufferFrames; } }
        public bool SpeakersChanged { get { return SpeakerWatch.Changes != changesAtStart; } }

        public long Played
        {
            get
            {
                long pos, qpc;
                int hr = clock.GetPosition(out pos, out qpc);
                if (hr < 0) throw new Exception("the speakers went away (0x" + hr.ToString("X8") + ")");
                return Math.Min(written, (long)(pos * (double)TrackMixer.Rate / frequency));
            }
        }

        public int Write(short[] frames, int offset, int count)
        {
            int padding;
            int hr = client.GetCurrentPadding(out padding);
            if (hr < 0) throw new Exception("the speakers went away (0x" + hr.ToString("X8") + ")");
            int n = Math.Min(count, bufferFrames - padding);
            if (n <= 0) return 0;
            IntPtr p;
            Check(render.GetBuffer(n, out p), "the speakers took no sound");
            Marshal.Copy(frames, offset * 2, p, n * 2);
            render.ReleaseBuffer(n, 0);
            written += n;
            if (!started) { client.Start(); started = true; }
            return n;
        }

        // The level of the stream itself (0..1): heard within Windows' next period.
        public void SetLevel(float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            if (volume == null || Math.Abs(v - level) < 0.0005) return;
            level = v;
            volume.SetAllVolumes(2, new[] { v, v });
        }
        public bool CanLevel { get { return volume != null; } }

        public void Flush()
        {
            if (client == null) return;
            client.Stop();
            client.Reset();
            started = false;
            written = 0;
        }

        public void Dispose()
        {
            try { if (client != null && started) client.Stop(); } catch { }
            foreach (object o in new object[] { volume, clock, render, client }) if (o != null) try { Marshal.ReleaseComObject(o); } catch { }
            volume = null; clock = null; render = null; client = null;
        }
    }
}
