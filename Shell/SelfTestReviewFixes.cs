using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-review-fixes &lt;folder&gt;. Crash paths from the
/// final review: a resize number too big for an int, a damaged image file on the clipboard, and the
/// album's focus walk from a focused Hyperlink.</summary>
internal static class SelfTestReviewFixes
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-review-fixes") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            // A custom size past int.MaxValue is clamped, so Scale refuses it as too large.
            Check(MainWindow.ParseResize("99999999999 x 720", 800, 600) == (int.MaxValue, 720), "An oversized width clamps to int.MaxValue.");
            Check(MainWindow.ParseResize("99999999999", 800, 600) is { Width: int.MaxValue }, "An oversized single width clamps.");
            Check(MainWindow.ParseResize("99999999999%", 800, 600) is { Width: int.MaxValue, Height: int.MaxValue }, "An oversized percent clamps.");
            Check(MainWindow.ParseResize("50%", 800, 600) == (400, 300), "A percent keeps the shape.");
            Check(MainWindow.ParseResize("1280 x 720", 800, 600) == (1280, 720), "Width and height set both.");
            Check(MainWindow.ParseResize("400", 800, 600) == (400, 300), "A width alone keeps the shape.");
            Check(MainWindow.ParseResize("big", 800, 600) == null, "Text that is not a size is refused.");
            report.Add("resize_parse=True");

            // A copied image file that does not decode reaches the paste handler as one of the two
            // exception types it reports (IOException or NotSupportedException), never anything else.
            var valid = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
                64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[64 * 64 * 4], 64 * 4)));
            encoder.Save(valid);
            byte[][] variants = [[0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6], valid.ToArray()[..40]];
            for (int i = 0; i < variants.Length; i++)
            {
                string damaged = Path.Combine(folder, $"damaged-{i}.png");
                File.WriteAllBytes(damaged, variants[i]);
                try
                {
                    ClipboardImages.Read(new DataObject(DataFormats.FileDrop, new[] { damaged }));
                    throw new InvalidOperationException("A damaged copied file must not read as a picture.");
                }
                catch (Exception error) when (error is IOException or NotSupportedException) { report.Add($"damaged_{i}={error.GetType().Name}"); }
            }

            // The focus walk climbs from a content element through its logical parent.
            var link = new Hyperlink(new Run("link"));
            var text = new TextBlock(link);
            Check(ReferenceEquals(LibraryView.UiParent(link), text), "A Hyperlink's parent is its TextBlock.");
            report.Add("hyperlink_focus_walk=True");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "review-fixes-report.txt"), report);
        Application.Current.Shutdown(code);
        return true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
