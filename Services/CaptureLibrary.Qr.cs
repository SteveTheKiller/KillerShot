using System.Text.Json;
using KillerTools.Qr;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    private static readonly SemaphoreSlim QrWorker = new(1, 1);
    private static readonly Dictionary<string, bool> PendingQrChoices = new(StringComparer.OrdinalIgnoreCase);
    private static int _qrScheduled;
    internal static Func<System.Windows.Media.Imaging.BitmapSource, IReadOnlyList<QrResult>> QrRecognizer { get; set; } = QrImages.Read;

    public static bool QrReadingEnabled
    {
        get
        {
            lock (_gate) return _db == null && PendingQrChoices.TryGetValue(FilePath, out bool pending)
                ? pending : GetAlbumSetting("QrReading") == "1";
        }
        set
        {
            lock (_gate)
            {
                // Reading a picture in the editor also works while its album is locked.
                // Persist that album's choice once its connection becomes available.
                if (_db == null) { PendingQrChoices[FilePath] = value; return; }
                SetAlbumSetting("QrReading", value ? "1" : "0");
            }
            if (value) StartQrReading();
        }
    }

    private static void StartQrReading()
    {
        if (!BackgroundWork || !QrReadingEnabled || Interlocked.CompareExchange(ref _qrScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            bool completed = false;
            try { await ScanQrAsync().ConfigureAwait(false); completed = true; }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("QR reading stopped: {0}", error.Message); }
            finally
            {
                Interlocked.Exchange(ref _qrScheduled, 0);
                // A capture can arrive after the empty check but before the worker releases its claim.
                if (completed && BackgroundWork && QrReadingEnabled && HasPendingQr()) StartQrReading();
            }
        });
    }

    private static bool HasPendingQr()
    {
        lock (_gate)
        {
            if (_db == null) return false;
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM captures c WHERE NOT EXISTS(SELECT 1 FROM capture_qr q WHERE q.capture_id = c.id))";
            return (long)command.ExecuteScalar()! != 0;
        }
    }

    public static async Task ScanQrAsync()
    {
        await QrWorker.WaitAsync().ConfigureAwait(false);
        try
        {
            while (QrReadingEnabled && BackgroundWork)
            {
                SqliteConnection database;
                long id;
                long revision;
                byte[] image;
                lock (_gate)
                {
                    if (_db == null) return;
                    database = _db;
                    using var command = database.CreateCommand();
                    // The edited result when the capture has marks, so a redacted code is never decoded.
                    command.CommandText = "SELECT c.id, COALESCE((SELECT m.output_image FROM capture_marks m WHERE m.capture_id = c.id), c.image), COALESCE((SELECT revision FROM capture_revisions WHERE capture_id=c.id), 0) FROM captures c WHERE NOT EXISTS(SELECT 1 FROM capture_qr q WHERE q.capture_id = c.id) ORDER BY c.taken_utc DESC LIMIT 1";
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) return;
                    id = reader.GetInt64(0);
                    image = (byte[])reader[1];
                    revision = reader.GetInt64(2);
                }
                var results = await Task.Run(() => QrRecognizer(QrImages.Load(image))).ConfigureAwait(false);
                StoreQr(id, results, database, revision);
            }
        }
        finally { QrWorker.Release(); }
    }

    private static void ReadInitialQr(long id, SqliteConnection database)
    {
        byte[] output;
        long revision;
        lock (_gate)
        {
            if (!ReferenceEquals(_db, database)) return;
            using var command = database.CreateCommand();
            command.CommandText = "SELECT COALESCE((SELECT output_image FROM capture_marks WHERE capture_id=c.id), c.image), COALESCE((SELECT revision FROM capture_revisions WHERE capture_id=c.id), 0) FROM captures c WHERE c.id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return;
            output = (byte[])reader[0];
            revision = reader.GetInt64(1);
        }
        var results = QrRecognizer(QrImages.Load(output));
        StoreQr(id, results, database, revision);
    }

    public static void StoreQr(long id, IReadOnlyList<QrResult> results) => StoreQr(id, results, null);

    public static void StoreQr(CaptureItem item, IReadOnlyList<QrResult> results)
    {
        lock (_gate) if (IsCurrent(item)) StoreQr(item.Id, results, _db);
    }

    private static void StoreQr(long id, IReadOnlyList<QrResult> results, SqliteConnection? expectedDatabase, long? revision = null)
    {
        string json = JsonSerializer.Serialize(results);
        string text = string.Join('\n', results.Select(result => result.Text));
        lock (_gate)
        {
            if (_db == null || (expectedDatabase != null && !ReferenceEquals(_db, expectedDatabase))) return;
            using var transaction = Db.BeginTransaction();
            if (revision is long read && read != MarksRevision(id, transaction)) return;
            using var command = Db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_qr(capture_id, results, text)
                SELECT $id, $results, $text WHERE EXISTS(SELECT 1 FROM captures WHERE id = $id)
                ON CONFLICT(capture_id) DO UPDATE SET results = excluded.results, text = excluded.text;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$results", json);
            command.Parameters.AddWithValue("$text", text);
            if (command.ExecuteNonQuery() > 0) IndexText(id, transaction);
            transaction.Commit();
        }
        RaiseChanged();
    }

    public static IReadOnlyList<QrResult> QrCodes(long id)
    {
        lock (_gate)
        {
            if (_db == null) return [];
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT results FROM capture_qr WHERE capture_id = $id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<QrResult[]>(json) ?? [] : [];
        }
    }
}
