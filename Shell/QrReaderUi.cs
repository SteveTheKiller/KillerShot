using System.Windows;
using KillerShot.Services;
using System.Windows.Media.Imaging;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private BitmapSource? _qrImage;
    private int _qrReadGeneration;

    private void InitQrReader()
    {
        QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;
        Editor.QrClicked += ShowQrCard;
        Closed += (_, _) => { if (_qrResultCard != null) _qrResultCard.IsOpen = false; };
        Deactivated += (_, _) => { if (_qrResultCard != null) _qrResultCard.IsOpen = false; };
        PreviewMouseDown += (_, _) => { if (_qrResultCard != null) _qrResultCard.IsOpen = false; };
        RefreshDocumentQr();
    }

    private async void RefreshDocumentQr()
    {
        if (_doc == null)
        {
            _qrImage = null;
            _qrReadGeneration++;
            Editor.SetQrCodes([]);
            return;
        }
        var image = _doc.Image;
        if (ReferenceEquals(image, _qrImage)) return;
        _qrImage = image;
        int generation = ++_qrReadGeneration;
        Editor.SetQrCodes([]);
        var stored = App.Instance.CurrentItem is { Id: > 0 } item && !_doc.Dirty ? CaptureLibrary.QrCodes(item.Id) : [];
        if (stored.Count > 0) { Editor.SetQrCodes(stored); return; }
        if (!CaptureLibrary.QrReadingEnabled) { _qrImage = null; return; }
        try
        {
            var results = await Task.Run(() => QrImages.Read(image));
            if (generation == _qrReadGeneration && ReferenceEquals(_doc?.Image, image)) Editor.SetQrCodes(results);
        }
        catch (Exception error) { if (generation == _qrReadGeneration) StatusText.Text = error.Message; }
    }

    private void QrReadingButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureLibrary.QrReadingEnabled = !CaptureLibrary.QrReadingEnabled;
        QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;
        RefreshDocumentQr();
    }
}
