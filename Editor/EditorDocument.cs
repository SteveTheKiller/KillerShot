using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Editor;

/// <summary>The picture being edited: the capture's pixels plus the marks on top. A state is
/// immutable, so undo and redo swap whole states and crop keeps the uncropped pixels for undo.</summary>
public sealed record EditorState(BitmapSource Image, ImmutableList<Annotation> Items);

public sealed partial class EditorDocument
{
    public const int UndoLimit = 50;

    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();

    public EditorState State { get; private set; }
    public string? FilePath { get; set; }
    /// <summary>When the capture was taken or the file opened; used for default file names.</summary>
    public DateTime Taken { get; }
    public bool Dirty { get; private set; }
    public bool ReadQr { get; init; }

    public event Action? Changed;

    public EditorDocument(BitmapSource image, string? path = null, DateTime? taken = null)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        State = new EditorState(image, ImmutableList<Annotation>.Empty);
        Original = image;
        FilePath = path;
        Taken = taken ?? DateTime.Now;
    }

    /// <summary>A capture reopened from the album as the editor left it: its original pixels, and
    /// the cropped picture and marks it was saved with. Nothing is unsaved yet.</summary>
    public static EditorDocument Restore(BitmapSource original, EditorState restored, DateTime taken)
    {
        if (!restored.Image.IsFrozen && restored.Image.CanFreeze) restored.Image.Freeze();
        return new EditorDocument(original, null, taken) { State = restored };
    }

    /// <summary>The picture as captured or opened, before any crop.</summary>
    public BitmapSource Original { get; }

    public BitmapSource Image => State.Image;
    public int PixelWidth => State.Image.PixelWidth;
    public int PixelHeight => State.Image.PixelHeight;
    public bool CanUndo => _undo.Count > 0;
    internal int UndoCount => _undo.Count;
    public bool CanRedo => _redo.Count > 0;

    public void Apply(EditorState next)
    {
        if (ReferenceEquals(next, State)) return;
        _undo.Push(State);
        Trim(_undo);
        _redo.Clear();
        State = next;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Add(Annotation a) => Apply(State with { Items = State.Items.Add(a) });

    public void Replace(Annotation old, Annotation now)
    {
        // By reference: marks placed alike are equal records, and the given one is meant.
        int i = State.Items.FindIndex(a => ReferenceEquals(a, old));
        if (i >= 0) Apply(State with { Items = State.Items.SetItem(i, now) });
    }

    public void Remove(Annotation a)
    {
        int i = State.Items.FindIndex(m => ReferenceEquals(m, a));
        if (i >= 0) Apply(State with { Items = State.Items.RemoveAt(i) });
    }

    /// <summary>Crops to a rectangle in image pixels. The marks move with the picture; parts
    /// outside the new edge are clipped on export, not deleted, so undo restores everything.</summary>
    public bool Crop(Int32Rect r)
    {
        var img = State.Image;
        int x = Math.Clamp(r.X, 0, img.PixelWidth), y = Math.Clamp(r.Y, 0, img.PixelHeight);
        int w = Math.Clamp(r.Width, 0, img.PixelWidth - x), h = Math.Clamp(r.Height, 0, img.PixelHeight - y);
        if (w < 1 || h < 1 || (w == img.PixelWidth && h == img.PixelHeight)) return false;
        var cropped = new CroppedBitmap(img, new Int32Rect(x, y, w, h));
        // Copy the pixels out so the crop does not keep a live link to the full capture.
        var px = new byte[w * h * 4];
        var bgra = cropped.Format == PixelFormats.Bgra32 || cropped.Format == PixelFormats.Bgr32 || cropped.Format == PixelFormats.Pbgra32
            ? (BitmapSource)cropped : new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        bgra.CopyPixels(px, w * 4, 0);
        var copy = BitmapSource.Create(w, h, 96, 96, bgra.Format, null, px, w * 4);
        copy.Freeze();
        var shift = new Vector(-x, -y);
        Apply(new EditorState(copy, State.Items.Select(a => a.Offset(shift)).ToImmutableList()));
        return true;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(State);
        State = _undo.Pop();
        Dirty = true;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(State);
        State = _redo.Pop();
        Dirty = true;
        Changed?.Invoke();
    }

    public void MarkSaved() => Dirty = false;

    private static void Trim(Stack<EditorState> s)
    {
        if (s.Count <= UndoLimit) return;
        var keep = s.Take(UndoLimit).Reverse().ToList();
        s.Clear();
        foreach (var k in keep) s.Push(k);
    }

    /// <summary>The finished picture at exactly the capture's pixel size. With no marks this is
    /// the captured bitmap itself, so an unedited capture is exported bit for bit.</summary>
    public BitmapSource Render()
    {
        var st = State;
        if (st.Items.IsEmpty) return st.Image;
        int w = st.Image.PixelWidth, h = st.Image.PixelHeight;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.DrawImage(st.Image, new Rect(0, 0, w, h));
            foreach (var a in st.Items) a.Render(dc);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        // Imported images can have transparent pixels. Keep their alpha when exporting marks.
        var rendered = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        rendered.Freeze();
        return rendered;
    }
}
