using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-album-marks &lt;folder&gt;. Marks made on an album
/// capture are kept with it: closing loses nothing, reopening brings them back editable, every
/// export from the album is the flattened result while the stored original never changes, a new
/// capture is linked once stored, and deleting the capture removes its marks.</summary>
internal static class SelfTestAlbumMarks
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-album-marks") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static BitmapSource Picture(byte shade)
    {
        var px = new byte[200 * 120 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = shade; px[i + 1] = 180; px[i + 2] = 90; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(200, 120, 96, 96, PixelFormats.Bgra32, null, px, 200 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Pixel(BitmapSource bmp, int x, int y)
    {
        var p = new byte[4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0);
        return p;
    }

    private static async Task WaitFor(Func<bool> condition, string failure)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        if (!condition()) throw new InvalidOperationException(failure);
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new album-marks test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            long id = CaptureLibrary.AddNow(Picture(30), DateTime.Now, "region", "Ticket", null);
            var item = CaptureLibrary.Items().Single(i => i.Id == id);

            // Edit: crop, a redaction and text; then replace the document. No prompt, nothing lost.
            App.Instance.OpenLibraryCapture(item);
            var doc = App.Instance.Document ?? throw new InvalidOperationException("The capture did not open.");
            doc.Crop(new Int32Rect(10, 10, 160, 100));
            doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
            doc.Add(new TextAnnotation(new Point(80, 30), "Fixed", Colors.Red, 18));
            if (!App.Instance.SetDocument(null)) throw new InvalidOperationException("Closing an edited album capture asked to discard it.");
            var saved = CaptureLibrary.LoadMarks(id) ?? throw new InvalidOperationException("The marks were not kept in the album.");
            if (saved.Items.Count != 2 || saved.Image.PixelWidth != 160 || saved.Image.PixelHeight != 100)
                throw new InvalidOperationException("The album did not keep the crop and both marks.");
            report.Add("edits kept on close without a discard prompt PASS");

            // Exports are flattened; the stored original is untouched.
            var output = CaptureLibrary.LoadOutput(item);
            if (output.PixelWidth != 160 || Pixel(output, 30, 30)[0] != 0 || Pixel(output, 30, 30)[1] != 0)
                throw new InvalidOperationException("Exporting from the album did not give the flattened, redacted picture.");
            var original = CaptureLibrary.LoadImage(item);
            if (original.PixelWidth != 200 || Pixel(original, 40, 40)[1] != 180)
                throw new InvalidOperationException("The stored original changed.");
            using (var png = new MemoryStream(CaptureLibrary.LoadOutputPng(id)))
                if (BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).PixelWidth != 160)
                    throw new InvalidOperationException("The raw PNG export is not the edited picture.");
            report.Add("album exports flattened, original unchanged PASS");

            // Reopen: the marks are back as marks, unchanged and editable.
            App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == id));
            doc = App.Instance.Document!;
            if (doc.Dirty || doc.State.Items.Count != 2 || doc.PixelWidth != 160 || doc.State.Items.OfType<TextAnnotation>().Single().Text != "Fixed")
                throw new InvalidOperationException("Reopening did not restore the editable marks.");
            var text = doc.State.Items.OfType<TextAnnotation>().Single();
            doc.Replace(text, text with { Text = "Fixed again" });
            App.Instance.SetDocument(null);
            if (CaptureLibrary.LoadMarks(id)!.Items.OfType<TextAnnotation>().Single().Text != "Fixed again")
                throw new InvalidOperationException("A second edit was not kept.");
            report.Add("reopen restores editable marks; later edits kept PASS");

            // Back to plain: undoing everything forgets the marks and exports the original.
            App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == id));
            doc = App.Instance.Document!;
            doc.Apply(new EditorState(doc.Original, System.Collections.Immutable.ImmutableList<Annotation>.Empty));
            App.Instance.SetDocument(null);
            if (CaptureLibrary.LoadMarks(id) != null || CaptureLibrary.LoadOutput(item).PixelWidth != 200)
                throw new InvalidOperationException("Removing every mark and the crop did not return to the original.");
            report.Add("plain again forgets marks PASS");

            // Reopened marks sit on the original itself: deleting them one by one returns to plain
            // too, rather than keeping a second copy of the original as a base picture.
            long plainId = CaptureLibrary.AddNow(Picture(90), DateTime.Now, "region", "Plain again", null);
            App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == plainId));
            App.Instance.Document!.Add(new StepAnnotation(new Point(40, 40), 1, Colors.Red, 30));
            App.Instance.SetDocument(null);
            App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == plainId));
            doc = App.Instance.Document!;
            foreach (var mark in doc.State.Items.ToList()) doc.Remove(mark);
            App.Instance.SetDocument(null);
            if (CaptureLibrary.LoadMarks(plainId) != null)
                throw new InvalidOperationException("Deleting every reopened mark kept the capture's marks and a copy of its original.");
            report.Add("reopened marks deleted one by one return to plain PASS");

            // A new capture opens before the album stores it; its marks follow once it is stored.
            var fresh = Picture(200);
            App.Instance.SetDocument(new EditorDocument(fresh));
            App.Instance.Document!.Add(new StepAnnotation(new Point(50, 50), 1, Colors.Red, 30));
            CaptureLibrary.Add(fresh, DateTime.Now, "region", "Fresh", null);
            await WaitFor(() => CaptureLibrary.Items().Any(i => i.Window == "Fresh"), "The new capture was not stored.");
            long freshId = CaptureLibrary.Items().Single(i => i.Window == "Fresh").Id;
            await WaitFor(() => CaptureLibrary.LoadMarks(freshId) != null, "The new capture's marks were not linked to it.");
            App.Instance.SetDocument(null);
            report.Add("new capture linked once stored PASS");

            // A newer format is refused rather than guessed at; deleting a capture removes its marks.
            if (MarksFormat.Read("{\"v\":2,\"marks\":[]}") != null || MarksFormat.Read("not json") != null)
                throw new InvalidOperationException("An unsupported marks format was accepted.");
            CaptureLibrary.Delete([freshId]);
            if (CaptureLibrary.LoadMarks(freshId) != null) throw new InvalidOperationException("Deleting a capture left its marks behind.");
            report.Add("unsupported versions refused; delete removes marks PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "album-marks-report.txt"), report);
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
