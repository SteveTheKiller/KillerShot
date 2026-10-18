namespace KillerTools.Qr;

/// <summary>A finder pattern: the 7x7 square in three corners of every code.</summary>
internal sealed record Finder(double X, double Y, double ModuleSize, int Hits);

/// <summary>Finds finder patterns by their 1:1:3:1:1 dark-light-dark-light-dark runs, checked across and down.</summary>
internal static class FinderLocator
{
    public static List<Finder> Find(BitImage image)
    {
        var found = new List<(double X, double Y, double Module, int Hits)>();
        var runs = new int[5];
        for (int y = 0; y < image.Height; y++)
        {
            Array.Clear(runs);
            int state = 0;   // runs[0..4]: dark, light, dark, light, dark
            for (int x = 0; x <= image.Width; x++)
            {
                bool dark = x < image.Width && image[x, y];
                if (dark)
                {
                    if ((state & 1) == 1) state++;
                    runs[state]++;
                    continue;
                }
                if ((state & 1) == 1) { runs[state]++; continue; }
                if (state < 4) { state++; runs[state]++; continue; }
                if (IsFinderRatio(runs))
                {
                    double centerX = x - runs[4] - runs[3] - runs[2] / 2.0;
                    TryAdd(image, found, centerX, y, runs);
                }
                // Keep the last dark, light, dark runs and carry on in the light run that just began.
                runs[0] = runs[2]; runs[1] = runs[3]; runs[2] = runs[4]; runs[3] = 1; runs[4] = 0;
                state = 3;
            }
        }
        return found.Select(f => new Finder(f.X, f.Y, f.Module, f.Hits)).ToList();
    }

    internal static bool IsFinderRatio(ReadOnlySpan<int> runs)
    {
        int total = 0;
        foreach (int r in runs) { if (r == 0) return false; total += r; }
        if (total < 7) return false;
        double module = total / 7.0, variance = module / 1.5;
        return Math.Abs(module - runs[0]) < variance && Math.Abs(module - runs[1]) < variance
            && Math.Abs(3 * module - runs[2]) < 3 * variance
            && Math.Abs(module - runs[3]) < variance && Math.Abs(module - runs[4]) < variance;
    }

    private static void TryAdd(BitImage image, List<(double X, double Y, double Module, int Hits)> found, double centerX, int row, int[] across)
    {
        int total = across.Sum();
        double? centerY = CrossCheck(image, centerX, row, total, vertical: true, out double moduleDown);
        if (centerY is null) return;
        double? refinedX = CrossCheck(image, centerX, centerY.Value, total, vertical: false, out double moduleAcross);
        if (refinedX is null) return;
        double module = (moduleDown + moduleAcross) / 2;
        for (int i = 0; i < found.Count; i++)
        {
            var f = found[i];
            if (Math.Abs(f.X - refinedX.Value) <= module * 1.5 && Math.Abs(f.Y - centerY.Value) <= module * 1.5
                && Math.Abs(f.Module - module) <= Math.Max(1, f.Module * 0.5))
            {
                int hits = f.Hits + 1;
                found[i] = ((f.X * f.Hits + refinedX.Value) / hits, (f.Y * f.Hits + centerY.Value) / hits, (f.Module * f.Hits + module) / hits, hits);
                return;
            }
        }
        found.Add((refinedX.Value, centerY.Value, module, 1));
    }

    /// <summary>Walks from the center along one axis, both ways, and checks the same 1:1:3:1:1 runs.
    /// Returns the refined center on that axis.</summary>
    private static double? CrossCheck(BitImage image, double cx, double cy, int expectedTotal, bool vertical, out double module)
    {
        module = 0;
        int x0 = (int)cx, y0 = (int)cy;
        int limit = vertical ? image.Height : image.Width;
        int start = vertical ? y0 : x0;
        bool At(int p) => vertical ? image[x0, p] : image[p, y0];
        if (start < 0 || start >= limit || !At(start)) return null;
        Span<int> runs = stackalloc int[5];
        int p = start;
        // Center run and the two runs above or left of it.
        while (p >= 0 && At(p)) { runs[2]++; p--; }
        while (p >= 0 && !At(p)) { runs[1]++; p--; }
        while (p >= 0 && At(p)) { runs[0]++; p--; }
        p = start + 1;
        while (p < limit && At(p)) { runs[2]++; p++; }
        while (p < limit && !At(p)) { runs[3]++; p++; }
        while (p < limit && At(p)) { runs[4]++; p++; }
        int total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
        if (5 * Math.Abs(total - expectedTotal) >= 2 * expectedTotal) return null;
        if (!IsFinderRatio(runs)) return null;
        module = total / 7.0;
        return p - runs[4] - runs[3] - runs[2] / 2.0;
    }
}
