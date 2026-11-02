// FastViewer: saves videos with Windows' own video tools (Media Foundation), for "Make compatible copy"
// and "Trim". The result is a standard .mp4 (H.264 video, AAC sound) that plays and gets thumbnails
// everywhere, stored upright and in normal (non-HDR) colours.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FastViewer
{
    class CancelFlag
    {
        public volatile bool Cancelled;
    }

    static class VideoConverter
    {
        public static string LastInfo = "";
        // rotation: clockwise degrees to turn the picture (0/90/180/270). progress: 0..1.
        public static void Convert(string source, string target, TimeSpan? from, TimeSpan? to, int rotation,
                                   Action<double> progress, CancelFlag cancel)
        {
            Check(MFStartup(0x00020070, 0), "starting Windows' video tools");
            IMFSourceReader reader = null;
            IMFSinkWriter writer = null;
            try
            {
                // Reader: decodes the original. "Advanced video processing" converts to plain RGB pictures
                // and, where Windows supports it, HDR to normal colours.
                IMFAttributes ra;
                Check(MFCreateAttributes(out ra, 2), "setting up");
                ra.SetUINT32(ref G.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                Check(MFCreateSourceReaderFromURL(source, ra, out reader), "opening the video");
                reader.SetStreamSelection(ALL_STREAMS, false);
                Check(reader.SetStreamSelection(FIRST_VIDEO, true), "finding the picture in the video");
                bool hasAudio = reader.SetStreamSelection(FIRST_AUDIO, true) >= 0;

                IMFMediaType want;
                MFCreateMediaType(out want);
                want.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Video);
                want.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFVideoFormat_RGB32);
                want.SetUINT32(ref G.MF_MT_VIDEO_PRIMARIES, 2);   // BT.709 colours
                want.SetUINT32(ref G.MF_MT_TRANSFER_FUNCTION, 7); // sRGB brightness curve
                Check(reader.SetCurrentMediaType(FIRST_VIDEO, IntPtr.Zero, want), "decoding the video (is its codec installed?)");
                IMFMediaType vin;
                Check(reader.GetCurrentMediaType(FIRST_VIDEO, out vin), "reading the video format");
                long packed;
                Check(vin.GetUINT64(ref G.MF_MT_FRAME_SIZE, out packed), "reading the video size");
                int w = (int)(packed >> 32), h = (int)(packed & 0xFFFFFFFF);
                long rate;
                if (vin.GetUINT64(ref G.MF_MT_FRAME_RATE, out rate) < 0 || (rate & 0xFFFFFFFF) == 0) rate = (30L << 32) | 1;
                double fps = (double)(rate >> 32) / (rate & 0xFFFFFFFF);
                int stride;
                if (vin.GetUINT32(ref G.MF_MT_DEFAULT_STRIDE, out stride) < 0) stride = w * 4;
                bool bottomUp = stride < 0;

                // Windows' decoder may already have turned the picture using the file's own rotation tag.
                IMFMediaType native;
                int nativeTurn = 0, outTurn = 0, nw = w, nh = h;
                int[] nativeArea = null;
                if (reader.GetNativeMediaType(FIRST_VIDEO, 0, out native) >= 0)
                {
                    native.GetUINT32(ref G.MF_MT_VIDEO_ROTATION, out nativeTurn);
                    long np;
                    if (native.GetUINT64(ref G.MF_MT_FRAME_SIZE, out np) >= 0) { nw = (int)(np >> 32); nh = (int)(np & 0xFFFFFFFF); }
                    nativeArea = ReadArea(native, nw, nh);
                    Marshal.ReleaseComObject(native);
                }
                vin.GetUINT32(ref G.MF_MT_VIDEO_ROTATION, out outTurn);
                LastInfo = "native " + nw + "x" + nh + " rot " + nativeTurn + ", decoded " + w + "x" + h + " rot " + outTurn + ", asked " + rotation;
                if (nativeTurn != 0 && outTurn == 0)
                    rotation = ((rotation - nativeTurn) % 360 + 360) % 360; // only add what's still missing

                // Only the visible part: videos are often stored a few pixels bigger (e.g. 1088 rows for 1080).
                int ax = 0, ay = 0, aw = w, ah = h;
                var visible = ReadArea(vin, w, h);
                if (visible == null && nativeArea != null && (nativeTurn == 0 || outTurn == 0))
                {
                    // The original says which part is visible (the rest is padding at its bottom/right);
                    // turn that area the same way Windows turned the picture.
                    int x = nativeArea[0], y = nativeArea[1], cw = nativeArea[2], ch = nativeArea[3];
                    switch (nativeTurn == 0 ? 0 : nativeTurn)
                    {
                        case 90: visible = new[] { nh - (y + ch), x, ch, cw }; break;
                        case 180: visible = new[] { nw - (x + cw), nh - (y + ch), cw, ch }; break;
                        case 270: visible = new[] { y, nw - (x + cw), ch, cw }; break;
                        default: visible = nativeArea; break;
                    }
                    if (visible[0] < 0 || visible[1] < 0 || visible[0] + visible[2] > w || visible[1] + visible[3] > h) visible = null;
                }
                if (visible != null) { ax = visible[0]; ay = visible[1]; aw = visible[2]; ah = visible[3]; }                LastInfo += ", visible " + aw + "x" + ah;

                bool sideways = rotation == 90 || rotation == 270;
                int ow = (sideways ? ah : aw) & ~1, oh = (sideways ? aw : ah) & ~1; // H.264 wants even sizes

                IMFMediaType ain = null;
                int channels = 0, sampleRate = 0;
                if (hasAudio)
                {
                    IMFMediaType a;
                    MFCreateMediaType(out a);
                    a.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Audio);
                    a.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFAudioFormat_PCM);
                    a.SetUINT32(ref G.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    hasAudio = reader.SetCurrentMediaType(FIRST_AUDIO, IntPtr.Zero, a) >= 0 &&
                               reader.GetCurrentMediaType(FIRST_AUDIO, out ain) >= 0;
                    if (hasAudio)
                    {
                        ain.GetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, out channels);
                        ain.GetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, out sampleRate);
                        if (channels > 2 || (sampleRate != 44100 && sampleRate != 48000))
                        {
                            // The AAC encoder takes 44.1/48 kHz, mono or stereo: let Windows convert.
                            channels = Math.Min(Math.Max(channels, 1), 2);
                            sampleRate = 48000;
                            a.SetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, channels);
                            a.SetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
                            a.SetUINT32(ref G.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2);
                            a.SetUINT32(ref G.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, channels * 2 * sampleRate);
                            hasAudio = reader.SetCurrentMediaType(FIRST_AUDIO, IntPtr.Zero, a) >= 0 &&
                                       reader.GetCurrentMediaType(FIRST_AUDIO, out ain) >= 0;
                        }
                    }
                    if (!hasAudio) reader.SetStreamSelection(FIRST_AUDIO, false);
                }

                int videoIndex = -1, audioIndex = -1; // the reader reports samples by the file's own stream number
                for (int i = 0; i < 64; i++)
                {
                    IMFMediaType t;
                    if (reader.GetCurrentMediaType(i, out t) < 0) break;
                    bool selected;
                    reader.GetStreamSelection(i, out selected);
                    Guid major;
                    t.GetGUID(ref G.MF_MT_MAJOR_TYPE, out major);
                    if (selected && major == G.MFMediaType_Video && videoIndex < 0) videoIndex = i;
                    if (selected && major == G.MFMediaType_Audio && audioIndex < 0) audioIndex = i;
                    Marshal.ReleaseComObject(t);
                }

                // Writer: H.264 + AAC in an .mp4.
                IMFAttributes wa;
                MFCreateAttributes(out wa, 1);
                wa.SetUINT32(ref G.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, Environment.GetEnvironmentVariable("FASTVIEWER_NOHW") == null ? 1 : 0);
                if (File.Exists(target)) File.Delete(target);
                Check(MFCreateSinkWriterFromURL(target, IntPtr.Zero, wa, out writer), "creating the new file");

                IMFMediaType vo;
                MFCreateMediaType(out vo);
                vo.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Video);
                vo.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFVideoFormat_H264);
                vo.SetUINT32(ref G.MF_MT_AVG_BITRATE, (int)Math.Min(40e6, Math.Max(2e6, ow * (double)oh * fps * 0.12)));
                vo.SetUINT32(ref G.MF_MT_INTERLACE_MODE, 2); // progressive
                vo.SetUINT64(ref G.MF_MT_FRAME_SIZE, Pack(ow, oh));
                vo.SetUINT64(ref G.MF_MT_FRAME_RATE, rate);
                vo.SetUINT64(ref G.MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                vo.SetUINT32(ref G.MF_MT_MPEG2_PROFILE, 100); // H.264 High profile
                int vs;
                Check(writer.AddStream(vo, out vs), "setting up the H.264 encoder");

                IMFMediaType vi;
                MFCreateMediaType(out vi);
                vi.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Video);
                vi.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFVideoFormat_RGB32);
                vi.SetUINT32(ref G.MF_MT_INTERLACE_MODE, 2);
                vi.SetUINT64(ref G.MF_MT_FRAME_SIZE, Pack(ow, oh));
                vi.SetUINT64(ref G.MF_MT_FRAME_RATE, rate);
                vi.SetUINT64(ref G.MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                vi.SetUINT32(ref G.MF_MT_DEFAULT_STRIDE, ow * 4); // we hand over pictures top row first
                Check(writer.SetInputMediaType(vs, vi, IntPtr.Zero), "setting up the video encoder input");

                int aus = -1;
                if (hasAudio)
                {
                    IMFMediaType ao;
                    MFCreateMediaType(out ao);
                    ao.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Audio);
                    ao.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFAudioFormat_AAC);
                    ao.SetUINT32(ref G.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                    ao.SetUINT32(ref G.MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
                    ao.SetUINT32(ref G.MF_MT_AUDIO_NUM_CHANNELS, channels);
                    ao.SetUINT32(ref G.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000); // 192 kbit/s
                    Check(writer.AddStream(ao, out aus), "setting up the AAC sound encoder");
                    Check(writer.SetInputMediaType(aus, ain, IntPtr.Zero), "setting up the sound encoder input");
                }
                Check(writer.BeginWriting(), "starting to write");

                long start = from.HasValue ? from.Value.Ticks : 0; // TimeSpan ticks are Media Foundation's 100 ns units
                long end = to.HasValue ? to.Value.Ticks : long.MaxValue;
                long duration = Duration(reader);
                long span = Math.Max(1, Math.Min(end, duration > 0 ? duration : end) - start);
                // Players skip the start of each track as the file says ("edit list": phone pre-roll, encoder
                // delay). Windows' reader doesn't: it delivers those samples, stamped that amount later.
                // So a sample's real time is its stamp minus twice the skip; anything before 0 is dropped.
                long firstVideo = (long)(2 * VideoInfo.EditStart(source, "vide") * 1e7);
                long firstAudio = (long)(2 * VideoInfo.EditStart(source, "soun") * 1e7);
                LastInfo += ", skip video " + firstVideo / 20000 + " ms / sound " + firstAudio / 20000 + " ms";                if (start > 0) Seek(reader, start);

                bool videoDone = false, audioDone = !hasAudio;
                int[] inPx = null, outPx = null;
                while (!(videoDone && audioDone))
                {
                    if (cancel.Cancelled) return;
                    int index, flags;
                    long time;
                    IMFSample sample;
                    Check(reader.ReadSample(ANY_STREAM, 0, out index, out flags, out time, out sample), "reading the video");
                    if ((flags & 1) != 0) throw new Exception("Windows couldn't read this video.");
                    bool isVideo = index == videoIndex;
                    if ((flags & 2) != 0) // end of this stream
                    {
                        if (isVideo) videoDone = true; else audioDone = true;
                    }
                    if (sample == null) continue;
                    try
                    {
                        time -= isVideo ? firstVideo : firstAudio; // both tracks start at 0
                        if (time > end)
                        {
                            if (isVideo) videoDone = true; else audioDone = true;
                            continue;
                        }
                        if (time < start) continue; // before the clip (the reader starts at the keyframe before it)
                        long newTime = time - start;
                        if (isVideo)
                        {
                            if (rotation == 0 && !bottomUp && ow == w && oh == h && ax == 0 && ay == 0)
                            {
                                sample.SetSampleTime(newTime);
                                Check(writer.WriteSample(vs, sample), "writing the video");
                            }
                            else
                            {
                                if (inPx == null) { inPx = new int[w * h]; outPx = new int[ow * oh]; }
                                var turned = Turn(sample, inPx, outPx, w, h, ax, ay, aw, ah, ow, oh, rotation, bottomUp);
                                turned.SetSampleTime(newTime);
                                long d;
                                if (sample.GetSampleDuration(out d) >= 0) turned.SetSampleDuration(d);
                                Check(writer.WriteSample(vs, turned), "writing the video");
                                Marshal.ReleaseComObject(turned);
                            }
                            progress(Math.Min(1, Math.Max(0, (double)newTime / span)));
                        }
                        else if (index == audioIndex)
                        {
                            sample.SetSampleTime(newTime);
                            Check(writer.WriteSample(aus, sample), "writing the sound");
                        }
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                Check(writer.DoFinalize(), "finishing the file");
                progress(1);
            }
            finally
            {
                if (writer != null) Marshal.ReleaseComObject(writer);
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        // Copies one decoded picture into a new sample, turned clockwise and stored top row first.
        // (ax, ay, aw, ah) is the visible part of the w x h decoded picture.
        static IMFSample Turn(IMFSample sample, int[] inPx, int[] outPx, int w, int h, int ax, int ay, int aw, int ah,
                              int ow, int oh, int rotation, bool bottomUp)
        {
            IMFMediaBuffer buffer;
            Check(sample.ConvertToContiguousBuffer(out buffer), "reading a picture");
            IntPtr data;
            int max, length;
            buffer.Lock(out data, out max, out length);
            Marshal.Copy(data, inPx, 0, Math.Min(inPx.Length, length / 4));
            buffer.Unlock();
            Marshal.ReleaseComObject(buffer);

            for (int oy = 0; oy < oh; oy++)
            {
                int o = oy * ow;
                for (int ox = 0; ox < ow; ox++)
                {
                    int sx, sy;
                    switch (rotation)
                    {
                        case 90: sx = oy; sy = ah - 1 - ox; break;
                        case 180: sx = aw - 1 - ox; sy = ah - 1 - oy; break;
                        case 270: sx = aw - 1 - oy; sy = ox; break;
                        default: sx = ox; sy = oy; break;
                    }
                    sx += ax;
                    sy += ay;
                    if (bottomUp) sy = h - 1 - sy;
                    outPx[o + ox] = inPx[sy * w + sx];
                }
            }

            IMFMediaBuffer outBuffer;
            Check(MFCreateMemoryBuffer(ow * oh * 4, out outBuffer), "making a picture");
            outBuffer.Lock(out data, out max, out length);
            Marshal.Copy(outPx, 0, data, ow * oh);
            outBuffer.Unlock();
            outBuffer.SetCurrentLength(ow * oh * 4);
            IMFSample result;
            Check(MFCreateSample(out result), "making a picture");
            result.AddBuffer(outBuffer);
            Marshal.ReleaseComObject(outBuffer);
            return result;
        }

        // Small pictures from across a video, for the trim bar's filmstrip (in the background). For picture i of
        // `count`, it decodes the frame at the middle of that stretch of the video, shrinks it to `height` pixels
        // high, turns it like the viewer shows it (rotation, clockwise), and hands it over: frame(i, w, h, pixels).
        public static void Filmstrip(string source, int count, int height, int rotation, Action<int, int, int, int[]> frame, CancelFlag cancel)
        {
            Check(MFStartup(0x00020070, 0), "starting Windows' video tools");
            IMFSourceReader reader = null;
            try
            {
                IMFAttributes ra;
                Check(MFCreateAttributes(out ra, 1), "setting up");
                ra.SetUINT32(ref G.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
                Check(MFCreateSourceReaderFromURL(source, ra, out reader), "opening the video");
                reader.SetStreamSelection(ALL_STREAMS, false);
                Check(reader.SetStreamSelection(FIRST_VIDEO, true), "finding the picture in the video");
                IMFMediaType want;
                MFCreateMediaType(out want);
                want.SetGUID(ref G.MF_MT_MAJOR_TYPE, ref G.MFMediaType_Video);
                want.SetGUID(ref G.MF_MT_SUBTYPE, ref G.MFVideoFormat_RGB32);
                Check(reader.SetCurrentMediaType(FIRST_VIDEO, IntPtr.Zero, want), "decoding the video");
                IMFMediaType vin;
                Check(reader.GetCurrentMediaType(FIRST_VIDEO, out vin), "reading the video format");
                long packed;
                Check(vin.GetUINT64(ref G.MF_MT_FRAME_SIZE, out packed), "reading the video size");
                int w = (int)(packed >> 32), h = (int)(packed & 0xFFFFFFFF), stride;
                if (vin.GetUINT32(ref G.MF_MT_DEFAULT_STRIDE, out stride) < 0) stride = w * 4;
                bool bottomUp = stride < 0;
                // As when saving: Windows' decoder may already have turned the picture by the file's own tag.
                IMFMediaType native;
                int nativeTurn = 0, outTurn = 0;
                if (reader.GetNativeMediaType(FIRST_VIDEO, 0, out native) >= 0)
                {
                    native.GetUINT32(ref G.MF_MT_VIDEO_ROTATION, out nativeTurn);
                    Marshal.ReleaseComObject(native);
                }
                vin.GetUINT32(ref G.MF_MT_VIDEO_ROTATION, out outTurn);
                if (nativeTurn != 0 && outTurn == 0) rotation = ((rotation - nativeTurn) % 360 + 360) % 360;
                bool sideways = rotation == 90 || rotation == 270;
                int th = Math.Max(8, height), tw = Math.Max(8, (int)Math.Round((double)th * (sideways ? h : w) / (sideways ? w : h)));
                int sw = sideways ? th : tw, sh = sideways ? tw : th; // the small picture before turning
                long duration = Duration(reader);
                int pitch = Math.Max(w, Math.Abs(stride) / 4); // pixels per stored row (rows may be padded)
                var px = new int[pitch * h];
                for (int i = 0; i < count; i++)
                {
                    if (cancel.Cancelled) return;
                    long at = (long)(duration * (i + 0.5) / count);
                    try { Seek(reader, at); } catch { }
                    IMFSample sample = null;
                    long time = 0;
                    // The reader starts at the keyframe before `at`: decode on (at most ~a second) toward it.
                    for (int k = 0; k < 40; k++)
                    {
                        int index, flags;
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        sample = null;
                        if (reader.ReadSample(FIRST_VIDEO, 0, out index, out flags, out time, out sample) < 0 || (flags & 3) != 0) break;
                        if (sample != null && time >= at - 20000000 / 30) break;
                    }
                    if (sample == null) continue;
                    IMFMediaBuffer buffer;
                    if (sample.ConvertToContiguousBuffer(out buffer) >= 0)
                    {
                        IntPtr data;
                        int max, length;
                        buffer.Lock(out data, out max, out length);
                        Marshal.Copy(data, px, 0, Math.Min(px.Length, length / 4));
                        buffer.Unlock();
                        Marshal.ReleaseComObject(buffer);
                        // shrink (pick the nearest pixel; plenty for a 40-pixel picture), then turn
                        var small = new int[sw * sh];
                        for (int y = 0; y < sh; y++)
                        {
                            int sy = Math.Min(h - 1, (int)((y + 0.5) * h / sh));
                            if (bottomUp) sy = h - 1 - sy;
                            for (int x = 0; x < sw; x++) small[y * sw + x] = px[sy * pitch + Math.Min(w - 1, (int)((x + 0.5) * w / sw))] | unchecked((int)0xFF000000);
                        }
                        var turned = new int[tw * th];
                        for (int y = 0; y < sh; y++)
                            for (int x = 0; x < sw; x++)
                            {
                                int nx, ny;
                                switch (rotation)
                                {
                                    case 90: nx = sh - 1 - y; ny = x; break;
                                    case 180: nx = sw - 1 - x; ny = sh - 1 - y; break;
                                    case 270: nx = y; ny = sw - 1 - x; break;
                                    default: nx = x; ny = y; break;
                                }
                                turned[ny * tw + nx] = small[y * sw + x];
                            }
                        frame(i, tw, th, turned);
                    }
                    Marshal.ReleaseComObject(sample);
                }
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        static long Duration(IMFSourceReader reader)
        {
            IntPtr pv = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                if (reader.GetPresentationAttribute(MEDIASOURCE, ref G.MF_PD_DURATION, pv) < 0) return 0;
                return Marshal.ReadInt64(pv, 8);
            }
            finally { Marshal.FreeHGlobal(pv); }
        }

        static void Seek(IMFSourceReader reader, long time)
        {
            IntPtr pv = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 20); // VT_I8
                Marshal.WriteInt64(pv, 8, time);
                Guid timeFormat = Guid.Empty;
                Check(reader.SetCurrentPosition(ref timeFormat, pv), "jumping to the start of the clip");
            }
            finally { Marshal.FreeHGlobal(pv); }
        }

        // The visible part of a picture (x, y, width, height), from MF_MT_MINIMUM_DISPLAY_APERTURE; null if not given.
        static int[] ReadArea(IMFMediaType type, int w, int h)
        {
            IntPtr area = Marshal.AllocHGlobal(16);
            try
            {
                if (type.GetBlob(ref G.MF_MT_MINIMUM_DISPLAY_APERTURE, area, 16, IntPtr.Zero) < 0) return null;
                int x = Marshal.ReadInt16(area, 2), y = Marshal.ReadInt16(area, 6);
                int cw = Marshal.ReadInt32(area, 8), ch = Marshal.ReadInt32(area, 12);
                if (x < 0 || y < 0 || cw <= 0 || ch <= 0 || x + cw > w || y + ch > h || (cw == w && ch == h)) return null;
                return new[] { x, y, cw, ch };
            }
            finally { Marshal.FreeHGlobal(area); }
        }

        static long Pack(int hi, int lo) { return ((long)hi << 32) | (uint)lo; }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new Exception("Windows failed while " + what + " (error 0x" + hr.ToString("X8") + ").");
        }

        const int ALL_STREAMS = unchecked((int)0xFFFFFFFE), ANY_STREAM = unchecked((int)0xFFFFFFFE);
        const int FIRST_VIDEO = unchecked((int)0xFFFFFFFC), FIRST_AUDIO = unchecked((int)0xFFFFFFFD);
        const int MEDIASOURCE = unchecked((int)0xFFFFFFFF);

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, int size);
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);
    }

    // Media Foundation identifiers.
    static class G
    {
        public static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static Guid MFAudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static Guid MF_MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static Guid MF_MT_VIDEO_PRIMARIES = new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
        public static Guid MF_MT_TRANSFER_FUNCTION = new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93");
        public static Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
        public static Guid MF_MT_VIDEO_ROTATION = new Guid("c380465d-2271-428c-9b83-ecea3b4a85c1");
        public static Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new Guid("d7388766-18fe-48c6-a177-ee894867c8c4");
        public static Guid MF_PD_DURATION = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    }

    // ---- Media Foundation COM interfaces (only what's used; method order must match Windows) ----

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr got);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
    }

    // A media type starts with the same methods as IMFAttributes; only those are used here.
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr got);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, int size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, IntPtr got);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(int stream, int typeIndex, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
        [PreserveSig] int ReadSample(int stream, int controlFlags, out int actualStream, out int streamFlags, out long timestamp, out IMFSample sample);
        [PreserveSig] int Flush(int stream);
        [PreserveSig] int GetServiceForStream(int stream, ref Guid service, ref Guid iid, out IntPtr value);
        [PreserveSig] int GetPresentationAttribute(int stream, ref Guid attribute, IntPtr value);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetType, out int stream);
        [PreserveSig] int SetInputMediaType(int stream, IMFMediaType inputType, IntPtr encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int stream, IMFSample sample);
        [PreserveSig] int SendStreamTick(int stream, long timestamp);
        [PreserveSig] int PlaceMarker(int stream, IntPtr context);
        [PreserveSig] int NotifyEndOfSegment(int stream);
        [PreserveSig] int Flush(int stream);
        [PreserveSig] int DoFinalize();
        [PreserveSig] int GetServiceForStream(int stream, ref Guid service, ref Guid iid, out IntPtr value);
        [PreserveSig] int GetStatistics(int stream, IntPtr stats);
    }
}
