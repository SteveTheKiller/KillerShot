using System.IO;

namespace KillerShot.Video;

/// <summary>
/// Animated GIF writer built for screen recordings. Each frame gets its own palette chosen by
/// median cut, only the rectangle that changed since the last frame is stored, and unchanged
/// pixels inside that rectangle are transparent, which is what keeps a screen GIF small. A frame
/// identical to the previous one is merged into it by lengthening its delay.
/// </summary>
internal sealed class GifEncoder : IVideoSink
{
    private readonly string _path;
    private readonly int _maxColors;
    private readonly double _scale;
    private readonly bool _loop;
    private FileStream? _file;
    private int _srcW, _srcH, _w, _h;
    private byte[]? _scaled;
    private ushort[]? _keys, _prevKeys;
    private bool _first = true;

    // The frame waiting for its delay, which is known only when the next frame arrives.
    private byte[]? _pendingBlock;
    private TimeSpan _pendingAt;

    public GifEncoder(string path, int maxColors, double scale, bool loop)
    {
        _path = path;
        _maxColors = Math.Clamp(maxColors, 2, 255);   // one index is kept for transparency
        _scale = Math.Clamp(scale, 0.1, 1.0);
        _loop = loop;
    }

    public int OutputWidth => _w;
    public int OutputHeight => _h;

    public Task StartAsync(int width, int height, int fps)
    {
        _srcW = width; _srcH = height;
        _w = Math.Max(1, (int)Math.Round(width * _scale));
        _h = Math.Max(1, (int)Math.Round(height * _scale));
        _scaled = new byte[_w * _h * 4];
        _keys = new ushort[_w * _h];
        _prevKeys = new ushort[_w * _h];
        _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);

        Write("GIF89a"u8);
        WriteU16(_w); WriteU16(_h);
        _file.WriteByte(0);   // no global color table
        _file.WriteByte(0);
        _file.WriteByte(0);
        if (_loop)
        {
            _file.Write([0x21, 0xFF, 0x0B]);
            Write("NETSCAPE2.0"u8);
            _file.Write([0x03, 0x01, 0x00, 0x00, 0x00]);   // loop forever
        }
        return Task.CompletedTask;
    }

    public void AddFrame(byte[] bgra, TimeSpan timestamp)
    {
        Scale(bgra);
        var keys = _keys!;
        var px = _scaled!;
        for (int i = 0, o = 0; i < keys.Length; i++, o += 4)
            keys[i] = (ushort)(((px[o + 2] >> 3) << 10) | ((px[o + 1] >> 3) << 5) | (px[o] >> 3));

        // The rectangle that changed since the previous frame.
        int x0 = 0, y0 = 0, x1 = _w - 1, y1 = _h - 1;
        if (!_first)
        {
            x0 = _w; y0 = _h; x1 = -1; y1 = -1;
            var prev = _prevKeys!;
            for (int y = 0; y < _h; y++)
            {
                int row = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    if (keys[row + x] == prev[row + x]) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    y1 = y;
                }
            }
            if (x1 < 0) return;   // nothing moved: the pending frame simply lasts longer
        }

        var block = EncodeFrame(x0, y0, x1 - x0 + 1, y1 - y0 + 1, !_first);
        FlushPending(timestamp);
        _pendingBlock = block;
        _pendingAt = timestamp;
        Array.Copy(keys, _prevKeys!, keys.Length);
        _first = false;
    }

    public Task FinishAsync(TimeSpan duration)
    {
        FlushPending(duration > _pendingAt ? duration : _pendingAt + TimeSpan.FromMilliseconds(100));
        _file?.WriteByte(0x3B);   // trailer
        _file?.Flush();
        _file?.Dispose();
        _file = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
    }

    private void FlushPending(TimeSpan next)
    {
        if (_pendingBlock == null) return;
        long remaining = Math.Max(2, (long)Math.Round((next - _pendingAt).TotalMilliseconds / 10));
        // Repeating the same leave-in-place image preserves long static intervals.
        // Avoid a one-centisecond tail, which browsers lengthen to ten.
        while (remaining > 0)
        {
            int cs = (int)Math.Min(remaining, 65535);
            if (remaining - cs == 1) cs--;
            _pendingBlock[4] = (byte)(cs & 0xFF);
            _pendingBlock[5] = (byte)(cs >> 8);
            _file!.Write(_pendingBlock);
            remaining -= cs;
        }
        _pendingBlock = null;
    }

    /// <summary>Box-filtered downscale, or a straight copy at 100%.</summary>
    private void Scale(byte[] src)
    {
        var dst = _scaled!;
        if (_w == _srcW && _h == _srcH) { Buffer.BlockCopy(src, 0, dst, 0, dst.Length); return; }
        double sx = (double)_srcW / _w, sy = (double)_srcH / _h;
        for (int y = 0; y < _h; y++)
        {
            int ya = (int)(y * sy), yb = Math.Max(ya + 1, Math.Min(_srcH, (int)((y + 1) * sy)));
            for (int x = 0; x < _w; x++)
            {
                int xa = (int)(x * sx), xb = Math.Max(xa + 1, Math.Min(_srcW, (int)((x + 1) * sx)));
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = ya; yy < yb; yy++)
                {
                    int o = (yy * _srcW + xa) * 4;
                    for (int xx = xa; xx < xb; xx++, o += 4) { b += src[o]; g += src[o + 1]; r += src[o + 2]; n++; }
                }
                int d = (y * _w + x) * 4;
                dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(r / n); dst[d + 3] = 255;
            }
        }
    }

    private byte[] EncodeFrame(int fx, int fy, int fw, int fh, bool useTransparency)
    {
        var keys = _keys!;
        var prev = _prevKeys!;
        var px = _scaled!;

        // Histogram of the colors that actually changed, with their summed true colors so the
        // palette uses the real average rather than the 5-bit bucket center.
        var count = new int[32768];
        var sumR = new long[32768]; var sumG = new long[32768]; var sumB = new long[32768];
        for (int y = fy; y < fy + fh; y++)
            for (int x = fx; x < fx + fw; x++)
            {
                int i = y * _w + x;
                if (useTransparency && keys[i] == prev[i]) continue;
                int k = keys[i], o = i * 4;
                count[k]++; sumB[k] += px[o]; sumG[k] += px[o + 1]; sumR[k] += px[o + 2];
            }

        var present = new List<int>();
        for (int k = 0; k < 32768; k++) if (count[k] > 0) present.Add(k);
        var palette = MedianCut(present, count, sumR, sumG, sumB, _maxColors);

        // Nearest palette entry for every color present.
        var lut = new short[32768];
        foreach (int k in present)
        {
            int r = (int)(sumR[k] / count[k]), g = (int)(sumG[k] / count[k]), b = (int)(sumB[k] / count[k]);
            int best = 0, bestD = int.MaxValue;
            for (int p = 0; p < palette.Count; p++)
            {
                var c = palette[p];
                int dr = c.R - r, dg = c.G - g, db = c.B - b;
                int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestD) { bestD = d; best = p; if (d == 0) break; }
            }
            lut[k] = (short)best;
        }

        int transparent = palette.Count;
        int used = palette.Count + (useTransparency ? 1 : 0);
        int bits = 1;
        while ((1 << bits) < used) bits++;

        var idx = new byte[fw * fh];
        for (int y = 0, n = 0; y < fh; y++)
            for (int x = 0; x < fw; x++, n++)
            {
                int i = (fy + y) * _w + fx + x;
                idx[n] = useTransparency && keys[i] == prev[i] ? (byte)transparent : (byte)lut[keys[i]];
            }

        using var ms = new MemoryStream();
        // Graphic control extension: disposal "leave in place", optional transparency. The delay
        // (bytes 4 and 5) is filled in when the next frame's time is known.
        ms.Write([0x21, 0xF9, 0x04, (byte)((1 << 2) | (useTransparency ? 1 : 0)), 0, 0, (byte)(useTransparency ? transparent : 0), 0]);
        ms.WriteByte(0x2C);
        WriteU16(ms, fx); WriteU16(ms, fy); WriteU16(ms, fw); WriteU16(ms, fh);
        ms.WriteByte((byte)(0x80 | (bits - 1)));   // local color table
        for (int p = 0; p < (1 << bits); p++)
        {
            if (p < palette.Count) { ms.WriteByte(palette[p].R); ms.WriteByte(palette[p].G); ms.WriteByte(palette[p].B); }
            else { ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); }
        }
        int minCode = Math.Max(2, bits);
        ms.WriteByte((byte)minCode);
        Lzw.Encode(ms, idx, minCode);
        return ms.ToArray();
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>Weighted median cut over the 15-bit color histogram.</summary>
    private static List<Rgb> MedianCut(List<int> present, int[] count, long[] sr, long[] sg, long[] sb, int max)
    {
        var result = new List<Rgb>();
        if (present.Count == 0) { result.Add(new Rgb(0, 0, 0)); return result; }
        Rgb Average(List<int> box)
        {
            long n = 0, r = 0, g = 0, b = 0;
            foreach (int k in box) { n += count[k]; r += sr[k]; g += sg[k]; b += sb[k]; }
            return new Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        if (present.Count <= max)
        {
            foreach (int k in present) result.Add(Average([k]));
            return result;
        }

        var boxes = new List<List<int>> { present };
        while (boxes.Count < max)
        {
            // Split the box with the widest channel range, weighted by how many pixels it holds.
            int pick = -1, axis = 0; long bestScore = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                var bx = boxes[i];
                if (bx.Count < 2) continue;
                int[] lo = [31, 31, 31], hi = [0, 0, 0];
                long weight = 0;
                foreach (int k in bx)
                {
                    int r = k >> 10, g = (k >> 5) & 31, b = k & 31;
                    lo[0] = Math.Min(lo[0], r); hi[0] = Math.Max(hi[0], r);
                    lo[1] = Math.Min(lo[1], g); hi[1] = Math.Max(hi[1], g);
                    lo[2] = Math.Min(lo[2], b); hi[2] = Math.Max(hi[2], b);
                    weight += count[k];
                }
                int a = 0;
                int range = hi[0] - lo[0];
                if (hi[1] - lo[1] > range) { a = 1; range = hi[1] - lo[1]; }
                if (hi[2] - lo[2] > range) { a = 2; range = hi[2] - lo[2]; }
                long score = (long)range * (long)Math.Sqrt(weight + 1);
                if (range > 0 && score > bestScore) { bestScore = score; pick = i; axis = a; }
            }
            if (pick < 0) break;
            var box = boxes[pick];
            int shift = axis == 0 ? 10 : axis == 1 ? 5 : 0;
            box.Sort((p, q) => ((p >> shift) & 31).CompareTo((q >> shift) & 31));
            long total = 0;
            foreach (int k in box) total += count[k];
            long half = total / 2, run = 0;
            int cut = 1;
            for (int i = 0; i < box.Count - 1; i++)
            {
                run += count[box[i]];
                if (run >= half) { cut = i + 1; break; }
                cut = i + 1;
            }
            boxes[pick] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }
        foreach (var bx in boxes) result.Add(Average(bx));
        return result;
    }

    private void Write(ReadOnlySpan<byte> b) => _file!.Write(b);
    private void WriteU16(int v) { _file!.WriteByte((byte)(v & 0xFF)); _file.WriteByte((byte)(v >> 8)); }
    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)(v >> 8)); }

    /// <summary>GIF's variable-width LZW, with the code-width and table-reset points giflib uses,
    /// packed into 255-byte data sub-blocks.</summary>
    internal static class Lzw
    {
        public static void Encode(Stream output, byte[] data, int minCodeSize)
        {
            int clear = 1 << minCodeSize, eoi = clear + 1;
            int codeSize = minCodeSize + 1, next = eoi + 1;
            var table = new Dictionary<int, int>(4096);
            var block = new byte[256];
            int blockLen = 0;
            int acc = 0, accBits = 0;

            void Byte(byte b)
            {
                block[1 + blockLen++] = b;
                if (blockLen == 255) { block[0] = 255; output.Write(block, 0, 256); blockLen = 0; }
            }
            void Emit(int code)
            {
                acc |= code << accBits;
                accBits += codeSize;
                while (accBits >= 8) { Byte((byte)(acc & 0xFF)); acc >>= 8; accBits -= 8; }
            }

            Emit(clear);
            if (data.Length == 0) { Emit(eoi); }
            else
            {
                int prefix = data[0];
                for (int i = 1; i < data.Length; i++)
                {
                    int k = data[i];
                    int key = (prefix << 8) | k;
                    if (table.TryGetValue(key, out int code)) { prefix = code; continue; }
                    Emit(prefix);
                    if (next >= (1 << codeSize) && codeSize < 12) codeSize++;
                    if (next >= 4095)
                    {
                        Emit(clear);
                        table.Clear();
                        codeSize = minCodeSize + 1;
                        next = eoi + 1;
                    }
                    else
                    {
                        table[key] = next++;
                    }
                    prefix = k;
                }
                Emit(prefix);
                if (next >= (1 << codeSize) && codeSize < 12) codeSize++;
                Emit(eoi);
            }
            if (accBits > 0) Byte((byte)(acc & 0xFF));
            if (blockLen > 0) { block[0] = (byte)blockLen; output.Write(block, 0, blockLen + 1); }
            output.WriteByte(0);   // block terminator
        }
    }
}
