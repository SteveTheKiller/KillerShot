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

    public static void SavePng(BitmapSource bmp, string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, EncodePng(bmp));
        File.Move(tmp, path, true);
    }
}
