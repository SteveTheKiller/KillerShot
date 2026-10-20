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
using CaptureMode = KillerShot.Capture.CaptureMode;

namespace KillerShot.Shell;

/// <summary>Region selection across every monitor. Each monitor gets its own borderless overlay
/// showing that monitor's slice of the frozen desktop at one image pixel per device pixel, and
/// all overlays share one selection in physical virtual-desktop coordinates, so a drag can cross
/// monitors with different scaling. Region mode supports dragging or a keyboard rectangle;
/// window-pick mode highlights windows and captures one with a click.</summary>
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
    private Win32.POINT? _keyboardPoint;
    private readonly bool _showBar;
    private CaptureBar? _bar;
    public CaptureMode Mode { get; private set; }
    public bool IsVideo { get; private set; }
    internal Win32.RECT? CurrentSelection => _selection;
    internal Task<Win32.RECT?> Result => _result.Task;

    /// <summary>The window chosen by a click, or zero when the result is a dragged region.</summary>
    public IntPtr PickedWindow { get; private set; }

    public RegionSelector(FrozenDesktop frozen, bool windowOnly = false, bool showBar = false, CaptureMode? mode = null, bool video = false)
    {
        _frozen = frozen;
        Mode = mode ?? (windowOnly ? CaptureMode.Window : CaptureMode.Region);
        IsVideo = video;
        _showBar = showBar;
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
            if (_showBar)
            {
                _bar = new CaptureBar(this, _frozen.Monitors.FirstOrDefault(m => m.Primary) ?? _frozen.Monitors[0]) { Owner = home };
                _bar.Show();
                _bar.FocusFirstControl();
            }
            Move();   // Window mode highlights the target under the pointer.
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
        BeginAt(p);
    }

    internal void BeginAt(Win32.POINT p)
    {
        _keyboardPoint = null;
        _start = p;
        _dragged = false;
    }

    public void Move()
    {
        Win32.GetCursorPos(out var p);
        MoveAt(p);
    }

    internal void MoveAt(Win32.POINT p)
    {
        if (_keyboardPoint != null) return;
        if (_start == null && Mode is CaptureMode.Region or CaptureMode.Qr)
        {
            _hover = null;
            if (_selection != null)
            {
                _selection = null;
                RedrawAll();
            }
            return;
        }
        if (_start is { } s && Mode is CaptureMode.Region or CaptureMode.Qr)
        {
            if (!_dragged && Math.Abs(p.X - s.X) < 4 && Math.Abs(p.Y - s.Y) < 4) return;
            _dragged = true;
            _selection = new Win32.RECT(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y), Math.Max(s.X, p.X) + 1, Math.Max(s.Y, p.Y) + 1);
        }
        else
        {
            // Over the bare desktop the whole monitor is the target.
            var hover = Mode switch
            {
                CaptureMode.Monitor => new WindowBounds(IntPtr.Zero, _frozen.MonitorAt(p).Bounds),
                CaptureMode.AllMonitors => new WindowBounds(IntPtr.Zero, _frozen.Bounds),
                _ => _frozen.WindowAt(p) ?? new WindowBounds(IntPtr.Zero, _frozen.MonitorAt(p).Bounds)
            };
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
        // In window-pick mode, a click takes the highlighted window.
        if (Mode is not (CaptureMode.Region or CaptureMode.Qr) && _hover is { } w)
        {
            PickedWindow = w.Handle;   // zero for a whole monitor
            Finish(w.Bounds);
            return;
        }
        _selection = null;
        RedrawAll();
    }

    public void Cancel() => Finish(null);

    public void SetMode(CaptureMode mode)
    {
        Mode = IsVideo && mode == CaptureMode.Qr ? CaptureMode.Region : mode;
        _keyboardPoint = null;
        _start = null; _selection = null; _hover = null;
        RedrawAll();
        Move();
        _bar?.Sync();
    }

    public void SetVideo(bool video)
    {
        IsVideo = video;
        if (video && Mode == CaptureMode.Qr) SetMode(CaptureMode.Region);
    }

    public void CaptureSelection()
    {
        if (_selection is not { Width: >= 3, Height: >= 3 } selection)
        {
            if (Mode is CaptureMode.Region or CaptureMode.Qr) HandleKey(Key.F2, ModifierKeys.None);
            return;
        }
        if (Mode == CaptureMode.Window) PickedWindow = _hover?.Handle ?? IntPtr.Zero;
        Finish(selection);
    }

    public bool HandleKey(Key key, ModifierKeys modifiers, Win32.RECT? monitor = null)
    {
        if (key == Key.Escape) { Cancel(); return true; }
        if (Mode is not (CaptureMode.Region or CaptureMode.Qr)) return false;
        if (key == Key.F2)
        {
            var home = monitor ?? _frozen.Monitors.FirstOrDefault(m => m.Primary)?.Bounds ?? _frozen.Bounds;
            _keyboardPoint ??= new Win32.POINT { X = home.Left + home.Width / 2, Y = home.Top + home.Height / 2 };
            _start = _keyboardPoint;
            _selection = new Win32.RECT(_start.Value.X, _start.Value.Y, _start.Value.X + 1, _start.Value.Y + 1);
            _dragged = false;
            var overlay = _overlays.FirstOrDefault(o => o.Monitor.Contains(_keyboardPoint.Value));
            overlay?.Activate();
            if (overlay != null) Keyboard.Focus(overlay);
            RedrawAll();
            return true;
        }
        if (_keyboardPoint is not { } point || _start is not { } anchor) return false;
        if (key == Key.Enter) { CaptureSelection(); return true; }
        int step = (modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (key)
        {
            case Key.Left: point.X = Math.Max(_frozen.Bounds.Left, point.X - step); break;
            case Key.Right: point.X = Math.Min(_frozen.Bounds.Right - 1, point.X + step); break;
            case Key.Up: point.Y = Math.Max(_frozen.Bounds.Top, point.Y - step); break;
            case Key.Down: point.Y = Math.Min(_frozen.Bounds.Bottom - 1, point.Y + step); break;
            default: return false;
        }
        _keyboardPoint = point;
        _selection = new Win32.RECT(Math.Min(anchor.X, point.X), Math.Min(anchor.Y, point.Y),
            Math.Max(anchor.X, point.X) + 1, Math.Max(anchor.Y, point.Y) + 1);
        RedrawAll();
        return true;
    }

    private void RedrawAll()
    {
        foreach (var o in _overlays) o.Redraw(_selection);
    }

    private void Finish(Win32.RECT? rect)
    {
        if (_finished) return;
        _finished = true;
        _bar?.Close();
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
    private static Brush Accent => Application.Current.TryFindResource("SelectionAccent") as Brush ?? SystemColors.HighlightBrush;
    private static readonly Brush LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)));

    private readonly RegionSelector _owner;
    private readonly Path _shade;
    private readonly Rectangle _box;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly TextBlock _hint;

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
        _hint = new TextBlock
        {
            Text = Services.LocaleManager.Loc("Str_Capture_Keyboard"), Foreground = Brushes.White,
            Background = LabelBack, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 600, IsHitTestVisible = false
        };
        root.Children.Add(_hint);
        root.Children.Add(new Border { BorderBrush = Accent, BorderThickness = new Thickness(3), IsHitTestVisible = false });
        Content = root;

        SourceInitialized += (_, _) => PlaceOnMonitor();
        // A move onto a monitor with different scaling makes WPF apply a DPI-scaled size, so
        // place the window again once it lives on its own monitor.
        Loaded += (_, _) => { PlaceOnMonitor(); Redraw(_owner.CurrentSelection); };
        SizeChanged += (_, _) => Redraw(_owner.CurrentSelection);
        Closed += (_, _) => _owner.Cancel();

        MouseLeftButtonDown += (_, _) => { CaptureMouse(); _owner.Begin(); };
        MouseMove += (_, _) => _owner.Move();
        MouseLeftButtonUp += (_, _) => { ReleaseMouseCapture(); _owner.End(); };
        MouseRightButtonUp += (_, _) => _owner.Cancel();
        PreviewKeyDown += (_, e) => e.Handled = _owner.HandleKey(e.Key, Keyboard.Modifiers, Monitor);
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
        _hint.Visibility = _owner.Mode is CaptureMode.Region or CaptureMode.Qr ? Visibility.Visible : Visibility.Collapsed;
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
