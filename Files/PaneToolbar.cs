using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerShot.Controls;

namespace KillerShot.Files;

// KillerShell's location row extras, ported from Shell/Bookmarks.cs (the star), Shell/Recents.cs
// and Shell/PaneToolbar.cs (the overflow). Partial of FilesView.
public partial class FilesView
{
    private const string RecentsSetting = "Files.Recents";
    private const int RecentsMax = 15;
    private readonly List<string> _recents = [];
    private string? _lastRecorded;
    private bool _reflowing;

    private void InitToolbar()
    {
        string saved = Settings.Get(RecentsSetting) ?? string.Empty;
        foreach (string path in saved.Split('|', StringSplitOptions.RemoveEmptyEntries))
            if (_recents.Count < RecentsMax) _recents.Add(path);
        MenuBars.SizeChanged += (_, _) => ReflowToolStrip();
        Loaded += (_, _) => ReflowToolStrip();
    }

    // Called from UpdateNavigation: the star and recents both follow the folder you are in.
    private void UpdateToolbarForFolder()
    {
        string here = Navigation.CurrentFolder;
        if (!string.Equals(here, _lastRecorded, StringComparison.OrdinalIgnoreCase))
        {
            _lastRecorded = here;
            RecordRecent(here);
        }
        UpdateFavoriteStar();
    }

    // ── The star ─────────────────────────────────────────────
    private IEnumerable<string> SavedPlacePaths() => Places.Where(place => place.Pinned).Select(place => place.Path);

    private bool IsFavorite(string path) => SavedPlacePaths().Contains(path, StringComparer.OrdinalIgnoreCase);

    private void FavoriteStar_Click(object sender, RoutedEventArgs e)
    {
        string here = Navigation.CurrentFolder;
        if (string.IsNullOrEmpty(here)) return;
        var paths = SavedPlacePaths().ToList();
        if (IsFavorite(here)) paths.RemoveAll(path => string.Equals(path, here, StringComparison.OrdinalIgnoreCase));
        else paths.Add(here);
        SetPlaces(paths);
        UpdateFavoriteStar();
    }

    private void UpdateFavoriteStar()
    {
        bool on = Navigation.CurrentFolder.Length > 0 && IsFavorite(Navigation.CurrentFolder);
        // E735 filled, E734 outline.
        FavoriteStarBtn.Content = ((char)(on ? 0xE735 : 0xE734)).ToString();
        FavoriteStarBtn.Tag = on ? "on" : null;
    }

    // ── Recents ──────────────────────────────────────────────
    // Most recent first; a revisit moves the folder up instead of adding it twice. This PC is a
    // sentinel rather than a path, so it is never recorded.
    private void RecordRecent(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || Browser.IsThisPc(folder)) return;
        _recents.RemoveAll(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
        _recents.Insert(0, folder);
        while (_recents.Count > RecentsMax) _recents.RemoveAt(_recents.Count - 1);
        SaveRecents();
    }

    private void SaveRecents() => Settings.Set(RecentsSetting, string.Join('|', _recents));

    // Folders that were deleted or unplugged are dropped rather than grayed. All are checked at
    // once on a one second budget, so a dead share cannot freeze the menu.
    private static List<string> LiveFolders(IReadOnlyList<string> paths)
    {
        var checks = paths.Select(path => Task.Run(() => Directory.Exists(path))).ToArray();
        try { Task.WaitAll(checks, TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        return paths.Where((_, i) => checks[i].IsCompletedSuccessfully && checks[i].Result).ToList();
    }

    private void Recents_Click(object sender, RoutedEventArgs e)
    {
        var menu = RecentsMenu;
        menu.Items.Clear();
        var live = LiveFolders(_recents);
        if (live.Count == 0)
        {
            var empty = new MenuItem { IsEnabled = false };
            empty.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Recents_Empty");
            menu.Items.Add(empty);
        }
        else
        {
            int rows = live.Count;
            foreach (string path in live)
            {
                var name = new TextBlock
                {
                    Text = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : path,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var head = new Grid { Margin = new Thickness(8, 0, 0, 0) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(name);
                var item = new MenuItem { Header = head, ToolTip = path };
                // The chips' X: removes just this row and keeps the menu open for the next one.
                var remove = new Button
                {
                    Content = "x", Width = 26, Height = 26, FontSize = 14,
                    Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0),
                    Style = (Style)FindResource("DangerButton"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                remove.Click += (_, re) =>
                {
                    re.Handled = true;
                    _recents.RemoveAll(q => string.Equals(q, path, StringComparison.OrdinalIgnoreCase));
                    SaveRecents();
                    menu.Items.Remove(item);
                    if (--rows == 0) menu.IsOpen = false;
                };
                Grid.SetColumn(remove, 1);
                head.Children.Add(remove);
                item.Icon = new Image
                {
                    Width = 20, Height = 20,
                    Margin = new Thickness(-2, 0, -2, 0),
                    Source = IconCache.For(path, 20, isDirectory: true),
                };
                item.Click += async (_, _) => { if (Directory.Exists(path)) await NavigateTo(path); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var clear = new MenuItem();
            clear.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Recents_Clear");
            clear.Click += (_, _) => { _recents.Clear(); SaveRecents(); };
            menu.Items.Add(clear);
        }
        menu.PlacementTarget = RecentsBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── Overflow ─────────────────────────────────────────────
    // Secondary tools yield space before the address does. The sort control goes late, and the
    // place picker last of all, because it is how you get around (KillerShell keeps its places
    // in a sidebar, so its list has no equivalent).
    private static readonly string[] ShedOrder =
    [
        "ExportBtn", "ReloadBtn", "FoldersTopBtn", "ShowHiddenBtn", "DetailsPaneBtn",
        "FileDensityBtn", "ViewDetailsBtn", "ViewIconsBtn", "ViewListBtn",
        "ViewSortSeparator", "SortDirectionBtn", "SortBox", "PlacesBox",
    ];

    private static readonly string[] StripOrder =
    [
        "ViewListBtn", "ViewIconsBtn", "ViewDetailsBtn", "FileDensityBtn", "ViewSortSeparator",
        "SortBox", "SortDirectionBtn", "ShowHiddenBtn", "FoldersTopBtn", "ReloadBtn",
        "DetailsPaneBtn", "PlacesBox", "ExportBtn", "OverflowBtn",
    ];

    // About 36 characters of the 12px monospace path, past the prefix and recents button.
    private const double MinFilenameWidth = 260;

    private static double MeasuredWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width + element.Margin.Left + element.Margin.Right;
    }

    private void ReflowToolStrip()
    {
        if (_reflowing || MenuBars.ActualWidth <= 0 || OverflowPopup.IsOpen || SortBox.IsDropDownOpen || PlacesBox.IsDropDownOpen) return;
        _reflowing = true;
        try
        {
            double nav = MeasuredWidth(NavStrip);
            double margins = ToolStrip.Margin.Left + ToolStrip.Margin.Right;
            double fullTools = ToolStrip.Children.OfType<FrameworkElement>().Where(el => el != OverflowBtn)
                .Concat(OverflowPanel.Children.OfType<FrameworkElement>()).Sum(MeasuredWidth);
            var address = (Border)((Grid)ScopePathLabel.Parent).Parent;
            double path = MinFilenameWidth + MeasuredWidth(ScopePrefixText) + MeasuredWidth(RecentsBtn)
                + address.Padding.Left + address.Padding.Right;
            double avail = MenuBars.ActualWidth - nav - margins - path;
            double remaining = fullTools;
            double chevron = OverflowBtn.Width + OverflowBtn.Margin.Left + OverflowBtn.Margin.Right;
            var shed = new HashSet<string>();
            foreach (string name in ShedOrder)
            {
                if (remaining + (shed.Count > 0 ? chevron : 0) <= avail) break;
                if (FindName(name) is not FrameworkElement el || el.Visibility != Visibility.Visible) continue;
                shed.Add(name);
                remaining -= MeasuredWidth(el);
            }
            // Move only what changed, so focus and open flyout anchors are left alone.
            foreach (string name in ShedOrder.Reverse())
            {
                if (FindName(name) is not FrameworkElement el) continue;
                bool inOverflow = OverflowPanel.Children.Contains(el);
                if (shed.Contains(name) == inOverflow) continue;
                OverflowPopup.IsOpen = false;
                if (inOverflow)
                {
                    OverflowPanel.Children.Remove(el);
                    InsertInStripOrder(ToolStrip, el, name);
                }
                else
                {
                    ToolStrip.Children.Remove(el);
                    InsertInStripOrder(OverflowPanel, el, name);
                }
            }
            OverflowBtn.Visibility = shed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _reflowing = false; }
    }

    // Both panels keep strip order, so a shed tool sits in the overflow where it sat in the row.
    private static void InsertInStripOrder(Panel panel, FrameworkElement el, string name)
    {
        int want = Array.IndexOf(StripOrder, name);
        int at = panel.Children.Count;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement child || child is System.Windows.Controls.Primitives.Popup) continue;
            int index = Array.IndexOf(StripOrder, child.Name);
            if (index > want) { at = i; break; }
        }
        panel.Children.Insert(at, el);
    }

    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = !OverflowPopup.IsOpen;
        if (OverflowPopup.IsOpen && OverflowPopup.Child is UIElement child) Anim.FadeIn(child);
    }
}
