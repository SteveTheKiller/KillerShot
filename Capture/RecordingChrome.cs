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
    private readonly Window _frame;
    private readonly Window _bar;
    private readonly TextBlock _time;
    private readonly Ellipse _dot;
    private readonly Button _pause, _stop, _discard;
    private bool? _announcedPause;

    public event Action? PauseClicked;
    public event Action? StopClicked;
    public event Action? DiscardClicked;

    private static string Loc(string key) => LocaleManager.Loc(key);

    public RecordingChrome(Win32.RECT rect)
    {
        _frame = CaptureChromeWindows.Frame(rect);

        _dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _time = new TextBlock { Text = "0:00", FontSize = 14, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "BrandSansFont");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_time, System.Windows.Automation.AutomationLiveSetting.Polite);
        _pause = BarButton(Loc("Str_Rec_Pause"), () => PauseClicked?.Invoke());
        _stop = BarButton(Loc("Str_Rec_Stop"), () => StopClicked?.Invoke());
        _discard = BarButton(Loc("Str_Rec_Cancel"), () => DiscardClicked?.Invoke());
        _pause.ToolTip = Loc("Str_Rec_Pause") + " (Ctrl+Shift+R)";
        _stop.ToolTip = Loc("Str_Rec_Stop") + " (Ctrl+R)";
        _discard.ToolTip = Loc("Str_Rec_Cancel") + " (Ctrl+Shift+D)";

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 8, 6) };
        row.Children.Add(_dot);
        row.Children.Add(_time);
        row.Children.Add(_pause);
        row.Children.Add(_stop);
        row.Children.Add(_discard);
        _bar = CaptureChromeWindows.Bar(row, rect);
    }

    private static Button BarButton(string text, Action click) => CaptureChromeWindows.Button(text, click);

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
        _pause.ToolTip = _pause.Content + " (Ctrl+Shift+R)";
        _time.Text = paused ? Loc("Str_Rec_Paused") + "  " + Format(elapsed) : Format(elapsed);
        if (_announcedPause != paused)
        {
            _announcedPause = paused;
            AnnounceState();
        }
        _dot.Opacity = paused ? 0.35 : (DateTime.Now.Millisecond < 500 ? 1 : 0.55);
    }

    public void SetSaving()
    {
        _time.Text = Loc("Str_Rec_Saving");
        AnnounceState();
        _pause.IsEnabled = _stop.IsEnabled = _discard.IsEnabled = false;
        _frame.Hide();
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void AnnounceState()
    {
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_time)
            ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(_time);
        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
