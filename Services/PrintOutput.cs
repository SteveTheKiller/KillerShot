using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

/// <summary>Prints one picture, centered on the page, using the printing options in the capture
/// settings: shrink to fit, enlarge to fit, and rotate to match the page orientation.</summary>
internal static class PrintOutput
{
    /// <summary>Shows the print dialog and prints. Returns false when the user cancels.</summary>
    public static bool Print(BitmapSource bmp, string jobName)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return false;

        double pageW = dlg.PrintableAreaWidth, pageH = dlg.PrintableAreaHeight;
        // The picture's natural size on paper: one capture pixel per 1/96 inch.
        double imgW = bmp.PixelWidth, imgH = bmp.PixelHeight;
        bool rotate = CaptureSettings.PrintRotate && (imgW > imgH) != (pageW > pageH) && imgW != imgH;
        double fitW = rotate ? imgH : imgW, fitH = rotate ? imgW : imgH;

        double scale = 1;
        double fit = Math.Min(pageW / fitW, pageH / fitH);
        if (fit < 1 && CaptureSettings.PrintShrink) scale = fit;
        if (fit > 1 && CaptureSettings.PrintEnlarge) scale = fit;

        double w = fitW * scale, h = fitH * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform((pageW - w) / 2, (pageH - h) / 2));
            if (rotate)
            {
                dc.PushTransform(new TranslateTransform(w, 0));
                dc.PushTransform(new RotateTransform(90));
                dc.DrawImage(bmp, new Rect(0, 0, h, w));
                dc.Pop(); dc.Pop();
            }
            else
            {
                dc.DrawImage(bmp, new Rect(0, 0, w, h));
            }
            dc.Pop();
        }
        dlg.PrintVisual(visual, jobName);
        return true;
    }
}
