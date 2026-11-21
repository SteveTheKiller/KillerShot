using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Collections live on one collection page, never on the tab strip: the strip holds
/// folder tabs only, the sidebar and picker point the collection page at a collection, and the
/// strip shows only while a folder tab is active and there is more than one.</summary>
internal static class SelfTestCollectionTabs
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--selftest-collection-tabs") return false;
        _ = Run(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : "");
        return true;
    }

    private const string FirstName = "Tabs collection";
    private const string SecondName = "Second tabs collection";

    private static async Task Run(string folder, string mode)
    {
        Directory.CreateDirectory(folder);
        int code = 1;
        LibraryView? library = null;
        Window? window = null;
        var report = new StringBuilder();
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
            report.AppendLine("PASS: " + message);
        }
        string reportName = mode.Length == 0 ? "collection-tabs-report.txt" : $"collection-tabs-{mode}-report.txt";
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            CaptureSettings.OutputFolder = Directory.CreateDirectory(Path.Combine(folder, "output")).FullName;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            if (mode == "legacy")
            {
                // A 1.2.0 preview session: folder tabs only, written before tabs had kinds, with
                // the library last shown in the grid.
                Settings.Set("LibraryView", "grid");
                Settings.Set("Files.Tabs", "{\"Active\":0,\"Tabs\":[{\"Folder\":" + System.Text.Json.JsonSerializer.Serialize(CaptureSettings.OutputFolder) + "}]}");
            }
            else if (mode == "oldtabs")
            {
                // A session written while collections had tabs of their own: two collection tabs
                // around a folder tab, the second collection active.
                var ids = File.ReadAllText(Path.Combine(folder, "ids.txt")).Split(' ');
                Settings.Set("LibraryView", "grid");
                Settings.Set("Files.Tabs", "{\"Active\":2,\"Tabs\":[{\"Kind\":\"collection\",\"Source\":\"c" + ids[0] + "\"},{\"Folder\":"
                    + System.Text.Json.JsonSerializer.Serialize(CaptureSettings.OutputFolder) + "},{\"Kind\":\"collection\",\"Source\":\"c" + ids[1] + "\"}]}");
            }
            long first, second;
            if (mode.Length == 0)
            {
                var image = BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null,
                    Enumerable.Repeat((byte)200, 160 * 100 * 4).ToArray(), 160 * 4);
                image.Freeze();
                long a = CaptureLibrary.AddNow(image, DateTime.Today, "region", "Tab sample one", null);
                long b = CaptureLibrary.AddNow(image, DateTime.Today, "region", "Tab sample two", null);
                first = CaptureLibrary.CreateCollection(FirstName, [a]);
                second = CaptureLibrary.CreateCollection(SecondName, [b]);
                File.WriteAllText(Path.Combine(folder, "ids.txt"), first + " " + second);
            }
            else
            {
                var ids = File.ReadAllText(Path.Combine(folder, "ids.txt")).Split(' ').Select(long.Parse).ToArray();
                (first, second) = (ids[0], ids[1]);
            }
            string firstSource = "c" + first.ToString(CultureInfo.InvariantCulture);
            string secondSource = "c" + second.ToString(CultureInfo.InvariantCulture);

            library = new LibraryView();
            window = new Window { Content = library, Width = 1000, Height = 700, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await Settle(library);
            var workspace = library.FilesWorkspace;
            var tabBar = (FrameworkElement)workspace.FindName("TabBar");

            if (mode == "restore")
            {
                Check(workspace.Tabs.Count == 2 && workspace.Tabs.All(tab => tab.IsFolder), "restart restores the two folder tabs");
                Check(workspace.ActiveTab == workspace.LibraryTab && workspace.LibraryTab.Source == secondSource,
                    "restart restores the collection page and its collection");
                Check(library.CurrentSource == secondSource && !library.FilesActive, "restart shows the collection in the library");
                Check(workspace.LibraryTab.Title == SecondName, "the restored collection takes its name from the open album");
                Check(tabBar.Visibility != Visibility.Visible, "the collection page shows no tab strip");
                await workspace.SelectTab(workspace.Tabs[0]);
                await library.FilesNavigation;
                Check(library.FilesActive && workspace.ActiveView!.Navigation.CurrentFolder == CaptureSettings.OutputFolder,
                    "a restored folder tab lists its folder");
                Check(tabBar.Visibility == Visibility.Visible, "two restored folder tabs show the tab strip");
            }
            else if (mode == "legacy")
            {
                Check(workspace.Tabs.Count == 1 && workspace.Tabs[0].IsFolder, "the old session's folder tab is kept");
                Check(workspace.ActiveTab == workspace.LibraryTab && workspace.LibraryTab.Source == "all",
                    "the library opens where it was left, on the collection page");
                Check(library.CurrentView == "grid" && !library.FilesActive, "the old capture view still shows");
            }
            else if (mode == "oldtabs")
            {
                Check(workspace.Tabs.Count == 1 && workspace.Tabs[0].IsFolder, "old collection tabs leave only the folder tab on the strip");
                Check(workspace.ActiveTab == workspace.LibraryTab && workspace.LibraryTab.Source == firstSource,
                    "the first old collection tab becomes the collection page");
                Check(!library.FilesActive, "the library opens on the collection page");
            }
            else
            {
                Check(workspace.Tabs.Count == 0 && workspace.ActiveTab == workspace.LibraryTab && workspace.LibraryTab.Source == "all",
                    "a new library opens the collection page on every capture, with no tabs");
                Check(!library.FilesActive && library.CurrentView == "shelves", "the collection page shows the library's own view");
                Check(tabBar.Visibility != Visibility.Visible, "the collection page shows no tab strip");
                Check(workspace.LibraryTab.Title == LocaleManager.Loc("Str_Strip_All"), "the collection page is named for every capture");

                library.SetView("files");
                await library.FilesNavigation;
                Check(workspace.Tabs.Count == 1 && workspace.ActiveTab.IsFolder && library.FilesActive, "Files opens one folder tab");
                Check(tabBar.Visibility != Visibility.Visible, "one folder tab shows no tab strip");
                Check(library.CurrentView == "files", "the footer reads Files while a folder tab is active");
                Check(CollectionPage(library).Visibility != Visibility.Visible && workspace.ActiveView!.Visibility == Visibility.Visible,
                    "a folder tab shows its Files page, not the library");

                Check(workspace.HandleTabKey(Key.T, ModifierKeys.Control), "Ctrl+T works over a folder tab");
                await library.FilesNavigation;
                await Settle(library);
                Check(workspace.Tabs.Count == 2 && workspace.Tabs.All(tab => tab.IsFolder), "Ctrl+T opens a second folder tab");
                Check(tabBar.Visibility == Visibility.Visible, "two folder tabs show the tab strip");
                var folderTab = workspace.ActiveTab;

                library.SetView("grid");
                await Settle(library);
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab == workspace.LibraryTab,
                    "a capture view goes to the collection page without opening a tab");
                Check(library.CurrentView == "grid" && !library.FilesActive, "the collection page takes the chosen capture view");
                Check(CollectionPage(library).Visibility == Visibility.Visible, "the collection page shows the library");
                Check(tabBar.Visibility != Visibility.Visible, "the collection page hides the folder tab strip");
                Check(!workspace.HandleTabKey(Key.T, ModifierKeys.Control) && workspace.Tabs.Count == 2,
                    "Ctrl+T does nothing on the collection page");

                library.SetSource(firstSource);
                await Settle(library);
                Check(workspace.Tabs.Count == 2 && workspace.LibraryTab.Source == firstSource,
                    "picking a collection points the collection page at it");
                Check(workspace.LibraryTab.Title == FirstName, "the collection page is named for its collection");

                await workspace.SelectTab(folderTab);
                await library.FilesNavigation;
                library.SetSource(secondSource);
                await Settle(library);
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab == workspace.LibraryTab && workspace.LibraryTab.Source == secondSource,
                    "picking a collection over a folder tab brings the collection page back instead of opening a tab");
                Check(library.CurrentSource == secondSource && !library.FilesActive, "the library shows the picked collection");

                workspace.SaveSession();
                string session = Settings.Get("Files.Tabs") ?? "";
                Check(session.Contains(secondSource), "the session stores the collection page's id");
                Check(!session.Contains(FirstName) && !session.Contains(SecondName),
                    "the plain settings file never holds a name from inside the encrypted album");

                await workspace.SelectTab(folderTab);
                await library.FilesNavigation;
                foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
                {
                    ThemeManager.Apply(theme);
                    await Idle();
                    window.UpdateLayout();
                    var strip = (ItemsControl)workspace.FindName("TabStrip");
                    Check(Descendants<Button>(strip).Count(button => button.IsVisible) == 2 && Descendants<TextBlock>(strip).Count(label => label.Name == "tabLbl" && label.IsVisible) == 2, $"{theme} renders two folder tab labels and close buttons");
                    var bitmap = new RenderTargetBitmap((int)library.ActualWidth, (int)library.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(library);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(folder, $"collection-tabs-{theme}.png"));
                    encoder.Save(output);
                }
                ThemeManager.Apply(Theme.Dark);

                // A deleted collection sends the collection page back to every capture.
                long gone = CaptureLibrary.CreateCollection("Doomed collection");
                library.SetSource("c" + gone.ToString(CultureInfo.InvariantCulture));
                await Settle(library);
                CaptureLibrary.DeleteCollection(gone);
                library.Reload();
                await Settle(library);
                Check(workspace.LibraryTab.Source == "all" && library.CurrentSource == "all", "a deleted collection falls back to every capture");

                // Leave the session the restore run expects: two folder tabs and the collection
                // page on the second collection, active.
                library.SetSource(secondSource);
                await Settle(library);
                workspace.SaveSession();
            }
            File.WriteAllText(Path.Combine(folder, reportName), $"PASS: {checks} assertions.\n{report}");
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, reportName), $"FAIL after {checks}: {error}\n{report}"); }
        finally
        {
            library?.FilesWorkspace.Dispose();
            window?.Close();
            CaptureLibrary.Close();
        }
        App.Instance.Shutdown(code);
    }

    private static FrameworkElement CollectionPage(LibraryView library) => (FrameworkElement)library.FilesWorkspace.FindName("CollectionHost");

    private static async Task Settle(LibraryView library)
    {
        await Idle();
        await library.ListingTask;
        await Idle();
    }

    private static async Task Idle()
    {
        await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
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
}
