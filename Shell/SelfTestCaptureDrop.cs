using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-capture-drop &lt;folder&gt;: a capture dragged from the album onto a
/// collection row in the sidebar moves into that collection, as Move to does (GitHub issue 7).
/// All captures, the capture's own collection and folder files refuse the drop.
/// </summary>
internal static class SelfTestCaptureDrop
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-capture-drop") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.Enabled = false;
        App.InitializeKit();
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int failures = 0;
        void Check(bool pass, string name) { if (!pass) failures++; report.Add((pass ? "PASS " : "FAIL ") + name); }
        MainWindow? window = null;
        try
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            long loose = CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Loose capture", null);
            long kept = CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Kept capture", null);
            long target = CaptureLibrary.CreateCollection("Drop target", []);
            long other = CaptureLibrary.CreateCollection("Other", [kept]);
            window = new MainWindow { Width = 900, Height = 650, ShowActivated = false };
            window.Show();
            var strip = window.TestNavigationStrip!;
            strip.PreviewMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "mini"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ListBoxItem? Row(string source)
            {
                var browser = Descendants<CollectionBrowser>(strip).SingleOrDefault();
                if (browser == null) return null;
                browser.UpdateLayout();
                var choice = browser.Items.OfType<CollectionChoice>().FirstOrDefault(c => c.Source == source);
                return choice == null ? null : browser.ItemContainerGenerator.ContainerFromItem(choice) as ListBoxItem;
            }
            string targetSource = "c" + target, otherSource = "c" + other;
            for (int n = 0; n < 150 && (Row(targetSource) == null || Row(otherSource) == null || Row("all") == null); n++)
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var targetRow = Row(targetSource) ?? throw new InvalidOperationException("The sidebar has no row for the target collection.");
            var allRow = Row("all") ?? throw new InvalidOperationException("The sidebar has no All captures row.");
            var otherRow = Row(otherSource) ?? throw new InvalidOperationException("The sidebar has no row for the other collection.");
            // Hit testing lands on a row's inner text, not the row itself.
            var targetText = Descendants<TextBlock>(targetRow).FirstOrDefault() ?? (DependencyObject)targetRow;

            CaptureItem Item(long id) => CaptureLibrary.Items().Single(i => i.Id == id);
            Check(window.CanDropCapture(Item(loose), targetText, out _), "a collection row accepts a dragged capture");
            Check(!window.CanDropCapture(Item(loose), allRow, out _), "All captures refuses a dragged capture");
            Check(!window.CanDropCapture(Item(kept), otherRow, out _), "a capture's own collection refuses it");
            var folderFile = Item(loose) with { Id = -42, CollectionId = FolderCaptures.CollectionId };
            Check(!window.CanDropCapture(folderFile, targetRow, out _), "a folder file refuses a collection drop");
            Check(!window.CanDropCapture(null, targetRow, out _), "no capture drag means no capture drop");

            Check(window.DropCapture(Item(loose), targetText) && Item(loose).CollectionId == target, "dropping on a collection row moves the capture there");
            Check(window.DropCapture(Item(kept), targetRow) && Item(kept).CollectionId == target, "dropping moves a capture between collections");
            Check(MainWindow.TryCollectionTarget("c0", out long? unfiled) && unfiled == null, "the Unfiled row takes a capture out of its collection");
            Check(!MainWindow.TryCollectionTarget("folder", out _) && !MainWindow.TryCollectionTarget("all", out _),
                "folder and All rows are not drop targets");
        }
        catch (Exception error) { failures++; report.Add(error.ToString()); }
        report.Add(failures == 0 ? "result=ok" : $"result=error failures={failures}");
        try { window?.Close(); } catch (Exception error) { report.Add(error.ToString()); }
        try { CaptureLibrary.Close(); } catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "capture-drop-report.txt"), report);
        App.Instance.Shutdown(failures == 0 ? 0 : 1);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int n = 0; n < VisualTreeHelper.GetChildrenCount(root); n++)
        {
            var child = VisualTreeHelper.GetChild(root, n);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
