using System.Windows;
using KillerShot.Features.About;
using KillerShot.Services;

namespace KillerShot.Shell
{
    /// <summary>
    /// APP-OWNED: the rows KillerShot's About card adds to the shared core (Shell/About.cs): Start
    /// with Windows (which starts in the tray), Close to system tray (on by default, kept in
    /// KillerShot's own settings), and the rendering mode. Also dims the classic frame behind an
    /// open overlay.
    /// </summary>
    public partial class MainWindow : IAboutStartup, IAboutExtras
    {
        bool IAboutStartup.StartWithWindows { set => AboutStartWithWindows.IsChecked = value; }

        /// <summary>Runs as the card is filled: one overlay at a time, and the rows read their
        /// current values.</summary>
        void IAboutExtras.SyncExtraRows()
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) ShortcutsOverlay.Visibility = Visibility.Collapsed;
            if (CaptureSettingsOverlay.Visibility == Visibility.Visible) CaptureSettingsOverlay.Visibility = Visibility.Collapsed;
            if (VideoSettingsOverlay.Visibility == Visibility.Visible) VideoSettingsOverlay.Visibility = Visibility.Collapsed;
            SyncRenderModeRadios();
            _syncingCloseToTray = true;
            CloseToTrayCheck.IsChecked = App.CloseToTray;
            _syncingCloseToTray = false;
            SyncPrintScreenKey();
        }

        // ---- Use KillerShot for the Print Screen key ----

        private bool _syncingOptionsPrintScreen;

        private void PrintScreenKey_Click(object sender, RoutedEventArgs e) => SetPrintScreenKey(PrintScreenKeyToggle.IsChecked == true);

        /// <summary>The tray menu changed it: the card shows the new state if it is open.</summary>
        internal void PrintScreenKeyChangedElsewhere() => SyncPrintScreenKey();

        /// <summary>The Print Screen checkbox and its note, from the Windows setting as it is now.</summary>
        private void SyncPrintScreenKey()
        {
            bool was = _syncingOptionsPrintScreen;
            _syncingOptionsPrintScreen = true;
            var key = PrintScreenKey.Current;
            PrintScreenKeyToggle.IsChecked = key.State == PrintScreenKeyState.On;
            PrintScreenKeyToggle.IsEnabled = key.CanChange;
            PrintScreenKeyNote.Text = Loc(key.NoteKey);
            PrintScreenKeyToggle.ToolTip = Loc(key.NoteKey);
            PrintScreenKeyNote.Visibility = key.State == PrintScreenKeyState.ChangedElsewhere ? Visibility.Visible : Visibility.Collapsed;
            _syncingOptionsPrintScreen = was;
        }

        /// <summary>Frees Print Screen from Windows' screen capture, or gives it back, then claims the
        /// shortcuts again. Windows can take a moment to let go, so a failed claim is tried once more.</summary>
        private void SetPrintScreenKey(bool on)
        {
            try
            {
                var key = PrintScreenKey.Current;
                if (on) key.TurnOn(); else key.TurnOff();
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                StatusText.Text = error.Message;
            }
            void Claim()
            {
                _shortcutHost?.RegisterShortcuts();
                BuildShortcutRows();
            }
            Claim();
            if (on && _shortcutHost?.FailedShortcuts.Count > 0)
                Dispatcher.BeginInvoke(async () => { await Task.Delay(600); Claim(); });
            SyncPrintScreenKey();
        }


        partial void OnOverlayShowing(UIElement overlay) => ClassicFrameChrome.Opacity = 0.42;

        partial void OnOverlayHidden(UIElement overlay) => ClassicFrameChrome.Opacity = 1;

        // ---- Start with Windows ----

        /// <summary>Writes the Run value with the tray argument, then shows what the registry
        /// actually holds, so a refused write unchecks the box again.</summary>
        private void AboutStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            KillerFamily.Deployment.StartupRegistration.Set(Services.AppInfo.DisplayName,
                AboutStartWithWindows.IsChecked == true, AboutController.StartArgument);
            AboutStartWithWindows.IsChecked =
                KillerFamily.Deployment.StartupRegistration.IsEnabled(Services.AppInfo.DisplayName);
        }

        // ---- Close to system tray ----

        private bool _syncingCloseToTray;

        private void CloseToTray_Changed(object sender, RoutedEventArgs e)
        {
            if (!_syncingCloseToTray) App.CloseToTray = CloseToTrayCheck.IsChecked == true;
        }

        // ---- Rendering mode ----

        private bool _syncingRenderMode;

        private void SyncRenderModeRadios()
        {
            _syncingRenderMode = true;
            string mode = Services.RenderModeService.Choice.ToString();
            RenderModeCombo.SelectedItem = RenderModeCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == mode);
            _syncingRenderMode = false;
        }

        private void RenderMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_syncingRenderMode || RenderModeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string tag }) return;
            if (System.Enum.TryParse<Services.RenderChoice>(tag, out var choice))
                Services.RenderModeService.Set(choice);
        }
    }
}
