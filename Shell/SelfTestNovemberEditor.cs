using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerFamily.Layout;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestNovemberEditor
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-november-editor") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args[2]);
        return true;
    }

    private static async Task RunAsync(string folder, string scenario)
    {
        Directory.CreateDirectory(folder);
        int code = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            string screenshots = Path.Combine(folder, "screenshots");
            Directory.CreateDirectory(screenshots);
            FolderCaptures.UseFolder(screenshots);
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            Expect(CaptureLibrary.OpenAuto(), "The isolated library did not open.");
            await FolderCaptures.RefreshScreenshotsAsync();
            if (scenario == "strip") await StripAsync(folder);
            else if (scenario == "strip-deferred-folder") await StripAsync(folder, deferredFolder: true);
            else if (scenario == "colors") Colors();
            else if (scenario == "recent") await RecentAsync();
            else if (scenario == "timer") await HiddenTimerAsync();
            else if (scenario == "resize") await ResizeTextAsync();
            else if (scenario == "mode") CaptureMenuMemory();
            else throw new ArgumentException("Unknown editor test scenario.");
            File.WriteAllText(Path.Combine(folder, "editor-report.txt"), "PASS: " + scenario);
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "editor-report.txt"), "FAIL: " + error); }
        finally { CaptureLibrary.Close(); }
        App.Instance.Shutdown(code);
    }

    private static async Task StripAsync(string folder, bool deferredFolder = false)
    {
        using var reader = deferredFolder ? new DeferredFolderReader() : null;
        if (reader != null)
        {
            string screenshots = Path.Combine(folder, "screenshots");
            Directory.CreateDirectory(screenshots);
            FolderCaptures.UseFolder(screenshots, reader);
        }
        var trace = new List<string>();
        string stage = "initializing";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Note(string text)
        {
            lock (trace) trace.Add($"{clock.Elapsed.TotalMilliseconds:F1} ms [{Environment.CurrentManagedThreadId}] {stage}: {text}");
        }
        void FolderChanged() => Note("FolderCaptures.Changed");
        void LibraryChanged() => Note("CaptureLibrary.Changed");
        FolderCaptures.Changed += FolderChanged;
        CaptureLibrary.Changed += LibraryChanged;
        Note("Folder source " + FolderCaptures.Folder);
        var host = new DockHost();
        using var strip = new CollectionStrip(host, new Style(typeof(Slider)));
        strip.IsVisibleChanged += (_, _) => Note("IsVisible=" + strip.IsVisible);
        strip.SetAlbumMode(true);
        Task? folderScan = reader != null ? FolderCaptures.RefreshScreenshotsAsync() : null;
        var window = new Window { Content = strip, Width = 300, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            await IdleAsync();
            stage = "hiding";
            window.Hide();
            await IdleAsync();
            int changes = 0;
            strip.SourceChanged += () =>
            {
                changes++;
                Note("SourceChanged count=" + changes);
                if (stage == "showing" && changes == 1) reader?.Release.Set();
            };
            stage = "hidden collections";
            for (int i = 0; i < 5; i++) CaptureLibrary.CreateCollection("Hidden " + i);
            await IdleAsync();
            Expect(changes == 0, $"Hidden collection strip reloaded {changes} times.");
            stage = "showing";
            window.Show();
            await IdleAsync();
            if (folderScan != null) { await folderScan.WaitAsync(TimeSpan.FromSeconds(3)); await IdleAsync(); }
            int expected = deferredFolder ? 2 : 1;
            Expect(changes == expected, $"Visible collection strip refreshes after showing: expected {expected}, got {changes}.");
            changes = 0;
            stage = "visible collections";
            for (int i = 0; i < 5; i++) CaptureLibrary.CreateCollection("Visible " + i);
            await IdleAsync();
            Expect(changes == 1, $"Visible collection burst reloaded {changes} times instead of once.");
        }
        finally
        {
            stage = "closing";
            reader?.Release.Set();
            window.Close();
            FolderCaptures.Changed -= FolderChanged;
            CaptureLibrary.Changed -= LibraryChanged;
            lock (trace) File.WriteAllLines(Path.Combine(folder, "strip-events.txt"), trace);
        }
    }

    private sealed class DeferredFolderReader : IFolderDirectoryReader, IDisposable
    {
        internal ManualResetEventSlim Release { get; } = new();
        public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory, EnumerationOptions options)
        {
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The folder fixture was not released.");
            return directory.EnumerateFiles("*", options);
        }
        public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory, EnumerationOptions options)
            => directory.EnumerateDirectories("*", options);
        public void Dispose() => Release.Dispose();
    }

    private static Task IdleAsync() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static void Colors()
    {
        long id = CaptureLibrary.CreateCollection("Color fixture");
        CardColors.SetCollection(id, System.Windows.Media.Colors.Red);
        Expect(CardColors.Collection(id) == System.Windows.Media.Colors.Red, "The collection color did not resolve.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20000; i++)
            Expect(CardColors.Collection(id) == System.Windows.Media.Colors.Red, "Repeated card lookup lost its color.");
        Expect(watch.ElapsedMilliseconds < 100, $"Repeated collection-color rendering took {watch.ElapsedMilliseconds} ms.");
        CardColors.SetCollection(id, System.Windows.Media.Colors.Blue);
        Expect(CardColors.Collection(id) == System.Windows.Media.Colors.Blue, "A color edit left stale card colors.");
        CaptureLibrary.Close();
        Expect(CaptureLibrary.OpenAuto(), "The isolated library did not reopen.");
        Expect(CardColors.Collection(id) == System.Windows.Media.Colors.Blue, "A new library session lost its collection color.");
    }

    private static async Task RecentAsync()
    {
        var window = new MainWindow(null) { Left = -20000, Top = -20000, Width = 1100, Height = 800, ShowActivated = false, ShowInTaskbar = false };
        ContextMenu? menu = null;
        try
        {
            window.Show();
            await IdleAsync();
            window.OpenBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            menu = System.Windows.PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                .Where(source => source.RootVisual != null).SelectMany(source => Visuals(source.RootVisual))
                .OfType<ContextMenu>().Single(context => context.Items.OfType<MenuItem>().Any(item => Equals(item.Header, LocaleManager.Loc("Str_Btn_Recent"))));
            var recent = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocaleManager.Loc("Str_Btn_Recent")));
            Expect(recent.Items.Count == 1 && recent.Items[0] is MenuItem { IsEnabled: false }, "Opening the parent menu eagerly filled Recent capture rows.");
            recent.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, recent));
            Expect(recent.Items.OfType<MenuItem>().Any(item => Equals(item.Header, LocaleManager.Loc("Str_Recent_ShowLibrary"))), "Expanding Recent did not fill its capture choices.");
        }
        finally { if (menu != null) menu.IsOpen = false; window.Close(); }
    }

    private static async Task HiddenTimerAsync()
    {
        var window = new MainWindow(null);
        try
        {
            await Task.Delay(350);
            await IdleAsync();
            int background = 0;
            void Posted(object? sender, DispatcherHookEventArgs args)
            {
                if (args.Operation.Priority == DispatcherPriority.Background) background++;
            }
            var hooks = Dispatcher.CurrentDispatcher.Hooks;
            hooks.OperationPosted += Posted;
            hooks.OperationPriorityChanged += Posted;
            hooks.OperationCompleted += Posted;
            try
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Expect(background > 0, "Dispatcher observation did not see its positive control.");
                background = 0;
                await Task.Delay(500);
                Expect(background == 0, $"Hidden unshown editor kept posting background timer work ({background}).");
            }
            finally { hooks.OperationPosted -= Posted; hooks.OperationPriorityChanged -= Posted; hooks.OperationCompleted -= Posted; }
        }
        finally { window.Close(); }
    }

    private static async Task ResizeTextAsync()
    {
        var image = System.Windows.Media.Imaging.BitmapSource.Create(400, 300, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[400 * 300 * 4], 400 * 4);
        image.Freeze();
        var document = new KillerShot.Editor.EditorDocument(image);
        Expect(App.Instance.SetDocument(document), "The editor fixture document did not open.");
        var window = new MainWindow(null) { Left = -20000, Top = -20000, Width = 1100, Height = 800, ShowActivated = false, ShowInTaskbar = false };
        ContextMenu? menu = null;
        try
        {
            window.Show();
            await IdleAsync();
            window.Editor.SetTool(KillerShot.Editor.ToolKind.Text);
            Expect(window.Editor.PlaceMark(), "The real text tool did not begin a pending text mark.");
            window.Editor.Children.OfType<TextBox>().Single().Text = "Pending resize text";
            Expect(document.State.Items.IsEmpty, "The fixture text was committed before the resize action.");
            window.ImageBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            menu = System.Windows.PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                .Where(source => source.RootVisual != null).SelectMany(source => Visuals(source.RootVisual))
                .OfType<ContextMenu>().Single(context => context.Items.OfType<MenuItem>().Any(item => Equals(item.Header, LocaleManager.Loc("Str_Img_Resize"))));
            var resize = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocaleManager.Loc("Str_Img_Resize")));
            var preset = resize.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "50%"));
            preset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, preset));
            Expect(document.PixelWidth == 200 && document.PixelHeight == 150, "The real resize preset did not resize the picture.");
            Expect(document.State.Items.SingleOrDefault() is KillerShot.Editor.TextAnnotation { Text: "Pending resize text" }, "The resize preset lost the uncommitted text mark.");
            Expect(!window.Editor.IsEditingText, "The resize preset left text editing pending.");
        }
        finally { if (menu != null) menu.IsOpen = false; window.Close(); }
    }

    private static void CaptureMenuMemory()
    {
        CaptureSettings.LastMode = Capture.CaptureMode.Window;
        var window = new MainWindow(null);
        try
        {
            var items = window.CaptureMenu.Items.OfType<MenuItem>().ToArray();
            Expect(items.Any(item => Equals(item.Header, LocaleManager.Loc("Str_Btn_Capture")) && item.InputGestureText == "Ctrl+N"), "The capture menu did not expose the remembered-mode generic action.");
            Expect(items.Single(item => Equals(item.Header, LocaleManager.Loc("Str_Mode_Window"))).IsChecked, "The capture menu checked Region instead of the remembered mode.");
            Expect(items.Single(item => Equals(item.Header, LocaleManager.Loc("Str_Mode_Region"))).InputGestureText != "Ctrl+N", "The explicit Region entry claimed the generic shortcut.");
            Expect(items.Length >= 6, "The capture menu lost explicit mode choices.");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
