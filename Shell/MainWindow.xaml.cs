using System.Windows;
using System.Windows.Input;
using KillerShot.Features;

// The main window's constructor and the handlers that belong to no feature. The rest of MainWindow
// is split by surface: Chrome.cs (caption buttons, placement, grain), ThemeFlyout.cs, About.cs,
// EditorHost.cs (the editor and its toolbar), CaptureSettingsPanel.cs, FooterView.cs, Shortcuts.cs
// and ShortcutsOverlay.cs.
namespace KillerShot.Shell
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            new KillerFamily.Accessibility.OverlayFocus(this)
                .Track(ShortcutsOverlay, AboutOverlay, CaptureSettingsOverlay, VideoSettingsOverlay);
            var titleFrames = System.Windows.Media.Imaging.BitmapDecoder.Create(
                new System.Uri("pack://application:,,,/Resources/kst-icon.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames;
            foreach (var icon in new[] { WordmarkTitleIcon, PlainTitleIcon })
            {
                icon.Source = titleFrames[0];
            }

            RestoreWindowPlacement();
            InitAppScale();
            SourceInitialized += MainWindow_SourceInitialized;
            ApplyGrainTexture();
            Loaded += (_, _) =>
            {
                FadeInContent();
                ZoomToFit();
                Editor.Focus();
            };

            // Every flyout opens beside its own button and is clamped inside the content pane.
            Controls.FlyoutPlacement.UsePane(ContentPane);

            InitThemePicker();
            InitLanguageMenu();
            // After InitLanguageMenu: the toolbar captions are read through Loc(). The layout
            // places the buttons first, then the style builds their content.
            InitToolbarLayout();
            InitToolbarStyle();
            InitEditor();
            Services.CardColors.Apply();   // the card brushes the library and the strip draw on
            InitLibrary();
            InitStrip();
            InitFooterView();

            _about = new AboutController(this);
            VersionLabel.Text = "v" + Services.AppInfo.Version;
        }

        /// <summary>The footer version label opens the About card.</summary>
        private void VersionLabel_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowAboutOverlay();
        }
    }
}
