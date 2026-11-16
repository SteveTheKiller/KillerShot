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
using KillerFamily.Controls;
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
        internal long Session { get; init; }
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

        /// <summary>The collection the sidebar picked. As in KillerShell, the active tab shows it
        /// when it can: a collection tab is pointed at it, and a folder tab goes to a linked
        /// folder's own folder. A folder tab cannot show an album collection, so that opens a
        /// collection tab.</summary>
        public void SetSource(string source)
        {
            if (FilesActive)
            {
                if (FolderCaptures.IsSource(source))
                {
                    _source = source;
                    NavigateFilesSource();
                    return;
                }
                if (_source == source && FileBrowser.LastTab(collection: true) is { } open && open.Source == source)
                    return;
                _source = source;
                _ = FileBrowser.NewCollectionTab(source);
                return;
            }
            if (_source == source && FileBrowser.ActiveTab.Source == source) return;
            _source = source;
            FileBrowser.RetargetActive(source);
            FileBrowser.RefreshTitles();
            if (IsLoaded) Reload(); else _dirty = true;
        }
        /// <summary>The collection the library is showing, for the sidebar to follow when a
        /// collection tab brings its own.</summary>
        internal string CurrentSource => _source;
        /// <summary>A collection tab became active with its own collection; the sidebar follows.</summary>
        public event Action<string>? SourceShown;
        public event Action<string, FrameworkElement>? LinkRequested;

        private string _view = "shelves";
        private bool _syncingSearch;
        /// <summary>A folder tab is active, so the Files page shows instead of the captures.</summary>
        public bool FilesActive => FileBrowser.ActiveIsFolder;
        public Files.FilesView Files => FileBrowser.ActiveView ?? throw new InvalidOperationException("No folder tab is active.");
        public Files.FilesWorkspace FilesWorkspace => FileBrowser;
        public Task<bool> FilesNavigation { get; private set; } = Task.FromResult(false);
        private void NavigateFilesSource(bool initialize = false)
        {
            string path = FolderCaptures.IsSource(_source) ? FolderCaptures.PathForSource(_source) : "";
            string folder = string.IsNullOrWhiteSpace(path) ? CaptureSettings.OutputFolder : path;
            FilesNavigation = initialize ? FileBrowser.EnsureInitialized(folder) : Files.NavigateTo(folder);
        }

        /// <summary>The name a collection tab wears. Album names come from the open album and are
        /// never saved with the tab.</summary>
        private string CollectionTitle(string source) => source switch
        {
            "all" => Loc("Str_Strip_All"),
            "c0" => Loc("Str_Lib_Unfiled"),
            _ when FolderCaptures.IsSource(source) => FolderCaptures.NameForSource(source),
            _ => _albumCollections.FirstOrDefault(c => "c" + c.Id.ToString(CultureInfo.InvariantCulture) == source)?.Name
                 ?? Loc("Str_Strip_Collection"),
        };

        /// <summary>Whether a collection tab's source still names something in this album.</summary>
        private bool SourceExists(string source) => source is "all" or "c0" || FolderCaptures.IsSource(source)
            || _albumCollections.Any(c => "c" + c.Id.ToString(CultureInfo.InvariantCulture) == source);

        /// <summary>Another tab became active. The outgoing collection tab keeps its search, the
        /// incoming one brings back its collection and search, and a folder tab brings its filter.</summary>
        private void OnActiveTabChanged(Files.FilesTab? previous)
        {
            if (previous is { IsCollection: true }) previous.Search = SearchBox.Text;
            var tab = FileBrowser.ActiveTab;
            _searchDelay.Stop();
            _syncingSearch = true;
            try { SearchBox.Text = tab.IsCollection ? tab.Search : Files.Navigation.FilterText; }
            finally { _syncingSearch = false; }
            SyncSearchPresentation();
            if (tab.IsCollection && tab.Source != _source)
            {
                _source = tab.Source;
                SourceShown?.Invoke(_source);
            }
            if (IsLoaded) Reload(); else _dirty = true;
            ViewChanged?.Invoke();
        }
        private string _group = "date";
        private IReadOnlyList<CaptureItem> _items = [];
        private IReadOnlyList<CaptureCollection> _collections = [];
        private IReadOnlyList<CaptureCollection> _albumCollections = [];
        private IReadOnlyList<CaptureItem> _folderItems = [];
        private IReadOnlyList<LinkedFolderListing> _linkedFolders = [];
        private readonly Dictionary<string, long> _shelfFront = [];
        private readonly DispatcherTimer _searchDelay;
        private bool _reloadQueued;
        private bool _dirty = true;
        private int _tilesPerRow;
        private long _albumSession = -1;
        // Database and filesystem boundary, replaceable by a slow reader in the in-app regression.
        internal Func<string, string, LibraryListing> ListingReader { get; set; } = LibraryListing.Read;
        internal Func<(bool IsOpen, Task Opening)> StartupReader { get; set; } = () => (CaptureLibrary.IsOpen, CaptureLibrary.Opening);
        public Task ListingTask { get; private set; } = Task.CompletedTask;
        private readonly PickerListingWork _listingWork = new();
        private bool _awaitingOpen;

        private static string Loc(string key) => LocaleManager.Loc(key);

        public LibraryView()
        {
            InitializeComponent();
            ((Panel)CollectionPage.Parent).Children.Remove(CollectionPage);
            Grid.SetRow(CollectionPage, 0);
            FileBrowser.CollectionContent = CollectionPage;
            // Files used to be a fifth view. It is a kind of tab now, so a stored "files" view
            // means the folder tab was in front and the captures fall back to shelves.
            string stored = Settings.Get("LibraryView") ?? "";
            _view = stored is "shelves" or "flow" or "grid" or "timeline" ? stored : "shelves";
            _group = Settings.Get("LibraryGroup") is "collection" ? "collection" : "date";
            FileBrowser.ConfigureCollections(CollectionTitle);
            FileBrowser.Restore(collectionFirst: stored != "files");
            if (FileBrowser.ActiveTab.IsCollection) _source = FileBrowser.ActiveTab.Source;
            FileBrowser.ResultsChanged += UpdateFilesCount;
            FileBrowser.ActiveChanged += OnActiveTabChanged;

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
                _listingWork.Cancel();
                _dirty = true;
                CaptureLibrary.Changed -= OnLibraryChanged;
                FolderCaptures.Changed -= OnLibraryChanged;
                CardColors.Changed -= OnLibraryChanged;
            };
            IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Reload(); };
            SizeChanged += (_, _) =>
            {
                if (_view is "grid" or "timeline" && TilesPerRow() != _tilesPerRow) BuildRows();
            };
            SyncSearchPresentation();
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
            SyncSearchPresentation();
            if (FilesActive)
            {
                _listingWork.Cancel();
                _dirty = false;
                HideBusy();
                PendingBar.Visibility = Visibility.Collapsed;
                UpdateFilesCount();
                if (Files.Navigation.CurrentFolder.Length == 0)
                    NavigateFilesSource(initialize: true);
                return;
            }
            if (CaptureLibrary.VideoBusy) { _listingWork.Cancel(); _dirty = true; return; }
            long session = CaptureLibrary.Session;
            if (_albumSession != session)
            {
                bool switched = _albumSession >= 0;
                _albumSession = session;
                _searchDelay.Stop();
                // Another album's collection ids mean nothing here, so those tabs go back to All.
                // The first open keeps the restored tabs; the listing drops any that are gone.
                if (switched)
                {
                    SearchBox.Clear();
                    FileBrowser.ResetMissingSources(s => s is "all" or "c0" || FolderCaptures.IsSource(s));
                    foreach (var tab in FileBrowser.Tabs) tab.Search = "";
                }
                _source = FileBrowser.ActiveTab.IsCollection ? FileBrowser.ActiveTab.Source : _source;
                _shelfFront.Clear();
                BigFlow.Items = [];
            }
            _dirty = false;
            int pending = CaptureLibrary.PendingCount;
            PendingBar.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            PendingText.Text = string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_Pending"), pending);
            var startup = StartupReader();
            RetryPendingBtn.IsEnabled = startup.IsOpen;
            if (!startup.IsOpen && !startup.Opening.IsCompleted)
            {
                // Still opening at startup: the loading screen, then this again.
                ShowBusy();
                if (!_awaitingOpen)
                {
                    _awaitingOpen = true;
                    startup.Opening.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _awaitingOpen = false;
                        HideBusy();
                        if (IsLoaded && IsVisible) Reload(); else _dirty = true;
                    })), TaskScheduler.Default);
                }
                return;
            }
            if (!startup.IsOpen)
            {
                _listingWork.Cancel();
                ShowLock();
                return;
            }
            LockPanel.Visibility = Visibility.Collapsed;
            string search = SearchBox.Text.Trim();
            string source = _source;
            var reader = ListingReader;
            ListingTask = _listingWork.RunAsync(_ => reader(source, search), (listing, token) =>
            {
                if (!FilesActive && CaptureLibrary.Session == session && source == _source && search == SearchBox.Text.Trim())
                    ApplyListing(listing, search);
                return Task.CompletedTask;
            }, error => StatusMessage?.Invoke(error.Message));
        }

        private void ApplyListing(LibraryListing listing, string search)
        {
            _items = listing.Items;
            _collections = listing.Collections;
            _albumCollections = listing.AlbumCollections;
            _folderItems = listing.FolderItems;
            _linkedFolders = listing.LinkedFolders;
            int total = listing.Total;
            FileBrowser.ResetMissingSources(SourceExists);
            FileBrowser.RefreshTitles();
            if (FileBrowser.ActiveTab.IsCollection && FileBrowser.ActiveTab.Source != _source)
            {
                // The shown collection was deleted, so its tab went back to All; show that instead.
                _source = FileBrowser.ActiveTab.Source;
                SourceShown?.Invoke(_source);
                Reload();
                return;
            }

            SyncHeader();
            string? folderStatus = FolderReadStatus();
            CountText.Text = search.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_CountMatches"), _items.Count, total)
                : string.Format(CultureInfo.CurrentCulture, Loc(total == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), total);
            if (folderStatus != null) CountText.Text += " (" + Loc(folderStatus) + ")";
            CountChanged?.Invoke(CountText.Text);

            // The Screenshots folder shows as a collection, so only where collections do.
            bool linkedPhotos = listing.LinkedPhotos;
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
                foreach (var folder in _source == "all" ? _linkedFolders : [])
                {
                    if (folder.Members.Count > 0 || !searching)
                        groups.Add(new Group(folder.Source, folder.Name, FolderCaptures.CollectionId, false, folder.Members.ToList(), IsFolder: true));
                }
                var byId = _items.ToLookup(i => i.CollectionId);
                foreach (var c in _collections)
                {
                    var members = byId[c.Id].ToList();
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
                // Inside one collection, grouping by collection would leave a single carousel,
                // the same as Single carousel. Multiple carousels then gives one per day.
                foreach (var g in Groups(_group == "collection" && _source == "all"))
                {
                    rows.Add(new LibraryRow
                    {
                        Kind = g.Items.Count > 0 ? LibraryRowKind.Shelf : LibraryRowKind.Header,
                        Key = g.Key, Title = g.Title,
                        Detail = g.Items.Count > 0 ? CountLabel(g.Items.Count) : Loc("Str_Lib_EmptyCollection"),
                        CollectionId = g.CollectionId, IsCollection = g.IsCollection, IsFolder = g.IsFolder, Items = g.Items,
                        Session = _collections.FirstOrDefault(c => c.Id == g.CollectionId)?.Session ?? _albumSession,
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
                        Session = _collections.FirstOrDefault(c => c.Id == g.CollectionId)?.Session ?? _albumSession,
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
            var line = new CollectionHeader { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 8) };
            if (row.IsFolder)
            {
                // A folder on disk, not a collection in the library: a folder mark, and its menu
                // opens it in File Explorer.
                var mark = new TextBlock { Text = "\uF12B", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Margin = new Thickness(0, 0, 8, 2), VerticalAlignment = VerticalAlignment.Bottom };
                mark.SetResourceReference(TextBlock.ForegroundProperty, "SelectionAccent");
                line.Children.Add(mark);
                line.Background = Brushes.Transparent;
                // The linked folder's card: open it as a collection tab or as a folder tab, or in
                // File Explorer.
                string folderSource = row.Key;
                line.ContextMenu = new ContextMenu();
                MakeCard(line.ContextMenu);
                line.ContextMenu.Items.Add(CardBuilder.Header(row.Title, FolderCaptures.PathForSource(folderSource)));
                line.ContextMenu.Items.Add(CardBuilder.QuickStrip(Loc("Str_Card_Quick"),
                [
                    new("", Loc("Str_Tab_Open"), Loc("Str_Tab_OpenCollection"), null, () => _ = FileBrowser.NewCollectionTab(folderSource)),
                    new("", Loc("Str_Tab_Browse"), Loc("Str_Tab_BrowseFolder"), null, () => _ = FileBrowser.NewTab(FolderCaptures.PathForSource(folderSource))),
                    new("", Loc("Str_Tab_Explorer"), Loc("Str_Lib_OpenFolder"), null, () => ShowInExplorer(null, folderSource)),
                ]));
                if (KillerPdfHandoff.IsInstalled)
                    line.ContextMenu.Items.Add(CardBuilder.Row(Loc("Str_Menu_MakePdf"), "", "", () =>
                    {
                        if (KillerPdfHandoff.MessageKey(KillerPdfHandoff.MakePdf(FolderCaptures.PathForSource(folderSource))) is { } problem)
                            ConfirmDialog.ShowNotice(Loc(problem), Window.GetWindow(this));
                    }, true));
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
                line.ContextMenuOpening += (_, e) => { FillCollectionMenu(line.ContextMenu, id, row.Title, row.Session, row.Detail); };
                AutomationProperties.SetName(line, row.Title + ", " + row.Detail);
            }
            if (line.ContextMenu != null)
            {
                line.Focusable = true;
                KeyboardNavigation.SetIsTabStop(line, true);
                line.OpenMenu = () =>
                {
                    if (row.IsCollection && row.CollectionId is long collectionId)
                        FillCollectionMenu(line.ContextMenu, collectionId, row.Title, row.Session, row.Detail);
                    line.ContextMenu.PlacementTarget = line;
                    line.ContextMenu.Placement = PlacementMode.Bottom;
                    line.ContextMenu.IsOpen = true;
                };
                line.KeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Apps or Key.Enter or Key.Space) &&
                        !(e.SystemKey == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) return;
                    line.OpenMenu();
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
                ShelfMode = true,
                PerspectiveZoomEnabled = true,
                ViewCenterY = 0.255,
                ThumbnailSource = CaptureLibrary.RequestThumb,
                DeferHiddenLoading = true,
                Describe = item => row.Title + ", " + Describe(item),
            };
            flow.Items = row.Items;
            if (_shelfFront.TryGetValue(row.Key, out long front)) flow.Select(front);
            // Opening on the middle picture fills both sides of the band instead of leaving the left half empty.
            else if (row.Items.Count > 2) flow.Select(row.Items[row.Items.Count / 2].Id);
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
                // A subtle secondary surface separates default cards from the content pane.
                bool picked = !CardColors.CardIsDefault;
                caption.SetResourceReference(TextBlock.ForegroundProperty, picked ? "CaptureCardTextBrush" : "TextBrush");
                detail.SetResourceReference(TextBlock.ForegroundProperty, picked ? "CaptureCardMutedBrush" : "MutedTextBrush");
                var content = new StackPanel();
                content.Children.Add(VideoMark.Wrap(QrMark.Wrap(image, item.HasQr), VideoCaptures.IsVideo(item)));
                content.Children.Add(caption);
                content.Children.Add(detail);
                var tile = new Button { Style = (Style)FindResource("LibTile"), Content = content, Tag = item };
                tile.SetResourceReference(BackgroundProperty, picked ? "CaptureCardBrush" : "SurfaceBrush");
                if (!SystemParameters.HighContrast && CardColors.Collection(item.CollectionId) is Color own)
                {
                    tile.BorderBrush = new SolidColorBrush(own);
                }
                AutomationProperties.SetName(tile, Describe(item));
                tile.ToolTip = Describe(item);
                tile.Cursor = DragCursors.Open;
                ImageDrag.AttachCapture(tile, _ => item);
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

        /// <summary>The view: shelves, flow (the carousel), grid, timeline or files.</summary>
        public string CurrentView => FilesActive ? "files" : _view;

        /// <summary>The grouping: date or collection. The timeline is always by date.</summary>
        public string CurrentGroup => _view == "timeline" ? "date" : _group;

        /// <summary>The view or grouping changed, so the footer's radio buttons follow.</summary>
        public event Action? ViewChanged;

        /// <summary>Picks a view from the footer or F5 to F9, and remembers it.</summary>
        public void SetView(string view)
        {
            if (view is not ("shelves" or "flow" or "grid" or "timeline" or "files")) return;
            Settings.Set("LibraryView", view);
            // Files is a folder tab and the capture views belong to collection tabs, so picking
            // one brings the last tab of that kind forward, or opens one. The tab switch reloads.
            if (view == "files")
            {
                if (FilesActive) return;
                if (FileBrowser.LastTab(collection: false) is { } folder) _ = FileBrowser.SelectTab(folder);
                else _ = FileBrowser.NewTab(CaptureSettings.OutputFolder);
                return;
            }
            _view = view;
            if (FilesActive)
            {
                if (FileBrowser.LastTab(collection: true) is { } open) _ = FileBrowser.SelectTab(open);
                else _ = FileBrowser.NewCollectionTab(_source);
                return;
            }
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
            if (_syncingSearch) return;
            if (FilesActive) { Files.SetFilter(SearchBox.Text); return; }
            _searchDelay.Stop();
            _searchDelay.Start();
        }

        private void UpdateFilesCount()
        {
            if (!FilesActive) return;
            int folders = 0, files = 0;
            foreach (Files.FileEntry entry in CollectionViewSource.GetDefaultView(Files.Navigation.Results))
                if (entry.IsDirectory) folders++; else files++;
            CountText.Text = string.Format(CultureInfo.CurrentCulture, Loc("Str_Sum_Counts"), folders, files);
            CountChanged?.Invoke(CountText.Text);
        }

        private void SyncSearchPresentation()
        {
            string key = FilesActive ? "Str_TT_FilterResults" : "Str_Lib_Search";
            SearchHint.SetResourceReference(TextBlock.TextProperty, key);
            SearchBox.SetResourceReference(AutomationProperties.NameProperty, key);
            SearchAnchor?.SetResourceReference(ToolTipProperty, key);
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
            // Unlock reports a damaged album as false with a reason, not as a wrong password.
            if (!ok && openError == null && CaptureLibrary.LoadError is { } loadError) openError = loadError;
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
            // The capture card, KillerShell 1.6.0's pattern: the capture's name and when and how big
            // it is, a strip of the everyday actions, then the rest as rows. Every action shows
            // its shortcut, and a stale card does nothing once the album has changed under it.
            bool keyboard = Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && !anchor.IsMouseOver;
            var menu = CardBuilder.Menu(320);
            menu.Items.Add(CardBuilder.Header(Title(item), item.Taken.ToString("g", CultureInfo.CurrentCulture) + "  ·  "
                + item.Width.ToString(CultureInfo.CurrentCulture) + " x " + item.Height.ToString(CultureInfo.CurrentCulture)));
            Action Guard(Action action) => () => { if (CaptureLibrary.IsCurrent(item)) action(); };
            CardAction Quick(string glyph, string key, string? gesture, Action action) => new(glyph, Loc(key), Loc(key), gesture, Guard(action));
            void Row(string key, string? gesture, string glyph, Action action) => menu.Items.Add(CardBuilder.Row(Loc(key), gesture, glyph, Guard(action)));
            void Rows(params object[] rows) { foreach (var row in rows) menu.Items.Add(row); }
            var quick = new List<CardAction> { Quick("", "Str_Lib_Open", "Enter", () => open?.Invoke(item)) };
            bool video = VideoCaptures.IsVideo(item), inFolder = FolderCaptures.IsFolderId(item.Id);
            void Print()
            {
                try { PrintOutput.Print(CaptureLibrary.LoadOutput(item), Title(item)); }
                catch (Exception ex) { StatusMessage?.Invoke(string.Format(Loc("Str_Status_PrintFailed"), ex.Message)); }
            }
            // A recording has no picture to copy, print or recognize, only the clip itself.
            if (!video)
            {
                quick.Add(Quick("", "Str_Lib_Copy", "Ctrl+C", () => Copy(item)));
                quick.Add(Quick("", "Str_Btn_Print", "Ctrl+P", Print));
            }
            if (inFolder) quick.Add(Quick("", "Str_Lib_ShowInExplorer", "Ctrl+E", () => ShowInExplorer(item)));
            else quick.Add(Quick("", "Str_Lib_SaveAs", "Ctrl+Shift+S", () => SaveAs(item)));
            quick.Add(Quick("", "Str_Lib_Delete", deleteGesture, () => Delete(item)));
            menu.Items.Add(CardBuilder.QuickStrip(Loc("Str_Card_Quick"), quick));

            if (inFolder)
            {
                // A file in a folder collection: the folder keeps it, so no collections or notes,
                // only a way into the encrypted library.
                if (video) Row("Str_Rec_MoveToLibrary", null, "", () => MoveVideoToLibrary(item));
                else Row("Str_Lib_AddToLibrary", null, "", () => AddToLibrary(item));
            }
            else
            {
                var move = new MenuItem { Header = Loc("Str_Lib_MoveTo"), InputGestureText = "Ctrl+M", Icon = "" };
                FillMoveItems(move, item);
                Rows(move);
                Row("Str_Lib_Note", "F2", "", () => EditNote(item));
                if (!video)
                {
                    Row("Str_Marks_OpenOriginal", null, "", () => RecoverOriginal(item));
                    if (BuildOcrLinksMenu(item.Id, anchor) is { } links) { links.Icon = ""; Rows(links); }
                    Rows(new Separator());
                    Row("Str_Lib_CardColor", null, "", PickCardColor);
                    if (!CardColors.CardIsDefault) Row("Str_Lib_CardColorDefault", null, "", () => CardColors.SetCard(null));
                }
            }
            MenuTidy.HideInactive(menu);
            CardBuilder.Open(menu, anchor, keyboard, new Rect(0, anchor.ActualHeight, 0, 0));
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

        internal Func<PromptDialog, bool?> PresentPrompt { get; set; } = dialog => dialog.ShowDialog();

        /// <summary>Gives a header's own ContextMenu the card look at the card's width.</summary>
        private static void MakeCard(ContextMenu menu, double width = 300)
        {
            menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
            menu.MinWidth = menu.MaxWidth = width + 44;
        }

        /// <summary>The card for a folder on disk shown as a collection (Screenshots or a linked
        /// folder): open it as a collection tab, a folder tab or in File Explorer, and Make PDF.</summary>
        internal void FillFolderMenu(ContextMenu menu, string folderSource, string title)
        {
            menu.Items.Clear();
            MakeCard(menu);
            menu.Items.Add(CardBuilder.Header(title, FolderCaptures.PathForSource(folderSource)));
            menu.Items.Add(CardBuilder.QuickStrip(Loc("Str_Card_Quick"),
            [
                new("", Loc("Str_Tab_Open"), Loc("Str_Tab_OpenCollection"), null, () => _ = FileBrowser.NewCollectionTab(folderSource)),
                new("", Loc("Str_Tab_Browse"), Loc("Str_Tab_BrowseFolder"), null, () => _ = FileBrowser.NewTab(FolderCaptures.PathForSource(folderSource))),
                new("", Loc("Str_Tab_Explorer"), Loc("Str_Lib_OpenFolder"), null, () => ShowInExplorer(null, folderSource)),
            ]));
            if (KillerPdfHandoff.IsInstalled)
                menu.Items.Add(CardBuilder.Row(Loc("Str_Menu_MakePdf"), "", "", () =>
                {
                    if (KillerPdfHandoff.MessageKey(KillerPdfHandoff.MakePdf(FolderCaptures.PathForSource(folderSource))) is { } problem)
                        ConfirmDialog.ShowNotice(Loc(problem), Window.GetWindow(this));
                }, true));
        }

        /// <summary>The collection card: its name and count, a strip to open it in a tab of its own
        /// or color it, then rename and delete as rows.</summary>
        internal void FillCollectionMenu(ContextMenu menu, long id, string name, long session, string? detail = null)
        {
            menu.Items.Clear();
            MakeCard(menu);
            menu.Items.Add(CardBuilder.Header(name, detail));
            string source = "c" + id.ToString(CultureInfo.InvariantCulture);
            void PickColor()
            {
                var dlg = new ColorPickerDialog(OwnerWindow, CardColors.Collection(id) ?? Color.FromRgb(0x50, 0xAE, 0xE8));
                dlg.ShowDialog();
                if (dlg.Accepted) TryChangeAlbum(() => CardColors.SetCollection(id, dlg.SelectedColor, session));
            }
            menu.Items.Add(CardBuilder.QuickStrip(Loc("Str_Card_Quick"),
            [
                new("", Loc("Str_Tab_Open"), Loc("Str_Tab_OpenCollection"), null,
                    () => { if (CaptureLibrary.Session == session) _ = FileBrowser.NewCollectionTab(source); }),
                new("", Loc("Str_Lib_ColorShort"), Loc("Str_Lib_CollectionColor"), null, PickColor),
            ]));
            menu.Items.Add(CardBuilder.Row(Loc("Str_Lib_RenameCollection"), null, "", () =>
            {
                var dlg = new PromptDialog(Loc("Str_Lib_RenameCollectionTitle"), "", Loc("Str_Lib_Rename"), initialText: name) { Owner = OwnerWindow };
                if (PresentPrompt(dlg) == true && dlg.Text.Trim().Length > 0)
                    TryChangeAlbum(() => CaptureLibrary.RenameCollection(id, dlg.Text, session));
            }));
            if (CardColors.Collection(id) != null)
                menu.Items.Add(CardBuilder.Row(Loc("Str_Lib_CollectionColorNone"), null, "",
                    () => TryChangeAlbum(() => CardColors.SetCollection(id, null, session))));
            if (KillerPdfHandoff.IsInstalled)
                menu.Items.Add(CardBuilder.Row(Loc("Str_Menu_MakePdf"), "", "", async () =>
                {
                    if (KillerPdfHandoff.MessageKey(await KillerPdfHandoff.MakePdfFromCollectionAsync(id, session)) is { } problem)
                        ConfirmDialog.ShowNotice(Loc(problem), Window.GetWindow(this));
                }, true));
            menu.Items.Add(new Separator());
            menu.Items.Add(CardBuilder.Row(Loc("Str_Lib_DeleteCollection"), null, "", () =>
            {
                var dlg = new ConfirmDialog(string.Format(CultureInfo.CurrentCulture, Loc("Str_Lib_DeleteCollectionTitle"), name),
                    Loc("Str_Lib_DeleteCollectionDetail"), Loc("Str_Lib_DeleteCollection"), Loc("Str_Btn_Cancel")) { Owner = OwnerWindow };
                if (PresentConfirmation(dlg)) TryChangeAlbum(() => CaptureLibrary.DeleteCollection(id, session));
            }));
        }

        /// <summary>Picks the color of every card captures sit on.</summary>
        internal void PickCardColor()
        {
            var dlg = new ColorPickerDialog(OwnerWindow, CardColors.Card);
            dlg.ShowDialog();
            if (dlg.Accepted) CardColors.SetCard(dlg.SelectedColor);
        }

        internal void NewCollection(CaptureItem? moveIn)
        {
            long session = moveIn?.Session ?? CaptureLibrary.Session;
            var dlg = new PromptDialog(Loc("Str_Lib_NewCollectionTitle"), "", Loc("Str_Lib_Create")) { Owner = OwnerWindow };
            if (PresentPrompt(dlg) != true || dlg.Text.Trim().Length == 0) return;
            if (!TryChangeAlbum(() => CaptureLibrary.CreateCollection(dlg.Text, moveIn != null ? [moveIn.Id] : null, session))) return;
            if (_group != "collection" && _view != "timeline")
            {
                _group = "collection";
                Settings.Set("LibraryGroup", _group);
                Reload();
                ViewChanged?.Invoke();
            }
        }

        internal void EditNote(CaptureItem item)
        {
            var dlg = new PromptDialog(Loc("Str_Lib_NoteTitle"), Loc("Str_Lib_NoteDetail"), Loc("Str_Lib_Save"), initialText: item.Note ?? "") { Owner = OwnerWindow };
            if (PresentPrompt(dlg) == true) TryChangeAlbum(() => CaptureLibrary.SetNote(item.Id, dlg.Text, item.Session));
        }

        /// <summary>The capture the keyboard is on: a focused tile, or the current card of a focused
        /// carousel. Null when focus is elsewhere, so a shortcut never falls back to another image.</summary>
        internal CaptureItem? FocusedCapture()
        {
            for (var d = Keyboard.FocusedElement as DependencyObject; d != null && d != this; d = UiParent(d))
            {
                if (d is Button { Tag: CaptureItem tile }) return CaptureLibrary.IsCurrent(tile) ? tile : null;
                if (d is CoverFlow flow) return flow.SelectedItem is { } f && CaptureLibrary.IsCurrent(f) ? f : null;
            }
            return null;
        }

        /// <summary>The visual parent, or the logical one for a content element such as a focused Hyperlink,
        /// which VisualTreeHelper refuses.</summary>
        internal static DependencyObject? UiParent(DependencyObject d) =>
            d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        /// <summary>Ctrl+C over the album: copies the focused capture, or reports there is none.</summary>
        internal void CopyFocused()
        {
            if (FocusedCapture() is { } item) Copy(item);
            else StatusMessage?.Invoke(Loc("Str_Lib_NoCaptureFocused"));
        }

        /// <summary>The Move to list: no collection, each collection, then New collection.</summary>
        private void FillMoveItems(ItemsControl target, CaptureItem item)
        {
            var none = new MenuItem { Header = Loc("Str_Lib_Unfiled"), IsCheckable = true, IsChecked = item.CollectionId == null };
            none.Click += (_, _) => TryChangeAlbum(() => CaptureLibrary.Move([item.Id], null, item.Session));
            target.Items.Add(none);
            foreach (var c in _albumCollections)
            {
                var collection = c;
                var mi = new MenuItem { Header = c.Name, IsCheckable = true, IsChecked = item.CollectionId == c.Id };
                mi.Click += (_, _) => TryChangeAlbum(() => CaptureLibrary.Move([item.Id], collection.Id, item.Session));
                target.Items.Add(mi);
            }
            target.Items.Add(new Separator());
            var create = new MenuItem { Header = Loc("Str_Lib_NewCollectionDots") };
            create.Click += (_, _) => NewCollection(item);
            target.Items.Add(create);
        }

        // Keys over the album act on the focused capture, as Ctrl+C and Ctrl+P do.
        private bool WithFocused(Action<CaptureItem> action)
        {
            if (FocusedCapture() is not { } item) { StatusMessage?.Invoke(Loc("Str_Lib_NoCaptureFocused")); return false; }
            action(item);
            return true;
        }

        /// <summary>Ctrl+Shift+S over the album.</summary>
        internal void SaveAsFocused() => WithFocused(item =>
        {
            if (!FolderCaptures.IsFolderId(item.Id)) SaveAs(item);
        });

        /// <summary>Ctrl+E over the album: a Screenshots-folder file, shown in Explorer.</summary>
        internal void ShowFocusedInExplorer() => WithFocused(item =>
        {
            if (FolderCaptures.IsFolderId(item.Id)) ShowInExplorer(item);
        });

        /// <summary>F2 over the album: the focused capture's note.</summary>
        internal void NoteFocused() => WithFocused(item =>
        {
            if (!FolderCaptures.IsFolderId(item.Id)) EditNote(item);
        });

        /// <summary>Ctrl+M over the album: the Move to list, opened on the focused capture.</summary>
        internal void MoveFocused() => WithFocused(item =>
        {
            if (FolderCaptures.IsFolderId(item.Id)) return;
            var menu = new ContextMenu { PlacementTarget = Keyboard.FocusedElement as UIElement ?? this };
            FillMoveItems(menu, item);
            menu.IsOpen = true;
        });

        /// <summary>Ctrl+P over the album: prints the focused capture, or reports there is none.</summary>
        internal void PrintFocused()
        {
            if (FocusedCapture() is not { } item) { StatusMessage?.Invoke(Loc("Str_Lib_NoCaptureFocused")); return; }
            if (VideoCaptures.IsVideo(item)) return;
            try { PrintOutput.Print(CaptureLibrary.LoadOutput(item), Title(item)); }
            catch (Exception ex) { StatusMessage?.Invoke(string.Format(Loc("Str_Status_PrintFailed"), ex.Message)); }
        }

        private void Copy(CaptureItem item)
        {
            if (VideoCaptures.IsVideo(item)) return;
            try
            {
                StatusMessage?.Invoke(ImageOutput.TryCopy(CaptureLibrary.LoadOutput(item), out var error)
                    ? Loc("Str_Lib_Copied")
                    : string.Format(Loc("Str_Tray_ClipboardBusy"), error));
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        internal Func<FileDialog, Window?, bool?> PresentExportDialog { get; set; } = (dialog, owner) => dialog.ShowDialog(owner);

        internal void SaveAs(CaptureItem item)
        {
            if (VideoCaptures.IsVideo(item)) { SaveVideoAs(item); return; }
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
            if (PresentExportDialog(dlg, OwnerWindow) != true) return;
            try
            {
                if (Path.GetExtension(dlg.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] png = CaptureLibrary.LoadOutputPng(item);
                    OutputFiles.WriteAtomic(dlg.FileName, true, stream => stream.Write(png));
                }
                else
                    ImageOutput.Save(CaptureLibrary.LoadOutput(item), dlg.FileName, CaptureSettings.JpegQuality);
                StatusMessage?.Invoke(dlg.FileName);
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        private void RecoverOriginal(CaptureItem item)
        {
            var dialog = new ConfirmDialog(Loc("Str_Marks_OpenOriginal"), Loc("Str_Marks_OriginalWarning"),
                Loc("Str_Lib_Open"), Loc("Str_Btn_Cancel")) { Owner = OwnerWindow };
            dialog.ShowDialog();
            if (!dialog.Confirmed) return;
            try { App.Instance.RecoverLibraryOriginal(item); }
            catch (Exception error) { StatusMessage?.Invoke(error.Message); }
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

        /// <summary>The Save As name for a recording: its window title made safe for a file name
        /// (characters Windows refuses become _, trailing dots and spaces go), or the recording
        /// name when nothing is left. The title is used as it reads, never as a name pattern.</summary>
        internal static string VideoSaveName(string? window, string ext)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string name = new string((window ?? "").Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            return (name.Length == 0 ? Loc("Str_Rec_CollectionName") : name) + ext;
        }

        /// <summary>Writes a stored recording out as the video file it was, byte for byte.</summary>
        private async void SaveVideoAs(CaptureItem item)
        {
            string ext;
            try { ext = CaptureLibrary.VideoExtension(item.Id, item.Session); }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); return; }
            bool gif = ext == ".gif";
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Title = Loc("Str_Dlg_SaveTitle"),
                Filter = Loc(gif ? "Str_VS_FormatGif" : "Str_VS_FormatMp4") + "|*" + ext,
                DefaultExt = ext,
                FileName = VideoSaveName(item.Window, ext),
                InitialDirectory = VideoSettings.OutputFolder,
            };
            if (PresentExportDialog(dlg, OwnerWindow) != true) return;
            string destination = dlg.FileName;
            try
            {
                await Task.Run(() => CaptureLibrary.ExportVideo(item.Id, destination, item.Session));
                StatusMessage?.Invoke(destination);
            }
            catch (Exception ex) { StatusMessage?.Invoke(ex.Message); }
        }

        /// <summary>Moves a recording from the folder into the encrypted library. Unlike a picture's
        /// Add to library, the plain file goes, since a copy left behind would undo the encryption.</summary>
        private async void MoveVideoToLibrary(CaptureItem item)
        {
            if (FolderCaptures.PathOf(item.Id) is not { } path) { StatusMessage?.Invoke(Loc("Str_Lib_Missing")); return; }
            try
            {
                var result = await RecordingStorage.MoveToLibraryAsync(path, item.Taken);
                StatusMessage?.Invoke(result.Warning(path) ?? Loc("Str_Rec_SavedToLibrary"));
            }
            catch (VideoTooLargeException) { StatusMessage?.Invoke(string.Format(Loc("Str_Rec_TooLarge"), RecordingStorageDialog.ShortFolder(Path.GetDirectoryName(path)!))); }
            catch (RecordingNotRetainedException) { StatusMessage?.Invoke(string.Format(Loc("Str_Rec_NotRetained"), RecordingStorageDialog.ShortFolder(Path.GetDirectoryName(path)!))); }
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

        internal Func<ConfirmDialog, bool> PresentConfirmation { get; set; } = dialog => { dialog.ShowDialog(); return dialog.Confirmed; };

        /// <summary>Test seam: sends a folder file to the Recycle Bin.</summary>
        internal Action<long, IntPtr> RecycleFolderFile { get; set; } = FolderCaptures.Recycle;

        internal async void Delete(CaptureItem item)
        {
            if (FolderCaptures.IsFolderId(item.Id))
            {
                // A folder file is not in the encrypted album, so Delete sends the file itself to
                // the Recycle Bin, where a mistake can still be undone. A folder with no Recycle Bin
                // (a network share, a removable drive) says the file goes for good before asking.
                // A file that is already gone has nothing to delete, for good or otherwise.
                if (FolderCaptures.PathOf(item.Id) is not { } path) { StatusMessage?.Invoke(Loc("Str_Lib_Missing")); return; }
                bool recyclable = FolderCaptures.CanRecycle(path);
                var confirm = new ConfirmDialog(Loc("Str_Lib_DeleteTitle"), Loc(recyclable ? "Str_Lib_RecycleDetail" : "Str_Lib_PermanentDetail"),
                    Loc("Str_Lib_Delete"), Loc("Str_Btn_Cancel")) { Owner = OwnerWindow };
                if (!PresentConfirmation(confirm)) return;
                try
                {
                    if (recyclable)
                        RecycleFolderFile(item.Id, OwnerWindow is { } owner ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero);
                    else await FolderCaptures.DeleteForGoodAsync(item.Id);
                }
                catch (Exception ex) { StatusMessage?.Invoke(ex.Message); return; }
                // The editor closes the deleted picture unless it holds unsaved edits: those live
                // only in the editor, so they stay there to be saved again or discarded.
                if (App.Instance.CurrentItem is { } shown && shown.Id == item.Id && shown.Session == item.Session
                    && App.Instance.Document?.Dirty != true)
                    App.Instance.CompleteAlbumSwitch();
                return;
            }
            var dlg = new ConfirmDialog(Loc("Str_Lib_DeleteTitle"), Loc("Str_Lib_DeleteDetail"), Loc("Str_Lib_Delete"), Loc("Str_Btn_Cancel")) { Owner = OwnerWindow };
            if (!PresentConfirmation(dlg) || !TryChangeAlbum(() => CaptureLibrary.Delete([item.Id], item.Session))) return;
            // The editor no longer has a capture to keep its marks in, so it closes the picture.
            if (App.Instance.CurrentItem is { } open && open.Id == item.Id && open.Session == item.Session)
                App.Instance.CompleteAlbumSwitch();
        }
    }
}
