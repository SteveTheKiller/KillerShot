using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private sealed record RecognitionSnapshot(EditorDocument Document, EditorState State, long Session, long Generation);
    private EditorDocument? _recognitionDocument;
    private EditorState? _recognitionState;
    private long _recognitionSession;
    private long _recognitionGeneration;
    private bool _recognitionClosed;

    private RecognitionSnapshot CaptureRecognition() => new(_doc!, _doc!.State, CaptureLibrary.Session, _recognitionGeneration);

    private bool IsCurrentRecognition(RecognitionSnapshot? snapshot) => snapshot != null && !_recognitionClosed
        && snapshot.Generation == _recognitionGeneration && snapshot.Session == CaptureLibrary.Session
        && ReferenceEquals(snapshot.Document, _doc) && ReferenceEquals(snapshot.State, _doc?.State);

    private void SyncRecognitionState()
    {
        long session = CaptureLibrary.Session;
        if (!_recognitionClosed && ReferenceEquals(_recognitionDocument, _doc)
            && ReferenceEquals(_recognitionState, _doc?.State) && _recognitionSession == session) return;
        _recognitionDocument = _doc;
        _recognitionState = _doc?.State;
        _recognitionSession = session;
        _recognitionGeneration++;
        _textRead++;
        _textSnapshot = null;
        if (_textBox != null) _textBox.Text = "";
        if (_textStatus != null) _textStatus.Text = "";
        if (_textPopup != null) _textPopup.IsOpen = false;
        _qrReadGeneration++;
        _qrSnapshot = null;
        Editor.SetQrCodes([]);
        if (_qrResultCard != null) _qrResultCard.IsOpen = false;
    }
}
