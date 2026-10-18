using System.Runtime.InteropServices.WindowsRuntime;
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
    // ============================================================
    // Text in the pictures. Windows OCR reads each capture once, in the background, in the
    // languages of the user's Windows profile, and the text goes into the search index so a
    // search finds words that are only in the screenshot. Nothing leaves the PC.
    // ============================================================

    private static int _ocrRunning;

    /// <summary>Starts the OCR worker if it is not already running. It reads every capture that
    /// has no text yet, newest first, then stops.</summary>
    private static void StartOcr()
    {
        if (Interlocked.CompareExchange(ref _ocrRunning, 1, 0) != 0) return;
        Task.Run(async () =>
        {
            try
            {
                var engine = WinOcrEngine.TryCreateFromUserProfileLanguages();
                while (true)
                {
                    long id;
                    byte[] png;
                    lock (_gate)
                    {
                        if (_db == null) return;
                        using var cmd = _db.CreateCommand();
                        cmd.CommandText = "SELECT id, image FROM captures WHERE ocr_text IS NULL ORDER BY taken_utc DESC LIMIT 1";
                        using var r = cmd.ExecuteReader();
                        if (!r.Read()) return;
                        id = r.GetInt64(0);
                        png = (byte[])r[1];
                    }
                    string text = "";
                    if (engine != null)
                    {
                        try { text = await RecognizeAsync(engine, png); }
                        catch { /* an image OCR cannot read is stored as having no text */ }
                    }
                    lock (_gate)
                    {
                        if (_db == null) return;
                        using var cmd = _db.CreateCommand();
                        cmd.CommandText = "UPDATE captures SET ocr_text = $t WHERE id = $id";
                        cmd.Parameters.AddWithValue("$t", text);
                        cmd.Parameters.AddWithValue("$id", id);
                        if (cmd.ExecuteNonQuery() > 0) IndexText(id);
                    }
                }
            }
            catch { /* search still works on titles, programs, notes and collections */ }
            finally { Interlocked.Exchange(ref _ocrRunning, 0); }
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
