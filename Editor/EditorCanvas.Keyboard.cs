using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShot.Services;

namespace KillerShot.Editor;

/// <summary>Everything the mouse does to marks, without the mouse: place a mark with the current
/// tool, move between marks, resize the selected one, and the UI Automation tree screen readers use.</summary>
public sealed partial class EditorCanvas
{
    /// <summary>Raised when the selected mark changes, by keyboard, mouse or automation.</summary>
    public event Action? SelectionChanged;

    /// <summary>Selects a mark on the current picture, or clears the selection with null.</summary>
    public void Select(Annotation? mark)
    {
        if (_doc == null || (mark != null && !_doc.State.Items.Contains(mark))) return;
        if (ReferenceEquals(_selected, mark)) return;
        _selected = mark;
        InvalidateVisual();
        NotifySelection();
    }

    private void NotifySelection()
    {
        SelectionChanged?.Invoke();
        if (_selected != null && AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected)
            && UIElementAutomationPeer.FromElement(this) is EditorCanvasAutomationPeer peer)
            peer.RaiseSelected(_selected);
    }

    /// <summary>Tab: the next mark, Shift+Tab the previous one. Past either end the selection
    /// clears and false is returned, so focus moves on instead of being trapped in the picture.</summary>
    public bool SelectNext(bool back)
    {
        if (_doc == null || _textBox != null) return false;
        var items = _doc.State.Items;
        int at = _selected == null ? (back ? items.Count : -1) : items.IndexOf(_selected);
        int next = at + (back ? -1 : 1);
        if (next < 0 || next >= items.Count)
        {
            Select(null);
            return false;
        }
        Select(items[next]);
        return true;
    }

    /// <summary>Enter: the current tool's mark in the middle of the visible picture, selected so
    /// the arrow keys move it. Text opens its box there. Crop has nothing to place.</summary>
    public bool PlaceMark()
    {
        if (_doc == null || _textBox != null || Tool is ToolKind.Select or ToolKind.Crop) return false;
        var view = VisibleImage();
        var c = new Point(view.X + view.Width / 2, view.Y + view.Height / 2);
        double s = Math.Max(8, Math.Round(Math.Min(view.Width, view.Height) / 3));
        if (Tool == ToolKind.Text)
        {
            BeginText(new Point(Math.Round(c.X - s / 2), Math.Round(c.Y - TextSize / 2)));
            return true;
        }
        if (Tool == ToolKind.Callout)
        {
            // The tail points at the middle; the bubble sits above and to the right.
            BeginText(new Point(Math.Round(c.X + s / 2), Math.Round(c.Y - s)), new Point(Math.Round(c.X), Math.Round(c.Y)));
            return true;
        }
        if (Tool == ToolKind.Step)
        {
            // Each new step sits beside the last, so a keyboard sequence reads left to right.
            double size = StepSize;
            int placed = _doc.State.Items.OfType<StepAnnotation>().Count();
            int perRow = Math.Max(1, (int)(view.Width / (size * 1.4)));
            double x = view.X + size + placed % perRow * size * 1.4, y = c.Y + placed / perRow * size * 1.4;
            AddStep(new Point(Math.Round(Math.Min(x, view.Right - size / 2)), Math.Round(Math.Min(y, view.Bottom - size / 2))));
            return true;
        }
        var a = new Point(Math.Round(c.X - s / 2), Math.Round(c.Y - s / 3));
        var b = new Point(a.X + s, a.Y + Math.Round(s * 2 / 3));
        Annotation mark = Tool switch
        {
            ToolKind.Pen or ToolKind.Highlighter => new StrokeAnnotation([new Point(a.X, c.Y), new Point(b.X, c.Y)], Color,
                Tool == ToolKind.Highlighter ? HighlighterWidth : StrokeWidth, Tool == ToolKind.Highlighter),
            ToolKind.Line => new ShapeAnnotation(ShapeKind.Line, new Point(a.X, b.Y), new Point(b.X, a.Y), Color, StrokeWidth),
            _ => new ShapeAnnotation(ShapeFor(Tool)!.Value, a, b, Color, StrokeWidth, Filled && Tool is ToolKind.Rectangle or ToolKind.Ellipse),
        };
        _doc.Add(mark);
        _selected = mark;
        InvalidateVisual();
        NotifySelection();
        return true;
    }

    private double StepSize => Math.Max(22, StrokeWidth * 6);

    /// <summary>A numbered step at a point, taking the next number, selected for the keyboard.</summary>
    private void AddStep(Point at)
    {
        if (_doc == null) return;
        var step = new StepAnnotation(at, StepAnnotation.Next(_doc.State.Items), Color, StepSize);
        _doc.Add(step);
        _selected = step;
        InvalidateVisual();
        NotifySelection();
    }

    /// <summary>Ctrl+arrows: grows or shrinks the selected mark from its bottom right.</summary>
    public bool ResizeSelected(Vector d)
    {
        if (_doc == null || _selected == null) return false;
        var resized = _selected.Resize(d);
        _doc.Replace(_selected, resized);
        _selected = resized;
        InvalidateVisual();
        return true;
    }

    /// <summary>The part of the picture showing in the editor's scroller, in image pixels.</summary>
    private Rect VisibleImage()
    {
        var all = new Rect(0, 0, _doc!.PixelWidth, _doc.PixelHeight);
        Rect view = new(0, 0, ActualWidth, ActualHeight);
        DependencyObject? d = this;
        while (d != null && d is not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        if (d is ScrollViewer sv && sv.ViewportWidth > 0 && sv.IsAncestorOf(this))
            view.Intersect(sv.TransformToDescendant(this).TransformBounds(new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight)));
        if (view.IsEmpty) return all;
        var image = new Rect(ToImage(view.TopLeft), ToImage(view.BottomRight));
        image.Intersect(all);
        return image.IsEmpty ? all : image;
    }

    internal Rect ViewBounds(Annotation mark) => new(ToView(mark.Bounds.TopLeft), ToView(mark.Bounds.BottomRight));

    /// <summary>Replaces a mark with a changed copy and returns what is now on the picture.</summary>
    internal Annotation ChangeMark(Annotation mark, Func<Annotation, Annotation> change)
    {
        if (_doc == null || !_doc.State.Items.Contains(mark)) return mark;
        var now = change(mark);
        bool selected = ReferenceEquals(_selected, mark);   // Replace drops the old selection
        _doc.Replace(mark, now);
        if (selected) _selected = now;
        InvalidateVisual();
        return now;
    }

    internal double ViewScale => Scale;

    protected override AutomationPeer OnCreateAutomationPeer() => new EditorCanvasAutomationPeer(this);

    /// <summary>A spoken description: kind, text if any, size and position in image pixels.</summary>
    internal static string Describe(Annotation mark)
    {
        string kind = mark switch
        {
            StrokeAnnotation { Highlighter: true } => LocaleManager.Loc("Str_Tool_Highlighter"),
            StrokeAnnotation => LocaleManager.Loc("Str_Tool_Pen"),
            ShapeAnnotation { Kind: ShapeKind.Arrow } => LocaleManager.Loc("Str_Tool_Arrow"),
            ShapeAnnotation { Kind: ShapeKind.Line } => LocaleManager.Loc("Str_Tool_Line"),
            StepAnnotation s => LocaleManager.Loc("Str_Tool_Step") + " " + s.Number.ToString(CultureInfo.CurrentCulture),
            CalloutAnnotation c => LocaleManager.Loc("Str_Tool_Callout") + " \u201C" + c.Text + "\u201D",
            ShapeAnnotation { Kind: ShapeKind.Rectangle } => LocaleManager.Loc("Str_Tool_Rectangle"),
            ShapeAnnotation { Kind: ShapeKind.Ellipse } => LocaleManager.Loc("Str_Tool_Ellipse"),
            ShapeAnnotation => LocaleManager.Loc("Str_Tool_Redact"),
            TextAnnotation t => LocaleManager.Loc("Str_Tool_Text") + " “" + t.Text + "”",
            _ => "",
        };
        var b = mark is ShapeAnnotation { Kind: ShapeKind.Redact } r ? r.PixelCells : mark.Bounds;
        return string.Format(CultureInfo.CurrentCulture, LocaleManager.Loc("Str_Mark_Name"), kind,
            Math.Round(b.Width), Math.Round(b.Height), Math.Round(b.X), Math.Round(b.Y));
    }
}

/// <summary>The picture as a selection container of its marks.</summary>
internal sealed class EditorCanvasAutomationPeer(EditorCanvas owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private readonly ConditionalWeakTable<Annotation, AnnotationAutomationPeer> _peers = new();

    private EditorCanvas Canvas => (EditorCanvas)Owner;

    protected override string GetClassNameCore() => nameof(EditorCanvas);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? LocaleManager.Loc("Str_Mark_List") : name;
    }

    public override object? GetPattern(PatternInterface pattern) =>
        pattern == PatternInterface.Selection ? this : base.GetPattern(pattern);

    internal AnnotationAutomationPeer PeerFor(Annotation mark) =>
        _peers.GetValue(mark, m => new AnnotationAutomationPeer(this, m));

    protected override List<AutomationPeer> GetChildrenCore()
    {
        var children = base.GetChildrenCore() ?? [];
        if (Canvas.Document is { } doc)
            foreach (var mark in doc.State.Items) children.Add(PeerFor(mark));
        return children;
    }

    internal void RaiseSelected(Annotation mark)
    {
        ResetChildrenCache();
        PeerFor(mark).RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
    }

    internal EditorCanvas Owned => Canvas;

    IRawElementProviderSimple[] ISelectionProvider.GetSelection() =>
        Canvas.Selected is { } s ? [ProviderFromPeer(PeerFor(s))] : [];
    bool ISelectionProvider.CanSelectMultiple => false;
    bool ISelectionProvider.IsSelectionRequired => false;
}

/// <summary>One mark: selectable, movable and resizable through automation.</summary>
internal sealed class AnnotationAutomationPeer(EditorCanvasAutomationPeer parent, Annotation mark)
    : AutomationPeer, ISelectionItemProvider, ITransformProvider
{
    private EditorCanvas Canvas => parent.Owned;
    // Marks are immutable; after a move or resize this peer follows the replacement.
    private Annotation _mark = mark;
    private Annotation Mark => _mark;

    protected override string GetNameCore() => EditorCanvas.Describe(Mark);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    protected override string GetClassNameCore() => nameof(Annotation);
    protected override string GetAutomationIdCore() => "";
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => "";
    protected override string GetItemTypeCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override bool HasKeyboardFocusCore() => Canvas.IsKeyboardFocused && ReferenceEquals(Canvas.Selected, Mark);
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => true;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool IsOffscreenCore() => !Canvas.IsVisible;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override void SetFocusCore() { Canvas.Focus(); Canvas.Select(Mark); }
    protected override Point GetClickablePointCore()
    {
        var r = GetBoundingRectangleCore();
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }
    protected override Rect GetBoundingRectangleCore()
    {
        if (PresentationSource.FromVisual(Canvas) == null) return Rect.Empty;
        var v = Canvas.ViewBounds(Mark);
        return new Rect(Canvas.PointToScreen(v.TopLeft), Canvas.PointToScreen(v.BottomRight));
    }

    public override object? GetPattern(PatternInterface pattern) =>
        pattern is PatternInterface.SelectionItem or PatternInterface.Transform ? this : null;

    void ISelectionItemProvider.Select() => Canvas.Select(Mark);
    void ISelectionItemProvider.AddToSelection() => Canvas.Select(Mark);
    void ISelectionItemProvider.RemoveFromSelection() { if (ReferenceEquals(Canvas.Selected, Mark)) Canvas.Select(null); }
    bool ISelectionItemProvider.IsSelected => ReferenceEquals(Canvas.Selected, Mark);
    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(parent);

    bool ITransformProvider.CanMove => true;
    bool ITransformProvider.CanResize => Mark is not ShapeAnnotation { Kind: ShapeKind.Arrow or ShapeKind.Line };
    bool ITransformProvider.CanRotate => false;

    /// <summary>Moves the mark's top left corner to (x, y) in image pixels.</summary>
    void ITransformProvider.Move(double x, double y)
    {
        var b = Mark.Bounds;
        Canvas.Select(Mark);
        _mark = Canvas.ChangeMark(Mark, m => m.Offset(new Vector(x - b.X, y - b.Y)));
    }

    /// <summary>Sets the mark's size in image pixels.</summary>
    void ITransformProvider.Resize(double width, double height)
    {
        var b = Mark is ShapeAnnotation s ? s.Box : Mark.Bounds;
        Canvas.Select(Mark);
        _mark = Canvas.ChangeMark(Mark, m => m.Resize(new Vector(width - b.Width, height - b.Height)));
    }

    void ITransformProvider.Rotate(double degrees) => throw new InvalidOperationException();
}
