using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-redact-before-publish &lt;new folder&gt;. With
/// Scan every capture on, a capture is scanned before anything leaves: the saved file, the
/// clipboard payload and the album item all hold the box over the found text, and the editor's
/// document still has the box as a mark. A scan that finds nothing lets the original out.
/// Recognition and the clipboard are replaced by stand-ins the check controls, and the editor
/// is never shown, so this opens no windows and leaves the user's clipboard alone.</summary>
internal static class SelfTestRedactBeforePublish
{
    private static readonly Rect Email = new(260, 30, 560, 50);

    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-redact-before-publish", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }

    /// <summary>True when every pixel inside the found text's box is one solid color that is not
    /// the white page, that is, the text is covered.</summary>
    private static bool Covered(BitmapSource picture)
    {
        var inner = new Int32Rect((int)Email.X + 4, (int)Email.Y + 4, (int)Email.Width - 8, (int)Email.Height - 8);
        var px = new byte[inner.Width * inner.Height * 4];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(inner, px, inner.Width * 4, 0);
        var colors = new HashSet<int>();
        for (int i = 0; i < px.Length; i += 4) colors.Add(px[i] | px[i + 1] << 8 | px[i + 2] << 16);
        return colors.Count == 1 && !colors.Contains(0xFFFFFF);
    }

    private static BitmapSource Load(string path) =>
        BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        TrayHost? tray = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new redact-before-publish test folder.");
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
            CaptureSettings.Format = "png";
            CaptureSettings.SaveToFolder = true;
            CaptureSettings.AskToSave = false;
            CaptureSettings.CopyToClipboard = true;   // goes to the stand-in below, never the real clipboard
            CaptureSettings.OpenEditorAfterCapture = false;
            CaptureSettings.OpenWithApps = false;
            CaptureSettings.EmailCapture = false;
            CaptureSettings.Notify = false;
            CaptureSettings.PlaySound = false;
            CaptureSettings.ReviewFirst = false;
            RedactSettings.Kinds = RedactKinds.Email;
            RedactSettings.ScanEveryCapture = true;
            App.Instance.PresentDocumentReplacement = _ => true;

            tray = new TrayHost();
            var capture = App.Instance.TestUseCapture(new CaptureController(tray));
            var clipboard = new List<BitmapSource>();
            capture.CopyForTest = (picture, _) => clipboard.Add(picture);
            int reads = 0;
            capture.ReadWordsAsync = _ =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<IReadOnlyList<OcrWord>>?>([[new OcrWord("jane.doe@example.com", Email)]]);
            };

            var picture = SelfTestAutoRedact.Picture();
            Expect(!Covered(picture), "The test picture already looks covered.");
            await capture.DeliverAsync(picture, CaptureMode.Region, IntPtr.Zero, interval: false);
            await WaitFor(() => CaptureLibrary.Count == 1 && Directory.GetFiles(output).Length == 1);

            var files = Directory.GetFiles(output);
            Expect(files.Length == 1, $"Expected one saved file, found {files.Length}.");
            Expect(Covered(Load(files[0])), "The saved file shows the text the scan found.");
            report.Add("saved file holds the redaction box PASS");
            Expect(clipboard.Count == 1, $"Expected one clipboard copy, found {clipboard.Count}.");
            Expect(Covered(clipboard[0]), "The clipboard copy shows the text the scan found.");
            report.Add("clipboard payload holds the redaction box PASS");
            Expect(CaptureLibrary.Count == 1, "The album did not keep the capture.");
            Expect(Covered(CaptureLibrary.LoadImage(CaptureLibrary.Items().Single().Id)), "The album item shows the text the scan found.");
            report.Add("album item holds the redaction box PASS");
            Expect(Covered(capture.Last ?? throw new InvalidOperationException("No last capture.")), "The tray's last capture shows the found text.");
            report.Add("tray last capture holds the redaction box PASS");
            var doc = App.Instance.Document ?? throw new InvalidOperationException("The capture did not open as the document.");
            int boxes = doc.State.Items.OfType<ShapeAnnotation>().Count(s => s.Kind == ShapeKind.Redact);
            Expect(boxes == 1, $"The editor document has {boxes} redaction marks, expected 1.");
            Expect(!doc.Dirty && doc.FilePath == files[0], "The editor document is not the saved file.");
            report.Add("editor document keeps the box as a mark PASS");
            Expect(reads == 1, $"The capture was read {reads} times, expected once.");
            report.Add("one scan per capture PASS");

            // Nothing found: the original goes out as before.
            capture.ReadWordsAsync = _ => Task.FromResult<IReadOnlyList<IReadOnlyList<OcrWord>>?>([]);
            await capture.DeliverAsync(picture, CaptureMode.Region, IntPtr.Zero, interval: false);
            await WaitFor(() => Directory.GetFiles(output).Length == 2);
            Expect(clipboard.Count == 2 && !Covered(clipboard[1]), "A capture with nothing found did not go out as the original.");
            report.Add("nothing found publishes the original PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "redact-before-publish-report.txt"), report);
            App.Instance.EditorWindow?.Close();
            tray?.Dispose();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
