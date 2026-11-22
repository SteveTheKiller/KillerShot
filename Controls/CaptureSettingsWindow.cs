using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>
/// Capture Settings as its own window: modeless, so the main window stays usable while it is
/// open, and free to move past the main window's edge. The card itself (chrome, recorders and
/// options) lives in MainWindow.xaml, where its handlers are; MainWindow lends it to this window
/// while it is open and takes it back when it closes, as it does for the album manager.
/// </summary>
public sealed class CaptureSettingsWindow : Window
{
    public CaptureSettingsWindow(Window owner, FrameworkElement card)
    {
        // A self-test may lay the card out under a main window that was never shown; WPF only
        // accepts an owner that has a window handle.
        if (PresentationSource.FromVisual(owner) != null) Owner = owner;
        Title = "KillerShot - " + LocaleManager.Loc("Str_CS_Title");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TaskbarIdentity.Track(this);
        Content = card;
        Loaded += (_, _) => Anim.FadeIn(card);
        Loaded += (_, _) => KillerFamily.Windowing.WorkAreaFit.Fit(this, keepMargin: true);
    }

    /// <summary>Takes the card out again so MainWindow can put it back where it lives.</summary>
    public void Release() => Content = null;

    /// <summary>Moves the window by its top band, like a title bar.</summary>
    public void DragFrom(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
