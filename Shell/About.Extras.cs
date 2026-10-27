using System.Windows;
using KillerShot.Features.About;

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
            var mode = Services.RenderModeService.Choice;
            RenderAutoRadio.IsChecked = mode == Services.RenderChoice.Auto;
            RenderHardwareRadio.IsChecked = mode == Services.RenderChoice.Hardware;
            RenderSoftwareRadio.IsChecked = mode == Services.RenderChoice.Software;
            _syncingRenderMode = false;
        }

        private void RenderMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingRenderMode || sender is not FrameworkElement { Tag: string tag }) return;
            if (System.Enum.TryParse<Services.RenderChoice>(tag, out var choice))
                Services.RenderModeService.Set(choice);
        }
    }
}
