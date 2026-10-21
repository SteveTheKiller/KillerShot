using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

internal static class ImageOutput
{
    public static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Puts the image on the clipboard as PNG for apps that read it and as a standard
    /// bitmap for everything else. Another app holding the clipboard is retried briefly, then
    /// reported instead of swallowed.</summary>
    public static bool TryCopy(BitmapSource bmp, out string? error) => TryCopy(bmp, null, out error);

    /// <summary>The picture, plus the saved file's path as text and as a file, when one is given,
    /// in one clipboard item so neither replaces the other.</summary>
    public static bool TryCopy(BitmapSource bmp, string? filePath, out string? error)
    {
        byte[] png = EncodePng(bmp);
        error = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetData("PNG", new MemoryStream(png), false);
                data.SetImage(bmp);
                if (filePath != null)
                {
                    data.SetText(filePath);
                    data.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath });
                }
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                error = ex.Message;
                Thread.Sleep(50);
            }
        }
        return false;
    }

    public static void SavePng(BitmapSource bmp, string path) => Save(bmp, path);

    /// <summary>Writes PNG, or JPEG for a .jpg or .jpeg path, through a temporary file so a failed
    /// write never leaves a truncated image behind.</summary>
    public static void Save(BitmapSource bmp, string path, int jpegQuality = 95, bool overwrite = true)
    {
        var encoder = CreateEncoder(bmp, path, jpegQuality);
        OutputFiles.WriteAtomic(path, overwrite, encoder.Save);
    }

    /// <summary>Saves a complete image under a unique name, including when another process saves
    /// to the same folder at the same time.</summary>
    public static string SaveUnique(BitmapSource bmp, string folder, string fileName, int jpegQuality = 95)
    {
        var encoder = CreateEncoder(bmp, fileName, jpegQuality);
        return OutputFiles.WriteUnique(folder, fileName, encoder.Save);
    }

    private static BitmapEncoder CreateEncoder(BitmapSource bmp, string path, int jpegQuality)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            ".png" => new PngBitmapEncoder(),
            _ => throw new NotSupportedException("Image output requires a PNG or JPEG extension."),
        };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        return encoder;
    }

    public static bool CanSaveInPlace(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) return false;
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => decoder is PngBitmapDecoder && !HasAnimation(stream),
                ".jpg" or ".jpeg" => decoder is JpegBitmapDecoder,
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return false;
        }
    }

    // WPF exposes only APNG's default image. Inspect PNG chunks instead of trusting its frame count.
    private static bool HasAnimation(Stream stream)
    {
        stream.Position = 8;
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > stream.Length - stream.Position - 4) throw new InvalidDataException("Incomplete PNG chunk.");
            if (header[4..].SequenceEqual("acTL"u8)) return true;
            if (header[4..].SequenceEqual("IEND"u8)) return false;
            stream.Seek((long)length + 4, SeekOrigin.Current);
        }
        throw new InvalidDataException("Missing PNG end chunk.");
    }

    /// <summary>The most pixels an opened image may have: 100 megapixels, 400 MB as BGRA. A long
    /// scrolling capture fits; a hostile header claiming far more is refused before decoding.</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>Throws when an image of this size would take more memory than an import may use.</summary>
    public static void CheckSize(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxPixels)
            throw new ImageTooLargeException(width, height);
    }

    /// <summary>Loads an image at its true pixel size, upright and in sRGB. The size is checked
    /// from the header before any pixels decode. An EXIF orientation turns the pixels themselves,
    /// and an embedded color profile is converted, so saving the edit shows the same picture
    /// everywhere. WPF would lay out a 144 DPI file at two thirds of its pixels, so the pixels are
    /// copied into a 96 DPI bitmap.</summary>
    public static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.PreservePixelFormat
            | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        CheckSize(frame.PixelWidth, frame.PixelHeight);
        BitmapSource src = ToSrgb(frame);
        if (Orientation(frame) is { } turn) src = new TransformedBitmap(src, turn);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[(long)w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var copy = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        copy.Freeze();
        return copy;
    }

    /// <summary>BGRA in sRGB. A profile that cannot be read leaves the stored values as they are.</summary>
    private static BitmapSource ToSrgb(BitmapFrame frame)
    {
        var bgra = System.Windows.Media.PixelFormats.Bgra32;
        System.Windows.Media.ColorContext? profile = null;
        try { profile = frame.ColorContexts is { Count: > 0 } contexts ? contexts[0] : null; }
        catch (Exception) { }
        if (profile != null)
        {
            try
            {
                var converted = new ColorConvertedBitmap(frame, profile, new System.Windows.Media.ColorContext(bgra), bgra);
                converted.CopyPixels(new Int32Rect(0, 0, 1, 1), new byte[4], 4, 0);
                return converted;
            }
            catch (Exception) { }
        }
        return frame.Format == bgra ? frame : new FormatConvertedBitmap(frame, bgra, null, 0);
    }

    /// <summary>The turn and mirror an EXIF orientation tag asks for, or null when upright.</summary>
    private static System.Windows.Media.Transform? Orientation(BitmapFrame frame)
    {
        int tag = 1;
        if (frame.Metadata is BitmapMetadata meta)
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    if (meta.ContainsQuery(query) && meta.GetQuery(query) is { } value) { tag = Convert.ToInt32(value); break; }
                }
                catch (Exception) { }
            }
        static System.Windows.Media.Transform Turn(double angle, double sx = 1, double sy = 1)
        {
            var group = new System.Windows.Media.TransformGroup();
            group.Children.Add(new System.Windows.Media.RotateTransform(angle));
            if (sx != 1 || sy != 1) group.Children.Add(new System.Windows.Media.ScaleTransform(sx, sy));
            group.Freeze();
            return group;
        }
        return tag switch
        {
            2 => Turn(0, -1),
            3 => Turn(180),
            4 => Turn(0, 1, -1),
            5 => Turn(90, -1),
            6 => Turn(90),
            7 => Turn(90, 1, -1),
            8 => Turn(270),
            _ => null,
        };
    }
}

/// <summary>An image whose pixel count exceeds what an import may decode.</summary>
internal sealed class ImageTooLargeException(int width, int height)
    : NotSupportedException(string.Format(LocaleManager.Loc("Str_Err_ImageTooLarge"), width, height, ImageOutput.MaxPixels / 1_000_000))
{
    public int Width { get; } = width;
    public int Height { get; } = height;
}
