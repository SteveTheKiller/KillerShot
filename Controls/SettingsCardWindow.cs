using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>
/// Capture Settings and Video Settings each open in one of these: a modeless window, so the main
/// window stays usable while it is open, free to move past the main window's edge. The card itself
/// (chrome and controls) lives in MainWindow.xaml, where its handlers are; MainWindow lends it to
/// this window while it is open and takes it back when it closes, as it does for the album manager.
/// </summary>
public sealed class SettingsCardWindow : Window
{
    private readonly string _titleKey;

    public SettingsCardWindow(Window owner, FrameworkElement card, string titleKey)
    {
        _titleKey = titleKey;
        // A self-test may lay the card out under a main window that was never shown; WPF only
        // accepts an owner that has a window handle.
        if (PresentationSource.FromVisual(owner) != null) Owner = owner;
        Relocalize();
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

    /// <summary>Puts the window title in the current language.</summary>
    public void Relocalize() => Title = "KillerShot - " + LocaleManager.Loc(_titleKey);

    /// <summary>Takes the card out again so MainWindow can put it back where it lives.</summary>
    public void Release() => Content = null;

    /// <summary>Moves the window by its top band, like a title bar.</summary>
    public void DragFrom(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
