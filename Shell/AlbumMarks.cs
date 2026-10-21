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
    private Task _saving = Task.CompletedTask;

    public AlbumMarks(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _delay = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher);
        _delay.Stop();
        CaptureLibrary.CaptureStored += OnCaptureStored;
    }

    /// <summary>The capture the open document belongs to, or null for a picture from a file.</summary>
    public long? CaptureId => _id;

    /// <summary>Follows a new document. Call after the previous one was flushed.</summary>
    public void Attach(EditorDocument? doc, long? id)
    {
        if (_doc != null) _doc.Changed -= OnChanged;
        _delay.Stop();
        _doc = doc;
        _id = id;
        _session = CaptureLibrary.Session;
        if (_doc != null) _doc.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (_id == null) return;
        _delay.Stop();
        _delay.Start();
    }

    private void OnCaptureStored(BitmapSource bmp, long id) => _dispatcher.BeginInvoke(() =>
    {
        // The capture that opened this document was just stored: its marks belong to it now.
        if (_doc == null || _id != null || _doc.FilePath != null || !ReferenceEquals(_doc.Original, bmp)) return;
        _id = id;
        _session = CaptureLibrary.Session;
        if (_doc.Dirty) SaveNow();
    });

    /// <summary>Writes pending marks now. Waits for the write, so the album holds them before the
    /// document is replaced or the album switches.</summary>
    public void Flush()
    {
        if (_delay.IsEnabled) SaveNow();
        try { _saving.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }
    }

    private void SaveNow()
    {
        _delay.Stop();
        if (_doc is not { } doc || _id is not long id || CaptureLibrary.Session != _session) return;
        var state = doc.State;
        var original = doc.Original;
        var flattened = doc.Render();   // rendering belongs to this thread
        _saving = _saving.ContinueWith(_ =>
        {
            try
            {
                CaptureLibrary.SaveMarks(id, state, original, flattened);
                _dispatcher.BeginInvoke(() => { if (ReferenceEquals(_doc, doc) && ReferenceEquals(doc.State, state) && doc.FilePath == null) doc.MarkSaved(); });
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Saving marks failed: {0}", error.Message); }
        }, TaskScheduler.Default);
    }
}
