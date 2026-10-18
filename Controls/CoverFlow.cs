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
/// A cover flow of captures, in the style of the old iTunes album browser: the current capture
/// faces the viewer, the rest stand angled to either side, and every picture stands on a glossy
/// floor with its reflection beneath it. Drawn with real perspective in a Viewport3D.
///
/// Position is a double that eases toward SelectedIndex, so a jump of any distance slides
/// through the pictures in between. Only the pictures near the position exist as 3D models; they
/// are created and dropped as the position moves, and each model's transform is recomputed every
/// frame while it eases.
///
/// Mouse: click a side picture to bring it to the front, click the front picture to open it,
/// wheel to step, right-click for the item menu. Keyboard: Left, Right, Home, End, Page Up and
/// Page Down move; Enter opens; the context menu key or Shift+F10 opens the item menu.
/// </summary>
internal sealed class CoverFlow : FrameworkElement
{
    private const int Window = 9;            // models kept on each side of the position
    private const double SideAngle = 62;     // degrees a side picture is turned
    private const double CenterGap = 1.15;   // distance from the front picture to the first side one
    private const double SideStep = 0.40;    // distance between side pictures
    private const double SideDepth = 0.75;   // how far behind the front picture the side ones stand
    private const double MaxWidth = 1.5;     // the widest a picture is drawn, in model units
    private const double MaxHeight = 1.0;
    private const double CameraDistance = 3.1;
    /// <summary>The height the camera looks along, in model units: the middle of the view, with
    /// the pictures above it and their reflections below. Lower trims the room above the
    /// pictures and shows more of the reflections.</summary>
    public double ViewCenterY
    {
        get => _viewCenterY;
        set
        {
            _viewCenterY = value;
            if (_viewport.Camera is PerspectiveCamera camera) camera.Position = new Point3D(0, value, CameraDistance);
        }
    }
    private double _viewCenterY = 0.50;
    /// <summary>Model units visible top to bottom at the front picture. Smaller draws the
    /// pictures larger; the collection strip, which is short, uses a smaller value.</summary>
    public double ViewHeight { get; set; } = 1.60;
    private const double MinViewWidth = 4.6;      // model units always visible across at the front picture

    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _root = new();
    private readonly Model3DGroup _scene = new();
    private readonly Dictionary<int, Card> _cards = [];
    private readonly Dictionary<GeometryModel3D, int> _hits = [];
    private IReadOnlyList<CaptureItem> _items = [];
    private double _position;
    private TimeSpan _lastFrame;
    private bool _animating;

    private sealed class Card
    {
        public required Model3DGroup Group { get; init; }
        public required GeometryModel3D Face { get; init; }
        public required AxisAngleRotation3D Rotation { get; init; }
        public required TranslateTransform3D Translation { get; init; }
    }

    /// <summary>Gives a capture's thumbnail; null draws a blank card.</summary>
    public Func<long, BitmapSource?>? ThumbnailSource { get; set; }

    /// <summary>The front picture was clicked or Enter was pressed.</summary>
    public event Action<CaptureItem>? ItemActivated;

    /// <summary>The item menu was asked for, with the item and the element to anchor it to.</summary>
    public event Action<CaptureItem, FrameworkElement>? ItemMenuRequested;

    /// <summary>Delete was pressed on the front picture.</summary>
    public event Action<CaptureItem>? DeleteRequested;

    /// <summary>The front picture changed.</summary>
    public event Action? SelectionChanged;

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(CoverFlow),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged, CoerceSelectedIndex));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public static readonly DependencyProperty MaximumIndexProperty = DependencyProperty.Register(
        nameof(MaximumIndex), typeof(int), typeof(CoverFlow), new PropertyMetadata(0));

    /// <summary>The last index, for a slider bound to SelectedIndex.</summary>
    public int MaximumIndex
    {
        get => (int)GetValue(MaximumIndexProperty);
        private set => SetValue(MaximumIndexProperty, value);
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
            MaximumIndex = Math.Max(0, _items.Count - 1);
            int index = keep is long id ? IndexOf(id) : 0;
            SetCurrentValue(SelectedIndexProperty, Math.Max(0, index));
            _position = SelectedIndex;
            Rebuild();
            SelectionChanged?.Invoke();
            UpdateName();
        }
    }

    public CoverFlow()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(0, _viewCenterY, CameraDistance),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 60,
        };
        // Unlit-looking pictures: a full white ambient light draws each texture at its own colors.
        _scene.Children.Add(new AmbientLight(Colors.White));
        _root.Content = _scene;
        _viewport.Children.Add(_root);
        AddVisualChild(_viewport);
        Unloaded += (_, _) => StopAnimation();
        Loaded += (_, _) => { if (Math.Abs(_position - SelectedIndex) > 0.001) StartAnimation(); };
    }

    private int IndexOf(long id)
    {
        for (int i = 0; i < _items.Count; i++) if (_items[i].Id == id) return i;
        return -1;
    }

    /// <summary>Brings a capture to the front, sliding there.</summary>
    public void Select(long id)
    {
        int i = IndexOf(id);
        if (i >= 0) SelectedIndex = i;
    }

    private static object CoerceSelectedIndex(DependencyObject d, object value)
    {
        var flow = (CoverFlow)d;
        int max = Math.Max(0, flow._items.Count - 1);
        return Math.Clamp((int)value, 0, max);
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var flow = (CoverFlow)d;
        flow.StartAnimation();
        flow.SelectionChanged?.Invoke();
        flow.UpdateName();
    }

    // ---- layout ----

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _viewport;

    protected override Size MeasureOverride(Size availableSize)
    {
        _viewport.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport.Arrange(new Rect(finalSize));
        // WPF's field of view is horizontal, so it is worked out from the shape of the control:
        // in a wide, short control the front picture and its reflection fill the height and the
        // extra width shows more pictures to the sides; in a taller one the view widens until
        // the front picture and two side pictures each way fit across.
        if (finalSize.Height > 0 && _viewport.Camera is PerspectiveCamera camera)
        {
            double aspect = finalSize.Width / finalSize.Height;
            double height = Math.Max(ViewHeight, MinViewWidth / aspect);
            double halfV = height / 2 / CameraDistance;
            camera.FieldOfView = Math.Min(150, 2 * Math.Atan(aspect * halfV) * 180 / Math.PI);
        }
        return finalSize;
    }

    // ---- models ----

    private void ClearCards()
    {
        foreach (var card in _cards.Values) _scene.Children.Remove(card.Group);
        _cards.Clear();
        _hits.Clear();
    }

    /// <summary>Creates the models near the position, drops the rest, and places them all.</summary>
    private void Rebuild()
    {
        int center = (int)Math.Round(_position);
        int first = Math.Max(0, center - Window), last = Math.Min(_items.Count - 1, center + Window);
        foreach (int stale in _cards.Keys.Where(k => k < first || k > last).ToList())
        {
            _scene.Children.Remove(_cards[stale].Group);
            _hits.Remove(_cards[stale].Face);
            _cards.Remove(stale);
        }
        for (int i = first; i <= last; i++)
            if (!_cards.ContainsKey(i)) _cards[i] = CreateCard(i);
        Place();
    }

    private Card CreateCard(int index)
    {
        var item = _items[index];
        BitmapSource? thumb = ThumbnailSource?.Invoke(item.Id);
        double aspect = item.Height > 0 ? (double)item.Width / item.Height : 1.6;
        double w = Math.Min(MaxWidth, MaxHeight * aspect);
        double h = w / aspect;

        Brush faceBrush = thumb != null
            ? new ImageBrush(thumb) { Stretch = Stretch.Fill }
            : new SolidColorBrush(Color.FromRgb(60, 60, 60));
        faceBrush.Freeze();
        var face = new GeometryModel3D(Quad(w, 0, h, flipV: false), new DiffuseMaterial(faceBrush))
        {
            BackMaterial = new DiffuseMaterial(faceBrush),
        };

        // The reflection: the picture upside down beneath the floor line, fading out as it goes
        // down and gone halfway, so the edge of the view never cuts it off. The fade is an opacity
        // mask on a drawing, so it shows the theme's own background.
        var reflectionDrawing = new DrawingGroup
        {
            OpacityMask = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.5), new GradientStop(Color.FromArgb(110, 0, 0, 0), 1)],
                new Point(0, 0), new Point(0, 1)),
        };
        if (thumb != null) reflectionDrawing.Children.Add(new ImageDrawing(thumb, new Rect(0, 0, w, h)));
        else reflectionDrawing.Children.Add(new GeometryDrawing(faceBrush, null, new RectangleGeometry(new Rect(0, 0, w, h))));
        var reflectionBrush = new DrawingBrush(reflectionDrawing) { Stretch = Stretch.Fill };
        reflectionBrush.Freeze();
        var reflection = new GeometryModel3D(Quad(w, -h, 0, flipV: true), new DiffuseMaterial(reflectionBrush))
        {
            BackMaterial = new DiffuseMaterial(reflectionBrush),
        };

        var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var translation = new TranslateTransform3D();
        var transforms = new Transform3DGroup();
        transforms.Children.Add(new RotateTransform3D(rotation));
        transforms.Children.Add(translation);
        var group = new Model3DGroup { Transform = transforms };
        group.Children.Add(face);
        group.Children.Add(reflection);
        _scene.Children.Add(group);
        _hits[face] = index;
        return new Card { Group = group, Face = face, Rotation = rotation, Translation = translation };
    }

    /// <summary>A quad from y0 to y1, centered on x. The reflection's texture runs upward from
    /// the floor so it mirrors the picture above it.</summary>
    private static MeshGeometry3D Quad(double w, double y0, double y1, bool flipV)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = [new Point3D(-w / 2, y0, 0), new Point3D(w / 2, y0, 0), new Point3D(w / 2, y1, 0), new Point3D(-w / 2, y1, 0)],
            TextureCoordinates = flipV
                ? [new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1)]
                : [new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0)],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Sets every model's turn and place from its distance to the position. Within one
    /// step of the front the values blend, so a picture swings smoothly into and out of it.</summary>
    private void Place()
    {
        foreach (var (index, card) in _cards)
        {
            double d = index - _position;
            double ad = Math.Abs(d), sign = Math.Sign(d);
            double t = Math.Min(1, ad);
            double x = sign * (t * CenterGap + Math.Max(0, ad - 1) * SideStep);
            double z = -t * SideDepth - Math.Max(0, ad - 1) * 0.02;
            card.Rotation.Angle = -sign * t * SideAngle;
            card.Translation.OffsetX = x;
            card.Translation.OffsetZ = z;
        }
        // Draw back to front, so nearer pictures cover farther ones.
        var order = _cards.OrderByDescending(kv => Math.Abs(kv.Key - _position)).Select(kv => kv.Value.Group).ToList();
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

    // ---- animation ----

    private void StartAnimation()
    {
        if (_animating || !IsLoaded) { if (!IsLoaded) { _position = SelectedIndex; Rebuild(); } return; }
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        if (now == _lastFrame) return;
        _lastFrame = now;
        double target = SelectedIndex;
        double gap = target - _position;
        // Ease out, quicker over long jumps so a far slide does not crawl.
        double speed = 9 + Math.Min(30, Math.Abs(gap) * 2);
        _position += gap * Math.Min(1, dt * speed);
        if (Math.Abs(target - _position) < 0.002)
        {
            _position = target;
            StopAnimation();
        }
        Rebuild();
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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        if (IndexAt(e.GetPosition(this)) is not int i) return;
        e.Handled = true;
        if (i == SelectedIndex) ItemActivated?.Invoke(_items[i]);
        else SelectedIndex = i;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Focus();
        if (IndexAt(e.GetPosition(this)) is not int i) return;
        e.Handled = true;
        SelectedIndex = i;
        ItemMenuRequested?.Invoke(_items[i], this);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_items.Count == 0) return;
        SelectedIndex += e.Delta > 0 ? -1 : 1;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Left: SelectedIndex--; break;
            case Key.Right: SelectedIndex++; break;
            case Key.PageUp: SelectedIndex -= 5; break;
            case Key.PageDown: SelectedIndex += 5; break;
            case Key.Home: SelectedIndex = 0; break;
            case Key.End: SelectedIndex = _items.Count - 1; break;
            case Key.Enter when SelectedItem != null: ItemActivated?.Invoke(SelectedItem); break;
            case Key.Apps when SelectedItem != null:
            case Key.System when e.SystemKey == Key.F10 && shift && SelectedItem != null:
                ItemMenuRequested?.Invoke(SelectedItem!, this);
                break;
            case Key.Delete when SelectedItem != null:
                DeleteRequested?.Invoke(SelectedItem);
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

    /// <summary>Formats a capture for its name and caption. Set by the library screen.</summary>
    public Func<CaptureItem, string>? Describe { get; set; }

    private void UpdateName()
    {
        string name = SelectedItem is { } item && Describe != null
            ? Describe(item) + ", " + (SelectedIndex + 1) + " / " + _items.Count
            : "";
        AutomationProperties.SetName(this, name);
        if (IsKeyboardFocused && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, "", name);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CoverFlowPeer(this);

    private sealed class CoverFlowPeer(CoverFlow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => nameof(CoverFlow);
        protected override bool IsKeyboardFocusableCore() => true;
    }
}
