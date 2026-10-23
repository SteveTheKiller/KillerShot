using System.Windows;
using KillerShot.Services;
using System.Windows.Media.Imaging;

namespace KillerShot.Shell;

public partial class MainWindow
{
    internal Func<BitmapSource, Task<IReadOnlyList<KillerTools.Qr.QrResult>>> RecognizeQrAsync { get; set; }
        = image => Task.Run(() => QrImages.Read(image));
    private RecognitionSnapshot? _qrSnapshot;
    private bool _qrAllowed;
    private int _qrReadGeneration;
    private bool _qrReading, _qrRefreshPending;

    private void InitQrReader()
    {
        QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;
        Editor.QrClicked += ShowQrCard;
        Closed += (_, _) => { if (_qrResultCard != null) _qrResultCard.IsOpen = false; };
        Deactivated += (_, _) => { if (_qrResultCard != null) _qrResultCard.IsOpen = false; };
        PreviewMouseDown += (_, _) => { if (_qrResultCard != null) _qrResultCard.IsOpen = false; };
        void AlbumChanged() => Dispatcher.BeginInvoke(new Action(SyncRecognitionState));
        CaptureLibrary.Changed += AlbumChanged;
        Closed += (_, _) => CaptureLibrary.Changed -= AlbumChanged;
        RefreshDocumentQr();
    }

    private async void RefreshDocumentQr()
    {
        SyncRecognitionState();
        if (_doc == null || _recognitionClosed)
        {
            _qrSnapshot = null;
            _qrReadGeneration++;
            Editor.SetQrCodes([]);
            return;
        }
        bool allowed = CaptureLibrary.QrReadingEnabled || _doc.ReadQr || App.Instance.CurrentItem?.HasQr == true;
        if (IsCurrentRecognition(_qrSnapshot) && _qrAllowed == allowed) return;
        _qrSnapshot = CaptureRecognition();
        _qrAllowed = allowed;
        _qrReadGeneration++;
        Editor.SetQrCodes([]);
        if (_qrResultCard != null) _qrResultCard.IsOpen = false;
        if (!allowed) return;
        _qrRefreshPending = true;
        if (_qrReading) return;
        _qrReading = true;
        try
        {
            while (_qrRefreshPending && !_recognitionClosed)
            {
                _qrRefreshPending = false;
                var snapshot = _qrSnapshot;
                int generation = _qrReadGeneration;
                if (!_qrAllowed || !IsCurrentRecognition(snapshot)) continue;
                try
                {
                    // Only the active scan owns rendered pixels. Edits retain one latest pending state.
                    var image = _doc!.Render();
                    var results = await RecognizeQrAsync(image);
                    if (generation == _qrReadGeneration && IsCurrentRecognition(snapshot)) Editor.SetQrCodes(results);
                }
                catch (Exception error)
                {
                    if (generation == _qrReadGeneration && IsCurrentRecognition(snapshot)) StatusText.Text = error.Message;
                }
            }
        }
        finally { _qrReading = false; }
    }

    private void QrReadingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureLibrary.QrReadingEnabled = !CaptureLibrary.QrReadingEnabled;
            QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;
            RefreshDocumentQr();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
}
