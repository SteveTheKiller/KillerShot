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
    internal sealed class CollectionStrip : Border, IDisposable
    {
        /// <summary>A picture was chosen in the strip and should open in the editor.</summary>
        public event Action<CaptureItem>? OpenRequested;
        public event Action<CaptureItem, FrameworkElement>? ItemMenuRequested;

        /// <summary>The side view changed between the thumbnail list and the rolodex.</summary>
        public event Action? SideViewChanged;
        public event Action? SourceChanged;
        public event Action<string>? StatusMessage;
        public string Source => _source;
        public bool AlbumMode => _albumMode;
        public string SourceCaption => _caption.Text;
        public string CountCaption => _count.Text;

        /// <summary>How a left or right strip shows its captures: "list" or "rolodex".</summary>
        public string SideView => _sideView;

        internal FrameworkElement TestCarousel => _flow;

        private readonly DockHost _host;
        private readonly ComboBox _picker = new() { MinWidth = 150, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _caption = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly DockPanel _root = new();
        private readonly Grid _body = new();
        private readonly CollectionBrowser _browser = new();
        private bool _albumMode;
        private string _albumSource = "all";
        private readonly CoverFlow _flow;
        private readonly Slider _slider;
        private readonly ListBox _list;
        private readonly Rolodex _rolodex;
        private string _sideView = Settings.Get("StripView") == "rolodex" ? "rolodex" : "list";
        private readonly DispatcherTimer _openDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
        private string? _pendingSource;
        private IReadOnlyList<CaptureItem> _items = [];
        private string _source = "all";          // all, c<id>, c0 (not in a collection), folder
        private bool _syncing;
        private volatile bool _disposed;
        private long? _current;
        private long _albumSession = -1;

        public CollectionStrip(DockHost host, Style sliderStyle)
        {
            _host = host;
            // No pane of its own: the strip sits on the window's own background and grain, as
            // KillerPDF's sidebar does, so its cards are the only surface in it.
            Background = Brushes.Transparent;
            Padding = new Thickness(8, 6, 8, 6);
            Focusable = false;
            Panel.SetZIndex(this, 1);

            _count.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _browser.SelectionChanged += (_, _) =>
            {
                if (_syncing || _browser.SelectedItem is not CollectionChoice choice) return;
                _source = choice.Source;
                Load(keepCurrent: true);
            };
            _picker.SelectionChanged += (_, _) =>
            {
                if (_syncing || _picker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
                _source = tag;
                Load(keepCurrent: true);
            };
            _flow = new CoverFlow { ThumbnailSource = CaptureLibrary.RequestThumb, DeferHiddenLoading = true, Describe = LibraryView.Describe, ViewHeight = 1.2 };
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
                BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "xmlns:s=\"clr-namespace:KillerShot.Shell;assembly=KillerShot\"><s:StripThumb/></DataTemplate>"),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            _list.SelectionChanged += (_, _) => { if (!_syncing) QueueOpen(_list.SelectedItem as CaptureItem); };
            // Cards already supply their own padding. File-picker row insets would double it.
            _list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
                    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                    <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="row" Background="Transparent" CornerRadius="{DynamicResource SmallCornerRadius}"><ContentPresenter/></Border>
                    </ControlTemplate></Setter.Value></Setter>
                </Style>
                """);
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
                ThumbnailSource = CaptureLibrary.RequestThumb, DeferHiddenLoading = true, Caption = LibraryView.Title, Describe = LibraryView.Describe,
                CardColor = CardColors.StripCard(), Accent = item => CardColors.Collection(item.CollectionId),
            };
            CardColors.Changed += OnCardColorsChanged;
            if (double.TryParse(Settings.Get("RolodexZoom"), NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)) _rolodex.Zoom = zoom;
            _rolodex.ZoomChanged += () => Settings.Set("RolodexZoom", _rolodex.Zoom.ToString("0.###", CultureInfo.InvariantCulture));
            _rolodex.SelectionChanged += () => { if (!_syncing) QueueOpen(_rolodex.SelectedItem); };
            _rolodex.ItemActivated += item => Open(item);
            _rolodex.ItemMenuRequested += ShowItemMenu;

            _openDelay.Tick += OpenDelayElapsed;

            Child = _root;

            _host.SideChanged += Relayout;
            CaptureLibrary.Changed += OnLibraryChanged;
            FolderCaptures.Changed += OnFolderChanged;
            IsVisibleChanged += OnIsVisibleChanged;
            Relayout();
            Relocalize();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelPendingOpen();
            _openDelay.Tick -= OpenDelayElapsed;
            _host.SideChanged -= Relayout;
            CaptureLibrary.Changed -= OnLibraryChanged;
            FolderCaptures.Changed -= OnFolderChanged;
            CardColors.Changed -= OnCardColorsChanged;
            IsVisibleChanged -= OnIsVisibleChanged;
            OpenRequested = null;
            ItemMenuRequested = null;
            SideViewChanged = null;
            SourceChanged = null;
            StatusMessage = null;
        }

        private void OnCardColorsChanged()
        {
            if (_disposed) return;
            _rolodex.CardColor = CardColors.StripCard();
            _rolodex.Refresh();
            _list.Items.Refresh();
        }

        private void OnLibraryChanged()
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => Load(keepCurrent: true)));
        }

        private void OnFolderChanged()
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (_albumMode || FolderCaptures.IsSource(_source)) Load(keepCurrent: true); }));
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) Load(keepCurrent: true);
        }

        private void OpenDelayElapsed(object? sender, EventArgs e)
        {
            if (ImageDrag.Active) { CancelPendingOpen(); return; }
            if (_host.IsHorizontal ? _flow.IsMoving : _sideView == "rolodex" && _rolodex.IsMoving) return;
            var item = _openDelay.Tag as CaptureItem;
            string? source = _pendingSource;
            var selected = _host.IsHorizontal ? _flow.SelectedItem
                : _sideView == "rolodex" ? _rolodex.SelectedItem : _list.SelectedItem as CaptureItem;
            CancelPendingOpen();
            if (item != null && CaptureLibrary.IsCurrent(item) && source == _source && selected == item) Open(item);
        }

        /// <summary>Carousel across the top or bottom, a thumbnail column down the sides.</summary>
        private void Relayout()
        {
            if (_disposed) return;
            CancelPendingOpen();
            _body.Children.Clear();
            _body.RowDefinitions.Clear();
            if (_albumMode) _body.Children.Add(_browser);
            else if (_host.IsHorizontal)
            {
                _body.RowDefinitions.Add(new RowDefinition());
                _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(_slider, 1);
                _body.Children.Add(_flow);
                _body.Children.Add(_slider);
                _body.Children.Add(_picker);
            }
            else _body.Children.Add(_sideView == "rolodex" ? _rolodex : _list);

            _root.Children.Clear();
            _root.Children.Add(_body);
            Sync();
        }

        public void SetAlbumMode(bool album)
        {
            if (_albumMode == album) return;
            if (_albumMode) _albumSource = _source;
            _albumMode = album;
            if (album) _source = _albumSource;
            Padding = album ? new Thickness(0, 6, 0, 6) : new Thickness(8, 6, 8, 6);
            Relayout();
            Load(keepCurrent: true);
        }

        public void Relocalize()
        {
            AutomationProperties.SetName(_picker, Loc("Str_Strip_Collection"));
            AutomationProperties.SetName(_browser, Loc("Str_Strip_Collection"));
            AutomationProperties.SetName(_list, Loc("Str_Strip_Collection"));
            AutomationProperties.SetName(_rolodex, Loc("Str_Strip_Collection"));
            _host.RestoreText = Loc("Str_Dock_Restore") + "  (Alt+Shift+" + Arrow(_host.Side) + ")";
            _host.CollapseText = Loc("Str_Dock_Minimize") + "  (Alt+Shift+" + Arrow(_host.Side) + ")";
            _host.ResizeText = Loc("Str_Dock_Resize");
            Load(keepCurrent: true);
            for (int i = 0; i < _browser.Items.Count; i++)
                if (_browser.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row
                    && row.Template.FindName("row", row) is Border { Child: ContentPresenter presenter })
                {
                    presenter.ApplyTemplate();
                    if (VisualTreeHelper.GetChildrenCount(presenter) > 0
                        && VisualTreeHelper.GetChild(presenter, 0) is CollectionPreview preview) preview.Relocalize();
                }
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
                string want = item.CollectionId == FolderCaptures.CollectionId
                    ? FolderCaptures.IsSource(_source) && FolderCaptures.ItemsForSource(_source).Any(photo => photo.Id == item.Id)
                        ? _source : FolderCaptures.SourceForItem(item)
                    : _source == "all" ? "all"
                    : item.CollectionId is long cid ? "c" + cid.ToString(CultureInfo.InvariantCulture) : "c0";
                _source = want;
            }
            Load(keepCurrent: true);
        }

        private static bool IsInFolder(string path) =>
            string.Equals(System.IO.Path.GetDirectoryName(path)?.TrimEnd('\\'), FolderCaptures.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Fills the picker and the strip for the chosen source.</summary>
        private void Load(bool keepCurrent)
        {
            if (_disposed) return;
            CancelPendingOpen();
            long session = CaptureLibrary.Session;
            if (_albumSession != session)
            {
                _albumSession = session;
                _current = null;
                _source = "all";
                _albumSource = "all";
            }
            _syncing = true;
            try
            {
                var collections = CaptureLibrary.Collections();
                _picker.Items.Clear();
                void Add(string tag, string text) => _picker.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
                Add("all", Loc("Str_Strip_All"));
                if (FolderCaptures.Enabled) Add("folder", FolderCaptures.Name);
                var folders = FolderCaptures.LinkedCollections();
                foreach (var folder in folders) Add(folder.Source, folder.Name);
                foreach (var c in collections) Add("c" + c.Id.ToString(CultureInfo.InvariantCulture), c.Name);
                Add("c0", Loc("Str_Lib_Unfiled"));
                var chosen = _picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _source);
                if (chosen == null) { _source = "all"; chosen = (ComboBoxItem)_picker.Items[0]; }
                _picker.SelectedItem = chosen;
                _caption.Text = chosen.Content?.ToString() ?? "";

                if (_albumMode)
                {
                    var all = CaptureLibrary.Items();
                    var known = collections.Select(c => c.Id).ToHashSet();
                    var membersByCollection = all.ToLookup(i => i.CollectionId);
                    var choices = new List<CollectionChoice>();
                    void Choice(string source, string name, IReadOnlyList<CaptureItem> members, string? status = null, string? path = null) =>
                        choices.Add(new CollectionChoice(source, name, members.Take(PreviewStyle == "pile" ? 12 : 4).ToArray(), members.Count, PreviewStyle, status, path));
                    Choice("all", Loc("Str_Strip_All"), all);
                    if (FolderCaptures.Enabled) Choice("folder", FolderCaptures.Name, FolderCaptures.Items());
                    foreach (var folder in folders) Choice(folder.Source, folder.Name, FolderCaptures.LinkedItems(folder.Source),
                        folder.Loading ? "Str_FolderCollection_Loading" : !folder.Available ? "Str_FolderCollection_Unavailable" : null, folder.Path);
                    foreach (var c in collections)
                        Choice("c" + c.Id.ToString(CultureInfo.InvariantCulture), c.Name, membersByCollection[c.Id].ToArray());
                    Choice("c0", Loc("Str_Lib_Unfiled"), all.Where(i => i.CollectionId is not long id || !known.Contains(id)).ToArray());
                    var existing = _browser.Items.OfType<CollectionChoice>().ToArray();
                    bool unchanged = existing.Length == choices.Count && existing.Zip(choices).All(pair =>
                        pair.First.Source == pair.Second.Source && pair.First.Name == pair.Second.Name
                        && pair.First.Count == pair.Second.Count && pair.First.Style == pair.Second.Style
                        && pair.First.Status == pair.Second.Status && pair.First.Path == pair.Second.Path
                        && pair.First.Preview.SequenceEqual(pair.Second.Preview));
                    if (!unchanged) _browser.ItemsSource = choices;
                    _browser.SelectedItem = _browser.Items.OfType<CollectionChoice>().FirstOrDefault(c => c.Source == _source);
                }

                _items = _source switch
                {
                    var folder when FolderCaptures.IsSource(folder) => FolderCaptures.ItemsForSource(folder),
                    "c0" => CaptureLibrary.Items().Where(i => i.CollectionId == null || !collections.Any(c => c.Id == i.CollectionId)).ToList(),
                    "all" => CaptureLibrary.Items(),
                    _ => CaptureLibrary.Items().Where(i => "c" + i.CollectionId?.ToString(CultureInfo.InvariantCulture) == _source).ToList(),
                };
                _count.Text = string.Format(CultureInfo.CurrentCulture, Loc(_items.Count == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), _items.Count);
                if (!_albumMode)
                {
                    _flow.Items = _items;
                    _list.ItemsSource = _items;
                    _rolodex.CardColor = CardColors.StripCard();
                    _rolodex.Items = _items;
                    Sync();
                }
            }
            finally { _syncing = false; }
            SourceChanged?.Invoke();
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
            CancelPendingOpen();
            if (_albumMode || _disposed || item == null || !CaptureLibrary.IsCurrent(item) || item.Id == _current) return;
            _openDelay.Tag = item;
            _pendingSource = _source;
            _openDelay.Start();
        }

        private void CancelPendingOpen()
        {
            _openDelay.Stop();
            _openDelay.Tag = null;
            _pendingSource = null;
        }

        private void ShowItemMenu(CaptureItem item, FrameworkElement anchor)
        {
            CancelPendingOpen();
            ItemMenuRequested?.Invoke(item, anchor);
        }

        public ContextMenu SourceMenu(FrameworkElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
            foreach (var choice in _picker.Items.OfType<ComboBoxItem>())
            {
                string tag = (string)choice.Tag;
                var item = new MenuItem { Header = choice.Content, IsCheckable = true, IsChecked = tag == _source };
                item.Click += (_, _) => { _source = tag; Load(keepCurrent: true); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var openFolder = new MenuItem { Header = Loc("Str_FolderCollection_Add") };
            openFolder.Click += (_, _) =>
            {
                var dialog = new FileDialog(FileDialogMode.Folder)
                {
                    Title = Loc("Str_Btn_Select"),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                try { SetSource(FolderCaptures.AddFolder(dialog.FileName)); }
                catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, Loc("Str_Btn_Select")); }
            };
            menu.Items.Add(openFolder);
            if (_source.StartsWith("folder:", StringComparison.Ordinal))
            {
                var remove = new MenuItem { Header = Loc("Str_FolderCollection_Remove"), ToolTip = FolderCaptures.PathForSource(_source) };
                remove.Click += (_, _) =>
                {
                    try { FolderCaptures.RemoveFolder(_source); SetSource("all"); }
                    catch (Exception error) { StatusMessage?.Invoke(error.Message); }
                };
                menu.Items.Add(remove);
            }
            return menu;
        }

        public string PreviewStyle => Settings.Get("CollectionPreview") is "grid" or "mini" or "pile"
            ? Settings.Get("CollectionPreview")! : "stack";

        public MenuItem PreviewMenu()
        {
            var menu = new MenuItem { Header = Loc("Str_CollectionPreview") };
            foreach (string style in new[] { "stack", "grid", "mini", "pile" })
            {
                var item = new MenuItem { Header = Loc("Str_Preview_" + style), Tag = style, IsCheckable = true, IsChecked = PreviewStyle == style };
                item.Click += (_, _) => { Settings.Set("CollectionPreview", style); Load(keepCurrent: true); };
                menu.Items.Add(item);
            }
            return menu;
        }

        private void Open(CaptureItem item)
        {
            CancelPendingOpen();
            if (_disposed || !CaptureLibrary.IsCurrent(item) || item.Id == _current) return;
            OpenRequested?.Invoke(item);
        }

        /// <summary>For the self-test: the rolodex at a given zoom, as Ctrl+wheel sets it.</summary>
        internal void TestRolodexZoom(double zoom) => _rolodex.Zoom = zoom;

        /// <summary>For the self-test: show a source by its tag.</summary>
        internal void TestShow(string source)
            => SetSource(source);

        internal void SetSource(string source)
        {
            _source = source;
            Load(keepCurrent: true);
        }

        private static string Loc(string key) => LocaleManager.Loc(key);
    }

    /// <summary>One thumbnail in the strip's column, on a card in the card color with its
    /// collection's color around the frame, loaded off the interface thread when the virtualized list
    /// shows it.</summary>
    public sealed class StripThumb : PhotoCardBorder
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _caption = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) };

        public StripThumb()
        {
            Cursor = Cursors.Hand;
            Margin = new Thickness(0, 2, 0, 6);
            Padding = new Thickness(0);
            Style = (Style)System.Windows.Markup.XamlReader.Parse("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
                    <Setter Property="BorderBrush" Value="Transparent"/>
                    <Setter Property="BorderThickness" Value="2"/>
                    <Style.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="BorderBrush" Value="{DynamicResource SelectionAccent}"/>
                            <Setter Property="RenderTransform"><Setter.Value><TranslateTransform Y="-2"/></Setter.Value></Setter>
                            <Setter Property="Effect"><Setter.Value><DropShadowEffect Color="Black" BlurRadius="8" ShadowDepth="3" Direction="270" Opacity="{DynamicResource SelectedTileShadowOpacity}"/></Setter.Value></Setter>
                        </Trigger>
                        <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
                            <Setter Property="BorderBrush" Value="{DynamicResource SelectionAccent}"/>
                            <Setter Property="RenderTransform"><Setter.Value><TranslateTransform Y="-2"/></Setter.Value></Setter>
                            <Setter Property="Effect"><Setter.Value><DropShadowEffect Color="Black" BlurRadius="8" ShadowDepth="3" Direction="270" Opacity="{DynamicResource SelectedTileShadowOpacity}"/></Setter.Value></Setter>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
                """);
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
            var layers = new Grid();
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(CornerRadiusProperty, "ControlCornerRadius");
            layers.Children.Add(grain);
            stack.Margin = new Thickness(6, 6, 6, 5);
            layers.Children.Add(stack);
            Child = layers;
            Cursor = DragCursors.Open;
            ImageDrag.Attach(this, _ => DataContext is CaptureItem item ? () => CaptureLibrary.LoadImage(item) : null);
            DataContextChanged += (_, _) => Load();
        }

        private void Load()
        {
            _image.Source = null;
            if (DataContext is not CaptureItem item) return;
            if (!SystemParameters.HighContrast && CardColors.Collection(item.CollectionId) is Color own)
            {
                BorderBrush = new SolidColorBrush(own);
            }
            else ClearValue(BorderBrushProperty);
            _caption.Text = LibraryView.Title(item);
            AutomationProperties.SetName(this, LibraryView.Describe(item));
            ToolTip = LibraryView.Describe(item);
            CaptureLibrary.LoadThumbAsync(item).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && CaptureLibrary.IsCurrent(item) && DataContext is CaptureItem now && now == item) _image.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
