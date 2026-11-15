using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerShot.Files;

public partial class FilesView
{
    public int CurrentMode { get; private set; }
    public BrowseState Navigation => Browser.State;
    private bool _syncingOptions;
    private static int ReadInt(string key, int fallback) => int.TryParse(Settings.Get("Files." + key), out int value) ? value : fallback;
    private void RestoreOptions()
    {
        ViewState.Density = ReadInt("Density", 1);
        ViewState.TileSize = ReadInt("TileSize", 96);
        ViewState.RowIconSize = ReadInt("RowIconSize", 16);
        for (int column = 1; column <= 6; column++)
            if (double.TryParse(Settings.Get("Files.Column." + column), CultureInfo.InvariantCulture, out double width) && double.IsFinite(width))
                ViewState.SetColumnWidth(column, width);
        ViewState.SizeVisible = Settings.Get("Files.SizeVisible") != "0";
        ViewState.ModifiedVisible = Settings.Get("Files.ModifiedVisible") != "0";
        ViewState.TypeVisible = Settings.Get("Files.TypeVisible") != "0";
        ViewState.CreatedVisible = Settings.Get("Files.CreatedVisible") == "1";
        Navigation.SortIndex = Math.Clamp(ReadInt("Sort", 4), 1, 6);
        Navigation.SortAsc = Settings.Get("Files.SortAsc") == "1";
        Navigation.ShowHidden = Settings.Get("Files.ShowHidden") == "1";
        Navigation.FoldersOnTop = Settings.Get("Files.FoldersOnTop") != "0";
        SetView(Math.Clamp(ReadInt("Mode", 0), 0, 2));
        UpdateOptionButtons();
        ViewState.PropertyChanged += (_, e) =>
        {
            string? name = e.PropertyName;
            if (name is "Density" or "TileSize" or "RowIconSize" or "SizeVisible" or "ModifiedVisible" or "TypeVisible" or "CreatedVisible")
            {
                string value = name switch
                {
                    "Density" => ViewState.Density.ToString(CultureInfo.InvariantCulture),
                    "TileSize" => ViewState.TileSize.ToString(CultureInfo.InvariantCulture),
                    "RowIconSize" => ViewState.RowIconSize.ToString(CultureInfo.InvariantCulture),
                    "SizeVisible" => ViewState.SizeVisible ? "1" : "0",
                    "ModifiedVisible" => ViewState.ModifiedVisible ? "1" : "0",
                    "TypeVisible" => ViewState.TypeVisible ? "1" : "0",
                    _ => ViewState.CreatedVisible ? "1" : "0"
                };
                Settings.Set("Files." + name, value);
                UpdateOptionButtons();
            }
        };
        ViewState.ColumnWidthSet += column =>
        {
            if (column is >= 1 and <= 6)
                Settings.Set("Files.Column." + column, ViewState.GetColumnWidth(column).ToString(CultureInfo.InvariantCulture));
        };
    }
    public void SetDensity(int density) => ViewState.Density = density;
    public void SetSort(int column, bool ascending)
    {
        if (column is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(column));
        Navigation.SortIndex = column;
        Navigation.SortAsc = ascending;
        Settings.Set("Files.Sort", column.ToString(CultureInfo.InvariantCulture));
        Settings.Set("Files.SortAsc", ascending ? "1" : "0");
        Sorting.ApplySort(Navigation);
        UpdateOptionButtons();
    }
    public async Task SetShowHidden(bool visible)
    {
        Navigation.ShowHidden = visible;
        Settings.Set("Files.ShowHidden", visible ? "1" : "0");
        UpdateOptionButtons();
        if (Navigation.CurrentFolder.Length > 0) await Browser.NavigateTo(Navigation.CurrentFolder);
    }
    public void SetFoldersFirst(bool foldersFirst)
    {
        Navigation.FoldersOnTop = foldersFirst;
        Settings.Set("Files.FoldersOnTop", foldersFirst ? "1" : "0");
        Sorting.ApplySort(Navigation);
        UpdateOptionButtons();
    }
    private void View_Click(object sender, RoutedEventArgs e)
        => SetView(ReferenceEquals(sender, ViewIconsBtn) ? 1 : ReferenceEquals(sender, ViewDetailsBtn) ? 2 : 0);
    private void Density_Click(object sender, RoutedEventArgs e) => SetDensity((ViewState.Density + 1) % 5);
    private void Density_Wheel(object sender, MouseWheelEventArgs e) { SetDensity(ViewState.Density + (e.Delta > 0 ? -1 : 1)); e.Handled = true; }
    private async void Hidden_Click(object sender, RoutedEventArgs e) => await SetShowHidden(!Navigation.ShowHidden);
    private void Folders_Click(object sender, RoutedEventArgs e) => SetFoldersFirst(!Navigation.FoldersOnTop);
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (Navigation.CurrentFolder.Length > 0) await NavigateTo(Navigation.CurrentFolder); }
    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingOptions && SortBox.SelectedItem is ComboBoxItem item && int.TryParse(Convert.ToString(item.Tag), out int column)) SetSort(column, Navigation.SortAsc);
    }
    private void SortDirection_Click(object sender, RoutedEventArgs e) => SetSort(Navigation.SortIndex, !Navigation.SortAsc);
    private void UpdateOptionButtons()
    {
        _syncingOptions = true;
        try
        {
            SortBox.SelectedItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Convert.ToString(item.Tag) == Navigation.SortIndex.ToString(CultureInfo.InvariantCulture));
            ShowHiddenBtn.Tag = Navigation.ShowHidden ? "on" : null;
            ShowHiddenBtn.Content = Navigation.ShowHidden ? "\uE890" : "\uE7B3";
            FoldersTopBtn.Tag = Navigation.FoldersOnTop ? "on" : null;
            SortDirectionBtn.Tag = Navigation.SortAsc ? "on" : null;
            SortDirectionBtn.Content = Navigation.SortAsc ? "\uE74A" : "\uE74B";
            SortDirectionBtn.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, Navigation.SortAsc ? "Str_A11y_SortAsc" : "Str_A11y_SortDesc");
            FileDensityBtn.Tag = ViewState.Density >= 2 ? "on" : null;
            string[] densityKeys = ["Str_St_DensityRoomy", "Str_St_DensityFull", "Str_St_DensityCompact", "Str_St_DensityTight", "Str_St_DensityMin"];
            FileDensityBtn.ToolTip = TryFindResource(densityKeys[ViewState.Density]);
            string[] arrowNames = ["ColNameArrow", "ColFolderArrow", "ColSizeArrow", "ColModifiedArrow", "ColTypeArrow", "ColCreatedArrow"];
            for (int i = 0; i < arrowNames.Length; i++)
                if (FindName(arrowNames[i]) is TextBlock arrow) arrow.Text = Navigation.SortIndex == i + 1 ? Navigation.SortAsc ? "\uE70E" : "\uE70D" : "";
        }
        finally { _syncingOptions = false; }
    }
}
