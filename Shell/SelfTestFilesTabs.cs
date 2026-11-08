using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesTabs
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--selftest-files-tabs") return false;
        _ = Run(Path.GetFullPath(args[1]), args.Length == 3 && args[2] == "restore");
        return true;
    }

    private static async Task Run(string folder, bool restore)
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
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureSettings.OutputFolder = Directory.CreateDirectory(Path.Combine(folder, "first")).FullName;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            library = new LibraryView();
            window = new Window { Content = library, Width = 1000, Height = 700, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            library.SetView("files");
            await library.FilesNavigation;
            var workspace = library.FilesWorkspace;
            Check(workspace.FindName("TabStrip") != null, "Files exposes its tab strip");
            if (restore)
            {
                Check(workspace.Tabs.Count == 2, "restart restores two tabs");
                Check(workspace.Tabs.IndexOf(workspace.ActiveTab) == 1, "restart restores active tab and order");
                var first = workspace.ActiveTab;
                Check(first.View.Navigation.CurrentFolder == CaptureSettings.OutputFolder, "restart restores active folder");
                Check(first.View.Navigation.FilterText == "item", "restart restores tab filter");
                Check(first.View.CurrentMode == 2 && first.View.ViewState.Density == 3, "restart restores view and density");
                Check(first.View.Navigation.SortIndex == 1 && first.View.Navigation.SortAsc, "restart restores sort");
                Check(first.View.SelectedPaths.Single().EndsWith("item-025.txt"), "restart restores selection");
                Check(first.View.Navigation.CanGoBack, "restart restores navigation history");
                await Idle();
                Check(Descendants<ScrollViewer>(first.View.ResultsList).First().VerticalOffset > 0, "restart restores scrolling");
                await workspace.SelectTab(workspace.Tabs[0]);
                Check(workspace.ActiveView.Navigation.CurrentFolder == Path.Combine(folder, "second"), "restart lazily restores other folder");
                Check(workspace.ActiveView.Navigation.FilterText == "other" && workspace.ActiveView.CurrentMode == 1,
                    "restart keeps other tab options independent");
                await workspace.CloseTab(workspace.ActiveTab);
                Check(workspace.Tabs.Count == 1 && workspace.ActiveTab == first, "closing active tab selects its neighbor");
                await workspace.CloseTab(first);
                Check(workspace.Tabs.Count == 1 && workspace.ActiveView.Navigation.CurrentFolder.Length > 0,
                    "closing last tab leaves a usable browser");
                Settings.Set("Files.Tabs", "malformed");
                using var recovery = new FilesWorkspace();
                Check(recovery.Tabs.Count == 1, "malformed session opens one usable tab");
            }
            else
            {
                string firstFolder = CaptureSettings.OutputFolder;
                string secondFolder = Directory.CreateDirectory(Path.Combine(folder, "second")).FullName;
                string child = Directory.CreateDirectory(Path.Combine(firstFolder, "child")).FullName;
                for (int i = 0; i < 180; i++) File.WriteAllText(Path.Combine(firstFolder, $"item-{i:000}.txt"), "owned");
                File.WriteAllText(Path.Combine(secondFolder, "other.txt"), "second");
                var first = workspace.ActiveTab;
                await first.View.NavigateTo(child);
                await first.View.NavigateTo(firstFolder);
                first.View.SetView(2);
                first.View.SetDensity(3);
                first.View.SetSort(1, true);
                first.View.SetFilter("item");
                first.View.ResultsList.SelectedItem = first.View.Navigation.Results.Single(entry => entry.FileName == "item-025.txt");
                await Idle();
                first.View.UpdateLayout();
                var scroll = Descendants<ScrollViewer>(first.View.ResultsList).First();
                scroll.ScrollToVerticalOffset(35);
                await Idle();
                double offset = scroll.VerticalOffset;
                Check(offset > 0, "fixture actually scrolls the first tab");
                var second = await workspace.NewTab(secondFolder);
                second.View.SetView(1);
                second.View.SetFilter("other");
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab == second, "new tab selects its independent folder");
                await workspace.SelectTab(first);
                Check(first.View.Navigation.CurrentFolder == firstFolder && first.View.Navigation.FilterText == "item", "switch retains folder and filter");
                Check(first.View.CurrentMode == 2 && first.View.ViewState.Density == 3, "switch retains independent display options");
                Check(first.View.SelectedPaths.Single().EndsWith("item-025.txt"), "switch retains selection");
                await Idle();
                Check(scroll.VerticalOffset == offset, "switch retains scroll offset");
                Check(first.View.Navigation.CanGoBack, "switch retains history");
                library.SetView("grid");
                library.SetView("files");
                await library.FilesNavigation;
                Check(workspace.ActiveTab == first && first.View.Navigation.CurrentFolder == firstFolder,
                    "library round trip retains active tab and folder");
                Check(first.View.Navigation.FilterText == "item" && first.View.SelectedPaths.Count == 1,
                    "library round trip retains filter and selection");
                Check(workspace.HandleTabKey(Key.Tab, ModifierKeys.Control) && workspace.ActiveTab == second,
                    "Ctrl+Tab selects next tab");
                Check(workspace.HandleTabKey(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift) && workspace.ActiveTab == first,
                    "Ctrl+Shift+Tab selects previous tab");
                workspace.MoveTab(first, 1);
                Check(workspace.Tabs[1] == first && workspace.ActiveTab == first, "reorder retains active tab identity");
                workspace.ConfigureEditor(_ => true, _ => Task.FromResult(true));
                Check(await second.View.Browser.ActivateEntry(second.View.Navigation.Results.Single()), "editor callback reaches an existing inactive tab");
                var temporary = await workspace.NewTab(secondFolder);
                Check(await temporary.View.Browser.ActivateEntry(temporary.View.Navigation.Results.Single()), "new tabs inherit editor handoff");
                await workspace.CloseTab(temporary);
                await workspace.SelectTab(first);
                Check(workspace.Tabs.Count == 2, "closing temporary tab preserves other tabs");
                var opening = workspace.NewTab(secondFolder);
                var closing = workspace.ActiveTab;
                await workspace.CloseTab(closing);
                await opening;
                await workspace.SelectTab(first);
                Check(workspace.Tabs.Count == 2, "closing a loading tab does not revive it");
                var extraTabs = new List<FilesTab>();
                for (int i = 0; i < 6; i++) extraTabs.Add(await workspace.NewTab(secondFolder));
                await Idle();
                Check(workspace.Tabs.Any(tab => !tab.IsStripVisible) && workspace.ActiveTab.IsStripVisible,
                    "overflow keeps the active tab visible");
                await workspace.SelectTab(first);
                Check(first.IsStripVisible, "selecting a hidden tab moves the visible tab window");
                foreach (var extra in extraTabs) await workspace.CloseTab(extra);
                Check(workspace.Tabs.Count == 2 && workspace.ActiveTab == first, "closing overflow tabs retains the session");
                foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
                {
                    ThemeManager.Apply(theme);
                    await Idle();
                    window.UpdateLayout();
                    var strip = (ItemsControl)workspace.FindName("TabStrip");
                    Check(Descendants<Button>(strip).Count(button => button.IsVisible) == 4, $"{theme} renders two tab labels and close buttons");
                    var frame = first.View.FindName("PaneFrame") as Border;
                    var top = first.View.FindName("MenuBars") as FrameworkElement;
                    var bottom = first.View.FindName("StatusBar") as FrameworkElement;
                    Check(frame != null && top != null && bottom != null, $"{theme} exposes the Files page and menu bands");
                    Check(Math.Abs(first.View.ActualWidth - frame!.ActualWidth) < 1,
                        $"{theme} page reaches both pane edges");
                    double inner = frame.ActualWidth - frame.BorderThickness.Left - frame.BorderThickness.Right;
                    Check(Math.Abs(top!.ActualWidth - inner) < 1 && Math.Abs(bottom!.ActualWidth - inner) < 1,
                        $"{theme} top and bottom menu bands span the full page width");
                    var bitmap = new RenderTargetBitmap((int)workspace.ActualWidth, (int)workspace.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(workspace);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(folder, $"tabs-{theme}.png"));
                    encoder.Save(output);
                }
                workspace.SaveSession();
            }
            File.WriteAllText(Path.Combine(folder, restore ? "files-tabs-restore-report.txt" : "files-tabs-report.txt"), $"PASS: {checks} assertions.\n{report}");
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, restore ? "files-tabs-restore-report.txt" : "files-tabs-report.txt"), $"FAIL after {checks}: {error}\n{report}"); }
        finally { library?.FilesWorkspace.Dispose(); window?.Close(); }
        App.Instance.Shutdown(code);
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
