using System.Globalization;
using System.Windows;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

// The collection strip beside the open picture (CollectionStrip, in the family DockHost).
// Alt+Shift+arrow docks it to that side, and the arrow of its own side folds it to a bar and back.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private CollectionStrip? _strip;
        private (DockSide Side, bool IsMinimized)? _editorStripLayout;

        private void CollectionPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_strip == null) return;
            FrameworkElement anchor = CollectionPickerBtn.IsVisible ? CollectionPickerBtn : _toolbarHidden ? ToolbarOverflowButton : MoreBtn;
            var menu = _strip.SourceMenu(anchor);
            menu.IsOpen = true;
        }

        private void InitStrip()
        {
            StripDock.Side = Enum.TryParse(Settings.Get("StripSide"), out DockSide side) ? side : DockSide.Top;
            StripDock.IsMinimized = Settings.GetBool("StripMinimized", false);
            if (double.TryParse(Settings.Get("StripWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) StripDock.PaneWidth = w;
            StripDock.PaneHeight = double.TryParse(Settings.Get("StripHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 230;
            _strip = new CollectionStrip(StripDock, (Style)Library.FindResource("LibSlider"));
            _strip.SourceChanged += SyncFooterCollection;
            Library.CountChanged += caption => { if (Library.IsVisible) CollectionCountText.Text = caption; };
            _strip.OpenRequested += OpenFromStrip;
            _strip.ItemMenuRequested += (item, anchor) => Library.ShowItemMenu(item, anchor, OpenFromStrip, "");
            StripDock.Pane = _strip;
            SyncFooterCollection();
            StripDock.LayoutChanged += () =>
            {
                if (_editorStripLayout != null)
                {
                    StripDock.Side = DockSide.Left;
                    Settings.SetBool("AlbumStripMinimized", StripDock.IsMinimized);
                }
                else
                {
                    Settings.Set("StripSide", StripDock.Side.ToString());
                    Settings.SetBool("StripMinimized", StripDock.IsMinimized);
                }
                Settings.Set("StripWidth", StripDock.PaneWidth.ToString("0", CultureInfo.InvariantCulture));
                Settings.Set("StripHeight", StripDock.PaneHeight.ToString("0", CultureInfo.InvariantCulture));
                _strip.Relocalize();
                SyncEditorPaneMargin();
            };
            ThemeManager.ThemeChanged += SyncEditorPaneMargin;
            Closed += (_, _) => ThemeManager.ThemeChanged -= SyncEditorPaneMargin;
            SyncLibraryVisibility();
            SyncEditorPaneMargin();
        }

        private void SyncFooterCollection()
        {
            if (_strip == null) return;
            CollectionNameText.Text = _strip.SourceCaption;
            CollectionCountText.Text = _strip.CountCaption;
            Library.SetSource(_strip.Source);
        }

        private void SyncStripPlacement(bool library)
        {
            if (_strip == null) return;
            if (library)
            {
                if (_editorStripLayout == null)
                {
                    _editorStripLayout = (StripDock.Side, StripDock.IsMinimized);
                    StripDock.IsMinimized = Settings.GetBool("AlbumStripMinimized", false);
                }
                StripDock.Side = DockSide.Left;
            }
            else if (_editorStripLayout is { } layout)
            {
                _editorStripLayout = null;
                StripDock.Side = layout.Side;
                StripDock.IsMinimized = layout.IsMinimized;
            }
            SyncEditorPaneMargin();
        }

        private void SyncEditorPaneMargin()
        {
            var margin = (Thickness)FindResource("ContentPaneMargin");
            ContentPane.Margin = StripDock.Side switch
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

        internal CollectionStrip? TestStrip => _strip;
        internal DockHost TestStripDock => StripDock;
    }
}
