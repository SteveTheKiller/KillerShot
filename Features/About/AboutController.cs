using System;
using System.Threading.Tasks;
using KillerShot.Services;

namespace KillerShot.Features
{
    /// <summary>
    /// The About card's content: version and release date, the code-signing publisher and
    /// thumbprint, and the running exe's SHA-256. The update check joins once KillerShot has
    /// published releases to check against.
    /// </summary>
    internal sealed class AboutController
    {
        private readonly IAboutHost _host;

        internal AboutController(IAboutHost host) => _host = host;

        /// <summary>KillerShot starts in the tray, with no window, when Windows starts.</summary>
        internal const string StartArgument = "--tray";

        /// <summary>Adds or removes the app's Run value from the About checkbox, then shows what the
        /// registry actually holds, so a refused write unchecks the box again.</summary>
        internal void SetStartWithWindows(bool enabled)
        {
            KillerFamily.Deployment.StartupRegistration.Set(AppInfo.DisplayName, enabled, StartArgument);
            _host.StartWithWindows = KillerFamily.Deployment.StartupRegistration.IsEnabled(AppInfo.DisplayName);
        }

        /// <summary>Fills the card in and shows it, then finishes the hash in the background.</summary>
        internal void Show()
        {
            _host.Version     = $"v{AppInfo.Version}";
            _host.ReleaseDate = AppInfo.ReleaseDate;

            var (sigValid, subject, thumb) = CodeSignature.GetSignerInfo();
            _host.Publisher = sigValid ? subject : _host.Loc("Str_About_Unsigned");

            // Shown only when the exe is signed by the expected publisher AND the signature verifies.
            bool signedByPublisher = sigValid &&
                subject.IndexOf(AppInfo.SignerName, StringComparison.OrdinalIgnoreCase) >= 0;

            // 0x201C / 0x201D are the curly quotes, built from codepoints so the source stays ASCII.
            _host.Alias        = (char)0x201C + AppInfo.AkaName + (char)0x201D;
            _host.AliasVisible = signedByPublisher;
            _host.Thumbprint   = thumb;
            _host.Sha256       = _host.Loc("Str_About_Computing");
            _host.StartWithWindows = KillerFamily.Deployment.StartupRegistration.IsEnabled(AppInfo.DisplayName);

            _host.ShowCard();

            Task.Run(() =>
            {
                var hash = CodeSignature.ExeSha256();
                _host.Window.Dispatcher.BeginInvoke((Action)(() => _host.Sha256 = hash));
            });
        }

        /// <summary>Opens the project page for the running version.</summary>
        internal void OpenReleaseNotes() => WebLink.Open("https://github.com/" + AppInfo.GitHubRepo + "/releases");
    }
}
