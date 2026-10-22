using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Keeps the editor's marks with the open capture in the album, a moment after each
/// change and at once when the picture is replaced, so reopening it from the album brings the
/// marks back editable. A new capture is linked once the album has stored it.</summary>
internal sealed class AlbumMarks
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _delay;
    private EditorDocument? _doc;
    private long? _id;
    private long _session;
    private bool _awaitingCapture;
    private Task<bool> _saving = Task.FromResult(true);
    private EditorDocument? _queuedDocument;
    private EditorState? _queuedState;

    public AlbumMarks(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _delay = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher);
        _delay.Stop();
        CaptureLibrary.CaptureStored += OnCaptureStored;
    }

    /// <summary>The capture the open document belongs to, or null for a picture from a file.</summary>
    public long? CaptureId => _id;

    /// <summary>A successful file save moves this document's save responsibility to that file.
    /// It also gives an edited capture a recovery path when its album can no longer be written.</summary>
    public void SavedToFile(EditorDocument document)
    {
        if (!ReferenceEquals(_doc, document) || document.Dirty || document.FilePath == null) return;
        // Keep editable marks in a healthy album before moving to file-based editing. A failed
        // album write must still allow recovery because the file already holds this exact state.
        Flush();
        _delay.Stop();
        _id = null;
        _awaitingCapture = false;
    }

    /// <summary>Follows a new document. Call after the previous one was flushed.</summary>
    public void Attach(EditorDocument? doc, long? id)
    {
        if (_doc != null) _doc.Changed -= OnChanged;
        _delay.Stop();
        _doc = doc;
        _id = id;
        _session = CaptureLibrary.Session;
        _awaitingCapture = doc != null && id == null && doc.FilePath == null;
        LinkStoredCapture();
        if (_doc != null) _doc.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (_id == null && !_awaitingCapture) return;
        _delay.Stop();
        _delay.Start();
    }

    private void OnCaptureStored(BitmapSource bmp, long id) => _dispatcher.BeginInvoke(() =>
    {
        if (_doc == null || !ReferenceEquals(_doc.Original, bmp)) return;
        if (LinkStoredCapture() && (_doc.Dirty || _delay.IsEnabled)) SaveNow();
    });

    private bool LinkStoredCapture()
    {
        if (!_awaitingCapture || _doc == null ||
            !CaptureLibrary.TryGetStoredCapture(_doc.Original, _session, out long id)) return false;
        _id = id;
        _awaitingCapture = false;
        if (!_doc.State.Items.IsEmpty || !ReferenceEquals(_doc.State.Image, _doc.Original)) _delay.Start();
        return true;
    }

    /// <summary>Writes pending marks now. Waits for the write, so the album holds them before the
    /// document is replaced or the album switches.</summary>
    public bool Flush()
    {
        LinkStoredCapture();
        if (_doc != null && _id == null && _awaitingCapture &&
            (!_doc.State.Items.IsEmpty || !ReferenceEquals(_doc.State.Image, _doc.Original)))
        {
            if (CaptureLibrary.HasPending(_doc.Original))
            {
                CaptureLibrary.ReportWriteFailure(new InvalidOperationException(string.Format(
                    LocaleManager.Loc("Str_Lib_Pending"), CaptureLibrary.PendingCount)));
                return false;
            }
            // A pending write may have committed after the first lookup.
            LinkStoredCapture();
        }
        if (_doc == null || _id == null) return true;
        if (CaptureLibrary.Session != _session)
        {
            CaptureLibrary.ReportWriteFailure(new InvalidOperationException(LocaleManager.Loc("Str_Album_Changed")));
            return false;
        }
        if (!_doc.Dirty && !_delay.IsEnabled && !ReferenceEquals(_queuedDocument, _doc)) return true;
        if (_delay.IsEnabled || !ReferenceEquals(_queuedDocument, _doc) ||
            !ReferenceEquals(_queuedState, _doc.State) || (_saving.IsCompletedSuccessfully && !_saving.Result))
            if (!SaveNow()) return false;
        try
        {
            if (!_saving.Wait(TimeSpan.FromSeconds(10)))
            {
                CaptureLibrary.ReportWriteFailure(new TimeoutException("Saving album marks timed out."));
                return false;
            }
            if (!_saving.Result) return false;
            if (_doc.FilePath == null) _doc.MarkSaved();
            return true;
        }
        catch (AggregateException error)
        {
            CaptureLibrary.ReportWriteFailure(error.GetBaseException());
            return false;
        }
    }

    private bool SaveNow()
    {
        LinkStoredCapture();
        _delay.Stop();
        if (_doc is not { } doc || _id is not long id || CaptureLibrary.Session != _session) return false;
        var state = doc.State;
        long session = _session;
        var original = doc.Original;
        BitmapSource flattened;
        try { flattened = doc.Render(); } // rendering belongs to this thread
        catch (Exception error) { CaptureLibrary.ReportWriteFailure(error); return false; }
        _queuedDocument = doc;
        _queuedState = state;
        _saving = _saving.ContinueWith(_ =>
        {
            try
            {
                if (!CaptureLibrary.SaveMarks(id, state, original, flattened, session))
                {
                    CaptureLibrary.ReportWriteFailure(new InvalidOperationException(LocaleManager.Loc("Str_Album_Changed")));
                    return false;
                }
                _dispatcher.BeginInvoke(() =>
                {
                    if (CaptureLibrary.Session == session && _session == session && ReferenceEquals(_doc, doc) &&
                        ReferenceEquals(doc.State, state) && doc.FilePath == null) doc.MarkSaved();
                });
                return true;
            }
            catch (Exception error) { CaptureLibrary.ReportWriteFailure(error); return false; }
        }, TaskScheduler.Default);
        return true;
    }
}
