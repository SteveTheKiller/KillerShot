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

        /// <summary>Clicks the shortcut recorder for <paramref name="action"/>.</summary>
        internal bool TestStartRecording(Capture.ShortcutAction action)
        {
            System.Windows.Controls.Button? Find() => ShortcutRows.Children.OfType<System.Windows.Controls.Button>()
                .Concat(VideoShortcutRows.Children.OfType<System.Windows.Controls.Button>())
                .FirstOrDefault(button => button.Tag is Capture.ShortcutAction tagged && tagged == action);
            if (Find() == null) BuildShortcutRows();
            var recorder = Find();
            if (recorder == null) return false;
            StartRecording(recorder);
            return true;
        }

        internal void TestRecorderKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers) => RecorderKey(key, modifiers);

        internal bool TestIsRecording => _recording != null;

        internal void TestHideToTray() => HideToTray();

        internal void TestDeactivate() => OnDeactivated(EventArgs.Empty);

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
