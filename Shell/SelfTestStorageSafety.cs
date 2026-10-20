using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;
using KillerTools.Qr;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

/// <summary>Deterministic storage regressions against synthetic encrypted albums.</summary>
internal static class SelfTestStorageSafety
{
    public static bool TryRun(string[] args)
    {
        if (SelfTestAlbums.TryRun(args)) return true;
        if (SelfTestCaptureLifetime.TryRun(args)) return true;
        if (args.Length != 3 || args[0] != "--selftest-storage-safety") return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        Settings.UseFolder(Path.Combine(dir, "settings"));
        CaptureLibrary.UseFolder(Path.Combine(dir, "library"));
        CaptureLibrary.BackgroundWork = false;
        _ = RunAsync(dir, args[2]);
        return true;
    }

    private static async Task RunAsync(string dir, string scenario)
    {
        var log = new StringBuilder();
        int exit = 0;
        try
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            if (scenario == "pending") await PendingAfterWriteFailure(log);
            else if (scenario == "identity") CaptureIdentity(log);
            else if (scenario == "password") PasswordExportRecovery(log);
            else if (scenario == "mutations") MutationRollback(log);
            else if (scenario == "retention") await RetentionRollback(log);
            else if (scenario == "collection-color") CollectionColorReuse(log);
            else throw new ArgumentException("Unknown storage scenario.");
            log.AppendLine("result=ok");
        }
        catch (Exception error) { log.AppendLine($"result=error {error}"); exit = 1; }
        finally { CaptureLibrary.Close(); }
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Application.Current.Shutdown(exit);
    }

    private static void CollectionColorReuse(StringBuilder log)
    {
        long id = CaptureLibrary.CreateCollection("Colored collection");
        CardColors.SetCollection(id, Color.FromRgb(0x12, 0x34, 0x56));
        using (var fault = OpenFixtureDatabase())
        {
            Execute(fault, "CREATE TRIGGER fail_collection_delete BEFORE DELETE ON collections BEGIN SELECT RAISE(ABORT, 'delete failure'); END");
            try { CaptureLibrary.DeleteCollection(id); }
            catch (SqliteException) { }
            if (CardColors.Collection(id) != Color.FromRgb(0x12, 0x34, 0x56))
                throw new InvalidOperationException("Failed collection deletion removed its color.");
            Execute(fault, "DROP TRIGGER fail_collection_delete");
        }
        CaptureLibrary.DeleteCollection(id);
        long recreated = CaptureLibrary.CreateCollection("New collection");
        if (recreated != id || CardColors.Collection(recreated) != null)
            throw new InvalidOperationException("A new collection inherited the deleted collection's color.");
        log.AppendLine("collection_color_delete_rollback_and_reuse=True");
    }

    private static async Task RetentionRollback(StringBuilder log)
    {
        CaptureLibrary.AddNow(Pixel(), DateTime.Now.AddDays(-2), "region", "Older searchable", null);
        CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Newer searchable", null);
        using (var fault = OpenFixtureDatabase())
        {
            Execute(fault, "CREATE TRIGGER fail_retention BEFORE DELETE ON captures BEGIN SELECT RAISE(ABORT, 'retention failure'); END");
            CaptureLibrary.Keep = "count:1";
            await Task.Delay(600);
            if (CaptureLibrary.Count != 2 || CaptureLibrary.Items("Older").Count != 1)
                throw new InvalidOperationException("Failed retention removed surviving capture search results.");
            Execute(fault, "DROP TRIGGER fail_retention");
            Execute(fault, "DELETE FROM capture_search");
        }
        CaptureLibrary.Keep = "all";
        CaptureLibrary.Close();
        if (!CaptureLibrary.OpenAuto() || CaptureLibrary.Items("searchable").Count != 2)
            throw new InvalidOperationException("Reopening did not repair missing search rows from surviving captures.");
        CaptureLibrary.Keep = "count:1";
        for (int n = 0; n < 100 && CaptureLibrary.Count != 1; n++) await Task.Delay(20);
        if (CaptureLibrary.Count != 1 || CaptureLibrary.Items("Older").Count != 0 || CaptureLibrary.Items("Newer").Count != 1)
            throw new InvalidOperationException("Retention retry did not remove the same capture from storage and search.");
        log.AppendLine("retention_rollback_search_repair_and_retry=True");
    }

    private static BitmapSource Pixel()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        image.Freeze();
        return image;
    }

    private static SqliteConnection OpenFixtureDatabase()
    {
        string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(CaptureLibrary.FilePath)!, "Library.key")),
            "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ConnectionString);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(20);
    }

    private static async Task PendingAfterWriteFailure(StringBuilder log)
    {
        using var db = OpenFixtureDatabase();
        Execute(db, "CREATE TRIGGER fail_capture BEFORE INSERT ON captures BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFailure(string message) { if (message.Contains("synthetic write failure")) failed.TrySetResult(); }
        CaptureLibrary.WriteFailed += OnFailure;
        try
        {
            CaptureLibrary.Add(Pixel(), DateTime.Now, "region", "Retained capture", null);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { CaptureLibrary.WriteFailed -= OnFailure; }
        if (CaptureLibrary.PendingCount != 1 || CaptureLibrary.Count != 0)
            throw new InvalidOperationException("A failed capture was not retained exactly once.");
        Execute(db, "DROP TRIGGER fail_capture");
        CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Later capture", null);
        await WaitUntil(() => CaptureLibrary.Count == 2 && CaptureLibrary.PendingCount == 0);
        var items = CaptureLibrary.Items();
        bool recovered = CaptureLibrary.IsOpen && CaptureLibrary.PendingCount == 0 && items.Count == 2
            && items.Count(item => item.Window == "Retained capture") == 1
            && items.Count(item => item.Window == "Later capture") == 1;
        log.AppendLine($"pending_recovered_without_reopen={recovered} stored={items.Count} pending={CaptureLibrary.PendingCount}");
        if (!recovered) throw new InvalidOperationException("A failed capture stayed pending after a later successful write.");

        Execute(db, "CREATE TRIGGER fail_capture BEFORE INSERT ON captures BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END");
        failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureLibrary.WriteFailed += OnFailure;
        try
        {
            CaptureLibrary.Add(Pixel(), DateTime.Now, "region", "Explicit retry", null);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { CaptureLibrary.WriteFailed -= OnFailure; }
        Execute(db, "DROP TRIGGER fail_capture");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CaptureLibrary.RetryPendingAsync()));
        bool retried = CaptureLibrary.PendingCount == 0 && CaptureLibrary.Count == 3
            && CaptureLibrary.Items().Count(item => item.Window == "Explicit retry") == 1;
        log.AppendLine($"concurrent_explicit_retry_once={retried}");
        if (!retried) throw new InvalidOperationException("Concurrent retries lost or duplicated a pending capture.");
    }

    private static void CaptureIdentity(StringBuilder log)
    {
        long original = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Original", null);
        CaptureLibrary.Delete([original]);
        long replacement = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Replacement", null);
        CaptureLibrary.StoreOcr(original, "https://stale-capture.example");
        CaptureLibrary.StoreQr(original, [new QrResult("https://stale-qr.example", 1, QrErrorCorrection.L, [])]);
        bool isolated = replacement != original && CaptureLibrary.OcrLinks(replacement).Count == 0
            && CaptureLibrary.QrCodes(replacement).Count == 0 && CaptureLibrary.Items("stale").Count == 0;
        log.AppendLine($"deleted_identity_not_reused={isolated} original={original} replacement={replacement}");
        if (!isolated) throw new InvalidOperationException("A late recognition result was stored on a replacement capture.");

        CaptureLibrary.Delete([replacement]);
        CaptureLibrary.Close();
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The empty album did not reopen.");
        long reopened = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "After reopening", null);
        bool persisted = reopened > replacement;
        log.AppendLine($"empty_album_identity_persisted={persisted}");
        if (!persisted) throw new InvalidOperationException("An empty album forgot its previous capture identities.");

        // Model a version-two album at the disk boundary, then exercise the normal open migration.
        CaptureLibrary.Close();
        using (var db = OpenFixtureDatabase())
            Execute(db, "DELETE FROM meta WHERE key = 'capture_id'; UPDATE meta SET value = '2' WHERE key = 'schema'");
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The older album did not migrate.");
        long migrated = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "After migration", null);
        bool preserved = migrated > reopened && CaptureLibrary.Items().Any(item => item.Id == reopened)
            && CaptureLibrary.LoadImage(reopened).PixelWidth == 1;
        log.AppendLine($"legacy_identity_migration_preserved_captures={preserved}");
        if (!preserved) throw new InvalidOperationException("Identity migration lost a capture or reused its ID.");
    }

    private static void PasswordExportRecovery(StringBuilder log)
    {
        long id = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Password recovery", null);
        using (var db = OpenFixtureDatabase())
        {
            db.CreateCollation("synthetic_export_fault", string.CompareOrdinal);
            Execute(db, "CREATE TABLE export_fault(value TEXT COLLATE synthetic_export_fault)");
        }
        bool failed = false;
        try { CaptureLibrary.SetPassword("after recovery"); }
        catch (SqliteException error)
        {
            failed = error.SqliteErrorCode == 1;
            log.AppendLine($"export_failure={error}");
        }
        if (!failed) throw new InvalidOperationException("The fixture did not fail during export.");
        bool usable = CaptureLibrary.IsOpen && CaptureLibrary.Count == 1 && CaptureLibrary.LoadImage(id).PixelWidth == 1;
        log.AppendLine($"original_album_usable_after_export_failure={usable}");
        if (!usable) throw new InvalidOperationException("Export failure made the original capture unavailable.");
        using (var db = OpenFixtureDatabase())
        {
            db.CreateCollation("synthetic_export_fault", string.CompareOrdinal);
            Execute(db, "DROP TABLE export_fault");
        }
        // Refresh SQLite's schema cache after the external fixture repair.
        if (CaptureLibrary.Count != 1) throw new InvalidOperationException("Fixture repair lost the capture.");
        CaptureLibrary.SetPassword("after recovery");
        CaptureLibrary.Close();
        bool recovered = !CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword
            && CaptureLibrary.Unlock("after recovery") && CaptureLibrary.Count == 1
            && CaptureLibrary.Items().Single().Id == id;
        log.AppendLine($"password_retry_after_export_failure={recovered}");
        if (!recovered) throw new InvalidOperationException("A password change could not retry after export failure.");
    }

    private static void FailingIndex(Action action)
    {
        using var db = OpenFixtureDatabase();
        Execute(db, """
            ALTER TABLE capture_search RENAME TO saved_search;
            CREATE TABLE capture_search(title TEXT, process TEXT, note TEXT, ocr TEXT, collection TEXT);
            CREATE TRIGGER fail_index BEFORE INSERT ON capture_search
            BEGIN SELECT RAISE(ABORT, 'synthetic index failure'); END;
            """);
        bool failed = false;
        try
        {
            try { action(); }
            catch (SqliteException error) { failed = error.Message.Contains("synthetic index failure", StringComparison.Ordinal); }
        }
        finally { Execute(db, "DROP TABLE capture_search; ALTER TABLE saved_search RENAME TO capture_search"); }
        if (!failed) throw new InvalidOperationException("The mutation fixture did not fail while indexing.");
    }

    private static void MutationRollback(StringBuilder log)
    {
        long id = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Mutation recovery", null);
        CaptureLibrary.SetNote(id, "Original note");
        FailingIndex(() => CaptureLibrary.SetNote(id, "Changed note"));
        bool notePreserved = CaptureLibrary.Items().Single().Note == "Original note";
        log.AppendLine($"failed_note_rolled_back={notePreserved}");
        if (!notePreserved) throw new InvalidOperationException("A failed note edit left the note changed.");
        long collection = CaptureLibrary.CreateCollection("Original collection");
        CaptureLibrary.Move([id], collection);
        FailingIndex(() => CaptureLibrary.RenameCollection(collection, "Changed collection"));
        bool namePreserved = CaptureLibrary.Collections().Single().Name == "Original collection";
        log.AppendLine($"failed_collection_rename_rolled_back={namePreserved}");
        if (!namePreserved) throw new InvalidOperationException("A failed rename left the collection changed.");
        FailingIndex(() => CaptureLibrary.DeleteCollection(collection));
        bool collectionPreserved = CaptureLibrary.Collections().SingleOrDefault()?.Id == collection
            && CaptureLibrary.Items().Single().CollectionId == collection;
        log.AppendLine($"failed_collection_delete_rolled_back={collectionPreserved}");
        if (!collectionPreserved) throw new InvalidOperationException("A failed collection removal left captures moved or the collection deleted.");
        FailingIndex(() => CaptureLibrary.CreateCollection("Incomplete collection", [id]));
        bool createRolledBack = CaptureLibrary.Collections().Count == 1
            && CaptureLibrary.Items().Single().CollectionId == collection;
        log.AppendLine($"failed_create_and_move_rolled_back={createRolledBack}");
        if (!createRolledBack) throw new InvalidOperationException("A failed create-and-move left an empty collection behind.");
        CaptureLibrary.SetNote(id, "Retried note");
        CaptureLibrary.RenameCollection(collection, "Retried collection");
        CaptureLibrary.DeleteCollection(collection);
        long created = CaptureLibrary.CreateCollection("Completed collection", [id]);
        bool retried = CaptureLibrary.Collections().Single().Id == created
            && CaptureLibrary.Items().Single().CollectionId == created
            && CaptureLibrary.Items("Retried").Single().Id == id;
        log.AppendLine($"mutations_retry_after_index_failure={retried}");
        if (!retried) throw new InvalidOperationException("A repaired album could not retry its edits.");
    }
}
