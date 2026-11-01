// FastViewer: reads the rotation phones store in MP4/MOV files.
//
// Phones often save a video sideways and add a "display matrix" saying how to turn it.
// Windows' player ignores that, so FastViewer reads it here and turns the video itself.

using System;
using System.IO;
using System.Text;

namespace FastViewer
{
    static class VideoInfo
    {
        // Clockwise degrees (0, 90, 180 or 270) the video needs to be turned to look right.
        public static int Rotation(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long moov, moovEnd;
                    if (!FindChild(fs, 0, fs.Length, "moov", out moov, out moovEnd)) return 0;
                    long pos = moov;
                    long size, header;
                    string type;
                    while (ReadHeader(fs, pos, moovEnd, out size, out type, out header))
                    {
                        if (type == "trak")
                        {
                            int angle;
                            if (IsVideoTrack(fs, pos + header, pos + size) && TrackAngle(fs, pos + header, pos + size, out angle))
                                return angle;
                        }
                        pos += size;
                    }
                }
            }
            catch { }
            return 0;
        }

        // Length of one frame in seconds (1/30 if it can't be read): media time scale (mdhd) and the
        // duration of the first frames (stts) of the video track.
        public static double FrameSeconds(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long moov, moovEnd;
                    if (!FindChild(fs, 0, fs.Length, "moov", out moov, out moovEnd)) return 1 / 30.0;
                    long pos = moov, size, header;
                    string type;
                    while (ReadHeader(fs, pos, moovEnd, out size, out type, out header))
                    {
                        if (type == "trak" && IsVideoTrack(fs, pos + header, pos + size))
                        {
                            long mdia, mdiaEnd, mdhd, mdhdEnd, minf, minfEnd, stbl, stblEnd, stts, sttsEnd;
                            if (!FindChild(fs, pos + header, pos + size, "mdia", out mdia, out mdiaEnd)) break;
                            if (!FindChild(fs, mdia, mdiaEnd, "mdhd", out mdhd, out mdhdEnd)) break;
                            fs.Position = mdhd;
                            int version = fs.ReadByte();
                            var b = new byte[8];
                            fs.Position = mdhd + 4 + (version == 1 ? 16 : 8);
                            fs.Read(b, 0, 4);
                            double timescale = Int(b, 0);
                            if (!FindChild(fs, mdia, mdiaEnd, "minf", out minf, out minfEnd)) break;
                            if (!FindChild(fs, minf, minfEnd, "stbl", out stbl, out stblEnd)) break;
                            if (!FindChild(fs, stbl, stblEnd, "stts", out stts, out sttsEnd)) break;
                            fs.Position = stts + 8; // version/flags, entry count
                            fs.Read(b, 0, 8);       // first entry: sample count, sample duration
                            double delta = Int(b, 4);
                            if (timescale > 0 && delta > 0)
                            {
                                double seconds = delta / timescale;
                                if (seconds > 0.001 && seconds < 1) return seconds;
                            }
                            break;
                        }
                        pos += size;
                    }
                }
            }
            catch { }
            return 1 / 30.0;
        }

        // How much of a track's start players skip, in seconds: the first "edit list" entry of the
        // track (handler "vide" or "soun"). Phones use it to hide pre-roll; encoders for start-up delay.
        public static double EditStart(string file, string handler)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long moov, moovEnd;
                    if (!FindChild(fs, 0, fs.Length, "moov", out moov, out moovEnd)) return 0;
                    long pos = moov, size, header;
                    string type;
                    while (ReadHeader(fs, pos, moovEnd, out size, out type, out header))
                    {
                        long trak = pos + header, trakEnd = pos + size;
                        pos += size;
                        if (type != "trak" || Handler(fs, trak, trakEnd) != handler) continue;
                        long edts, edtsEnd, elst, elstEnd, mdia, mdiaEnd, mdhd, mdhdEnd;
                        if (!FindChild(fs, trak, trakEnd, "edts", out edts, out edtsEnd)) return 0;
                        if (!FindChild(fs, edts, edtsEnd, "elst", out elst, out elstEnd)) return 0;
                        if (!FindChild(fs, trak, trakEnd, "mdia", out mdia, out mdiaEnd)) return 0;
                        if (!FindChild(fs, mdia, mdiaEnd, "mdhd", out mdhd, out mdhdEnd)) return 0;
                        var b = new byte[20];
                        fs.Position = mdhd;
                        int mdhdVersion = fs.ReadByte();
                        fs.Position = mdhd + 4 + (mdhdVersion == 1 ? 16 : 8);
                        fs.Read(b, 0, 4);
                        double timescale = Int(b, 0);

                        fs.Position = elst;
                        int version = fs.ReadByte();
                        fs.Position = elst + 4;
                        fs.Read(b, 0, 4);
                        long entries = Int(b, 0);
                        int entrySize = version == 1 ? 20 : 12;
                        for (long e = 0; e < entries && e < 8; e++)
                        {
                            fs.Position = elst + 8 + e * entrySize;
                            fs.Read(b, 0, entrySize);
                            long mediaTime = version == 1
                                ? (long)(((ulong)Int(b, 8) << 32) | (ulong)Int(b, 12))
                                : (int)Int(b, 4);
                            if (mediaTime == -1) continue; // an empty edit (delay) - not used by phones
                            return timescale > 0 && mediaTime > 0 ? mediaTime / timescale : 0;
                        }
                        return 0;
                    }
                }
            }
            catch { }
            return 0;
        }

        static string Handler(Stream fs, long trak, long trakEnd)
        {
            long mdia, mdiaEnd, hdlr, hdlrEnd;
            if (!FindChild(fs, trak, trakEnd, "mdia", out mdia, out mdiaEnd)) return null;
            if (!FindChild(fs, mdia, mdiaEnd, "hdlr", out hdlr, out hdlrEnd)) return null;
            var h = new byte[12];
            fs.Position = hdlr;
            return fs.Read(h, 0, 12) == 12 ? Encoding.ASCII.GetString(h, 8, 4) : null;
        }

        static long Int(byte[] b, int i) // unsigned 32-bit big-endian
        {
            return ((long)b[i] << 24) | ((long)b[i + 1] << 16) | ((long)b[i + 2] << 8) | b[i + 3];
        }

        // The track header's matrix is [a b u / c d v / x y w]; the turn is atan2(b, a).
        static bool TrackAngle(Stream fs, long start, long end, out int angle)
        {
            angle = 0;
            long tkhd, tkhdEnd;
            if (!FindChild(fs, start, end, "tkhd", out tkhd, out tkhdEnd)) return false;
            fs.Position = tkhd;
            int version = fs.ReadByte();
            long matrixAt = tkhd + 4 + (version == 1 ? 32 : 20) + 16;
            if (matrixAt + 20 > tkhdEnd) return false;
            var m = new byte[20];
            fs.Position = matrixAt;
            if (fs.Read(m, 0, 20) != 20) return false;
            double a = Fixed(m, 0), b = Fixed(m, 4);
            double degrees = Math.Atan2(b, a) * 180 / Math.PI;
            angle = (((int)Math.Round(degrees / 90) * 90) % 360 + 360) % 360;
            return true;
        }

        static bool IsVideoTrack(Stream fs, long start, long end)
        {
            long mdia, mdiaEnd, hdlr, hdlrEnd;
            if (!FindChild(fs, start, end, "mdia", out mdia, out mdiaEnd)) return false;
            if (!FindChild(fs, mdia, mdiaEnd, "hdlr", out hdlr, out hdlrEnd)) return false;
            var h = new byte[12];
            fs.Position = hdlr;
            return fs.Read(h, 0, 12) == 12 && Encoding.ASCII.GetString(h, 8, 4) == "vide";
        }

        // Finds a box of the given type among the boxes in [start, end); returns where its content is.
        static bool FindChild(Stream fs, long start, long end, string wanted, out long contentStart, out long contentEnd)
        {
            long pos = start, size, header;
            string type;
            while (ReadHeader(fs, pos, end, out size, out type, out header))
            {
                if (type == wanted)
                {
                    contentStart = pos + header;
                    contentEnd = pos + size;
                    return true;
                }
                pos += size;
            }
            contentStart = contentEnd = 0;
            return false;
        }

        static bool ReadHeader(Stream fs, long pos, long end, out long size, out string type, out long header)
        {
            size = header = 0;
            type = null;
            if (pos + 8 > end) return false;
            var b = new byte[16];
            fs.Position = pos;
            if (fs.Read(b, 0, 8) != 8) return false;
            size = ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
            type = Encoding.ASCII.GetString(b, 4, 4);
            header = 8;
            if (size == 1) // 64-bit size follows
            {
                if (fs.Read(b, 8, 8) != 8) return false;
                size = 0;
                for (int i = 8; i < 16; i++) size = (size << 8) | b[i];
                header = 16;
            }
            else if (size == 0) size = end - pos; // box runs to the end
            return size >= header && pos + size <= end;
        }

        static double Fixed(byte[] m, int i) // signed 16.16 fixed point, big-endian
        {
            int v = (m[i] << 24) | (m[i + 1] << 16) | (m[i + 2] << 8) | m[i + 3];
            return v / 65536.0;
        }
    }
}
