using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Shapes;
using KillerFamily.Layout;

// The footer's view cell, as KillerPDF has it: icon radio buttons with the accent underline on the
// one in use. They pick the library's view (F5 to F8) and its grouping, and with a picture open
// they bring the library up in that view. Strip positioning and side views live in the rail.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private static readonly double[] ZoomMenuSteps = [0.25, 0.5, 0.75, 1, 1.5, 2, 4];
        private static readonly (DockSide Side, string Key, string Arrow)[] StripSides =
        [
            (DockSide.Left, "Str_Dock_Left", "Left"), (DockSide.Top, "Str_Dock_Top", "Up"),
            (DockSide.Right, "Str_Dock_Right", "Right"), (DockSide.Bottom, "Str_Dock_Bottom", "Down"),
        ];

        private readonly List<(RadioButton Button, string View)> _footerViews = [];
        private readonly List<(RadioButton Button, string Group)> _footerGroups = [];
        private readonly List<(RadioButton Button, string View)> _footerStripViews = [];
        private Button? _stripButton;
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
                var button = FooterRadio("FooterLibraryView", glyph, key, keys, () => ShowLibraryView(target));
                _footerViews.Add((button, view));
                FooterLibraryViews.Children.Add(button);
            }
            FooterLibraryViews.Children.Add(FooterSeparator());
            foreach (var (group, glyph, key) in new[] { ("date", "\uE787", "Str_Lib_ByDate"), ("collection", "\uE8B7", "Str_Lib_ByCollection") })
            {
                string target = group;
                var button = FooterRadio("FooterLibraryGroup", glyph, key, null, () => ShowLibraryGroup(target));
                _footerGroups.Add((button, group));
                FooterLibraryViews.Children.Add(button);
            }

            foreach (var (side, key) in new[] { (DockSide.Top, "Str_Dock_Top"), (DockSide.Bottom, "Str_Dock_Bottom") })
            {
                var target = side;
                object icon = DockIcon(side, typeof(RadioButton));
                var button = FooterRadio("FooterStripView", icon, key, null, () =>
                {
                    StripDock.DockTo(target);
                    if (StripDock.IsMinimized) StripDock.Restore();
                    SyncFooterView();
                });
                _footerStripViews.Add((button, side.ToString()));
                FooterStripControls.Children.Add(button);
            }
            FooterStripControls.Children.Add(FooterSeparator());

            _stripButton = StripRailButton(DockIcon(StripDock.Side, typeof(Button)), "Str_Dock_Menu");
            _stripButton.Click += (_, _) => ShowStripMenu();
            RailStack.Children.Insert(0, _stripButton);

            if (_strip != null) _strip.SideViewChanged += SyncFooterView;
            Library.ViewChanged += SyncFooterView;
            StripDock.LayoutChanged += SyncFooterView;
            NavigationDock.LayoutChanged += SyncFooterView;
            SyncFooterView();
        }

        private Button StripRailButton(object icon, string key)
        {
            // These geometry icons use the same rail size and states as font glyphs.
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var style = new Style(typeof(Button), (Style)FindResource("RailButton"));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
            foreach (var state in new[] { new Trigger { Property = IsMouseOverProperty, Value = true }, new Trigger { Property = TagProperty, Value = "on" } })
            {
                state.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("SelectionAccent")));
                style.Triggers.Add(state);
            }
            var button = new Button { Content = icon, Style = style,
                Margin = new Thickness(0, 0, 0, 6) };
            button.SetResourceReference(ToolTipProperty, key);
            button.SetResourceReference(AutomationProperties.NameProperty, key);
            return button;
        }

        /// <summary>One radio button: a glyph, a tooltip that follows the language with its shortcut
        /// after it, and the same name for a screen reader. Click, not Checked, so the one already
        /// in use still brings the library up over a picture.</summary>
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
            button.Click += (_, _) => { if (!_footerSyncing) pick(); };
            return button;
        }

        /// <summary>A view from the footer or F5 to F8, with the library brought up over a picture.</summary>
        private void ShowLibraryView(string view)
        {
            Library.SetView(view);
            if (_doc != null && !_libraryShown) ToggleLibrary();
            SyncFooterView();
        }

        private void ShowLibraryGroup(string group)
        {
            Library.SetGroup(group);
            if (_doc != null && !_libraryShown) ToggleLibrary();
            SyncFooterView();
        }

        private static Border FooterSeparator()
        {
            var line = new Border { Width = 1, Height = 14, Margin = new Thickness(5, 0, 5, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            return line;
        }

        private static Binding IconColor(Type owner) => new(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, owner, 1),
        };

        /// <summary>A small window outline with a solid bar on the side the strip docks to, drawn in
        /// its button's or menu item's own color so hover and the accent reach it.</summary>
        private static Grid DockIcon(DockSide side, Type owner)
        {
            var icon = new Grid { Width = 14, Height = 11 };
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetBinding(Border.BorderBrushProperty, IconColor(owner));
            var bar = new Rectangle();
            bar.SetBinding(Shape.FillProperty, IconColor(owner));
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

        /// <summary>Three rows of a thumbnail and a line: the thumbnail list.</summary>
        private static StackPanel ListIcon(Type owner)
        {
            var icon = new StackPanel { Width = 14 };
            for (int i = 0; i < 3; i++)
            {
                var row = new DockPanel { Height = 3, Margin = new Thickness(0, i == 0 ? 0 : 1, 0, 0), LastChildFill = true };
                var thumb = new Rectangle { Width = 4 };
                thumb.SetBinding(Shape.FillProperty, IconColor(owner));
                var line = new Rectangle { Height = 1, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                line.SetBinding(Shape.FillProperty, IconColor(owner));
                row.Children.Add(thumb);
                row.Children.Add(line);
                icon.Children.Add(row);
            }
            return icon;
        }

        /// <summary>Two cards leaning back behind a front card: the rolodex.</summary>
        private static Grid RolodexIcon(Type owner)
        {
            var icon = new Grid { Width = 14, Height = 11 };
            foreach (var (inset, top, height) in new[] { (3.0, 0.0, 3.0), (1.5, 2.0, 3.0), (0.0, 4.0, 7.0) })
            {
                var card = new Border
                {
                    Margin = new Thickness(inset, top, inset, 0), Height = height, VerticalAlignment = VerticalAlignment.Top,
                    BorderThickness = new Thickness(1, 1, 1, inset == 0 ? 1 : 0),
                };
                card.SetBinding(Border.BorderBrushProperty, IconColor(owner));
                icon.Children.Add(card);
            }
            return icon;
        }

        /// <summary>The strip's flyout: the four sides with their shortcuts, and Minimize.</summary>
        private void ShowStripMenu()
        {
            if (_stripButton == null) return;
            var menu = new ContextMenu { PlacementTarget = _stripButton,
                Placement = NavigationDock.Side switch { DockSide.Left => PlacementMode.Right, DockSide.Right => PlacementMode.Left,
                    DockSide.Top => PlacementMode.Bottom, _ => PlacementMode.Top } };
            menu.HorizontalOffset = 4;
            foreach (var (side, key, arrow) in StripSides)
            {
                var item = new MenuItem
                {
                    Header = Loc(key), InputGestureText = "Alt+Shift+" + arrow,
                    Icon = DockIcon(side, typeof(MenuItem)),
                    IsCheckable = true, IsChecked = (side is DockSide.Left or DockSide.Right ? NavigationDock : StripDock).Side == side,
                    IsEnabled = side is DockSide.Left or DockSide.Right || !Library.IsVisible,
                };
                DockSide target = side;
                item.Click += (_, _) => (target is DockSide.Left or DockSide.Right ? NavigationDock : StripDock).DockTo(target);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var minimizeHost = Library.IsVisible ? NavigationDock : StripDock;
            var arrowOfSide = StripSides.First(s => s.Side == minimizeHost.Side).Arrow;
            var minimize = new MenuItem
            {
                Header = Loc(minimizeHost.IsMinimized ? "Str_Dock_Restore" : "Str_Dock_Minimize"),
                InputGestureText = "Alt+Shift+" + arrowOfSide,
            };
            minimize.Click += (_, _) => { if (minimizeHost.IsMinimized) minimizeHost.Restore(); else minimizeHost.Minimize(); };
            menu.Items.Add(minimize);
            menu.IsOpen = true;
        }

        /// <summary>Shows the buttons that apply and marks the ones in use.</summary>
        private void SyncFooterView()
        {
            bool library = Library.Visibility == Visibility.Visible;
            bool picture = !library && _doc != null;
            FooterStripControls.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            FooterZoomControls.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            if (_stripButton != null)
            {
                _stripButton.Visibility = Visibility.Visible;
                _stripButton.Content = DockIcon(picture ? StripDock.Side : NavigationDock.Side, typeof(Button));
            }
            _footerSyncing = true;
            try
            {
                string stripView = StripDock.Side.ToString();
                foreach (var (button, view) in _footerStripViews) button.IsChecked = picture && stripView == view;
                // Over a picture none is underlined: the library is not what is showing.
                foreach (var (button, view) in _footerViews) button.IsChecked = library && Library.CurrentView == view;
                // The timeline is a line of days, so it is always by date.
                bool grouped = Library.CurrentView != "timeline";
                foreach (var (button, group) in _footerGroups)
                {
                    button.IsEnabled = grouped;
                    button.IsChecked = library && Library.CurrentGroup == group;
                }
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
