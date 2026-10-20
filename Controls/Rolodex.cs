using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>
/// A rolodex of captures for a narrow column: every capture is a card on a drum that turns
/// about a level axis. The current card faces the viewer; the ones before it lean back over the
/// top of the drum and the ones after it under the bottom, each overlapping the next, so the
/// column flips through cards the way a desk rolodex does. Drawn with real perspective in a
/// Viewport3D, the same way as <see cref="CoverFlow"/>.
///
/// Position is a double that eases toward SelectedIndex, so a jump of any distance turns the
/// drum through the cards in between. Only the cards on the visible half of the drum exist as
/// models.
///
/// Mouse: click a card to bring it to the front, click the front card to open it, wheel to
/// turn, Ctrl+wheel to draw the cards larger or smaller, right-click for the item menu. Keyboard: Up, Down, Home, End, Page Up and Page Down
/// move; Enter opens; the context menu key or Shift+F10 opens the item menu.
/// </summary>
internal sealed class Rolodex : FrameworkElement
{
    private const int Window = 30;              // models kept on each side of the position
    private const double CardWidth = 1.0;       // model units
    private const double CardHeight = 0.74;
    private const double CaptionBand = 0.13;    // the strip at the foot of a card that holds its title
    private const double Inset = 0.012;         // a few pixels of card stock around the picture
    private const double ViewWidth = 1.24;      // model units visible across at Zoom 1: the risen front card, a little inset
    private const double CameraDistance = 3.2;
    private const double Reveal = 0.40;         // how much of the next card shows past the front one
    private const double MaxAngle = 86;         // cards past this, at the drum's top or bottom, are dropped
    private const double SinkBack = 0.45;       // how far every other card sits behind the front one
    private const double Lift = 0.25;           // how far the front card rises toward the viewer
    private const double Grow = 0.1;            // and how much larger it is drawn there

    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _root = new();
    private readonly Model3DGroup _scene = new();
    private readonly Dictionary<int, Card> _cards = [];
    private readonly Dictionary<GeometryModel3D, int> _hits = [];
    private IReadOnlyList<CaptureItem> _items = [];
    private readonly CarouselMotion _motion;
    private double _position => _motion.Position;
    private double _radius = 1.4, _step = 17;   // drum radius and degrees between cards, from the size
    private int _orderBucket = int.MinValue;
    public bool IsMoving => _motion.IsMoving;
    private Color _cardColor = Color.FromRgb(0x14, 0x14, 0x14);
    private double _zoom = 0.85;
    private bool _showInfoCards = ViewInfoCards.Get(true);
    public bool ShowInfoCards
    {
        get => _showInfoCards;
        set { if (_showInfoCards == value) return; _showInfoCards = value; Refresh(); }
    }
    private static ImageBrush? s_shadowModern, s_shadowSquare;

    private sealed class Card
    {
        public required Model3DGroup Group { get; init; }
        public required GeometryModel3D Face { get; init; }
        public required AxisAngleRotation3D Rotation { get; init; }
        public required TranslateTransform3D Translation { get; init; }
        public required ScaleTransform3D Scale { get; init; }
    }

    /// <summary>Gives a capture's thumbnail; null draws an empty card.</summary>
    public Func<CaptureItem, BitmapSource?>? ThumbnailSource { get; set; }
    public bool DeferHiddenLoading { get; set; }

    /// <summary>The title printed at the foot of each card.</summary>
    public Func<CaptureItem, string>? Caption { get; set; }

    /// <summary>Formats a capture for screen readers.</summary>
    public Func<CaptureItem, string>? Describe { get; set; }

    /// <summary>A color for a card's frame, such as its collection's; null leaves it plain.</summary>
    public Func<CaptureItem, Color?>? Accent { get; set; }

    /// <summary>Draws every card again, after the card or collection colors change.</summary>
    public void Refresh()
    {
        ClearCards();
        Rebuild();
    }

    /// <summary>The card stock behind each picture. Dark by default, so the pictures stand out.</summary>
    public Color CardColor
    {
        get => _cardColor;
        set { if (_cardColor == value) return; _cardColor = value; ClearCards(); Rebuild(); }
    }

    /// <summary>How large the cards are drawn. At maximum the front picture fills the column;
    /// smaller values show more of the drum. Ctrl+wheel changes it.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            double z = Math.Clamp(value, MinZoom, MaxZoom);
            if (Math.Abs(z - _zoom) < 0.0001) return;
            _zoom = z;
            InvalidateArrange();
            ZoomChanged?.Invoke();
        }
    }

    public const double MinZoom = 0.35, MaxZoom = 1.12;

    /// <summary>Ctrl+wheel changed the zoom, so the host can remember it.</summary>
    public event Action? ZoomChanged;

    /// <summary>The caption text color, chosen against <see cref="CardColor"/>.</summary>
    private Brush CaptionBrush => _cardColor.R * 0.299 + _cardColor.G * 0.587 + _cardColor.B * 0.114 > 150
        ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

    public event Action<CaptureItem>? ItemActivated;
    public event Action<CaptureItem, FrameworkElement>? ItemMenuRequested;
    public event Action? SelectionChanged;

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(Rolodex),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged, CoerceSelectedIndex));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public CaptureItem? SelectedItem => SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

    public IReadOnlyList<CaptureItem> Items
    {
        get => _items;
        set
        {
            long? keep = SelectedItem?.Id;
            _items = value ?? [];
            ClearCards();
            int index = keep is long id ? IndexOf(id) : 0;
            SetCurrentValue(SelectedIndexProperty, Math.Max(0, index));
            _motion.Reset(SelectedIndex);
            Rebuild();
            SelectionChanged?.Invoke();
            UpdateName();
        }
    }

    public Rolodex()
    {
        _motion = new CarouselMotion(this, Rebuild, true,
            () => ActualWidth * _zoom * Reveal / ViewWidth, () => Math.Max(0, _items.Count - 1),
            index => SetCurrentValue(SelectedIndexProperty, index), cyclic: true);
        ImageDrag.Attach(this, point =>
        {
            if (IndexAt(point) is not int index || index >= _items.Count) return null;
            var item = _items[index];
            return () => CaptureLibrary.LoadImage(item);
        }, verticalNavigation: true);
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, CameraDistance),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 30,
        };
        _scene.Children.Add(new AmbientLight(Colors.White));
        _root.Content = _scene;
        _viewport.Children.Add(_root);
        AddVisualChild(_viewport);
        IsVisibleChanged += (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += Refresh;
            CaptureLibrary.ThumbnailReady += OnThumbnailReady;
            ViewInfoCards.Changed += SyncInfoCards;
            SyncInfoCards();
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= Refresh;
            CaptureLibrary.ThumbnailReady -= OnThumbnailReady;
            ViewInfoCards.Changed -= SyncInfoCards;
        };
    }

    private void SyncInfoCards() => ShowInfoCards = ViewInfoCards.Get(true);

    private void OnThumbnailReady(CaptureItem item) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (IsLoaded && IsVisible && _items.Contains(item)) Refresh();
    }));

    private int IndexOf(long id)
    {
        for (int i = 0; i < _items.Count; i++) if (_items[i].Id == id) return i;
        return -1;
    }

    private static object CoerceSelectedIndex(DependencyObject d, object value)
    {
        var r = (Rolodex)d;
        int count = r._items.Count;
        return count == 0 ? 0 : ((int)value % count + count) % count;
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var r = (Rolodex)d;
        r._motion.MoveTo(r.SelectedIndex);
        r.SelectionChanged?.Invoke();
        r.UpdateName();
    }

    // ---- layout ----

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _viewport;
    protected override HitTestResult HitTestCore(PointHitTestParameters parameters) => new PointHitTestResult(this, parameters.HitPoint);

    protected override Size MeasureOverride(Size availableSize)
    {
        _viewport.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport.Arrange(new Rect(finalSize));
        if (finalSize.Width > 0 && finalSize.Height > 0 && _viewport.Camera is PerspectiveCamera camera)
        {
            // The front card fills the width; the drum is sized to the height, so a taller column
            // shows more cards and the spacing between them stays the same.
            double viewWidth = ViewWidth / _zoom;
            camera.FieldOfView = 2 * Math.Atan(viewWidth / 2 / CameraDistance) * 180 / Math.PI;
            double halfHeight = viewWidth * finalSize.Height / finalSize.Width / 2;
            // Grow the drum until its farthest card, seen in perspective, reaches the column's edge.
            double radius = 0.5;
            while (radius < 20 && Extent(radius * 1.05) < halfHeight * 0.97) radius *= 1.05;
            _radius = radius;
            _step = Math.Clamp(Math.Asin(Math.Min(1, Reveal / _radius)) * 180 / Math.PI, 2, 35);
            Rebuild();
        }
        return finalSize;
    }

    /// <summary>How far from the middle the top edge of the farthest visible card lands on screen,
    /// in model units at the front card, for a drum of this radius.</summary>
    private static double Extent(double radius)
    {
        double max = 0;
        for (double deg = 0; deg <= MaxAngle; deg += 2)
        {
            double t = deg * Math.PI / 180;
            double y = radius * Math.Sin(t) + CardHeight / 2 * Math.Cos(t);
            double z = radius * (Math.Cos(t) - 1) - CardHeight / 2 * Math.Sin(t);
            max = Math.Max(max, y * CameraDistance / (CameraDistance - z));
        }
        return max;
    }

    // ---- models ----

    private void ClearCards()
    {
        _orderBucket = int.MinValue;
        foreach (var card in _cards.Values) _scene.Children.Remove(card.Group);
        _cards.Clear();
        _hits.Clear();
    }

    private double Distance(int index)
    {
        if (_items.Count == 0) return 0;
        double distance = (index - _position) % _items.Count;
        if (distance > _items.Count / 2.0) distance -= _items.Count;
        if (distance < -_items.Count / 2.0) distance += _items.Count;
        return distance;
    }

    private bool Visible(int index) => Math.Abs(Distance(index) * _step) <= MaxAngle;

    private void Rebuild()
    {
        if (DeferHiddenLoading && !IsVisible) return;
        int center = (int)Math.Round(_position);
        var wanted = _items.Count == 0 ? [] : Enumerable.Range(center - Window, Window * 2 + 1)
            .Select(i => (i % _items.Count + _items.Count) % _items.Count).Where(Visible).ToHashSet();
        foreach (int stale in _cards.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _orderBucket = int.MinValue;
            _scene.Children.Remove(_cards[stale].Group);
            _hits.Remove(_cards[stale].Face);
            _cards.Remove(stale);
        }
        foreach (int i in wanted)
            if (!_cards.ContainsKey(i)) { _cards[i] = CreateCard(i); _orderBucket = int.MinValue; }
        Place();
    }

    private Card CreateCard(int index)
    {
        var item = _items[index];
        BitmapSource? thumb = ThumbnailSource?.Invoke(item);
        var stock = new SolidColorBrush(_cardColor);
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 0.004);

        // The card: stock with a faint edge, the picture fitted inside its frame, and the title
        // at the foot. Drawn as vectors, so it stays sharp at any size.
        var drawing = new DrawingGroup();
        double corner = ThemeManager.Current == Theme.SE98 ? 0 : 0.03;
        double aspect = item.Height > 0 ? (double)item.Width / item.Height : 1.6;
        double width = ShowInfoCards ? CardWidth : Math.Min(CardWidth, CardHeight * aspect);
        double height = ShowInfoCards ? CardHeight : width / aspect;
        double photoWidth = Math.Min(CardWidth - 2 * Inset, (CardHeight - Inset - CaptionBand) * aspect);
        double photoHeight = photoWidth / aspect;
        if (ShowInfoCards)
        {
            width = photoWidth + 2 * Inset;
            height = photoHeight + Inset + CaptionBand;
        }
        if (ShowInfoCards)
            drawing.Children.Add(new GeometryDrawing(stock, edge, new RectangleGeometry(new Rect(0, 0, width, height), corner, corner)));
        var frame = ShowInfoCards ? new Rect(Inset, Inset, photoWidth, photoHeight)
            : new Rect(0, 0, width, height);
        double w = Math.Min(frame.Width, frame.Height * aspect), h = w / aspect;
        var picture = new Rect(frame.X + (frame.Width - w) / 2, frame.Y + (frame.Height - h) / 2, w, h);
        if (thumb != null) drawing.Children.Add(new ImageDrawing(thumb, picture));
        else drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(60, 60, 60)), null, new RectangleGeometry(picture)));
        string caption = Caption?.Invoke(item) ?? "";
        if (ShowInfoCards && caption.Length > 0)
        {
            var text = new FormattedText(caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), CaptionBand * 0.46, CaptionBrush, 1.0)
            {
                MaxTextWidth = width - 2 * Inset,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var origin = new Point(Inset, height - CaptionBand + (CaptionBand - text.Height) / 2);
            drawing.Children.Add(new GeometryDrawing(CaptionBrush, null, text.BuildGeometry(origin)));
        }
        if (ShowInfoCards && Accent?.Invoke(item) is Color accent)
            PhotoCardBorder.DrawFrame(drawing, new Rect(0, 0, width, height), corner, accent, .012);
        // Keep the brush the card's full size even where the drawing leaves transparent corners.
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill, Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute };
        brush.Freeze();

        var face = new GeometryModel3D(Quad(width, height), new DiffuseMaterial(brush)) { BackMaterial = new DiffuseMaterial(brush) };
        // A soft drop shadow just behind the card and a little below it.
        var shadow = new GeometryModel3D(ShadowQuad(width, height), new DiffuseMaterial(ShadowBrush()));
        var rotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        var translation = new TranslateTransform3D();
        var scale = new ScaleTransform3D();
        var transforms = new Transform3DGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(new RotateTransform3D(rotation));
        transforms.Children.Add(translation);
        var group = new Model3DGroup { Transform = transforms };
        group.Children.Add(shadow);
        group.Children.Add(face);
        _scene.Children.Add(group);
        _hits[face] = index;
        return new Card { Group = group, Face = face, Rotation = rotation, Translation = translation, Scale = scale };
    }

    private const double ShadowMargin = 0.09, ShadowDrop = 0.03;

    /// <summary>The shadow's quad: the card grown by the blur margin, dropped a little, and set a
    /// hair behind the card so the card always draws over it.</summary>
    private static MeshGeometry3D ShadowQuad(double width, double height)
    {
        double x = width / 2 + ShadowMargin, y = height / 2 + ShadowMargin, dy = -ShadowDrop, z = -0.006;
        var mesh = new MeshGeometry3D
        {
            Positions = [new Point3D(-x, -y + dy, z), new Point3D(x, -y + dy, z), new Point3D(x, y + dy, z), new Point3D(-x, y + dy, z)],
            TextureCoordinates = [new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0)],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>A blurred card shadow, cached for modern and classic corners.</summary>
    private static ImageBrush ShadowBrush()
    {
        bool square = ThemeManager.Current == Theme.SE98;
        if (square && s_shadowSquare != null) return s_shadowSquare;
        if (!square && s_shadowModern != null) return s_shadowModern;
        const double ppu = 220;   // pixels per model unit
        double w = (CardWidth + 2 * ShadowMargin) * ppu, h = (CardHeight + 2 * ShadowMargin) * ppu;
        var rect = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            CornerRadius = new CornerRadius(square ? 0 : 8),
            Margin = new Thickness(ShadowMargin * ppu),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = ShadowMargin * ppu * 0.8 },
        };
        var host = new Grid { Width = w, Height = h, Background = Brushes.Transparent };
        host.Children.Add(rect);
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        var shadow = new ImageBrush(bitmap);
        shadow.Freeze();
        if (square) s_shadowSquare = shadow;
        else s_shadowModern = shadow;
        return shadow;
    }

    /// <summary>A card-sized quad centered on the origin, texture upright.</summary>
    private static MeshGeometry3D Quad(double width, double height)
    {
        double x = width / 2, y = height / 2;
        var mesh = new MeshGeometry3D
        {
            Positions = [new Point3D(-x, -y, 0), new Point3D(x, -y, 0), new Point3D(x, y, 0), new Point3D(-x, y, 0)],
            TextureCoordinates = [new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0)],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Puts every card on the drum by its distance from the position: earlier cards turn
    /// up and back over the top, later ones down and back under the bottom. The front card rises
    /// out of the drum toward the viewer, larger, so its whole picture shows; the rest sink back
    /// behind it. Both follow the position, so a card rises as it turns to the front.</summary>
    private void Place()
    {
        foreach (var (index, card) in _cards)
        {
            double d = Distance(index);
            double front = 1 - Math.Min(1, Math.Abs(d));   // 1 at the front, 0 a card away
            double theta = -d * _step * Math.PI / 180;
            card.Translation.OffsetY = _radius * Math.Sin(theta);
            card.Translation.OffsetZ = _radius * (Math.Cos(theta) - 1) - SinkBack * (1 - front) + Lift * front;
            card.Rotation.Angle = -theta * 180 / Math.PI;
            double grow = 1 + Grow * front;
            card.Scale.ScaleX = card.Scale.ScaleY = grow;
        }
        // Back to front, so the front card covers the ones behind it.
        int bucket = (int)Math.Floor(_position * 2);
        if (_orderBucket == bucket) return;
        _orderBucket = bucket;
        var order = _cards.OrderByDescending(kv => Math.Abs(Distance(kv.Key))).Select(kv => kv.Value.Group).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int at = _scene.Children.IndexOf(order[i]);
            if (at != i + 1)
            {
                _scene.Children.Remove(order[i]);
                _scene.Children.Insert(i + 1, order[i]);
            }
        }
    }

    // ---- input ----

    private int? IndexAt(Point p)
    {
        int? found = null;
        VisualTreeHelper.HitTest(_viewport, null, r =>
        {
            if (r is RayMeshGeometry3DHitTestResult mesh && mesh.ModelHit is GeometryModel3D model && _hits.TryGetValue(model, out int i))
            {
                found = i;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return found;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); _motion.Press(e); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _motion.Drag(e); Cursor = IndexAt(e.GetPosition(this)).HasValue ? DragCursors.Open : Cursors.Arrow; }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _motion.LostCapture(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_motion.Release(e)) { e.Handled = true; return; }
        Focus();
        if (IndexAt(e.GetPosition(this)) is not int i) return;
        e.Handled = true;
        if (i == SelectedIndex) ItemActivated?.Invoke(_items[i]);
        else SelectedIndex = i;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _motion.Interrupt();
        Focus();
        if (IndexAt(e.GetPosition(this)) is not int i) return;
        e.Handled = true;
        SelectedIndex = i;
        ItemMenuRequested?.Invoke(_items[i], this);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _motion.Interrupt();
        e.Handled = true;
        // Ctrl+wheel: larger cards and fewer of them, or smaller cards and more of the drum.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { Zoom *= e.Delta > 0 ? 1.1 : 1 / 1.1; return; }
        if (_items.Count == 0) return;
        SelectedIndex += e.Delta > 0 ? -1 : 1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Enter or Key.Apps
            || shift && (e.Key == Key.F10 || e.Key == Key.System && e.SystemKey == Key.F10)) _motion.Interrupt();
        switch (e.Key)
        {
            case Key.Up: SelectedIndex--; break;
            case Key.Down: SelectedIndex++; break;
            case Key.PageUp: SelectedIndex -= 5; break;
            case Key.PageDown: SelectedIndex += 5; break;
            case Key.Home: SelectedIndex = 0; break;
            case Key.End: SelectedIndex = _items.Count - 1; break;
            case Key.Enter when SelectedItem != null: ItemActivated?.Invoke(SelectedItem); break;
            case Key.Apps when SelectedItem != null:
            case Key.F10 when shift && SelectedItem != null:
            case Key.System when e.SystemKey == Key.F10 && shift && SelectedItem != null:
                ItemMenuRequested?.Invoke(SelectedItem!, this);
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        UpdateName();
    }

    // ---- screen readers ----

    private void UpdateName()
    {
        string name = SelectedItem is { } item && Describe != null
            ? Describe(item) + ", " + (SelectedIndex + 1) + " / " + _items.Count
            : "";
        AutomationProperties.SetName(this, name);
        if (IsKeyboardFocused && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, "", name);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RolodexPeer(this);

    private sealed class RolodexPeer(Rolodex owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => nameof(Rolodex);
        protected override bool IsKeyboardFocusableCore() => true;
    }
}
