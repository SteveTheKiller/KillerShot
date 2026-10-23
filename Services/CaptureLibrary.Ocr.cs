using Microsoft.Data.Sqlite;
using KillerTools.Qr;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    public static IReadOnlyList<string> OcrLinks(long id)
    {
        lock (_gate)
        {
            if (_db == null) return [];
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT ocr_text FROM captures WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() is string text ? QrLink.FindWebLinks(text) : [];
        }
    }

    public static void StoreOcr(long id, string text) => StoreOcr(id, text, null);

    /// <summary>Forgets every stored text and reads the album again, for example after adding a
    /// recognition language. Captures stay searchable by title, program and notes meanwhile.</summary>
    public static void ReindexOcr()
    {
        lock (_gate)
        {
            if (_db == null) return;
            Exec("UPDATE captures SET ocr_text = NULL");
        }
        Interlocked.Increment(ref _ocrGeneration);
        lock (_ocrFailed) _ocrFailed.Clear();
        StartOcr();
    }

    public static void StoreOcr(CaptureItem item, string text)
    {
        lock (_gate) if (IsCurrent(item)) StoreOcr(item.Id, text, _db);
    }

    private static void StoreOcr(long id, string text, SqliteConnection? expectedDatabase, long? revision = null)
    {
        lock (_gate)
        {
            if (_db == null || (expectedDatabase != null && !ReferenceEquals(_db, expectedDatabase))) return;
            using var transaction = _db.BeginTransaction();
            if (revision is long read && read != MarksRevision(id, transaction)) return;
            using var command = _db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE captures SET ocr_text = $text WHERE id = $id";
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() > 0) IndexText(id, transaction);
            transaction.Commit();
        }
        RaiseChanged();
    }

    // ============================================================
    // Text in the pictures. Windows OCR reads each capture once, in the background, in the
    // languages of the user's Windows profile, and the text goes into the search index so a
    // search finds words that are only in the screenshot. Nothing leaves the PC.
    // ============================================================

    private static int _ocrRunning;
    // Bumped by ReindexOcr, so a running worker builds its recognizer again with the new language.
    private static int _ocrGeneration;

    /// <summary>Makes the text recognizer, or null when Windows has no OCR language for this
    /// user. Tests replace it at this boundary.</summary>
    internal static Func<Func<byte[], Task<string>>?> OcrEngineFactory { get; set; } = TextRecognition.CreateRecognizer;

    /// <summary>Captures whose recognition failed in this session. They stay unread in the album,
    /// so the next open tries them again, but are not retried in a loop now.</summary>
    private static readonly HashSet<long> _ocrFailed = [];

    /// <summary>Starts the OCR worker if it is not already running. It reads every capture that
    /// has no text yet, newest first, then stops. Without an OCR engine it leaves them unread.</summary>
    private static void StartOcr()
    {
        if (Interlocked.CompareExchange(ref _ocrRunning, 1, 0) != 0) return;
        Task.Run(async () =>
        {
            bool completed = true;
            try
            {
                int generation = Volatile.Read(ref _ocrGeneration);
                var recognize = OcrEngineFactory();
                if (recognize == null) { completed = false; return; }
                while (true)
                {
                    if (Volatile.Read(ref _ocrGeneration) != generation)
                    {
                        generation = Volatile.Read(ref _ocrGeneration);
                        recognize = OcrEngineFactory();
                        if (recognize == null) { completed = false; return; }
                    }
                    long id;
                    long revision;
                    byte[] png;
                    SqliteConnection database;
                    lock (_gate)
                    {
                        if (_db == null) return;
                        database = _db;
                        using var cmd = _db.CreateCommand();
                        lock (_ocrFailed)
                            // The edited result when the capture has marks, so text under a redaction is never read.
                            cmd.CommandText = "SELECT c.id, COALESCE((SELECT m.output_image FROM capture_marks m WHERE m.capture_id = c.id), c.image), COALESCE((SELECT revision FROM capture_revisions WHERE capture_id=c.id), 0) FROM captures c WHERE c.ocr_text IS NULL"
                                + (_ocrFailed.Count > 0 ? " AND c.id NOT IN (" + string.Join(",", _ocrFailed) + ")" : "")
                                + " ORDER BY c.taken_utc DESC LIMIT 1";
                        using var r = cmd.ExecuteReader();
                        if (!r.Read()) return;
                        id = r.GetInt64(0);
                        png = (byte[])r[1];
                        revision = r.GetInt64(2);
                    }
                    string text;
                    try { text = await recognize(png); }
                    catch
                    {
                        lock (_gate)
                            if (ReferenceEquals(_db, database))
                                lock (_ocrFailed) _ocrFailed.Add(id);
                        continue;
                    }
                    StoreOcr(id, text, database, revision);
                }
            }
            catch { completed = false; /* search still works on titles, programs, notes and collections */ }
            finally
            {
                Interlocked.Exchange(ref _ocrRunning, 0);
                lock (_gate)
                {
                    if (completed && BackgroundWork && _db != null)
                    {
                        using var remaining = _db.CreateCommand();
                        remaining.CommandText = "SELECT COUNT(*) FROM captures WHERE ocr_text IS NULL";
                        int failed;
                        lock (_ocrFailed) failed = _ocrFailed.Count;
                        if (Convert.ToInt64(remaining.ExecuteScalar()) > failed) StartOcr();
                    }
                }
            }
        });
    }

}
