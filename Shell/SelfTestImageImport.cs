using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-image-import &lt;folder&gt;. Imported images
/// show the way camera and photo apps show them: every EXIF orientation in JPEG and TIFF, embedded
/// color profiles converted to sRGB, and files too large to decode safely refused from their header
/// alone, before any pixel memory is taken.</summary>
internal static class SelfTestImageImport
{
    // Source quadrants: top left red, top right green, bottom left blue, bottom right white.
    private static readonly (byte R, byte G, byte B)[] Quadrants = [(230, 30, 30), (30, 200, 40), (30, 40, 220), (250, 250, 250)];
    private const int W = 48, H = 32;

    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-image-import") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            Oversized(folder, report);
            foreach (var format in new[] { "jpg", "tif" })
                for (ushort orientation = 1; orientation <= 8; orientation++)
                    Orientation(folder, format, orientation, report);
            Profile(folder, report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "image-import-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    /// <summary>A valid PNG whose header claims 30000 x 30000 pixels, with almost no data behind it.</summary>
    internal static byte[] OversizedPng()
    {
        using var s = new MemoryStream();
        {
            s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, 30000);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 30000);
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(s, "IHDR", ihdr);
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(new byte[16]);
            Chunk(s, "IDAT", ms.ToArray());
            Chunk(s, "IEND", []);
        }
        return s.ToArray();
    }

    private static void Oversized(string folder, List<string> report)
    {
        string path = Path.Combine(folder, "oversized.png");
        File.WriteAllBytes(path, OversizedPng());
        long before = GC.GetTotalMemory(true);
        try
        {
            ImageOutput.Load(path);
            throw new InvalidOperationException("A 900 megapixel header was decoded.");
        }
        catch (ImageTooLargeException) { }
        long grew = GC.GetTotalMemory(false) - before;
        if (grew > 64L * 1024 * 1024) throw new InvalidOperationException($"Refusing the oversized image still took {grew / 1048576} MiB.");
        byte[] header = File.ReadAllBytes(path);
        try
        {
            QrImages.Load(header);
            throw new InvalidOperationException("QR input decoded a 900 megapixel header.");
        }
        catch (ImageTooLargeException) { }
        report.Add($"oversized: refused from header, managed growth {grew / 1024} KiB PASS");
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(n, (uint)data.Length);
        s.Write(n);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(n, Crc32(body));
        s.Write(n);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static BitmapSource Source()
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var c = Quadrants[(y < H / 2 ? 0 : 2) + (x < W / 2 ? 0 : 1)];
                int i = (y * W + x) * 4;
                px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Where a displayed pixel comes from in the stored pixels, per the EXIF definition.</summary>
    private static (int X, int Y) StoredAt(ushort o, int dx, int dy) => o switch
    {
        2 => (W - 1 - dx, dy),
        3 => (W - 1 - dx, H - 1 - dy),
        4 => (dx, H - 1 - dy),
        5 => (dy, dx),
        6 => (dy, H - 1 - dx),
        7 => (W - 1 - dy, H - 1 - dx),
        8 => (W - 1 - dy, dx),
        _ => (dx, dy),
    };

    private static void Orientation(string folder, string format, ushort orientation, List<string> report)
    {
        string path = Path.Combine(folder, $"orientation-{orientation}.{format}");
        BitmapEncoder encoder = format == "jpg" ? new JpegBitmapEncoder { QualityLevel = 100 } : new TiffBitmapEncoder();
        var metadata = new BitmapMetadata(format == "jpg" ? "jpg" : "tiff");
        metadata.SetQuery(format == "jpg" ? "/app1/ifd/{ushort=274}" : "/ifd/{ushort=274}", orientation);
        encoder.Frames.Add(BitmapFrame.Create(Source(), null, metadata, null));
        using (var s = File.Create(path)) encoder.Save(s);

        var loaded = ImageOutput.Load(path);
        bool swap = orientation >= 5;
        int dw = swap ? H : W, dh = swap ? W : H;
        if (loaded.PixelWidth != dw || loaded.PixelHeight != dh)
            throw new InvalidOperationException($"{format} orientation {orientation}: loaded {loaded.PixelWidth}x{loaded.PixelHeight}, expected {dw}x{dh}.");
        var px = new byte[dw * dh * 4];
        loaded.CopyPixels(px, dw * 4, 0);
        foreach (var (dx, dy) in new[] { (2, 2), (dw - 3, 2), (2, dh - 3), (dw - 3, dh - 3) })
        {
            var (sx, sy) = StoredAt(orientation, dx, dy);
            var want = Quadrants[(sy < H / 2 ? 0 : 2) + (sx < W / 2 ? 0 : 1)];
            int i = (dy * dw + dx) * 4;
            if (Math.Abs(px[i + 2] - want.R) > 12 || Math.Abs(px[i + 1] - want.G) > 12 || Math.Abs(px[i] - want.B) > 12)
                throw new InvalidOperationException($"{format} orientation {orientation}: pixel {dx},{dy} is {px[i + 2]},{px[i + 1]},{px[i]}, expected {want}.");
        }

        // Saving writes upright pixels with no orientation tag, so every viewer agrees.
        string saved = Path.Combine(folder, $"saved-{orientation}.{format}");
        ImageOutput.Save(loaded, Path.ChangeExtension(saved, "jpg"));
        using (var s = File.OpenRead(Path.ChangeExtension(saved, "jpg")))
        {
            var frame = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            object? tag = (frame.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/{ushort=274}");
            if (frame.PixelWidth != dw || (tag != null && Convert.ToInt32(tag) != 1))
                throw new InvalidOperationException($"{format} orientation {orientation}: the saved file would display turned.");
        }
        report.Add($"{format} orientation {orientation}: {dw}x{dh} PASS");
    }

    private static void Profile(string folder, List<string> report)
    {
        string icc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "Acer XF270H B.icm");
        if (!File.Exists(icc))
        {
            report.Add("profile: SKIPPED, the wide-gamut test profile is not installed on this machine");
            return;
        }
        var px = new byte[8 * 8 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 100; px[i + 2] = 40; px[i + 3] = 255; }
        var plain = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, px, 32);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(plain, null, null, new ReadOnlyCollection<ColorContext>([new ColorContext(new Uri(icc))])));
        string path = Path.Combine(folder, "profiled.png");
        using (var s = File.Create(path)) png.Save(s);

        // The independent expectation: the operating system's own conversion to sRGB.
        byte[] want = new byte[4];
        using (var s = File.OpenRead(path))
        {
            var frame = BitmapDecoder.Create(s, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            new ColorConvertedBitmap(frame, new ColorContext(new Uri(icc)), new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32)
                .CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), want, 4, 0);
        }
        var got = new byte[4];
        ImageOutput.Load(path).CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), got, 4, 0);
        if (want.Zip(got).Any(p => Math.Abs(p.First - p.Second) > 2))
            throw new InvalidOperationException($"profile: loaded {Convert.ToHexString(got)}, sRGB is {Convert.ToHexString(want)}.");
        report.Add($"profile: converted to sRGB {Convert.ToHexString(got)} PASS");
    }
}
