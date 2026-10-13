using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerShot.Capture;
using KillerShot.Native;
using Path = System.Windows.Shapes.Path;

namespace KillerShot.Shell;

/// <summary>Region selection across every monitor. Each monitor gets its own borderless overlay
/// showing that monitor's slice of the frozen desktop at one image pixel per device pixel, and
/// all overlays share one selection in physical virtual-desktop coordinates, so a drag can cross
/// monitors with different scaling. Hovering highlights the window under the pointer, and a
/// click without a drag takes that window. In window-pick mode a click is the only gesture.</summary>
internal sealed class RegionSelector
{
    private readonly FrozenDesktop _frozen;
    private readonly List<RegionOverlay> _overlays = new();
    private readonly TaskCompletionSource<Win32.RECT?> _result = new();
    private Win32.POINT? _start;
    private Win32.RECT? _selection;
    private WindowBounds? _hover;
    private bool _dragged;
    private bool _finished;
    private readonly bool _windowOnly;

    /// <summary>The window chosen by a click, or zero when the result is a dragged region.</summary>
    public IntPtr PickedWindow { get; private set; }

    public RegionSelector(FrozenDesktop frozen, bool windowOnly = false)
    {
        _frozen = frozen;
        _windowOnly = windowOnly;
    }

    public Task<Win32.RECT?> RunAsync()
    {
        try
        {
            foreach (var mon in _frozen.Monitors)
            {
                var slice = _frozen.Crop(mon.Bounds);
                if (slice == null) continue;
                _overlays.Add(new RegionOverlay(this, mon.Bounds, slice));
            }
            if (_overlays.Count == 0) { Finish(null); return _result.Task; }

            Win32.GetCursorPos(out var cursor);
            RegionOverlay? home = null;
            foreach (var o in _overlays)
            {
                o.Show();
                if (o.Monitor.Contains(cursor)) home = o;
            }
            home ??= _overlays[0];
            home.Activate();
            home.Focus();
            Keyboard.Focus(home);
            Move();   // highlight the window already under the pointer
        }
        catch
        {
            // Never leave a topmost overlay covering the desktop.
            Finish(null);
            throw;
        }
        return _result.Task;
    }

    public void Begin()
    {
        Win32.GetCursorPos(out var p);
        _start = p;
        _dragged = false;
    }

    public void Move()
    {
        Win32.GetCursorPos(out var p);
        if (_start is { } s && !_windowOnly)
        {
            if (!_dragged && Math.Abs(p.X - s.X) < 4 && Math.Abs(p.Y - s.Y) < 4) return;
            _dragged = true;
            _selection = new Win32.RECT(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y), Math.Max(s.X, p.X) + 1, Math.Max(s.Y, p.Y) + 1);
        }
        else if (_start == null || _windowOnly)
        {
            // Over the bare desktop the whole monitor is the target.
            var hover = _frozen.WindowAt(p) ?? new WindowBounds(IntPtr.Zero, _frozen.MonitorAt(p).Bounds);
            if (hover == _hover && _selection != null) return;
            _hover = hover;
            _selection = hover?.Bounds;
        }
        RedrawAll();
    }

    public void End()
    {
        bool dragged = _dragged;
        _start = null;
        _dragged = false;
        if (dragged && _selection is { Width: >= 3, Height: >= 3 } sel) { Finish(sel); return; }
        // A click without a drag takes the highlighted window.
        if (_hover is { } w)
        {
            PickedWindow = w.Handle;   // zero for a whole monitor
            Finish(w.Bounds);
            return;
        }
        _selection = null;
        RedrawAll();
    }

    public void Cancel() => Finish(null);

    private void RedrawAll()
    {
        foreach (var o in _overlays) o.Redraw(_selection);
    }

    private void Finish(Win32.RECT? rect)
    {
        if (_finished) return;
        _finished = true;
        foreach (var o in _overlays)
        {
            try { o.Close(); } catch { }
        }
        _overlays.Clear();
        _result.TrySetResult(rect);
    }
}

internal sealed class RegionOverlay : Window
{
    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6)));
    private static readonly Brush LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)));

    private readonly RegionSelector _owner;
    private readonly Path _shade;
    private readonly Rectangle _box;
    private readonly Border _label;
    private readonly TextBlock _labelText;

    public Win32.RECT Monitor { get; }

    public RegionOverlay(RegionSelector owner, Win32.RECT monitor, BitmapSource slice)
    {
        _owner = owner;
        Monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "KillerShot";
        // Placed in physical pixels once the handle exists; start off screen so nothing flashes.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 64; Height = 64;

        var image = new Image { Source = slice, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        _shade = new Path { Fill = Shade, IsHitTestVisible = false };
        _box = new Rectangle { Stroke = Accent, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false, SnapsToDevicePixels = true };
        _labelText = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        _label = new Border { Background = LabelBack, Padding = new Thickness(6, 2, 6, 2), Child = _labelText, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(_shade);
        canvas.Children.Add(_box);
        canvas.Children.Add(_label);

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(image);
        root.Children.Add(canvas);
        Content = root;

        SourceInitialized += (_, _) => PlaceOnMonitor();
        // A move onto a monitor with different scaling makes WPF apply a DPI-scaled size, so
        // place the window again once it lives on its own monitor.
        Loaded += (_, _) => { PlaceOnMonitor(); Redraw(null); };
        SizeChanged += (_, _) => Redraw(null);

        MouseLeftButtonDown += (_, _) => { CaptureMouse(); _owner.Begin(); };
        MouseMove += (_, _) => _owner.Move();
        MouseLeftButtonUp += (_, _) => { ReleaseMouseCapture(); _owner.End(); };
        MouseRightButtonUp += (_, _) => _owner.Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _owner.Cancel(); } };
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height, Win32.SWP_NOACTIVATE);
    }

    /// <summary>Draws this monitor's part of the shared selection. Selection coordinates are
    /// physical; this window's layout is in device-independent units, so scale by the ratio of
    /// its actual size to the monitor's pixel size.</summary>
    public void Redraw(Win32.RECT? selection)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double sx = w / Monitor.Width, sy = h / Monitor.Height;
        var full = new RectangleGeometry(new Rect(0, 0, w, h));

        var local = selection is { } sel ? Win32.RECT.Intersect(sel, Monitor) : default;
        if (local.IsEmpty)
        {
            _shade.Data = full;
            _box.Visibility = Visibility.Collapsed;
        }
        else
        {
            var r = new Rect((local.Left - Monitor.Left) * sx, (local.Top - Monitor.Top) * sy, local.Width * sx, local.Height * sy);
            _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r));
            Canvas.SetLeft(_box, r.X);
            Canvas.SetTop(_box, r.Y);
            _box.Width = Math.Max(1, r.Width);
            _box.Height = Math.Max(1, r.Height);
            _box.Visibility = Visibility.Visible;
        }

        // The size label sits beside the selection corner on whichever monitor holds it.
        if (selection is { } s && Monitor.Contains(new Win32.POINT { X = s.Left, Y = s.Top }))
        {
            _labelText.Text = $"{s.Width} x {s.Height}";
            _label.Visibility = Visibility.Visible;
            double lx = (s.Left - Monitor.Left) * sx, ly = (s.Top - Monitor.Top) * sy - 22;
            Canvas.SetLeft(_label, lx);
            Canvas.SetTop(_label, ly < 0 ? (s.Top - Monitor.Top) * sy + 4 : ly);
        }
        else
        {
            _label.Visibility = Visibility.Collapsed;
        }
    }
}
