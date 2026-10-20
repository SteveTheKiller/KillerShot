using System.IO;

namespace KillerTools.Qr;

/// <summary>Finds and reads QR codes in an image. Written from scratch from the QR code standard.</summary>
public static class QrReader
{
    /// <summary>Reads every QR code in an 8-bit grayscale image (0 black, 255 white), row by row with no padding.</summary>
    /// <exception cref="ArgumentNullException">The pixel buffer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">The buffer length is not exactly the pixel count.</exception>
    public static IReadOnlyList<QrResult> Read(byte[] gray, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gray);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (gray.LongLength != (long)width * height)
            throw new ArgumentException("The pixel buffer length must equal width times height.", nameof(gray));
        var results = new List<QrResult>();
        var image = Binarizer.Global(gray, width, height);
        ReadAll(image, results);
        // Light-on-dark codes (dark themes, terminals) read on the inverted image.
        ReadAll(image.Inverted(), results);
        var local = Binarizer.Local(gray, width, height);
        ReadAll(local, results);
        ReadAll(local.Inverted(), results);
        return results;
    }

    private static void ReadAll(BitImage image, List<QrResult> results)
    {
        var finders = FinderLocator.Find(image);
        var used = new HashSet<Finder>();
        foreach (var (a, b, c) in Triples(finders))
        {
            if (used.Contains(a) || used.Contains(b) || used.Contains(c)) continue;
            if (results.Any(r => Inside(r.Corners, a) || Inside(r.Corners, b) || Inside(r.Corners, c))) continue;
            var (tl, tr, bl) = Detector.Order(a, b, c);
            if (!Detector.Plausible(tl, tr, bl)) continue;
            double module = Detector.ModuleSize(image, tl, tr, bl);
            int dimension = Detector.Dimension(tl, tr, bl, module);
            if (dimension < 21 || dimension > 177) continue;
            QrResult? result = null;
            foreach (int size in new[] { dimension, dimension - 4, dimension + 4 })
            {
                if (size < 21 || size > 177) continue;
                result = Decode(image, Detector.Place(tl, tr, bl, size));
                if (result is not null) break;
                foreach (var alignment in Detector.Alignments(image, tl, tr, bl, size))
                {
                    result = Decode(image, Detector.Place(tl, tr, bl, size, alignment));
                    result ??= Decode(image, Detector.Place(tl, tr, bl, size, alignment, bilinear: true));
                    if (result is not null) break;
                }
                if (result is not null) break;
            }
            if (result is null) continue;
            results.Add(result);
            used.Add(a); used.Add(b); used.Add(c);
        }
    }

    /// <summary>Every three finders, most likely first: the ones confirmed on the most rows, with matching module sizes.</summary>
    private static IEnumerable<(Finder, Finder, Finder)> Triples(List<Finder> found)
    {
        var f = found.Where(x => x.Hits >= 2).OrderByDescending(x => x.Hits).Take(40).ToList();
        var triples = new List<(Finder, Finder, Finder, double)>();
        for (int i = 0; i < f.Count; i++)
            for (int j = i + 1; j < f.Count; j++)
                for (int k = j + 1; k < f.Count; k++)
                {
                    double mean = (f[i].ModuleSize + f[j].ModuleSize + f[k].ModuleSize) / 3;
                    double spread = (Math.Abs(f[i].ModuleSize - mean) + Math.Abs(f[j].ModuleSize - mean) + Math.Abs(f[k].ModuleSize - mean)) / mean;
                    triples.Add((f[i], f[j], f[k], (f[i].Hits + f[j].Hits + f[k].Hits) / (1 + 4 * spread)));
                }
        return triples.OrderByDescending(t => t.Item4).Select(t => (t.Item1, t.Item2, t.Item3));
    }

    /// <summary>Whether a finder lies inside a code already read (a data pattern that only looks like a finder).</summary>
    private static bool Inside(IReadOnlyList<QrPoint> quad, Finder f)
    {
        bool inside = false;
        for (int i = 0, j = quad.Count - 1; i < quad.Count; j = i++)
            if ((quad[i].Y > f.Y) != (quad[j].Y > f.Y)
                && f.X < (quad[j].X - quad[i].X) * (f.Y - quad[i].Y) / (quad[j].Y - quad[i].Y) + quad[i].X)
                inside = !inside;
        return inside;
    }

    private static QrResult? Decode(BitImage image, Placement placement)
    {
        var modules = Detector.Sample(image, placement);
        int version = (placement.Dimension - 17) / 4;
        if (MatrixDecoder.ReadVersion(modules) != version) return null;
        var format = MatrixDecoder.ReadFormat(modules);
        if (format is null) return null;
        var codewords = MatrixDecoder.ReadCodewords(modules, version, format.Value.Mask);
        var data = MatrixDecoder.Deinterleave(codewords, version, format.Value.Level, ReedSolomon.Correct);
        if (data is null) return null;
        string? text;
        try { text = Segments.Decode(data, version); }
        catch (InvalidDataException) { return null; }
        return text is null ? null : new QrResult(text, version, format.Value.Level, Detector.Corners(placement));
    }
}
