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
    private const double MaxPictureWidth = 1.5;     // the widest a picture is drawn, in model units
    private const double MaxPictureHeight = 1.0;
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
            UpdateCamera(RenderSize);
        }
    }
    private double _viewCenterY = 0.50;
    /// <summary>Model units visible top to bottom at the front picture. Smaller draws the
    /// pictures larger; the collection strip, which is short, uses a smaller value.</summary>
    public double ViewHeight { get; set; } = 1.60;
    /// <summary>Enables Ctrl+wheel depth changes for album carousels.</summary>
    public bool PerspectiveZoomEnabled { get; set; }
    /// <summary>Responsive framing for the album's primary photo, independent of compact strips.</summary>
    public bool MainView { get; set; }
    private double _visibleWidth = MinViewWidth;
    public bool ShowInfoCards
    {
        get => _showInfoCards;
        set { if (_showInfoCards == value) return; _showInfoCards = value; RefreshCards(); }
    }
    private bool _showInfoCards = ViewInfoCards.Get(false);
    private double _perspectiveZoom;
    private const double MinViewWidth = 4.6;      // model units always visible across at the front picture

    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _root = new();
    private readonly Model3DGroup _scene = new();
    private readonly Dictionary<int, Card> _cards = [];
    private readonly Dictionary<GeometryModel3D, int> _hits = [];
    private IReadOnlyList<CaptureItem> _items = [];
    private readonly CarouselMotion _motion;
    private double _position => _motion.Position;
    private int _orderBucket = int.MinValue;
    public bool IsMoving => _motion.IsMoving;

    private sealed class Card
    {
        public required Model3DGroup Group { get; init; }
        public required GeometryModel3D Face { get; init; }
        public required AxisAngleRotation3D Rotation { get; init; }
        public required TranslateTransform3D Translation { get; init; }
    }

    /// <summary>Gives a capture's thumbnail; null draws a blank card.</summary>
    public Func<CaptureItem, BitmapSource?>? ThumbnailSource { get; set; }
    public bool DeferHiddenLoading { get; set; }
    public Func<CaptureItem, BitmapSource?>? SelectedPreviewSource { get; set; }
    private readonly SemaphoreSlim _previewSlot = new(1);
    private CancellationTokenSource? _previewRequest;
    private CaptureItem? _previewItem;
    private BitmapSource? _previewBitmap;

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
            _motion.Reset(SelectedIndex);
            Rebuild();
            QueueSelectedPreview();
            SelectionChanged?.Invoke();
            UpdateName();
        }
    }

    public CoverFlow()
    {
        _motion = new CarouselMotion(this, Rebuild, false,
            () => ActualWidth * SideStep / Math.Max(.1, _visibleWidth),
            () => Math.Max(0, _items.Count - 1), index => SetCurrentValue(SelectedIndexProperty, index));
        ImageDrag.Attach(this, point =>
        {
            if (IndexAt(point) is not int index || index >= _items.Count) return null;
            var item = _items[index];
            return () => CaptureLibrary.LoadImage(item);
        }, verticalNavigation: false);
        Focusable = true;
        FocusVisualStyle = KillerFamily.Accessibility.AccessibleControls.FocusStyleFor(this);
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
        IsVisibleChanged += (_, _) => { Rebuild(); QueueSelectedPreview(); };
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += RefreshCards;
            CaptureLibrary.ThumbnailReady += OnThumbnailReady;
            CardColors.Changed += RefreshCards;
            ViewInfoCards.Changed += SyncInfoCards;
            SyncInfoCards();
            QueueSelectedPreview();
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= RefreshCards;
            CaptureLibrary.ThumbnailReady -= OnThumbnailReady;
            CardColors.Changed -= RefreshCards;
            ViewInfoCards.Changed -= SyncInfoCards;
            _previewRequest?.Cancel();
            _previewRequest = null;
            _previewItem = null;
            _previewBitmap = null;
        };
    }

    private void SyncInfoCards() => ShowInfoCards = ViewInfoCards.Get(false);

    private void OnThumbnailReady(CaptureItem item) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (IsLoaded && IsVisible && _items.Contains(item)) RefreshCards();
    }));

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
        flow._motion.MoveTo(flow.SelectedIndex);
        flow.QueueSelectedPreview();
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
        UpdateCamera(finalSize);
        return finalSize;
    }

    private void UpdateCamera(Size size)
    {
        // WPF's field of view is horizontal, so it is worked out from the shape of the control:
        // in a wide, short control the front picture and its reflection fill the height and the
        // extra width shows more pictures to the sides; in a taller one the view widens until
        // the front picture and two side pictures each way fit across.
        if (_viewport.Camera is not PerspectiveCamera camera) return;
        var (width, pictureHeight) = PictureSize(SelectedItem);
        double center = MainView ? pictureHeight * .43 : _viewCenterY;
        double aim = center + ((MainView ? pictureHeight * .5 : .5) - center) * Math.Clamp(_perspectiveZoom / 1.5, 0, 1);
        camera.Position = new Point3D(0, aim, CameraDistance);
        if (size.Width > 0 && size.Height > 0)
        {
            double aspect = size.Width / size.Height;
            double height = Math.Max(ViewHeight, MinViewWidth / aspect) * (1 + .12 * _perspectiveZoom);
            if (MainView)
            {
                double projection = CameraDistance / (CameraDistance - .9 * _perspectiveZoom);
                height = Math.Max(pictureHeight * 1.28, width / .62 / aspect) * (1 + .12 * _perspectiveZoom);
                height = Math.Max(height, Math.Max(pictureHeight * 1.15, width / .90 / aspect) * projection);
            }
            _visibleWidth = height * aspect;
            double halfV = height / 2 / CameraDistance;
            camera.FieldOfView = Math.Min(150, 2 * Math.Atan(aspect * halfV) * 180 / Math.PI);
        }
    }

    // ---- models ----

    private void RefreshCards()
    {
        ClearCards();
        Rebuild();
    }

    private async void QueueSelectedPreview()
    {
        _previewRequest?.Cancel();
        _previewRequest = null;
        if (!MainView || !IsVisible || SelectedPreviewSource is not { } load || SelectedItem is not { } item)
        {
            _previewItem = null;
            _previewBitmap = null;
            return;
        }
        if (_previewItem == item && _previewBitmap != null) return;
        _previewItem = null;
        _previewBitmap = null;
        using var request = new CancellationTokenSource();
        _previewRequest = request;
        try
        {
            await Task.Delay(150, request.Token);
            await _previewSlot.WaitAsync(request.Token);
            try
            {
                request.Token.ThrowIfCancellationRequested();
                var bitmap = await Task.Run(() => load(item));
                if (request.IsCancellationRequested || !IsLoaded || SelectedItem != item || !CaptureLibrary.IsCurrent(item)) return;
                _previewItem = item;
                _previewBitmap = bitmap;
                RefreshCards();
            }
            finally { _previewSlot.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The thumbnail remains available when the larger preview cannot load. */ }
        finally { if (ReferenceEquals(_previewRequest, request)) _previewRequest = null; }
    }

    private void ClearCards()
    {
        _orderBucket = int.MinValue;
        foreach (var card in _cards.Values) _scene.Children.Remove(card.Group);
        _cards.Clear();
        _hits.Clear();
    }

    /// <summary>Creates the models near the position, drops the rest, and places them all.</summary>
    private void Rebuild()
    {
        if (DeferHiddenLoading && !IsVisible) return;
        UpdateCamera(RenderSize);
        int center = (int)Math.Round(_position);
        int first = Math.Max(0, center - Window), last = Math.Min(_items.Count - 1, center + Window);
        foreach (int stale in _cards.Keys.Where(k => k < first || k > last).ToList())
        {
            _orderBucket = int.MinValue;
            _scene.Children.Remove(_cards[stale].Group);
            _hits.Remove(_cards[stale].Face);
            _cards.Remove(stale);
        }
        for (int i = first; i <= last; i++)
            if (!_cards.ContainsKey(i)) { _cards[i] = CreateCard(i); _orderBucket = int.MinValue; }
        Place();
    }

    private Card CreateCard(int index)
    {
        var item = _items[index];
        BitmapSource? thumb = _previewItem == item && _previewBitmap != null ? _previewBitmap : ThumbnailSource?.Invoke(item);
        double aspect = item.Height > 0 ? (double)item.Width / item.Height : 1.6;
        double w = Math.Min(MaxPictureWidth, MaxPictureHeight * aspect);
        double h = w / aspect;

        Brush faceBrush = thumb != null
            ? new ImageBrush(thumb) { Stretch = Stretch.Fill }
            : new SolidColorBrush(Color.FromRgb(60, 60, 60));
        var cardDrawing = QrMark.Drawing(faceBrush, w, h, item.HasQr);
        if (ShowInfoCards)
        {
            const double inset = .035, captionBand = .22;
            w = Math.Max(.5, w);
            h = Math.Min(MaxPictureHeight, h + captionBand + inset * 2);
            var stock = CardColors.StripCard();
            var drawing = new DrawingGroup();
            double corner = ThemeManager.Current == Theme.SE98 ? 0 : .03;
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(stock), null,
                new RectangleGeometry(new Rect(0, 0, w, h), corner, corner)));
            double pictureWidth = Math.Min(w - inset * 2, (h - captionBand - inset) * aspect);
            double pictureHeight = pictureWidth / aspect;
            var picture = new Rect((w - pictureWidth) / 2, inset, pictureWidth, pictureHeight);
            drawing.Children.Add(new GeometryDrawing(new DrawingBrush(cardDrawing), null, new RectangleGeometry(picture)));
            var ink = CardColors.IsLight(stock) ? Brushes.Black : Brushes.White;
            string caption = Describe?.Invoke(item) ?? item.Window ?? item.Mode;
            var text = new FormattedText(caption, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), .060, ink, 1)
            {
                MaxTextWidth = w - inset * 2, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis,
            };
            drawing.Children.Add(new GeometryDrawing(ink, null, text.BuildGeometry(new Point(inset, h - captionBand + .025))));
            if (CardColors.Collection(item.CollectionId) is Color accent)
            {
                double border = ActualWidth > 0 ? 2 * _visibleWidth / ActualWidth * (CameraDistance - .9 * _perspectiveZoom) / CameraDistance : .012;
                PhotoCardBorder.DrawFrame(drawing, new Rect(0, 0, w, h), corner, accent, border);
            }
            cardDrawing = drawing;
        }
        faceBrush = new DrawingBrush(cardDrawing) { Stretch = Stretch.Fill };
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
        reflectionDrawing.Children.Add(cardDrawing);
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

    private (double Width, double Height) PictureSize(CaptureItem? item)
    {
        double aspect = item is { Width: > 0, Height: > 0 } ? (double)item.Width / item.Height : 1.6;
        double width = Math.Min(MaxPictureWidth, MaxPictureHeight * aspect);
        double height = width / aspect;
        return ShowInfoCards ? (Math.Max(.5, width), Math.Min(MaxPictureHeight, height + .29)) : (width, height);
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
            // Widen the lens while bringing the front forward and sending the sides back.
            // The blend follows motion between cards and is exactly the original depth at zero.
            z += _perspectiveZoom * (.9 * (1 - t) - 1.2 * t);
            card.Rotation.Angle = -sign * t * SideAngle;
            card.Translation.OffsetX = x;
            card.Translation.OffsetZ = z;
        }
        // Draw back to front, so nearer pictures cover farther ones.
        // Relative depths only swap when the position crosses a half-card boundary.
        int bucket = (int)Math.Floor(_position * 2);
        if (_orderBucket == bucket) return;
        _orderBucket = bucket;
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
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _motion.Drag(e); UpdateHover(e.GetPosition(this)); }
    public void UpdateHover(Point position) => Cursor = IndexAt(position).HasValue ? DragCursors.Open : Cursors.Arrow;
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Cursor = Cursors.Arrow; }
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
        e.Handled = HandleWheel(e.Delta, Keyboard.Modifiers);
    }

    public bool HandleWheel(int delta, ModifierKeys modifiers)
    {
        if (_items.Count == 0) return false;
        if (PerspectiveZoomEnabled && (modifiers & ModifierKeys.Control) != 0)
        {
            _perspectiveZoom = Math.Clamp(_perspectiveZoom + delta / 1200.0, -.5, 1.5);
            UpdateCamera(RenderSize);
            Place();
            return true;
        }
        _motion.Interrupt();
        SelectedIndex += delta > 0 ? -1 : 1;
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key is Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Enter or Key.Apps or Key.Delete
            || shift && (e.Key == Key.F10 || e.Key == Key.System && e.SystemKey == Key.F10)) _motion.Interrupt();
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
            case Key.F10 when shift && SelectedItem != null:
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
