using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using KillerShot.Controls;
using KillerShot.Features;

namespace KillerShot.Shell
{
    /// <summary>
    /// The About overlay's window half: the fade, the click handling, and the IAboutHost
    /// implementation that maps the controller's values onto the named XAML elements.
    /// </summary>
    public partial class MainWindow : IAboutHost
    {
        private readonly AboutController _about = null!;

        private void ShowAboutOverlay()
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) ShortcutsOverlay.Visibility = Visibility.Collapsed;
            if (CaptureSettingsOverlay.Visibility == Visibility.Visible) CaptureSettingsOverlay.Visibility = Visibility.Collapsed;
            if (VideoSettingsOverlay.Visibility == Visibility.Visible) VideoSettingsOverlay.Visibility = Visibility.Collapsed;
            SyncRenderModeRadios();
            _syncingCloseToTray = true;
            CloseToTrayCheck.IsChecked = App.CloseToTray;
            _syncingCloseToTray = false;
            _about.Show();
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

        // ---- IAboutHost ----

        string IAboutHost.Version     { set => AboutVersionBlock.Text = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text = value; }
        string IAboutHost.Alias       { set => AboutAkaRun.Text = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text = value; }
        bool IAboutHost.StartWithWindows { set => AboutStartWithWindows.IsChecked = value; }

        bool IAboutHost.AliasVisible
        {
            set => AboutAkaBlock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        void IAboutHost.ShowCard() => FadeOverlayIn(AboutOverlay);

        // ---- Overlay fade ----

        private void FadeOverlayIn(UIElement o)
        {
            ClassicFrameChrome.Opacity = 0.42;
            o.Visibility = Visibility.Visible;
            Anim.FadeIn(o);
        }

        private void FadeOverlayOut(UIElement o)
        {
            var a = new DoubleAnimation(o.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Anim.FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            a.Completed += (_, _) =>
            {
                o.Visibility = Visibility.Collapsed;
                ClassicFrameChrome.Opacity = 1;
            };
            o.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // ---- Handlers ----

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => FadeOverlayOut(AboutOverlay);
        private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void AboutClose_Click(object sender, RoutedEventArgs e) => FadeOverlayOut(AboutOverlay);

        private void AboutVersion_Click(object sender, MouseButtonEventArgs e) => _about.OpenReleaseNotes();

        private void AboutStartWithWindows_Click(object sender, RoutedEventArgs e)
            => _about.SetStartWithWindows(AboutStartWithWindows.IsChecked == true);

        private void AboutLink_Navigate(object sender, RequestNavigateEventArgs e)
        {
            Services.WebLink.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
