using System.Windows;
using System.Windows.Media.Imaging;
using KillerShot.Controls;

namespace KillerShot.Services;

/// <summary>Shows the edited picture in the themed print preview before sending a job.</summary>
internal static class PrintOutput
{
    /// <summary>Replaces the preview and print queue in self-tests.</summary>
    internal static Func<BitmapSource, string, bool>? PrintOverride { get; set; }

    public static bool Print(BitmapSource picture, string jobName)
    {
        if (PrintOverride is { } print) return print(picture, jobName);
        var preview = new PrintPreviewWindow(Application.Current.MainWindow, picture, jobName);
        preview.ShowDialog();
        return preview.Printed;
    }
}
