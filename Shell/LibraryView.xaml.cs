using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KillerShot.Capture;
using KillerShot.Controls;
using KillerShot.Services;
using Shortcut = KillerShot.Services.Shortcut;
using CaptureMode = KillerShot.Capture.CaptureMode;
using Path = System.IO.Path;

namespace KillerShot.Shell
{
    /// <summary>What one row of the library list draws.</summary>
    public enum LibraryRowKind { Shelf, Header, Tiles }

    /// <summary>One row of the library list. Rows are plain data; LibraryRowHost builds the
    /// elements for a row only when the list scrolls it into view.</summary>
    public sealed class LibraryRow
    {
        internal LibraryRowKind Kind { get; init; }
        internal string Key { get; init; } = "";
        internal string Title { get; init; } = "";
        internal string Detail { get; init; } = "";
        internal long? CollectionId { get; init; }
        internal bool IsCollection { get; init; }
        internal bool IsFolder { get; init; }
        internal bool Timeline { get; init; }
        internal IReadOnlyList<CaptureItem> Items { get; init; } = [];
    }

    /// <summary>Builds its row's elements when it is realized, because the virtualized list
    /// creates and drops rows as they scroll in and out of view.</summary>
    public sealed class LibraryRowHost : Border
    {
        private LibraryRow? _built;

        public LibraryRowHost()
        {
            DataContextChanged += (_, _) => Build();
            Loaded += (_, _) => Build();
        }

        private void Build()
        {
            if (DataContext is not LibraryRow row || ReferenceEquals(row, _built)) return;
            DependencyObject? node = this;
            while (node != null && node is not LibraryView) node = VisualTreeHelper.GetParent(node);
            if (node is not LibraryView view) return;
            _built = row;
            Child = view.BuildRow(row);
        }
    }

    /// <summary>
    /// The library screen: every capture in the encrypted library (CaptureLibrary), shown when no
    /// image is open and on Ctrl+L. Shelves draws a cover flow per collection or per day, Cover
    /// Flow one big flow across everything, Grid a wall of thumbnails, and Timeline the days in
    /// order down a line. Search filters every view. Right-click a capture for Open, Copy, Save
    /// as, a collection, a note or Delete.
    /// </summary>
    public partial class LibraryView : UserControl
    {
        private const double GridTileWidth = 208, GridTileHeight = 128;
        private const double TimelineTileWidth = 168, TimelineTileHeight = 104;
        private const double TimelineGutter = 32;
        private const double TileGap = 12;

        /// <summary>A capture should open in the editor.</summary>
        internal event Action<CaptureItem>? OpenRequested;

        /// <summary>The empty library's Capture a region button was clicked.</summary>
        public event Action? CaptureRequested;

        /// <summary>A line for the status bar.</summary>
        public event Action<string>? StatusMessage;
        public event Action<string>? CountChanged;
        private string _source = "all";

        public void SetSource(string source)
        {
            if (_source == source) return;
            _source = source;
            if (IsLoaded) Reload(); else _dirty = true;
        }
        public event Action<string, FrameworkElement>? LinkRequested;

        private string _view = "shelves";
        private string _group = "date";
        private IReadOnlyList<CaptureItem> _items = [];
        private IReadOnlyList<CaptureCollection> _collections = [];
        private IReadOnlyList<CaptureItem> _folderItems = [];
        private readonly Dictionary<string, long> _shelfFront = [];
        private readonly DispatcherTimer _searchDelay;
        private bool _reloadQueued;
        private bool _dirty = true;
        private int _tilesPerRow;
        private long _albumSession = -1;

        private static string Loc(string key) => LocaleManager.Loc(key);

        public LibraryView()
        {
            InitializeComponent();
            _view = Settings.Get("LibraryView") is "shelves" or "flow" or "grid" or "timeline" ? Settings.Get("LibraryView")! : "shelves";
            _group = Settings.Get("LibraryGroup") is "collection" ? "collection" : "date";

            BigFlow.ThumbnailSource = CaptureLibrary.RequestThumb;
            BigFlow.DeferHiddenLoading = true;
            BigFlow.SelectedPreviewSource = CaptureLibrary.LoadPreview;
            BigFlow.Describe = Describe;
            BigFlow.ItemActivated += item => OpenRequested?.Invoke(item);
            BigFlow.ItemMenuRequested += ShowItemMenu;
            BigFlow.DeleteRequested += Delete;
            BigFlow.SelectionChanged += UpdateBigFlowCaption;

            _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Reload(); };

            Loaded += (_, _) =>
            {
                CaptureLibrary.Changed += OnLibraryChanged;
                FolderCaptures.Changed += OnLibraryChanged;
                CardColors.Changed += OnLibraryChanged;
                Relocalize();
                if (_dirty) Reload();
            };
            Unloaded += (_, _) =>
            {
                CaptureLibrary.Changed -= OnLibraryChanged;
                FolderCaptures.Changed -= OnLibraryChanged;
                CardColors.Changed -= OnLibraryChanged;
            };
            IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Reload(); };
            SizeChanged += (_, _) =>
            {
                if (_view is "grid" or "timeline" && TilesPerRow() != _tilesPerRow) BuildRows();
            };
        }

        /// <summary>Redraws with the current language. Called on load and after a language change.</summary>
        public void Relocalize()
        {
            if (IsLoaded) Reload();
        }

        private void OnLibraryChanged()
        {
            if (_reloadQueued) return;
            _reloadQueued = true;
            // Coalesced, and on the UI thread: the library changes from background threads.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _reloadQueued = false;
                if (IsVisible) Reload();
                else _dirty = true;
            }));
        }

        // ============================================================
        // Loading
        // ============================================================

        /// <summary>Reads the library again and redraws the current view.</summary>
        public void Reload()
        {
            long session = CaptureLibrary.Session;
            if (_albumSession != session)
            {
                _albumSession = session;
                _searchDelay.Stop();
                SearchBox.Clear();
                _source = "all";
                _shelfFront.Clear();
                BigFlow.Items = [];
            }
            _dirty = false;
            int pending = CaptureLibrary.PendingCount;
            PendingBar.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            PendingText.Text = string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_Pending"), pending);
            RetryPendingBtn.IsEnabled = CaptureLibrary.IsOpen;
            if (!CaptureLibrary.IsOpen && !CaptureLibrary.Opening.IsCompleted)
            {
                // Still opening at startup: the loading screen, then this again.
                ShowBusy();
                CaptureLibrary.Opening.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { HideBusy(); Reload(); })),
                    TaskScheduler.Default);
                return;
            }
            if (!CaptureLibrary.IsOpen)
            {
                ShowLock();
                return;
            }
            LockPanel.Visibility = Visibility.Collapsed;
            string search = SearchBox.Text.Trim();
            _items = CaptureLibrary.Items(search.Length > 0 ? search : null);
            _collections = CaptureLibrary.Collections();
            _folderItems = FolderCaptures.Items(search.Length > 0 ? search : null);
            bool Matches(CaptureItem item) => _source == "all" || (_source == "c0"
                ? item.CollectionId == null || !_collections.Any(c => c.Id == item.CollectionId)
                : "c" + item.CollectionId?.ToString(CultureInfo.InvariantCulture) == _source);
            int total;
            if (FolderCaptures.IsSource(_source))
            {
                _items = FolderCaptures.ItemsForSource(_source, search.Length > 0 ? search : null);
                total = search.Length > 0 ? FolderCaptures.ItemsForSource(_source).Count : _items.Count;
                _collections = [];
            }
            else
            {
                _items = _items.Where(Matches).ToList();
                total = search.Length > 0 ? CaptureLibrary.Items().Count(Matches) : _items.Count;
                if (_source != "all") _collections = _collections.Where(c => "c" + c.Id.ToString(CultureInfo.InvariantCulture) == _source).ToList();
            }
            if (_source != "all") _folderItems = [];

            SyncHeader();
            string? folderStatus = FolderReadStatus();
            CountText.Text = search.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_CountMatches"), _items.Count, total)
                : string.Format(CultureInfo.CurrentCulture, Loc(total == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), total);
            if (folderStatus != null) CountText.Text += " (" + Loc(folderStatus) + ")";
            CountChanged?.Invoke(CountText.Text);

            // The Screenshots folder shows as a collection, so only where collections do.
            bool linkedPhotos = FolderCaptures.LinkedCollections().Any(folder => FolderCaptures.LinkedItems(folder.Source, search).Count > 0);
            bool folderShown = _group == "collection" && _view != "timeline" && (_folderItems.Count > 0 || _source == "all" && linkedPhotos);
            bool libraryEmpty = total == 0 && _collections.Count == 0 && _folderItems.Count == 0 && !linkedPhotos;
            if (_items.Count == 0 && !folderShown && (search.Length > 0 || _view != "shelves" || _group != "collection" || _collections.Count == 0))
            {
                ShowEmpty(searching: search.Length > 0 && !libraryEmpty, folderStatus);
                return;
            }
            EmptyPanel.Visibility = Visibility.Collapsed;
            if (_view == "flow")
            {
                RowsList.Visibility = Visibility.Collapsed;
                FlowHost.Visibility = Visibility.Visible;
                BigFlow.Items = _group == "collection" ? ByCollection(_items) : _items;
                UpdateBigFlowCaption();
            }
            else
            {
                FlowHost.Visibility = Visibility.Collapsed;
                RowsList.Visibility = Visibility.Visible;
                BuildRows();
            }
        }

        private void ShowLock()
        {
            RowsList.Visibility = FlowHost.Visibility = EmptyPanel.Visibility = Visibility.Collapsed;
            LockPanel.Visibility = Visibility.Visible;
            bool locked = CaptureLibrary.NeedsPassword;
            UnlockBox.Visibility = UnlockBtn.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            int pending = CaptureLibrary.PendingCount;
            LockDetail.Text = !locked
                ? string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_OpenFailed"), CaptureLibrary.LoadError ?? "")
                : pending > 0
                    ? string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_LockedPending"), pending)
                    : Loc("Str_Lib_LockedDetail");
        }

        private string? FolderReadStatus()
        {
            var folders = FolderCaptures.IsSource(_source)
                ? new[] { FolderCaptures.StatusForSource(_source) }.OfType<FolderCollection>()
                : _source == "all" ? FolderCaptures.LinkedCollections().Concat(FolderCaptures.Enabled
                    ? new[] { FolderCaptures.StatusForSource("folder") }.OfType<FolderCollection>() : []) : [];
            if (folders.Any(folder => !folder.Loading && !folder.Available)) return "Str_FolderCollection_Unavailable";
            return folders.Any(folder => folder.Loading) ? "Str_FolderCollection_Loading" : null;
        }

        private void ShowEmpty(bool searching, string? folderStatus = null)
        {
            RowsList.Visibility = FlowHost.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyTitle.Text = Loc(folderStatus ?? (searching ? "Str_Lib_NoMatches" : "Str_Empty_Title"));
            EmptyHint.Text = folderStatus != null ? (FolderCaptures.IsSource(_source) ? FolderCaptures.PathForSource(_source) : "") : searching
                ? Loc("Str_Lib_NoMatchesHint")
                : string.Format(Loc("Str_Empty_Hint"), Shortcut.TryParse(CaptureSettings.ShortcutText(ShortcutAction.LastMode), out var sc) ? sc.Short : "PrtScn");
            EmptyCaptureBtn.Visibility = searching || folderStatus != null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SyncHeader()
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ============================================================
        // Groups and rows
        // ============================================================

        private sealed record Group(string Key, string Title, long? CollectionId, bool IsCollection, List<CaptureItem> Items, bool IsFolder = false);

        private List<Group> Groups(bool byCollection)
        {
            var groups = new List<Group>();
            if (byCollection)
            {
                bool searching = SearchBox.Text.Trim().Length > 0;
                if (FolderCaptures.IsSource(_source))
                {
                    if (_items.Count > 0 || !searching)
                        groups.Add(new Group(_source, FolderCaptures.NameForSource(_source), FolderCaptures.CollectionId, false, _items.ToList(), IsFolder: true));
                    return groups;
                }
                var known = _collections.Select(c => c.Id).ToHashSet();
                if (_source == "all" && FolderCaptures.Enabled && (_folderItems.Count > 0 || !searching))
                    groups.Add(new Group("folder", FolderCaptures.Name, FolderCaptures.CollectionId, false, _folderItems.ToList(), IsFolder: true));
                foreach (var folder in _source == "all" ? FolderCaptures.LinkedCollections() : [])
                {
                    var members = FolderCaptures.LinkedItems(folder.Source, searching ? SearchBox.Text.Trim() : null);
                    if (members.Count > 0 || !searching)
                        groups.Add(new Group(folder.Source, folder.Name, FolderCaptures.CollectionId, false, members.ToList(), IsFolder: true));
                }
                foreach (var c in _collections)
                {
                    var members = _items.Where(i => i.CollectionId == c.Id).ToList();
                    if (members.Count == 0 && searching) continue;
                    groups.Add(new Group("c" + c.Id, c.Name, c.Id, true, members));
                }
                var loose = _items.Where(i => i.CollectionId is not long id || !known.Contains(id)).ToList();
                if (loose.Count > 0) groups.Add(new Group("c0", Loc("Str_Lib_Unfiled"), null, false, loose));
            }
            else
            {
                foreach (var day in _items.GroupBy(i => i.Taken.Date))
                    groups.Add(new Group("d" + day.Key.ToString("yyyyMMdd", CultureInfo.InvariantCulture), DayTitle(day.Key), null, false, day.ToList()));
            }
            return groups;
        }

        private List<CaptureItem> ByCollection(IReadOnlyList<CaptureItem> items) =>
            Groups(byCollection: true).SelectMany(g => g.Items).ToList();

        private static string DayTitle(DateTime day)
        {
            var today = DateTime.Today;
            if (day == today) return Loc("Str_Lib_Today");
            if (day == today.AddDays(-1)) return Loc("Str_Lib_Yesterday");
            return day.ToString("D", CultureInfo.CurrentCulture);
        }

        private static string CountLabel(int n) =>
            string.Format(CultureInfo.CurrentCulture, Loc(n == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), n);

        private int TilesPerRow()
        {
            double tile = (_view == "timeline" ? TimelineTileWidth : GridTileWidth) + 14 + TileGap;
            double room = ActualWidth - 16 - 8 - 18 - (_view == "timeline" ? TimelineGutter : 0);
            return Math.Max(1, (int)(room / tile));
        }

        private void BuildRows()
        {
            var rows = new List<LibraryRow>();
            _tilesPerRow = TilesPerRow();
            if (_view == "shelves")
            {
                foreach (var g in Groups(_group == "collection"))
                {
                    rows.Add(new LibraryRow
                    {
                        Kind = g.Items.Count > 0 ? LibraryRowKind.Shelf : LibraryRowKind.Header,
                        Key = g.Key, Title = g.Title,
                        Detail = g.Items.Count > 0 ? CountLabel(g.Items.Count) : Loc("Str_Lib_EmptyCollection"),
                        CollectionId = g.CollectionId, IsCollection = g.IsCollection, IsFolder = g.IsFolder, Items = g.Items,
                    });
                }
            }
            else
            {
                bool timeline = _view == "timeline";
                foreach (var g in Groups(!timeline && _group == "collection"))
                {
                    rows.Add(new LibraryRow
                    {
                        Kind = LibraryRowKind.Header, Key = g.Key, Title = g.Title,
                        Detail = g.Items.Count > 0 ? CountLabel(g.Items.Count) : Loc("Str_Lib_EmptyCollection"),
                        CollectionId = g.CollectionId, IsCollection = g.IsCollection, IsFolder = g.IsFolder, Timeline = timeline,
                    });
                    for (int i = 0; i < g.Items.Count; i += _tilesPerRow)
                        rows.Add(new LibraryRow
                        {
                            Kind = LibraryRowKind.Tiles, Key = g.Key + ":" + i, Timeline = timeline,
                            Items = g.Items.Skip(i).Take(_tilesPerRow).ToList(),
                        });
                }
            }
            RowsList.ItemsSource = rows;
        }

        /// <summary>The elements for one row, built when the list realizes it.</summary>
        internal FrameworkElement BuildRow(LibraryRow row) => row.Kind switch
        {
            LibraryRowKind.Shelf => BuildShelf(row),
            LibraryRowKind.Header => BuildHeader(row),
            _ => BuildTiles(row),
        };

        private FrameworkElement BuildHeader(LibraryRow row)
        {
            var title = new TextBlock
            {
                Text = row.Title, FontSize = 16,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
            var detail = new TextBlock { Text = row.Detail, FontSize = 11, Margin = new Thickness(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 8) };
            if (row.IsFolder)
            {
                // A folder on disk, not a collection in the library: a folder mark, and its menu
                // opens it in File Explorer.
                var mark = new TextBlock { Text = "\uF12B", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Margin = new Thickness(0, 0, 8, 2), VerticalAlignment = VerticalAlignment.Bottom };
                mark.SetResourceReference(TextBlock.ForegroundProperty, "SelectionAccent");
                line.Children.Add(mark);
                line.Background = Brushes.Transparent;
                var open = new MenuItem { Header = Loc("Str_Lib_OpenFolder") };
                open.Click += (_, _) => ShowInExplorer(null, row.Key);
                line.ContextMenu = new ContextMenu();
                line.ContextMenu.Items.Add(open);
                line.ToolTip = FolderCaptures.PathForSource(row.Key);
                AutomationProperties.SetName(line, row.Title + ", " + row.Detail);
            }
            if (CardColors.Collection(row.CollectionId) is Color own)
            {
                // The collection's own color, as a dot before its name.
                var swatch = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(own), Margin = new Thickness(0, 0, 8, 5), VerticalAlignment = VerticalAlignment.Bottom };
                line.Children.Add(swatch);
            }
            line.Children.Add(title);
            line.Children.Add(detail);
            if (row.IsCollection && row.CollectionId is long id)
            {
                line.Background = Brushes.Transparent;
                line.ContextMenu = new ContextMenu();
                line.ContextMenuOpening += (_, e) => { FillCollectionMenu(line.ContextMenu, id, row.Title); };
                AutomationProperties.SetName(line, row.Title + ", " + row.Detail);
            }
            if (line.ContextMenu != null)
            {
                line.Focusable = true;
                KeyboardNavigation.SetIsTabStop(line, true);
                line.KeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Apps or Key.Enter or Key.Space) &&
                        !(e.SystemKey == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) return;
                    if (row.IsCollection && row.CollectionId is long collectionId)
                        FillCollectionMenu(line.ContextMenu, collectionId, row.Title);
                    line.ContextMenu.PlacementTarget = line;
                    line.ContextMenu.Placement = PlacementMode.Bottom;
                    line.ContextMenu.IsOpen = true;
                    e.Handled = true;
                };
            }
            if (!row.Timeline) return line;

            // On the timeline a day is a dot on the line, with its name beside it.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimelineGutter) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(TimelineLine());
            var dot = new Ellipse { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 11), StrokeThickness = 2 };
            dot.SetResourceReference(Shape.FillProperty, "ContentPaneBrush");
            dot.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
            grid.Children.Add(dot);
            Grid.SetColumn(line, 1);
            grid.Children.Add(line);
            return grid;
        }

        private static Rectangle TimelineLine()
        {
            var rule = new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Center };
            rule.SetResourceReference(Shape.FillProperty, "CardBorderBrush");
            return rule;
        }

        private FrameworkElement BuildShelf(LibraryRow row)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
            var header = BuildHeader(row);
            header.Margin = new Thickness(0, 4, 0, 2);
            panel.Children.Add(header);
            var flow = new CoverFlow
            {
                // A band from just over the tallest picture down to where the reflections fade
                // out, so the shelves stack with no empty room above or below the pictures.
                Height = 240,
                ViewHeight = 1.52,
                PerspectiveZoomEnabled = true,
                ViewCenterY = 0.255,
                ThumbnailSource = CaptureLibrary.RequestThumb,
                DeferHiddenLoading = true,
                Describe = item => row.Title + ", " + Describe(item),
            };
            flow.Items = row.Items;
            if (_shelfFront.TryGetValue(row.Key, out long front)) flow.Select(front);
            flow.SelectionChanged += () => { if (flow.SelectedItem is { } s) _shelfFront[row.Key] = s.Id; };
            flow.ItemActivated += item => OpenRequested?.Invoke(item);
            flow.ItemMenuRequested += ShowItemMenu;
            flow.DeleteRequested += Delete;
            panel.Children.Add(flow);

            var slider = new Slider
            {
                Style = (Style)FindResource("LibSlider"), MaxWidth = 560, Minimum = 0, Margin = new Thickness(0, -8, 0, 0),
                Focusable = false, IsTabStop = false,
                Visibility = row.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
            };
            slider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(CoverFlow.MaximumIndex)) { Source = flow });
            slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(CoverFlow.SelectedIndex)) { Source = flow, Mode = BindingMode.TwoWay });
            panel.Children.Add(slider);
            return panel;
        }

        private FrameworkElement BuildTiles(LibraryRow row)
        {
            double w = row.Timeline ? TimelineTileWidth : GridTileWidth;
            double h = row.Timeline ? TimelineTileHeight : GridTileHeight;
            var tiles = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in row.Items)
            {
                var image = new Image { Width = w, Height = h, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                LoadThumbAsync(image, item);
                var caption = new TextBlock
                {
                    Text = Title(item), FontSize = 12, Width = w, Margin = new Thickness(0, 6, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var detail = new TextBlock
                {
                    Text = row.Timeline ? item.Taken.ToString("t", CultureInfo.CurrentCulture) : item.Taken.ToString("g", CultureInfo.CurrentCulture),
                    FontSize = 10, Width = w, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                // Until a card color is picked, a card is the theme's pane gray on the window background.
                bool picked = !CardColors.CardIsDefault;
                caption.SetResourceReference(TextBlock.ForegroundProperty, picked ? "CaptureCardTextBrush" : "TextBrush");
                detail.SetResourceReference(TextBlock.ForegroundProperty, picked ? "CaptureCardMutedBrush" : "MutedTextBrush");
                var content = new StackPanel();
                content.Children.Add(QrMark.Wrap(image, item.HasQr));
                content.Children.Add(caption);
                content.Children.Add(detail);
                var tile = new Button { Style = (Style)FindResource("LibTile"), Content = content };
                tile.SetResourceReference(BackgroundProperty, picked ? "CaptureCardBrush" : "ContentPaneBrush");
                if (!SystemParameters.HighContrast && CardColors.Collection(item.CollectionId) is Color own)
                {
                    tile.BorderBrush = new SolidColorBrush(own);
                }
                AutomationProperties.SetName(tile, Describe(item));
                tile.ToolTip = Describe(item);
                tile.Cursor = DragCursors.Open;
                ImageDrag.Attach(tile, _ => () => CaptureLibrary.LoadImage(item));
                tile.Click += (_, _) => OpenRequested?.Invoke(item);
                tile.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowItemMenu(item, tile); };
                tile.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Apps || (e.SystemKey == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
                    {
                        e.Handled = true;
                        ShowItemMenu(item, tile);
                    }
                    else if (e.Key == Key.Delete)
                    {
                        e.Handled = true;
                        Delete(item);
                    }
                };
                tiles.Children.Add(tile);
            }
            if (!row.Timeline) return tiles;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimelineGutter) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(TimelineLine());
            Grid.SetColumn(tiles, 1);
            grid.Children.Add(tiles);
            return grid;
        }

        /// <summary>Loads a thumbnail off the UI thread, so scrolling never waits on decoding.</summary>
        private static void LoadThumbAsync(Image image, CaptureItem item)
        {
            image.Tag = item;
            Task.Run(() => CaptureLibrary.LoadThumb(item)).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && CaptureLibrary.IsCurrent(item) && image.Tag is CaptureItem current && current == item) image.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ============================================================
        // Captions
        // ============================================================

        /// <summary>What a capture is called: the window it came from, its program, or Capture.</summary>
        internal static string Title(CaptureItem item) =>
            !string.IsNullOrWhiteSpace(item.Window) ? item.Window!
            : !string.IsNullOrWhiteSpace(item.Process) ? item.Process!
            : Loc("Str_Lib_Capture");

        internal static string Describe(CaptureItem item) =>
            Title(item) + ", " + item.Taken.ToString("g", CultureInfo.CurrentCulture) + ", " +
            item.Width.ToString(CultureInfo.CurrentCulture) + " x " + item.Height.ToString(CultureInfo.CurrentCulture)
            + (item.HasQr ? ", QR" : "");

        private void UpdateBigFlowCaption()
        {
            if (BigFlow.SelectedItem is not { } item) { BigFlowTitle.Text = BigFlowDetail.Text = ""; return; }
            BigFlowTitle.Text = Title(item);
            string collection = item.CollectionId == FolderCaptures.CollectionId
                ? "  ·  " + FolderCaptures.NameForSource(FolderCaptures.IsSource(_source) ? _source : FolderCaptures.SourceForItem(item))
                : item.CollectionId is long cid && _collections.FirstOrDefault(c => c.Id == cid) is { } c ? "  ·  " + c.Name : "";
            BigFlowDetail.Text = item.Taken.ToString("f", CultureInfo.CurrentCulture) + "  ·  " +
                item.Width.ToString(CultureInfo.CurrentCulture) + " x " + item.Height.ToString(CultureInfo.CurrentCulture) + collection +
                (string.IsNullOrWhiteSpace(item.Note) ? "" : "  ·  " + item.Note);
        }

        // ============================================================
        // Header bar
        // ============================================================

        /// <summary>The view: shelves, flow (the carousel), grid or timeline.</summary>
        public string CurrentView => _view;

        /// <summary>The grouping: date or collection. The timeline is always by date.</summary>
        public string CurrentGroup => _view == "timeline" ? "date" : _group;

        /// <summary>The view or grouping changed, so the footer's radio buttons follow.</summary>
        public event Action? ViewChanged;

        /// <summary>Picks a view from the footer or F5 to F8, and remembers it.</summary>
        public void SetView(string view)
        {
            if (view is not ("shelves" or "flow" or "grid" or "timeline")) return;
            _view = view;
            Settings.Set("LibraryView", view);
            Reload();
            ViewChanged?.Invoke();
        }

        /// <summary>Picks the grouping from the footer, and remembers it.</summary>
        public void SetGroup(string group)
        {
            if (group is not ("date" or "collection")) return;
            _group = group;
            Settings.Set("LibraryGroup", group);
            Reload();
            ViewChanged?.Invoke();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (SearchAnchor != null) SearchAnchor.Tag = SearchBox.Text.Length > 0 ? "on" : null;
            _searchDelay.Stop();
            _searchDelay.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                _searchDelay.Stop();
                Reload();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SearchPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        internal FrameworkElement? SearchAnchor { get; set; }

        /// <summary>Puts the cursor in the search box (Ctrl+F on the library).</summary>
        public void FocusSearch()
        {
            if (SearchAnchor == null) return;
            SearchAnchor.UpdateLayout();
            FlyoutPlacement.AttachInsidePane(SearchPopup, SearchAnchor);
            SearchPopup.IsOpen = true;
        }

        private void SearchPopup_Opened(object sender, EventArgs e)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        internal void CreateCollection() => NewCollection(null);

        private void EmptyCapture_Click(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke();

        // ============================================================
        // Unlock
        // ============================================================

        private void Unlock_Click(object sender, RoutedEventArgs e) => TryUnlock();

        private void UnlockBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            TryUnlock();
        }

        private async void TryUnlock()
        {
            if (UnlockBox.Password.Length == 0 || _busy != null) return;
            string password = UnlockBox.Password;
            // The password goes through SQLCipher's key derivation, which takes a moment, so the
            // loading screen covers it off the interface thread.
            ShowBusy();
            bool ok = false;
            string? openError = null;
            try { ok = await Task.Run(() => CaptureLibrary.Unlock(password)); }
            catch (Exception ex) { openError = ex.Message; }
            finally { HideBusy(); }
            UnlockBox.Clear();
            if (openError != null)
            {
                UnlockError.Text = string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_OpenFailed"), openError);
                UnlockError.Visibility = Visibility.Visible;
                UnlockBox.Focus();
                return;
            }
            UnlockError.SetResourceReference(TextBlock.TextProperty, "Str_Lib_WrongPassword");
            UnlockError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (ok) Reload();
            else UnlockBox.Focus();
        }

        // ============================================================
        // Loading screen, as in KillerNotes: a dimmed cover with a turning ring while the
        // encrypted library opens.
        // ============================================================

        private Border? _busy;

        private void ShowBusy()
        {
            if (_busy != null || Content is not Grid root) return;
            var rotation = new RotateTransform();
            var spinner = new Ellipse
            {
                Width = 34, Height = 34, StrokeThickness = 3,
                StrokeDashArray = [5.5, 3.5],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Stroke = Brushes.White,
                RenderTransform = rotation,
            };
            rotation.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            var message = new TextBlock
            {
                Text = Loc("Str_Busy_OpeningLibrary"), Foreground = Brushes.White,
                FontSize = 14, FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            AutomationProperties.SetName(spinner, message.Text);
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(spinner);
            panel.Children.Add(message);
            _busy = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 0x12, 0x12, 0x12)),
                Child = panel, Cursor = Cursors.Wait,
            };
            Grid.SetRowSpan(_busy, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(_busy, Math.Max(1, root.ColumnDefinitions.Count));
            Panel.SetZIndex(_busy, 10050);
            root.Children.Add(_busy);
        }

        private void HideBusy()
        {
            if (_busy == null) return;
            if (_busy.Child is StackPanel { Children: [Ellipse { RenderTransform: RotateTransform rotation }, ..] })
                rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            ((Grid)Content).Children.Remove(_busy);
            _busy = null;
        }

        /// <summary>For the self-test: the view, grouping and search as the header buttons set them.</summary>
        internal void TestSet(string view, string group, string search)
        {
            _view = view;
            _group = group;
            SearchBox.Text = search;
            _searchDelay.Stop();
            Reload();
            ViewChanged?.Invoke();
        }

        internal void TestShowBusy(bool on) { if (on) ShowBusy(); else HideBusy(); }

        // ============================================================
        // Menus and actions
        // ============================================================

        private Window? OwnerWindow => Window.GetWindow(this);

        private async void RetryPending_Click(object sender, RoutedEventArgs e)
        {
            RetryPendingBtn.IsEnabled = false;
            try { await CaptureLibrary.RetryPendingAsync(); }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
            finally { Reload(); }
        }

        private bool TryChangeAlbum(Action change)
        {
            try { change(); return true; }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); return false; }
        }

        private void ShowItemMenu(CaptureItem item, FrameworkElement anchor)
            => ShowItemMenu(item, anchor, OpenRequested, "Del");

        internal void ShowItemMenu(CaptureItem item, FrameworkElement anchor, Action<CaptureItem>? open, string deleteGesture)
        {
            var menu = new ContextMenu { PlacementTarget = anchor };
            if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && !anchor.IsMouseOver)
                menu.Placement = PlacementMode.Bottom;
            MenuItem Add(string key, Action action, string gesture = "")
            {
                var mi = new MenuItem { Header = Loc(key), InputGestureText = gesture };
                mi.Click += (_, _) => { if (CaptureLibrary.IsCurrent(item)) action(); };
                menu.Items.Add(mi);
                return mi;
            }
            Add("Str_Lib_Open", () => open?.Invoke(item)).FontWeight = FontWeights.SemiBold;
            if (anchor is CoverFlow or Rolodex)
                menu.Items.Add(ViewInfoCards.Menu(anchor is Rolodex));
            Add("Str_Lib_Copy", () => Copy(item));
            Add("Str_Btn_Print", () =>
            {
                try { PrintOutput.Print(CaptureLibrary.LoadImage(item), Title(item)); }
                catch (Exception ex) { StatusMessage?.Invoke(string.Format(Loc("Str_Status_PrintFailed"), ex.Message)); }
            });
            if (FolderCaptures.IsFolderId(item.Id))
            {
                // A file in the Screenshots folder: the folder keeps it, so no collections, notes
                // or delete here, only a way to it and a way into the encrypted library.
                Add("Str_Lib_ShowInExplorer", () => ShowInExplorer(item));
                Add("Str_Lib_AddToLibrary", () => AddToLibrary(item));
                menu.IsOpen = true;
                Anim.FadeIn(menu);
                return;
            }
            Add("Str_Lib_SaveAs", () => SaveAs(item));
            if (BuildOcrLinksMenu(item.Id, anchor) is { } links) menu.Items.Add(links);
            menu.Items.Add(new Separator());

            var move = new MenuItem { Header = Loc("Str_Lib_MoveTo") };
            var none = new MenuItem { Header = Loc("Str_Lib_Unfiled"), IsCheckable = true, IsChecked = item.CollectionId == null };
            none.Click += (_, _) => TryChangeAlbum(() => CaptureLibrary.Move([item.Id], null));
            move.Items.Add(none);
            foreach (var c in CaptureLibrary.Collections())
            {
                var target = c;
                var mi = new MenuItem { Header = c.Name, IsCheckable = true, IsChecked = item.CollectionId == c.Id };
                mi.Click += (_, _) => TryChangeAlbum(() => CaptureLibrary.Move([item.Id], target.Id));
                move.Items.Add(mi);
            }
            move.Items.Add(new Separator());
            var create = new MenuItem { Header = Loc("Str_Lib_NewCollectionDots") };
            create.Click += (_, _) => NewCollection(item);
            move.Items.Add(create);
            menu.Items.Add(move);
            Add("Str_Lib_Note", () => EditNote(item));
            Add("Str_Lib_CardColor", PickCardColor);
            if (!CardColors.CardIsDefault) Add("Str_Lib_CardColorDefault", () => CardColors.SetCard(null));
            menu.Items.Add(new Separator());
            Add("Str_Lib_Delete", () => Delete(item), deleteGesture);
            menu.IsOpen = true;
            Anim.FadeIn(menu);
        }

        public MenuItem? BuildOcrLinksMenu(long id, FrameworkElement anchor)
        {
            var links = CaptureLibrary.OcrLinks(id);
            if (links.Count == 0) return null;
            var menu = new MenuItem { Header = Loc("Str_Ocr_Links") };
            foreach (string link in links)
            {
                var item = new MenuItem { Header = link };
                item.Click += (_, _) => LinkRequested?.Invoke(link, anchor);
                menu.Items.Add(item);
            }
            return menu;
        }

        private void FillCollectionMenu(ContextMenu menu, long id, string name)
        {
            menu.Items.Clear();
            var rename = new MenuItem { Header = Loc("Str_Lib_RenameCollection") };
            rename.Click += (_, _) =>
            {
                var dlg = new PromptDialog(Loc("Str_Lib_RenameCollectionTitle"), "", Loc("Str_Lib_Rename"), initialText: name) { Owner = OwnerWindow };
                if (dlg.ShowDialog() == true && dlg.Text.Trim().Length > 0)
                    TryChangeAlbum(() => CaptureLibrary.RenameCollection(id, dlg.Text));
            };
            menu.Items.Add(rename);
            var color = new MenuItem { Header = Loc("Str_Lib_CollectionColor") };
            color.Click += (_, _) =>
            {
                var dlg = new ColorPickerDialog(OwnerWindow, CardColors.Collection(id) ?? Color.FromRgb(0x50, 0xAE, 0xE8));
                dlg.ShowDialog();
                if (dlg.Accepted) CardColors.SetCollection(id, dlg.SelectedColor);
            };
            menu.Items.Add(color);
            if (CardColors.Collection(id) != null)
            {
                var clear = new MenuItem { Header = Loc("Str_Lib_CollectionColorNone") };
                clear.Click += (_, _) => CardColors.SetCollection(id, null);
                menu.Items.Add(clear);
            }
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = Loc("Str_Lib_DeleteCollection") };
            delete.Click += (_, _) =>
            {
                var dlg = new ConfirmDialog(string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_DeleteCollectionTitle"), name),
                    Loc("Str_Lib_DeleteCollectionDetail"), Loc("Str_Lib_DeleteCollection"), Loc("Str_Btn_Cancel")) { Owner = OwnerWindow };
                dlg.ShowDialog();
                if (dlg.Confirmed) TryChangeAlbum(() => CaptureLibrary.DeleteCollection(id));
            };
            menu.Items.Add(delete);
        }

        /// <summary>Picks the color of every card captures sit on.</summary>
        internal void PickCardColor()
        {
            var dlg = new ColorPickerDialog(OwnerWindow, CardColors.Card);
            dlg.ShowDialog();
            if (dlg.Accepted) CardColors.SetCard(dlg.SelectedColor);
        }

        private void NewCollection(CaptureItem? moveIn)
        {
            var dlg = new PromptDialog(Loc("Str_Lib_NewCollectionTitle"), "", Loc("Str_Lib_Create")) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true || dlg.Text.Trim().Length == 0) return;
            if (!TryChangeAlbum(() => CaptureLibrary.CreateCollection(dlg.Text, moveIn != null ? [moveIn.Id] : null))) return;
            if (_group != "collection" && _view != "timeline")
            {
                _group = "collection";
                Settings.Set("LibraryGroup", _group);
                Reload();
                ViewChanged?.Invoke();
            }
        }

        private void EditNote(CaptureItem item)
        {
            var dlg = new PromptDialog(Loc("Str_Lib_NoteTitle"), Loc("Str_Lib_NoteDetail"), Loc("Str_Lib_Save"), initialText: item.Note ?? "") { Owner = OwnerWindow };
            if (dlg.ShowDialog() == true) TryChangeAlbum(() => CaptureLibrary.SetNote(item.Id, dlg.Text));
        }

        private void Copy(CaptureItem item)
        {
            try
            {
                StatusMessage?.Invoke(ImageOutput.TryCopy(CaptureLibrary.LoadImage(item), out var error)
                    ? Loc("Str_Lib_Copied")
                    : string.Format(Loc("Str_Tray_ClipboardBusy"), error));
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        private void SaveAs(CaptureItem item)
        {
            var mode = item.Mode switch { "window" => CaptureMode.Window, "monitor" => CaptureMode.Monitor, "all" => CaptureMode.AllMonitors, _ => CaptureMode.Region };
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Title = Loc("Str_Dlg_SaveTitle"),
                Filter = Loc("Str_Dlg_ImageFilter"),
                FilterIndex = CaptureSettings.Format == "jpg" ? 2 : 1,
                DefaultExt = "." + CaptureSettings.Format,
                FileName = OutputFiles.FileName(CaptureSettings.FileNamePattern, item.Taken, mode, item.Width, item.Height, CaptureSettings.Format),
                InitialDirectory = CaptureSettings.OutputFolder,
            };
            if (dlg.ShowDialog(OwnerWindow) != true) return;
            try
            {
                if (Path.GetExtension(dlg.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] png = CaptureLibrary.LoadPng(item);
                    OutputFiles.WriteAtomic(dlg.FileName, true, stream => stream.Write(png));
                }
                else
                    ImageOutput.Save(CaptureLibrary.LoadImage(item), dlg.FileName, CaptureSettings.JpegQuality);
                StatusMessage?.Invoke(dlg.FileName);
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        /// <summary>Opens the Screenshots folder in File Explorer, with a file selected when one is given.</summary>
        private void ShowInExplorer(CaptureItem? item, string? source = null)
        {
            try
            {
                string args = item != null && FolderCaptures.PathOf(item.Id) is { } path
                    ? "/select,\"" + path + "\""
                    : "\"" + FolderCaptures.PathForSource(source ?? (FolderCaptures.IsSource(_source) ? _source : "folder")) + "\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        /// <summary>Copies a file from the Screenshots folder into the encrypted library.</summary>
        private void AddToLibrary(CaptureItem item)
        {
            try
            {
                CaptureLibrary.AddNow(FolderCaptures.LoadImage(item.Id), item.Taken, "", item.Window, null);
                StatusMessage?.Invoke(Loc("Str_Lib_Added"));
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        private void Delete(CaptureItem item)
        {
            if (FolderCaptures.IsFolderId(item.Id)) return;   // the folder's files are left alone
            var dlg = new ConfirmDialog(Loc("Str_Lib_DeleteTitle"), Loc("Str_Lib_DeleteDetail"), Loc("Str_Lib_Delete"), Loc("Str_Btn_Cancel")) { Owner = OwnerWindow };
            dlg.ShowDialog();
            if (dlg.Confirmed) TryChangeAlbum(() => CaptureLibrary.Delete([item.Id]));
        }
    }
}
