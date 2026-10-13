using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Video;

/// <summary>
/// What the user sees while recording: an accent frame just outside the recorded rectangle and a
/// small control bar with the time, Pause, Stop and Discard. Both windows are excluded from screen
/// capture, so neither can end up in the video even when they overlap it.
/// </summary>
internal sealed class RecordingChrome
{
    private const int FramePx = 3;
    private readonly Win32.RECT _rect;
    private readonly Window _frame;
    private readonly Window _bar;
    private readonly TextBlock _time;
    private readonly Ellipse _dot;
    private readonly Button _pause, _stop, _discard;

    public event Action? PauseClicked;
    public event Action? StopClicked;
    public event Action? DiscardClicked;

    private static string Loc(string key) => LocaleManager.Loc(key);

    public RecordingChrome(Win32.RECT rect)
    {
        _rect = rect;

        _frame = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Left = -32000, Top = -32000, Width = 10, Height = 10, Title = "KillerShot",
        };
        var border = new Border { BorderThickness = new Thickness(FramePx) };
        border.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
        _frame.Content = border;
        _frame.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_frame).Handle;
            // Click-through, never activated, not in Alt+Tab.
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            Win32.SetWindowDisplayAffinity(h, Win32.WDA_EXCLUDEFROMCAPTURE);
            Win32.SetWindowPos(h, Win32.HWND_TOPMOST, rect.Left - FramePx, rect.Top - FramePx,
                rect.Width + FramePx * 2, rect.Height + FramePx * 2, Win32.SWP_NOACTIVATE);
        };

        _dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _time = new TextBlock { Text = "0:00", FontSize = 14, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "BrandSansFont");
        _pause = BarButton(Loc("Str_Rec_Pause"), () => PauseClicked?.Invoke());
        _stop = BarButton(Loc("Str_Rec_Stop"), () => StopClicked?.Invoke());
        _discard = BarButton(Loc("Str_Rec_Cancel"), () => DiscardClicked?.Invoke());

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 8, 6) };
        row.Children.Add(_dot);
        row.Children.Add(_time);
        row.Children.Add(_pause);
        row.Children.Add(_stop);
        row.Children.Add(_discard);
        var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "OverlayWindowBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");

        _bar = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, Left = -32000, Top = -32000, Content = card, Title = "KillerShot",
        };
        _bar.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        _bar.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_bar).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            Win32.SetWindowDisplayAffinity(h, Win32.WDA_EXCLUDEFROMCAPTURE);
        };
        _bar.Loaded += (_, _) => PlaceBar();
    }

    private Button BarButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
        b.SetResourceReference(FrameworkElement.StyleProperty, "OutlineButton");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Below the rectangle when there is room on its monitor, above it otherwise, and
    /// inside its bottom edge for a full-screen recording.</summary>
    private void PlaceBar()
    {
        var h = new WindowInteropHelper(_bar).Handle;
        Win32.GetWindowRect(h, out var wr);
        int bw = wr.Width, bh = wr.Height;
        var mon = Capture.FrozenDesktop.EnumerateMonitors()
            .FirstOrDefault(m => m.Bounds.Contains(new Win32.POINT { X = _rect.Left + _rect.Width / 2, Y = _rect.Top + _rect.Height / 2 }))?.Bounds
            ?? _rect;
        int x = Math.Clamp(_rect.Left + (_rect.Width - bw) / 2, mon.Left, Math.Max(mon.Left, mon.Right - bw));
        int y = _rect.Bottom + FramePx + 8;
        if (y + bh > mon.Bottom) y = _rect.Top - FramePx - 8 - bh;
        if (y < mon.Top) y = mon.Bottom - bh - 24;
        Win32.SetWindowPos(h, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    public void Show()
    {
        _frame.Show();
        _bar.Show();
    }

    public void Close()
    {
        try { _frame.Close(); } catch { }
        try { _bar.Close(); } catch { }
    }

    public void SetCountdown(int seconds)
    {
        _time.Text = string.Format(Loc("Str_Rec_Starting"), seconds);
        _pause.IsEnabled = false;
        _stop.IsEnabled = true;
    }

    public void SetRecording(TimeSpan elapsed, bool paused)
    {
        _pause.IsEnabled = true;
        _pause.Content = Loc(paused ? "Str_Rec_Resume" : "Str_Rec_Pause");
        _time.Text = paused ? Loc("Str_Rec_Paused") + "  " + Format(elapsed) : Format(elapsed);
        _dot.Opacity = paused ? 0.35 : (DateTime.Now.Millisecond < 500 ? 1 : 0.55);
    }

    public void SetSaving()
    {
        _time.Text = Loc("Str_Rec_Saving");
        _pause.IsEnabled = _stop.IsEnabled = _discard.IsEnabled = false;
        _frame.Hide();
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
}
