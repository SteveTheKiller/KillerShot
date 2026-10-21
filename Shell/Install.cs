using System.Windows;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>The footer's portable badge, the UI half of the family install system. The engine is in
/// App.Installation.cs; the confirm carries the desktop shortcut and all-users choices as in Killendar.</summary>
public partial class MainWindow
{
    /// <summary>Shown when running from outside the installed location, never in demo mode.</summary>
    private void RefreshPortableBadge()
    {
        PortableBadge.Visibility = App.IsPortable() && !DemoMode.IsDemo ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog(
            LocaleManager.Loc("Str_Dlg_InstallMsg"),
            LocaleManager.Loc("Str_Dlg_InstallBullets"),
            LocaleManager.Loc("Str_Btn_DoInstall"),
            LocaleManager.Loc("Str_Btn_Cancel"),
            check1Label: LocaleManager.Loc("Str_Chk_Desktop"), check1Initial: true,
            check2Label: LocaleManager.Loc("Str_Chk_AllUsers"), check2Initial: false)
        { Owner = this };
        dialog.ShowDialog();
        if (!dialog.Confirmed) return;

        // The installed copy replaces this one, so unsaved edits, a recording and captures still
        // waiting for the album go through the same checks as Exit first.
        if (!await App.Instance.CanExitAsync()) return;
        PortableBadge.Visibility = Visibility.Collapsed;
        bool desktop = dialog.Check1Checked, allUsers = dialog.Check2Checked;
        // The copy and the wait for the elevated install run off the interface thread, so the
        // window keeps painting; their dialogs and the restart come back to it.
        bool installed;
        using (new WaitCursorScope()) installed = await Task.Run(() => App.InstallAndRelaunch(wantDesktop: desktop, allUsers: allUsers));
        if (!installed)
        {
            RefreshPortableBadge();
            StatusText.Text = LocaleManager.Loc("Str_Status_InstallFailed");
        }
    }
}
