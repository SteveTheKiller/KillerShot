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
        }
    }
}
