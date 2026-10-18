using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell
{
    /// <summary>
    /// The collection strip: one collection of the library (or the Screenshots folder) beside the
    /// open picture, so the next one is a click or an arrow key away. Docked at the top or bottom
    /// it is a carousel; on the left or right it is a column of thumbnails. It lives in a
    /// DockHost, which moves it between the sides and folds it to a bar.
    /// </summary>
    internal sealed class CollectionStrip : Border
    {
        /// <summary>A picture was chosen in the strip and should open in the editor.</summary>
        public event Action<CaptureItem>? OpenRequested;

        private readonly DockHost _host;
        private readonly ComboBox _picker = new() { MinWidth = 150, MaxWidth = 260, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button _dockBtn, _minBtn;
        private readonly Grid _body = new();
        private readonly CoverFlow _flow;
        private readonly Slider _slider;
        private readonly ListBox _list;
        private readonly DispatcherTimer _openDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
        private IReadOnlyList<CaptureItem> _items = [];
        private string _source = "all";          // all, c<id>, c0 (not in a collection), folder
        private bool _syncing;
        private long? _current;

        public CollectionStrip(DockHost host, Style sliderStyle)
        {
            _host = host;
            SetResourceReference(BackgroundProperty, "ContentPaneBrush");
            SetResourceReference(BorderBrushProperty, "PaneBorderBrush");
            BorderThickness = new Thickness(1);
            SetResourceReference(CornerRadiusProperty, "PanelCornerRadius");
            Padding = new Thickness(8, 6, 8, 6);
            SetResourceReference(MarginProperty, "ContentPaneMargin");
            Focusable = false;

            _count.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _picker.SelectionChanged += (_, _) =>
            {
                if (_syncing || _picker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
                _source = tag;
                Load(keepCurrent: true);
            };
            _dockBtn = HeaderButton("", ShowDockMenu);
            _minBtn = HeaderButton("", () => _host.Minimize());

            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(_minBtn, Dock.Right);
            DockPanel.SetDock(_dockBtn, Dock.Right);
            header.Children.Add(_minBtn);
            header.Children.Add(_dockBtn);
            header.Children.Add(_picker);
            header.Children.Add(_count);

            _flow = new CoverFlow { ThumbnailSource = CaptureLibrary.LoadThumb, Describe = LibraryView.Describe, ViewHeight = 1.2 };
            _flow.SelectionChanged += () => { if (!_syncing) QueueOpen(_flow.SelectedItem); };
            _flow.ItemActivated += item => Open(item);
            _slider = new Slider { Style = sliderStyle, Minimum = 0, Focusable = false, IsTabStop = false, Margin = new Thickness(40, 2, 40, 0) };
            _slider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(CoverFlow.MaximumIndex)) { Source = _flow });
            _slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(CoverFlow.SelectedIndex)) { Source = _flow, Mode = BindingMode.TwoWay });

            _list = new ListBox
            {
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "xmlns:s=\"clr-namespace:KillerShot.Shell;assembly=KillerShot\"><s:StripThumb/></DataTemplate>"),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            _list.SelectionChanged += (_, _) => { if (!_syncing) QueueOpen(_list.SelectedItem as CaptureItem); };
            _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PickerRow");

            _openDelay.Tick += (_, _) =>
            {
                _openDelay.Stop();
                if (_openDelay.Tag is CaptureItem item) Open(item);
            };

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(_body);
            Child = root;

            _host.SideChanged += Relayout;
            CaptureLibrary.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Load(keepCurrent: true); }));
            FolderCaptures.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && _source == "folder") Load(keepCurrent: true); }));
            IsVisibleChanged += (_, _) => { if (IsVisible) Load(keepCurrent: true); };
            Relayout();
            Relocalize();
        }

        private Button HeaderButton(string glyph, Action click)
        {
            var b = new Button
            {
                Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12,
                Width = 26, Height = 24, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
            };
            b.SetResourceReference(StyleProperty, "ToolbarButton");
            b.Click += (_, _) => click();
            return b;
        }

        /// <summary>Carousel across the top or bottom, a thumbnail column down the sides.</summary>
        private void Relayout()
        {
            _body.Children.Clear();
            _body.RowDefinitions.Clear();
            if (_host.IsHorizontal)
            {
                _body.RowDefinitions.Add(new RowDefinition());
                _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(_slider, 1);
                _body.Children.Add(_flow);
                _body.Children.Add(_slider);
            }
            else _body.Children.Add(_list);
            _minBtn.Content = _host.Side switch
            {
                DockSide.Left => "", DockSide.Right => "",
                DockSide.Top => "", _ => "",
            };
            Sync();
        }

        public void Relocalize()
        {
            _dockBtn.ToolTip = Loc("Str_Dock_Menu");
            AutomationProperties.SetName(_dockBtn, Loc("Str_Dock_Menu"));
            _minBtn.ToolTip = Loc("Str_Dock_Minimize") + "  (Alt+Shift+" + Arrow(_host.Side) + ")";
            AutomationProperties.SetName(_minBtn, Loc("Str_Dock_Minimize"));
            AutomationProperties.SetName(_picker, Loc("Str_Strip_Collection"));
            AutomationProperties.SetName(_list, Loc("Str_Strip_Collection"));
            _host.RestoreText = Loc("Str_Dock_Restore");
            _host.ResizeText = Loc("Str_Dock_Resize");
            if (IsVisible) Load(keepCurrent: true);
        }

        private static string Arrow(DockSide side) => side switch
        {
            DockSide.Left => "Left", DockSide.Top => "Up", DockSide.Right => "Right", _ => "Down",
        };

        private void ShowDockMenu()
        {
            var menu = new ContextMenu { PlacementTarget = _dockBtn, Placement = PlacementMode.Bottom };
            foreach (var (side, key) in new[] { (DockSide.Left, "Str_Dock_Left"), (DockSide.Top, "Str_Dock_Top"), (DockSide.Right, "Str_Dock_Right"), (DockSide.Bottom, "Str_Dock_Bottom") })
            {
                var mi = new MenuItem { Header = Loc(key), InputGestureText = "Alt+Shift+" + Arrow(side), IsCheckable = true, IsChecked = _host.Side == side };
                var target = side;
                mi.Click += (_, _) => _host.DockTo(target);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var min = new MenuItem { Header = Loc("Str_Dock_Minimize"), InputGestureText = "Alt+Shift+" + Arrow(_host.Side) };
            min.Click += (_, _) => _host.Minimize();
            menu.Items.Add(min);
            menu.IsOpen = true;
            Anim.FadeIn(menu);
        }

        /// <summary>Follows the picture in the editor: its collection, with it selected.</summary>
        public void Follow(CaptureItem? item, string? filePath)
        {
            if (item == null && filePath != null && IsInFolder(filePath))
                item = FolderCaptures.Items().FirstOrDefault(i => string.Equals(FolderCaptures.PathOf(i.Id), filePath, StringComparison.OrdinalIgnoreCase));
            _current = item?.Id;
            if (item != null)
            {
                string want = item.CollectionId == FolderCaptures.CollectionId ? "folder"
                    : _source == "all" ? "all"
                    : item.CollectionId is long cid ? "c" + cid.ToString(CultureInfo.InvariantCulture) : "c0";
                _source = want;
            }
            if (IsVisible) Load(keepCurrent: true);
        }

        private static bool IsInFolder(string path) =>
            string.Equals(System.IO.Path.GetDirectoryName(path)?.TrimEnd('\\'), FolderCaptures.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Fills the picker and the strip for the chosen source.</summary>
        private void Load(bool keepCurrent)
        {
            _syncing = true;
            try
            {
                var collections = CaptureLibrary.Collections();
                _picker.Items.Clear();
                void Add(string tag, string text) => _picker.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
                Add("all", Loc("Str_Strip_All"));
                if (FolderCaptures.Enabled) Add("folder", FolderCaptures.Name);
                foreach (var c in collections) Add("c" + c.Id.ToString(CultureInfo.InvariantCulture), c.Name);
                Add("c0", Loc("Str_Lib_Unfiled"));
                var chosen = _picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _source);
                if (chosen == null) { _source = "all"; chosen = (ComboBoxItem)_picker.Items[0]; }
                _picker.SelectedItem = chosen;

                _items = _source switch
                {
                    "folder" => FolderCaptures.Items(),
                    "c0" => CaptureLibrary.Items().Where(i => i.CollectionId == null || !collections.Any(c => c.Id == i.CollectionId)).ToList(),
                    "all" => CaptureLibrary.Items(),
                    _ => CaptureLibrary.Items().Where(i => "c" + i.CollectionId?.ToString(CultureInfo.InvariantCulture) == _source).ToList(),
                };
                _count.Text = string.Format(CultureInfo.CurrentCulture, Loc(_items.Count == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), _items.Count);
                _flow.Items = _items;
                _list.ItemsSource = _items;
                Sync();
            }
            finally { _syncing = false; }
        }

        /// <summary>Selects the picture in the editor without opening it again.</summary>
        private void Sync()
        {
            bool was = _syncing;
            _syncing = true;
            try
            {
                int index = _current is long id ? IndexOf(id) : -1;
                if (index >= 0)
                {
                    _flow.SelectedIndex = index;
                    _list.SelectedIndex = index;
                    _list.ScrollIntoView(_items[index]);
                }
                else _list.SelectedIndex = -1;
            }
            finally { _syncing = was; }
        }

        private int IndexOf(long id)
        {
            for (int i = 0; i < _items.Count; i++) if (_items[i].Id == id) return i;
            return -1;
        }

        /// <summary>Arrow keys and the slider move through the strip quickly; the picture opens
        /// once the selection rests for a moment.</summary>
        private void QueueOpen(CaptureItem? item)
        {
            if (item == null || item.Id == _current) return;
            _openDelay.Stop();
            _openDelay.Tag = item;
            _openDelay.Start();
        }

        private void Open(CaptureItem item)
        {
            _openDelay.Stop();
            if (item.Id == _current) return;
            OpenRequested?.Invoke(item);
        }

        /// <summary>For the self-test: show a source by its tag.</summary>
        internal void TestShow(string source)
        {
            _source = source;
            Load(keepCurrent: true);
        }

        private static string Loc(string key) => LocaleManager.Loc(key);
    }

    /// <summary>One thumbnail in the strip's column, loaded off the interface thread when the
    /// virtualized list shows it.</summary>
    public sealed class StripThumb : StackPanel
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _caption = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };

        public StripThumb()
        {
            Margin = new Thickness(0, 2, 0, 6);
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            Children.Add(_image);
            Children.Add(_caption);
            DataContextChanged += (_, _) => Load();
        }

        private void Load()
        {
            _image.Source = null;
            if (DataContext is not CaptureItem item) return;
            _caption.Text = LibraryView.Title(item);
            AutomationProperties.SetName(this, LibraryView.Describe(item));
            ToolTip = LibraryView.Describe(item);
            long id = item.Id;
            Task.Run(() => CaptureLibrary.LoadThumb(id)).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && DataContext is CaptureItem now && now.Id == id) _image.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
