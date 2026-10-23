using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

/// <summary>Synthetic album recovery checks. Never opens a window or writes the clipboard.</summary>
internal static class SelfTestPrivacyRecovery
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-privacy-recovery") return false;
        string folder = Path.GetFullPath(args[1]);
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
            var original = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 16).ToArray(), 8);
            original.Freeze();
            long id = CaptureLibrary.AddNow(original, DateTime.Now, "region", "Synthetic recovery", null);
            var edit = new EditorDocument(original);
            edit.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(2, 2), Colors.Black, 1));
            if (!CaptureLibrary.SaveMarks(id, edit.State, original, edit.Render())) throw new InvalidOperationException("Marks save failed.");
            var item = CaptureLibrary.Items().Single();
            App.Instance.OpenLibraryCapture(item);
            AssertBlack(App.Instance.Document!.Render());
            if (App.Instance.Document.State.Items.Count != 1) throw new InvalidOperationException("Valid marks are no longer editable.");
            App.Instance.SetDocument(null);
            report.Add("valid marks remain editable PASS");
            string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")),
                "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ConnectionString);
            database.Open();
            using var update = database.CreateCommand();
            foreach (string scenario in new[] { "unsupported-version", "malformed-json", "unknown-redaction", "numeric-unknown-shape", "corrupt-base" })
            {
                string marks = scenario == "malformed-json" ? "{" : MarksFormat.Write(edit.State.Items);
                if (scenario == "unknown-redaction") marks = marks.Replace("Redact", "FutureRedaction");
                if (scenario == "numeric-unknown-shape") marks = marks.Replace("Redact", "999");
                update.Parameters.Clear();
                update.CommandText = "UPDATE capture_marks SET version=$v, marks=$marks, base_image=$base WHERE capture_id=$id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$v", scenario == "unsupported-version" ? 999 : MarksFormat.Version);
                update.Parameters.AddWithValue("$marks", marks);
                update.Parameters.AddWithValue("$base", scenario == "corrupt-base" ? ImageOutput.EncodePng(original)[..12] : (object)DBNull.Value);
                update.ExecuteNonQuery();
                App.Instance.OpenLibraryCapture(item);
                var recovered = App.Instance.Document!;
                AssertBlack(recovered.Render());
                AssertBlack(recovered.Original);
                if (recovered.CanUndo || recovered.Recovery != AlbumRecovery.Flattened)
                    throw new InvalidOperationException("Recovery exposed an original or undo state.");
                ImageOutput.Save(recovered.Render(), Path.Combine(folder, scenario + ".png"), 90);
                AssertBlack(ImageOutput.Load(Path.Combine(folder, scenario + ".png")));
                recovered.Add(new StepAnnotation(new Point(1, 1), 1, Colors.Red, 1));
                // Model a successful separate-file save before replacement, without a dialog.
                recovered.FilePath = Path.Combine(folder, scenario + "-edited.png");
                ImageOutput.Save(recovered.Render(), recovered.FilePath, 90);
                recovered.MarkSaved();
                App.Instance.DocumentSavedToFile(recovered);
                if (!App.Instance.SetDocument(null)) throw new InvalidOperationException("A saved recovery could not close.");
                using var preserved = database.CreateCommand();
                preserved.CommandText = "SELECT marks FROM capture_marks WHERE capture_id=$id";
                preserved.Parameters.AddWithValue("$id", id);
                if ((string?)preserved.ExecuteScalar() != marks) throw new InvalidOperationException("Recovery rewrote unsupported metadata.");
                AssertBlack(CaptureLibrary.LoadOutput(item));
                report.Add(scenario + " flattened editor/export, no original in undo, metadata preserved PASS");
            }
            App.Instance.RecoverLibraryOriginal(item);
            if (App.Instance.Document!.Recovery != AlbumRecovery.Original || App.Instance.Document.Image.PixelWidth != 2)
                throw new InvalidOperationException("Explicit original recovery did not open a separate copy.");
            AssertSolid(App.Instance.Document.Render(), 255);
            App.Instance.SetDocument(null);
            update.Parameters.Clear();
            update.CommandText = "UPDATE capture_marks SET output_image=x'000102' WHERE capture_id=$id";
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            bool refused = false;
            try { App.Instance.OpenLibraryCapture(item); }
            catch (InvalidDataException) { refused = true; }
            if (!refused || App.Instance.Document != null) throw new InvalidOperationException("Unreadable flattened output opened original content.");
            report.Add("corrupt flattened output refused; explicit original recovery remains separate PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "privacy-recovery-report.txt"), report);
        }
        App.Instance.Shutdown(exit);
        return true;
    }

    private static void AssertBlack(BitmapSource image) => AssertSolid(image, 0);

    private static void AssertSolid(BitmapSource image, byte shade)
    {
        int stride = image.PixelWidth * 4;
        byte[] pixels = new byte[stride * image.PixelHeight];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] != shade || pixels[i + 1] != shade || pixels[i + 2] != shade || pixels[i + 3] != 255)
                throw new InvalidOperationException("The editor pixels differ from the required safe or explicit recovery result.");
    }
}
