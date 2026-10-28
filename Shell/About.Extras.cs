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

        /// <summary>The Print Screen checkbox, with what it does in its tooltip.</summary>
        private void SyncPrintScreenKey()
        {
            bool was = _syncingOptionsPrintScreen;
            _syncingOptionsPrintScreen = true;
            PrintScreenKeyToggle.IsChecked = PrintScreenKey.Enabled;
            PrintScreenKeyToggle.ToolTip = Loc("Str_CS_PrintScreenKeyHint");
            _syncingOptionsPrintScreen = was;
        }

        /// <summary>Turns the Print Screen switch on or off, then claims the shortcuts again, which
        /// puts the hook on or takes it off (Native/PrintScreenHook.cs).</summary>
        private void SetPrintScreenKey(bool on)
        {
            try { PrintScreenKey.Enabled = on; }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                StatusText.Text = error.Message;
            }
            _shortcutHost?.RegisterShortcuts();
            BuildShortcutRows();
            SyncPrintScreenKey();
            if (on) App.OfferPrintScreenHelper();   // so it works over admin windows too
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
