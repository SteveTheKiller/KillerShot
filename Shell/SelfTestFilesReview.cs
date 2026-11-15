using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShot.Files;

namespace KillerShot.Shell;

/// <summary>Regressions for the Files review findings. Run through --selftest-files-safety.</summary>
internal static class SelfTestFilesReview
{
    internal static readonly string[] Scenarios =
    [
        "review-delete-readonly", "review-shell-mixed", "review-recycle-partial", "review-partial-status",
        "review-cut-clipboard", "review-drop-effects", "review-watch-unreadable", "review-column-save",
        "review-grips", "review-update-in-place", "review-icon-cap", "review-drop-empty", "review-skip",
        "review-new-folder",
    ];

    internal static Task Run(string scenario, string folder, Action<bool, string> check, StringBuilder report) => scenario switch
    {
        "review-delete-readonly" => DeleteReadOnly(folder, check),
        "review-shell-mixed" => ShellMixed(folder, check),
        "review-recycle-partial" => RecyclePartial(folder, check, report),
        "review-partial-status" => PartialStatus(folder, check, report),
        "review-cut-clipboard" => CutClipboard(folder, check),
        "review-drop-effects" => DropEffects(folder, check, report),
        "review-watch-unreadable" => WatchUnreadable(folder, check),
        "review-column-save" => ColumnSave(check),
        "review-grips" => Grips(check),
        "review-update-in-place" => UpdateInPlace(folder, check, report),
        "review-icon-cap" => IconCap(folder, check, report),
        "review-drop-empty" => DropEmpty(check),
        "review-skip" => Skip(check),
        "review-new-folder" => NewFolder(folder, check),
        _ => throw new ArgumentException("Unknown review scenario: " + scenario),
    };

    private static async Task Wait(Func<bool> condition, double seconds = 3)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(20);
    }

    // FI-001
    private static Task DeleteReadOnly(string folder, Action<bool, string> check)
    {
        string tree = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        string sub = Directory.CreateDirectory(Path.Combine(tree, "sub")).FullName;
        string inner = Path.Combine(tree, "camera.jpg"), deeper = Path.Combine(sub, "deeper.jpg"), single = Path.Combine(folder, "single.txt");
        foreach (string file in new[] { inner, deeper, single })
        {
            File.WriteAllText(file, "read-only bytes");
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }
        File.SetAttributes(sub, File.GetAttributes(sub) | FileAttributes.ReadOnly);
        var result = FileOps.Delete([tree, single], _ => { }, CancellationToken.None);
        bool treeGone = !Directory.Exists(tree), singleGone = !File.Exists(single);
        if (!treeGone) foreach (string left in Directory.GetFileSystemEntries(tree, "*", SearchOption.AllDirectories))
                File.SetAttributes(left, File.GetAttributes(left) & ~FileAttributes.ReadOnly);
        if (!singleGone) File.SetAttributes(single, FileAttributes.Normal);
        check(treeGone && singleGone && result is { Succeeded: 2, Failed.Count: 0 },
            $"permanent delete removes read-only files and folders (treeGone={treeGone}, singleGone={singleGone}, succeeded={result.Succeeded}, failed={result.Failed.Count})");
        return Task.CompletedTask;
    }

    // FI-002
    private static Task ShellMixed(string folder, Action<bool, string> check)
    {
        string one = Directory.CreateDirectory(Path.Combine(folder, "one")).FullName;
        string two = Directory.CreateDirectory(Path.Combine(folder, "two")).FullName;
        string a = Path.Combine(one, "a.txt"), b = Path.Combine(two, "b.txt"), c = Path.Combine(one, "c.txt");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b"); File.WriteAllText(c, "c");
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            using (var same = ShellContextMenu.Acquire(window, [a, c]))
                check(same != null, "the shell menu still opens for items from one folder");
            using (var mixed = ShellContextMenu.Acquire(window, [a, b]))
                check(mixed == null, "the shell menu refuses items from two folders instead of dropping some");
            using (var missing = ShellContextMenu.Acquire(window, [a, Path.Combine(one, "gone.txt")]))
                check(missing == null, "the shell menu refuses a selection when one item cannot be resolved");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    // FI-003
    private static Task RecyclePartial(string folder, Action<bool, string> check, StringBuilder report)
    {
        string loose = Path.Combine(folder, "ks-selftest-recycle-loose.txt"), held = Path.Combine(folder, "ks-selftest-recycle-held.txt");
        File.WriteAllText(loose, "loose");
        File.WriteAllText(held, "held");
        FileOpResult result;
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = FileOps.Recycle([loose, held], silent: true);
        string[] remaining = new[] { loose, held }.Where(File.Exists).ToArray();
        report.AppendLine($"Recycle: succeeded={result.Succeeded}, failed=[{string.Join("|", result.Failed.Select(f => Path.GetFileName(f.Path)))}], remaining=[{string.Join("|", remaining.Select(Path.GetFileName))}]");
        check(result.Succeeded == 2 - remaining.Length
            && result.Failed.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(remaining.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "a partly refused recycle counts what actually left the disk and names what stayed");
        return Task.CompletedTask;
    }

    // FI-005
    private static async Task PartialStatus(string folder, Action<bool, string> check, StringBuilder report)
    {
        string source = Directory.CreateDirectory(Path.Combine(folder, "src")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(folder, "dst")).FullName;
        string ok = Path.Combine(source, "ok.txt"), held = Path.Combine(source, "held.txt");
        File.WriteAllText(ok, "ok"); File.WriteAllText(held, "held");
        using var files = new FilesView();
        check(await files.NavigateTo(target), "partial status fixture opens");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { ok, held });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        files.ReadClipboard = () => data;
        FileOpResult? result;
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await files.PasteFiles();
        string partial = string.Format(Convert.ToString(files.TryFindResource("Str_Status_OpPartial")) ?? "{0} {1}", 1, 1);
        report.AppendLine("Partial status text: " + files.CommandStatus.Text);
        check(result is { Succeeded: 1, Failed.Count: 1 } && files.CommandStatus.Text.StartsWith(partial, StringComparison.Ordinal),
            $"a copy where one file fails reports the partial result (status='{files.CommandStatus.Text}')");
    }

    // FI-006
    private static async Task CutClipboard(string folder, Action<bool, string> check)
    {
        string source = Directory.CreateDirectory(Path.Combine(folder, "src")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(folder, "dst")).FullName;
        string cut = Path.Combine(source, "cut.txt"), copied = Path.Combine(source, "copied.txt");
        File.WriteAllText(cut, "cut"); File.WriteAllText(copied, "copied");
        using var files = new FilesView();
        check(await files.NavigateTo(target), "cut clipboard fixture opens");
        IDataObject? clipboard = null;
        files.ReadClipboard = () => clipboard;
        files.WriteClipboard = data => clipboard = data;
        DataObject Clip(string path, int effect)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(effect)));
            return data;
        }
        clipboard = Clip(copied, 1);
        var copy = await files.PasteFiles();
        check(copy is { Succeeded: 1 } && clipboard?.GetDataPresent(DataFormats.FileDrop) == true,
            "a copy-paste leaves the copied list on the clipboard");
        clipboard = Clip(cut, 2);
        var move = await files.PasteFiles();
        check(move is { Succeeded: 1 } && File.Exists(Path.Combine(target, "cut.txt")) && !File.Exists(cut),
            "the cut file moved");
        check(clipboard?.GetDataPresent(DataFormats.FileDrop) != true,
            "a finished cut-paste clears the move list from the clipboard");
    }

    // FR-001
    private static async Task DropEffects(string folder, Action<bool, string> check, StringBuilder report)
    {
        string source = Directory.CreateDirectory(Path.Combine(folder, "src")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(folder, "dst")).FullName;
        string moved = Path.Combine(source, "moved.txt"), copied = Path.Combine(source, "copied.txt");
        File.WriteAllText(moved, "moved"); File.WriteAllText(copied, "copied");
        using var files = new FilesView();
        check(await files.NavigateTo(target), "drop effect fixture opens");
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 5);
        async Task<DragDropEffects> Drop(IDataObject data, DragDropKeyStates keys)
        {
            var args = (DragEventArgs)ctor.Invoke([data, keys, DragDropEffects.Copy | DragDropEffects.Move, files.ResultsList, new Point()]);
            args.RoutedEvent = DragDrop.DropEvent;
            files.ResultsList.RaiseEvent(args);
            await files.CurrentCommand;
            return args.Effects;
        }
        var moveData = new DataObject();
        moveData.SetData(DataFormats.FileDrop, new[] { moved });
        var moveEffect = await Drop(moveData, DragDropKeyStates.None);
        report.AppendLine("Move drop answered " + moveEffect);
        check(File.Exists(Path.Combine(target, "moved.txt")) && !File.Exists(moved), "the plain same-volume drop moved the file");
        check(moveEffect == DragDropEffects.None, $"a move this view performed answers None so the source keeps its hands off (answered {moveEffect})");
        check(moveData.GetData("Performed DropEffect") is MemoryStream performed && BitConverter.ToInt32(performed.ToArray()) == 0,
            "an optimized move records Performed DropEffect as DROPEFFECT_NONE");
        var selfData = new DataObject();
        selfData.SetData(DataFormats.FileDrop, new[] { target });
        var selfEffect = await Drop(selfData, DragDropKeyStates.ControlKey);
        check(selfEffect == DragDropEffects.None && files.CommandError != null, $"a drop into itself answers None (answered {selfEffect})");
        var copyData = new DataObject();
        copyData.SetData(DataFormats.FileDrop, new[] { copied });
        var copyEffect = await Drop(copyData, DragDropKeyStates.ControlKey);
        check(copyEffect == DragDropEffects.Copy && File.Exists(copied) && File.Exists(Path.Combine(target, "copied.txt")),
            $"a Ctrl drop copies and answers Copy (answered {copyEffect})");
        var textData = new DataObject();
        textData.SetData(DataFormats.UnicodeText, "not files");
        var rejectEffect = await Drop(textData, DragDropKeyStates.None);
        check(rejectEffect == DragDropEffects.None, $"a rejected drop answers None (answered {rejectEffect})");
    }

    // FR-002
    private static async Task WatchUnreadable(string folder, Action<bool, string> check)
    {
        using var files = new FilesView();
        check(await files.NavigateTo(folder), "watch fixture opens");
        int faults = 0;
        Browser.WatchReadFault = path => Path.GetFileName(path).Equals("locked.txt", StringComparison.OrdinalIgnoreCase)
            && Interlocked.Increment(ref faults) <= 2 ? new UnauthorizedAccessException("fixture refuses this read") : null;
        try
        {
            File.WriteAllText(Path.Combine(folder, "locked.txt"), "locked");
            File.WriteAllText(Path.Combine(folder, "plain.txt"), "plain");
            bool Has(string name) => files.Navigation.Results.Any(entry => entry.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            await Wait(() => Has("plain.txt"));
            check(Has("plain.txt"), "one unreadable file does not lose the rest of the watcher batch");
            await Wait(() => Has("locked.txt"), 5);
            check(Has("locked.txt") && faults >= 3, $"the unreadable file is read again and appears once it can be read (reads={faults})");
        }
        finally { Browser.WatchReadFault = null; }
    }

    // FR-003
    private static Task ColumnSave(Action<bool, string> check)
    {
        using var files = new FilesView();
        Settings.Set("Files.Column.1", "333");
        files.ViewState.AvailableWidth = 120;
        files.ViewState.AvailableWidth = 4000;
        check(Settings.Get("Files.Column.1") == "333",
            $"a resize that refits the columns never writes a width back (stored={Settings.Get("Files.Column.1")})");
        files.ViewState.SetColumnWidth(1, 300);
        check(Settings.Get("Files.Column.1") == "300", "a column the user sizes is still saved");
        files.ViewState.SetColumnWidth(1, 410, false);
        files.ViewState.SetColumnWidth(1, 420, false);
        check(Settings.Get("Files.Column.1") == "300", "a drag in progress does not write on every move");
        files.ViewState.CommitColumnWidth(1);
        check(Settings.Get("Files.Column.1") == "420", "the drag's last width is saved when it ends");
        return Task.CompletedTask;
    }

    // FR-005
    private static async Task Grips(Action<bool, string> check)
    {
        using var files = new FilesView();
        var window = new Window { Content = files, Width = 1200, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            files.SetView(2);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var header = (FrameworkElement)files.FindName("DetailsHeader");
            Border Grip(string tag) => Descendants(header).OfType<Border>().First(border => Equals(border.Tag, tag) && border.Cursor == Cursors.SizeWE);
            files.ViewState.SizeVisible = false;
            files.ViewState.ModifiedVisible = false;
            window.UpdateLayout();
            check(Grip("3").Visibility == Visibility.Collapsed && Grip("4").Visibility == Visibility.Collapsed,
                $"hidden Size and Modified columns hide their resize grips (size={Grip("3").Visibility}, modified={Grip("4").Visibility})");
            files.ViewState.SizeVisible = true;
            files.ViewState.ModifiedVisible = true;
            window.UpdateLayout();
            check(Grip("3").Visibility == Visibility.Visible && Grip("4").Visibility == Visibility.Visible,
                "shown Size and Modified columns show their resize grips");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    // FR-006
    private static async Task UpdateInPlace(string folder, Action<bool, string> check, StringBuilder report)
    {
        string path = Path.Combine(folder, "grow.txt");
        File.WriteAllText(path, "small");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-400));
        using var files = new FilesView();
        files.SetView(2);
        files.Navigation.GroupDates = true;
        files.SetSort(4, false);
        check(await files.NavigateTo(folder), "update fixture opens");
        var row = files.Navigation.Results.Single(entry => entry.FileName == "grow.txt");
        files.ResultsList.SelectedItems.Add(row);
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        long before = row.SizeBytes;
        File.AppendAllText(path, new string('x', 4096));
        FileEntry Current() => files.Navigation.Results.Single(entry => entry.FileName == "grow.txt");
        await Wait(() => Current().SizeBytes != before || row.SizeBytes != before);
        report.AppendLine($"Update: sameRow={ReferenceEquals(Current(), row)}, size={row.SizeBytes}, notified=[{string.Join(",", changed.Distinct())}]");
        check(ReferenceEquals(Current(), row) && row.SizeBytes > before, "a changed file updates its existing row rather than replacing it");
        check(files.ResultsList.SelectedItems.Contains(row), "the changed file stays selected");
        check(changed.Contains(nameof(FileEntry.SizeLabel)) && changed.Contains(nameof(FileEntry.ModifiedLabel)),
            "the row announces its new size and date");
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(files.Navigation.Results);
        var group = view.Groups?.OfType<System.Windows.Data.CollectionViewGroup>().FirstOrDefault(g => g.Items.Contains(row));
        report.AppendLine($"Update group: viewGroup={group?.Name}, rowGroup={row.ModifiedGroup}, groups={view.Groups?.Count}");
        check(group != null && Equals(group.Name, row.ModifiedGroup), "the changed file moves to its new date group");
    }

    // FR-008
    private static async Task IconCap(string folder, Action<bool, string> check, StringBuilder report)
    {
        for (int i = 0; i < 300; i++)
        {
            string path = Path.Combine(folder, $"app{i:000}.exe");
            File.WriteAllBytes(path, []);
            await IconCache.ForAsync(path, 32);
        }
        report.AppendLine("Per-file icons held: " + IconCache.PerFileIconCount);
        check(IconCache.PerFileIconCount <= 256, $"per-file icons are capped (held {IconCache.PerFileIconCount})");
    }

    // FR-009
    private static Task DropEmpty(Action<bool, string> check)
    {
        DragDropEffects effect;
        try { effect = FilesView.DropEffect([@"C:\fixture\a.txt"], "", DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.None); }
        catch (Exception error) { check(false, "an empty drop target answers None instead of throwing " + error.GetType().Name); return Task.CompletedTask; }
        check(effect == DragDropEffects.None, $"an empty drop target answers None (answered {effect})");
        return Task.CompletedTask;
    }

    // FR-010
    private static Task Skip(Action<bool, string> check)
    {
        var formats = new System.Runtime.InteropServices.ComTypes.FORMATETC[2];
        var enumerator = new FormatEtcEnumerator(formats);
        check(enumerator.Skip(1) == 0, "skipping inside the list succeeds");
        int past = enumerator.Skip(5);
        var buffer = new System.Runtime.InteropServices.ComTypes.FORMATETC[1];
        int[] fetched = [9];
        int next = enumerator.Next(1, buffer, fetched);
        check(past == 1 && next == 1 && fetched[0] == 0, $"skipping past the end returns S_FALSE and stops at the end (skip={past}, next={next}, fetched={fetched[0]})");
        return Task.CompletedTask;
    }

    // FI-009 (race not reproducible; numbering behavior kept)
    private static Task NewFolder(string folder, Action<bool, string> check)
    {
        string? first = FileOps.NewFolder(folder, "New folder");
        string? second = FileOps.NewFolder(folder, "New folder");
        File.WriteAllText(Path.Combine(folder, "Taken"), "file");
        string? third = FileOps.NewFolder(folder, "Taken");
        check(first == Path.Combine(folder, "New folder") && second == Path.Combine(folder, "New folder (2)")
            && third == Path.Combine(folder, "Taken (2)") && Directory.Exists(third!),
            $"new folders number past taken names (first={first}, second={second}, third={third})");
        string deep = Directory.CreateDirectory(Path.Combine(folder, string.Join(Path.DirectorySeparatorChar,
            Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 40))))).FullName;
        string? nested = FileOps.NewFolder(deep, "New folder");
        check(deep.Length > 260 && nested != null && Directory.Exists(nested), $"a new folder past the classic path limit is created (length={deep.Length})");
        return Task.CompletedTask;
    }
}
