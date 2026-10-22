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
            InsertedImageRedaction(report);
            ResizePreservesImageLayers(report);
            BaseRedactionUsesOpaqueBlack(report);
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

    private static void InsertedImageRedaction(List<string> report)
    {
        Check("integer opaque", new Rect(0, 0, 40, 40), new Point(15, 15), new Point(25, 25), false, 120, 120);
        Check("fractional transparent", new Rect(0.25, 0.5, 39.5, 38.75), new Point(14.25, 13.75), new Point(25.75, 26.25), true, 99, 77);
        Check("clipped fractional", new Rect(-5.25, -3.5, 40, 40), new Point(8.5, 10.1), new Point(20.5, 22.5), true, 73, 51);
        Check("downscale", new Rect(0, 0, 40, 40), new Point(15, 15), new Point(25, 25), false, 23, 17);

        void Check(string name, Rect box, Point a, Point b, bool transparent, int width, int height)
        {
            EditorDocument Make(byte secret)
            {
                var doc = new EditorDocument(RedactionPicture(128, transparent));
                doc.Add(new ImageAnnotation(RedactionPicture(secret, transparent), box));
                doc.Add(new ShapeAnnotation(ShapeKind.Redact, a, b, Colors.Black, 1));
                return doc;
            }
            var dark = Make(0); var light = Make(255);
            var before = dark.State;
            var beforeExport = Export(dark);
            Expect(Same(dark.Render(), light.Render()) && Same(beforeExport, Export(light)), name + ": the fixture did not hide the original secret.");
            dark.Scale(width, height); light.Scale(width, height);
            bool renderedSame = Same(dark.Render(), light.Render()), exportedSame = Same(Export(dark), Export(light));
            report.Add($"inserted-image {name}: equal before resize=True, render after={renderedSame}, PNG after={exportedSame}");
            Expect(renderedSame && exportedSame, name + ": resizing exposed secret pixels under an inserted-image redaction.");
            Expect(dark.State.Items.Count == 2 && dark.State.Items[0] is ImageAnnotation && dark.State.Items[1] is ShapeAnnotation { Kind: ShapeKind.Redact }, name + ": resizing flattened or reordered editable objects.");
            var image = (ImageAnnotation)dark.State.Items[0];
            Expect(At(image.Image, 2, 2) == At(((ImageAnnotation)before.Items[0]).Image, 2, 2), name + ": resizing changed uncovered image pixels or alpha.");
            var after = dark.State;
            dark.Undo();
            Expect(ReferenceEquals(before, dark.State) && Same(beforeExport, Export(dark)), name + ": one undo did not restore the original objects and pixels.");
            dark.Redo();
            Expect(ReferenceEquals(after, dark.State), name + ": redo did not restore the safe resized state.");
            dark.Scale(137, 91); light.Scale(137, 91);
            Expect(Same(Export(dark), Export(light)), name + ": a second resize exposed the secret.");
        }
    }

    private static BitmapSource RedactionPicture(byte secret, bool transparent = false)
    {
        var px = new byte[40 * 40 * 4];
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
            {
                int i = (y * 40 + x) * 4;
                bool hidden = x >= 15 && x < 25 && y >= 15 && y < 25;
                byte v = hidden ? secret : (byte)128;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = transparent ? (hidden ? (byte)(secret == 0 ? 40 : 220) : (byte)96) : (byte)255;
            }
        var bmp = BitmapSource.Create(40, 40, 96, 96, PixelFormats.Bgra32, null, px, 160);
        bmp.Freeze();
        return bmp;
    }

    private static void ResizePreservesImageLayers(List<string> report)
    {
        var doc = new EditorDocument(RedactionPicture(128));
        doc.Add(new ImageAnnotation(RedactionPicture(255), new Rect(0, 0, 40, 40)));
        doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(15, 15), new Point(25, 25), Colors.Black, 1));
        var top = RedactionPicture(255);
        doc.Add(new ImageAnnotation(top, new Rect(18, 18, 4, 4)));
        doc.Scale(120, 120);
        Expect(doc.State.Items.Count == 3 && doc.State.Items[0] is ImageAnnotation && doc.State.Items[1] is ShapeAnnotation && doc.State.Items[2] is ImageAnnotation, "Resize changed the layer order or object types.");
        var upper = (ImageAnnotation)doc.State.Items[2];
        Expect(Same(upper.Image, top) && At(Export(doc), 60, 60) == Colors.White, "A redaction damaged a picture above it.");
        var moved = upper.Offset(new Vector(30, 0));
        doc.Replace(upper, moved);
        Expect(ReferenceEquals(doc.State.Items[2], moved) && At(Export(doc), 60, 60) == Colors.Black, "The resized image object could not be moved independently.");
        doc.Undo();
        Expect(At(Export(doc), 60, 60) == Colors.White, "Undo did not restore the moved image object.");
        report.Add("image layer order, independent movement, upper-layer pixels and undo PASS");
    }

    private static void BaseRedactionUsesOpaqueBlack(List<string> report)
    {
        EditorDocument Make(byte secret)
        {
            var doc = new EditorDocument(RedactionPicture(secret, true));
            doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(15, 15), new Point(25, 25), Color.FromArgb(40, 255, 32, 64), 1));
            return doc;
        }
        var dark = Make(0); var light = Make(255);
        Expect(Same(Export(dark), Export(light)), "The base-image fixture did not hide the secret.");
        dark.Scale(120, 120); light.Scale(120, 120);
        bool same = Same(Export(dark), Export(light));
        report.Add($"base-image redaction with stored translucent color: equal after resize={same}");
        Expect(same, "Resize used the stored redaction color instead of its opaque black rendering.");
    }

    private static BitmapSource Export(EditorDocument doc)
    {
        using var stream = new MemoryStream(Services.ImageOutput.EncodePng(doc.Render()));
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
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
