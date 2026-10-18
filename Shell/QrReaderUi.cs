using System.Windows;
using KillerShot.Services;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private void InitQrReader() => QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;

    private void QrReadingButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureLibrary.QrReadingEnabled = !CaptureLibrary.QrReadingEnabled;
        QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;
    }
}
