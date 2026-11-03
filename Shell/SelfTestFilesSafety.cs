using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesSafety
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--selftest-files-safety") return false;
        _ = Run(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : "all");
        return true;
    }

    private static async Task Run(string folder, string scenario)
    {
        int checks = 0, exit = 1;
        var report = new StringBuilder();
        void Check(bool passed, string message)
        {
            checks++;
            report.AppendLine((passed ? "PASS: " : "FAIL: ") + message);
            if (!passed) throw new InvalidOperationException(message);
        }
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a fresh safety fixture.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (scenario == "all")
            {
                await Containment(folder, Check);
                await CacheRetention(folder, Check, report);
                await RequestedSize(folder, Check);
                await Orientations(folder, Check, report);
                await ColumnFocus(Check, report);
                await ColumnMenu(Check, report);
            }
            else if (scenario == "containment") await Containment(folder, Check);
            else if (scenario == "cache") await CacheRetention(folder, Check, report);
            else if (scenario == "size") await RequestedSize(folder, Check);
            else if (scenario == "orientation") await Orientations(folder, Check, report);
            else if (scenario == "columns-menu") await ColumnMenu(Check, report);
            else if (scenario == "columns-focus") await ColumnFocus(Check, report);
            else throw new ArgumentException("Unknown safety scenario: " + scenario);
            exit = 0;
        }
        catch (Exception error) { report.AppendLine(error.ToString()); }
        finally
        {
            report.AppendLine($"Assertions: {checks}; Exit: {exit}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "files-safety-report.txt"), report.ToString());
            Application.Current.Shutdown(exit);
        }
    }

    private static async Task Containment(string folder, Action<bool, string> check)
    {
        string source = Directory.CreateDirectory(Path.Combine(folder, "A")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(source, "B")).FullName;
        File.WriteAllText(Path.Combine(source, "original.txt"), "original bytes");
        using var files = new FilesView();
        check(await files.NavigateTo(child), "owned child opens through public navigation");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { source });
        files.ReadClipboard = () => data;
        var result = await files.PasteFiles();
        check(result is { Succeeded: 0, Failed.Count: > 0 } && !Directory.Exists(Path.Combine(child, "A"))
            && File.ReadAllText(Path.Combine(source, "original.txt")) == "original bytes",
            "public paste rejects a directory into its descendant before creating anything");
        check(await files.NavigateTo(source), "owned source opens for self-paste control");
        result = await files.PasteFiles();
        check(result is { Succeeded: 0, Failed.Count: > 0 } && !Directory.Exists(Path.Combine(source, "A")),
            "public paste rejects a directory into itself before creating anything");
        var self = FileOps.CopyOrMove([source], Path.Combine(source, "."), false,
            _ => ConflictChoice.KeepBoth, _ => { }, CancellationToken.None);
        check(self is { Succeeded: 0, Failed.Count: > 0 } && !Directory.Exists(Path.Combine(source, "A")),
            "canonical public operation rejects normalized self destination");
        string loose = Path.Combine(folder, "loose.txt");
        File.WriteAllText(loose, "loose bytes");
        var mixed = FileOps.CopyOrMove([loose, source], Path.Combine(child, "..", "B") + Path.DirectorySeparatorChar,
            true, _ => ConflictChoice.KeepBoth, _ => { }, CancellationToken.None);
        check(mixed is { Succeeded: 0, Failed.Count: > 0 } && File.ReadAllText(loose) == "loose bytes"
            && !File.Exists(Path.Combine(child, "loose.txt")) && !Directory.Exists(Path.Combine(child, "A")),
            "mixed-source move rejects the entire normalized descendant batch without moving an earlier file");
        var dropped = await files.DropFiles(data, child, DragDropEffects.Copy, ModifierKeys.Control);
        check(dropped == null && files.CommandError != null && !Directory.Exists(Path.Combine(child, "A")),
            "public drop also rejects descendant copy without mutation");
        var duplicate = FileOps.CopyOrMove([loose], folder, false, _ => ConflictChoice.KeepBoth,
            _ => { }, CancellationToken.None);
        check(duplicate is { Succeeded: 1, Failed.Count: 0 } && duplicate.SucceededTargets.Single() != loose
            && File.ReadAllText(duplicate.SucceededTargets.Single()) == "loose bytes" && File.ReadAllText(loose) == "loose bytes",
            "same-folder file copy preserves public duplicate naming and both copies");
        string outside = Directory.CreateDirectory(Path.Combine(folder, "outside")).FullName;
        var ordinary = FileOps.CopyOrMove([source], outside, false, _ => ConflictChoice.KeepBoth,
            _ => { }, CancellationToken.None);
        check(ordinary is { Succeeded: 1, Failed.Count: 0 }
            && File.ReadAllText(Path.Combine(outside, "A", "original.txt")) == "original bytes"
            && Directory.Exists(Path.Combine(outside, "A", "B")), "ordinary directory copy to a sibling remains supported");
    }

    private static void WriteImage(string path, int width = 320, int height = 160)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 220; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task<WeakReference> RequestWeak(string path)
    {
        var image = await IconCache.ForAsync(path, 64);
        if (image is not BitmapSource { PixelWidth: 64, PixelHeight: 32 })
            throw new InvalidOperationException("Public thumbnail did not decode owned image.");
        return new WeakReference(image);
    }

    private static void CollectImages()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task CacheRetention(string folder, Action<bool, string> check, StringBuilder report)
    {
        var references = new List<WeakReference>();
        for (int i = 0; i < 270; i++)
        {
            string path = Path.Combine(folder, $"photo-{i}.png");
            WriteImage(path);
            references.Add(await RequestWeak(path));
        }
        CollectImages();
        int alive = references.Count(reference => reference.IsAlive);
        report.AppendLine($"Public thumbnail objects retained after 270 requests and GC: {alive}");
        check(alive <= 256 && !references[0].IsAlive && references[^1].IsAlive,
            "photo cache retains at most 256 public thumbnail objects and releases evicted images");
        var hot = await RequestWeak(Path.Combine(folder, "photo-14.png"));
        string extra = Path.Combine(folder, "photo-270.png");
        WriteImage(extra);
        references.Add(await RequestWeak(extra));
        CollectImages();
        check(hot.IsAlive && references[14].IsAlive && !references[15].IsAlive,
            "public cache hit refreshes recency so the next oldest photo is evicted");
        string edited = Path.Combine(folder, "edited.png");
        var versions = new List<WeakReference>();
        for (int i = 0; i < 5; i++)
        {
            WriteImage(edited);
            File.SetLastWriteTimeUtc(edited, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
            versions.Add(await RequestWeak(edited));
        }
        CollectImages();
        report.AppendLine($"Public edited thumbnail versions retained after GC: {versions.Count(reference => reference.IsAlive)} of 5");
        check(versions.Take(4).All(reference => !reference.IsAlive) && versions[^1].IsAlive,
            "changed timestamps release all superseded public thumbnail objects");
    }

    private static async Task RequestedSize(string folder, Action<bool, string> check)
    {
        string path = Path.Combine(folder, "sizes.png");
        WriteImage(path);
        var small = await IconCache.ForAsync(path, 64) as BitmapSource;
        check(small is { PixelWidth: 64, PixelHeight: 32 }, "public small request decodes 64 by 32");
        var large = await IconCache.ForAsync(path, 256) as BitmapSource;
        check(large is { PixelWidth: 256, PixelHeight: 128 },
            "public large request after small request honors 256 pixels within the same shell tier");
    }

    private static readonly Color[] FixtureColors = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow, Colors.Magenta, Colors.Cyan];

    private static void WriteTaggedImage(string path, bool tiff, ushort orientation)
    {
        var pixels = new byte[96 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 96; x++)
            {
                Color color = FixtureColors[y / 32 * 3 + x / 32];
                int offset = (y * 96 + x) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
            }
        var image = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 96 * 4);
        var metadata = new BitmapMetadata(tiff ? "tiff" : "jpg");
        metadata.SetQuery(tiff ? "/ifd/{ushort=274}" : "/app1/ifd/{ushort=274}", orientation);
        BitmapEncoder encoder = tiff ? new TiffBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ReadColorCells(BitmapSource image, int columns, int rows)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var cells = new StringBuilder();
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                var pixel = new byte[4];
                converted.CopyPixels(new Int32Rect((2 * column + 1) * image.PixelWidth / (2 * columns),
                    (2 * row + 1) * image.PixelHeight / (2 * rows), 1, 1), pixel, 4, 0);
                var closest = FixtureColors.Select((color, index) => (Index: index,
                    Error: Math.Abs(color.B - pixel[0]) + Math.Abs(color.G - pixel[1]) + Math.Abs(color.R - pixel[2])))
                    .OrderBy(candidate => candidate.Error).First();
                cells.Append(closest.Error < 60 ? (char)('A' + closest.Index) : '?');
            }
        return cells.ToString();
    }

    private static async Task Orientations(string folder, Action<bool, string> check, StringBuilder report)
    {
        // Literal displayed cell orders for stored ABC / DEF, from the eight EXIF orientations.
        string[] expected = ["ABCDEF", "CBAFED", "FEDCBA", "DEFABC", "ADBECF", "DAEBFC", "FCEBDA", "CFBEAD"];
        foreach (bool tiff in new[] { false, true })
            for (ushort tag = 1; tag <= 8; tag++)
            {
                string path = Path.Combine(folder, $"tag-{tag}." + (tiff ? "tif" : "jpg"));
                WriteTaggedImage(path, tiff, tag);
                using (var stream = File.OpenRead(path))
                {
                    var frame = BitmapFrame.Create(stream);
                    var metadata = (BitmapMetadata)frame.Metadata;
                    check(Convert.ToUInt16(metadata.GetQuery(tiff ? "/ifd/{ushort=274}" : "/app1/ifd/{ushort=274}")) == tag,
                        $"fixture {(tiff ? "TIFF" : "JPEG")} stores actual orientation {tag}");
                }
                var image = await IconCache.ForAsync(path, 96) as BitmapSource;
                int width = tag >= 5 ? 64 : 96, height = tag >= 5 ? 96 : 64;
                string cells = image == null ? "null" : ReadColorCells(image, tag >= 5 ? 2 : 3, tag >= 5 ? 3 : 2);
                report.AppendLine($"{Path.GetFileName(path)} actual={image?.PixelWidth}x{image?.PixelHeight} cells={cells} expected={expected[tag - 1]}");
                check(image != null && image.PixelWidth == width && image.PixelHeight == height && cells == expected[tag - 1],
                    $"public {(tiff ? "TIFF" : "JPEG")} thumbnail orientation {tag} matches literal asymmetric cells and bounded dimensions");
                var small = await IconCache.ForAsync(path, 48) as BitmapSource;
                string smallCells = small == null ? "null" : ReadColorCells(small, tag >= 5 ? 2 : 3, tag >= 5 ? 3 : 2);
                check(small != null && small.PixelWidth == width / 2 && small.PixelHeight == height / 2 && smallCells == expected[tag - 1],
                    $"public {(tiff ? "TIFF" : "JPEG")} orientation {tag} downsamples to a bounded 48-pixel longest side");
            }
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    private sealed class TestKeyboard() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }

    private static async Task ColumnMenu(Action<bool, string> check, StringBuilder report)
    {
        using var files = new FilesView();
        files.ViewState.CreatedVisible = false;
        var window = new Window { Content = files, Width = 1200, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            files.SetView(2);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var header = (FrameworkElement)files.FindName("DetailsHeader");
            var name = Children<Button>(header).Single(button => Grid.GetColumn(button) == 1);
            check(name.IsVisible && name.ActualWidth > 0, "owned offscreen Details header is laid out");
            ContextMenu? offered = null;
            bool keyboardOpening = false;
            header.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, e) =>
            {
                keyboardOpening = e.CursorLeft == -1 && e.CursorTop == -1;
                offered = header.ContextMenu;
                e.Handled = true; // Observe the actual routed opening without displaying a popup.
            }), true);
            var keyboard = new TestKeyboard();
            keyboard.Focus(name);
            var key = new KeyEventArgs(keyboard, PresentationSource.FromVisual(window)!, 0, Key.Apps)
                { RoutedEvent = Keyboard.KeyUpEvent };
            bool handled = InputManager.Current.ProcessInput(key);
            report.AppendLine($"Context keyboard opening={keyboardOpening}; input handled={handled}; menu items={offered?.Items.Count}");
            check(keyboardOpening && offered?.Items.Count == 4,
                "context-menu key on actual header routes to the four localized column choices");
            var created = offered!.Items.OfType<MenuItem>().Last();
            check(!files.ViewState.CreatedVisible && !created.IsChecked && Convert.ToString(created.Header) == "created",
                "keyboard column menu exposes the existing localized unchecked Created action");
            var peer = new MenuItemAutomationPeer(created);
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(files.ViewState.CreatedVisible && created.IsChecked,
                "actual column menu automation toggle makes Created visible");
        }
        finally { window.Close(); }
    }
    private static async Task ColumnFocus(Action<bool, string> check, StringBuilder report)
    {
        using var files = new FilesView();
        files.ViewState.CreatedVisible = false;
        files.ViewState.SizeVisible = files.ViewState.ModifiedVisible = files.ViewState.TypeVisible = true;
        var window = new Window { Content = files, Width = 1200, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            files.SetView(2);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var header = (FrameworkElement)files.FindName("DetailsHeader");
            var headings = Children<Button>(header).OrderBy(Grid.GetColumn).ToArray();
            check(headings.Length == 6 && headings[0].IsVisible, "all actual header actions exist in offscreen fixture");
            KeyboardNavigation.SetTabNavigation(header, KeyboardNavigationMode.Cycle);
            FocusManager.SetIsFocusScope(header, true);
            FocusManager.SetFocusedElement(header, headings[0]);
            bool moved = headings[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            var focused = FocusManager.GetFocusedElement(header) as Button;
            report.AppendLine($"Forward after Name: moved={moved}, column={(focused == null ? -1 : Grid.GetColumn(focused))}");
            check(moved && focused == headings[2], "forward Tab skips hidden Folder and reaches Size");
            focused!.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            check(FocusManager.GetFocusedElement(header) == headings[0], "backward Tab skips hidden Folder and returns to Name");
            headings[4].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[0], "forward Tab skips default hidden Created");
            files.ViewState.CreatedVisible = true;
            files.ViewState.LocationHidden = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            headings[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[1], "shown Folder returns to forward Tab traversal");
            headings[4].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[5], "shown Created returns to forward Tab traversal");
            files.ViewState.SizeVisible = files.ViewState.ModifiedVisible = files.ViewState.TypeVisible = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            headings[1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[5], "forward Tab skips hidden Size Modified and Type");
            headings[5].MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            check(FocusManager.GetFocusedElement(header) == headings[1], "backward Tab skips hidden Size Modified and Type");
        }
        finally { window.Close(); }
    }
}
