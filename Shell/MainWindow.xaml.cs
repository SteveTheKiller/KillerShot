using System.Windows;
using System.Windows.Input;
using KillerShot.Features.About;

// The main window's constructor and the handlers that belong to no feature. The rest of MainWindow
// is split by surface: Chrome.cs (caption buttons, placement, grain), ThemeFlyout.cs, About.cs,
// EditorHost.cs (the editor and its toolbar), CaptureSettingsPanel.cs, FooterView.cs, Shortcuts.cs
// and ShortcutsOverlay.cs.
namespace KillerShot.Shell
{
    public partial class MainWindow : Window
    {
        private readonly TrayHost? _shortcutHost;

        public MainWindow() : this(App.Instance.Tray) { }

        internal MainWindow(TrayHost? shortcutHost)
        {
            _shortcutHost = shortcutHost;
            InitializeComponent();
            InitChromeExtras();
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
                _about.CheckOnStartup();
            };

            Controls.FlyoutPlacement.UsePane(ContentPane);
            // The editor can be picked in Window mode like any other app's window.
            SourceInitialized += (_, _) => Capture.WindowCapture.AllowOwnWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, true);
            Closed += (_, _) => Capture.WindowCapture.AllowOwnWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, false);

            InitThemePicker();
            InitLanguageMenu();
            // After InitLanguageMenu: the toolbar captions are read through Loc(). The layout
            // places the buttons first, then the style builds their content.
            InitToolbarLayout();
            InitToolbarStyle();
            InitEditor();
            InitQrReader();
            Services.CardColors.Apply();   // the card brushes the library and the strip draw on
            InitLibrary();
            InitStrip();
            InitFooterView();
            InitWorkspaceMenus();
            InitImageDrop();
            RefreshPortableBadge();

            _about = new AboutController(this);
            VersionLabel.Text = "v" + Services.AppInfo.Version;
        }
    }
}
