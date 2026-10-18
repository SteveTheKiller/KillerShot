using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerShot.Shell;

namespace KillerShot.Controls
{
    // Customize toolbar, as in KillerPDF: the toolbar's buttons in order, with a marker where the
    // left side ends and the right side begins, beside the More menu (the chevron at the end of
    // the bar). Buttons move between the two lists and up and down by button, double-click or
    // drag, and separators can be added and removed. Nothing changes until OK.
    public partial class CustomizeToolbarDialog : Window
    {
        private enum EntryKind { Button, Separator, Space }

        private sealed record Entry(EntryKind Kind, MainWindow.ToolbarUnit? Unit = null);

        private readonly ListBox _bar = new();
        private readonly ListBox _more = new();
        private readonly Button _toMore;
        private readonly Button _toBar;
        private readonly Button _up;
        private readonly Button _down;
        private readonly Button _addSeparator;
        private readonly Button _removeSeparator;
        private readonly IReadOnlyDictionary<string, MainWindow.ToolbarUnit> _units;
        private readonly MainWindow.ToolbarLayout _defaults;
        private readonly Func<string, FrameworkElement> _icon;
        private readonly Func<string, string> _loc;
        private ListBox _active;
        private Point _dragStart;
        private ListBoxItem? _dragItem;

        internal MainWindow.ToolbarLayout? Result { get; private set; }

        internal CustomizeToolbarDialog(IReadOnlyList<MainWindow.ToolbarUnit> units, MainWindow.ToolbarLayout layout,
            MainWindow.ToolbarLayout defaults, Func<string, FrameworkElement> icon, Func<string, string> loc)
        {
            InitializeComponent();
            var titleFrames = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/kst-icon.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            TitleIcon.Source = titleFrames[0];
            Loaded += (_, _) => Anim.FadeIn(RootBorder);

            _units = units.ToDictionary(unit => unit.Id);
            _defaults = defaults;
            _icon = icon;
            _loc = loc;
            _active = _bar;
            Title = "KillerShot - " + loc("Str_CustomizeToolbar_Title");
            SubtitleText.Text = loc("Str_CustomizeToolbar_Title");
            ResetButton.Content = loc("Str_CustomizeToolbar_Reset");
            CancelButton.Content = loc("Str_Btn_Cancel");
            OkButton.Content = loc("Str_Btn_OK");

            var body = new DockPanel { LastChildFill = true };
            var hint = new TextBlock
            {
                Text = loc("Str_CustomizeToolbar_Hint"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            DockPanel.SetDock(hint, Dock.Top);
            body.Children.Add(hint);

            _toMore = SideButton("Str_CustomizeToolbar_ToMore", () => MoveAcross(_bar, _more));
            _toBar = SideButton("Str_CustomizeToolbar_ToBar", () => MoveAcross(_more, _bar));
            _up = SideButton("Str_CustomizeToolbar_MoveUp", () => MoveSelected(-1));
            _down = SideButton("Str_CustomizeToolbar_MoveDown", () => MoveSelected(+1));
            _addSeparator = SideButton("Str_CustomizeToolbar_AddSeparator", AddSeparator);
            _removeSeparator = SideButton("Str_CustomizeToolbar_RemoveSeparator", RemoveSeparator);
            var side = new StackPanel { Margin = new Thickness(10, 22, 10, 0), Width = 170 };
            side.Children.Add(_toMore);
            side.Children.Add(_toBar);
            side.Children.Add(new Border { Height = 14 });
            foreach (Button button in new[] { _up, _down, _addSeparator, _removeSeparator })
                side.Children.Add(button);

            var lists = new Grid();
            lists.ColumnDefinitions.Add(new ColumnDefinition());
            lists.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lists.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement barColumn = ListColumn("Str_CustomizeToolbar_Toolbar", _bar);
            FrameworkElement moreColumn = ListColumn("Str_CustomizeToolbar_More", _more);
            Grid.SetColumn(side, 1);
            Grid.SetColumn(moreColumn, 2);
            lists.Children.Add(barColumn);
            lists.Children.Add(side);
            lists.Children.Add(moreColumn);
            body.Children.Add(lists);
            BodyHost.Children.Add(body);

            Load(layout);
        }

        private Button SideButton(string key, Action click)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = _loc(key), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Style = (Style)FindResource("SurfaceButton"),
                MinHeight = 28,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, _loc(key));
            button.Click += (_, _) => click();
            return button;
        }

        private FrameworkElement ListColumn(string headerKey, ListBox list)
        {
            var column = new DockPanel();
            var header = new TextBlock
            {
                Text = _loc(headerKey),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DockPanel.SetDock(header, Dock.Top);
            column.Children.Add(header);

            // Each list sits in a raised pane like the About card's: rounded, grained and on the
            // pane shadow. The list itself is transparent so the pane shows through.
            list.Background = Brushes.Transparent;
            list.BorderThickness = new Thickness(0);
            list.Padding = new Thickness(2, 4, 2, 4);
            list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            list.AllowDrop = true;
            // Rows fit the list's width: long captions trim instead of scrolling sideways.
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            System.Windows.Automation.AutomationProperties.SetName(list, _loc(headerKey));
            list.GotKeyboardFocus += (_, _) => Activate(list);
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not null) Activate(list);
                SyncButtons();
            };
            list.MouseDoubleClick += (_, e) =>
            {
                if (ItemUnder(list, e.OriginalSource) is null) return;
                if (ReferenceEquals(list, _bar)) MoveAcross(_bar, _more);
                else MoveAcross(_more, _bar);
            };
            list.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _dragStart = e.GetPosition(this);
                _dragItem = ItemUnder(list, e.OriginalSource);
            };
            list.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
                Vector moved = e.GetPosition(this) - _dragStart;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                ListBoxItem item = _dragItem;
                _dragItem = null;
                DragDrop.DoDragDrop(item, item, DragDropEffects.Move);
            };
            list.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetData(typeof(ListBoxItem)) is ListBoxItem dragged && CanDrop(dragged, list)
                    ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            list.Drop += (_, e) =>
            {
                if (e.Data.GetData(typeof(ListBoxItem)) is not ListBoxItem dragged || !CanDrop(dragged, list)) return;
                var source = (ListBox)ItemsControl.ItemsControlFromItemContainer(dragged);
                ListBoxItem? target = ItemUnder(list, e.OriginalSource);
                int index = target is null ? list.Items.Count : list.Items.IndexOf(target);
                if (target is not null && e.GetPosition(target).Y > target.ActualHeight / 2) index++;
                int from = source.Items.IndexOf(dragged);
                source.Items.Remove(dragged);
                if (ReferenceEquals(source, list) && from < index) index--;
                list.Items.Insert(Math.Clamp(index, 0, list.Items.Count), dragged);
                list.SelectedItem = dragged;
                Activate(list);
                SyncButtons();
            };

            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            var layers = new Grid();
            layers.Children.Add(grain);
            layers.Children.Add(list);
            var pane = new Border { BorderThickness = new Thickness(1), Child = layers };
            pane.SetResourceReference(Border.BackgroundProperty, "AboutPanelBrush");
            pane.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            pane.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 2, Direction = 270 };
            if (TryFindResource("FlyoutShadowOpacity") is double opacity) shadow.Opacity = opacity;
            var shadowHost = new Border { IsHitTestVisible = false, Effect = shadow };
            shadowHost.SetResourceReference(Border.BackgroundProperty, "AboutPanelBrush");
            shadowHost.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            var host = new Grid();
            host.Children.Add(shadowHost);
            host.Children.Add(pane);
            column.Children.Add(host);
            return column;
        }

        // Separators and the side marker belong to the toolbar list only.
        private bool CanDrop(ListBoxItem dragged, ListBox target) =>
            ReferenceEquals(target, _bar) || (dragged.Tag is Entry { Kind: EntryKind.Button });

        private static ListBoxItem? ItemUnder(ListBox list, object source)
        {
            for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, list);
                 node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
                if (node is ListBoxItem item) return item;
            return null;
        }

        private void Activate(ListBox list)
        {
            if (ReferenceEquals(_active, list)) return;
            _active = list;
            (ReferenceEquals(list, _bar) ? _more : _bar).SelectedItem = null;
            SyncButtons();
        }

        private void Load(MainWindow.ToolbarLayout layout)
        {
            _bar.Items.Clear();
            _more.Items.Clear();
            foreach (string id in layout.Left) AddBarEntry(id);
            _bar.Items.Add(Row(new Entry(EntryKind.Space)));
            foreach (string id in layout.Right) AddBarEntry(id);
            foreach (string id in layout.More)
                if (_units.TryGetValue(id, out var unit)) _more.Items.Add(Row(new Entry(EntryKind.Button, unit)));
            _active = _bar;
            SyncButtons();
        }

        private void AddBarEntry(string id)
        {
            if (id == MainWindow.ToolbarSeparatorToken) _bar.Items.Add(Row(new Entry(EntryKind.Separator)));
            else if (_units.TryGetValue(id, out var unit)) _bar.Items.Add(Row(new Entry(EntryKind.Button, unit)));
        }

        private ListBoxItem Row(Entry entry)
        {
            FrameworkElement content;
            string name;
            switch (entry.Kind)
            {
                case EntryKind.Button:
                {
                    var icon = new Border { Width = 24, Child = _icon(entry.Unit!.Id) };
                    name = _loc(entry.Unit.LabelKey);
                    var text = new TextBlock
                    {
                        Text = name,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var row = new DockPanel();
                    DockPanel.SetDock(icon, Dock.Left);
                    row.Children.Add(icon);
                    row.Children.Add(text);
                    content = row;
                    break;
                }
                case EntryKind.Space:
                {
                    // The side marker: an accent rule above and below a caption that wraps to the
                    // list's width, so a long translation never runs off the row.
                    name = _loc("Str_CustomizeToolbar_Space");
                    var caption = new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        Margin = new Thickness(0, 3, 0, 3),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                    };
                    caption.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                    var band = new Border { BorderThickness = new Thickness(0, 1, 0, 1), Child = caption };
                    band.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
                    content = band;
                    break;
                }
                default:
                {
                    name = _loc("Str_CustomizeToolbar_Separator");
                    var grid = new Grid { MinHeight = 18 };
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    foreach (int column in new[] { 0, 2 })
                    {
                        var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center };
                        line.SetResourceReference(Shape.FillProperty, "CardBorderBrush");
                        Grid.SetColumn(line, column);
                        grid.Children.Add(line);
                    }
                    var caption = new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        Margin = new Thickness(8, 0, 8, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    Grid.SetColumn(caption, 1);
                    grid.Children.Add(caption);
                    content = grid;
                    break;
                }
            }
            var item = new ListBoxItem
            {
                Content = content,
                Tag = entry,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, name);
            item.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return item;
        }

        private Entry? SelectedEntry => (_active.SelectedItem as ListBoxItem)?.Tag as Entry;

        private void SyncButtons()
        {
            Entry? entry = SelectedEntry;
            bool inBar = ReferenceEquals(_active, _bar);
            int index = _active.SelectedIndex;
            _toMore.IsEnabled = inBar && entry?.Kind == EntryKind.Button;
            _toBar.IsEnabled = !inBar && entry is not null;
            _up.IsEnabled = entry is not null && index > 0;
            _down.IsEnabled = entry is not null && index >= 0 && index < _active.Items.Count - 1;
            _addSeparator.IsEnabled = inBar;
            _removeSeparator.IsEnabled = inBar && entry?.Kind == EntryKind.Separator;
        }

        /// <summary>Moves the selected button to the other list: into the More menu at the end, or
        /// onto the toolbar just above the side marker, the end of the left side.</summary>
        private void MoveAcross(ListBox from, ListBox to)
        {
            if (from.SelectedItem is not ListBoxItem { Tag: Entry { Kind: EntryKind.Button } } item) return;
            int index = from.SelectedIndex;
            from.Items.Remove(item);
            if (ReferenceEquals(to, _bar))
            {
                int space = _bar.Items.Cast<ListBoxItem>().ToList().FindIndex(row => row.Tag is Entry { Kind: EntryKind.Space });
                _bar.Items.Insert(space < 0 ? _bar.Items.Count : space, item);
            }
            else to.Items.Add(item);
            if (from.Items.Count > 0) from.SelectedIndex = Math.Min(index, from.Items.Count - 1);
            to.SelectedItem = item;
            Activate(to);
            to.ScrollIntoView(item);
            SyncButtons();
        }

        private void MoveSelected(int step)
        {
            if (_active.SelectedItem is not ListBoxItem item) return;
            int index = _active.SelectedIndex, target = index + step;
            if (target < 0 || target >= _active.Items.Count) return;
            _active.Items.RemoveAt(index);
            _active.Items.Insert(target, item);
            _active.SelectedIndex = target;
            _active.ScrollIntoView(item);
            SyncButtons();
        }

        private void AddSeparator()
        {
            int index = _bar.SelectedIndex < 0 ? _bar.Items.Count : _bar.SelectedIndex + 1;
            ListBoxItem item = Row(new Entry(EntryKind.Separator));
            _bar.Items.Insert(index, item);
            _bar.SelectedItem = item;
            SyncButtons();
        }

        private void RemoveSeparator()
        {
            if (_bar.SelectedItem is not ListBoxItem { Tag: Entry { Kind: EntryKind.Separator } } item) return;
            int index = _bar.SelectedIndex;
            _bar.Items.Remove(item);
            if (_bar.Items.Count > 0) _bar.SelectedIndex = Math.Min(index, _bar.Items.Count - 1);
            SyncButtons();
        }

        private MainWindow.ToolbarLayout BuildLayout()
        {
            var left = new List<string>();
            var right = new List<string>();
            List<string> current = left;
            foreach (ListBoxItem row in _bar.Items)
            {
                switch (row.Tag)
                {
                    case Entry { Kind: EntryKind.Space }: current = right; break;
                    case Entry { Kind: EntryKind.Separator }: current.Add(MainWindow.ToolbarSeparatorToken); break;
                    case Entry { Kind: EntryKind.Button, Unit: { } unit }: current.Add(unit.Id); break;
                }
            }
            var more = _more.Items.Cast<ListBoxItem>()
                .Select(row => (row.Tag as Entry)?.Unit?.Id).OfType<string>().ToList();
            return new MainWindow.ToolbarLayout(left, right, more);
        }

        private void Reset_Click(object sender, RoutedEventArgs e) => Load(_defaults);

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            Result = BuildLayout();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
