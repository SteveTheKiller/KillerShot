using System.Text.Json;
using KillerTools.Qr;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    private static readonly SemaphoreSlim QrWorker = new(1, 1);
    private static readonly Dictionary<string, bool> PendingQrChoices = new(StringComparer.OrdinalIgnoreCase);
    private static int _qrScheduled;

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
                byte[] image;
                lock (_gate)
                {
                    if (_db == null) return;
                    database = _db;
                    using var command = database.CreateCommand();
                    command.CommandText = "SELECT id, image FROM captures c WHERE NOT EXISTS(SELECT 1 FROM capture_qr q WHERE q.capture_id = c.id) ORDER BY taken_utc DESC LIMIT 1";
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) return;
                    id = reader.GetInt64(0);
                    image = (byte[])reader[1];
                }
                var results = await Task.Run(() => QrImages.Read(QrImages.Load(image))).ConfigureAwait(false);
                StoreQr(id, results, database);
            }
        }
        finally { QrWorker.Release(); }
    }

    public static void StoreQr(long id, IReadOnlyList<QrResult> results) => StoreQr(id, results, null);

    public static void StoreQr(CaptureItem item, IReadOnlyList<QrResult> results)
    {
        lock (_gate) if (IsCurrent(item)) StoreQr(item.Id, results, _db);
    }

    private static void StoreQr(long id, IReadOnlyList<QrResult> results, SqliteConnection? expectedDatabase)
    {
        string json = JsonSerializer.Serialize(results);
        string text = string.Join('\n', results.Select(result => result.Text));
        lock (_gate)
        {
            if (expectedDatabase != null && !ReferenceEquals(_db, expectedDatabase)) return;
            using var transaction = Db.BeginTransaction();
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
