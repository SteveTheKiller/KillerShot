using System.Collections.Specialized;
using System.IO;
using System.Windows;
using KillerShot.Controls;

namespace KillerShot.Files;

public partial class FilesView
{
    /// <summary>The operating-system clipboard boundary. Defaults use Windows.</summary>
    public Action<IDataObject> WriteClipboard { get; set; } = data => Clipboard.SetDataObject(data, true);
    public Func<IDataObject?> ReadClipboard { get; set; } = Clipboard.GetDataObject;
    /// <summary>Answers a name collision during copy or move. Null shows the conflict dialog.</summary>
    public Func<ConflictInfo, ConflictChoice>? ConflictPrompt { get; set; }
    public FileOpResult? LastOperation { get; private set; }
    public string? CommandError { get; private set; }
    private CancellationTokenSource? _fileOperation;
    public bool IsFileOperationBusy => _fileOperation != null;
    private FileEntry? _renaming;
    private bool _committingRename;
    public bool CanRename => !IsFileOperationBusy && !Browser.IsThisPc(Navigation.CurrentFolder)
        && ResultsList.SelectedItems.Count == 1 && ResultsList.SelectedItem is FileEntry entry && MutableEntry(entry);
    public bool CanCut => !Browser.IsThisPc(Navigation.CurrentFolder) && ResultsList.SelectedItems.Count > 0
        && ResultsList.SelectedItems.OfType<FileEntry>().All(MutableEntry);
    public bool CanRecycle => !IsFileOperationBusy && CanCut;
    public bool CanPaste => !IsFileOperationBusy && !Browser.IsThisPc(Navigation.CurrentFolder) && Navigation.CurrentFolder.Length > 0;

    private CancellationTokenSource? BeginMutation(CancellationToken token = default, bool cancelable = false)
    {
        Dispatcher.VerifyAccess();
        if (_fileOperation != null) return null;
        var lease = CancellationTokenSource.CreateLinkedTokenSource(token);
        _fileOperation = lease;
        CancelOperationButton.Visibility = cancelable ? Visibility.Visible : Visibility.Collapsed;
        return lease;
    }

    private void EndMutation(CancellationTokenSource lease)
    {
        if (!ReferenceEquals(_fileOperation, lease)) return;
        _fileOperation = null;
        CancelOperationButton.Visibility = Visibility.Collapsed;
    }

    private static bool MutableEntry(FileEntry entry)
    {
        try
        {
            string path = Path.GetFullPath(entry.FilePath).TrimEnd(Path.DirectorySeparatorChar);
            string root = (Path.GetPathRoot(path) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
            return path.Length > 0 && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private void SetCommandError(string? error)
    {
        CommandError = error;
        RenameBox.ToolTip = error;
        CommandStatus.Text = error ?? string.Empty;
        CommandStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            error == null ? "MutedTextBrush" : "DangerRed");
    }

    public bool BeginRename()
    {
        Dispatcher.VerifyAccess();
        if (!CanRename || ResultsList.SelectedItem is not FileEntry entry) return false;
        ResultsList.ScrollIntoView(entry);
        ResultsList.UpdateLayout();
        if (ResultsList.ItemContainerGenerator.ContainerFromItem(entry) is not System.Windows.Controls.ListBoxItem row) return false;
        var label = Descendants<System.Windows.Controls.TextBlock>(row).FirstOrDefault(text =>
            System.Windows.Data.BindingOperations.GetBinding(text, System.Windows.Controls.TextBlock.TextProperty)?.Path.Path == "NameLabel");
        if (label == null) return false;
        var position = label.TranslatePoint(new Point(), InteractionLayer);
        _renaming = entry;
        RenameBox.Text = entry.FileName;
        RenameBox.Width = Math.Max(80, Math.Min(ResultsList.ActualWidth - position.X - 8, Math.Max(label.ActualWidth, 160)));
        RenameBox.Height = Math.Max(22, label.ActualHeight + 4);
        System.Windows.Controls.Canvas.SetLeft(RenameBox, position.X);
        System.Windows.Controls.Canvas.SetTop(RenameBox, position.Y);
        RenameBox.Visibility = Visibility.Visible;
        RenameBox.Focus();
        int extension = entry.IsDirectory ? -1 : entry.FileName.LastIndexOf('.');
        RenameBox.Select(0, extension > 0 ? extension : entry.FileName.Length);
        SetCommandError(null);
        return true;
    }

    public void CancelRename()
    {
        _renaming = null;
        RenameBox.Visibility = Visibility.Collapsed;
        SetCommandError(null);
    }

    public async Task<bool> CommitRename()
    {
        if (_renaming is not { } entry || _committingRename) return false;
        using var lease = BeginMutation();
        if (lease == null) return false;
        _committingRename = true;
        try
        {
            string name = RenameBox.Text;
            string? error = await Task.Run(() => FileOps.Rename(entry.FilePath, name));
            if (error != null)
            {
                string reason = error switch
                {
                    "empty name" => Loc("Str_Err_EmptyName"),
                    "invalid characters" => Loc("Str_Err_InvalidChars"),
                    "already exists" => Loc("Str_Err_AlreadyExists"),
                    _ => error
                };
                SetCommandError(string.Format(Loc("Str_Status_RenameFailed"), reason));
                // Back to the box for a fix, but never by pulling activation back from another app.
                if (Window.GetWindow(this) is not { IsActive: false }) RenameBox.Focus();
                return false;
            }
            CancelRename();
            ResultsList.Focus();
            return true;
        }
        finally { _committingRename = false; EndMutation(lease); }
    }

    private void RenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CancelRename(); ResultsList.Focus(); }
        else if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; StartCommand(CommitRename()); }
    }

    // Leaving the box commits the typed name the way Explorer does, whether the click went
    // elsewhere in the list or the window lost activation (Alt+Tab, a click into another app).
    // Only Escape throws the edit away.
    private void RenameBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        => CommitRenameOnLeave();

    internal void CommitRenameOnLeave()
    {
        if (_renaming == null || _committingRename || RenameBox.Visibility != Visibility.Visible) return;
        StartCommand(CommitRename());
    }

    public IReadOnlyList<string> SelectedPaths => ResultsList.SelectedItems.OfType<FileEntry>()
        .Select(entry => entry.FilePath).ToArray();

    public bool CopySelection(bool cut = false)
    {
        Dispatcher.VerifyAccess();
        string[] paths = SelectedPaths.ToArray();
        if (paths.Length == 0 || (cut && !CanCut)) return false;
        try
        {
            var data = new DataObject();
            var list = new StringCollection();
            list.AddRange(paths);
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
            WriteClipboard(data);
            SetCommandError(null);
            return true;
        }
        catch (Exception error) { SetCommandError(error.Message); return false; }
    }

    public async Task<FileOpResult?> PasteFiles(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        if (!CanPaste) return null;
        IDataObject? data;
        try { data = ReadClipboard(); }
        catch (Exception error) { SetCommandError(error.Message); return null; }
        if (data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return null;
        bool move = false;
        if (data.GetData("Preferred DropEffect") is Stream stream)
        {
            long position = stream.CanSeek ? stream.Position : 0;
            try
            {
                if (stream.CanSeek) stream.Position = 0;
                byte[] effect = new byte[4];
                if (stream.Read(effect, 0, 4) == 4) move = (BitConverter.ToInt32(effect) & 2) != 0;
            }
            finally { if (stream.CanSeek) stream.Position = position; }
        }
        var result = await RunCopyMove(paths, Navigation.CurrentFolder, move, cancellationToken);
        // Explorer empties the clipboard after a cut-paste: the sources are gone, so a second
        // paste of the same list could only fail. Anything copied since is left alone.
        if (move && result.Succeeded > 0 && !result.Canceled)
        {
            try
            {
                if (ReadClipboard()?.GetData(DataFormats.FileDrop) is string[] still
                    && still.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
                    WriteClipboard(new DataObject());
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { }
        }
        return result;
    }

    public void CancelFileOperation() => _fileOperation?.Cancel();
    public async Task<string?> NewFolder()
    {
        if (!CanPaste || _fileOperation != null) return null;
        string target = Navigation.CurrentFolder;
        string name = Loc("Str_Fo_NewFolderName");
        using var lease = BeginMutation();
        if (lease == null) return null;
        try
        {
            string? path = await Task.Run(() => FileOps.NewFolder(target, name));
            if (path == null) { SetCommandError(Loc("Str_Status_OpFailed")); return null; }
            await SelectArrivals(target, [path]);
            EndMutation(lease);
            if (string.Equals(Navigation.CurrentFolder, target, StringComparison.OrdinalIgnoreCase)
                && ResultsList.SelectedItem is FileEntry created && string.Equals(created.FilePath, path, StringComparison.OrdinalIgnoreCase))
                BeginRename();
            return path;
        }
        finally { EndMutation(lease); }
    }
    public async Task<FileOpResult?> DropFiles(IDataObject data, string target, DragDropEffects allowed,
        System.Windows.Input.ModifierKeys modifiers, CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        if (IsFileOperationBusy) return null;
        if (Browser.IsThisPc(target) || data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return null;
        var effect = DropEffect(paths, target, allowed, modifiers);
        if (effect == DragDropEffects.None) return null;
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if (destination.Equals(full, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { SetCommandError(Loc("Str_Status_DropIntoSelf")); return null; }
            if (effect == DragDropEffects.Move && !MutableEntry(new FileEntry { FilePath = path })) return null;
        }
        // Moving an item into the folder it is already in changes nothing, the way Explorer treats
        // it. A copy there is a duplicate beside it ("name - Copy"), which FileOps names.
        string[] incoming = effect != DragDropEffects.Move ? paths : paths.Where(path => !string.Equals(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)),
            target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (incoming.Length == 0) return null;
        var result = await RunCopyMove(incoming, target, effect == DragDropEffects.Move, cancellationToken);
        if (!result.Canceled && result.SucceededTargets.Count > 0 && effect == DragDropEffects.Move)
        {
            await SelectArrivals(target, result.SucceededTargets);
        }
        return result;
    }

    public static DragDropEffects DropEffect(IReadOnlyList<string> paths, string target, DragDropEffects allowed,
        System.Windows.Input.ModifierKeys modifiers)
    {
        if (paths.Count == 0 || string.IsNullOrEmpty(target) || Browser.IsThisPc(target)) return DragDropEffects.None;
        bool ctrl = (modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool shift = (modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        if (ctrl && shift) return DragDropEffects.None; // Shortcuts are outside this view's file-operation scope.
        var wanted = shift ? DragDropEffects.Move : ctrl ? DragDropEffects.Copy
            : paths.All(path => string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Path.GetFullPath(target)),
                StringComparison.OrdinalIgnoreCase)) && (allowed & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        return (allowed & wanted) != 0 ? wanted : DragDropEffects.None;
    }

    private async Task SelectArrivals(string target, IReadOnlyList<string> paths)
    {
        if (!string.Equals(Navigation.CurrentFolder, target, StringComparison.OrdinalIgnoreCase)) return;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(2)
            && paths.Any(path => !Navigation.Results.Any(entry => string.Equals(entry.FilePath, path, StringComparison.OrdinalIgnoreCase))))
            await Task.Delay(20);
        if (!string.Equals(Navigation.CurrentFolder, target, StringComparison.OrdinalIgnoreCase)) return;
        var hits = Navigation.Results.Where(entry => paths.Contains(entry.FilePath, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (hits.Length == 0) return;
        ResultsList.SelectedItems.Clear();
        foreach (var hit in hits) ResultsList.SelectedItems.Add(hit);
        ResultsList.ScrollIntoView(hits[0]);
    }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => CancelFileOperation();

    public async Task<FileOpResult?> RecycleSelection()
    {
        if (!CanRecycle || _fileOperation != null) return null;
        string[] paths = SelectedPaths.ToArray();
        if (paths.Any(Directory.Exists) && Settings.Get("Files.ConfirmFolderDelete") != "0")
        {
            var dialog = new ConfirmDialog(string.Format(Loc("Str_Dlg_RecycleFolderMsg"), paths.Length),
                string.Join(Environment.NewLine, paths.Take(8)), Loc("Str_Btn_Delete"),
                check1Label: Loc("Str_Chk_DontAskAgain")) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            if (!dialog.Confirmed) return LastOperation = new FileOpResult { Canceled = true };
            if (dialog.Check1Checked) Settings.Set("Files.ConfirmFolderDelete", "0");
        }
        using var lease = BeginMutation();
        if (lease == null) return null;
        try
        {
            // The shell's progress and "cannot be recycled" dialogs are owned by this window, so
            // they open in front of it. ShellRecycle runs them on their own STA thread.
            IntPtr owner = Window.GetWindow(this) is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
            var result = await Task.Run(() => FileOps.Recycle(paths, owner: owner));
            LastOperation = result;
            ReportOperation(result);
            return result;
        }
        finally { EndMutation(lease); }
    }

    private void ReportOperation(FileOpResult result)
    {
        // The error alone only when nothing went through; a mixed result says how many of each
        // and then the first reason.
        if (result.Failed.Count > 0 && result.Succeeded == 0)
        {
            SetCommandError(result.Failed[0].Error);
            return;
        }
        SetCommandError(null);
        CommandStatus.Text = result.Failed.Count > 0
            ? string.Format(Loc("Str_Status_OpPartial"), result.Succeeded, result.Failed.Count) + ": " + result.Failed[0].Error
            : string.Format(Loc(result.Canceled ? "Str_Status_OpCanceled" : "Str_Status_OpDone"), result.Succeeded);
        if (result.Failed.Count > 0)
            CommandStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerRed");
        if (result.SkippedDirectoryLinks > 0)
            CommandStatus.Text += " " + string.Format(Loc("Str_Status_DirectoryLinksSkipped"), result.SkippedDirectoryLinks);
    }

    private async Task<FileOpResult> RunCopyMove(string[] paths, string target, bool move, CancellationToken cancellationToken)
    {
        using var cancellation = BeginMutation(cancellationToken, cancelable: true)
            ?? throw new InvalidOperationException("A file operation is already running.");
        var result = new FileOpResult();
        try
        {
            result = await Task.Run(() => FileOps.CopyOrMove(paths, target, move,
                info => Dispatcher.Invoke(() => cancellation.IsCancellationRequested ? ConflictChoice.Cancel
                    : ConflictPrompt?.Invoke(info) ?? AskConflict(info)),
                progress => Dispatcher.BeginInvoke(() => CommandStatus.Text = progress.CurrentFile), cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException) { result.Canceled = true; }
        catch (Exception error) { result.Failed.Add((target, error.Message)); }
        finally { EndMutation(cancellation); }
        LastOperation = result;
        ReportOperation(result);
        return result;
    }

    private ConflictChoice AskConflict(ConflictInfo info)
    {
        var dialog = new ConfirmDialog(string.Format(Loc("Str_Fo_ConflictTitle"), Path.GetFileName(info.TargetPath)),
            info.SourcePath + Environment.NewLine + info.TargetPath, Loc("Str_Btn_Replace"))
            { Owner = Window.GetWindow(this) };
        dialog.AddChoice("skip", Loc("Str_Btn_Skip"));
        dialog.AddChoice("keep", Loc("Str_Btn_KeepBoth"));
        dialog.DefaultToCancel();
        dialog.ShowDialog();
        return dialog.Confirmed ? ConflictChoice.Replace : dialog.Choice switch
        {
            "skip" => ConflictChoice.Skip,
            "keep" => ConflictChoice.KeepBoth,
            _ => ConflictChoice.Cancel
        };
    }

    private string Loc(string key) => Convert.ToString(TryFindResource(key)) ?? key;
}
