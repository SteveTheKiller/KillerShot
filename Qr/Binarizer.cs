namespace KillerTools.Qr;

/// <summary>Turns luminance into dark (true) and light (false) pixels.</summary>
internal static class Binarizer
{
    /// <summary>Uses nearby blocks to separate ink from paper under uneven lighting.</summary>
    public static BitImage Local(byte[] luma, int width, int height)
    {
        const int block = 8;
        int columns = (width + block - 1) / block, rows = (height + block - 1) / block;
        var minimum = new byte[rows, columns];
        var maximum = new byte[rows, columns];
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                byte lo = 255, hi = 0;
                for (int y = row * block; y < Math.Min(height, (row + 1) * block); y++)
                    for (int x = col * block; x < Math.Min(width, (col + 1) * block); x++)
                    {
                        byte value = luma[y * width + x];
                        lo = Math.Min(lo, value); hi = Math.Max(hi, value);
                    }
                minimum[row, col] = lo; maximum[row, col] = hi;
            }
        var image = new BitImage(width, height);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                int lo = 255, hi = 0;
                for (int r = Math.Max(0, row - 2); r <= Math.Min(rows - 1, row + 2); r++)
                    for (int c = Math.Max(0, col - 2); c <= Math.Min(columns - 1, col + 2); c++)
                    {
                        lo = Math.Min(lo, minimum[r, c]); hi = Math.Max(hi, maximum[r, c]);
                    }
                int threshold = hi - lo >= 24 ? (hi + lo) / 2 : lo - 1;
                for (int y = row * block; y < Math.Min(height, (row + 1) * block); y++)
                    for (int x = col * block; x < Math.Min(width, (col + 1) * block); x++)
                        image.Dark[y * width + x] = luma[y * width + x] <= threshold;
            }
        return image;
    }

    /// <summary>One threshold for the whole image, chosen by Otsu's method.</summary>
    public static BitImage Global(byte[] luma, int width, int height)
    {
        var histogram = new int[256];
        foreach (byte v in luma) histogram[v]++;
        long total = luma.Length, sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += (long)i * histogram[i];
        long sumBack = 0, weightBack = 0;
        double best = -1;
        int threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0) continue;
            long weightFore = total - weightBack;
            if (weightFore == 0) break;
            sumBack += (long)t * histogram[t];
            double meanBack = (double)sumBack / weightBack, meanFore = (double)(sumAll - sumBack) / weightFore;
            double between = (double)weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > best) { best = between; threshold = t; }
        }
        var bits = new BitImage(width, height);
        for (int i = 0; i < luma.Length; i++) bits.Dark[i] = luma[i] <= threshold;
        return bits;
    }
}

/// <summary>A width-by-height grid of dark (true) and light pixels.</summary>
internal sealed class BitImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool[] Dark { get; } = new bool[width * height];

    public bool this[int x, int y] => Dark[y * Width + x];

    public BitImage Inverted()
    {
        var copy = new BitImage(Width, Height);
        for (int i = 0; i < Dark.Length; i++) copy.Dark[i] = !Dark[i];
        return copy;
    }

    /// <summary>The pixel under a point, or light outside the image.</summary>
    public bool At(double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        return ix >= 0 && iy >= 0 && ix < Width && iy < Height && Dark[iy * Width + ix];
    }
}
