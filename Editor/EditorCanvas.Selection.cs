using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;

namespace KillerShot.Editor;

/// <summary>Working with several marks at once: Ctrl+click and Ctrl+A select more than one, and
/// move, nudge, delete, duplicate and layer order apply to all of them as one undo step. Text and
/// callouts can be edited again after they are placed.</summary>
public sealed partial class EditorCanvas
{
    private Annotation? _primary;
    private readonly List<Annotation> _others = [];

    /// <summary>The primary selection. Setting it replaces the whole selection.</summary>
    private Annotation? _selected
    {
        get => _primary;
        set { _primary = value; _others.Clear(); }
    }

    /// <summary>Every selected mark, the primary one first.</summary>
    public IReadOnlyList<Annotation> SelectedMarks => _primary == null ? [] : [_primary, .. _others];

    private bool IsSelectedMark(Annotation a) => ReferenceEquals(a, _primary) || _others.Any(o => ReferenceEquals(o, a));

    /// <summary>Ctrl+click: adds a mark to the selection, or takes it out.</summary>
    public void ToggleSelection(Annotation mark)
    {
        if (_doc == null || !_doc.State.Items.Contains(mark)) return;
        if (ReferenceEquals(mark, _primary))
        {
            _primary = _others.Count > 0 ? _others[0] : null;
            if (_others.Count > 0) _others.RemoveAt(0);
        }
        else if (_others.FindIndex(o => ReferenceEquals(o, mark)) is int at and >= 0) _others.RemoveAt(at);
        else if (_primary == null) _primary = mark;
        else { _others.Add(_primary); _primary = mark; }
        InvalidateVisual();
        NotifySelection();
    }

    /// <summary>Ctrl+A: every mark on the picture.</summary>
    public bool SelectAll()
    {
        if (_doc == null || _doc.State.Items.IsEmpty || _textBox != null) return false;
        var items = _doc.State.Items;
        _selected = items[^1];
        _others.AddRange(items.Take(items.Count - 1));
        InvalidateVisual();
        NotifySelection();
        return true;
    }

    /// <summary>Applies a change to every selected mark as one undo step and keeps them selected.</summary>
    private bool ChangeSelected(Func<Annotation, Annotation> change)
    {
        if (_doc == null || _primary == null) return false;
        var items = _doc.State.Items;
        int primaryIndex = items.FindIndex(a => ReferenceEquals(a, _primary));
        var chosen = Enumerable.Range(0, items.Count).Where(i => IsSelectedMark(items[i])).ToList();
        var next = items.Select((a, i) => chosen.Contains(i) ? change(a) : a).ToImmutableList();
        // A change that changes nothing, such as shrinking a mark already at its smallest, adds
        // no undo step and leaves the picture saved.
        if (chosen.All(i => Equals(next[i], items[i]))) return true;
        // Apply first: the document's change handler drops marks it no longer holds.
        _doc.Apply(_doc.State with { Items = next });
        _primary = next[primaryIndex];
        _others.Clear();
        _others.AddRange(chosen.Where(i => i != primaryIndex).Select(i => next[i]));
        InvalidateVisual();
        NotifySelection();
        return true;
    }

    /// <summary>Ctrl+D: copies of the selected marks, a little down and right, selected. Copied
    /// steps take new numbers after the highest one shown.</summary>
    public bool DuplicateSelected()
    {
        if (_doc == null || _primary == null) return false;
        var items = _doc.State.Items;
        int number = StepAnnotation.Next(items);
        var copies = new List<Annotation>();
        foreach (var a in items.Where(IsSelectedMark))
        {
            var copy = a.Offset(new Vector(12, 12));
            if (copy is StepAnnotation step) copy = step with { Number = number++ };
            copies.Add(copy);
        }
        _doc.Apply(_doc.State with { Items = items.AddRange(copies) });
        _selected = copies[^1];
        _others.AddRange(copies.Take(copies.Count - 1));
        InvalidateVisual();
        NotifySelection();
        return true;
    }

    /// <summary>Layer order: one step forward or back, or all the way, for the selected marks.</summary>
    public bool Reorder(int direction, bool allTheWay)
    {
        if (_doc == null || _primary == null) return false;
        var items = _doc.State.Items.ToList();
        var chosen = items.Where(IsSelectedMark).ToList();
        var rest = items.Where(a => !IsSelectedMark(a)).ToList();
        List<Annotation> next;
        if (allTheWay) next = direction > 0 ? [.. rest, .. chosen] : [.. chosen, .. rest];
        else
        {
            next = [.. items];
            var order = direction > 0 ? Enumerable.Range(0, next.Count).Reverse() : Enumerable.Range(0, next.Count);
            foreach (int i in order)
            {
                int j = i + Math.Sign(direction);
                if (!IsSelectedMark(next[i]) || j < 0 || j >= next.Count || IsSelectedMark(next[j])) continue;
                (next[i], next[j]) = (next[j], next[i]);
            }
        }
        if (next.SequenceEqual(items, ReferenceEqualityComparer.Instance)) return true;
        _doc.Apply(_doc.State with { Items = next.ToImmutableList() });
        InvalidateVisual();
        NotifySelection();
        return true;
    }

    /// <summary>F2 or double-click: opens the selected text or callout for editing. The edit
    /// replaces the mark as one undo step; Escape leaves it as it was.</summary>
    public bool EditSelectedText()
    {
        if (_doc == null || _textBox != null || _primary is not (TextAnnotation or CalloutAnnotation)) return false;
        var mark = _primary;
        _editing = mark;
        _toolBeforeEdit = (Color, TextSize);
        Color = mark.Color;
        TextSize = mark.Width;
        if (mark is CalloutAnnotation callout) BeginText(callout.Origin, callout.Tip, callout.Text);
        else if (mark is TextAnnotation text) BeginText(text.Origin, null, text.Text);
        InvalidateVisual();
        return true;
    }

    // The mark being edited again; hidden while its text box is open.
    private Annotation? _editing;
    // The tool's color and size while a placed mark is edited again, put back when it is cancelled.
    private (Color Color, double Size)? _toolBeforeEdit;

    private void RestoreToolAfterEdit()
    {
        if (_toolBeforeEdit is not { } tool) return;
        _toolBeforeEdit = null;
        Color = tool.Color;
        TextSize = tool.Size;
    }

    // ---- resize grip ----

    private const double GripSize = 9;

    /// <summary>The grip at the bottom right of a single selection, in view units.</summary>
    private Rect? GripRect()
    {
        if (_primary == null || _others.Count > 0 || _primary is ShapeAnnotation { Kind: ShapeKind.Arrow or ShapeKind.Line }) return null;
        var b = _primary.Bounds;
        var corner = ToView(b.BottomRight);
        return new Rect(corner.X - GripSize / 2, corner.Y - GripSize / 2, GripSize, GripSize);
    }

    private void DrawGrip(DrawingContext dc, Brush accent)
    {
        if (GripRect() is not { } grip || _moveBy != default) return;
        if (_resizeBy != default && _primary != null)
        {
            var corner = ToView(_primary.Resize(_resizeBy).Bounds.BottomRight);
            grip = new Rect(corner.X - GripSize / 2, corner.Y - GripSize / 2, GripSize, GripSize);
        }
        dc.DrawRectangle(Brushes.White, new Pen(accent, 1.5), grip);
    }

    private Vector _resizeBy;
    private bool _resizing;
}
