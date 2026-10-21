using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

/// <summary>Reads a picture from the clipboard when the user pastes, never by watching it. PNG is
/// preferred because it keeps transparency; a standard bitmap or a copied image file also work.</summary>
internal static class ClipboardImages
{
    /// <summary>The clipboard's picture, or null when it holds none or is busy.</summary>
    public static BitmapSource? FromClipboard()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { return Clipboard.GetDataObject() is { } data ? Read(data) : null; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }   // another app holds it
        }
        return null;
    }

    /// <summary>The picture in a data object at its true pixel size, BGRA at 96 DPI, frozen.</summary>
    public static BitmapSource? Read(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is { } png)
        {
            using var stream = png switch
            {
                MemoryStream ms => new MemoryStream(ms.ToArray(), writable: false),
                Stream s => Copy(s),
                byte[] bytes => new MemoryStream(bytes, writable: false),
                _ => null,
            };
            // A damaged PNG falls back to the other variants; an oversized one is refused outright.
            try { if (stream != null && Decode(stream) is { } fromPng) return fromPng; }
            catch (Exception error) when (error is FileFormatException or System.Runtime.InteropServices.COMException
                || error is NotSupportedException and not ImageTooLargeException) { }
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return Normalize(bitmap);
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            && File.Exists(files[0]) && ImageExtensions.Contains(Path.GetExtension(files[0])))
        {
            // A damaged or unreadable file is reported like any other failed read, never thrown past the paste.
            try { return ImageOutput.Load(files[0]); }
            catch (Exception error) when (error is FormatException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            { throw new IOException(error.Message, error); }
        }
        return null;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private static MemoryStream Copy(Stream s)
    {
        var copy = new MemoryStream();
        s.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static BitmapSource? Decode(Stream stream)
    {
        var header = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0];
        ImageOutput.CheckSize(header.PixelWidth, header.PixelHeight);
        stream.Position = 0;
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return Normalize(frame);
    }

    private static BitmapSource Normalize(BitmapSource source)
    {
        ImageOutput.CheckSize(source.PixelWidth, source.PixelHeight);
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[(long)w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var copy = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        copy.Freeze();
        return copy;
    }
}
