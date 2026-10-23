using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerTools.Qr;

namespace KillerShot.Services;

internal static class QrImages
{
    public static IReadOnlyList<QrResult> Read(BitmapSource source)
    {
        var image = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = image.PixelWidth, height = image.PixelHeight;
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        image.CopyPixels(pixels, stride, 0);
        var gray = new byte[checked(width * height)];
        for (int i = 0; i < gray.Length; i++)
        {
            int at = i * 4, alpha = pixels[at + 3];
            int luminance = (29 * pixels[at] + 150 * pixels[at + 1] + 77 * pixels[at + 2] + 128) >> 8;
            gray[i] = (byte)((luminance * alpha + 255 * (255 - alpha) + 127) / 255);
        }
        return QrReader.Read(gray, width, height);
    }

    public static BitmapSource Load(byte[] encoded) => ImageOutput.Load(encoded);
}
