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
    /// DockHost, which moves it between the sides and folds it away behind the rail's chevron.
    /// </summary>
    internal sealed class CollectionStrip : Border
    {
        /// <summary>A picture was chosen in the strip and should open in the editor.</summary>
        public event Action<CaptureItem>? OpenRequested;
        public event Action<CaptureItem, FrameworkElement>? ItemMenuRequested;

        /// <summary>The side view changed between the thumbnail list and the rolodex.</summary>
        public event Action? SideViewChanged;

        /// <summary>How a left or right strip shows its captures: "list" or "rolodex".</summary>
        public string SideView => _sideView;

        internal FrameworkElement TestCaption => _caption;
        internal FrameworkElement TestCarousel => _flow;

        private readonly DockHost _host;
        private readonly ComboBox _picker = new() { MinWidth = 150, MaxWidth = 260, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _caption = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        // A single caption row leaves the strip's width to its pictures.
        private readonly DockPanel _root = new();
        private readonly DockPanel _header = new() { LastChildFill = true };
        private readonly Grid _body = new();
        private readonly CoverFlow _flow;
        private readonly Slider _slider;
        private readonly ListBox _list;
        private readonly Rolodex _rolodex;
        private string _sideView = Settings.Get("StripView") == "rolodex" ? "rolodex" : "list";
        private readonly DispatcherTimer _openDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
        private IReadOnlyList<CaptureItem> _items = [];
        private string _source = "all";          // all, c<id>, c0 (not in a collection), folder
        private bool _syncing;
        private long? _current;

        public CollectionStrip(DockHost host, Style sliderStyle)
        {
            _host = host;
            // No pane of its own: the strip sits on the window's own background and grain, as
            // KillerPDF's sidebar does, so its cards are the only surface in it.
            Background = Brushes.Transparent;
            Padding = new Thickness(8, 6, 8, 6);
            SetResourceReference(MarginProperty, "ContentPaneMargin");
            Focusable = false;
            Panel.SetZIndex(this, 1);   // above the splitter, so the slider's overhang stays clickable

            _count.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _picker.SelectionChanged += (_, _) =>
            {
                if (_syncing || _picker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
                _source = tag;
                Load(keepCurrent: true);
            };
            _flow = new CoverFlow { ThumbnailSource = CaptureLibrary.LoadThumb, Describe = LibraryView.Describe, ViewHeight = 1.2 };
            _flow.SelectionChanged += () => { if (!_syncing) QueueOpen(_flow.SelectedItem); };
            _flow.ItemActivated += item => Open(item);
            _flow.ItemMenuRequested += ShowItemMenu;
            // The slider runs along the strip's bottom edge and hangs a little over it (past the 6 px
            // padding and the border), so the carousel keeps the height.
            _slider = new Slider { Style = sliderStyle, Minimum = 0, Focusable = false, IsTabStop = false, Margin = new Thickness(40, 0, 40, -14) };
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
            _list.MouseRightButtonUp += (_, e) =>
            {
                if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(_list, source) is not ListBoxItem { DataContext: CaptureItem item } row) return;
                ShowItemMenu(item, row);
                e.Handled = true;
            };
            _list.KeyDown += (_, e) =>
            {
                bool contextKey = e.Key == Key.Apps || (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    && (e.Key == Key.F10 || e.Key == Key.System && e.SystemKey == Key.F10);
                if (!contextKey || _list.SelectedItem is not CaptureItem item) return;
                ShowItemMenu(item, _list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement ?? _list);
                e.Handled = true;
            };

            _rolodex = new Rolodex
            {
                ThumbnailSource = CaptureLibrary.LoadThumb, Caption = LibraryView.Title, Describe = LibraryView.Describe,
                CardColor = CardColors.StripCard(), Accent = item => CardColors.Collection(item.CollectionId),
            };
            CardColors.Changed += () =>
            {
                _rolodex.CardColor = CardColors.StripCard();
                _rolodex.Refresh();
                _list.Items.Refresh();
            };
            if (double.TryParse(Settings.Get("RolodexZoom"), NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)) _rolodex.Zoom = zoom;
            _rolodex.ZoomChanged += () => Settings.Set("RolodexZoom", _rolodex.Zoom.ToString("0.###", CultureInfo.InvariantCulture));
            _rolodex.SelectionChanged += () => { if (!_syncing) QueueOpen(_rolodex.SelectedItem); };
            _rolodex.ItemActivated += item => Open(item);
            _rolodex.ItemMenuRequested += ShowItemMenu;

            _openDelay.Tick += (_, _) =>
            {
                if (_host.IsHorizontal ? _flow.IsMoving : _sideView == "rolodex" && _rolodex.IsMoving) return;
                _openDelay.Stop();
                if (_openDelay.Tag is CaptureItem item) Open(item);
            };

            Child = _root;

            _host.SideChanged += Relayout;
            CaptureLibrary.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Load(keepCurrent: true); }));
            FolderCaptures.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && _source == "folder") Load(keepCurrent: true); }));
            IsVisibleChanged += (_, _) => { if (IsVisible) Load(keepCurrent: true); };
            Relayout();
            Relocalize();
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
            else _body.Children.Add(_sideView == "rolodex" ? _rolodex : _list);

            _root.Children.Clear();
            _header.Children.Clear();
            _header.Margin = new Thickness(0, 0, 0, 4);
            _count.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(_count, Dock.Right);
            _header.Children.Add(_count);
            _header.Children.Add(_caption);
            DockPanel.SetDock(_header, Dock.Top);
            _root.Children.Add(_header);
            _root.Children.Add(_body);
            Sync();
        }

        public void Relocalize()
        {
            AutomationProperties.SetName(_picker, Loc("Str_Strip_Collection"));
            AutomationProperties.SetName(_list, Loc("Str_Strip_Collection"));
            AutomationProperties.SetName(_rolodex, Loc("Str_Strip_Collection"));
            _host.RestoreText = Loc("Str_Dock_Restore") + "  (Alt+Shift+" + Arrow(_host.Side) + ")";
            _host.CollapseText = Loc("Str_Dock_Minimize") + "  (Alt+Shift+" + Arrow(_host.Side) + ")";
            _host.ResizeText = Loc("Str_Dock_Resize");
            if (IsVisible) Load(keepCurrent: true);
        }

        private static string Arrow(DockSide side) => side switch
        {
            DockSide.Left => "Left", DockSide.Top => "Up", DockSide.Right => "Right", _ => "Down",
        };

        /// <summary>Picks the thumbnail list or the rolodex for a left or right strip, and remembers it.</summary>
        public void SetSideView(string view)
        {
            if (view is not ("list" or "rolodex") || view == _sideView) return;
            _sideView = view;
            Settings.Set("StripView", view);
            Relayout();
            SideViewChanged?.Invoke();
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
                _caption.Text = chosen.Content?.ToString() ?? "";

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
                _rolodex.CardColor = CardColors.StripCard();   // follows a theme change too
                _rolodex.Items = _items;
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
                    _rolodex.SelectedIndex = index;
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

        private void ShowItemMenu(CaptureItem item, FrameworkElement anchor)
        {
            _openDelay.Stop();
            ItemMenuRequested?.Invoke(item, anchor);
        }

        public ContextMenu SourceMenu(FrameworkElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            foreach (var choice in _picker.Items.OfType<ComboBoxItem>())
            {
                string tag = (string)choice.Tag;
                var item = new MenuItem { Header = choice.Content, IsCheckable = true, IsChecked = tag == _source };
                item.Click += (_, _) => { _source = tag; Load(keepCurrent: true); };
                menu.Items.Add(item);
            }
            return menu;
        }

        private void Open(CaptureItem item)
        {
            _openDelay.Stop();
            if (item.Id == _current) return;
            OpenRequested?.Invoke(item);
        }

        /// <summary>For the self-test: the rolodex at a given zoom, as Ctrl+wheel sets it.</summary>
        internal void TestRolodexZoom(double zoom) => _rolodex.Zoom = zoom;

        /// <summary>For the self-test: show a source by its tag.</summary>
        internal void TestShow(string source)
        {
            _source = source;
            Load(keepCurrent: true);
        }

        private static string Loc(string key) => LocaleManager.Loc(key);
    }

    /// <summary>One thumbnail in the strip's column, on a card in the card color with its
    /// collection's color along the top, loaded off the interface thread when the virtualized list
    /// shows it.</summary>
    public sealed class StripThumb : Border
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _caption = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) };

        public StripThumb()
        {
            Margin = new Thickness(0, 2, 0, 6);
            Padding = new Thickness(6, 6, 6, 5);
            // The theme's pane gray until a card color is picked: the strip has the window's dark
            // background behind it, so gray cards stand out there.
            bool own = !CardColors.CardIsDefault;
            SetResourceReference(BackgroundProperty, own ? "CaptureCardBrush" : "ContentPaneBrush");
            SetResourceReference(CornerRadiusProperty, "ControlCornerRadius");
            _caption.SetResourceReference(TextBlock.ForegroundProperty, own ? "CaptureCardTextBrush" : "TextBrush");
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            var stack = new StackPanel();
            stack.Children.Add(_image);
            stack.Children.Add(_caption);
            Child = stack;
            DataContextChanged += (_, _) => Load();
        }

        private void Load()
        {
            _image.Source = null;
            if (DataContext is not CaptureItem item) return;
            if (CardColors.Collection(item.CollectionId) is Color own)
            {
                BorderBrush = new SolidColorBrush(own);
                BorderThickness = new Thickness(0, 3, 0, 0);
            }
            else BorderThickness = new Thickness(0);
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
