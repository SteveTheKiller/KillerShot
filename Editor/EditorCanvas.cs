using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerShot.Editor;

/// <summary>Draws the document at the current zoom and turns mouse input into marks. Zoom is in
/// device pixels: at 100% one capture pixel is one screen pixel on any monitor scale.</summary>
public sealed partial class EditorCanvas : Canvas
{
    private EditorDocument? _doc;
    private double _zoom = 1.0;

    // In-progress gesture, in image pixels.
    private Point _start;
    private bool _dragging;
    private List<Point>? _stroke;
    private Rect? _box;
    private Vector _moveBy;
    private TextBox? _textBox;
    private Point _textOrigin;

    public ToolKind Tool { get; private set; } = ToolKind.Pen;
    public Color Color { get; set; } = Color.FromRgb(0xE5, 0x39, 0x35);
    public double StrokeWidth { get; set; } = 4;
    public double TextSize { get; set; } = 24;
    /// <summary>New rectangles and ellipses are filled with the mark color.</summary>
    public bool Filled { get; set; }
    // A callout's tail tip while its text is being typed; null for plain text.
    private Point? _calloutTip;

    /// <summary>Raised when the zoom or the cursor position text should be refreshed.</summary>
    public event Action? ViewChanged;
    /// <summary>Raised for a status message such as a crop size.</summary>
    public event Action<string>? Status;

    public EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        Services.ImageDrag.Attach(this, point =>
        {
            if (Tool != ToolKind.Select || _doc == null || HitTest(ToImage(point)) != null) return null;
            var document = _doc;
            return () => { CommitText(); return document.Render(); };
        }, capturePointer: true);
    }

    public EditorDocument? Document
    {
        get => _doc;
        set
        {
            if (_doc != null) _doc.Changed -= OnDocChanged;
            CommitText();
            // A drag belongs to the picture it started on; its release must not reach the next.
            CancelGesture();
            _doc = value;
            SetQrCodes([]);
            _selected = null;
            if (_doc != null) _doc.Changed += OnDocChanged;
            System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public Annotation? Selected => _selected;

    private void OnDocChanged()
    {
        // A selection that no longer exists (undo, delete) is dropped.
        if (_selected != null && _doc != null && !_doc.State.Items.Contains(_selected)) _selected = null;
        if (_doc != null) _others.RemoveAll(o => !_doc.State.Items.Contains(o));
        // Screen readers see marks come and go.
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            double z = Math.Clamp(value, 0.05, 16);
            if (Math.Abs(z - _zoom) < 0.0001) return;
            CommitText();
            _zoom = z;
            InvalidateMeasure();
            InvalidateVisual();
            ViewChanged?.Invoke();
        }
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Device-independent units per image pixel.</summary>
    private double Scale => _zoom / DpiScale;

    public void SetTool(ToolKind tool)
    {
        CommitText();
        Tool = tool;
        if (tool != ToolKind.Select) _selected = null;
        Cursor = tool switch
        {
            ToolKind.Select => Cursors.Arrow,
            ToolKind.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        InvalidateVisual();
    }

    /// <summary>The zoom that fits the picture in the given viewport, never above 100%.</summary>
    public double FitZoom(Size viewport)
    {
        if (_doc == null || viewport.Width <= 0 || viewport.Height <= 0) return 1;
        double dpi = DpiScale;
        double zx = viewport.Width * dpi / _doc.PixelWidth, zy = viewport.Height * dpi / _doc.PixelHeight;
        return Math.Min(1.0, Math.Min(zx, zy));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        if (_doc == null) return new Size(0, 0);
        return new Size(_doc.PixelWidth * Scale, _doc.PixelHeight * Scale);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        PositionQrBadges();
        base.ArrangeOverride(arrangeSize);
        return arrangeSize;
    }

    private Point ToImage(Point p) => new(p.X / Scale, p.Y / Scale);
    private Point ToView(Point p) => new(p.X * Scale, p.Y * Scale);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_doc == null) return;
        var st = _doc.State;
        double s = Scale;
        RenderOptions.SetBitmapScalingMode(this, _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        dc.PushTransform(new ScaleTransform(s, s));
        var bounds = new Rect(0, 0, st.Image.PixelWidth, st.Image.PixelHeight);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawImage(st.Image, bounds);
        foreach (var a in st.Items)
        {
            if (ReferenceEquals(a, _editing)) continue;   // its text box stands in while it is edited
            if (_moveBy != default && IsSelectedMark(a))
                a.Offset(_moveBy).Render(dc);
            else if (_resizeBy != default && ReferenceEquals(a, _selected))
                a.Resize(_resizeBy).Render(dc);
            else
                a.Render(dc);
        }
        Preview()?.Render(dc);
        dc.Pop();
        dc.Pop();

        // Chrome drawn in view units so it stays one pixel wide at any zoom.
        var accent = TryFindResource("PrimaryBrush") as Brush ?? Brushes.Teal;
        DrawQrOutlines(dc);
        var dash = new Pen(accent, 1) { DashStyle = new DashStyle([4, 3], 0) };
        foreach (var mark in SelectedMarks)
        {
            var r = (_moveBy != default ? mark.Offset(_moveBy) : _resizeBy != default && ReferenceEquals(mark, _selected) ? mark.Resize(_resizeBy) : mark).Bounds;
            dc.DrawRectangle(null, dash, new Rect(ToView(r.TopLeft), ToView(r.BottomRight)));
        }
        DrawGrip(dc, accent);
        if (Tool == ToolKind.Crop && _box is { } box)
        {
            var v = new Rect(ToView(box.TopLeft), ToView(box.BottomRight));
            var all = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            var shade = new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(v));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), null, shade);
            dc.DrawRectangle(null, new Pen(accent, 1), v);
        }
    }

    /// <summary>The mark being drawn right now, if any.</summary>
    private Annotation? Preview()
    {
        if (!_dragging) return null;
        if (_stroke != null)
            return new StrokeAnnotation(_stroke.ToImmutableArray(), Color, Tool == ToolKind.Highlighter ? HighlighterWidth : StrokeWidth, Tool == ToolKind.Highlighter);
        if (_box is { } b && ShapeFor(Tool) is { } kind)
            return kind is ShapeKind.Arrow or ShapeKind.Line
                ? new ShapeAnnotation(kind, _start, _arrowEnd, Color, StrokeWidth)
                : new ShapeAnnotation(kind, b.TopLeft, b.BottomRight, Color, StrokeWidth, Filled && kind is ShapeKind.Rectangle or ShapeKind.Ellipse);
        return null;
    }

    private Point _arrowEnd;
    private double HighlighterWidth => Math.Max(12, StrokeWidth * 4);

    private static ShapeKind? ShapeFor(ToolKind t) => t switch
    {
        ToolKind.Arrow => ShapeKind.Arrow,
        ToolKind.Line => ShapeKind.Line,
        ToolKind.Rectangle => ShapeKind.Rectangle,
        ToolKind.Ellipse => ShapeKind.Ellipse,
        ToolKind.Redact => ShapeKind.Redact,
        _ => null,
    };

    private Point Clamp(Point p) => _doc == null ? p
        : new Point(Math.Clamp(p.X, 0, _doc.PixelWidth), Math.Clamp(p.Y, 0, _doc.PixelHeight));

    /// <summary>Shift keeps shapes square and arrows on 45 degree steps.</summary>
    private Rect BoxFrom(Point a, Point b, bool constrain)
    {
        if (constrain)
        {
            double d = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            b = new Point(a.X + Math.Sign(b.X - a.X) * d, a.Y + Math.Sign(b.Y - a.Y) * d);
        }
        return new Rect(a, b);
    }

    private static Point Snap45(Point a, Point b)
    {
        var v = b - a;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return a + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_doc == null || e.OriginalSource is TextBox) return;
        Focus();
        var p = ToImage(e.GetPosition(this));
        if (Tool == ToolKind.Text)
        {
            if (_textBox != null) { CommitText(); e.Handled = true; return; }
            BeginText(p);
            e.Handled = true;
            return;
        }
        if (Tool == ToolKind.Step)
        {
            AddStep(p);
            e.Handled = true;
            return;
        }
        if (Tool == ToolKind.Callout && _textBox != null) { CommitText(); e.Handled = true; return; }
        if (Tool == ToolKind.Select)
        {
            _moveBy = _resizeBy = default;
            _start = p;
            if (GripRect() is { } grip && grip.Contains(e.GetPosition(this)))
            {
                _resizing = _dragging = true;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            var hit = HitTest(p);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (hit != null) ToggleSelection(hit);
                e.Handled = true;
                return;
            }
            // A click on a mark already selected keeps the group, so it can be dragged together.
            if (hit == null || !IsSelectedMark(hit)) { if (!ReferenceEquals(hit, _selected) || _others.Count > 0) { _selected = hit; NotifySelection(); } }
            if (e.ClickCount == 2 && hit is TextAnnotation or CalloutAnnotation)
            {
                _selected = hit;
                EditSelectedText();
                e.Handled = true;
                return;
            }
            _dragging = _selected != null;
            if (_dragging) CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _start = Tool == ToolKind.Crop ? Clamp(p) : p;
        _dragging = true;
        _stroke = Tool is ToolKind.Pen or ToolKind.Highlighter ? [p] : null;
        _box = _stroke == null ? new Rect(_start, _start) : null;
        _arrowEnd = p;
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_doc == null) return;
        var p = ToImage(e.GetPosition(this));
        if (!_dragging)
        {
            if (Tool == ToolKind.Select) Cursor = HitTest(p) != null ? Cursors.SizeAll : Controls.DragCursors.Open;
            return;
        }
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (Tool == ToolKind.Select && _resizing)
        {
            _resizeBy = p - _start;
        }
        else if (Tool == ToolKind.Select && _selected != null)
        {
            _moveBy = p - _start;
        }
        else if (_stroke != null)
        {
            if ((p - _stroke[^1]).Length >= 0.75) _stroke.Add(p);
        }
        else
        {
            if (Tool == ToolKind.Crop) p = Clamp(p);
            _box = BoxFrom(_start, p, shift && Tool != ToolKind.Crop && Tool != ToolKind.Redact);
            _arrowEnd = shift ? Snap45(_start, p) : p;
            if (Tool == ToolKind.Crop && _box is { } b)
                Status?.Invoke($"{(int)Math.Round(b.Width)} x {(int)Math.Round(b.Height)}");
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging || _doc == null) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;

        if (Tool == ToolKind.Select)
        {
            var by = _moveBy;
            var size = _resizeBy;
            _moveBy = _resizeBy = default;
            if (_resizing) { _resizing = false; if (size.Length > 0.25) ChangeSelected(a => a.Resize(size)); }
            else if (_selected != null && by.Length > 0.25) ChangeSelected(a => a.Offset(by));
            InvalidateVisual();
            return;
        }

        if (Tool == ToolKind.Crop)
        {
            if (_box is { } b)
            {
                var r = new Int32Rect((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
                if (r.Width >= 2 && r.Height >= 2) _doc.Crop(r);
            }
            _box = null;
            InvalidateVisual();
            return;
        }

        if (Tool == ToolKind.Callout)
        {
            // Drag from the point being called out to where the bubble goes, then type.
            _box = null;
            var end = _arrowEnd;
            if ((end - _start).Length < 8) end = _start + new Vector(24, -48);
            BeginText(end, _start);
            InvalidateVisual();
            return;
        }

        _dragging = true;   // so Preview() returns the finished mark
        var mark = Preview();
        _dragging = false;
        _stroke = null;
        _box = null;
        if (mark != null && IsWorthKeeping(mark)) _doc.Add(mark);
        InvalidateVisual();
    }

    private static bool IsWorthKeeping(Annotation a) => a switch
    {
        ShapeAnnotation { Kind: ShapeKind.Arrow or ShapeKind.Line } s => (s.B - s.A).Length >= 3,
        ShapeAnnotation s => s.Box.Width >= 2 && s.Box.Height >= 2,
        _ => true,
    };

    private Annotation? HitTest(Point p)
    {
        if (_doc == null) return null;
        double tol = 4 / Scale;   // four screen units of slack at any zoom
        for (int i = _doc.State.Items.Count - 1; i >= 0; i--)
            if (_doc.State.Items[i].Hit(p, tol)) return _doc.State.Items[i];
        return null;
    }

    public bool DeleteSelected()
    {
        if (_doc == null || _selected == null) return false;
        var keep = _doc.State.Items.Where(a => !IsSelectedMark(a)).ToImmutableList();
        _selected = null;
        _doc.Apply(_doc.State with { Items = keep });
        InvalidateVisual();
        return true;
    }

    /// <summary>Arrow keys nudge the selection one pixel, ten with Shift.</summary>
    public bool Nudge(Vector v)
    {
        return ChangeSelected(a => a.Offset(v));
    }

    // ---- text ----

    private void BeginText(Point imagePoint, Point? calloutTip = null, string initial = "")
    {
        _textOrigin = imagePoint;
        _calloutTip = calloutTip;
        _textBox = new TextBox
        {
            MinWidth = 40,
            AcceptsReturn = true,
            FontFamily = TextAnnotation.Face.FontFamily,
            FontWeight = TextAnnotation.Face.Weight,
            FontSize = TextSize * Scale,
            Foreground = new SolidColorBrush(Color),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            BorderBrush = TryFindResource("PrimaryBrush") as Brush ?? Brushes.Teal,
            Padding = new Thickness(0),
            CaretBrush = new SolidColorBrush(Color),
            Text = initial,
        };
        var v = ToView(imagePoint);
        SetLeft(_textBox, v.X - 1);
        SetTop(_textBox, v.Y - 1);
        _textBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { CommitText(); Focus(); e.Handled = true; }
        };
        _textBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(CommitText);
        Children.Add(_textBox);
        _textBox.CaretIndex = initial.Length;
        _textBox.Focus();
        Keyboard.Focus(_textBox);
    }

    public bool IsEditingText => _textBox != null;

    public void CommitText()
    {
        var tb = _textBox;
        if (tb == null) return;
        _textBox = null;
        Children.Remove(tb);
        var text = tb.Text.TrimEnd();
        var edited = _editing;
        _editing = null;
        if (_doc != null && edited != null && _doc.State.Items.Contains(edited))
        {
            // An edited mark is replaced in place, keeping its layer; emptied, it is removed.
            var items = _doc.State.Items;
            int at = items.IndexOf(edited);
            Annotation? now = text.Length == 0 ? null : edited switch
            {
                CalloutAnnotation c => c with { Text = text },
                TextAnnotation t => t with { Text = text },
                _ => edited,
            };
            _selected = null;
            _doc.Apply(_doc.State with { Items = now == null ? items.RemoveAt(at) : items.SetItem(at, now) });
            _selected = now;
            NotifySelection();
            InvalidateVisual();
        }
        else if (_doc != null && text.Length > 0)
        {
            // The new text stays selected, so the keyboard can move it straight away.
            Annotation mark = _calloutTip is Point tip
                ? new CalloutAnnotation(tip, _textOrigin, text, Color, TextSize)
                : new TextAnnotation(_textOrigin, text, Color, TextSize);
            _doc.Add(mark);
            _selected = mark;
            NotifySelection();
            InvalidateVisual();
        }
    }

    private void CancelText()
    {
        if (_textBox == null) return;
        Children.Remove(_textBox);
        _textBox = null;
        _editing = null;
        InvalidateVisual();
        Focus();
    }

    /// <summary>Cancels a drag in progress. Returns false when there was nothing to cancel.</summary>
    public bool CancelGesture()
    {
        if (_textBox != null) { CancelText(); return true; }
        if (_dragging)
        {
            _dragging = false;
            _stroke = null;
            _box = null;
            _moveBy = _resizeBy = default;
            _resizing = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            return true;
        }
        if (_selected != null) { _selected = null; InvalidateVisual(); return true; }
        return false;
    }

    /// <summary>Image pixel under the mouse, for the footer readout.</summary>
    public Point? PixelUnderMouse()
    {
        if (_doc == null || !IsMouseOver) return null;
        var p = ToImage(Mouse.GetPosition(this));
        if (p.X < 0 || p.Y < 0 || p.X >= _doc.PixelWidth || p.Y >= _doc.PixelHeight) return null;
        return new Point(Math.Floor(p.X), Math.Floor(p.Y));
    }
}
