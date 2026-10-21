using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-editor-transforms &lt;folder&gt;. Turning, mirroring,
/// resizing, adding a margin and trimming a plain border change the pixels exactly as expected,
/// keep the marks on the same picture content, export at the new size and undo in one step.</summary>
internal static class SelfTestEditorTransforms
{
    private const int W = 60, H = 40;
    // Quadrants: top left red, top right green, bottom left blue, bottom right yellow.
    private static readonly Color[] Quadrant = [Color.FromRgb(220, 30, 30), Color.FromRgb(30, 200, 40), Color.FromRgb(30, 40, 220), Color.FromRgb(240, 220, 30)];

    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-editor-transforms") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            Run(report);
            UpscaleKeepsRedaction(report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "editor-transforms-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    private static BitmapSource Source()
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var c = Quadrant[(y < H / 2 ? 0 : 2) + (x < W / 2 ? 0 : 1)];
                int i = (y * W + x) * 4;
                px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
        bmp.Freeze();
        return bmp;
    }

    private static Color At(BitmapSource bmp, int x, int y)
    {
        var p = new byte[4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0);
        return Color.FromArgb(p[3], p[2], p[1], p[0]);
    }

    private static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static bool Same(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        int w = a.PixelWidth, h = a.PixelHeight;
        var pa = new byte[w * h * 4]; var pb = new byte[w * h * 4];
        new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0).CopyPixels(pa, w * 4, 0);
        new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(pb, w * 4, 0);
        return pa.AsSpan().SequenceEqual(pb);
    }

    /// <summary>Enlarging blends neighbouring pixels. The secret under a redaction must not bleed
    /// into the pixels just outside the scaled box.</summary>
    private static void UpscaleKeepsRedaction(List<string> report)
    {
        const int size = 40;
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = x >= 15 && x < 25 && y >= 15 && y < 25 ? (byte)255 : (byte)128;   // white secret on gray
                int i = (y * size + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        image.Freeze();
        var doc = new EditorDocument(image);
        doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(15, 15), new Point(25, 25), Colors.Black, 1));
        doc.Scale(size * 3, size * 3);
        var output = new FormatConvertedBitmap(doc.Render(), PixelFormats.Bgra32, null, 0);
        var all = new byte[size * 3 * size * 3 * 4];
        output.CopyPixels(all, size * 3 * 4, 0);
        int brightest = 0;
        for (int i = 0; i < all.Length; i += 4) brightest = Math.Max(brightest, Math.Max(all[i], Math.Max(all[i + 1], all[i + 2])));
        report.Add($"upscaled redaction: brightest pixel {brightest} (gray is 128)");
        Expect(brightest <= 130, "Enlarging let the redacted pixels bleed past the redaction.");
    }

    private static void Run(List<string> report)
    {
        var source = Source();
        var doc = new EditorDocument(source);
        doc.Add(new StepAnnotation(new Point(10, 10), 1, Colors.White, 8));   // on the red quadrant
        StepAnnotation Step() => (StepAnnotation)doc.State.Items[0];

        // Clockwise: 40 x 60, red at the top right, the step follows it.
        doc.Rotate(clockwise: true);
        Expect(doc.PixelWidth == H && doc.PixelHeight == W, "Rotating did not swap the size.");
        Expect(At(doc.Image, H - 3, 2) == Quadrant[0] && At(doc.Image, 2, 2) == Quadrant[2], "Clockwise turn put the corners in the wrong place.");
        Expect(Step().Center == new Point(H - 10, 10), "The step did not turn with the picture: " + Step().Center);
        Expect(doc.Render().PixelWidth == H, "Export did not take the turned size.");
        doc.Rotate(clockwise: false);
        Expect(Same(doc.Image, source) && Step().Center == new Point(10, 10), "Turning back did not restore the picture and step exactly.");
        report.Add("rotate clockwise and back: exact pixels and mark position PASS");

        // Mirror: red moves right, the step mirrors; twice restores.
        doc.Flip(horizontal: true);
        Expect(At(doc.Image, W - 3, 2) == Quadrant[0] && Step().Center == new Point(W - 10, 10), "Mirroring left to right went wrong.");
        doc.Flip(horizontal: true);
        doc.Flip(horizontal: false);
        Expect(At(doc.Image, 2, H - 3) == Quadrant[0] && Step().Center == new Point(10, H - 10), "Mirroring top to bottom went wrong.");
        doc.Flip(horizontal: false);
        Expect(Same(doc.Image, source), "Mirroring twice did not restore the picture.");
        report.Add("mirror both ways and back: exact PASS");

        // Resize to half: the step halves too; undo restores in one step.
        doc.Scale(W / 2, H / 2);
        Expect(doc.PixelWidth == W / 2 && doc.PixelHeight == H / 2 && doc.Render().PixelWidth == W / 2, "Resizing did not take the new size.");
        Expect(Step().Center == new Point(5, 5) && Math.Abs(Step().Width - 4) < 0.01, "The step did not scale with the picture.");
        Expect(At(doc.Image, 2, 2) == Quadrant[0] && At(doc.Image, W / 2 - 3, H / 2 - 3) == Quadrant[3], "The resized picture lost its corners.");
        doc.Undo();
        Expect(Same(doc.Image, source) && Step().Center == new Point(10, 10), "One undo did not restore the original size.");
        report.Add("resize to half and undo PASS");

        // Margin: a gray frame of 8 pixels; marks move in by 8. Auto crop trims it back exactly.
        var gray = Color.FromRgb(128, 128, 128);
        doc.AddMargin(8, gray);
        Expect(doc.PixelWidth == W + 16 && doc.PixelHeight == H + 16 && At(doc.Image, 0, 0) == gray && At(doc.Image, 8, 8) == Quadrant[0],
            "The margin is the wrong size or color.");
        Expect(Step().Center == new Point(18, 18), "Marks did not move inside the margin.");
        Expect(doc.AutoCrop(), "Auto crop found no border to trim.");
        Expect(Same(doc.Image, source) && Step().Center == new Point(10, 10), "Auto crop did not trim exactly the margin.");
        Expect(!doc.AutoCrop(), "Auto crop trimmed a picture that has no plain border.");
        report.Add("margin and auto crop round trip PASS");
    }
}
