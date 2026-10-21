using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-paste-images &lt;folder&gt;. Pasted pictures are
/// read from PNG (keeping transparency) before a standard bitmap, oversized ones are refused, and a
/// picture placed on a capture is an object that exports, keeps its shape when resized, turns with
/// the capture and survives the album's mark format. Uses its own data objects, never the clipboard.</summary>
internal static class SelfTestPasteImages
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-paste-images") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        Window? window = null;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            Run(report, ref window);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        window?.Close();
        File.WriteAllLines(Path.Combine(folder, "paste-images-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    private static BitmapSource Picture(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] At(BitmapSource bmp, int x, int y)
    {
        var p = new byte[4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0);
        return p;
    }

    private static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static void Run(List<string> report, ref Window? window)
    {
        // PNG keeps transparency and wins over the bitmap variant.
        var translucent = Picture(30, 20, 200, 40, 40, 128);
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(ImageOutput.EncodePng(translucent)));
        data.SetData(DataFormats.Bitmap, Picture(30, 20, 0, 0, 255, 255));
        var read = ClipboardImages.Read(data) ?? throw new InvalidOperationException("The PNG clipboard picture was not read.");
        var p = At(read, 5, 5);
        Expect(read.PixelWidth == 30 && p[3] == 128 && p[2] == 200, "The PNG variant was not preferred or lost its transparency.");
        var bitmapOnly = new DataObject();
        bitmapOnly.SetData(DataFormats.Bitmap, Picture(16, 9, 10, 220, 10, 255));
        Expect(ClipboardImages.Read(bitmapOnly) is { PixelWidth: 16 } b && At(b, 1, 1)[1] == 220, "The standard bitmap variant was not read.");
        Expect(ClipboardImages.Read(new DataObject(DataFormats.UnicodeText, "not a picture")) == null, "Text was taken for a picture.");
        report.Add("PNG preferred with alpha, bitmap fallback, text ignored PASS");

        // A huge PNG header is refused before decoding.
        var huge = new MemoryStream(SelfTestImageImport.OversizedPng());
        var hugeData = new DataObject();
        hugeData.SetData("PNG", huge);
        try { ClipboardImages.Read(hugeData); throw new InvalidOperationException("A 900 megapixel paste was decoded."); }
        catch (ImageTooLargeException) { }
        report.Add("oversized paste refused PASS");
        var damaged = new DataObject();
        damaged.SetData("PNG", new MemoryStream([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]));
        damaged.SetData(DataFormats.Bitmap, Picture(8, 8, 1, 2, 3, 255));
        Expect(ClipboardImages.Read(damaged) is { PixelWidth: 8 }, "A damaged PNG did not fall back to the bitmap variant.");
        report.Add("damaged PNG falls back to the bitmap PASS");

        // Placed on a capture: an object that exports, keeps its shape and turns with the capture.
        var canvas = new EditorCanvas();
        window = new Window { Content = canvas, Width = 500, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        window.Show();
        var doc = new EditorDocument(Picture(300, 200, 20, 20, 20, 255));
        canvas.Document = doc;
        window.UpdateLayout();
        var logo = Picture(40, 20, 250, 250, 0, 255);
        Expect(canvas.InsertImage(logo) && canvas.Selected is ImageAnnotation, "The picture was not placed and selected.");
        var placed = (ImageAnnotation)canvas.Selected!;
        var center = new Point(placed.Box.X + placed.Box.Width / 2, placed.Box.Y + placed.Box.Height / 2);
        Expect(At(doc.Render(), (int)center.X, (int)center.Y)[2] == 250, "The placed picture did not export.");
        canvas.ResizeSelected(new Vector(40, 0));
        var resized = (ImageAnnotation)canvas.Selected!;
        Expect(Math.Abs(resized.Box.Width / resized.Box.Height - 2) < 0.01 && resized.Box.Width == placed.Box.Width + 40, "Resizing did not keep the picture's shape.");
        doc.Rotate(clockwise: true);
        var turned = doc.State.Items.OfType<ImageAnnotation>().Single();
        Expect(turned.Image.PixelWidth == 20 && turned.Image.PixelHeight == 40 && Math.Abs(turned.Box.Height / turned.Box.Width - 2) < 0.01,
            "Turning the capture did not turn the placed picture.");
        report.Add("placed object exports, keeps shape, turns with the capture PASS");

        // The album's mark format keeps the placed picture's pixels and box.
        var again = MarksFormat.Read(MarksFormat.Write(doc.State.Items))?.OfType<ImageAnnotation>().Single()
            ?? throw new InvalidOperationException("The placed picture did not survive the mark format.");
        Expect(again.Box == turned.Box && again.Image.PixelWidth == 20 && At(again.Image, 3, 3)[2] == 250, "The stored placed picture changed.");
        report.Add("placed picture round trips through the album format PASS");
    }
}
