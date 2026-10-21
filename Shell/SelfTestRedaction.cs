using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-redaction &lt;folder&gt;. Redaction boxes at
/// fractional coordinates must turn every pixel they touch opaque black in the exported PNG, after a
/// fractional move and after a crop, and leave every other pixel untouched.</summary>
internal static class SelfTestRedaction
{
    private const int Size = 12;

    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-redaction") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            var source = Pattern();
            var box = new ShapeAnnotation(ShapeKind.Redact, new Point(2.25, 3.5), new Point(5.5, 7.75), Colors.Black, 1);

            var doc = new EditorDocument(source);
            doc.Add(box);
            Check(report, "placed", folder, doc, source, 0, 0, box);

            var moved = (ShapeAnnotation)box.Offset(new Vector(1.4, 0.3));
            doc.Replace(box, moved);
            Check(report, "moved", folder, doc, source, 0, 0, moved);

            if (!doc.Crop(new Int32Rect(1, 2, 9, 9))) throw new InvalidOperationException("Crop was refused.");
            Check(report, "cropped", folder, doc, source, 1, 2, (ShapeAnnotation)doc.State.Items[0]);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "redaction-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    /// <summary>Every pixel a different, non-black color so a partial cover shows up as a mix.</summary>
    private static BitmapSource Pattern()
    {
        var px = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                px[i] = (byte)(40 + x * 17); px[i + 1] = (byte)(60 + y * 15); px[i + 2] = (byte)(240 - x * 9); px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, px, Size * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void Check(List<string> report, string name, string folder, EditorDocument doc, BitmapSource source, int ox, int oy, ShapeAnnotation box)
    {
        // Encode and decode independently, the way a recipient would see the file.
        string path = Path.Combine(folder, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(doc.Render()));
        using (var stream = File.Create(path)) encoder.Save(stream);
        BitmapSource decoded;
        using (var stream = File.OpenRead(path))
            decoded = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        int w = decoded.PixelWidth, h = decoded.PixelHeight;
        var got = new byte[w * h * 4];
        decoded.CopyPixels(got, w * 4, 0);
        var src = new byte[Size * Size * 4];
        source.CopyPixels(src, Size * 4, 0);

        var r = box.Box;
        int covered = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, s = ((y + oy) * Size + x + ox) * 4;
                bool touched = x + 1 > r.Left && x < r.Right && y + 1 > r.Top && y < r.Bottom;
                if (touched)
                {
                    covered++;
                    if (got[i] != 0 || got[i + 1] != 0 || got[i + 2] != 0 || got[i + 3] != 255)
                        throw new InvalidOperationException($"{name}: pixel {x},{y} under the redaction kept {got[i + 2]},{got[i + 1]},{got[i]},{got[i + 3]}.");
                }
                else if (got[i] != src[s] || got[i + 1] != src[s + 1] || got[i + 2] != src[s + 2] || got[i + 3] != src[s + 3])
                    throw new InvalidOperationException($"{name}: pixel {x},{y} outside the redaction changed.");
            }
        report.Add($"{name}: size={w}x{h} covered={covered} ok");
    }
}
