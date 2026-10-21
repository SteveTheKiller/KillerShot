using System.Globalization;
using System.Windows;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

// Collections beside the workspace and a separate photo carousel above or below its main view.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private CollectionStrip? _strip;
        private CollectionStrip? _navigationStrip;
        private bool _syncingSources;

        private void CollectionPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_navigationStrip == null) return;
            FrameworkElement anchor = CollectionPickerBtn.IsVisible ? CollectionPickerBtn : _toolbarHidden ? ToolbarOverflowButton : MoreBtn;
            var menu = _navigationStrip.SourceMenu(anchor);
            menu.IsOpen = true;
        }

        private void InitStrip()
        {
            NavigationDock.Side = Settings.Get("CollectionSidebarSide") == "Right" ? DockSide.Right : DockSide.Left;
            NavigationDock.IsMinimized = Settings.GetBool("AlbumStripMinimized", false);
            NavigationDock.PaneWidth = double.TryParse(Settings.Get("AlbumStripWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                && double.IsFinite(w) ? Math.Clamp(w, 110, 600) : 220;
            StripDock.Side = Settings.Get("EditorCarouselSide") == "Bottom" ? DockSide.Bottom : DockSide.Top;
            StripDock.IsMinimized = Settings.GetBool("EditorCarouselMinimized", false);
            StripDock.PaneHeight = double.TryParse(Settings.Get("EditorCarouselHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
                && double.IsFinite(h) ? Math.Clamp(h, 110, 320) : 170;
            _navigationStrip = new CollectionStrip(NavigationDock, (Style)Library.FindResource("LibSlider"));
            _navigationStrip.ClipToBounds = true;
            _navigationStrip.SetAlbumMode(true);
            NavigationDock.Pane = _navigationStrip;
            _strip = new CollectionStrip(StripDock, (Style)Library.FindResource("LibSlider"));
            ToolbarCollectionHost.Content = _strip.CollectionPicker;
            _navigationStrip.StatusMessage += message => StatusText.Text = message;
            _strip.StatusMessage += message => StatusText.Text = message;
            _navigationStrip.SourceChanged += () => SyncSources(_navigationStrip);
            _strip.SourceChanged += () => SyncSources(_strip);
            _strip.SourceChanged += SyncFooterCollection;
            Library.CountChanged += caption => { if (Library.IsVisible) CollectionCountText.Text = caption; };
            _strip.OpenRequested += OpenFromStrip;
            _strip.ItemMenuRequested += (item, anchor) => Library.ShowItemMenu(item, anchor, OpenFromStrip, "");
            StripDock.Pane = _strip;
            SyncFooterCollection();
            StripDock.LayoutChanged += () =>
            {
                Settings.Set("EditorCarouselSide", StripDock.Side.ToString());
                Settings.SetBool("EditorCarouselMinimized", StripDock.IsMinimized);
                Settings.Set("EditorCarouselHeight", StripDock.PaneHeight.ToString("0", CultureInfo.InvariantCulture));
                _strip.Relocalize();
                SyncEditorPaneMargin();
            };
            NavigationDock.LayoutChanged += () =>
            {
                Settings.Set("CollectionSidebarSide", NavigationDock.Side.ToString());
                Settings.SetBool("AlbumStripMinimized", NavigationDock.IsMinimized);
                Settings.Set("AlbumStripWidth", NavigationDock.PaneWidth.ToString("0", CultureInfo.InvariantCulture));
                _navigationStrip.Relocalize();
                SyncEditorPaneMargin();
            };
            ThemeManager.ThemeChanged += SyncEditorPaneMargin;
            Closed += (_, _) => ThemeManager.ThemeChanged -= SyncEditorPaneMargin;
            SyncLibraryVisibility();
            if (_doc != null) _strip.Follow(App.Instance.CurrentItem, _doc.FilePath);
            SyncEditorPaneMargin();
        }

        private void SyncFooterCollection()
        {
            if (_navigationStrip == null) return;
            CollectionNameText.Text = _navigationStrip.SourceCaption;
            CollectionCountText.Text = _navigationStrip.CountCaption;
            if (Library.IsVisible) Library.SetSource(_navigationStrip.Source);
        }

        private void SyncSources(CollectionStrip origin)
        {
            if (_syncingSources || _strip == null || _navigationStrip == null) return;
            _syncingSources = true;
            try
            {
                var other = ReferenceEquals(origin, _strip) ? _navigationStrip : _strip;
                if (other.Source != origin.Source) other.SetSource(origin.Source);
                SyncFooterCollection();
            }
            finally { _syncingSources = false; }
        }

        private void SyncStripPlacement()
        {
            NavigationDock.ShowPane = true;
            // Over the album's carousel views the photo strip would be a second carousel of the
            // same pictures, so only its rail stays.
            bool showPane = !(Library.IsVisible && Library.CurrentView is "flow" or "shelves");
            bool railChanged = StripDock.RailSize != 24;
            bool paneChanged = StripDock.ShowPane != showPane;
            StripDock.RailSize = 24;
            StripDock.ShowPane = showPane;
            if (railChanged && !paneChanged) StripDock.RefreshLayout();
            if (_navigationStrip != null) SyncSources(_navigationStrip);
            SyncEditorPaneMargin();
        }

        private void SyncEditorPaneMargin()
        {
            var margin = (Thickness)FindResource("ContentPaneMargin");
            ContentPane.Margin = NavigationDock.Side switch
            {
                DockSide.Right => new Thickness(margin.Right, margin.Top, margin.Left, margin.Bottom),
                DockSide.Top or DockSide.Bottom => new Thickness(margin.Right, margin.Top, margin.Right, margin.Bottom),
                _ => margin,
            };
        }

        /// <summary>A picture chosen in the strip replaces the one in the editor. Marks that were
        /// added and not saved are asked about first, since the library keeps the original.</summary>
        private void OpenFromStrip(CaptureItem item)
        {
            try { App.Instance.OpenLibraryCapture(item); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _strip?.Follow(App.Instance.CurrentItem, _doc?.FilePath); }
        }

        internal CollectionStrip? TestStrip => Library.IsVisible ? _navigationStrip : _strip;
        internal DockHost TestStripDock => Library.IsVisible ? NavigationDock : StripDock;
        internal void TestShowLibraryView(string view) => ShowLibraryView(view);
        internal CollectionStrip? TestNavigationStrip => _navigationStrip;
        internal DockHost TestNavigationDock => NavigationDock;
    }
}
