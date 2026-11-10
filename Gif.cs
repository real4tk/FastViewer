// FastViewer: writes animated GIFs by itself (no other program needed). A GIF has at most 256 colours:
// GifColours looks at every picture first and picks the 255 that fit them best; GifWriter then stores each
// picture with those, keeping only what changed since the picture before.

using System;
using System.Collections.Generic;
using System.IO;

namespace FastViewer
{
    class GifColours
    {
        // Colours are counted in 32768 groups (5 bits each of red, green and blue).
        readonly int[] count = new int[32768];
        readonly long[] sumR = new long[32768], sumG = new long[32768], sumB = new long[32768];
        public byte[] Palette = new byte[768]; // 256 x red, green, blue (the last one is never used as a colour)
        public byte[] Nearest = new byte[32768]; // colour group -> the palette colour closest to it

        public void Add(int[] px, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int p = px[i], r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                int bin = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                count[bin]++;
                sumR[bin] += r;
                sumG[bin] += g;
                sumB[bin] += b;
            }
        }

        class Box
        {
            public int Start, Length, Axis;
            public double Spread;
            public long Count;
        }

        // How far a box's colours lie apart, along the direction they spread most (green counts most to the eye).
        Box Measure(int[] all, int start, int length)
        {
            var box = new Box { Start = start, Length = length };
            double n = 0;
            var s = new double[3];
            var s2 = new double[3];
            for (int i = start; i < start + length; i++)
            {
                int bin = all[i];
                double c = count[bin];
                n += c;
                for (int a = 0; a < 3; a++)
                {
                    double v = (bin >> (10 - 5 * a)) & 31;
                    s[a] += c * v;
                    s2[a] += c * v * v;
                }
            }
            box.Count = (long)n;
            for (int a = 0; a < 3; a++)
            {
                double spread = (s2[a] - s[a] * s[a] / Math.Max(1, n)) * (a == 0 ? 0.6 : a == 1 ? 1.0 : 0.35);
                if (spread > box.Spread) { box.Spread = spread; box.Axis = a; }
            }
            return box;
        }

        // "Median cut": the box of colours that's spread out most is cut in two halves with as many pixels each,
        // until there are 255 boxes; each box becomes its average colour.
        public void Build()
        {
            var bins = new List<int>();
            for (int i = 0; i < 32768; i++) if (count[i] > 0) bins.Add(i);
            var all = bins.ToArray();
            var boxes = new List<Box>();
            if (all.Length > 0) boxes.Add(Measure(all, 0, all.Length));
            while (boxes.Count < 255)
            {
                Box best = null;
                foreach (var b in boxes) if (b.Length > 1 && (best == null || b.Spread > best.Spread)) best = b;
                if (best == null) break;
                int shift = 10 - 5 * best.Axis;
                Array.Sort(all, best.Start, best.Length, Comparer<int>.Create((x, y) => ((x >> shift) & 31) - ((y >> shift) & 31)));
                long run = 0;
                int cut = best.Start + 1;
                for (int i = best.Start; i < best.Start + best.Length - 1; i++)
                {
                    run += count[all[i]];
                    cut = i + 1;
                    if (run >= best.Count / 2) break;
                }
                boxes.Remove(best);
                boxes.Add(Measure(all, best.Start, cut - best.Start));
                boxes.Add(Measure(all, cut, best.Start + best.Length - cut));
            }
            int colours = Math.Max(1, boxes.Count);
            for (int k = 0; k < boxes.Count; k++)
            {
                long n = 0, r = 0, g = 0, b = 0;
                for (int i = boxes[k].Start; i < boxes[k].Start + boxes[k].Length; i++)
                {
                    int bin = all[i];
                    n += count[bin];
                    r += sumR[bin];
                    g += sumG[bin];
                    b += sumB[bin];
                }
                n = Math.Max(1, n);
                Palette[k * 3] = (byte)(r / n);
                Palette[k * 3 + 1] = (byte)(g / n);
                Palette[k * 3 + 2] = (byte)(b / n);
            }
            for (int bin = 0; bin < 32768; bin++)
            {
                int r, g, b;
                if (count[bin] > 0) { r = (int)(sumR[bin] / count[bin]); g = (int)(sumG[bin] / count[bin]); b = (int)(sumB[bin] / count[bin]); }
                else { r = ((bin >> 10) & 31) * 8 + 4; g = ((bin >> 5) & 31) * 8 + 4; b = (bin & 31) * 8 + 4; }
                int nearest = 0, least = int.MaxValue;
                for (int k = 0; k < colours; k++)
                {
                    int dr = r - Palette[k * 3], dg = g - Palette[k * 3 + 1], db = b - Palette[k * 3 + 2];
                    int d = 2 * dr * dr + 4 * dg * dg + db * db;
                    if (d < least) { least = d; nearest = k; }
                }
                Nearest[bin] = (byte)nearest;
            }
        }
    }

    class GifWriter
    {
        const int SeeThrough = 255; // the palette number that means "leave what's there"
        // A fixed fine pattern of small brightness shifts, so smooth areas (skies, shadows) don't show bands.
        // It's the same in every picture, so things that stand still stay exactly the same and pack small.
        static readonly int[] Pattern = { -4, 0, -3, 1, 2, -2, 3, -1, -2, 2, -3, 1, 4, 0, 3, -1 };
        const int HashSize = 9973;

        readonly Stream file;
        readonly GifColours colours;
        readonly int w, h;
        byte[] shown;      // the palette numbers on screen after the pictures so far
        byte[] waiting;    // the last picture, written once it's known how long it stays (until the next one)
        int waitX, waitY, waitW, waitH, lastDelay = 7;
        long waitTime;
        public int Frames;

        readonly int[] keys = new int[HashSize], values = new int[HashSize];
        readonly byte[] block = new byte[255];
        int bitBuffer, bitCount, blockLength;

        public GifWriter(Stream file, int w, int h, GifColours colours)
        {
            this.file = file;
            this.w = w;
            this.h = h;
            this.colours = colours;
            Bytes(0x47, 0x49, 0x46, 0x38, 0x39, 0x61); // "GIF89a"
            Word(w);
            Word(h);
            Bytes(0xF7, 0, 0); // one palette of 256 colours for all pictures
            file.Write(colours.Palette, 0, 768);
            Bytes(0x21, 0xFF, 0x0B, 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30, 3, 1, 0, 0, 0); // plays over and over
        }

        void Bytes(params int[] values) { foreach (int v in values) file.WriteByte((byte)v); }
        void Word(int v) { file.WriteByte((byte)v); file.WriteByte((byte)(v >> 8)); }

        // The next picture (w x h pixels) and when it appears, in 100 ns units from the start.
        public void Add(int[] px, long time)
        {
            var now = new byte[w * h];
            var nearest = colours.Nearest;
            for (int y = 0, i = 0; y < h; y++)
                for (int x = 0; x < w; x++, i++)
                {
                    int p = px[i], d = Pattern[(y & 3) * 4 + (x & 3)];
                    int r = ((p >> 16) & 255) + d, g = ((p >> 8) & 255) + d, b = (p & 255) + d;
                    r = r < 0 ? 0 : r > 255 ? 255 : r;
                    g = g < 0 ? 0 : g > 255 ? 255 : g;
                    b = b < 0 ? 0 : b > 255 ? 255 : b;
                    now[i] = nearest[(r >> 3) << 10 | (g >> 3) << 5 | (b >> 3)];
                }
            if (waiting != null) WriteWaiting(time);
            if (shown == null)
            {
                waiting = now;
                waitX = waitY = 0;
                waitW = w;
                waitH = h;
            }
            else
            {
                // Only the box around what changed; inside it, pixels that stayed the same are "see-through".
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0, i = 0; y < h; y++)
                    for (int x = 0; x < w; x++, i++)
                        if (now[i] != shown[i])
                        {
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            y1 = y;
                        }
                if (x1 < 0) { x0 = y0 = x1 = y1 = 0; } // nothing changed: one see-through pixel keeps the timing
                waitX = x0;
                waitY = y0;
                waitW = x1 - x0 + 1;
                waitH = y1 - y0 + 1;
                waiting = new byte[waitW * waitH];
                for (int y = 0, j = 0; y < waitH; y++)
                {
                    int i = (y0 + y) * w + x0;
                    for (int x = 0; x < waitW; x++, i++, j++) waiting[j] = now[i] == shown[i] ? (byte)SeeThrough : now[i];
                }
            }
            shown = now;
            waitTime = time;
        }

        public void Finish()
        {
            if (waiting != null) WriteWaiting(-1);
            file.WriteByte(0x3B);
            file.Flush();
        }

        void WriteWaiting(long nextTime)
        {
            // GIF times are in hundredths of a second: rounded per picture from the real times, so they add up right.
            int delay = nextTime < 0 ? lastDelay : (int)(Math.Round(nextTime / 100000.0) - Math.Round(waitTime / 100000.0));
            delay = Math.Max(2, Math.Min(6000, delay)); // (browsers play anything under 2 slowly)
            lastDelay = delay;
            Bytes(0x21, 0xF9, 4, Frames == 0 ? 0x04 : 0x05); // stays on screen; later pictures have see-through pixels
            Word(delay);
            Bytes(SeeThrough, 0, 0x2C);
            Word(waitX);
            Word(waitY);
            Word(waitW);
            Word(waitH);
            Bytes(0);
            Pack(waiting, waiting.Length);
            waiting = null;
            Frames++;
        }

        void Emit(int code, int size)
        {
            bitBuffer |= code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                block[blockLength++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bitCount -= 8;
                if (blockLength == 255)
                {
                    file.WriteByte(255);
                    file.Write(block, 0, 255);
                    blockLength = 0;
                }
            }
        }

        // GIF's own packing (LZW): runs of pixels seen before are stored as one number of 9 to 12 bits.
        void Pack(byte[] data, int n)
        {
            file.WriteByte(8);
            Array.Clear(keys, 0, HashSize);
            int size = 9, next = 258, max = 511;
            Emit(256, size); // "start afresh"
            int prefix = data[0];
            for (int i = 1; i < n; i++)
            {
                int c = data[i], key = (prefix << 8 | c) + 1;
                int slot = (int)((uint)key * 2654435761u % HashSize);
                while (keys[slot] != 0 && keys[slot] != key) if (++slot == HashSize) slot = 0;
                if (keys[slot] == key) { prefix = values[slot]; continue; }
                Emit(prefix, size);
                if (next > max) { size++; max = size == 12 ? 4096 : (1 << size) - 1; }
                if (next < 4096)
                {
                    keys[slot] = key;
                    values[slot] = next++;
                }
                else
                {
                    Emit(256, size);
                    Array.Clear(keys, 0, HashSize);
                    size = 9;
                    next = 258;
                    max = 511;
                }
                prefix = c;
            }
            Emit(prefix, size);
            if (next > max && size < 12) size++;
            Emit(257, size); // "the end"
            if (bitCount > 0) block[blockLength++] = (byte)bitBuffer;
            bitBuffer = bitCount = 0;
            if (blockLength > 0)
            {
                file.WriteByte((byte)blockLength);
                file.Write(block, 0, blockLength);
                blockLength = 0;
            }
            file.WriteByte(0);
        }
    }
}
