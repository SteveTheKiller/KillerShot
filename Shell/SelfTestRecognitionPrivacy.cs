using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Recognition privacy checks with synthetic pixels and controlled completion ordering.</summary>
internal static class SelfTestRecognitionPrivacy
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-recognition-privacy") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int exit = 1;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new scratch folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            var image = Fixture();
            if (QrImages.Read(image).Single().Text != "SYNTHETIC-REDACTION-SECRET-4417") throw new InvalidOperationException("QR fixture invalid.");
            var edit = new EditorDocument(image);
            edit.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(image.PixelWidth, image.PixelHeight), Colors.Black, 1));
            var flattened = edit.Render();
            if (QrImages.Read(flattened).Count != 0) throw new InvalidOperationException("Fixture redaction invalid.");
            bool saved = false;
            void SaveBeforeRecognition(BitmapSource _, long id) => saved = CaptureLibrary.SaveMarks(id, edit.State, image, flattened);
            CaptureLibrary.CaptureStored += SaveBeforeRecognition;
            long capture;
            try { capture = CaptureLibrary.AddNow(image, DateTime.Now, "qr", "Synthetic race", null); }
            finally { CaptureLibrary.CaptureStored -= SaveBeforeRecognition; }
            if (!saved || CaptureLibrary.QrCodes(capture).Count != 0 || CaptureLibrary.Items("4417").Count != 0)
                throw new InvalidOperationException("Initial QR indexing restored redacted content to the album or search.");
            CaptureLibrary.Close();
            if (!CaptureLibrary.OpenAuto() || CaptureLibrary.QrCodes(capture).Count != 0 || CaptureLibrary.Items("4417").Count != 0)
                throw new InvalidOperationException("Redacted metadata returned after reopen.");
            report.Add("initial QR insertion interleaved with redaction, search and reopen PASS");
            long control = CaptureLibrary.AddNow(image, DateTime.Now, "qr", "Unchanged control", null);
            if (CaptureLibrary.QrCodes(control).Count != 1 || CaptureLibrary.Items("4417").Count != 1)
                throw new InvalidOperationException("Unchanged QR recognition stopped working.");
            report.Add("unchanged QR capture still indexed PASS");
            await DelayedAlbumQr(image, edit, report);
            // Recreate a pre-repair derived cache through the store boundary, then migrate it.
            CaptureLibrary.StoreQr(capture, QrImages.Read(image));
            CaptureLibrary.StoreOcr(capture, "old-metadata-secret");
            CaptureLibrary.SetNote(capture, "retained-user-note");
            string key = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")), "KillerShot.Library.v1"u8.ToArray(),
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
            using (var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ConnectionString))
            {
                database.Open();
                using var oldVersion = database.CreateCommand();
                oldVersion.CommandText = "DELETE FROM meta WHERE key='recognition_safe_output_v1'";
                oldVersion.ExecuteNonQuery();
            }
            CaptureLibrary.Close();
            if (!CaptureLibrary.OpenAuto() || CaptureLibrary.QrCodes(capture).Count != 0
                || CaptureLibrary.Items("old-metadata-secret").Count != 0 || CaptureLibrary.Items("retained-user-note").Count != 1
                || CaptureLibrary.QrCodes(control).Count != 1)
                throw new InvalidOperationException("Recognition migration retained unsafe derived data or changed user data.");
            report.Add("legacy edited recognition invalidated; original, notes and unedited QR preserved PASS");
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            var window = new MainWindow();
            var ocrDocument = EditorCopy(image);
            App.Instance.SetDocument(ocrDocument);
            var heldText = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var copiedText = new List<string>();
            window.RecognizeTextAsync = _ => heldText.Task;
            window.CopyTextToClipboard = copiedText.Add;
            var pendingText = window.ReadPictureTextAsync(null, copy: true);
            ocrDocument.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(image.PixelWidth, image.PixelHeight), Colors.Black, 1));
            heldText.SetResult("obsolete synthetic secret");
            await pendingText;
            if (copiedText.Count != 0 || window.TestRecognizedText.Length != 0)
                throw new InvalidOperationException("OCR completion displayed or copied text from before the redaction.");
            report.Add("held OCR after redaction cannot display or copy PASS");
            ocrDocument.MarkSaved();
            App.Instance.SetDocument(null);
            var canvas = (EditorCanvas)window.FindName("Editor");
            int Badges() => canvas.Children.OfType<System.Windows.Controls.Button>().Count(b =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("QrBadge-"));
            var document = EditorCopy(image, readQr: true);
            App.Instance.SetDocument(document);
            for (int i = 0; i < 100 && Badges() == 0; i++) await Task.Delay(10);
            if (Badges() != 1) throw new InvalidOperationException("The editor QR control did not produce its badge.");
            document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(image.PixelWidth, image.PixelHeight), Colors.Black, 1));
            await Task.Delay(50);
            if (Badges() != 0) throw new InvalidOperationException("A redacted QR badge and payload remained in the editor.");
            report.Add("editor redaction removes QR badges PASS");
            document.Undo();
            for (int i = 0; i < 100 && Badges() == 0; i++) await Task.Delay(10);
            if (Badges() != 1) throw new InvalidOperationException("Undo did not restore visible QR recognition.");
            document.Redo();
            for (int i = 0; i < 100 && Badges() != 0; i++) await Task.Delay(10);
            if (Badges() != 0) throw new InvalidOperationException("Redo restored a hidden QR badge.");
            document.Rotate(true);
            document.Scale(116, 116);
            document.Crop(new Int32Rect(0, 0, 100, 100));
            await Task.Delay(40);
            if (Badges() != 0) throw new InvalidOperationException("Transformed redaction exposed QR results.");
            report.Add("QR undo, redo, rotate, scale and crop follow visible pixels PASS");
            document.MarkSaved();
            App.Instance.SetDocument(null);
            var heldQr = new List<TaskCompletionSource<IReadOnlyList<KillerTools.Qr.QrResult>>>();
            window.RecognizeQrAsync = _ =>
            {
                var pending = new TaskCompletionSource<IReadOnlyList<KillerTools.Qr.QrResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
                heldQr.Add(pending);
                return pending.Task;
            };
            var delayedDoc = EditorCopy(image, readQr: true);
            App.Instance.SetDocument(delayedDoc);
            delayedDoc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(image.PixelWidth, image.PixelHeight), Colors.Black, 1));
            for (int i = 0; i < 10; i++) { delayedDoc.Undo(); delayedDoc.Redo(); }
            if (heldQr.Count != 1) throw new InvalidOperationException("Rapid edits launched overlapping QR scans instead of coalescing the latest state.");
            heldQr[0].SetResult(QrImages.Read(image));
            for (int i = 0; i < 100 && heldQr.Count < 2; i++) await Task.Delay(10);
            if (heldQr.Count != 2) throw new InvalidOperationException("The latest edited QR state was not scanned after the old request completed.");
            if (Badges() != 0) throw new InvalidOperationException("A late raw QR result restored a redacted badge.");
            heldQr[1].SetResult([]);
            await Task.Delay(20);
            App.Instance.SetDocument(delayedDoc);
            if (Badges() != 0 || heldQr.Count != 2) throw new InvalidOperationException("Valid empty QR cache was mistaken for a missing result.");
            report.Add("rapid QR edits coalesced; late result rejected; latest empty state cached PASS");
            delayedDoc.MarkSaved();
            App.Instance.SetDocument(null);
            await OcrInvalidation(window, image, copiedText, report);
            window.Close();
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "recognition-privacy-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }

    private static async Task DelayedAlbumQr(BitmapSource image, EditorDocument edit, List<string> report)
    {
        foreach (string change in new[] { "redact", "delete" })
        {
            string originalAlbum = CaptureLibrary.FilePath;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            long capturedId = 0;
            void Stored(BitmapSource _, long id) => capturedId = id;
            CaptureLibrary.CaptureStored += Stored;
            CaptureLibrary.QrRecognizer = bitmap =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Held QR was not released.");
                return QrImages.Read(bitmap);
            };
            var insertion = Task.Run(() => CaptureLibrary.AddNow(image, DateTime.Now, "qr", "Held QR", null));
            try
            {
                if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))) throw new TimeoutException("Initial QR did not reach the recognizer.");
                if (change == "redact") CaptureLibrary.SaveMarks(capturedId, edit.State, image, edit.Render());
                else if (change == "delete") CaptureLibrary.Delete([capturedId]);
                release.Set();
                await insertion;
                if (CaptureLibrary.QrCodes(capturedId).Count != 0) throw new InvalidOperationException("Held initial QR crossed " + change);
                report.Add("held initial QR rejected after " + change + " PASS");
            }
            finally
            {
                release.Set();
                await insertion;
                CaptureLibrary.CaptureStored -= Stored;
                CaptureLibrary.QrRecognizer = QrImages.Read;
                if (CaptureLibrary.FilePath != originalAlbum) CaptureLibrary.TrySwitchAlbum(originalAlbum, null, out _);
            }
        }
    }

    private static async Task OcrInvalidation(MainWindow window, BitmapSource image, List<string> copies, List<string> report)
    {
        foreach (string change in new[] { "unchanged", "replace", "switch", "closed" })
        {
            var doc = EditorCopy(image);
            App.Instance.SetDocument(doc);
            var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RecognizeTextAsync = _ => held.Task;
            copies.Clear();
            var request = window.ReadPictureTextAsync(null, copy: true);
            if (change == "replace") App.Instance.SetDocument(EditorCopy(image));
            else if (change == "switch")
            {
                string target = CaptureLibrary.CreateAlbum("OCR destination");
                CaptureLibrary.TrySwitchAlbum(target, null, out _);
                App.Instance.CompleteAlbumSwitch();
            }
            else if (change == "closed") window.Close();
            held.SetResult("current synthetic text");
            await request;
            if (change == "unchanged" ? copies.Count != 1 || window.TestRecognizedText != "current synthetic text"
                : copies.Count != 0 || window.TestRecognizedText.Length != 0)
                throw new InvalidOperationException("OCR validity failed for " + change);
            report.Add("held OCR " + change + " validity and copy boundary PASS");
            App.Instance.SetDocument(null);
        }
    }

    private static EditorDocument EditorCopy(BitmapSource image, bool readQr = false)
    {
        var copy = BitmapFrame.Create(image);
        copy.Freeze();
        return new EditorDocument(copy) { ReadQr = readQr };
    }

    internal static BitmapSource Fixture()
    {
        string[] rows = [
            "00000000000000000000000000000", "00000000000000000000000000000",
            "00111111100001100010111111100", "00100000101010000000100000100",
            "00101110100101110110101110100", "00101110100010001010101110100",
            "00101110101001001110101110100", "00100000100110001010100000100",
            "00111111101010101010111111100", "00000000000101111100000000000",
            "00101010100111011000001001000", "00101111001101010101000100000",
            "00000111110010011111011011100", "00010101010110000010011011000",
            "00010110100011000100100010000", "00000111010111011000011101100",
            "00101011111100000001011001000", "00011001001111010101011011000",
            "00101100110000110111111000100", "00000000001001111110001110000",
            "00111111100111101110101111000", "00100000100000110010001000100",
            "00101110101111001011111111000", "00101110100001000101100111000",
            "00101110101110010001000000100", "00100000100001000110000001100",
            "00111111101000100010111010100", "00000000000000000000000000000",
            "00000000000000000000000000000"
        ];
        const int scale = 8;
        int size = rows.Length * scale;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte value = rows[y / scale][x / scale] == '1' ? (byte)0 : (byte)255;
                int at = (y * size + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        image.Freeze();
        return image;
    }
}
