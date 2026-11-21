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

/// <summary>Collections open as tabs beside folder tabs in one strip: the library's own view is
/// the page of a collection tab, a folder tab keeps its Files page, and the sidebar retargets the
/// active tab when it can show the place and opens a tab when it cannot, as KillerShell does.</summary>
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

            if (mode == "restore")
            {
                Check(workspace.Tabs.Count == 3, "restart restores three tabs");
                Check(workspace.Tabs[0].IsCollection && workspace.Tabs[1].IsFolder && workspace.Tabs[2].IsCollection,
                    "restart restores each tab's kind in order");
                Check(workspace.ActiveTab == workspace.Tabs[2] && workspace.ActiveTab.Source == secondSource,
                    "restart restores the active collection tab and its collection");
                Check(library.CurrentSource == secondSource && !library.FilesActive, "restart shows the active collection in the library");
                Check(workspace.ActiveTab.Title == SecondName, "a restored collection tab takes its name from the open album");
                Check(workspace.Tabs[0].Source == firstSource && workspace.Tabs[0].Title == FirstName,
                    "an inactive restored collection tab keeps its collection");
                await workspace.SelectTab(workspace.Tabs[1]);
                await library.FilesNavigation;
                Check(library.FilesActive && workspace.ActiveView!.Navigation.CurrentFolder == CaptureSettings.OutputFolder,
                    "a restored folder tab lists its folder");
            }
            else if (mode == "legacy")
            {
                Check(workspace.Tabs.Count == 2, "an old folder-only session gains the library's collection tab");
                Check(workspace.Tabs[0].IsCollection && workspace.Tabs[0].Source == "all" && workspace.ActiveTab == workspace.Tabs[0],
                    "the library opens where it was left, on the collection tab ahead of the folder tabs");
                Check(workspace.Tabs[1].IsFolder, "the old session's folder tab is kept");
                Check(library.CurrentView == "grid" && !library.FilesActive, "the old capture view still shows");
            }
            else
            {
                Check(workspace.Tabs.Count == 1 && workspace.ActiveTab.IsCollection && workspace.ActiveTab.Source == "all",
                    "a new library opens one collection tab showing every capture");
                Check(!library.FilesActive && library.CurrentView == "shelves", "the collection tab shows the library's own view");
                Check(((FrameworkElement)workspace.FindName("TabBar")).Visibility != Visibility.Visible, "one tab shows no tab strip");
                Check(workspace.ActiveTab.Title == LocaleManager.Loc("Str_Strip_All"), "the all-captures tab is named for it");

                library.SetView("files");
                await library.FilesNavigation;
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab.IsFolder && library.FilesActive,
                    "Files opens a folder tab beside the collection tab");
                Check(((FrameworkElement)workspace.FindName("TabBar")).Visibility == Visibility.Visible, "two tabs show the tab strip");
                Check(library.CurrentView == "files", "the footer reads Files while a folder tab is active");
                Check(CollectionPage(library).Visibility != Visibility.Visible && workspace.ActiveView!.Visibility == Visibility.Visible,
                    "a folder tab shows its Files page, not the library");
                var folderTab = workspace.ActiveTab;

                library.SetView("grid");
                await Settle(library);
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab == workspace.Tabs[0],
                    "a capture view returns to the collection tab instead of opening another");
                Check(library.CurrentView == "grid" && !library.FilesActive, "the collection tab takes the chosen capture view");
                Check(CollectionPage(library).Visibility == Visibility.Visible, "a collection tab shows the library");

                library.SetSource(firstSource);
                await Settle(library);
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab.Source == firstSource,
                    "picking a collection retargets the active collection tab");
                Check(workspace.ActiveTab.Title == FirstName, "the retargeted tab is named for its collection");

                await workspace.SelectTab(folderTab);
                await library.FilesNavigation;
                library.SetSource(secondSource);
                await Settle(library);
                Check(workspace.Tabs.Count == 3 && workspace.ActiveTab.IsCollection && workspace.ActiveTab.Source == secondSource,
                    "picking a collection over a folder tab opens a collection tab");
                Check(workspace.Tabs.IndexOf(workspace.ActiveTab) == 2, "the new collection tab opens at the end");
                Check(library.CurrentSource == secondSource, "the library shows the new tab's collection");

                Check(workspace.HandleTabKey(Key.T, ModifierKeys.Control), "Ctrl+T works over a collection tab");
                await Settle(library);
                Check(workspace.Tabs.Count == 4 && workspace.ActiveTab.IsCollection && workspace.ActiveTab.Source == secondSource,
                    "Ctrl+T on a collection tab opens another tab of that collection");
                Check(workspace.HandleTabKey(Key.W, ModifierKeys.Control), "Ctrl+W works over a collection tab");
                await Settle(library);
                Check(workspace.Tabs.Count == 3, "Ctrl+W closes the collection tab");

                await workspace.SelectTab(workspace.Tabs[0]);
                await Settle(library);
                Check(library.CurrentSource == firstSource && !library.FilesActive,
                    $"switching tabs brings the library to that tab's collection (library {library.CurrentSource}, tabs {string.Join(",", workspace.Tabs.Select(t => (t.IsCollection ? t.Source : "folder") + (t.IsActive ? "*" : "")))})");
                await workspace.SelectTab(workspace.Tabs[2]);
                await Settle(library);
                Check(library.CurrentSource == secondSource, "each collection tab keeps its own collection");

                workspace.SaveSession();
                string session = Settings.Get("Files.Tabs") ?? "";
                Check(session.Contains(firstSource) && session.Contains(secondSource), "the session stores each collection tab's id");
                Check(!session.Contains(FirstName) && !session.Contains(SecondName),
                    "the plain settings file never holds a name from inside the encrypted album");

                foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
                {
                    ThemeManager.Apply(theme);
                    await Idle();
                    window.UpdateLayout();
                    var strip = (ItemsControl)workspace.FindName("TabStrip");
                    Check(Descendants<Button>(strip).Count(button => button.IsVisible) == 3 && Descendants<TextBlock>(strip).Count(label => label.Name == "tabLbl" && label.IsVisible) == 3, $"{theme} renders three tab labels and close buttons");
                    var bitmap = new RenderTargetBitmap((int)library.ActualWidth, (int)library.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(library);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(folder, $"collection-tabs-{theme}.png"));
                    encoder.Save(output);
                }
                ThemeManager.Apply(Theme.Dark);

                // Leave the session the restore run expects: the first collection, the folder,
                // then the second collection, active. A deleted collection is checked on a
                // throwaway tab so the restore fixture keeps both.
                var doomed = await workspace.NewCollectionTab(firstSource);
                long gone = CaptureLibrary.CreateCollection("Doomed collection");
                library.SetSource("c" + gone.ToString(CultureInfo.InvariantCulture));
                await Settle(library);
                CaptureLibrary.DeleteCollection(gone);
                library.Reload();
                await Settle(library);
                Check(doomed.Source == "all" && library.CurrentSource == "all", "a deleted collection's tab falls back to every capture");
                await workspace.CloseTab(doomed);
                await workspace.SelectTab(workspace.Tabs[2]);
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
