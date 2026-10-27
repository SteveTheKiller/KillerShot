using System.Threading.Tasks;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>APP-OWNED: KillerShot's side of the family install badge (Shell/Install.cs). The confirm
/// carries the all-users choice, the install goes through the same checks as Exit first, the copy
/// runs off the interface thread, and a relaunch can be deferred.</summary>
public partial class MainWindow
{
    partial void ConfigureInstall(InstallOptions options)
    {
        options.OfferAllUsers = true;
        // The installed copy replaces this one, so unsaved edits, a recording and captures still
        // waiting for the album go through the same checks as Exit first.
        options.CanInstallAsync = () => App.Instance.CanExitAsync();
        options.InstallAsync = async (desktop, allUsers) =>
        {
            var approval = App.Instance.CaptureExitApproval();
            // The copy and the wait for the elevated install run off the interface thread, so the
            // window keeps painting; their dialogs and the restart come back to it.
            bool installed;
            bool deferred = false;
            using (new WaitCursorScope()) installed = await Task.Run(() => App.InstallAndRelaunch(wantDesktop: desktop, allUsers: allUsers, approval: approval, out deferred));
            if (installed) return InstallOutcome.Installed;
            return deferred ? InstallOutcome.Deferred : InstallOutcome.Failed;
        };
    }

    // The spoken name starts with the visible word, so "click Portable" finds it by voice.
    partial void AfterPortableBadgeRefreshed()
        => System.Windows.Automation.AutomationProperties.SetName(PortableBadge,
            LocaleManager.Loc("Str_Portable") + ", " + LocaleManager.Loc("Str_Btn_Install"));
}
