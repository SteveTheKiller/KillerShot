using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerShot.Controls;

// Capture Settings and Video Settings open in their own modeless windows (SettingsCardWindow):
// the main window stays usable while one is open, and the window moves anywhere on the desktop.
// Each card lives in MainWindow.xaml inside a collapsed home grid that holds its styles, and is
// lent to its window while the window is open.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        /// <summary>One settings card and the window it is lent to while open.</summary>
        private sealed class LentCard(string titleKey, Panel home, FrameworkElement card, ScrollViewer scroll, Control button)
        {
            public string TitleKey { get; } = titleKey;
            public Panel Home { get; } = home;
            public FrameworkElement Card { get; } = card;
            public ScrollViewer Scroll { get; } = scroll;
            public Control Button { get; } = button;
            public SettingsCardWindow? Window { get; set; }
        }

        private LentCard? _captureCard, _videoCard;
        private LentCard CaptureCard => _captureCard ??= new("Str_CS_Title", CaptureSettingsHome, CaptureSettingsCard, CaptureSettingsScroll, CaptureSettingsButton);
        private LentCard VideoCard => _videoCard ??= new("Str_VS_Title", VideoSettingsHome, VideoSettingsCard, VideoSettingsScroll, VideoSettingsButton);
        private IEnumerable<LentCard> LentCards => [CaptureCard, VideoCard];

        /// <summary>True while the Capture Settings window is open.</summary>
        internal bool CaptureSettingsOpen => CaptureCard.Window?.IsVisible == true;

        /// <summary>True while the Video Settings window is open.</summary>
        internal bool VideoSettingsOpen => VideoCard.Window?.IsVisible == true;

        internal Window? TestCaptureSettingsWindow => CaptureCard.Window;
        internal Window? TestVideoSettingsWindow => VideoCard.Window;

        /// <summary>Self-tests: the screen size the windows are fitted to, instead of the work area.</summary>
        internal Size? SettingsWindowLimit { get; set; }

        /// <summary>How a settings window is shown. Self-tests can swap this out.</summary>
        internal Action<Window> PresentSettingsWindow { get; set; } = window => window.Show();

        /// <summary>Opens the card in its own window, or brings an open one forward.</summary>
        private void OpenSettingsWindow(LentCard lent)
        {
            if (lent.Window != null) { lent.Window.Activate(); return; }
            var card = lent.Card;
            lent.Home.Children.Remove(card);
            // The window grows to fit the card and scrolls only on a screen too short for it; on a
            // narrow screen the panels wrap to one column.
            var limit = SettingsWindowLimit ?? new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
            lent.Scroll.MaxHeight = Math.Max(200, limit.Height - 110) + lent.Scroll.Padding.Top + lent.Scroll.Padding.Bottom;
            card.MaxWidth = Math.Max(320, limit.Width - card.Margin.Left - card.Margin.Right);
            var window = new SettingsCardWindow(this, card, lent.TitleKey);
            // A self-test window that shows without focus, or invisibly, gets a settings window that does too.
            window.ShowActivated = ShowActivated;
            if (Opacity < 1) window.Opacity = Opacity;
            window.PreviewKeyDown += SettingsWindow_PreviewKeyDown;
            // A shortcut recorder only listens while its window has the keyboard.
            window.Deactivated += (_, _) => StopRecording(apply: null);
            window.Closed += (_, _) => ReturnCard(lent, window);
            lent.Window = window;
            PresentSettingsWindow(window);
        }

        private void CloseSettingsWindow(LentCard lent)
        {
            StopRecording(apply: null);
            if (lent.Window is not { } window) return;
            window.Close();
            ReturnCard(lent, window);   // a window a self-test never showed raises no Closed
        }

        /// <summary>Takes the card back from a closed window. Runs once per window.</summary>
        private void ReturnCard(LentCard lent, SettingsCardWindow window)
        {
            if (lent.Window != window) return;
            StopRecording(apply: null);
            lent.Window = null;
            window.Release();
            lent.Home.Children.Add(lent.Card);
            if (IsActive) lent.Button.Focus();
        }

        /// <summary>The main window hides for a capture and when it goes to the tray. An owned
        /// window does not hide with it, so the settings windows follow it by hand.</summary>
        private void FollowOwnerVisibility(object sender, DependencyPropertyChangedEventArgs e)
        {
            foreach (var lent in LentCards)
            {
                if (lent.Window is not { } window) continue;
                if (IsVisible) { if (!window.IsVisible) window.Show(); }
                else if (window.IsVisible) window.Hide();
            }
        }

        private void RelocalizeSettingsWindows()
        {
            foreach (var lent in LentCards) lent.Window?.Relocalize();
        }

        private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // A shortcut recorder owns every key while it is listening.
            if (_recording != null) { RecorderKey(e); return; }
            if (e.Key != Key.Escape) return;
            // An open drop-down takes Escape first, the way it does everywhere else.
            if (Keyboard.FocusedElement is DependencyObject focused
                && (focused as ComboBox ?? ItemsControl.ItemsControlFromItemContainer(focused) as ComboBox
                    ?? FindAncestor<ComboBox>(focused)) is { IsDropDownOpen: true }) return;
            if (LentCards.FirstOrDefault(l => l.Window == sender) is not { } lent) return;
            e.Handled = true;
            CloseSettingsWindow(lent);
        }

        // A card's top band moves its window, like a title bar.
        private void SettingsDrag_Down(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) != null) return;
            if (Window.GetWindow((DependencyObject)sender) is not SettingsCardWindow window) return;
            e.Handled = true;
            window.DragFrom(e);
        }
    }
}
