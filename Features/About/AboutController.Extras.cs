namespace KillerShot.Features.About
{
    /// <summary>APP-OWNED: KillerShot's rows on the About card, filled as the card opens.</summary>
    internal interface IAboutExtras
    {
        void SyncExtraRows();
    }

    /// <summary>
    /// APP-OWNED: KillerShot's additions to the shared About controller.
    /// </summary>
    internal sealed partial class AboutController
    {
        /// <summary>KillerShot starts in the tray, with no window, when Windows starts.</summary>
        internal const string StartArgument = "--tray";

        partial void OnShow()
        {
            if (_host is IAboutExtras extras) extras.SyncExtraRows();
            if (!Services.UpdateService.IsStoreBuild) return;
            _host.UpdateText    = _host.Loc(Services.UpdateService.StoreChannelMessageKey);
            _host.UpdateVisible = true;
            _host.UpdateEnabled = false;
        }

        /// <summary>The Microsoft Store updates a Store copy, so it never offers the GitHub exe.</summary>
        partial void SkipStartupCheck(ref bool skip) => skip |= Services.UpdateService.IsStoreBuild;

        partial void HoldUpdate(ref bool hold) => hold |= Services.UpdateService.IsStoreBuild;
    }
}
