using System.Windows;
using KillerShot.Services;

namespace KillerShot.Shell
{
    /// <summary>The language menu's window half, and putting strings that were set from code back
    /// after a live language change.</summary>
    public partial class MainWindow
    {
        private Controls.LanguageMenu _languageMenu = null!;

        private void InitLanguageMenu()
        {
            // LangButton: a flyout hangs off its own button (family rule).
            _languageMenu = new Controls.LanguageMenu(LangMenu, LangButton, RelocalizeDynamicUi);
        }

        private void LangButton_Click(object sender, RoutedEventArgs e) => _languageMenu.Open();

        /// <summary>Look up a localized string.</summary>
        private string Loc(string key) => LocaleManager.Loc(key);

        /// <summary>Re-applies strings that were set from code. {DynamicResource Str_*} in XAML
        /// refreshes itself; this is the remainder.</summary>
        private void RelocalizeDynamicUi()
        {
            RelocalizeToolbar();
            RefreshPortableBadge();
            BuildCaptureMenu();
            RefreshDocumentUi();
            if (CaptureSettingsOverlay.Visibility == Visibility.Visible) { SyncCaptureOptions(); BuildShortcutRows(); }
            if (VideoSettingsOverlay.Visibility == Visibility.Visible) { SyncVideoOptions(); BuildShortcutRows(); }
            if (LibrarySettingsPopup.IsOpen) RefreshAlbumManager();
            StatusText.Text = Loc("Str_Status_Ready");
            App.Instance.Tray?.Relocalize();
        }
    }
}
