using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerShot.Files;

/// <summary>One tab of the library's strip. A folder tab owns a Files page; a collection tab
/// shows one album collection through the library's own view, which every collection tab
/// shares (there is one library surface, never a copy per tab).</summary>
public sealed class FilesTab : INotifyPropertyChanged
{
    internal FilesTab(bool collection, string source)
    {
        IsCollection = collection;
        _source = source;
        if (!collection) View = new FilesView();
    }
    /// <summary>The Files page of a folder tab; null on a collection tab.</summary>
    public FilesView? View { get; }
    public bool IsCollection { get; }
    public bool IsFolder => !IsCollection;
    private string _source;
    /// <summary>The library source a collection tab shows: all, c0, c&lt;id&gt; or a linked folder.</summary>
    public string Source { get => _source; internal set { _source = value; Notify(); } }
    /// <summary>The capture search typed on this collection tab. Kept in memory only: it can
    /// name what is inside the encrypted album, so it never reaches the plain settings file.</summary>
    internal string Search { get; set; } = "";
    internal double ScrollOffset { get; set; }
    internal long Activated { get; set; }
    internal Func<string, string>? CollectionTitle { get; set; }
    private string Folder => View == null ? "" : View.Navigation.CurrentFolder.Length > 0 ? View.Navigation.CurrentFolder : Pending?.Folder ?? "";
    public ImageSource? Icon => IsCollection ? null : Browser.IsThisPc(Folder) ? IconCache.ForComputer(32) : IconCache.For(Folder, 32, isDirectory: true);
    /// <summary>The library's shelves glyph marks a collection tab, where a folder tab has its shell icon.</summary>
    public string Glyph => IsCollection ? "" : "";
    public string Title => IsCollection ? CollectionTitle?.Invoke(Source) ?? Services.LocaleManager.Loc("Str_Strip_All")
        : Browser.IsThisPc(Folder) ? Services.LocaleManager.Loc("Str_Nav_ThisPc")
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name
        : Folder.Length > 0 ? Folder : Services.LocaleManager.Loc("Str_Lib_ViewFiles");
    public string CloseLabel => $"{Services.LocaleManager.Loc("Str_TT_CloseTab")}: {Title}";
    public bool IsActive { get; internal set; }
    public bool IsFirst { get; internal set; }
    public bool IsLast { get; internal set; }
    public bool IsStripVisible { get; internal set; } = true;
    public bool UseRetroTabChrome => Services.ThemeManager.Current == Services.Theme.SE98;
    public bool PaneFocused => false;
    public bool PaneDimmed => false;
    internal FilesTabState? Pending;
    internal Task<bool>? Loading;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class FilesTabState
{
    /// <summary>"collection" for a collection tab. Sessions saved before tabs had kinds hold
    /// folder tabs only and read back as folders.</summary>
    public string Kind { get; set; } = "folder";
    /// <summary>A collection tab's source id. Only the id: collection names live inside the
    /// encrypted album and are looked up when it is open.</summary>
    public string Source { get; set; } = "";
    public string Folder { get; set; } = "";
    public string[] History { get; set; } = [];
    public int HistoryIndex { get; set; } = -1;
    public string Filter { get; set; } = "";
    public string[] Selection { get; set; } = [];
    public double Horizontal { get; set; }
    public double Vertical { get; set; }
    public int Mode { get; set; }
    public int Sort { get; set; } = 4;
    public bool Ascending { get; set; }
    public bool Hidden { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public int Density { get; set; } = 1;
    public int TileSize { get; set; } = 96;
    public int RowIconSize { get; set; } = 16;
    public double[] Columns { get; set; } = [];
    public bool SizeVisible { get; set; } = true;
    public bool ModifiedVisible { get; set; } = true;
    public bool TypeVisible { get; set; } = true;
    public bool CreatedVisible { get; set; }
}

public partial class FilesWorkspace : UserControl, IDisposable
{
    private sealed class Session
    {
        public int Active { get; set; }
        public FilesTabState[] Tabs { get; set; } = [];
    }
    private readonly ObservableCollection<FilesTab> _tabs = [];
    public ReadOnlyObservableCollection<FilesTab> Tabs { get; }
    public FilesTab ActiveTab { get; private set; } = null!;
    /// <summary>The active tab's Files page, or null while a collection tab is active.</summary>
    public FilesView? ActiveView => ActiveTab.View;
    public bool ActiveIsFolder => ActiveTab.IsFolder;
    public event Action? ResultsChanged;
    /// <summary>Raised after another tab became active, with the tab that was active before.</summary>
    public event Action<FilesTab?>? ActiveChanged;
    private readonly DispatcherTimer _saveTimer;
    private bool _disposed;
    private Func<string, bool>? _canOpen;
    private Func<string, Task<bool>>? _open;
    private Func<string, string>? _collectionTitle;
    private Point _dragStart;
    private int _tabWindow;
    private long _activation;

    public static readonly DependencyProperty CollectionContentProperty = DependencyProperty.Register(
        nameof(CollectionContent), typeof(object), typeof(FilesWorkspace));
    /// <summary>The library's own view, shown as the page of every collection tab.</summary>
    public object? CollectionContent
    {
        get => GetValue(CollectionContentProperty);
        set => SetValue(CollectionContentProperty, value);
    }

    public FilesWorkspace()
    {
        InitializeComponent();
        Tabs = new(_tabs);
        TabStrip.ItemsSource = _tabs;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSession(); };
        Restore(collectionFirst: false);
        Loaded += (_, _) =>
        {
            Services.ThemeManager.ThemeChanged -= UpdateTabLayout;
            Services.ThemeManager.ThemeChanged += UpdateTabLayout;
            UpdateTabLayout();
        };
        Unloaded += (_, _) => Services.ThemeManager.ThemeChanged -= UpdateTabLayout;
    }

    /// <summary>Reads the saved tabs again. The library calls this once with what it showed last:
    /// a session saved before collection tabs existed holds only folder tabs, and a library that
    /// was showing captures puts its collection tab first and active so it opens where it was.</summary>
    internal void Restore(bool collectionFirst)
    {
        foreach (var tab in _tabs) tab.View?.Dispose();
        _tabs.Clear();
        Pages.Children.Clear();
        Session? session = null;
        try { session = JsonSerializer.Deserialize<Session>(Settings.Get("Files.Tabs") ?? "null"); }
        catch (JsonException) { }
        foreach (var state in session?.Tabs ?? [])
            if (state != null) AddTab(state);
        int active = Math.Clamp(session?.Active ?? 0, 0, Math.Max(0, _tabs.Count - 1));
        if (collectionFirst && !_tabs.Any(tab => tab.IsCollection))
        {
            InsertTab(0, NewCollectionTabCore("all"));
            active = 0;
        }
        if (_tabs.Count == 0) AddCollection("all");
        SelectCore(_tabs[Math.Clamp(active, 0, _tabs.Count - 1)]);
    }

    public void ConfigureEditor(Func<string, bool> canOpen, Func<string, Task<bool>> open)
    {
        _canOpen = canOpen;
        _open = open;
        foreach (var tab in _tabs) if (tab.View != null) Configure(tab.View);
    }
    /// <summary>Names a collection tab from its source while the album is open.</summary>
    internal void ConfigureCollections(Func<string, string> title)
    {
        _collectionTitle = title;
        foreach (var tab in _tabs) tab.CollectionTitle = title;
        RefreshTitles();
    }
    internal void RefreshTitles() { foreach (var tab in _tabs) tab.Notify(); }
    private void Configure(FilesView view)
    {
        view.CanOpenInHost = _canOpen;
        view.Browser.OpenInEditor = _open;
    }
    private FilesTab AddTab(FilesTabState? state)
    {
        if (state?.Kind == "collection")
            return AddCollection(string.IsNullOrWhiteSpace(state.Source) ? "all" : state.Source);
        if (state != null)
        {
            state.History ??= [];
            state.Selection ??= [];
            state.Columns ??= [];
        }
        var tab = new FilesTab(false, "") { Pending = state };
        var view = tab.View!;
        Configure(view);
        view.Visibility = Visibility.Collapsed;
        if (state != null) ApplyOptions(view, state);
        view.ResultsChanged += () => { if (tab == ActiveTab) ResultsChanged?.Invoke(); QueueSave(); };
        view.Navigation.PropertyChanged += (_, _) => { tab.Notify(); QueueSave(); };
        view.ViewState.PropertyChanged += (_, _) => QueueSave();
        view.ResultsList.SelectionChanged += (_, _) => QueueSave();
        view.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueSave()));
        _tabs.Add(tab);
        Pages.Children.Add(view);
        return tab;
    }
    private FilesTab NewCollectionTabCore(string source) => new(true, source) { CollectionTitle = _collectionTitle };
    private FilesTab AddCollection(string source)
    {
        var tab = NewCollectionTabCore(source);
        _tabs.Add(tab);
        return tab;
    }
    private void InsertTab(int index, FilesTab tab) => _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
    private static void ApplyOptions(FilesView view, FilesTabState state)
    {
        view.SetView(Math.Clamp(state.Mode, 0, 2));
        view.SetDensity(state.Density);
        view.ViewState.TileSize = state.TileSize;
        view.ViewState.RowIconSize = state.RowIconSize;
        view.ViewState.SizeVisible = state.SizeVisible;
        view.ViewState.ModifiedVisible = state.ModifiedVisible;
        view.ViewState.TypeVisible = state.TypeVisible;
        view.ViewState.CreatedVisible = state.CreatedVisible;
        for (int i = 0; i < Math.Min(6, state.Columns.Length); i++)
            if (double.IsFinite(state.Columns[i]) && state.Columns[i] > 0) view.ViewState.SetColumnWidth(i + 1, state.Columns[i]);
        view.Navigation.ShowHidden = state.Hidden;
        view.SetFoldersFirst(state.FoldersFirst);
        view.SetSort(Math.Clamp(state.Sort, 1, 6), state.Ascending);
        view.SetFilter(state.Filter ?? "");
    }
    /// <summary>Lists the active folder tab's folder the first time it is shown. A collection
    /// tab has nothing to list here; the library reads it.</summary>
    public Task<bool> EnsureInitialized(string fallback)
    {
        var tab = ActiveTab;
        if (tab.View == null) return Task.FromResult(true);
        if (tab.Loading is { IsCompleted: false }) return tab.Loading;
        if (tab.View.Navigation.CurrentFolder.Length > 0) return Task.FromResult(true);
        return tab.Loading = InitializeTab(tab, fallback);
    }
    private async Task<bool> InitializeTab(FilesTab tab, string fallback)
    {
        var view = tab.View!;
        var state = tab.Pending;
        bool success = await view.NavigateTo(string.IsNullOrWhiteSpace(state?.Folder) ? fallback : state.Folder);
        if (_disposed || !_tabs.Contains(tab)) return false;
        if (!success && state != null) success = await view.NavigateTo(fallback);
        if (_disposed || !_tabs.Contains(tab)) return false;
        if (success && state != null && string.Equals(state.Folder, view.Navigation.CurrentFolder, StringComparison.OrdinalIgnoreCase))
        {
            var navigation = view.Navigation;
            if (state.History.Length > 0 && state.HistoryIndex >= 0 && state.HistoryIndex < state.History.Length
                && string.Equals(state.History[state.HistoryIndex], state.Folder, StringComparison.OrdinalIgnoreCase))
            {
                navigation.History.Clear();
                navigation.History.AddRange(state.History);
                navigation.HistoryIndex = state.HistoryIndex;
                navigation.Notify(nameof(BrowseState.CanGoBack));
                navigation.Notify(nameof(BrowseState.CanGoForward));
            }
            foreach (var entry in navigation.Results.Where(entry => state.Selection.Contains(entry.FilePath, StringComparer.OrdinalIgnoreCase)))
                view.ResultsList.SelectedItems.Add(entry);
            await Dispatcher.InvokeAsync(() =>
            {
                view.UpdateLayout();
                if (FindScroll(view.ResultsList) is { } scroll)
                {
                    if (double.IsFinite(state.Horizontal)) scroll.ScrollToHorizontalOffset(state.Horizontal);
                    if (double.IsFinite(state.Vertical)) scroll.ScrollToVerticalOffset(state.Vertical);
                }
            }, DispatcherPriority.Loaded);
        }
        tab.Pending = null;
        tab.Notify();
        QueueSave();
        return success;
    }
    /// <summary>Opens a folder tab at the end of the strip and selects it.</summary>
    public async Task<FilesTab> NewTab(string? folder = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string start = folder ?? ActiveView?.Navigation.CurrentFolder ?? "";
        var tab = AddTab(null);
        // The tab starts loading its own folder before it is selected: selecting it tells the
        // library, which would otherwise list the empty tab at the output folder first.
        var loading = tab.Loading = InitializeTab(tab, string.IsNullOrEmpty(start) ? Services.CaptureSettings.OutputFolder : start);
        SelectCore(tab);
        await loading;
        SaveSession();
        return tab;
    }
    /// <summary>Opens a collection tab at the end of the strip and selects it.</summary>
    public Task<FilesTab> NewCollectionTab(string source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tab = AddCollection(source);
        SelectCore(tab);
        SaveSession();
        return Task.FromResult(tab);
    }
    /// <summary>The most recently active tab of one kind, so F9 and the capture views go back
    /// to the tab they left instead of opening another.</summary>
    public FilesTab? LastTab(bool collection) =>
        _tabs.Where(tab => tab.IsCollection == collection).OrderByDescending(tab => tab.Activated).FirstOrDefault();
    /// <summary>Points the active collection tab at another collection.</summary>
    internal void RetargetActive(string source)
    {
        if (!ActiveTab.IsCollection || ActiveTab.Source == source) return;
        ActiveTab.Source = source;
        ActiveTab.Search = "";
        ActiveTab.ScrollOffset = 0;
        QueueSave();
    }
    /// <summary>Points every collection tab whose collection is gone at every capture: another
    /// album opened, or the collection was deleted.</summary>
    internal void ResetMissingSources(Func<string, bool> exists)
    {
        bool changed = false;
        foreach (var tab in _tabs.Where(tab => tab.IsCollection && !exists(tab.Source)))
        {
            tab.Source = "all";
            tab.Search = "";
            tab.ScrollOffset = 0;
            changed = true;
        }
        if (changed) QueueSave();
    }
    public async Task SelectTab(FilesTab tab)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_tabs.Contains(tab)) return;
        SelectCore(tab);
        await EnsureInitialized(Services.CaptureSettings.OutputFolder);
        QueueSave();
    }
    private void SelectCore(FilesTab tab)
    {
        var previous = _tabs.Contains(ActiveTab) ? ActiveTab : null;
        foreach (var item in _tabs)
        {
            item.IsActive = item == tab;
            if (item.View != null) item.View.Visibility = item.IsActive ? Visibility.Visible : Visibility.Collapsed;
            item.Notify();
        }
        CollectionHost.Visibility = tab.IsCollection ? Visibility.Visible : Visibility.Collapsed;
        tab.Activated = ++_activation;
        ActiveTab = tab;
        UpdateTabLayout();
        if (previous != tab) ActiveChanged?.Invoke(previous);
        ResultsChanged?.Invoke();
    }
    public async Task CloseTab(FilesTab tab)
    {
        if (!_tabs.Contains(tab) || _disposed) return;
        int index = _tabs.IndexOf(tab);
        bool active = tab == ActiveTab;
        _tabs.Remove(tab);
        if (tab.View != null)
        {
            Pages.Children.Remove(tab.View);
            tab.View.Dispose();
        }
        // The library is never left without a tab: the last one closed leaves every capture.
        if (_tabs.Count == 0) AddCollection("all");
        if (active) await SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else UpdateTabLayout();
        SaveSession();
    }
    public void MoveTab(FilesTab tab, int index)
    {
        if (!_tabs.Contains(tab) || index < 0 || index >= _tabs.Count) return;
        _tabs.Move(_tabs.IndexOf(tab), index);
        UpdateTabLayout();
        SaveSession();
    }
    private void QueueSave()
    {
        if (_disposed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }
    private void TabBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabLayout();
    private void UpdateTabLayout()
    {
        bool show = _tabs.Count > 1;
        TabBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        double available = Math.Max(0, (TabBar.ActualWidth > 0 ? TabBar.ActualWidth : ActualWidth) - 26);
        bool overflow = available / 160 < _tabs.Count;
        int count = overflow ? Math.Max(1, (int)((available - 26) / 160)) : _tabs.Count;
        count = Math.Min(count, _tabs.Count);
        TabOverflowButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        double stripAvailable = Math.Max(0, available - (overflow ? 26 : 0));
        TabStripHost.Width = Math.Max(0, Math.Min(stripAvailable, count * 260));
        _tabWindow = Math.Clamp(_tabWindow, 0, Math.Max(0, _tabs.Count - count));
        int active = _tabs.IndexOf(ActiveTab);
        if (active < _tabWindow) _tabWindow = active;
        else if (active >= _tabWindow + count) _tabWindow = active - count + 1;
        bool fills = !overflow && TabStripHost.Width >= available - 0.5;
        var radius = (CornerRadius)FindResource("PanelCornerRadius");
        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            tab.IsStripVisible = i >= _tabWindow && i < _tabWindow + count;
            tab.IsFirst = i == _tabWindow;
            tab.IsLast = fills && i == _tabWindow + count - 1;
            if (tab.View != null)
                tab.View.PaneFrame.CornerRadius = new CornerRadius(show && tab.IsActive && tab.IsFirst ? 0 : radius.TopLeft,
                    show && tab.IsActive && tab.IsLast ? 0 : radius.TopRight, radius.BottomRight, radius.BottomLeft);
            tab.Notify();
        }
        // The collection page wears the same frame as a Files page, joined to its tab.
        CollectionFrame.CornerRadius = new CornerRadius(show && ActiveTab is { IsFirst: true } ? 0 : radius.TopLeft,
            show && ActiveTab is { IsLast: true } ? 0 : radius.TopRight, radius.BottomRight, radius.BottomLeft);
        // A lone collection tab shows the library as it always looked, on the window's own
        // surface. Beside other tabs it sits in the Files page's frame, so the active tab joins it.
        if (show)
        {
            CollectionFrame.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
            CollectionFrame.SetResourceReference(Border.BorderThicknessProperty, "PaneEdgeSideThickness");
            CollectionFrame.SetResourceReference(EffectProperty, "PaneShadowEffect");
        }
        else
        {
            CollectionFrame.Background = Brushes.Transparent;
            CollectionFrame.BorderThickness = new Thickness(0);
            CollectionFrame.Effect = null;
        }
        Pages.Margin = show ? new Thickness(0, -1, 0, 0) : new Thickness(0);
        CollectionHost.Margin = Pages.Margin;
    }
    private void TabOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabOverflowButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var tab in _tabs.Where(tab => !tab.IsStripVisible))
        {
            var item = new MenuItem { Header = tab.Title, ToolTip = tab.View?.Navigation.CurrentFolder };
            item.Click += async (_, _) => await SelectTab(tab);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private static FilesTabState Capture(FilesTab tab)
    {
        if (tab.IsCollection) return new FilesTabState { Kind = "collection", Source = tab.Source };
        if (tab.Pending != null) return tab.Pending;
        var view = tab.View!;
        var state = view.Navigation;
        var scroll = FindScroll(view.ResultsList);
        return new FilesTabState
        {
            Folder = state.CurrentFolder, History = state.History.ToArray(), HistoryIndex = state.HistoryIndex,
            Filter = state.FilterText, Selection = view.SelectedPaths.ToArray(), Mode = view.CurrentMode,
            Horizontal = scroll?.HorizontalOffset ?? 0, Vertical = scroll?.VerticalOffset ?? 0,
            Sort = state.SortIndex, Ascending = state.SortAsc, Hidden = state.ShowHidden, FoldersFirst = state.FoldersOnTop,
            Density = view.ViewState.Density, TileSize = view.ViewState.TileSize, RowIconSize = view.ViewState.RowIconSize,
            Columns = Enumerable.Range(1, 6).Select(view.ViewState.GetColumnWidth).ToArray(),
            SizeVisible = view.ViewState.SizeVisible, ModifiedVisible = view.ViewState.ModifiedVisible,
            TypeVisible = view.ViewState.TypeVisible, CreatedVisible = view.ViewState.CreatedVisible
        };
    }
    public void SaveSession()
    {
        if (_disposed) return;
        string json = JsonSerializer.Serialize(new Session { Active = _tabs.IndexOf(ActiveTab), Tabs = _tabs.Select(Capture).ToArray() });
        if (Settings.Get("Files.Tabs") != json) Settings.Set("Files.Tabs", json);
    }
    /// <summary>Ctrl+T opens a tab like the active one, Ctrl+W closes it and Ctrl+Tab and
    /// Ctrl+Shift+Tab step through the strip, over a folder or a collection.</summary>
    public bool HandleTabKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control && key == Key.T) { _ = DuplicateActive(); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.W) { _ = CloseTab(ActiveTab); return true; }
        if (key == Key.Tab && modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))
        {
            int step = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            _ = SelectTab(_tabs[(_tabs.IndexOf(ActiveTab) + step + _tabs.Count) % _tabs.Count]);
            return true;
        }
        return false;
    }
    private Task<FilesTab> DuplicateActive() => ActiveTab.IsCollection ? NewCollectionTab(ActiveTab.Source) : NewTab();
    private async void NewTab_Click(object sender, RoutedEventArgs e) => await DuplicateActive();
    private async void CloseTab_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is FilesTab tab) await CloseTab(tab); e.Handled = true; }
    private async void SelectTab_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is FilesTab tab) await SelectTab(tab); }
    private async void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FilesTab tab) return;
        if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; await CloseTab(tab); }
        else if (e.ChangedButton == MouseButton.Left) await SelectTab(tab);
    }
    private async void Tab_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && ((FrameworkElement)sender).DataContext is FilesTab tab)
        { e.Handled = true; await SelectTab(tab); }
    }
    private void Tab_DragDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);
    private void Tab_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ((FrameworkElement)sender).DataContext is not FilesTab tab) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(FilesTab), tab), DragDropEffects.Move);
    }
    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(FilesTab)) is FilesTab tab && ((FrameworkElement)sender).DataContext is FilesTab target)
        { MoveTab(tab, _tabs.IndexOf(target)); e.Handled = true; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        SaveSession();
        _disposed = true;
        _saveTimer.Stop();
        Services.ThemeManager.ThemeChanged -= UpdateTabLayout;
        foreach (var tab in _tabs) tab.View?.Dispose();
    }
}
