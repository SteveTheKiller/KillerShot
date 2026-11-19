using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Controls;
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

            // The real drop point: hit-test the centre of each target and hand CanDropCapture
            // whatever WPF finds there, as PreviewDrop does with e.OriginalSource.
            DependencyObject? HitAt(FrameworkElement target)
            {
                target.UpdateLayout();
                if (!target.IsVisible || target.ActualWidth < 1 || target.ActualHeight < 1) return null;
                if (PresentationSource.FromVisual(target)?.RootVisual is not Visual root) return null;
                var centre = target.TransformToAncestor(root).Transform(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                // Skip what the input system skips, such as the theme ink laid over a popup.
                DependencyObject? hit = null;
                VisualTreeHelper.HitTest(root,
                    d => d is UIElement { IsHitTestVisible: false } or UIElement { IsVisible: false }
                        ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
                    r => { hit = r.VisualHit; return HitTestResultBehavior.Stop; },
                    new PointHitTestParameters(centre));
                return hit;
            }
            async Task Settle() { for (int n = 0; n < 20; n++) await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
            var library = (LibraryView)window.FindName("Library");
            Check(window.CanDropCapture(Item(loose), HitAt(targetRow), out long? rowHit) && rowHit == target, "hit-test: a sidebar collection row accepts a drop at its centre");

            library.TestSet("grid", "collection", "");
            await Settle();
            var header = Descendants<CollectionHeader>(library).FirstOrDefault(h => h.DataContext is LibraryRow { Key: var key } && key == targetSource);
            Check(header != null && window.CanDropCapture(Item(loose), HitAt(header), out long? headerHit) && headerHit == target,
                "hit-test: a collection header on the library page accepts a drop at its centre");

            library.TestSet("shelves", "collection", "");
            await Settle();
            var shelf = Descendants<LibraryRowHost>(library).FirstOrDefault(h => h.DataContext is LibraryRow { Kind: LibraryRowKind.Shelf, Key: var key } && key == otherSource);
            Check(shelf != null && window.CanDropCapture(Item(loose), HitAt(shelf), out long? shelfHit) && shelfHit == other,
                "hit-test: a collection shelf on the library page accepts a drop at its centre");
            Check(shelf != null && !window.CanDropCapture(Item(kept), HitAt(shelf), out _), "hit-test: a capture's own shelf refuses it");

            var picker = ((ContentControl)window.FindName("ToolbarCollectionHost")).Content as ComboBox;
            picker?.ApplyTemplate();
            if (picker != null) { picker.IsDropDownOpen = true; await Settle(); }
            var entry = picker?.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, targetSource));
            var unfiledEntry = picker?.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, "c0"));
            Check(entry != null && window.CanDropCapture(Item(kept), HitAt(entry), out long? entryHit) && entryHit == target,
                "hit-test: a collection entry in the toolbar picker accepts a drop at its centre");
            Check(unfiledEntry != null && window.CanDropCapture(Item(kept), HitAt(unfiledEntry), out long? unfiledHit) && unfiledHit == null,
                "hit-test: the toolbar picker's Unfiled entry takes a capture out of its collection");
            if (picker != null) { picker.IsDropDownOpen = false; await Settle(); }
            Check(picker != null && !window.CanDropCapture(Item(loose), HitAt(picker), out _), "hit-test: the closed picker showing All captures refuses a drop");

            ImageDrag.BeginDropTracking();
            Check(ImageDrag.KeepExport((int)DragDropEffects.Copy), "a copy to another program keeps its exported file");
            Check(window.DropCapture(Item(loose), targetText) && Item(loose).CollectionId == target, "dropping on a collection row moves the capture there");
            Check(!ImageDrag.KeepExport((int)DragDropEffects.Copy), "a drop handled inside KillerShot deletes the decrypted drag file");
            ImageDrag.BeginDropTracking();
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
