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

    private static async Task FailedSaveKeepsEdits(string folder, List<string> report)
    {
        long id = CaptureLibrary.AddNow(Picture(40), DateTime.Now, "region", "Failed marks", null);
        var item = CaptureLibrary.Items().Single(i => i.Id == id);
        string key = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
            File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")), "KillerShot.Library.v1"u8.ToArray(),
            System.Security.Cryptography.DataProtectionScope.CurrentUser));
        using var fault = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = CaptureLibrary.FilePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Password = key, Pooling = false }.ToString());
        fault.Open();
        using var command = fault.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_marks BEFORE INSERT ON capture_marks BEGIN SELECT RAISE(ABORT, 'Synthetic marks write failure'); END;";
        command.ExecuteNonQuery();
        EditorDocument? document = null;
        try
        {
            App.Instance.OpenLibraryCapture(item);
            document = App.Instance.Document!;
            document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
            bool replaced = App.Instance.SetDocument(null);
            report.Add($"failed marks write: replacement={replaced}, retained={ReferenceEquals(App.Instance.Document, document)}, dirty={document.Dirty}");
            if (replaced || !ReferenceEquals(App.Instance.Document, document) || !document.Dirty)
                throw new InvalidOperationException("A failed marks write discarded the edited album document.");
            if (await App.Instance.CanExitAsync())
                throw new InvalidOperationException("Exit accepted an album document whose marks were not saved.");
            if (CaptureLibrary.LoadMarks(id) != null)
                throw new InvalidOperationException("The rejecting fixture unexpectedly stored marks.");
        }
        finally
        {
            command.CommandText = "DROP TRIGGER reject_marks;";
            command.ExecuteNonQuery();
        }
        if (!App.Instance.SetDocument(null)) throw new InvalidOperationException("Retry after recovery did not permit replacement.");
        App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == id));
        if (App.Instance.Document?.State.Items.Count != 1 || Pixel(CaptureLibrary.LoadOutput(item), 30, 30)[1] != 0)
            throw new InvalidOperationException("Retry lost the pending redaction.");
        App.Instance.SetDocument(null);
        CaptureLibrary.Delete([id]);
        report.Add("failed marks stay open, prevent exit, and persist on retry PASS");

        long recoveryId = CaptureLibrary.AddNow(Picture(80), DateTime.Now, "region", "Export recovery", null);
        command.CommandText = "CREATE TRIGGER reject_marks BEFORE INSERT ON capture_marks BEGIN SELECT RAISE(ABORT, 'Synthetic marks write failure'); END;";
        command.ExecuteNonQuery();
        try
        {
            App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == recoveryId));
            var recovery = App.Instance.Document!;
            recovery.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
            if (App.Instance.SetDocument(null)) throw new InvalidOperationException("The recovery fixture did not reject its album write.");
            App.Instance.ShowEditor();
            string export = Path.Combine(folder, "recovered.png");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<KillerShot.Controls.FileDialog>().FirstOrDefault(w => w.IsVisible);
                if (dialog == null) return;
                timer.Stop();
                dialog.FileName = export;
                dialog.DialogResult = true;
            };
            timer.Start();
            try
            {
                ((System.Windows.Controls.Button)App.Instance.EditorWindow!.FindName("SaveAsBtn"))
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }
            finally { timer.Stop(); }
            if (recovery.Dirty || recovery.FilePath != export || Pixel(ImageOutput.Load(export), 30, 30)[1] != 0)
                throw new InvalidOperationException("Recovery Save As did not preserve the redacted picture.");
            if (!App.Instance.SetDocument(null))
                throw new InvalidOperationException("A successful file export remained trapped by the failed album write.");
            if (CaptureLibrary.LoadMarks(recoveryId) != null)
                throw new InvalidOperationException("Recovery falsely reported the album marks as stored.");
            report.Add("actual Save As recovers failed album edits and permits replacement PASS");
        }
        finally
        {
            command.CommandText = "DROP TRIGGER reject_marks;";
            command.ExecuteNonQuery();
        }
        CaptureLibrary.Delete([recoveryId]);
        long healthyId = CaptureLibrary.AddNow(Picture(110), DateTime.Now, "region", "Healthy export", null);
        App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(i => i.Id == healthyId));
        var healthy = App.Instance.Document!;
        healthy.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
        var exportTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        exportTimer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<KillerShot.Controls.FileDialog>().FirstOrDefault(w => w.IsVisible);
            if (dialog == null) return;
            exportTimer.Stop();
            dialog.FileName = Path.Combine(folder, "healthy-export.png");
            dialog.DialogResult = true;
        };
        exportTimer.Start();
        try
        {
            ((System.Windows.Controls.Button)App.Instance.EditorWindow!.FindName("SaveAsBtn"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        finally { exportTimer.Stop(); }
        if (healthy.Dirty || !App.Instance.SetDocument(null) || CaptureLibrary.LoadMarks(healthyId)?.Items.Count != 1)
            throw new InvalidOperationException("Successful Save As lost editable redaction in a healthy album.");
        report.Add("healthy Save As preserves the editable album redaction before detaching PASS");
    }

    private static async Task StaleSaveCannotCrossAlbums(List<string> report)
    {
        string originalAlbum = CaptureLibrary.FilePath;
        string albumA = CaptureLibrary.CreateAlbum("Queued marks A");
        string albumB = CaptureLibrary.CreateAlbum("Queued marks B");
        if (!CaptureLibrary.TrySwitchAlbum(albumA, null, out _)) throw new InvalidOperationException("Album A did not open.");
        var imageA = Picture(25);
        long idA = CaptureLibrary.AddNow(imageA, DateTime.Now, "region", "A", null);
        long sessionA = CaptureLibrary.Session;
        var document = new EditorDocument(imageA);
        document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(10, 10), new Point(70, 70), Colors.Black, 1));
        var state = document.State;
        var output = document.Render();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Task.Run(async () =>
        {
            await release.Task;
            return CaptureLibrary.SaveMarks(idA, state, imageA, output, sessionA);
        });
        if (!CaptureLibrary.TrySwitchAlbum(albumB, null, out _)) throw new InvalidOperationException("Album B did not open.");
        long idB = CaptureLibrary.AddNow(Picture(230), DateTime.Now, "region", "B", null);
        if (idA != idB) throw new InvalidOperationException("The album identity fixture needs equal local IDs.");
        byte[] before = CaptureLibrary.LoadOutputPng(idB);
        release.SetResult();
        bool saved = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        bool unchanged = before.SequenceEqual(CaptureLibrary.LoadOutputPng(idB));
        report.Add($"stale marks: accepted={saved}, destination unchanged={unchanged}");
        if (saved || !unchanged || CaptureLibrary.LoadMarks(idB) != null)
            throw new InvalidOperationException("A queued marks write crossed into another album with the same capture ID.");
        if (!CaptureLibrary.SaveMarks(idB, state, imageA, output, CaptureLibrary.Session))
            throw new InvalidOperationException("A current-session marks write was incorrectly refused.");
        CaptureLibrary.Delete([idB]);
        if (CaptureLibrary.SaveMarks(idB, new EditorDocument(imageA).State, imageA, imageA, CaptureLibrary.Session))
            throw new InvalidOperationException("A deleted capture was reported as saved.");
        if (!CaptureLibrary.TrySwitchAlbum(albumA, null, out _) || CaptureLibrary.LoadMarks(idA) != null)
            throw new InvalidOperationException("The rejected write changed the source album.");
        if (!CaptureLibrary.TrySwitchAlbum(originalAlbum, null, out _)) throw new InvalidOperationException("The original album did not reopen.");
        report.Add("queued marks stay bound to their album; current writes succeed and deleted rows fail PASS");
    }

    private static async Task CaptureLinkTiming(string folder, List<string> report)
    {
        CaptureSettings.SaveToFolder = true;
        CaptureSettings.OutputFolder = Path.Combine(folder, "capture-link-output");
        CaptureSettings.CopyToClipboard = false;
        CaptureSettings.AskToSave = false;
        CaptureSettings.OpenWithApps = false;
        CaptureSettings.EmailCapture = false;
        CaptureSettings.Notify = false;
        using var tray = new TrayHost();
        App.Instance.TestUseCapture(new CaptureController(tray));
        foreach (bool committedBeforeAttach in new[] { true, false })
        {
            var image = Picture(committedBeforeAttach ? (byte)141 : (byte)142);
            long id = committedBeforeAttach ? CaptureLibrary.AddNow(image, DateTime.Now, "region", "Early link", null) : 0;
            var document = new EditorDocument(image);
            if (!App.Instance.SetDocument(document)) throw new InvalidOperationException("The capture link fixture did not open.");
            App.Instance.ShowEditor();
            document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
            App.Instance.EditorWindow!.BeginReview(document, DateTime.Now, KillerShot.Capture.CaptureMode.Region);
            // AddNow queues the stored callback, but this UI turn accepts the review first.
            if (!committedBeforeAttach) id = CaptureLibrary.AddNow(image, DateTime.Now, "region", "Queued link", null);
            if (!App.Instance.EditorWindow.AcceptReview() || document.Dirty || document.FilePath == null)
                throw new InvalidOperationException("The capture link fixture was not accepted and saved.");
            if (!committedBeforeAttach)
                await WaitFor(() => CaptureLibrary.LoadMarks(id)?.Items.Count == 1,
                    "The queued stored callback ignored an accepted review after MarkSaved.");
            if (!App.Instance.SetDocument(null) || CaptureLibrary.LoadMarks(id)?.Items.Count != 1 ||
                Pixel(CaptureLibrary.LoadOutput(id), 30, 30)[1] != 0)
                throw new InvalidOperationException("Capture association timing lost the accepted review's editable redaction.");
            report.Add(committedBeforeAttach ? "capture committed before first editor attachment retains accepted marks PASS"
                : "queued stored callback after review MarkSaved retains accepted marks PASS");
        }
    }

    private static async Task PendingReviewKeepsEdits(string folder, List<string> report)
    {
        CaptureSettings.SaveToFolder = true;
        CaptureSettings.OutputFolder = Path.Combine(folder, "pending-review-output");
        CaptureSettings.CopyToClipboard = false;
        CaptureSettings.AskToSave = false;
        CaptureSettings.OpenWithApps = false;
        CaptureSettings.EmailCapture = false;
        CaptureSettings.Notify = false;
        using var tray = new TrayHost();
        App.Instance.TestUseCapture(new CaptureController(tray));
        App.Instance.ShowEditor();
        var editor = App.Instance.EditorWindow!;
        var image = Picture(135);
        var document = new EditorDocument(image);
        object writer = typeof(CaptureLibrary).GetField("PendingWriter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = Task.Run(() =>
        {
            lock (writer)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Pending review test did not release the album writer.");
            }
        });
        await entered.Task;
        try
        {
            if (!CaptureLibrary.Add(image, DateTime.Now, "region", "Pending review", null))
                throw new InvalidOperationException("The pending review fixture was not queued.");
            var unrelated = new EditorDocument(Picture(136));
            App.Instance.SetDocument(unrelated);
            unrelated.Add(new StepAnnotation(new Point(50, 50), 1, Colors.Red, 30));
            unrelated.MarkSaved();
            if (!App.Instance.SetDocument(null))
                throw new InvalidOperationException("A different pending capture blocked an unrelated document.");
            if (!App.Instance.SetDocument(document) || !App.Instance.SetDocument(null) || !App.Instance.SetDocument(document))
                throw new InvalidOperationException("An unchanged pending capture could not be replaced.");
            document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
            editor.BeginReview(document, DateTime.Now, KillerShot.Capture.CaptureMode.Region);
            if (!editor.AcceptReview() || document.Dirty || document.FilePath == null ||
                Pixel(ImageOutput.Load(document.FilePath), 30, 30)[1] != 0 || CaptureLibrary.PendingCount != 1)
                throw new InvalidOperationException("The accepted review fixture did not save a redacted file while the original was pending.");
            bool replaced = App.Instance.SetDocument(null);
            report.Add($"accepted pending review: replacement={replaced}, retained={ReferenceEquals(App.Instance.Document, document)}, dirty={document.Dirty}");
            if (replaced || !ReferenceEquals(App.Instance.Document, document))
                throw new InvalidOperationException("Replacing an accepted review before its original was stored lost its editable album redaction.");
        }
        finally
        {
            release.Set();
            await blocker;
        }
        await CaptureLibrary.RetryPendingAsync();
        long id = CaptureLibrary.Items().Single(i => i.Window == "Pending review").Id;
        if (!App.Instance.SetDocument(null) || CaptureLibrary.LoadMarks(id)?.Items.Count != 1 ||
            Pixel(CaptureLibrary.LoadOutput(id), 30, 30)[1] != 0)
            throw new InvalidOperationException("Replacement after the pending capture committed did not preserve the editable redaction.");
        report.Add("pending accepted review retained until album commit; unrelated and unchanged documents remain replaceable PASS");
    }

    private static async Task AcceptedReviewAlbumSwitch(string folder, List<string> report)
    {
        CaptureSettings.SaveToFolder = true;
        CaptureSettings.OutputFolder = Path.Combine(folder, "accepted-switch-output");
        CaptureSettings.CopyToClipboard = false;
        CaptureSettings.AskToSave = false;
        CaptureSettings.OpenWithApps = false;
        CaptureSettings.EmailCapture = false;
        CaptureSettings.Notify = false;
        using var tray = new TrayHost();
        App.Instance.TestUseCapture(new CaptureController(tray));
        App.Instance.ShowEditor();
        var editor = App.Instance.EditorWindow!;
        string originalAlbum = CaptureLibrary.FilePath;
        string destination = CaptureLibrary.CreateAlbum("Accepted switch destination");
        string key = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
            File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")), "KillerShot.Library.v1"u8.ToArray(),
            System.Security.Cryptography.DataProtectionScope.CurrentUser));
        using var fault = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = originalAlbum, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Password = key, Pooling = false }.ToString());
        fault.Open();
        using var command = fault.CreateCommand();
        foreach (bool reject in new[] { true, false })
        {
            var image = Picture(reject ? (byte)151 : (byte)152);
            long id = CaptureLibrary.AddNow(image, DateTime.Now, "region", "Accepted switch", null);
            var document = new EditorDocument(image);
            if (!App.Instance.SetDocument(document)) throw new InvalidOperationException("The accepted switch fixture did not open.");
            if (reject)
            {
                command.CommandText = "CREATE TRIGGER reject_switch_marks BEFORE INSERT ON capture_marks BEGIN SELECT RAISE(ABORT, 'Synthetic switch marks write failure'); END;";
                command.ExecuteNonQuery();
            }
            try
            {
                document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(20, 20), new Point(60, 50), Colors.Black, 1));
                editor.BeginReview(document, DateTime.Now, KillerShot.Capture.CaptureMode.Region);
                if (!editor.AcceptReview() || document.Dirty || document.FilePath == null ||
                    Pixel(ImageOutput.Load(document.FilePath), 30, 30)[1] != 0)
                    throw new InvalidOperationException("The accepted switch fixture did not save its redacted file.");
                // Switch in the same turn as acceptance, before the marks debounce can run.
                await editor.TestOpenAlbumAsync(destination);
                bool switched = CaptureLibrary.FilePath == destination;
                report.Add($"accepted review album switch: reject={reject}, switched={switched}, retained={ReferenceEquals(App.Instance.Document, document)}");
                if (reject && (switched || !ReferenceEquals(App.Instance.Document, document) || CaptureLibrary.LoadMarks(id) != null))
                    throw new InvalidOperationException("Album switching detached an accepted review whose editable marks could not be saved.");
                if (!reject && (!switched || App.Instance.Document != null))
                    throw new InvalidOperationException("A healthy accepted review did not permit album switching.");
            }
            finally
            {
                if (reject)
                {
                    command.CommandText = "DROP TRIGGER reject_switch_marks;";
                    command.ExecuteNonQuery();
                }
            }
            if (reject) await editor.TestOpenAlbumAsync(destination);
            if (CaptureLibrary.FilePath != destination || App.Instance.Document != null)
                throw new InvalidOperationException("Retry after marks recovery did not switch albums.");
            await editor.TestOpenAlbumAsync(originalAlbum);
            if (CaptureLibrary.FilePath != originalAlbum || CaptureLibrary.LoadMarks(id)?.Items.Count != 1 ||
                Pixel(CaptureLibrary.LoadOutput(id), 30, 30)[1] != 0)
                throw new InvalidOperationException("Switching immediately after review acceptance lost the original album's editable redaction.");
            report.Add(reject ? "accepted review album switch refuses failed marks and preserves them on retry PASS"
                : "healthy immediate album switch preserves accepted editable marks PASS");
        }
        ((System.Windows.Controls.Primitives.Popup)editor.FindName("LibrarySettingsPopup")).IsOpen = false;
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
            await CaptureLinkTiming(folder, report);
            await FailedSaveKeepsEdits(folder, report);
            await StaleSaveCannotCrossAlbums(report);
            await PendingReviewKeepsEdits(folder, report);
            await AcceptedReviewAlbumSwitch(folder, report);
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
