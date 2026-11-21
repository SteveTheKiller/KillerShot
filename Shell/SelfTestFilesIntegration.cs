using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesIntegration
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-files-integration") return false;
        _ = Run(Path.GetFullPath(args[1]));
        return true;
    }

    private sealed class TestKeyboard(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            Key.LeftAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            _ => KeyStates.None,
        };
    }

    private static bool SendKey(MainWindow window, IInputElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var keyboard = new TestKeyboard(modifiers);
        var source = PresentationSource.FromVisual(window)!;
        var down = new KeyEventArgs(keyboard, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(down);
        if (down.Handled) return true;
        down = new KeyEventArgs(keyboard, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(down);
        return down.Handled;
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

    private static async Task Run(string folder)
    {
        var report = new StringBuilder();
        MainWindow? window = null;
        int exit = 1, checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
            report.AppendLine("PASS: " + message);
        }
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new integration fixture folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Directory.CreateDirectory(Path.Combine(folder, "screenshots")).FullName);
            FolderCaptures.Enabled = false;
            CaptureSettings.OutputFolder = Path.Combine(folder, "screenshots");
            VideoSettings.OutputFolder = Directory.CreateDirectory(Path.Combine(folder, "recordings")).FullName;
            FolderCaptures.RecordingsFolderOverride = VideoSettings.OutputFolder;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new IOException("Owned album did not open.");
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
                Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(100);
            var library = (LibraryView)window.FindName("Library");
            SendKey(window, library, Key.F9);
            Check(library.CurrentView == "files" && Settings.Get("LibraryView") == "files", "F9 selects and persists Files");
            var footer = (System.Windows.Controls.Panel)window.FindName("FooterLibraryViews");
            var filesButton = footer.Children.OfType<System.Windows.Controls.RadioButton>().SingleOrDefault(button =>
                Convert.ToString(button.Content) == "\uE8B7" && button.GroupName == "FooterLibraryView");
            Check(filesButton != null, "footer exposes Files radio");
            library.SetView("grid");
            filesButton!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(library.FilesActive && Settings.Get("LibraryView") == "files", "footer selects and persists Files");
            // Opening the app again on the saved Files tab: the editor's frame stays off from the
            // first frame, not only after the view is switched once.
            {
                await Task.Delay(200);
                var startup = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
                    Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
                startup.Show();
                await Task.Delay(300);
                var startupLibrary = (LibraryView)startup.FindName("Library");
                var startupPane = (System.Windows.Controls.Border)startup.FindName("ContentPane");
                report.AppendLine($"startup_files: active={startupLibrary.FilesActive} pane={startupPane.BorderThickness} effect={startupPane.Effect != null}");
                Check(startupLibrary.FilesActive && startupPane.Effect == null && startupPane.BorderThickness == new Thickness(0),
                    "the app opened on Files shows no editor frame around it");
                startup.Close();
                await Task.Delay(100);
            }            Check(!((KillerFamily.Layout.DockHost)window.FindName("StripDock")).ShowPane, "Files hides the photo strip");
            var files = library.Files;
            await library.FilesNavigation;
            string original = Path.Combine(CaptureSettings.OutputFolder, "rename.txt");
            var originalTab = library.FilesWorkspace.ActiveTab;
            int tabsBefore = library.FilesWorkspace.Tabs.Count;
            SendKey(window, files.ResultsList, Key.T, ModifierKeys.Control);
            for (int wait = 0; wait < 200 && library.FilesWorkspace.ActiveView!.Navigation.CurrentFolder.Length == 0; wait++) await Task.Delay(10);
            // Files keeps folder tabs only, so Ctrl+T adds one folder tab beside the original.
            Check(library.FilesWorkspace.Tabs.Count == tabsBefore + 1 && library.FilesWorkspace.ActiveTab.IsFolder
                && !ReferenceEquals(library.FilesWorkspace.ActiveTab, originalTab), "host Ctrl+T opens a Files tab");
            window.UpdateLayout();
            var contentPane = (Border)window.FindName("ContentPane");
            var frame = library.Files.PaneFrame;
            var edge = frame.TranslatePoint(new Point(), contentPane);
            Check(Math.Abs(edge.X) < 1 && Math.Abs(frame.ActualWidth - contentPane.ActualWidth) < 1,
                "Files page spans the real host pane without a nested inset");
            var render = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(window);
            var rendered = new PngBitmapEncoder();
            rendered.Frames.Add(BitmapFrame.Create(render));
            using (var imageOutput = File.Create(Path.Combine(folder, "files-tabs-host.png"))) rendered.Save(imageOutput);
            SendKey(window, library.Files.ResultsList, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
            Check(library.FilesWorkspace.ActiveTab == originalTab, "host Ctrl+Shift+Tab selects the previous Files tab");
            SendKey(window, files.ResultsList, Key.Tab, ModifierKeys.Control);
            Check(library.FilesWorkspace.ActiveTab != originalTab, "host Ctrl+Tab selects the next Files tab");
            SendKey(window, library.Files.ResultsList, Key.W, ModifierKeys.Control);
            Check(library.FilesWorkspace.Tabs.Count == tabsBefore && library.FilesWorkspace.ActiveTab == originalTab,
                "host Ctrl+W closes only the active Files tab");
            File.WriteAllText(original, "owned");
            await files.NavigateTo(CaptureSettings.OutputFolder);
            files.ResultsList.SelectedItem = files.Navigation.Results.Single(entry => entry.FileName == "rename.txt");
            files.ResultsList.UpdateLayout();
            files.ResultsList.Focus();
            SendKey(window, files.ResultsList, Key.F2);
            var rename = (System.Windows.Controls.TextBox)files.FindName("RenameBox");
            Check(rename.IsVisible, "F2 reaches Files inline rename before album note action");
            rename.Text = "renamed.txt";
            SendKey(window, rename, Key.Enter);
            await files.CurrentCommand;
            Check(!File.Exists(original) && File.ReadAllText(Path.Combine(CaptureSettings.OutputFolder, "renamed.txt")) == "owned", "routed Enter commits Files rename");
            string navigated = Directory.CreateDirectory(Path.Combine(folder, "browsed")).FullName;
            await files.NavigateTo(navigated);
            library.Reload();
            SendKey(window, files.ResultsList, Key.F9);
            await library.FilesNavigation;
            Check(files.Navigation.CurrentFolder == navigated, "reload and repeated F9 preserve Files navigation");
            library.SetView("grid");
            library.SetView("files");
            await library.FilesNavigation;
            Check(files.Navigation.CurrentFolder == navigated, "returning from library preserves the active Files folder");
            await files.NavigateTo(CaptureSettings.OutputFolder);
            var search = (System.Windows.Controls.TextBox)library.FindName("SearchBox");
            library.SetView("grid");
            search.Text = "capture query";
            library.SetView("files");
            await library.FilesNavigation;
            search.Text = "renamed";
            SendKey(window, search, Key.Enter);
            Check(files.Navigation.FilterText == "renamed", "search popup filters Files instead of hidden captures");
            library.SetView("grid");
            Check(search.Text == "capture query", "capture search survives independent Files filter");
            Check(files.CanOpenInHost?.Invoke("sample.gif") == true && files.CanOpenInHost("sample.png")
                && !files.CanOpenInHost("sample.mp4") && !files.CanOpenInHost("sample.txt"), "host recognizes image extensions including GIF without treating video as an image");
            var pixels = Enumerable.Repeat((byte)255, 24 * 16 * 4).ToArray();
            var image = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 24 * 4);
            image.Freeze();
            string imagePath = Path.Combine(CaptureSettings.OutputFolder, "image.png");
            ImageOutput.Save(image, imagePath);
            long captureId = CaptureLibrary.AddNow(image, DateTime.Now, "region", "Independent capture", null);
            string editorPath = Path.Combine(folder, "independent-editor.png");
            ImageOutput.Save(image, editorPath);
            var dirty = new EditorDocument(image, editorPath);
            Check(App.Instance.SetDocument(dirty), "owned editor document opens");
            dirty.Add(new ShapeAnnotation(ShapeKind.Rectangle, new Point(1, 1), new Point(10, 10), Colors.Red, 1));
            SendKey(window, window.Editor, Key.F9);
            await library.FilesNavigation;
            files.SetFilter("");
            search.Text = "";
            await files.NavigateTo(CaptureSettings.OutputFolder);
            files.ResultsList.SelectedItem = files.Navigation.Results.Single(entry => entry.FileName == "renamed.txt");
            files.ResultsList.Focus();
            SendKey(window, files.ResultsList, Key.Delete);
            await files.CurrentCommand;
            Check(!File.Exists(Path.Combine(CaptureSettings.OutputFolder, "renamed.txt"))
                && CaptureLibrary.Items().Any(item => item.Id == captureId) && ReferenceEquals(App.Instance.Document, dirty)
                && dirty.Dirty && dirty.State.Items.Count == 1, "Files Delete recycles file and preserves independent capture and dirty editor");
            IDataObject? clipboard = null;
            int writes = 0, reads = 0;
            files.WriteClipboard = data => { clipboard = data; writes++; };
            files.ReadClipboard = () => { reads++; return clipboard; };
            files.ResultsList.SelectedItem = files.Navigation.Results.Single(entry => entry.FileName == "image.png");
            files.ResultsList.Focus();
            SendKey(window, files.ResultsList, Key.C, ModifierKeys.Control);
            Check(writes == 1 && clipboard?.GetData(DataFormats.FileDrop) is string[] copied && copied.Single() == imagePath, "host Ctrl+C routes actual file data through owned clipboard boundary");
            SendKey(window, files.ResultsList, Key.X, ModifierKeys.Control);
            Check(writes == 2, "host Ctrl+X reaches Files clipboard boundary");
            string pasteFolder = Directory.CreateDirectory(Path.Combine(folder, "paste")).FullName;
            await files.NavigateTo(pasteFolder);
            SendKey(window, files.ResultsList, Key.V, ModifierKeys.Control);
            await files.CurrentCommand;
            // The second read checks the clipboard still holds the move list before clearing it.
            Check(reads == 2 && File.Exists(Path.Combine(pasteFolder, "image.png")) && !File.Exists(imagePath)
                && clipboard?.GetDataPresent(DataFormats.FileDrop) != true, "host Ctrl+V moves actual file through FileOps and clears the cut list");
            await files.NavigateTo(pasteFolder);
            files.ResultsList.Focus();
            SendKey(window, files.ResultsList, Key.A, ModifierKeys.Control);
            Check(files.SelectedPaths.Count == 1, "host Ctrl+A selects Files entries");
            SendKey(window, files.ResultsList, Key.Back);
            await files.CurrentCommand;
            Check(files.Navigation.CurrentFolder == folder && dirty.State.Items.Count == 1, "host Backspace navigates Files and preserves editor mark");
            files.BeginEditAddress();
            var address = (TextBox)files.FindName("AddressBox");
            foreach (var key in new[] { Key.C, Key.X, Key.V, Key.A })
            {
                var down = new KeyEventArgs(new TestKeyboard(ModifierKeys.Control), PresentationSource.FromVisual(window)!, 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                address.RaiseEvent(down);
                // writes 3 and reads 2 include the cut-list clear after the Ctrl+V move above.
                Check(!down.Handled && writes == 3 && reads == 2, "address text retains preview ownership for Ctrl+" + key);
            }
            address.Text = "abc";
            address.CaretIndex = 3;
            var back = new KeyEventArgs(new TestKeyboard(ModifierKeys.None), PresentationSource.FromVisual(window)!, 0, Key.Back)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            address.RaiseEvent(back);
            Check(!back.Handled && files.Navigation.CurrentFolder == folder, "address Backspace stays with text field");
            files.ResultsList.Focus();
            foreach (var key in new[] { Key.H, Key.G, Key.I, Key.M, Key.Z, Key.Y, Key.D, Key.S, Key.P })
                SendKey(window, files.ResultsList, key, ModifierKeys.Control);
            Check(ReferenceEquals(App.Instance.Document, dirty) && dirty.Dirty && dirty.State.Items.Count == 1
                && dirty.PixelWidth == 24 && dirty.PixelHeight == 16, "Files control chords preserve hidden editor document and marks");
            int confirmations = 0;
            App.Instance.PresentDocumentReplacement = dialog => { confirmations++; dialog.Close(); return false; };
            string movedImage = Path.Combine(pasteFolder, "image.png");
            bool canceledHandled = await files.Browser.ActivateEntry(new FileEntry { FilePath = movedImage, FileName = "image.png" });
            report.AppendLine($"Cancellation boundary: handled={canceledHandled}, prompts={confirmations}, same={ReferenceEquals(App.Instance.Document, dirty)}, dirty={dirty.Dirty}, browserError={files.Navigation.Error}, status={((TextBlock)window.FindName("StatusText")).Text}");
            Check(canceledHandled
                && confirmations == 1 && ReferenceEquals(App.Instance.Document, dirty) && dirty.Dirty, "canceled recognized image remains handled and preserves dirty document");
            string broken = Path.Combine(folder, "broken.png");
            File.WriteAllText(broken, "invalid image");
            Check(await files.Browser.ActivateEntry(new FileEntry { FilePath = broken, FileName = "broken.png" })
                && ReferenceEquals(App.Instance.Document, dirty), "failed recognized image remains handled without shell fallback");
            dirty.MarkSaved();
            Check(await files.Browser.ActivateEntry(new FileEntry { FilePath = movedImage, FileName = "image.png" })
                && App.Instance.Document?.FilePath == movedImage, "image activation uses existing editor document path");
            var opened = App.Instance.Document!;
            opened.Add(new ShapeAnnotation(ShapeKind.Rectangle, new Point(2, 2), new Point(9, 9), Colors.Blue, 1));
            SendKey(window, window.Editor, Key.F7);
            search.Clear();
            library.Reload();
            await library.ListingTask;
            window.UpdateLayout();
            var tile = Children<Button>(library).Single(button => button.IsVisible && button.Tag is CaptureItem item && item.Id == captureId);
            tile.Focus();
            int notes = 0;
            library.PresentPrompt = dialog =>
            {
                notes++;
                ((TextBox)dialog.FindName("InputText")).Text = "Integration note";
                dialog.Close();
                return true;
            };
            SendKey(window, tile, Key.F2);
            Check(notes == 1 && CaptureLibrary.Items().Single(item => item.Id == captureId).Note == "Integration note"
                && opened.Dirty && File.Exists(movedImage), "Grid F2 edits the capture note and preserves independent file and dirty editor");
            library.PresentConfirmation = dialog => { dialog.Close(); return true; };
            SendKey(window, tile, Key.Delete);
            Check(CaptureLibrary.Items().All(item => item.Id != captureId) && File.Exists(movedImage)
                && opened.Dirty && opened.State.Items.Count == 1, "Grid Delete removes focused capture and preserves file and dirty editor");
            long flowId = CaptureLibrary.AddNow(image, DateTime.Now, "region", "Flow capture", null);
            library.SetView("shelves");
            await library.ListingTask;
            window.UpdateLayout();
            // The shelf carousel holding the new capture, the one a user would arrow to and delete from.
            var flow = Children<Controls.CoverFlow>(library)
                .FirstOrDefault(shelf => shelf.IsVisible && shelf.Items.Any(item => item.Id == flowId));
            Check(flow != null, "Shelves show the new capture in a carousel");
            flow!.Select(flowId);
            flow.Focus();
            SendKey(window, flow, Key.Delete);
            Check(CaptureLibrary.Items().All(item => item.Id != flowId) && File.Exists(movedImage)
                && ReferenceEquals(App.Instance.Document, opened) && opened.Dirty, "Shelf Delete removes selected capture and preserves file and dirty editor");
            SendKey(window, library, Key.F9);
            await library.FilesNavigation;
            var contentMenu = library.ContextMenu!;
            contentMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Check(SelfTestCards.TextRows(contentMenu) == null && SelfTestCards.Tiles(contentMenu)
                .Count(tile => System.Windows.Automation.AutomationProperties.GetAcceleratorKey(tile) is "F5" or "F7" or "F8" or "F9") == 4,
                "content card exposes all four library views as tiles without opening a popup");
            Check(!footer.Children.OfType<RadioButton>().Any(button => button.GroupName == "FooterLibraryGroup"), "the footer has no separate grouping buttons");
            string fallback = Directory.CreateDirectory(Path.Combine(folder, "fallback")).FullName;
            CaptureSettings.OutputFolder = fallback;
            var sidebar = (CollectionStrip)((KillerFamily.Layout.DockHost)window.FindName("NavigationDock")).Pane!;
            FolderCaptures.Enabled = true;
            await FolderCaptures.RefreshScreenshotsAsync();
            await Task.Delay(100);
            var sourceMenu = sidebar.SourceMenu(sidebar);
            sourceMenu.Items.OfType<MenuItem>().Single(item => Convert.ToString(item.Header) == LocaleManager.Loc("Str_Strip_All"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await library.FilesNavigation;
            sourceMenu = sidebar.SourceMenu(sidebar);
            sourceMenu.Items.OfType<MenuItem>().Single(item => Convert.ToString(item.Header) == FolderCaptures.Name)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (int wait = 0; wait < 200 && files.Navigation.CurrentFolder != Path.Combine(folder, "screenshots"); wait++) await Task.Delay(10);
            Check(files.Navigation.CurrentFolder == Path.Combine(folder, "screenshots"), "changed sidebar folder source hands off its filesystem path");
            sourceMenu = sidebar.SourceMenu(sidebar);
            sourceMenu.Items.OfType<MenuItem>().Single(item => Convert.ToString(item.Header) == LocaleManager.Loc("Str_Strip_All"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await library.FilesNavigation;
            // Only the footer changes the view, so an album collection keeps Files in front on the capture folder.
            Check(library.FilesActive && files.Navigation.CurrentFolder == CaptureSettings.OutputFolder,
                "non-folder sidebar source keeps Files in front on the capture folder");
            _ = library.FilesWorkspace.SelectTab(library.FilesWorkspace.Tabs.Last(tab => !tab.IsCollection));
            for (int wait = 0; wait < 200 && !library.FilesActive; wait++) await Task.Delay(10);
            library.FilesWorkspace.SaveSession();
            var startupView = new LibraryView();
            Check(startupView.FilesActive, "persisted Files view survives constructor startup whitelist");
            Check(((TextBlock)startupView.FindName("SearchHint")).Text == LocaleManager.Loc("Str_TT_FilterResults"),
                "startup Files search describes filtering files");
            startupView.FilesWorkspace.Dispose();
            await files.NavigateTo(pasteFolder);
            files.SetFilter("image");
            Check(((TextBlock)library.FindName("CountText")).Text == "0 folder(s), 1 file(s)", "Files count describes visible filesystem entries");
            files.SetFilter("");
            var dropped = new DataObject(DataFormats.FileDrop, new[] { movedImage });
            int albumCount = CaptureLibrary.Items().Count;
            Check(!window.DropImages(dropped) && CaptureLibrary.Items().Count == albumCount, "Files rejects parent image-import command without adding captures");
            var droppedResult = await files.DropFiles(dropped, fallback, DragDropEffects.Copy, ModifierKeys.Control);
            Check(droppedResult?.SucceededTargets.Count == 1 && File.Exists(Path.Combine(fallback, "image.png"))
                && File.Exists(movedImage) && CaptureLibrary.Items().Count == albumCount && ReferenceEquals(App.Instance.Document, opened),
                "Files drop copies actual image without album import or editor replacement");
            using var listingStarted = new ManualResetEventSlim();
            using var listingRelease = new ManualResetEventSlim();
            var originalReader = library.ListingReader;
            library.ListingReader = (source, query) =>
            {
                listingStarted.Set();
                if (!listingRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned listing release timed out.");
                return originalReader(source, query);
            };
            library.SetView("grid");
            Task obsoleteListing = library.ListingTask;
            for (int wait = 0; wait < 200 && !listingStarted.IsSet; wait++) await Task.Delay(5);
            Check(listingStarted.IsSet, "owned obsolete capture listing reaches database read boundary");
            SendKey(window, library, Key.F9);
            listingRelease.Set();
            await obsoleteListing;
            library.ListingReader = originalReader;
            await Task.Delay(100);
            Check(files.IsVisible && !((FrameworkElement)library.FindName("RowsList")).IsVisible
                && !((FrameworkElement)library.FindName("FlowHost")).IsVisible,
                "late capture listing cannot replace Files presentation");
            CaptureLibrary.AddNow(image, DateTime.Now, "region", "Strip first", null);
            CaptureLibrary.AddNow(image, DateTime.Now.AddSeconds(1), "region", "Strip second", null);
            library.SetView("grid");
            await library.ListingTask;
            await Task.Delay(100);
            var photoStrip = (CollectionStrip)((KillerFamily.Layout.DockHost)window.FindName("StripDock")).Pane!;
            var stripFlow = Children<Controls.CoverFlow>(photoStrip).Single();
            stripFlow.SelectedIndex = stripFlow.SelectedIndex == 0 ? 1 : 0;
            SendKey(window, library, Key.F9);
            await Task.Delay(350);
            Check(library.FilesActive && library.IsVisible && ReferenceEquals(App.Instance.Document, opened) && opened.Dirty,
                "delayed photo-strip selection cannot reopen capture over Files");
            CaptureLibrary.SetPassword("owned-files-integration-password");
            CaptureLibrary.Close();
            Check(!CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword, "owned capture album is actually password locked");
            library.Reload();
            await files.NavigateTo(pasteFolder);
            Check(library.FilesActive && files.IsVisible && ((FrameworkElement)library.FindName("LockPanel")).Visibility == Visibility.Collapsed
                && files.Navigation.Results.Any(entry => entry.FileName == "image.png"), "Files browses owned folder while capture album is password locked");
            opened.MarkSaved();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            bool disposed = false;
            try { await files.NavigateTo(pasteFolder); }
            catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "closing host disposes its Files browser and watcher lifetime");
            exit = 0;
        }
        catch (Exception error) { report.AppendLine("FAIL: " + error); }
        finally
        {
            App.Instance.PresentDocumentReplacement = null;
            App.Instance.Document?.MarkSaved();
            window?.Close();
            report.AppendLine($"Assertions: {checks}; Exit: {exit}");
            File.WriteAllText(Path.Combine(folder, "files-integration-report.txt"), report.ToString());
            App.Current.Shutdown(exit);
        }
    }
}
