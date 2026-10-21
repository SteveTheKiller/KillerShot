using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Data.Sqlite;
using KillerTools.Qr;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using WinBitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using WinBitmapTransform = Windows.Graphics.Imaging.BitmapTransform;
using WinExifOrientationMode = Windows.Graphics.Imaging.ExifOrientationMode;
using WinColorManagementMode = Windows.Graphics.Imaging.ColorManagementMode;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinMemoryStream = Windows.Storage.Streams.InMemoryRandomAccessStream;

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

    public static void StoreOcr(CaptureItem item, string text)
    {
        lock (_gate) if (IsCurrent(item)) StoreOcr(item.Id, text, _db);
    }

    private static void StoreOcr(long id, string text, SqliteConnection? expectedDatabase)
    {
        lock (_gate)
        {
            if (_db == null || (expectedDatabase != null && !ReferenceEquals(_db, expectedDatabase))) return;
            using var transaction = _db.BeginTransaction();
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

    /// <summary>Makes the text recognizer, or null when Windows has no OCR language for this
    /// user. Tests replace it at this boundary.</summary>
    internal static Func<Func<byte[], Task<string>>?> OcrEngineFactory { get; set; } = () =>
        WinOcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? png => RecognizeAsync(engine, png) : null;

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
                var recognize = OcrEngineFactory();
                if (recognize == null) { completed = false; return; }
                while (true)
                {
                    long id;
                    byte[] png;
                    SqliteConnection database;
                    lock (_gate)
                    {
                        if (_db == null) return;
                        database = _db;
                        using var cmd = _db.CreateCommand();
                        lock (_ocrFailed)
                            cmd.CommandText = "SELECT id, image FROM captures WHERE ocr_text IS NULL"
                                + (_ocrFailed.Count > 0 ? " AND id NOT IN (" + string.Join(",", _ocrFailed) + ")" : "")
                                + " ORDER BY taken_utc DESC LIMIT 1";
                        using var r = cmd.ExecuteReader();
                        if (!r.Read()) return;
                        id = r.GetInt64(0);
                        png = (byte[])r[1];
                    }
                    string text;
                    try { text = await recognize(png); }
                    catch
                    {
                        lock (_ocrFailed) _ocrFailed.Add(id);
                        continue;
                    }
                    StoreOcr(id, text, database);
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

    private static async Task<string> RecognizeAsync(WinOcrEngine engine, byte[] png)
    {
        using var stream = new WinMemoryStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await WinBitmapDecoder.CreateAsync(stream);
        uint max = WinOcrEngine.MaxImageDimension;
        var transform = new WinBitmapTransform();
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        if (w > max || h > max)
        {
            double scale = Math.Min((double)max / w, (double)max / h);
            transform.ScaledWidth = Math.Max(1, (uint)(w * scale));
            transform.ScaledHeight = Math.Max(1, (uint)(h * scale));
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(WinBitmapPixelFormat.Bgra8, WinBitmapAlphaMode.Premultiplied,
            transform, WinExifOrientationMode.IgnoreExifOrientation, WinColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Text ?? "";
    }
}
