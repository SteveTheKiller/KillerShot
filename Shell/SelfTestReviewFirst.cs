using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Editor;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-review-first &lt;folder&gt;. With review first on,
/// a capture waits in the editor and nothing reaches the folder or clipboard; accepting saves the
/// edited picture, discarding or replacing it sends nothing, and with review off captures go out
/// at once. Captures a 1 x 1 pixel region into the test folder; launches no programs or email.</summary>
internal static class SelfTestReviewFirst
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-review-first") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        TrayHost? tray = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new review-first test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            string output = Path.Combine(folder, "output");
            Directory.CreateDirectory(output);
            CaptureSettings.OutputFolder = output;
            CaptureSettings.SaveToFolder = true;
            CaptureSettings.CopyToClipboard = false;   // this test never writes the user's clipboard
            CaptureSettings.PlaySound = false;
            CaptureSettings.DelaySeconds = 0;
            CaptureSettings.ReviewFirst = true;
            var monitors = FrozenDesktop.EnumerateMonitors();
            var primary = monitors.First(m => m.Primary).Bounds;
            RegionMemory.Remember(new Win32.RECT(primary.Left, primary.Top, primary.Left + 1, primary.Top + 1), monitors);
            tray = new TrayHost();
            var capture = App.Instance.TestUseCapture(new CaptureController(tray));
            uint clipboard = GetClipboardSequenceNumber();
            int Files() => Directory.GetFiles(output).Length;

            // Held for review: in the editor and the album, nowhere else.
            await capture.RepeatRegion();
            await WaitFor(() => App.Instance.EditorWindow?.InReview == true);
            var editor = App.Instance.EditorWindow ?? throw new InvalidOperationException("The editor did not open for review.");
            Expect(editor.InReview && Files() == 0, "A capture held for review was saved before it was accepted.");
            await WaitFor(() => CaptureLibrary.Count == 1);
            Expect(CaptureLibrary.Count == 1, "The album did not keep the reviewed capture's original.");
            report.Add("held for review: editor and album only, nothing saved PASS");

            // Accept: the edited (redacted) picture is what gets saved.
            var doc = App.Instance.Document!;
            doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(1, 1), Colors.Black, 1));
            Expect(editor.AcceptReview() && !editor.InReview, "Copy and save did not accept the review.");
            Expect(Files() == 1, "Accepting did not save exactly one file.");
            var saved = BitmapFrame.Create(new Uri(Directory.GetFiles(output)[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var px = new byte[4];
            new FormatConvertedBitmap(saved, PixelFormats.Bgra32, null, 0).CopyPixels(px, 4, 0);
            Expect(px[0] == 0 && px[1] == 0 && px[2] == 0, "The saved file is not the edited, redacted picture.");
            report.Add("accept saves the edited picture PASS");

            // Discard, and replacing the picture, send nothing.
            await capture.RepeatRegion();
            await WaitFor(() => editor.InReview);
            editor.TestDiscardReview();
            Expect(!editor.InReview && App.Instance.Document == null && Files() == 1, "Discarding a review sent something.");
            await capture.RepeatRegion();
            await WaitFor(() => editor.InReview);
            App.Instance.SetDocument(new EditorDocument(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
            await Task.Delay(100);
            Expect(!editor.InReview && Files() == 1, "Replacing a picture under review sent it.");
            report.Add("discard and replacement send nothing PASS");

            // Review off: out at once.
            CaptureSettings.ReviewFirst = false;
            await capture.RepeatRegion();
            await WaitFor(() => Files() == 2);
            Expect(Files() == 2 && !editor.InReview, "With review off the capture was not saved at once.");
            Expect(GetClipboardSequenceNumber() == clipboard, "The clipboard changed during the test.");
            report.Add("review off publishes at once; clipboard untouched PASS");

            // Programs: only existing .exe paths are kept or started.
            Handoff.Apps = [@"C:\does\not\exist.exe", Path.Combine(folder, "notes.txt"), "relative.exe"];
            Expect(Handoff.Apps.Count == 0, "Open with kept a program that is missing, relative or not an .exe.");
            Expect(Handoff.OpenWith(@"C:\does\not\exist.exe", Directory.GetFiles(output)[0]) != null, "A missing program was reported as started.");
            report.Add("open with accepts only existing programs PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "review-first-report.txt"), report);
            App.Instance.EditorWindow?.Close();
            tray?.Dispose();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
