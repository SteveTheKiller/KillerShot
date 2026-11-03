using System.Windows.Input;
using KillerShot.Services;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private bool _videoWorkShown;
    private string _videoPreviousStatus = "";
    private Cursor? _videoPreviousCursor;

    private void InitLibraryVideoWork()
    {
        void Changed()
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(SyncLibraryVideoWork));
        }
        CaptureLibrary.Changed += Changed;
        Closed += (_, _) => CaptureLibrary.Changed -= Changed;
        SyncLibraryVideoWork();
    }

    private void SyncLibraryVideoWork()
    {
        bool busy = CaptureLibrary.VideoBusy;
        if (_videoWorkShown == busy) return;
        _videoWorkShown = busy;
        ScaleHost.IsEnabled = !busy;
        ToolbarOverflowButton.IsEnabled = !busy;
        LibrarySettingsButton.IsEnabled = !busy;
        TitleLibraryLabel.IsEnabled = !busy;
        if (busy)
        {
            _videoPreviousStatus = StatusText.Text;
            _videoPreviousCursor = ScaleHost.Cursor;
            StatusText.Text = Loc("Str_Album_Working");
            ScaleHost.Cursor = Cursors.Wait;
        }
        else
        {
            if (StatusText.Text == Loc("Str_Album_Working")) StatusText.Text = _videoPreviousStatus;
            ScaleHost.Cursor = _videoPreviousCursor;
        }
    }
}
