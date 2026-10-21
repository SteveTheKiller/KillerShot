using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShot.Native;

namespace KillerShot.Video;

/// <summary>The windows shown around a live capture: an accent frame just outside the captured
/// rectangle and a small control card beside it. Both are excluded from screen capture, so
/// neither appears in what is captured, even where they overlap it.</summary>
internal static class CaptureChromeWindows
{
    public const int FramePx = 3;

    /// <summary>A click-through accent frame around <paramref name="rect"/>, never activated.</summary>
    public static Window Frame(Win32.RECT rect)
    {
        var frame = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Left = -32000, Top = -32000, Width = 10, Height = 10, Title = "KillerShot",
        };
        var border = new Border { BorderThickness = new Thickness(FramePx) };
        border.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
        frame.Content = border;
        frame.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(frame).Handle;
            // Click-through, never activated, not in Alt+Tab.
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            Win32.SetWindowDisplayAffinity(h, Win32.WDA_EXCLUDEFROMCAPTURE);
            Win32.SetWindowPos(h, Win32.HWND_TOPMOST, rect.Left - FramePx, rect.Top - FramePx,
                rect.Width + FramePx * 2, rect.Height + FramePx * 2, Win32.SWP_NOACTIVATE);
        };
        return frame;
    }

    /// <summary>A themed card window for the controls, placed next to <paramref name="rect"/>
    /// once it has its size. It never takes focus from the window being captured.</summary>
    public static Window Bar(UIElement content, Win32.RECT rect)
    {
        var card = new Border { BorderThickness = new Thickness(1), Child = content };
        card.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
        card.SetResourceReference(Border.BackgroundProperty, "OverlayWindowBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "OverlayEdgeBrush");
        var bar = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, Left = -32000, Top = -32000, Content = card, Title = "KillerShot",
        };
        bar.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        bar.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(bar).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            Win32.SetWindowDisplayAffinity(h, Win32.WDA_EXCLUDEFROMCAPTURE);
        };
        bar.Loaded += (_, _) => Place(bar, rect);
        return bar;
    }

    public static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
        b.SetResourceReference(FrameworkElement.StyleProperty, "OutlineButton");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Below the rectangle when there is room on its monitor, above it otherwise, and
    /// inside its bottom edge for a full-screen rectangle.</summary>
    public static void Place(Window bar, Win32.RECT rect)
    {
        var h = new WindowInteropHelper(bar).Handle;
        Win32.GetWindowRect(h, out var wr);
        int bw = wr.Width, bh = wr.Height;
        var mon = Capture.FrozenDesktop.EnumerateMonitors()
            .FirstOrDefault(m => m.Bounds.Contains(new Win32.POINT { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 }))?.Bounds
            ?? rect;
        int x = Math.Clamp(rect.Left + (rect.Width - bw) / 2, mon.Left, Math.Max(mon.Left, mon.Right - bw));
        int y = rect.Bottom + FramePx + 8;
        if (y + bh > mon.Bottom) y = rect.Top - FramePx - 8 - bh;
        if (y < mon.Top) y = mon.Bottom - bh - 24;
        Win32.SetWindowPos(h, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
}
