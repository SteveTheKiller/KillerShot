using System.IO;
using System.Windows.Threading;
using KillerShot.Files;

namespace KillerShot.Shell;

internal static class SelfTestFilesCore
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-files-core") return false;
        string folder = Path.GetFullPath(args[1]);
        App.Instance.Dispatcher.BeginInvoke(async () =>
        {
            var report = new List<string>();
            int code = 1;
            try
            {
                if (Directory.Exists(folder)) throw new IOException("Use a new Files core fixture folder.");
                Directory.CreateDirectory(folder);
                Services.VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
                string listing = Path.Combine(folder, "listing");
                Directory.CreateDirectory(listing);
                for (int i = 0; i < 5000; i++) File.WriteAllText(Path.Combine(listing, $"file-{i:D4}.txt"), "entry");
                using var browser = new Browser();
                if (!await browser.NavigateTo(listing) || browser.State.Results.Count != 5000)
                    throw new InvalidOperationException("Files browser did not list all 5000 entries.", browser.State.Error);
                report.Add("5000-entry worker listing PASS");
                SortAndFilter();
                report.Add("all seven sort indices in both directions, type-name tiebreaker and path/name filter PASS");
                string child = Directory.CreateDirectory(Path.Combine(listing, "child")).FullName;
                if (!await browser.NavigateTo(child) || !await browser.GoUp() || browser.State.CurrentFolder != listing
                    || !await browser.GoBack() || browser.State.CurrentFolder != child
                    || !await browser.GoForward() || browser.State.CurrentFolder != listing
                    || Browser.ParentOf(@"C:\") != Browser.ThisPc || Browser.ParentOf(Browser.ThisPc) != null)
                    throw new InvalidOperationException("Navigation history or drive-root parent behavior diverged from the file browser.");
                report.Add("up/back/forward history and This PC drive-root parent PASS");
                var original = browser.State.Results.Single(entry => entry.FileName == "file-0000.txt");
                string? renameError = await Task.Run(() => FileOps.Rename(original.FilePath, "renamed.txt"));
                if (renameError != null) throw new IOException(renameError);
                await WaitUntil(() => original.FileName == "renamed.txt" && browser.State.Results.Contains(original));
                report.Add("watcher applies real rename within2s and preserves row identity PASS");
                string incoming = Path.Combine(listing, "continuous.txt");
                var writes = Task.Run(async () =>
                {
                    for (int i = 0; i < 65; i++)
                    {
                        File.WriteAllText(incoming, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        await Task.Delay(50);
                    }
                });
                var arrived = System.Diagnostics.Stopwatch.StartNew();
                while (arrived.Elapsed < TimeSpan.FromSeconds(2)
                    && !browser.State.Results.Any(entry => entry.FilePath == incoming)) await Task.Delay(25);
                bool refreshedDuringWrites = browser.State.Results.Any(entry => entry.FilePath == incoming);
                await writes;
                if (!refreshedDuringWrites)
                    throw new InvalidOperationException("Continuous file changes starved browser refresh beyond two seconds.");
                report.Add("continuous filesystem changes refresh the browser within2s PASS");
                string thumbnail = Path.Combine(folder, "thumbnail.png");
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 3, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, Enumerable.Repeat((byte)127, 24).ToArray(), 8);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var output = File.Create(thumbnail)) encoder.Save(output);
                var icon = await IconCache.ForAsync(thumbnail, 32);
                if (icon is not System.Windows.Media.Imaging.BitmapSource preview || !preview.IsFrozen
                    || preview.PixelHeight > 32 || preview.PixelWidth > 32)
                    throw new InvalidOperationException("File icon pipeline did not return a bounded frozen image.");
                report.Add("background thumbnail decode returns bounded frozen image PASS");
                string? opened = null;
                browser.OpenInEditor = path => { opened = path; return Task.FromResult(true); };
                if (!await browser.ActivateEntry(original) || opened != original.FilePath)
                    throw new InvalidOperationException("File activation did not reach the editor host boundary.");
                report.Add("file activation hands the selected path to the editor host PASS");
                string created = await Task.Run(() => FileOps.NewFolder(listing, "New folder"))
                    ?? throw new IOException("The new folder operation failed.");
                await WaitUntil(() => browser.State.Results.Any(entry => entry.FilePath == created && entry.IsDirectory));
                string numbered = await Task.Run(() => FileOps.NewFolder(listing, "New folder"))
                    ?? throw new IOException("The numbered new folder operation failed.");
                if (Path.GetFileName(numbered) != "New folder (2)") throw new InvalidOperationException("New folder did not preserve collision numbering.");
                var recycled = await Task.Run(() => FileOps.Recycle([created], silent: true));
                if (recycled.Succeeded != 1 || recycled.Canceled || recycled.Failed.Count != 0 || Directory.Exists(created))
                    throw new InvalidOperationException("The Recycle Bin operation did not remove the fixture folder.");
                await WaitUntil(() => browser.State.Results.All(entry => entry.FilePath != created));
                report.Add("real new-folder numbering and Recycle Bin operation reflected by watcher within2s PASS");
                await NavigationCancellation(browser, listing, child);
                report.Add("cancellation, failed path, expired deadline and competing navigation preserve current state PASS");
                code = 0;
            }
            catch (Exception error) { report.Add(error.ToString()); }
            finally
            {
                File.WriteAllLines(Path.Combine(folder, "files-core-report.txt"), report);
                App.Instance.Shutdown(code);
            }
        }, DispatcherPriority.Normal);
        return true;
    }

    private static void SortAndFilter()
    {
        var state = new BrowseState { FoldersOnTop = false };
        state.Results.Add(new FileEntry { FileName = "charlie.txt", FilePath = "C:\\Z\\charlie.txt", Folder = "Z", SizeBytes = 30,
            Modified = new DateTime(2026, 3, 1), Created = new DateTime(2025, 3, 1), Seq = 0 });
        state.Results.Add(new FileEntry { FileName = "bravo.txt", FilePath = "C:\\Y\\bravo.txt", Folder = "Y", SizeBytes = 20,
            Modified = new DateTime(2026, 2, 1), Created = new DateTime(2025, 2, 1), Seq = 1 });
        state.Results.Add(new FileEntry { FileName = "alpha.txt", FilePath = "C:\\X\\alpha.txt", Folder = "X", SizeBytes = 10,
            Modified = new DateTime(2026, 1, 1), Created = new DateTime(2025, 1, 1), Seq = 2 });
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(state.Results);
        foreach (int index in Enumerable.Range(0, 7))
            foreach (bool ascending in new[] { true, false })
            {
                state.SortIndex = index;
                state.SortAsc = ascending;
                Sorting.ApplySort(state);
                string expected = index == 0 ? ascending ? "charlie.txt" : "alpha.txt"
                    : index == 5 || ascending ? "alpha.txt" : "charlie.txt";
                if (view.Cast<FileEntry>().First().FileName != expected)
                    throw new InvalidOperationException($"Sort {index}, ascending={ascending} did not preserve the file-browser order.");
            }
        state.FilterText = "ALPHA";
        Sorting.ApplyFilter(state);
        if (view.Cast<FileEntry>().Single().FileName != "alpha.txt" || state.Results.Count != 3)
            throw new InvalidOperationException("Name filter changed or failed to narrow the underlying listing.");
        state.FilterText = "Y";
        state.FilterLocations = true;
        Sorting.ApplyFilter(state);
        if (view.Cast<FileEntry>().Single().FileName != "bravo.txt")
            throw new InvalidOperationException("Location filter did not match the containing folder.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Live browser did not reflect the file-system change within2s.");
    }

    private static async Task NavigationCancellation(Browser browser, string listing, string child)
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool canceledObserved = false;
        try { await browser.NavigateTo(child, canceled.Token); }
        catch (OperationCanceledException) { canceledObserved = true; }
        if (!canceledObserved || browser.State.CurrentFolder != listing || browser.State.IsLoading)
            throw new InvalidOperationException("Canceled navigation changed the current listing or lost caller cancellation.");
        if (await browser.NavigateTo(Path.Combine(listing, "not-present")) || browser.State.Error is not DirectoryNotFoundException
            || browser.State.CurrentFolder != listing)
            throw new InvalidOperationException("Failed navigation did not preserve its error and current listing.");
        browser.NavigationTimeout = TimeSpan.FromTicks(1);
        if (await browser.NavigateTo(listing) || browser.State.Error is not TimeoutException || browser.State.CurrentFolder != listing)
            throw new InvalidOperationException("Listing deadline did not bound the background operation.");
        browser.NavigationTimeout = TimeSpan.FromSeconds(5);
        var stale = browser.NavigateTo(listing);
        var latest = browser.NavigateTo(child);
        await Task.WhenAll(stale, latest);
        if (await stale || !await latest || browser.State.CurrentFolder != child)
            throw new InvalidOperationException("A canceled older listing overwrote the latest navigation.");
    }
}
