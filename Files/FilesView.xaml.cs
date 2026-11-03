using System.Windows.Controls;

namespace KillerShot.Files;

public partial class FilesView : UserControl, IDisposable
{
    public Browser Browser { get; } = new();
    public ResultsViewState ViewState { get; } = new();
    public event Action? ResultsChanged;
    public int VisibleCount => System.Windows.Data.CollectionViewSource.GetDefaultView(Navigation.Results).Cast<object>().Count();
    public void SetFilter(string text)
    {
        Navigation.FilterText = text;
        Sorting.ApplyFilter(Navigation);
        ResultsChanged?.Invoke();
    }
    public FilesView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = Browser.State.Results;
        ((System.Collections.Specialized.INotifyCollectionChanged)System.Windows.Data.CollectionViewSource.GetDefaultView(Navigation.Results))
            .CollectionChanged += (_, _) => ResultsChanged?.Invoke();
        ResultsList.Tag = ViewState;
        ViewState.LocationHidden = true;
        ResultsList.SizeChanged += (_, _) => FitColumns();
        ViewState.PropertyChanged += (_, _) => FitColumns();
        RestoreOptions();
        InitAddress();
        InitInteraction();
        InitFilesMenu();
        Loaded += (_, _) =>
        {
            Services.ThemeManager.ThemeChanged -= RefreshArt;
            Services.ThemeManager.ThemeChanged += RefreshArt;
            RefreshArt();
        };
        Unloaded += (_, _) => Services.ThemeManager.ThemeChanged -= RefreshArt;
        ResultsList.MouseDoubleClick += async (_, e) =>
        {
            if (e.OriginalSource is System.Windows.DependencyObject source)
            {
                var item = ItemsControl.ContainerFromElement(ResultsList, source) as ListBoxItem;
                if (item?.DataContext is FileEntry entry) { e.Handled = true; await Browser.ActivateEntry(entry); }
            }
        };
    }
    private void FitColumns() => ViewState.AvailableWidth = Math.Max(0, ResultsList.ActualWidth - 16
        - ViewState.RowIconColumn.Value - ViewState.RowPad.Left - ViewState.RowPad.Right
        - System.Windows.SystemParameters.VerticalScrollBarWidth);
    public Task<bool> NavigateTo(string folder) => Browser.NavigateTo(folder);
    public void SetView(int mode)
    {
        if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        CurrentMode = mode;
        DetailsHeader.Visibility = mode == 2 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ViewListBtn.Tag = mode == 0 ? "on" : null;
        ViewIconsBtn.Tag = mode == 1 ? "on" : null;
        ViewDetailsBtn.Tag = mode == 2 ? "on" : null;
        Settings.Set("Files.Mode", mode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ResultsList.ItemTemplate = (System.Windows.DataTemplate)FindResource(mode switch { 1 => "TileTemplate", 2 => "DetailsRowTemplate", _ => "CompactListRowTemplate" });
        ResultsList.ItemsPanel = (ItemsPanelTemplate)FindResource(mode switch { 1 => "PanelWrap", 2 => "PanelStack", _ => "PanelWrapVertical" });
        ScrollViewer.SetHorizontalScrollBarVisibility(ResultsList, mode == 0 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(ResultsList, mode == 0 ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }
    private void RefreshArt() => RefreshArt(ResultsList);
    private static void RefreshArt(System.Windows.DependencyObject root)
    {
        if (root is Image image && TileArt.GetPath(image) != null) TileArt.Refresh(image);
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshArt(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }
    public void Dispose()
    {
        CancelFileOperation();
        EndPointer();
        ReleaseDropImage();
        CancelRename();
        Services.ThemeManager.ThemeChanged -= RefreshArt;
        Browser.Dispose();
    }
}
