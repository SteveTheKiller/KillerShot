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

        private void InitStrip()
        {
            StripDock.Side = Enum.TryParse(Settings.Get("StripSide"), out DockSide side) ? side : DockSide.Top;
            StripDock.IsMinimized = Settings.GetBool("StripMinimized", false);
            if (double.TryParse(Settings.Get("StripWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) StripDock.PaneWidth = w;
            StripDock.PaneHeight = double.TryParse(Settings.Get("StripHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 230;
            _strip = new CollectionStrip(StripDock, (Style)Library.FindResource("LibSlider"));
            _strip.OpenRequested += OpenFromStrip;
            _strip.ItemMenuRequested += (item, anchor) => Library.ShowItemMenu(item, anchor, OpenFromStrip, "");
            StripDock.Pane = _strip;
            StripDock.LayoutChanged += () =>
            {
                Settings.Set("StripSide", StripDock.Side.ToString());
                Settings.SetBool("StripMinimized", StripDock.IsMinimized);
                Settings.Set("StripWidth", StripDock.PaneWidth.ToString("0", CultureInfo.InvariantCulture));
                Settings.Set("StripHeight", StripDock.PaneHeight.ToString("0", CultureInfo.InvariantCulture));
                _strip.Relocalize();
            };
        }

        /// <summary>A picture chosen in the strip replaces the one in the editor. Marks that were
        /// added and not saved are asked about first, since the library keeps the original.</summary>
        private void OpenFromStrip(CaptureItem item)
        {
            if (_doc?.CanUndo == true)
            {
                var dlg = new ConfirmDialog(Loc("Str_Strip_DiscardTitle"), Loc("Str_Strip_DiscardDetail"), Loc("Str_Strip_Discard"), Loc("Str_Btn_Cancel")) { Owner = this };
                dlg.ShowDialog();
                if (!dlg.Confirmed)
                {
                    _strip?.Follow(App.Instance.CurrentItem, _doc.FilePath);
                    return;
                }
            }
            try { App.Instance.OpenLibraryCapture(item); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        internal CollectionStrip? TestStrip => _strip;
        internal DockHost TestStripDock => StripDock;
    }
}
