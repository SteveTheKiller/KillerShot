using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-text-recognition &lt;folder&gt;. The editor reads
/// the text in a picture with Windows recognition on this computer, keeps its line breaks, reads
/// just a dragged area, and explains when no recognition language is installed. It never copies.</summary>
internal static class SelfTestTextRecognition
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-text-recognition") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static BitmapSource TextPicture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 260));
            var face = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText("INVOICE 4417", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 56, Brushes.Black, 1), new Point(40, 30));
            dc.DrawText(new FormattedText("PAYMENT OVERDUE", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 56, Brushes.Black, 1), new Point(40, 150));
        }
        var rtb = new RenderTargetBitmap(900, 260, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new text-recognition test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            App.Instance.SetDocument(new EditorDocument(TextPicture()));
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1100, Height = 760, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(300);

            if (TextRecognition.CreateRecognizer() == null)
            {
                await window.ReadPictureTextAsync(null, copy: false);
                if (window.TestTextStatus != LocaleManager.Loc("Str_Ocr_Unavailable")) throw new InvalidOperationException("No recognition language, but no explanation was shown.");
                report.Add("no recognition language installed: explained PASS (recognition itself SKIPPED on this machine)");
                code = 0;
                return;
            }
            report.Add("languages: " + string.Join(", ", TextRecognition.Languages.Select(l => l.Tag)));
            await window.ReadPictureTextAsync(null, copy: false);
            string all = window.TestRecognizedText;
            report.Add("whole picture: " + all.Replace("\n", " | "));
            var lines = all.Split('\n');
            if (lines.Length < 2 || !lines[0].Contains("4417") || !lines[^1].Contains("OVERDUE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The picture's two lines were not read as two lines.");
            await window.ReadPictureTextAsync(new Int32Rect(0, 130, 900, 130), copy: false);
            string region = window.TestRecognizedText;
            report.Add("region: " + region);
            if (!region.Contains("OVERDUE", StringComparison.OrdinalIgnoreCase) || region.Contains("4417"))
                throw new InvalidOperationException("Reading a region read text outside it.");
            report.Add("reads lines and a region locally PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "text-recognition-report.txt"), report);
            window?.Close();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
