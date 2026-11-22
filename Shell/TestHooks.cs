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

        /// <summary>Self-test shots: the window content, with an open settings window drawn over it
        /// where it sits on screen (centered when it has no position yet).</summary>
        internal System.Windows.Media.Imaging.RenderTargetBitmap TestRenderScene()
        {
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            System.Windows.Media.Imaging.RenderTargetBitmap Render(System.Windows.Media.Visual visual, double width, double height)
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                return bitmap;
            }
            var main = Render(root, root.ActualWidth, root.ActualHeight);
            if (LentCards.Select(l => l.Window).FirstOrDefault(w => w is { IsVisible: true, ActualWidth: > 0 }) is not { } settings) return main;
            settings.UpdateLayout();
            // The settings window's content, card margin (shadow halo) included.
            var card = Render((System.Windows.Media.Visual)settings.Content, settings.ActualWidth, settings.ActualHeight);
            double x = double.IsNaN(settings.Left) || double.IsNaN(Left) ? (root.ActualWidth - settings.ActualWidth) / 2 : settings.Left - Left;
            double y = double.IsNaN(settings.Top) || double.IsNaN(Top) ? (root.ActualHeight - settings.ActualHeight) / 2 : settings.Top - Top;
            var scene = new System.Windows.Media.DrawingVisual();
            using (var dc = scene.RenderOpen())
            {
                dc.DrawImage(main, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawImage(card, new Rect(x, y, settings.ActualWidth, settings.ActualHeight));
            }
            return Render(scene, root.ActualWidth, root.ActualHeight);
        }

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
            foreach (var o in new UIElement[] { ShortcutsOverlay, AboutOverlay })
            {
                o.BeginAnimation(OpacityProperty, null);
                o.Visibility = Visibility.Collapsed;
            }
            CloseCaptureSettings();
            CloseVideoSettings();
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
