using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Text;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesView
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--selftest-files-view") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : "basic");
        return true;
    }

    private static async Task RunAsync(string folder, string scenario)
    {
        Directory.CreateDirectory(folder);
        int code = 1, checks = 0;
        Window? window = null;
        FilesView? view = null;
        var details = new StringBuilder();
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            Settings.Set("Files.Places", "");
            App.InitializeKit();
            string files = Directory.CreateDirectory(Path.Combine(folder, "files")).FullName;
            File.WriteAllText(Path.Combine(files, "alpha.txt"), "a");
            Directory.CreateDirectory(Path.Combine(files, "child"));
            view = new FilesView();
            window = new Window { Content = view, Width = 900, Height = 600, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            if (!await view.NavigateTo(files)) throw new InvalidOperationException("Files view did not navigate to its owned folder.");
            checks++;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (view.ResultsList.Items.Count != 2) throw new InvalidOperationException("Files view did not display the real directory entries.");
            checks++;
            foreach (int mode in new[] { 0, 1, 2 })
            {
                view.SetView(mode);
                window.UpdateLayout();
                if (view.ResultsList.ItemTemplate == null) throw new InvalidOperationException($"Files display mode {mode} has no file-row template.");
                checks++;
            }
            if (view.FindName("AddressBox") is not System.Windows.Controls.TextBox || view.FindName("NavBackBtn") is not System.Windows.Controls.Button)
                throw new InvalidOperationException("Files view has no editable address and navigation controls.");
            checks++;
            // KillerShell's pane carries navigation, the address and the view tools in ONE row,
            // with the tool strip right-aligned after the address. Measured on the real controls.
            double Middle(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), view).Y;
            double LeftEdge(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), view).X;
            if (view.FindName("ToolStrip") is not StackPanel toolStrip || !toolStrip.IsVisible
                || Math.Abs(Middle(view.NavBackBtn) - Middle(view.ViewListBtn)) > 4
                || Math.Abs(Middle(view.NavBackBtn) - Middle(view.ScopePathLabel)) > 4
                || LeftEdge(view.ViewListBtn) <= LeftEdge(view.ScopePathLabel) + 40
                || LeftEdge(toolStrip) + toolStrip.ActualWidth < view.ActualWidth - 40)
                throw new InvalidOperationException($"Files toolbar is not KillerShell's single location row (back y {Middle(view.NavBackBtn):0}, view buttons y {Middle(view.ViewListBtn):0}).");
            checks++;
            double columns = view.ViewState.NameWidth.Value + view.ViewState.LocationWidth.Value
                + view.ViewState.SizeWidth.Value + view.ViewState.ModifiedWidth.Value
                + view.ViewState.TypeWidth.Value + view.ViewState.CreatedWidth.Value
                + view.ViewState.RowIconColumn.Value + view.ViewState.RowPad.Left + view.ViewState.RowPad.Right;
            if (columns > view.ResultsList.ActualWidth - 16) throw new InvalidOperationException($"Details columns overflow: {columns} > {view.ResultsList.ActualWidth - 16}.");
            checks++;
            view.ViewState.Density = 4;
            view.ViewState.SetColumnWidth(1, 360);
            using (var restored = new FilesView())
            {
                if (restored.ViewState.Density != 4 || restored.ViewState.GetColumnWidth(1) != 360)
                    throw new InvalidOperationException("Files view did not restore its density and column width.");
                checks++;
            }
            string child = Path.Combine(files, "child");
            if (!await view.NavigateAddress('"' + child + '"') || view.Navigation.CurrentFolder != child) throw new InvalidOperationException("Address navigation failed.");
            checks++;
            view.NavBackBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            if (view.Navigation.CurrentFolder != files || !view.Navigation.CanGoForward) throw new InvalidOperationException("Back toolbar did not navigate.");
            checks++;
            if (!await view.GoForward() || view.Navigation.CurrentFolder != child || !await view.GoUp() || view.Navigation.CurrentFolder != files) throw new InvalidOperationException("Forward/up navigation failed.");
            checks++;
            string? opened = null;
            view.Browser.OpenInEditor = path => { opened = path; return Task.FromResult(true); };
            if (!await view.NavigateAddress(Path.Combine(files, "alpha.txt")) || opened != Path.Combine(files, "alpha.txt")) throw new InvalidOperationException("Typed file did not use host activation boundary.");
            checks++;
            File.WriteAllText(Path.Combine(files, ".hidden.txt"), "hidden");
            await view.SetShowHidden(false);
            if (view.ResultsList.Items.Count != 2) throw new InvalidOperationException("Hidden entry was visible.");
            await view.SetShowHidden(true);
            if (view.ResultsList.Items.Count != 3) throw new InvalidOperationException("Hidden entry was not shown.");
            checks += 2;
            view.SetSort(1, true);
            view.SetFoldersFirst(true);
            if (view.SortBox.SelectedItem is not ComboBoxItem { Tag: "1" } || string.IsNullOrWhiteSpace(view.SortBox.Text)
                || view.PlacesBox.SelectedItem is not KillerShot.Controls.PickerPlace { Path: var currentPlace } || currentPlace != files)
                throw new InvalidOperationException("Sort and current place toolbar labels are blank or stale.");
            if (!Equals(view.ShowHiddenBtn.Tag, "on") || !Equals(view.FoldersTopBtn.Tag, "on") || !Equals(view.SortDirectionBtn.Tag, "on"))
                throw new InvalidOperationException("Toolbar toggle states do not reflect the current options.");
            checks += 2;
            if (view.ResultsList.Items[0] is not FileEntry { IsDirectory: true }) throw new InvalidOperationException("Folders-first sort did not order actual rows.");
            checks++;
            view.SetView(1);
            using (var restored = new FilesView())
            {
                if (restored.CurrentMode != 1 || !restored.Navigation.ShowHidden || !restored.Navigation.SortAsc || restored.Navigation.SortIndex != 1)
                    throw new InvalidOperationException("View/sort/hidden options did not restore.");
                checks++;
            }
            foreach (Theme theme in new[] { Theme.Dark, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    view.SetView(mode);
                    foreach (int density in new[] { 1, 4 })
                    {
                        view.SetDensity(density);
                        foreach (bool selected in new[] { false, true })
                        {
                            view.ResultsList.SelectedIndex = selected ? 0 : -1;
                            await Idle();
                            window.UpdateLayout();
                            int realized = Descendants<ListBoxItem>(view.ResultsList).Count();
                            if (realized == 0) throw new InvalidOperationException($"No actual realized row in {theme}/{mode}/{density}.");
                            var image = Render(view);
                            Save(image, Path.Combine(folder, $"{theme}-{mode}-{density}-{selected}.png"));
                            byte[] corner = new byte[4];
                            image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight - 30, 1, 1), corner, 4, 0);
                            if (corner[3] != 255) throw new InvalidOperationException("Files list surface is transparent instead of the family list surface.");
                            if (view.ResultsList.SelectedItems.Count != (selected ? 1 : 0)) throw new InvalidOperationException("Rendered selection state did not reach actual rows.");
                            details.AppendLine($"Render {theme} mode={mode} density={density} realized={realized} selected={view.ResultsList.SelectedItems.Count} size={image.PixelWidth}x{image.PixelHeight}");
                            checks++;
                        }
                    }
                }
            }
            view.ResultsList.SelectedIndex = -1;
            if (scenario.StartsWith("reference=", StringComparison.Ordinal))
            {
                var reference = (DataTemplate)System.Windows.Markup.XamlReader.Parse(File.ReadAllText(scenario[10..]));
                view.SetView(0);
                view.SetDensity(1);
                foreach (Theme theme in new[] { Theme.Dark, Theme.SE98 })
                {
                    ThemeManager.Apply(theme);
                    foreach (bool selected in new[] { false, true })
                    {
                        var rows = new ListBox
                        {
                            Tag = view.ViewState, ItemsSource = view.Navigation.Results, ItemTemplate = reference,
                            ItemsPanel = view.ResultsList.ItemsPanel, ItemContainerStyle = view.ResultsList.ItemContainerStyle,
                            Padding = view.ResultsList.Padding, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                            FontFamily = view.ResultsList.FontFamily, Foreground = view.ResultsList.Foreground,
                            SelectedIndex = selected ? 0 : -1
                        };
                        ScrollViewer.SetCanContentScroll(rows, true);
                        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Auto);
                        ScrollViewer.SetVerticalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
                        using var referenceHost = new OwnedWindow(new Window
                        {
                            Content = rows, Width = window.Width, Height = window.Height,
                            Left = -21000, Top = -21000, ShowActivated = false, ShowInTaskbar = false,
                            UseLayoutRounding = true
                        });
                        referenceHost.Window.Show();
                        view.ResultsList.SelectedIndex = selected ? 0 : -1;
                        await Idle();
                        window.UpdateLayout();
                        referenceHost.Window.UpdateLayout();
                        var actualItem = (ListBoxItem)view.ResultsList.ItemContainerGenerator.ContainerFromIndex(0);
                        var referenceItem = (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(0);
                        var actualImage = Render(actualItem);
                        var referenceImage = Render(referenceItem);
                        Save(actualImage, Path.Combine(folder, $"row-shot-{theme}-{selected}.png"));
                        Save(referenceImage, Path.Combine(folder, $"row-shell-source-reference-{theme}-{selected}.png"));
                        int stride = actualImage.PixelWidth * 4;
                        byte[] actualPixels = new byte[stride * actualImage.PixelHeight], expectedPixels = new byte[stride * actualImage.PixelHeight];
                        if (actualImage.PixelWidth != referenceImage.PixelWidth || actualImage.PixelHeight != referenceImage.PixelHeight) throw new InvalidOperationException("Reference-source row dimensions differ.");
                        actualImage.CopyPixels(actualPixels, stride, 0);
                        referenceImage.CopyPixels(expectedPixels, stride, 0);
                        int different = actualPixels.Zip(expectedPixels).Count(pair => pair.First != pair.Second);
                        details.AppendLine($"Source-extracted Shell reference {theme} density=1 selected={actualItem.IsSelected} size={actualImage.PixelWidth}x{actualImage.PixelHeight} differentChannels={different}");
                        if (different != 0) throw new InvalidOperationException("Rendered row differs from source-extracted Shell reference.");
                        checks++;
                    }
                }
            }
            view.SetView(2);
            foreach (double width in new[] { 520.0, 900.0 })
            {
                window.Width = width;
                await Idle();
                window.UpdateLayout();
                var item = Descendants<ListBoxItem>(view.ResultsList).First();
                var row = Descendants<Grid>(item).First(grid => grid.ColumnDefinitions.Count == 8);
                double sum = row.ColumnDefinitions.Sum(column => column.ActualWidth) + view.ViewState.RowPad.Left + view.ViewState.RowPad.Right;
                details.AppendLine($"Actual details columns width={sum:F1} listWidth={view.ResultsList.ActualWidth:F1} windowWidth={width:F0}");
                if (sum <= 0 || sum > view.ResultsList.ActualWidth - 16 + 1) throw new InvalidOperationException("Realized details columns overflow the actual list viewport.");
                checks++;
            }
            if (scenario == "sorting") checks += await CheckSortReference(folder, details);
            if (scenario == "watcher") checks += await CheckFilteredWatcher(folder, details);
            if (scenario.StartsWith("performance", StringComparison.Ordinal))
            {
                string large = scenario.StartsWith("performance=", StringComparison.Ordinal)
                    ? Path.GetFullPath(scenario[12..]) : Directory.CreateDirectory(Path.Combine(folder, "large")).FullName;
                var setup = Stopwatch.StartNew();
                bool reuse = scenario.Contains('=');
                if (!reuse) await Task.Run(() => { for (int i = 0; i < 100000; i++) using (File.Create(Path.Combine(large, $"entry-{i:D6}.txt"))) { } });
                int actualFiles = await Task.Run(() => Directory.EnumerateFiles(large, "entry-??????.txt").Count());
                if (actualFiles != 100000 || Directory.EnumerateDirectories(large).Any()) throw new InvalidOperationException("Owned large fixture is incomplete.");
                details.AppendLine($"Fixture count={actualFiles} reused={reuse} setupMs={setup.ElapsedMilliseconds}");
                bool navigated = false;
                var stages = Stopwatch.StartNew();
                ((System.ComponentModel.INotifyPropertyChanged)view.Navigation.Results).PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "Count") details.AppendLine($"Stage source Count {stages.Elapsed.TotalMilliseconds:F1}ms");
                };
                view.Navigation.Results.CollectionChanged += (_, e) => details.AppendLine($"Stage source {e.Action} {stages.Elapsed.TotalMilliseconds:F1}ms");
                ((System.Collections.Specialized.INotifyCollectionChanged)System.Windows.Data.CollectionViewSource.GetDefaultView(view.Navigation.Results)).CollectionChanged += (_, e)
                    => details.AppendLine($"Stage view {e.Action} {stages.Elapsed.TotalMilliseconds:F1}ms");
                var measure = await MeasureAsync(async () => { navigated = await view.NavigateTo(large); });
                details.AppendLine($"Real filesystem navigation count={view.ResultsList.Items.Count} elapsedMs={measure.Elapsed:F1} postedPings={measure.Pings} maxQueueLatencyMs={measure.Worst:F1}");
                if (!navigated || view.ResultsList.Items.Count != 100000) throw new InvalidOperationException("Large owned folder was not actually navigated.");
                if (view.ResultsList.Items[0] is not FileEntry { FileName: "entry-000000.txt" } || view.ResultsList.Items[99999] is not FileEntry { FileName: "entry-099999.txt" }) throw new InvalidOperationException("Large listing did not retain actual name sort.");
                if (measure.Pings < 2 || measure.Worst > 500) throw new InvalidOperationException("Large navigation exceeded 500ms dispatcher queue latency.");
                checks++;
                var collection = view.Navigation.Results;
                var second = await MeasureAsync(async () => { navigated = await view.NavigateTo(large); });
                details.AppendLine($"Second real filesystem navigation count={view.ResultsList.Items.Count} elapsedMs={second.Elapsed:F1} postedPings={second.Pings} maxQueueLatencyMs={second.Worst:F1}");
                if (!navigated || !ReferenceEquals(collection, view.Navigation.Results) || view.ResultsList.Items.Count != 100000
                    || second.Pings < 2 || second.Worst > 500 || view.ResultsList.Items[0] is not FileEntry { FileName: "entry-000000.txt" })
                    throw new InvalidOperationException("Second large navigation failed identity, sorted count or 500ms queue latency.");
                checks++;
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    view.SetView(mode);
                    await Idle();
                    window.UpdateLayout();
                    int before = Descendants<ListBoxItem>(view.ResultsList).Count();
                    view.ResultsList.ScrollIntoView(view.ResultsList.Items[99999]);
                    await Idle();
                    window.UpdateLayout();
                    int after = Descendants<ListBoxItem>(view.ResultsList).Count();
                    int lastIndex = Descendants<ListBoxItem>(view.ResultsList).Select(item => view.ResultsList.ItemContainerGenerator.IndexFromContainer(item)).DefaultIfEmpty(-1).Max();
                    details.AppendLine($"Virtualized mode={mode} before={before} after={after} maxIndex={lastIndex} selected={view.ResultsList.SelectedItems.Count}");
                    if (before is <= 0 or >= 250 || after is <= 0 or >= 250 || lastIndex != 99999) throw new InvalidOperationException("Large listing failed actual scrolling/virtualization budget.");
                    checks++;
                }
            }
            File.WriteAllText(Path.Combine(folder, "files-view-report.txt"), $"PASS: {checks} Files view assertions.\n{details}");
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "files-view-report.txt"), $"FAIL after {checks} assertions: {error}\n{details}"); }
        finally { window?.Close(); view?.Dispose(); }
        App.Instance.Shutdown(code);
    }
    private static async Task Idle()
    {
        await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private sealed class OwnedWindow(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        public void Dispose() => Window.Close();
    }
    private static async Task<(double Elapsed, int Pings, double Worst)> MeasureAsync(Func<Task> work)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var samples = new System.Collections.Concurrent.ConcurrentQueue<double>();
        using var stop = new CancellationTokenSource();
        var sender = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long posted = Stopwatch.GetTimestamp();
                dispatcher.BeginInvoke(DispatcherPriority.Input, () => samples.Enqueue(Stopwatch.GetElapsedTime(posted).TotalMilliseconds));
                Thread.Sleep(16);
            }
        }) { IsBackground = true };
        var clock = Stopwatch.StartNew();
        sender.Start();
        try { await work(); await Idle(); }
        finally { stop.Cancel(); sender.Join(); }
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        return (clock.Elapsed.TotalMilliseconds, samples.Count, samples.DefaultIfEmpty().Max());
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }
    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
    private static async Task<int> CheckSortReference(string folder, StringBuilder details)
    {
        string sample = Directory.CreateDirectory(Path.Combine(folder, "sort-samples")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(sample, "nested")).FullName;
        DateTime stamp = DateTime.Today.AddDays(-2);
        string[] names = ["a-alpha.txt", "A-Beta.txt", "éclair.txt", "eclair.txt", "I-test.bin", "ı-test.bin", "İ-test.txt", "i-test.bin", "-punct.txt", "_punct.txt", "z-last.bin"];
        foreach (string parent in new[] { sample, nested })
        {
            foreach (string name in names)
            {
                string path = Path.Combine(parent, name);
                File.WriteAllText(path, name.Length % 2 == 0 ? "aa" : "bbbb");
                File.SetCreationTime(path, stamp);
                File.SetLastWriteTime(path, stamp.AddDays(name.Length % 2));
            }
        }
        using var browser = new Browser();
        if (!await browser.NavigateTo(sample)) throw new InvalidOperationException("Sort reference filesystem listing failed.", browser.State.Error);
        var actualEntries = browser.State.Results.ToList();
        if (!await browser.NavigateTo(nested)) throw new InvalidOperationException("Sort reference nested listing failed.", browser.State.Error);
        actualEntries.AddRange(browser.State.Results);
        int cases = 0;
        foreach (string cultureName in new[] { "en-US", "tr-TR", "sv-SE", "null" })
        foreach (int index in Enumerable.Range(0, 7))
        foreach (bool ascending in new[] { false, true })
        foreach (bool foldersFirst in new[] { false, true })
        foreach (bool groups in new[] { false, true })
        foreach (string query in new[] { "", "é", ".txt" })
        {
            var state = new BrowseState { SortIndex = index, SortAsc = ascending, FoldersOnTop = foldersFirst, GroupDates = groups, FilterText = query };
            foreach (var entry in actualEntries) state.Results.Add(entry);
            var actual = (System.Windows.Data.ListCollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(state.Results);
            var expected = new System.Windows.Data.ListCollectionView(actualEntries.ToList());
            if (cultureName != "null") actual.Culture = expected.Culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            else if (actual.Culture != null || expected.Culture != null) throw new InvalidOperationException("Unbound WPF reference did not begin with null culture.");
            using (expected.DeferRefresh())
            {
                string? group = groups ? index switch { 4 => nameof(FileEntry.ModifiedGroup), 6 => nameof(FileEntry.CreatedGroup), _ => null } : null;
                if (group == null && foldersFirst) expected.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(FileEntry.IsDirectory), System.ComponentModel.ListSortDirection.Descending));
                string? property = index switch { 1 => nameof(FileEntry.FileName), 2 => nameof(FileEntry.Folder), 3 => nameof(FileEntry.SizeBytes), 4 => nameof(FileEntry.Modified), 5 => nameof(FileEntry.TypeName), 6 => nameof(FileEntry.Created), _ => null };
                if (property == null && !ascending) property = nameof(FileEntry.Seq);
                if (property != null) expected.SortDescriptions.Add(new System.ComponentModel.SortDescription(property, ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                if (index == 5) expected.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(FileEntry.FileName), System.ComponentModel.ListSortDirection.Ascending));
                if (group != null) expected.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(group));
                if (query.Length > 0) expected.Filter = value => value is FileEntry entry && (entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Folder.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            Sorting.ApplySort(state);
            Sorting.ApplyFilter(state);
            if (!actual.Cast<FileEntry>().SequenceEqual(expected.Cast<FileEntry>()) || GroupOrder(actual) != GroupOrder(expected))
                throw new InvalidOperationException($"WPF sort reference differs: {cultureName}/{index}/{ascending}/{foldersFirst}/{groups}/{query}.");
            state.FilterText = "";
            Sorting.ApplyFilter(state);
            if (actual.Count != actualEntries.Count) throw new InvalidOperationException("Clearing active filter did not restore actual entries.");
            cases++;
        }
        details.AppendLine($"WPF SortDescriptions reference {cases} actual-filesystem row identity/group-order cases,3 explicit cultures plus null,7indices,both directions/folder/group modes,3filters PASS");
        return cases;
    }
    private static string GroupOrder(System.Windows.Data.ListCollectionView view) => view.Groups == null ? "" : string.Join("|", view.Groups.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => group.Name + ":" + string.Join(",", group.Items.Cast<FileEntry>().Select(entry => entry.FilePath))));
    private static async Task<int> CheckFilteredWatcher(string folder, StringBuilder details)
    {
        string sample = Directory.CreateDirectory(Path.Combine(folder, "watcher-samples")).FullName;
        foreach (string name in new[] { "watch-current.txt", "watch-z.txt", "other.txt" }) File.WriteAllText(Path.Combine(sample, name), "entry");
        using var browser = new Browser();
        browser.State.SortIndex = 1;
        browser.State.SortAsc = true;
        browser.State.FoldersOnTop = false;
        browser.State.FilterText = "watch-";
        if (!await browser.NavigateTo(sample)) throw new InvalidOperationException("Filtered watcher listing failed.", browser.State.Error);
        var rows = browser.State.Results;
        var listing = System.Windows.Data.CollectionViewSource.GetDefaultView(rows);
        var current = rows.Single(entry => entry.FileName == "watch-current.txt");
        listing.MoveCurrentTo(current);
        string renamed = Path.Combine(sample, "watch-renamed.txt");
        File.Move(current.FilePath, renamed);
        await Until(() => current.FilePath == renamed && listing.Cast<FileEntry>().Contains(current));
        if (!ReferenceEquals(rows, browser.State.Results) || !ReferenceEquals(listing.CurrentItem, current)) throw new InvalidOperationException("Filtered rename lost the collection or current-row identity.");
        string added = Path.Combine(sample, "watch-a.txt");
        File.WriteAllText(added, "entry");
        await Until(() => listing.Cast<FileEntry>().FirstOrDefault()?.FilePath == added);
        if (!ReferenceEquals(listing.CurrentItem, current)) throw new InvalidOperationException("Sorted watcher add lost current-row identity.");
        File.Delete(added);
        await Until(() => rows.All(entry => entry.FilePath != added));
        if (!ReferenceEquals(listing.CurrentItem, current)) throw new InvalidOperationException("Watcher remove lost the unrelated current row.");
        string outside = Path.Combine(sample, "outside.txt");
        File.Move(renamed, outside);
        await Until(() => current.FilePath == outside && !listing.Cast<FileEntry>().Contains(current));
        if (!rows.Contains(current)) throw new InvalidOperationException("Rename out of filter removed the underlying row identity.");
        browser.State.FilterText = "";
        Sorting.ApplyFilter(browser.State);
        if (listing.Cast<FileEntry>().Count() != 3 || !listing.Cast<FileEntry>().Contains(current)) throw new InvalidOperationException("Clearing watcher filter failed to restore its renamed row.");
        browser.State.FilterText = "watch-";
        Sorting.ApplyFilter(browser.State);
        var other = rows.Single(entry => entry.FileName == "other.txt");
        File.Move(other.FilePath, Path.Combine(sample, "watch-visible.txt"));
        await Until(() => other.FileName == "watch-visible.txt" && listing.Cast<FileEntry>().Contains(other));
        details.AppendLine("Real sorted filtered watcher rename/add/remove/rename-out/clear/rename-in preserved collection and current-row identity PASS");
        return 7;
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Actual filesystem watcher did not reach the expected view state within2s.");
    }
}
