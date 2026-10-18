using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Shapes;
using KillerFamily.Layout;

// The footer's view cell, as KillerPDF has it: icon radio buttons with the accent underline on the
// one in use. In the library they pick its view (F5 to F8) and its grouping; with a picture open
// they dock the collection strip, and the zoom buttons sit beside them.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private static readonly double[] ZoomMenuSteps = [0.25, 0.5, 0.75, 1, 1.5, 2, 4];

        private readonly List<(RadioButton Button, string View)> _footerViews = [];
        private readonly List<(RadioButton Button, string Group)> _footerGroups = [];
        private readonly List<(RadioButton Button, DockSide Side)> _footerDocks = [];
        private bool _footerSyncing;

        private void InitFooterView()
        {
            foreach (var (view, glyph, key, keys) in new[]
            {
                ("shelves", "\uE8A9", "Str_Lib_ViewShelves", "F5"), ("flow", "\uE7C5", "Str_Lib_ViewFlow", "F6"),
                ("grid", "\uE80A", "Str_Lib_ViewGrid", "F7"), ("timeline", "\uE823", "Str_Lib_ViewTimeline", "F8"),
            })
            {
                string target = view;
                var button = FooterRadio("FooterLibraryView", glyph, key, keys, () => Library.SetView(target));
                _footerViews.Add((button, view));
                FooterLibraryViews.Children.Add(button);
            }
            FooterLibraryViews.Children.Add(FooterSeparator());
            foreach (var (group, glyph, key) in new[] { ("date", "\uE787", "Str_Lib_ByDate"), ("collection", "\uE8F1", "Str_Lib_ByCollection") })
            {
                string target = group;
                var button = FooterRadio("FooterLibraryGroup", glyph, key, null, () => Library.SetGroup(target));
                _footerGroups.Add((button, group));
                FooterLibraryViews.Children.Add(button);
            }
            foreach (var (side, key, arrow) in new[]
            {
                (DockSide.Left, "Str_Dock_Left", "Left"), (DockSide.Top, "Str_Dock_Top", "Up"),
                (DockSide.Right, "Str_Dock_Right", "Right"), (DockSide.Bottom, "Str_Dock_Bottom", "Down"),
            })
            {
                DockSide target = side;
                var button = FooterRadio("FooterStripDock", DockIcon(side), key, "Alt+Shift+" + arrow, () => StripDock.DockTo(target));
                _footerDocks.Add((button, side));
                FooterDockRadios.Children.Add(button);
            }
            Library.ViewChanged += SyncFooterView;
            StripDock.LayoutChanged += SyncFooterView;
            SyncFooterView();
        }

        /// <summary>One radio button: a glyph or a drawn icon, a tooltip that follows the language
        /// with its shortcut after it, and the same name for a screen reader.</summary>
        private RadioButton FooterRadio(string group, object content, string labelKey, string? keys, Action pick)
        {
            var button = new RadioButton { GroupName = group, Content = content };
            button.SetResourceReference(StyleProperty, "FooterModeButton");
            button.SetResourceReference(AutomationProperties.NameProperty, labelKey);
            var label = new TextBlock();
            label.SetResourceReference(TextBlock.TextProperty, labelKey);
            var tip = new StackPanel { Orientation = Orientation.Horizontal };
            tip.Children.Add(label);
            if (keys != null) tip.Children.Add(new TextBlock { Text = " (" + keys + ")" });
            button.ToolTip = tip;
            button.Checked += (_, _) => { if (!_footerSyncing) pick(); };
            return button;
        }

        private static Border FooterSeparator()
        {
            var line = new Border { Width = 1, Height = 14, Margin = new Thickness(5, 0, 5, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            return line;
        }

        /// <summary>A small window outline with a solid bar on the side the strip docks to, drawn in
        /// the button's own color so hover and the checked accent reach it.</summary>
        private static Grid DockIcon(DockSide side)
        {
            var color = new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
            var icon = new Grid { Width = 14, Height = 11 };
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetBinding(Border.BorderBrushProperty, color);
            var bar = new Rectangle();
            bar.SetBinding(Shape.FillProperty, color);
            switch (side)
            {
                case DockSide.Left: bar.Width = 4; bar.HorizontalAlignment = HorizontalAlignment.Left; break;
                case DockSide.Right: bar.Width = 4; bar.HorizontalAlignment = HorizontalAlignment.Right; break;
                case DockSide.Top: bar.Height = 4; bar.VerticalAlignment = VerticalAlignment.Top; break;
                default: bar.Height = 4; bar.VerticalAlignment = VerticalAlignment.Bottom; break;
            }
            icon.Children.Add(frame);
            icon.Children.Add(bar);
            return icon;
        }

        /// <summary>Shows the library's buttons or the picture's, and checks the ones in use.</summary>
        private void SyncFooterView()
        {
            bool library = Library.Visibility == Visibility.Visible;
            FooterLibraryViews.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
            FooterEditorViews.Visibility = !library && _doc != null ? Visibility.Visible : Visibility.Collapsed;
            _footerSyncing = true;
            try
            {
                foreach (var (button, view) in _footerViews) button.IsChecked = Library.CurrentView == view;
                // The timeline is a line of days, so it is always by date.
                bool grouped = Library.CurrentView != "timeline";
                foreach (var (button, group) in _footerGroups)
                {
                    button.IsEnabled = grouped;
                    button.IsChecked = Library.CurrentGroup == group;
                }
                foreach (var (button, side) in _footerDocks) button.IsChecked = StripDock.Side == side;
            }
            finally { _footerSyncing = false; }
        }

        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => ZoomStep(-1);
        private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => ZoomStep(+1);

        /// <summary>The zoom level opens a menu of Fit and the common sizes, the current one checked.</summary>
        private void ZoomLevelBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomMenu.Items.Clear();
            var fit = new MenuItem { Header = Loc("Str_Btn_Fit"), InputGestureText = "Ctrl+0", IsCheckable = true, IsChecked = _fitMode };
            fit.Click += (_, _) => ZoomToFit();
            ZoomMenu.Items.Add(fit);
            ZoomMenu.Items.Add(new Separator());
            foreach (double z in ZoomMenuSteps)
            {
                double level = z;
                var item = new MenuItem
                {
                    Header = (z * 100).ToString("0", CultureInfo.CurrentCulture) + "%",
                    InputGestureText = z == 1 ? "Ctrl+1" : "",
                    IsCheckable = true,
                    IsChecked = !_fitMode && Math.Abs(Editor.Zoom - z) < 0.001,
                };
                item.Click += (_, _) => SetZoom(level);
                ZoomMenu.Items.Add(item);
            }
            ZoomMenu.PlacementTarget = ZoomLevelBtn;
            ZoomMenu.Placement = PlacementMode.Top;
            ZoomMenu.IsOpen = true;
        }
    }
}
