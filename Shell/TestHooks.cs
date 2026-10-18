using System.Windows;

namespace KillerShot.Shell
{
    /// <summary>Entry points for SelfTestUi. They drive the same methods the buttons and keys use.</summary>
    public partial class MainWindow
    {
        internal void TestRelocalize() => RelocalizeDynamicUi();

        internal void TestShowShortcuts(bool map)
        {
            if (ShortcutsOverlay.Visibility != Visibility.Visible) ToggleShortcutsOverlay();
            ApplyShortcutView(map, persist: false);
        }

        internal void TestShowAbout() => ShowAboutOverlay();

        internal void TestShowCaptureSettings() => ShowCaptureSettings();

        internal void TestShowVideoSettings() => ShowVideoSettings();

        internal void TestCloseOverlays()
        {
            foreach (var o in new UIElement[] { ShortcutsOverlay, AboutOverlay, CaptureSettingsOverlay, VideoSettingsOverlay })
            {
                o.BeginAnimation(OpacityProperty, null);
                o.Visibility = Visibility.Collapsed;
            }
            ClassicFrameChrome.Opacity = 1;
        }

        internal LibraryView TestLibrary => Library;

        internal void TestToggleLibrary() => ToggleLibrary();

        internal void TestOpenLibraryCapture(Services.CaptureItem item) => App.Instance.OpenLibraryCapture(item);

        internal void TestToolbarStyle(int number) => PickToolbarStyle(number);

        internal void TestToolbarLayout(ToolbarLayout? layout) => SetToolbarLayout(layout ?? DefaultToolbarLayout);

        /// <summary>Customize toolbar, shown off screen without the modal loop, for a render.</summary>
        internal Window TestCustomizeToolbarDialog()
        {
            var dialog = new Controls.CustomizeToolbarDialog(_toolbarUnits, _toolbarLayout, DefaultToolbarLayout,
                id => ToolbarUnitIcon(id), Loc)
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            dialog.Show();
            return dialog;
        }

        internal void TestSetLabels(bool under)
        {
            _labelMode = under ? LabelMode.Under : LabelMode.None;
            ApplyToolbarStyle();
        }
    }
}
