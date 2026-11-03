using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesInteraction
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--selftest-files-interaction" or "--selftest-files-interaction-commands-only")) return false;
        _ = Run(Path.GetFullPath(args[1]), args[0] == "--selftest-files-interaction-commands-only");
        return true;
    }

    private static System.Windows.Controls.TextBox? FindTextBox(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TextBox box && box.Visibility == Visibility.Visible) return box;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }

    private static async Task Run(string folder, bool commandsOnly)
    {
        var report = new StringBuilder();
        if (commandsOnly) report.AppendLine("Scope: commands-only. Native context query/resource lifetime: NOT SELECTED. Full phase C acceptance remains pending.");
        Window? window = null;
        FilesView? view = null;
        int exit = 1, checks = 0;
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new interaction fixture folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            Settings.Set("Files.Places", "");
            App.InitializeKit();
            string source = Directory.CreateDirectory(Path.Combine(folder, "source")).FullName;
            File.WriteAllText(Path.Combine(source, "alpha.txt"), "alpha");
            File.WriteAllText(Path.Combine(source, "bravo.txt"), "bravo");
            view = new FilesView();
            window = new Window { Content = view, Width = 900, Height = 600, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await view.NavigateTo(source);
            window.UpdateLayout();
            Check(await Dispatch(view, Key.A, ModifierKeys.Control, view.ResultsList)
                && view.ResultsList.SelectedItems.Count == 2, "Ctrl+A selects actual extended ListBox rows");
            IDataObject? clipboard = null;
            view.WriteClipboard = data => clipboard = data;
            view.ReadClipboard = () => clipboard;
            Check(await Dispatch(view, Key.C, ModifierKeys.Control, view.ResultsList)
                && clipboard?.GetData(DataFormats.FileDrop) is string[] copied && copied.Length == 2
                && copied.Contains(Path.Combine(source, "alpha.txt")), "Ctrl+C carries selected actual paths in WPF DataObject");
            Check(clipboard?.GetData("Preferred DropEffect") is MemoryStream effect
                && BitConverter.ToInt32(effect.ToArray()) == 1, "copy preferred effect stream round trips on net10");
            Check(clipboard is System.Runtime.InteropServices.ComTypes.IDataObject, "WPF clipboard data exposes native IDataObject on net10");
            if (clipboard is System.Runtime.InteropServices.ComTypes.IDataObject nativeClipboard)
            {
                var preferred = new System.Runtime.InteropServices.ComTypes.FORMATETC
                {
                    cfFormat = unchecked((short)DataFormats.GetDataFormat("Preferred DropEffect").Id),
                    dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                    lindex = -1, tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                };
                nativeClipboard.GetData(ref preferred, out var medium);
                try
                {
                    IntPtr bytes = GlobalLock(medium.unionmember);
                    try { Check(bytes != IntPtr.Zero && System.Runtime.InteropServices.Marshal.ReadInt32(bytes) == 1,
                        "native Preferred DropEffect HGLOBAL round trips exact copy DWORD"); }
                    finally { if (bytes != IntPtr.Zero) GlobalUnlock(medium.unionmember); }
                }
                finally { ReleaseStgMedium(ref medium); }
            }
            string sibling = Directory.CreateDirectory(Path.Combine(folder, "sibling")).FullName;
            await view.NavigateTo(sibling);
            Check(await Dispatch(view, Key.V, ModifierKeys.Control, view.ResultsList)
                && File.ReadAllText(Path.Combine(sibling, "alpha.txt")) == "alpha"
                && File.Exists(Path.Combine(source, "alpha.txt")), "Ctrl+V copies into owned sibling through real FileOps");
            await view.NavigateTo(source);
            var alpha = view.Navigation.Results.Single(entry => entry.FileName == "alpha.txt");
            view.ResultsList.SelectedItem = alpha;
            Check(await Dispatch(view, Key.X, ModifierKeys.Control, view.ResultsList)
                && clipboard?.GetData("Preferred DropEffect") is MemoryStream moving
                && BitConverter.ToInt32(moving.ToArray()) == 2, "Ctrl+X carries move preferred effect");
            string moved = Directory.CreateDirectory(Path.Combine(folder, "moved")).FullName;
            await view.NavigateTo(moved);
            await Dispatch(view, Key.V, ModifierKeys.Control, view.ResultsList);
            Check(File.ReadAllText(Path.Combine(moved, "alpha.txt")) == "alpha" && !File.Exists(alpha.FilePath),
                "cut paste moves actual file only after paste");
            await view.NavigateTo(source);
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(entry => entry.FileName == "bravo.txt");
            Check(await Dispatch(view, Key.F2, ModifierKeys.None, view.ResultsList)
                && FindTextBox(view) is { Visibility: Visibility.Visible } rename, "F2 opens an actual in-place rename TextBox");
            var renameBox = (System.Windows.Controls.TextBox)view.FindName("RenameBox");
            Check(renameBox.SelectionStart == 0 && renameBox.SelectedText == "bravo", "rename selects basename and preserves extension by default");
            renameBox.Text = "invalid/name.txt";
            RenameKey(Key.Enter);
            await view.CurrentCommand;
            Check(renameBox.Visibility == Visibility.Visible && view.CommandError != null
                && File.Exists(Path.Combine(source, "bravo.txt")), "invalid rename remains editable and preserves original");
            File.WriteAllText(Path.Combine(source, "occupied.txt"), "keep");
            renameBox.Text = "occupied.txt";
            RenameKey(Key.Enter);
            await view.CurrentCommand;
            Check(renameBox.Visibility == Visibility.Visible && File.ReadAllText(Path.Combine(source, "occupied.txt")) == "keep"
                && File.Exists(Path.Combine(source, "bravo.txt")), "collision rename preserves both files and remains editable");
            renameBox.Text = "renamed.txt";
            RenameKey(Key.Enter);
            await view.CurrentCommand;
            Check(File.ReadAllText(Path.Combine(source, "renamed.txt")) == "bravo" && renameBox.Visibility == Visibility.Collapsed,
                "Enter commits the in-place TextBox through FileOps");
            await WaitUntil(() => view.Navigation.Results.Any(entry => entry.FileName == "renamed.txt"));
            foreach (int mode in new[] { 0, 1, 2 })
            {
                view.SetView(mode);
                window.UpdateLayout();
                Check(await Dispatch(view, Key.F2, ModifierKeys.None, view.ResultsList) && renameBox.Visibility == Visibility.Visible,
                    $"rename anchors to realized filename in mode {mode}");
                renameBox.Text = "canceled.txt";
                RenameKey(Key.Escape);
                Check(renameBox.Visibility == Visibility.Collapsed && !File.Exists(Path.Combine(source, "canceled.txt")),
                    $"Escape cancels in-place rename in mode {mode}");
            }
            Check(!view.HandleFileKey(Key.C, ModifierKeys.Control, renameBox)
                && !view.HandleFileKey(Key.F5, ModifierKeys.None, view.ResultsList), "text fields and application view keys retain ownership");
            Check(await Dispatch(view, Key.Delete, ModifierKeys.None, view.ResultsList)
                && !File.Exists(Path.Combine(source, "renamed.txt")), "Delete recycles only the owned selected file");
            await view.NavigateTo(sibling);
            window.UpdateLayout();
            var first = (System.Windows.Controls.ListBoxItem)view.ResultsList.ItemContainerGenerator.ContainerFromIndex(0);
            var second = (System.Windows.Controls.ListBoxItem)view.ResultsList.ItemContainerGenerator.ContainerFromIndex(1);
            var firstBounds = first.TransformToAncestor(view.ResultsList).TransformBounds(new Rect(first.RenderSize));
            var secondBounds = second.TransformToAncestor(view.ResultsList).TransformBounds(new Rect(second.RenderSize));
            view.BeginPointer(new Point(0, 0), view.ResultsList, ModifierKeys.None);
            view.MovePointer(new Point(view.ResultsList.ActualWidth - 5, secondBounds.Bottom));
            view.EndPointer();
            Check(view.ResultsList.SelectedItems.Count == 2, "marquee selects actual realized extended ListBox rows");
            Check(!view.BeginPointer(firstBounds.TopLeft, first, ModifierKeys.Control)
                && view.ResultsList.SelectedItems.Count == 2, "item click retains native Ctrl and Shift ListBox ownership");
            view.ResultsList.SelectedItem = view.Navigation.Results.First();
            using (var transfer = view.CreateDragData())
            {
                Check(transfer != null, "drag command retains pre-click multiple selection snapshot");
                if (transfer != null)
                {
                    var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
                    {
                        cfFormat = 15, dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                        lindex = -1, tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                    };
                    transfer.Data.GetData(ref format, out var medium);
                    try
                    {
                        uint count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
                        var paths = new List<string>();
                        for (uint i = 0; i < count; i++)
                        {
                            var path = new StringBuilder(32768);
                            DragQueryFile(medium.unionmember, i, path, 32768);
                            paths.Add(path.ToString());
                        }
                        Check(paths.Count == 2 && paths.Contains(Path.Combine(sibling, "alpha.txt"))
                            && paths.Contains(Path.Combine(sibling, "bravo.txt")), "native drag HDROP carries exact pre-click selected paths");
                    }
                    finally { ReleaseStgMedium(ref medium); }
                }
            }
            var dropped = new DataObject();
            var dropPaths = new System.Collections.Specialized.StringCollection { Path.Combine(sibling, "alpha.txt") };
            dropped.SetFileDropList(dropPaths);
            string dropCopy = Directory.CreateDirectory(Path.Combine(folder, "drop-copy")).FullName;
            var dropResult = await view.DropFiles(dropped, dropCopy, DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.Control);
            Check(dropResult?.Succeeded == 1 && File.Exists(Path.Combine(dropCopy, "alpha.txt"))
                && File.Exists(Path.Combine(sibling, "alpha.txt")), "Ctrl drop copies through real FileOps");
            string dropMove = Directory.CreateDirectory(Path.Combine(sibling, "child-target")).FullName;
            dropResult = await view.DropFiles(dropped, dropMove, DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.Shift);
            Check(dropResult?.Succeeded == 1 && File.Exists(Path.Combine(dropMove, "alpha.txt"))
                && !File.Exists(Path.Combine(sibling, "alpha.txt")), "Shift drop moves and selects its actual destination");
            bool childFolderPreserved = view.Navigation.CurrentFolder == sibling;
            await view.NavigateTo(dropMove);
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(entry => entry.FileName == "alpha.txt");
            Check(FilesView.DropEffect([@"C:\\one.txt"], @"D:\\", DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.None) == DragDropEffects.Copy
                && FilesView.DropEffect([@"C:\\one.txt"], @"C:\\two", DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.None) == DragDropEffects.Move
                && FilesView.DropEffect([@"C:\\one.txt"], @"C:\\two", DragDropEffects.Copy, ModifierKeys.Shift) == DragDropEffects.None,
                "drop effect respects volume, modifiers and source allowed effects");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                dropResult = await view.DropFiles(dropped, dropCopy, DragDropEffects.Copy, ModifierKeys.Control, canceled.Token);
                Check(dropResult?.Canceled == true && File.Exists(Path.Combine(dropMove, "alpha.txt"))
                    && view.SelectedPaths.Single() == Path.Combine(dropMove, "alpha.txt"), "canceled drop retains files and selection");
            }
            Check(await Dispatch(view, Key.N, ModifierKeys.Control | ModifierKeys.Shift, view.ResultsList)
                && Directory.Exists(Path.Combine(dropMove, "New folder")) && renameBox.Visibility == Visibility.Visible,
                "Ctrl+Shift+N creates actual folder and starts in-place rename");
            RenameKey(Key.Escape);
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(entry => entry.FileName == "alpha.txt");
            view.CanOpenInHost = path => Path.GetExtension(path) == ".txt";
            var menu = view.BuildFilesMenu(view.ResultsList.SelectedItem as FileEntry);
            string[] menuTags = menu.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => Convert.ToString(item.Tag) ?? "").ToArray();
            Check(new[] { "open", "host", "explorer", "cut", "copy", "paste", "rename", "newfolder", "delete", "shell", "properties", "refresh" }
                .All(menuTags.Contains), "context menu exposes only the approved file command surface");
            if (!commandsOnly)
                using (var shell = ShellContextMenu.Acquire(window, [Path.Combine(dropMove, "alpha.txt")]))
                    Check(shell is { CommandCount: > 0 }, "native shell context query acquires real commands without opening a popup");
            int foldersBefore = Directory.GetDirectories(dropMove).Length;
            var firstFolder = view.NewFolder();
            bool folderBusy = view.IsFileOperationBusy;
            var repeatedFolder = view.NewFolder();
            await Task.WhenAll(firstFolder, repeatedFolder);
            Check(folderBusy && repeatedFolder.Result == null && Directory.GetDirectories(dropMove).Length == foldersBefore + 1,
                "new-folder owns mutation lease before await and rejects repeated dispatch");
            view.CancelRename();
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(entry => entry.FileName == "alpha.txt");
            view.BeginRename();
            renameBox.Text = "blocked.txt";
            string large = Path.Combine(folder, "copy-source.bin");
            using (var output = File.Create(large)) output.SetLength(64 * 1024 * 1024);
            var largeData = new DataObject();
            largeData.SetFileDropList(new System.Collections.Specialized.StringCollection { large });
            clipboard = largeData;
            Check(view.HandleFileKey(Key.V, ModifierKeys.Control, view.ResultsList) && view.IsFileOperationBusy,
                "real copy claims mutation lease synchronously during key dispatch");
            Task copyCommand = view.CurrentCommand;
            RenameKey(Key.Enter);
            var busyMenu = view.BuildFilesMenu(view.ResultsList.SelectedItem as FileEntry);
            var newFolderItem = busyMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => Equals(item.Tag, "newfolder"));
            int busyFolderCount = Directory.GetDirectories(dropMove).Length;
            newFolderItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            bool busyTaskRetained = ReferenceEquals(copyCommand, view.CurrentCommand);
            bool renameDenied = !view.BeginRename();
            await view.NavigateTo(sibling);
            await copyCommand;
            Check(busyTaskRetained && renameDenied && !File.Exists(Path.Combine(dropMove, "blocked.txt"))
                && File.Exists(Path.Combine(dropMove, "alpha.txt")) && Directory.GetDirectories(dropMove).Length == busyFolderCount
                && new FileInfo(Path.Combine(dropMove, "copy-source.bin")).Length == 64 * 1024 * 1024,
                "copy lease blocks menu mutations and Enter rename while retaining completion and files");
            Check(view.Navigation.CurrentFolder == sibling, "navigation remains responsive and independent of file mutation lease");
            view.CancelRename();
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(entry => entry.FileName == "bravo.txt");
            var firstRecycle = view.RecycleSelection();
            bool recycleBusy = view.IsFileOperationBusy;
            var repeatedRecycle = view.RecycleSelection();
            await Task.WhenAll(firstRecycle, repeatedRecycle);
            Check(recycleBusy && repeatedRecycle.Result == null && !File.Exists(Path.Combine(sibling, "bravo.txt")),
                "recycle owns mutation lease and rejects duplicate Delete work");
            string backgroundMove = Path.Combine(folder, "background-move.txt");
            File.WriteAllText(backgroundMove, "moving");
            var backgroundData = new DataObject();
            backgroundData.SetFileDropList(new System.Collections.Specialized.StringCollection { backgroundMove });
            var backgroundWork = view.DropFiles(backgroundData, dropMove, DragDropEffects.Move, ModifierKeys.Shift);
            await view.NavigateTo(source);
            await backgroundWork;
            bool laterNavigationPreserved = view.Navigation.CurrentFolder == source && File.Exists(Path.Combine(dropMove, "background-move.txt"));
            await view.NavigateTo(source);
            var backgroundFolder = view.NewFolder();
            await view.NavigateTo(dropMove);
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(entry => entry.FileName == "alpha.txt");
            window.UpdateLayout();
            await backgroundFolder;
            bool unrelatedRenameAvoided = renameBox.Visibility != Visibility.Visible;
            view.CancelRename();
            report.AppendLine($"Navigation ownership: child={childFolderPreserved}, later={laterNavigationPreserved}, rename={unrelatedRenameAvoided}");
            Check(childFolderPreserved && laterNavigationPreserved && unrelatedRenameAvoided,
                "child-target move, later navigation and new-folder rename retain view ownership");
            string collisionFolder = Directory.CreateDirectory(Path.Combine(folder, "collision-probe-A")).FullName;
            Directory.CreateDirectory(Path.Combine(collisionFolder, "New folder"));
            for (int i = 2; i <= 5000; i++) Directory.CreateDirectory(Path.Combine(collisionFolder, $"New folder ({i})"));
            string independent = Directory.CreateDirectory(Path.Combine(folder, "collision-probe-B")).FullName;
            File.WriteAllText(Path.Combine(independent, "independent.txt"), "independent");
            await view.NavigateTo(collisionFolder);
            var collisionCreation = view.NewFolder();
            await view.NavigateTo(independent);
            view.ResultsList.SelectedItem = view.Navigation.Results.Single();
            window.UpdateLayout();
            bool collisionCreationPending = !collisionCreation.IsCompleted;
            await collisionCreation;
            if (collisionCreationPending)
                Check(view.Navigation.CurrentFolder == independent && renameBox.Visibility != Visibility.Visible,
                    "5000-collision NewFolder completion does not rename an independent later row");
            else report.AppendLine("5000-collision NewFolder navigation probe inconclusive: creation already completed at B readiness.");
            view.CancelRename();
            if (!commandsOnly)
            {
                string[] nativePaths = [Path.Combine(independent, "independent.txt")];
                using (var warmup = ShellContextMenu.Acquire(window, nativePaths))
                    Check(warmup is { CommandCount: > 0 }, "same-target native context query warm-up succeeds");
                await DrainNativeIdle();
                uint idleBefore = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
                var idleDuration = System.Diagnostics.Stopwatch.StartNew();
                await DrainNativeIdle();
                uint idleAfter = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
                report.AppendLine($"Native idle control: before={idleBefore}, after={idleAfter}, durationMs={idleDuration.ElapsedMilliseconds}");
                uint previousObjects = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
                bool stableMenus = true, acquiredAll = true;
                for (int batch = 0; batch < 3; batch++)
                {
                    int acquiredCount = 0;
                    uint beforeObjects = previousObjects;
                    var batchDuration = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 10; i++)
                    {
                        using var lease = ShellContextMenu.Acquire(window, nativePaths);
                        if (lease is { CommandCount: > 0 }) acquiredCount++;
                    }
                    uint immediateObjects = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
                    await DrainNativeIdle();
                    previousObjects = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
                    acquiredAll &= acquiredCount == 10;
                    stableMenus &= previousObjects <= beforeObjects;
                    report.AppendLine($"Native lease batch {batch}: acquired={acquiredCount}/10, USER before={beforeObjects}, immediate={immediateObjects}, settled={previousObjects}, durationMs={batchDuration.ElapsedMilliseconds}");
                }
                Check(acquiredAll && stableMenus, "repeated same-target native menu acquire/dispose retains no additional USER objects");
            }
            view.ResultsList.SelectedItem = view.Navigation.Results.Single();
            int hostCalls = 0;
            view.Browser.OpenInEditor = _ => { hostCalls++; return Task.FromResult(false); };
            var hostMenu = view.BuildFilesMenu(view.ResultsList.SelectedItem as FileEntry);
            var hostItem = hostMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => Equals(item.Tag, "host"));
            Check(Convert.ToString(hostItem.Header) == "Open in KillerShot", "explicit host menu uses approved localized app wording");
            hostItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            await view.CurrentCommand;
            Check(hostCalls == 1 && File.Exists(Path.Combine(independent, "independent.txt")),
                "canceled explicit host action reaches the host once and preserves its file");
            view.CanOpenInHost = _ => false;
            Check(!view.BuildFilesMenu(view.ResultsList.SelectedItem as FileEntry).Items.OfType<System.Windows.Controls.MenuItem>()
                .Any(item => Equals(item.Tag, "host")), "unsupported file hides explicit editor action");
            string[] beforeErrors = view.SelectedPaths.ToArray();
            view.WriteClipboard = _ => throw new System.Runtime.InteropServices.ExternalException("fixture clipboard busy");
            Check(!view.CopySelection() && view.CommandError != null && view.SelectedPaths.SequenceEqual(beforeErrors),
                "clipboard refusal reports error without losing selection");
            var commandStatus = (System.Windows.Controls.TextBlock)view.FindName("CommandStatus");
            Check(commandStatus.Text == "fixture clipboard busy" && StatusColor("DangerRed"),
                "copy refusal appears in actual status text with family error color");
            view.WriteClipboard = data => clipboard = data;
            Check(view.CopySelection() && commandStatus.Text.Length == 0 && StatusColor("MutedTextBrush"),
                "successful copy clears visible error and restores muted status color");
            view.ReadClipboard = () => throw new System.Runtime.InteropServices.ExternalException("fixture clipboard read busy");
            bool pasteRefusalOwned = view.HandleFileKey(Key.V, ModifierKeys.Control, view.ResultsList);
            await view.CurrentCommand;
            Check(pasteRefusalOwned && commandStatus.Text == "fixture clipboard read busy" && StatusColor("DangerRed")
                && view.SelectedPaths.SequenceEqual(beforeErrors), "paste read refusal appears in actual status text and preserves selection");
            view.ReadClipboard = () => clipboard;
            string pasteRecovery = Directory.CreateDirectory(Path.Combine(folder, "paste-recovery")).FullName;
            await view.NavigateTo(pasteRecovery);
            await Dispatch(view, Key.V, ModifierKeys.Control, view.ResultsList);
            Check(view.CommandError == null && StatusColor("MutedTextBrush")
                && File.ReadAllText(Path.Combine(pasteRecovery, "independent.txt")) == "independent",
                "successful paste clears error color after clipboard read refusal");
            await view.NavigateTo(independent);
            view.ResultsList.SelectedItem = view.Navigation.Results.Single();
            var missingData = new DataObject();
            missingData.SetFileDropList(new System.Collections.Specialized.StringCollection { Path.Combine(folder, "missing.txt") });
            var missingResult = await view.DropFiles(missingData, independent, DragDropEffects.Copy, ModifierKeys.Control);
            Check(missingResult is { Succeeded: 0, Failed.Count: 0, Canceled: false } && view.SelectedPaths.SequenceEqual(beforeErrors)
                && File.ReadAllText(Path.Combine(independent, "independent.txt")) == "independent",
                "vanished source is ignored without mutating existing file or selection");
            string lockedSource = Path.Combine(folder, "locked-source.txt");
            File.WriteAllText(lockedSource, "locked original");
            var lockedData = new DataObject();
            lockedData.SetFileDropList(new System.Collections.Specialized.StringCollection { lockedSource });
            using (var locked = File.Open(lockedSource, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failedCopy = await view.DropFiles(lockedData, independent, DragDropEffects.Copy, ModifierKeys.Control);
                Check(failedCopy is { Failed.Count: > 0 } && view.CommandError != null
                    && view.SelectedPaths.SequenceEqual(beforeErrors) && locked.Length == 15
                    && File.ReadAllText(Path.Combine(independent, "independent.txt")) == "independent",
                    "real sharing violation reports failure and retains existing files and selection");
            }
            Check(File.ReadAllText(lockedSource) == "locked original", "failed copy preserves source bytes after lock release");
            var driveNavigation = System.Diagnostics.Stopwatch.StartNew();
            bool thisPcNavigated = await view.NavigateTo(Browser.ThisPc);
            report.AppendLine($"This PC navigation: success={thisPcNavigated}, count={view.Navigation.Results.Count}, elapsedMs={driveNavigation.Elapsed.TotalMilliseconds:F1}, deadlineMs={view.Browser.NavigationTimeout.TotalMilliseconds:F1}");
            Check(thisPcNavigated && Browser.IsThisPc(view.Navigation.CurrentFolder)
                && driveNavigation.Elapsed < view.Browser.NavigationTimeout, "actual This PC navigation succeeds within unchanged deadline");
            var expectedDrives = Environment.GetLogicalDrives();
            var listedDrives = new HashSet<string>(view.Navigation.Results.Select(entry => entry.FilePath), StringComparer.OrdinalIgnoreCase);
            Check(listedDrives.SetEquals(expectedDrives) && view.Navigation.Results.All(entry => entry.IsDirectory && !string.IsNullOrWhiteSpace(entry.TypeName)),
                "all actual logical drive entries remain present with primed type names");
            var networkDrives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Network).ToArray();
            Check(networkDrives.All(drive => view.Navigation.Results.Single(entry => string.Equals(entry.FilePath, drive.Name, StringComparison.OrdinalIgnoreCase))
                .FileName == drive.Name.TrimEnd('\\')), "network drive labels use drive-letter fallback");
            report.AppendLine($"Network drive fallback controls selected={networkDrives.Length}");
            Check(!view.CanPaste && view.Navigation.Results.Count > 0, "This PC has no paste or new-folder target");
            bool protectedDrives = true;
            foreach (FileEntry drive in view.Navigation.Results)
            {
                view.ResultsList.SelectedItem = drive;
                protectedDrives &= !view.CanRename && !view.CanCut && !view.CanRecycle;
            }
            Check(protectedDrives, "actual drive-root rows disable cut rename and recycle without invoking them");
            exit = 0;

            void RenameKey(Key key)
            {
                renameBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(renameBox), 0, key)
                    { RoutedEvent = Keyboard.KeyDownEvent });
            }

            bool StatusColor(string key) => commandStatus.Foreground is System.Windows.Media.SolidColorBrush actual
                && view.TryFindResource(key) is System.Windows.Media.SolidColorBrush expected
                && actual.Color == expected.Color && actual.Opacity == expected.Opacity;
        }
        catch (Exception error) { report.AppendLine(error.ToString()); }
        finally
        {
            view?.Dispose();
            window?.Close();
            report.AppendLine($"Assertions: {checks}; Exit: {exit}");
            File.WriteAllText(Path.Combine(folder, "files-interaction-report.txt"), report.ToString());
            App.Instance.Shutdown(exit);
        }

        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
            report.AppendLine(message + " PASS");
        }
    }

    private static async Task<bool> Dispatch(FilesView view, Key key, ModifierKeys modifiers, DependencyObject source)
    {
        bool handled = view.HandleFileKey(key, modifiers, source);
        await view.CurrentCommand;
        return handled;
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        if (!ready()) throw new TimeoutException("Interaction did not reach the expected state within two seconds.");
    }

    private static async Task DrainNativeIdle()
    {
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(2));
    }


    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint length);
    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
