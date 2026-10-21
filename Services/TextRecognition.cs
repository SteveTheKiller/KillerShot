using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using WinBitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using WinBitmapTransform = Windows.Graphics.Imaging.BitmapTransform;
using WinExifOrientationMode = Windows.Graphics.Imaging.ExifOrientationMode;
using WinColorManagementMode = Windows.Graphics.Imaging.ColorManagementMode;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinLanguage = Windows.Globalization.Language;
using WinMemoryStream = Windows.Storage.Streams.InMemoryRandomAccessStream;

namespace KillerShot.Services;

/// <summary>Windows text recognition, on this computer only. The language is the user's Windows
/// language unless one was chosen; recognized lines keep their line breaks.</summary>
internal static class TextRecognition
{
    /// <summary>The installed recognition languages as (tag, display name).</summary>
    public static IReadOnlyList<(string Tag, string Name)> Languages
    {
        get
        {
            try { return WinOcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList(); }
            catch (Exception error) when (error is COMException or TypeLoadException) { return []; }
        }
    }

    /// <summary>The chosen language tag, or null for the Windows language.</summary>
    public static string? LanguageTag
    {
        get => Settings.Get("OcrLanguage") is { Length: > 0 } tag ? tag : null;
        set => Settings.Set("OcrLanguage", value ?? "");
    }

    /// <summary>A recognizer, or null when no language is installed (or the chosen one was removed).</summary>
    public static Func<byte[], Task<string>>? CreateRecognizer()
    {
        try
        {
            WinOcrEngine? engine = null;
            if (LanguageTag is { } tag)
            {
                var language = new WinLanguage(tag);
                if (WinOcrEngine.IsLanguageSupported(language)) engine = WinOcrEngine.TryCreateFromLanguage(language);
            }
            engine ??= WinOcrEngine.TryCreateFromUserProfileLanguages();
            return engine == null ? null : png => RecognizeAsync(engine, png);
        }
        catch (Exception error) when (error is COMException or ArgumentException) { return null; }
    }

    /// <summary>The text in a picture, or null when Windows cannot recognize text here.</summary>
    public static async Task<string?> ReadAsync(BitmapSource image)
    {
        if (CreateRecognizer() is not { } recognize) return null;
        return await recognize(ImageOutput.EncodePng(image));
    }

    internal static async Task<string> RecognizeAsync(WinOcrEngine engine, byte[] png)
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
        // One line per recognized line, so copied text keeps its layout.
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
}
