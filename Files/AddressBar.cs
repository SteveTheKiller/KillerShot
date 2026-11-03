using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShot.Controls;

namespace KillerShot.Files;

public partial class FilesView
{
    public ObservableCollection<PickerPlace> Places { get; } = [];
    private bool _syncingPlace;
    private PickerPlace? _currentPlace;
    public Task<bool> GoBack() => Browser.GoBack();
    public Task<bool> GoForward() => Browser.GoForward();
    public Task<bool> GoUp() => Browser.GoUp();
    public void BeginEditAddress()
    {
        AddressBox.Text = Browser.IsThisPc(Navigation.CurrentFolder) ? "" : Navigation.CurrentFolder;
        AddressBox.Visibility = Visibility.Visible;
        ScopePathLabel.Visibility = Visibility.Collapsed;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }
    private void EndEditAddress()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        ScopePathLabel.Visibility = Visibility.Visible;
    }
    public async Task<bool> NavigateAddress(string address)
    {
        string path = Environment.ExpandEnvironmentVariables(address.Trim().Trim('"'));
        EndEditAddress();
        if (path.Length == 0) return false;
        if (File.Exists(path)) return await Browser.ActivateEntry(new FileEntry { FilePath = Path.GetFullPath(path), FileName = Path.GetFileName(path) });
        return await NavigateTo(path);
    }
    private void InitAddress()
    {
        PlacesBox.ItemsSource = Places;
        Navigation.PropertyChanged += (_, _) => UpdateNavigation();
        UpdateNavigation();
        Loaded += (_, _) => { if (Places.Count == 0) LoadPlaces(); };
    }
    private void LoadPlaces()
    {
        string? saved = Settings.Get("Files.Places");
        var defaults = new[]
        {
            (Environment.SpecialFolder.UserProfile, "Str_QA_Home"),
            (Environment.SpecialFolder.Desktop, "Str_QA_Desktop"),
            (Environment.SpecialFolder.MyDocuments, "Str_QA_Documents"),
            (Environment.SpecialFolder.MyPictures, "Str_QA_Pictures")
        };
        if (saved != null)
            foreach (string path in saved.Split('|', StringSplitOptions.RemoveEmptyEntries)) Places.Add(new PickerPlace(Path.GetFileName(path.TrimEnd('\\')), path, true));
        else
        {
            foreach (var (folder, key) in defaults)
            {
                string path = Environment.GetFolderPath(folder);
                if (path.Length > 0) Places.Add(new PickerPlace(Convert.ToString(TryFindResource(key)) ?? path, path, true));
            }
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Places.Add(new PickerPlace(Convert.ToString(TryFindResource("Str_QA_Downloads")) ?? downloads, downloads, true));
        }
        Places.Add(new PickerPlace(Convert.ToString(TryFindResource("Str_Nav_ThisPc")) ?? Browser.ThisPc, Browser.ThisPc));
        UpdateNavigation();
    }
    public void SetPlaces(IEnumerable<string> paths)
    {
        Settings.Set("Files.Places", string.Join('|', paths));
        Places.Clear();
        LoadPlaces();
    }
    private void UpdateNavigation()
    {
        ScopePathLabel.Text = Browser.IsThisPc(Navigation.CurrentFolder)
            ? Convert.ToString(TryFindResource("Str_Nav_ThisPc")) ?? Navigation.CurrentFolder : Navigation.CurrentFolder;
        NavBackBtn.IsEnabled = Navigation.CanGoBack;
        NavForwardBtn.IsEnabled = Navigation.CanGoForward;
        NavUpBtn.IsEnabled = Browser.ParentOf(Navigation.CurrentFolder) != null;
        ToolTip = Navigation.Error?.Message;
        _syncingPlace = true;
        try
        {
            string here = Navigation.CurrentFolder;
            var match = Places.FirstOrDefault(place => string.Equals(place.Path, here, StringComparison.OrdinalIgnoreCase));
            if (match == null && here.Length > 0)
            {
                if (_currentPlace != null) Places.Remove(_currentPlace);
                string label = Browser.IsThisPc(here) ? Convert.ToString(TryFindResource("Str_Nav_ThisPc")) ?? here : Path.GetFileName(here.TrimEnd('\\'));
                _currentPlace = new PickerPlace(label.Length > 0 ? label : here, here);
                Places.Add(_currentPlace);
                match = _currentPlace;
            }
            PlacesBox.SelectedItem = match ?? Places.FirstOrDefault();
        }
        finally { _syncingPlace = false; }
    }
    private async void Back_Click(object sender, RoutedEventArgs e) => await GoBack();
    private async void Forward_Click(object sender, RoutedEventArgs e) => await GoForward();
    private async void Up_Click(object sender, RoutedEventArgs e) => await GoUp();
    private void ScopeBar_Click(object sender, MouseButtonEventArgs e) { BeginEditAddress(); e.Handled = true; }
    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndEditAddress(); e.Handled = true; }
        if (e.Key == Key.Enter) { e.Handled = true; await NavigateAddress(AddressBox.Text); }
    }
    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEditAddress();
    private async void Place_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingPlace && PlacesBox.SelectedItem is PickerPlace place) await NavigateTo(place.Path);
    }
    private async void Files_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.OriginalSource is TextBox) return;
        var modifiers = e.KeyboardDevice.Modifiers;
        if (HandleFileKey(e.Key == Key.System ? e.SystemKey : e.Key, modifiers, e.OriginalSource as DependencyObject))
        { e.Handled = true; return; }
        if ((modifiers == ModifierKeys.Control && e.Key == Key.L) || (modifiers == ModifierKeys.Alt && e.SystemKey == Key.D)) { BeginEditAddress(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right or Key.Up)
        {
            e.Handled = true;
            if (e.SystemKey == Key.Left) await GoBack(); else if (e.SystemKey == Key.Right) await GoForward(); else await GoUp();
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is >= Key.D1 and <= Key.D4)
        {
            e.Handled = true;
            if (e.Key == Key.D4) SetDensity((ViewState.Density + 1) % 5); else SetView(e.Key - Key.D1);
        }
        else if (modifiers == ModifierKeys.Alt && e.SystemKey == Key.O) { SetFoldersFirst(!Navigation.FoldersOnTop); e.Handled = true; }
        else if (modifiers == ModifierKeys.Alt && e.SystemKey == Key.S) { SortBox.IsDropDownOpen = true; e.Handled = true; }
        else if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && e.SystemKey == Key.S) { SetSort(Navigation.SortIndex, !Navigation.SortAsc); e.Handled = true; }
        else if (e.Key == Key.Enter && ResultsList.SelectedItem is FileEntry entry) { e.Handled = true; await Browser.ActivateEntry(entry); }
    }
}
