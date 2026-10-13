using System.IO;
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
    public static bool TryCopy(BitmapSource bmp, out string? error)
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
    public static void Save(BitmapSource bmp, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder enc = ext is ".jpg" or ".jpeg" ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, true);
    }

    /// <summary>Loads an image at its true pixel size. WPF would otherwise lay out a 144 DPI file
    /// at two thirds of its pixels, so the pixels are copied into a 96 DPI bitmap.</summary>
    public static BitmapSource Load(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
        img.UriSource = new Uri(path);
        img.EndInit();
        BitmapSource src = img.Format == System.Windows.Media.PixelFormats.Bgra32
            ? img : new FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[(long)w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var copy = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        copy.Freeze();
        return copy;
    }
}
