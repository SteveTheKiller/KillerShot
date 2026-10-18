using System.Windows;
using System.Windows.Media.Imaging;
using KillerShot.Controls;

namespace KillerShot.Services;

/// <summary>Shows the edited picture in the themed print preview before sending a job.</summary>
internal static class PrintOutput
{
    public static bool Print(BitmapSource picture, string jobName)
    {
        var preview = new PrintPreviewWindow(Application.Current.MainWindow, picture, jobName);
        preview.ShowDialog();
        return preview.Printed;
    }
}
