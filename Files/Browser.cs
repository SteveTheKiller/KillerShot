using System.IO;
using System.Windows.Threading;

namespace KillerShot.Files;

public sealed partial class Browser : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, bool> _fileExists;
    private CancellationTokenSource? _navigation;
    private long _revision;
    private bool _disposed;
    public BrowseState State { get; } = new();
    public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public Browser() : this(null, File.Exists) { }
    public Browser(Dispatcher? dispatcher = null) : this(dispatcher, File.Exists) { }
    public Browser(Dispatcher? dispatcher, Func<string, bool> fileExists)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }
    public Func<string, Task<bool>>? OpenInEditor { get; set; }
    public async Task<bool> ActivateEntry(FileEntry entry, CancellationToken token = default)
    {
        _dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        if (entry.IsDirectory) return await NavigateTo(entry.FilePath, token);
        State.Error = null;
        try
        {
            if (OpenInEditor != null && await OpenInEditor(entry.FilePath).WaitAsync(token)) return true;
            token.ThrowIfCancellationRequested();
            var result = ShellOpen.Open(entry.FilePath);
            if (result == ShellOpenResult.Failed) State.Error = new IOException("Windows could not open " + entry.FilePath + ".");
            return result == ShellOpenResult.Opened;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { State.Error = error; return false; }
    }
    public const string ThisPc = ":ThisPC:";
    public static bool IsThisPc(string? path) => string.Equals(path, ThisPc, StringComparison.Ordinal);
    public static string? ParentOf(string folder)
    {
        if (string.IsNullOrEmpty(folder) || IsThisPc(folder)) return null;
        try { return Directory.GetParent(folder)?.FullName ?? ThisPc; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public async Task<bool> GoBack(CancellationToken token = default)
    {
        _dispatcher.VerifyAccess();
        int index = State.HistoryIndex - 1;
        return index >= 0 && await NavigateCore(State.History[index], false, index, token);
    }

    public async Task<bool> GoForward(CancellationToken token = default)
    {
        _dispatcher.VerifyAccess();
        int index = State.HistoryIndex + 1;
        return index < State.History.Count && await NavigateCore(State.History[index], false, index, token);
    }

    public Task<bool> GoUp(CancellationToken token = default)
    {
        _dispatcher.VerifyAccess();
        string? parent = ParentOf(State.CurrentFolder);
        return parent == null ? Task.FromResult(false) : NavigateTo(parent, token);
    }

    public Task<bool> NavigateTo(string folder, CancellationToken cancellationToken = default)
        => NavigateCore(folder, true, null, cancellationToken);

    public Task<bool> NavigateAddress(string path, CancellationToken cancellationToken = default)
        => NavigateCore(path, true, null, cancellationToken, activateFile: true);

    private async Task<bool> NavigateCore(string folder, bool record, int? historyIndex, CancellationToken cancellationToken, bool activateFile = false)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!activateFile) StopWatching();
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _navigation.Token;
        long revision = ++_revision;
        State.IsLoading = true;
        State.Error = null;
        try
        {
            string resolved = IsThisPc(folder) ? ThisPc : Path.GetFullPath(folder);
            string full = IsThisPc(resolved) ? ThisPc : Path.TrimEndingDirectorySeparator(resolved);
            bool showHidden = State.ShowHidden;
            var listed = await Task.Run(() =>
            {
                if (IsThisPc(full)) return (Entries: ListDrives(), Error: (Exception?)null, IsFile: false);
                if (activateFile && _fileExists(resolved)) return (Entries: new List<FileEntry>(), Error: (Exception?)null, IsFile: true);
                var entries = ListFolder(full, showHidden, token, out var error);
                return (Entries: entries, Error: error, IsFile: false);
            }, token)
                .WaitAsync(NavigationTimeout, token);
            if (token.IsCancellationRequested || revision != _revision || _disposed) return false;
            if (listed.IsFile) return await ActivateEntry(new FileEntry { FilePath = full, FileName = Path.GetFileName(full) }, token);
            if (listed.Error != null && listed.Entries.Count == 0) { State.Error = listed.Error; return false; }
            if (activateFile) StopWatching();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(State.Results);
            using (view.DeferRefresh())
            {
                if (view is System.Windows.Data.ListCollectionView list) list.CustomSort = null;
                view.SortDescriptions.Clear();
                view.GroupDescriptions.Clear();
                if (view.Filter != null) view.Filter = null;
            }
            State.ReplaceResults(listed.Entries);
            using (view.DeferRefresh())
            {
                Sorting.ApplySort(State);
                Sorting.ApplyFilter(State);
            }
            State.CurrentFolder = full;
            if (historyIndex is int index) State.HistoryIndex = index;
            else if (record && (State.HistoryIndex < 0
                || !string.Equals(State.History[State.HistoryIndex], full, StringComparison.OrdinalIgnoreCase)))
            {
                if (State.HistoryIndex < State.History.Count - 1)
                    State.History.RemoveRange(State.HistoryIndex + 1, State.History.Count - State.HistoryIndex - 1);
                State.History.Add(full);
                State.HistoryIndex = State.History.Count - 1;
            }
            State.Notify(nameof(BrowseState.CanGoBack));
            State.Notify(nameof(BrowseState.CanGoForward));
            if (!IsThisPc(full)) StartWatching(full);
            State.Error ??= listed.Error;
            return listed.Error == null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (revision == _revision) State.Error = error;
            if (error is TimeoutException && revision == _revision) _navigation.Cancel();
            if (revision == _revision && !_disposed && _watcher == null && State.CurrentFolder.Length > 0 && !IsThisPc(State.CurrentFolder))
                StartWatching(State.CurrentFolder);
            return false;
        }
        finally
        {
            if (revision == _revision)
            {
                State.IsLoading = false;
                if (!_disposed && _watcher == null && State.CurrentFolder.Length > 0 && !IsThisPc(State.CurrentFolder))
                    StartWatching(State.CurrentFolder);
            }
        }
    }

    public static List<FileEntry> ListFolder(string folder, bool showHidden, CancellationToken token = default)
    {
        var entries = ListFolder(folder, showHidden, token, out var error);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return entries;
    }

    public static List<FileEntry> ListFolder(string folder, bool showHidden, CancellationToken token, out Exception? error)
    {
        error = null;
        var entries = new List<FileEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = info.Attributes; }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { continue; }
                if (ShouldHide(info.Name, attributes, showHidden)) continue;
                bool directory = (attributes & FileAttributes.Directory) != 0;
                var entry = new FileEntry
                {
                    FilePath = info.FullName, FileName = info.Name, Folder = folder, IsDirectory = directory,
                    SizeBytes = directory ? 0 : SafeLength((FileInfo)info),
                    Modified = SafeWriteTime(info), Created = SafeCreateTime(info), Seq = entries.Count
                };
                entry.PrimeTypeName();
                entries.Add(entry);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { error = failure; }
        return entries;
    }

    private static long SafeLength(FileInfo info) { try { return info.Length; } catch { return 0; } }
    private static DateTime SafeWriteTime(FileSystemInfo info) { try { return info.LastWriteTime; } catch { return default; } }
    private static DateTime SafeCreateTime(FileSystemInfo info) { try { return info.CreationTime; } catch { return default; } }

    private static List<FileEntry> ListDrives()
    {
        var entries = new List<FileEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string label = drive.Name.TrimEnd('\\');
            try
            {
                // A ready network mapping can still block on its optional volume label.
                if (drive.DriveType != DriveType.Network && drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    label = drive.VolumeLabel + " (" + label + ")";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            var entry = new FileEntry { FilePath = drive.Name, FileName = label, IsDirectory = true, Seq = entries.Count };
            entry.PrimeTypeName();
            entries.Add(entry);
        }
        return entries;
    }

    internal static bool ShouldHide(string name, FileAttributes attributes, bool showHidden)
        => !showHidden && (name.StartsWith(".", StringComparison.Ordinal)
            || (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0);

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        _disposed = true;
        StopWatching();
        ++_revision;
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = null;
    }
}
