using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>A scrolling capture in progress: the region is framed, and while the user scrolls it
/// with the wheel, scroll bar or keys, it is grabbed a few times a second and stitched. Enter or
/// Done finishes, Esc or Cancel throws it away. Nothing is sent to the scrolled program; the frame
/// and the card are hidden from the grabs.</summary>
internal sealed class ScrollCaptureSession
{
    private const int HotkeyDone = 0x5C11, HotkeyCancel = 0x5C12;
    private readonly Win32.RECT _rect;
    private readonly TaskCompletionSource<BitmapSource?> _result = new();
    private ScrollStitcher? _stitcher;
    private ScreenGrabber? _grabber;
    private byte[]? _buffer;
    private DispatcherTimer? _timer;
    private Window? _frame, _bar;
    private TextBlock? _status;
    private HwndSource? _keys;

    private static string Loc(string key) => LocaleManager.Loc(key);

    public ScrollCaptureSession(Win32.RECT rect) => _rect = rect;

    /// <summary>The stitched picture, or null when cancelled.</summary>
    public Task<BitmapSource?> RunAsync()
    {
        try
        {
            _grabber = new ScreenGrabber(_rect, includeCursor: false);
            _buffer = new byte[_rect.Width * _rect.Height * 4];
            _stitcher = new ScrollStitcher(_rect.Width, _rect.Height, ImageOutput.MaxPixels);
            ShowChrome();
            RegisterKeys();
            Tick();   // the first picture before anything moves
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(110), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
            _timer.Start();
        }
        catch (Exception error)
        {
            // Reported, not taken for a cancel: the result is set before Finish completes it.
            _result.TrySetException(error);
            Finish(keep: false);
        }
        return _result.Task;
    }

    private void Tick()
    {
        if (_grabber == null || _stitcher == null || _buffer == null) return;
        if (!_grabber.Grab(_buffer)) return;
        _stitcher.Add(_buffer);
        if (_stitcher.Full) { SetStatus(Loc("Str_Scroll_Full")); Finish(keep: true); return; }
        SetStatus(_stitcher.LostTrack ? Loc("Str_Scroll_Lost")
            : !_stitcher.Moved ? Loc("Str_Scroll_Start")
            : string.Format(CultureInfo.CurrentCulture, Loc("Str_Scroll_Progress"), _stitcher.Added));
    }

    private void SetStatus(string text)
    {
        if (_status == null || _status.Text == text) return;
        _status.Text = text;
    }

    private void ShowChrome()
    {
        _status = new TextBlock { MinWidth = 220, MaxWidth = 380, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var done = CaptureChromeWindows.Button(Loc("Str_Scroll_Done"), () => Finish(keep: true));
        done.ToolTip = Loc("Str_Scroll_Done") + " (Enter)";
        var cancel = CaptureChromeWindows.Button(Loc("Str_Btn_Cancel"), () => Finish(keep: false));
        cancel.ToolTip = Loc("Str_Btn_Cancel") + " (Esc)";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 8, 6) };
        row.Children.Add(_status);
        row.Children.Add(done);
        row.Children.Add(cancel);
        _frame = CaptureChromeWindows.Frame(_rect);
        _bar = CaptureChromeWindows.Bar(row, _rect);
        _frame.Show();
        _bar.Show();
    }

    /// <summary>Enter and Esc work while another program has the keyboard, only for this session.</summary>
    private void RegisterKeys()
    {
        _keys = new HwndSource(new HwndSourceParameters("KillerShot scrolling capture") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _keys.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Win32.WM_HOTKEY && wParam.ToInt32() is HotkeyDone or HotkeyCancel)
            {
                Finish(keep: wParam.ToInt32() == HotkeyDone);
                handled = true;
            }
            return IntPtr.Zero;
        });
        Win32.RegisterHotKey(_keys.Handle, HotkeyDone, Win32.MOD_NOREPEAT, 0x0D);
        Win32.RegisterHotKey(_keys.Handle, HotkeyCancel, Win32.MOD_NOREPEAT, 0x1B);
    }

    /// <summary>For the self-test: Done or Cancel without the keyboard.</summary>
    internal void TestFinish(bool keep) => Finish(keep);

    private void Finish(bool keep)
    {
        if (_result.Task.IsCompleted) return;
        _timer?.Stop();
        if (_keys != null)
        {
            Win32.UnregisterHotKey(_keys.Handle, HotkeyDone);
            Win32.UnregisterHotKey(_keys.Handle, HotkeyCancel);
            _keys.Dispose();
            _keys = null;
        }
        try { _frame?.Close(); } catch (InvalidOperationException) { }
        try { _bar?.Close(); } catch (InvalidOperationException) { }
        BitmapSource? result = null;
        try { if (keep && _stitcher != null) result = _stitcher.Result(); }
        catch (InvalidOperationException) { }
        catch (OutOfMemoryException error) { _result.TrySetException(error); }
        _grabber?.Dispose();
        _grabber = null;
        _result.TrySetResult(result);
    }
}
