using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Files;

// Port of KillerShell's details pane (Shell/DetailsPane.cs). The pane docks below or to the right
// of the listing, and the toolbar button cycles bottom, right, off. It repaints once per selection
// change, never on a timer, and the slow parts (created date, image decode) run off the UI thread
// behind a generation counter so a newer selection always wins.
public partial class FilesView
{
    private const int DetailsPreviewPx = 220;
    private const double DetailsPreviewWidthRatio = 1.4;
    private const double DetailsPreviewMinWidth = 100;
    private const double DetailsFieldsMinWidth = 210;
    private const double DetailsPaneDividerWidth = 16;
    private const string DetailsBottomIcon = "M0,0 H14 V14 H0 Z M0,10 H14";
    private const string DetailsRightIcon = "M0,0 H14 V14 H0 Z M10,0 V14";
    private const string DetailsOffIcon = "M0,0 H14 V14 H0 Z M2,2 L12,12";

    private static readonly HashSet<string> DetailsImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff",
    };

    // 0 off, 1 bottom, 2 right.
    public int DetailsPosition { get; private set; }
    private double _detailsHeight = 160;
    private double _detailsWidth = 280;
    private int _detailsGen;

    private void InitDetailsPane()
    {
        DetailsPosition = int.TryParse(Settings.Get("Files.DetailsPane"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int saved)
            && saved is >= 0 and <= 2 ? saved : 0;
        if (TryParseSize(Settings.Get("Files.DetailsPaneHeight"), out double height)) _detailsHeight = height;
        if (TryParseSize(Settings.Get("Files.DetailsPaneWidth"), out double width)) _detailsWidth = width;
        ResultsList.SelectionChanged += (_, _) => UpdateDetailsPaneForSelection();
        DetailsFieldsGrid.SizeChanged += (_, _) => CorrectDetailsPaneSize();
        ResultsList.SizeChanged += (_, _) => CorrectDetailsPaneSize();
        ApplyDetailsPane();
    }

    private static bool TryParseSize(string? raw, out double value)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

    private void DetailsPane_Click(object sender, RoutedEventArgs e) => CycleDetailsPane();

    internal void CycleDetailsPane() => SetDetailsPanePosition(DetailsPosition switch { 0 => 1, 1 => 2, _ => 0 });

    public void SetDetailsPanePosition(int position)
    {
        if (position is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(position));
        DetailsPosition = position;
        Settings.Set("Files.DetailsPane", position.ToString(CultureInfo.InvariantCulture));
        ApplyDetailsPane();
    }

    private void ApplyDetailsPane()
    {
        bool open = DetailsPosition != 0;
        DetailsPaneBtn.Tag = open ? "on" : null;
        DetailsPaneGlyph.Data = Geometry.Parse(DetailsPosition switch { 1 => DetailsBottomIcon, 2 => DetailsRightIcon, _ => DetailsOffIcon });
        ConfigureDetailsPaneDock();
        DetailsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) UpdateDetailsPaneForSelection();
    }

    private double DetailsWidthCeiling => ResultsList.ActualWidth > 0 ? (ResultsList.ActualWidth + DetailsPane.ActualWidth) * 0.5 : 400;
    private double ClampDetailsWidth(double width) => Math.Max(Math.Min(210, DetailsWidthCeiling), Math.Min(DetailsWidthCeiling, width));

    private void ConfigureDetailsPaneDock()
    {
        bool right = DetailsPosition == 2;
        var panel = DetailsPane;
        DockPanel.SetDock(panel, right ? Dock.Right : Dock.Bottom);
        if (panel.Parent is DockPanel dock)
        {
            // On the right the pane goes ahead of the column header, so the header and rows share
            // one width. At the bottom it sits right above the status bar.
            dock.Children.Remove(panel);
            int position = right ? dock.Children.IndexOf(DetailsHeader) : dock.Children.IndexOf(StatusBar) + 1;
            dock.Children.Insert(position, panel);
        }
        panel.Width = right ? ClampDetailsWidth(_detailsWidth) : double.NaN;
        panel.Height = right ? double.NaN : _detailsHeight;
        if (right) panel.BorderThickness = new Thickness(1, 0, 0, 0);
        else panel.SetResourceReference(Border.BorderThicknessProperty, "DetailsPaneBorderThickness");
        DetailsScroll.VerticalScrollBarVisibility = right ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        DetailsPreviewHost.MaxHeight = right ? 320 : double.PositiveInfinity;
        DetailsPreviewHost.VerticalAlignment = right ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        var content = DetailsPaneContent;
        content.RowDefinitions[0].Height = right ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        content.RowDefinitions[1].Height = new GridLength(right ? 16 : 0);
        content.RowDefinitions[2].Height = right ? GridLength.Auto : new GridLength(0);
        content.ColumnDefinitions[1].Width = new GridLength(right ? 0 : 16);
        DetailsPreviewCol.Width = new GridLength(right ? 0 : 150);
        Grid.SetColumn(DetailsPreviewHost, right ? 0 : 2);
        Grid.SetRow(DetailsPreviewHost, right ? 2 : 0);
        Grid.SetColumn(DetailsDivider, right ? 0 : 1);
        Grid.SetRow(DetailsDivider, right ? 1 : 0);
        DetailsDivider.Height = right ? 1 : double.NaN;
        DetailsDivider.HorizontalAlignment = right ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        if (right) DetailsDivider.Width = double.NaN;
        else DetailsDivider.SetResourceReference(WidthProperty, "DetailsDividerWidth");
        DetailsDivider.Margin = right ? new Thickness(0, 8, 0, 8) : new Thickness(0, 2, 0, 2);
        DetailsFieldsGrid.VerticalAlignment = right ? VerticalAlignment.Top : VerticalAlignment.Center;
        var grip = DetailsPaneGrip;
        grip.Tag = right ? "right" : null;
        grip.Height = right ? double.NaN : 5;
        grip.Width = right ? 5 : double.NaN;
        grip.VerticalAlignment = right ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        grip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        grip.Cursor = right ? System.Windows.Input.Cursors.SizeWE : System.Windows.Input.Cursors.SizeNS;
    }

    // The strip never shrinks below the rows it is showing and never takes more than half the
    // listing area.
    private double DetailsContentFloor()
    {
        FrameworkElement content = DetailsFieldsGrid.Visibility == Visibility.Visible ? DetailsFieldsGrid : DetailsEmptyText;
        double width = content.ActualWidth > 0 ? content.ActualWidth : DetailsPane.ActualWidth > 0 ? DetailsPane.ActualWidth : double.PositiveInfinity;
        content.Measure(new Size(width, double.PositiveInfinity));
        return Math.Ceiling(content.DesiredSize.Height) + 20 + 1;
    }

    private double ClampDetailsHeight(double height)
    {
        double floor = DetailsContentFloor();
        double area = ResultsList.ActualHeight + (DetailsPane.IsVisible ? DetailsPane.ActualHeight : 0);
        double ceiling = Math.Max(floor, area > 0 ? area * 0.5 : 400);
        return Math.Max(Math.Min(floor, ceiling), Math.Min(ceiling, height));
    }

    private void ApplyDetailsPreviewWidth(double stripHeight)
    {
        if (DetailsPosition != 1) return;
        double preferred = Math.Max(DetailsPreviewMinWidth, stripHeight * DetailsPreviewWidthRatio);
        double total = DetailsPaneContent.ActualWidth;
        double max = total > 0 ? Math.Max(DetailsPreviewMinWidth, total - DetailsFieldsMinWidth - DetailsPaneDividerWidth) : preferred;
        DetailsPreviewCol.Width = new GridLength(Math.Min(max, preferred));
    }

    private void CorrectDetailsPaneSize()
    {
        if (DetailsPosition == 0 || !DetailsPane.IsVisible) return;
        FitDetailsFilename();
        if (DetailsPosition == 2)
        {
            double width = ClampDetailsWidth(_detailsWidth);
            if (Math.Abs(DetailsPane.Width - width) > 0.5) DetailsPane.Width = width;
            DetailsPreviewHost.MaxHeight = Math.Max(120, Math.Min(320, width - 28));
            return;
        }
        double height = ClampDetailsHeight(_detailsHeight);
        if (Math.Abs(DetailsPane.Height - height) > 0.5) DetailsPane.Height = height;
        ApplyDetailsPreviewWidth(height);
    }

    private void DetailsPaneGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DetailsPosition == 2)
        {
            _detailsWidth = ClampDetailsWidth(DetailsPane.ActualWidth - e.HorizontalChange);
            DetailsPane.Width = _detailsWidth;
            return;
        }
        if (DetailsPosition != 1) return;
        _detailsHeight = ClampDetailsHeight(DetailsPane.ActualHeight - e.VerticalChange);
        DetailsPane.Height = _detailsHeight;
        ApplyDetailsPreviewWidth(_detailsHeight);
    }

    private void DetailsPaneGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DetailsPosition == 2) Settings.Set("Files.DetailsPaneWidth", _detailsWidth.ToString("0.##", CultureInfo.InvariantCulture));
        else if (DetailsPosition == 1) Settings.Set("Files.DetailsPaneHeight", _detailsHeight.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private void UpdateDetailsPaneForSelection()
    {
        if (DetailsPosition == 0) return;
        int gen = ++_detailsGen;
        int count = ResultsList.SelectedItems.Count;
        DetailsMetadataRows.Children.Clear();
        DetailsPreviewImage.Source = null;
        DetailsPreviewImage.Visibility = Visibility.Collapsed;
        DetailsPreviewIcon.Source = null;
        DetailsPreviewIcon.Visibility = Visibility.Collapsed;
        if (count == 1 && ResultsList.SelectedItem is FileEntry one && !one.IsDirectory) ShowDetailsSingle(one, gen);
        else if (count > 1) ShowDetailsMulti([.. ResultsList.SelectedItems.OfType<FileEntry>()]);
        else ShowDetailsEmpty();
        CorrectDetailsPaneSize();
    }

    private void FitDetailsFilename()
    {
        var name = DetailsNameText;
        double width = DetailsFieldsGrid.ActualWidth;
        if (width <= 2 || string.IsNullOrEmpty(name.Text)) return;
        var typeface = new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch);
        var measured = new FormattedText(name.Text, CultureInfo.CurrentUICulture, name.FlowDirection, typeface,
            20, name.Foreground, VisualTreeHelper.GetDpi(name).PixelsPerDip);
        double size = Math.Max(12, Math.Min(20, 20 * (width - 2) / Math.Max(1, measured.WidthIncludingTrailingWhitespace)));
        if (Math.Abs(name.FontSize - size) > 0.01) name.FontSize = size;
    }

    private void ShowDetailsEmpty()
    {
        DetailsFieldsGrid.Visibility = Visibility.Collapsed;
        DetailsEmptyText.Visibility = Visibility.Visible;
    }

    private void ShowDetailsSingle(FileEntry entry, int gen)
    {
        DetailsEmptyText.Visibility = Visibility.Collapsed;
        DetailsFieldsGrid.Visibility = Visibility.Visible;
        DetailsNameText.Text = entry.FileName;
        FitDetailsFilename();
        DetailsSizeText.Text = entry.SizeLabel.Length > 0 ? entry.SizeLabel : "0 B";
        DetailsModifiedText.Text = entry.ModifiedLabel.Length > 0 ? entry.ModifiedLabel : "-";
        DetailsPathText.Text = entry.FilePath;
        DetailsTypeText.Text = DescribeExtension(entry.FileName);
        DetailsCreatedText.Text = "...";
        DetailsPreviewIcon.Source = IconCache.For(entry.FilePath, 128, false);
        DetailsPreviewIcon.Visibility = Visibility.Visible;
        string path = entry.FilePath;
        bool image = DetailsImageExtensions.Contains(Path.GetExtension(path));
        _ = Task.Run(() =>
        {
            DateTime created = default;
            try { created = File.GetCreationTime(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            (int Width, int Height) pixels = default;
            BitmapSource? preview = null;
            if (image)
            {
                pixels = ReadPixelSize(path);
                preview = ImageOrientation.Load(path, DetailsPreviewPx, ignoreColorProfile: false);
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _detailsGen) return;
                DetailsCreatedText.Text = created == default || created.Year < 1700 ? "-" : created.ToString("g", CultureInfo.CurrentCulture);
                if (pixels.Width > 0 && pixels.Height > 0) AddDetailsRow("Str_Details_Dimensions", pixels.Width + " × " + pixels.Height + " px");
                if (preview != null)
                {
                    DetailsPreviewImage.Source = preview;
                    DetailsPreviewImage.Visibility = Visibility.Visible;
                    DetailsPreviewIcon.Visibility = Visibility.Collapsed;
                }
                CorrectDetailsPaneSize();
            });
        });
    }

    private static (int Width, int Height) ReadPixelSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return default;
        }
    }

    private void AddDetailsRow(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0) };
        label.SetResourceReference(TextBlock.TextProperty, key);
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        var text = new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap, ToolTip = value };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        Grid.SetColumn(text, 1);
        row.Children.Add(label);
        row.Children.Add(text);
        DetailsMetadataRows.Children.Add(row);
    }

    private void ShowDetailsMulti(List<FileEntry> items)
    {
        DetailsEmptyText.Visibility = Visibility.Collapsed;
        DetailsFieldsGrid.Visibility = Visibility.Visible;
        DetailsNameText.Text = string.Format(CultureInfo.CurrentCulture, DetailsString("Str_Status_Selected"), items.Count.ToString("N0", CultureInfo.CurrentCulture));
        FitDetailsFilename();
        long total = 0;
        bool anyFolder = false;
        foreach (var item in items)
        {
            if (item.IsDirectory) { anyFolder = true; continue; }
            total += item.SizeBytes;
        }
        // Folder contents are not walked, so a selection with folders shows "at least" this much.
        DetailsSizeText.Text = anyFolder ? DetailsFormatBytes(total) + " +" : DetailsFormatBytes(total);
        DetailsModifiedText.Text = "-";
        DetailsCreatedText.Text = "-";
        DetailsTypeText.Text = "-";
        DetailsPathText.Text = items.Count > 0 ? items[0].Folder : string.Empty;
    }

    private string DetailsString(string key) => TryFindResource(key) as string ?? key;

    private string DescribeExtension(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return DetailsString("Str_Details_NoExt");
        return string.Format(CultureInfo.CurrentCulture, DetailsString("Str_Details_ExtFile"), ext.TrimStart('.').ToUpperInvariant());
    }

    private static string DetailsFormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0", CultureInfo.CurrentCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.0", CultureInfo.CurrentCulture) + " MB";
        return (mb / 1024.0).ToString("0.00", CultureInfo.CurrentCulture) + " GB";
    }
}
