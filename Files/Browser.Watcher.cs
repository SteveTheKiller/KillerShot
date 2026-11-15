using System.IO;
using System.Windows.Threading;

namespace KillerShot.Files;

public sealed partial class Browser
{
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchDebounce;
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string OldPath, string NewPath)> _renames = [];
    private bool _watchOverflow;
    private bool _flushing;
    private const int RelistThreshold = 200;
    private readonly Dictionary<string, int> _watchRetries = new(StringComparer.OrdinalIgnoreCase);
    private const int WatchRetryLimit = 10;

    private void StartWatching(string folder)
    {
        StopWatching();
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    | NotifyFilters.Size | NotifyFilters.Attributes,
                IncludeSubdirectories = false
            };
            _watcher.Created += OnFileChange;
            _watcher.Deleted += OnFileChange;
            _watcher.Changed += OnFileChange;
            _watcher.Renamed += OnRename;
            _watcher.Error += OnWatchError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StopWatching();
            State.Error = error;
        }
    }

    private void StopWatching()
    {
        _watchDebounce?.Stop();
        _touched.Clear();
        _renames.Clear();
        _watchRetries.Clear();
        _watchOverflow = false;
        if (_watcher == null) return;
        _watcher.Created -= OnFileChange;
        _watcher.Deleted -= OnFileChange;
        _watcher.Changed -= OnFileChange;
        _watcher.Renamed -= OnRename;
        _watcher.Error -= OnWatchError;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFileChange(object sender, FileSystemEventArgs args) => QueueChange(sender, args.FullPath);

    private void OnRename(object sender, RenamedEventArgs args)
        => QueueChange(sender, args.FullPath, args.OldFullPath);

    private void OnWatchError(object sender, ErrorEventArgs args)
        => QueueChange(sender, string.Empty, overflow: args.GetException() is InternalBufferOverflowException,
            failure: args.GetException() is InternalBufferOverflowException ? null : args.GetException());

    private void QueueChange(object sender, string path, string? previous = null, bool overflow = false, Exception? failure = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            if (failure != null)
            {
                // A provider failure is not retried indefinitely. The flush still recovers a deleted folder.
                StopWatching();
                State.Error = failure;
            }
            if (overflow) _watchOverflow = true;
            if (path.Length > 0) _touched.Add(path);
            if (previous != null) { _touched.Add(previous); _renames.Add((previous, path)); }
            _watchDebounce ??= CreateWatchTimer();
            // A pending refresh keeps its deadline while more filesystem events arrive.
            // The flush schedules the next batch when changes arrive during its worker read.
            if (!_flushing && !_watchDebounce.IsEnabled) _watchDebounce.Start();
        }, DispatcherPriority.Background);
    }

    private DispatcherTimer CreateWatchTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (_, _) => { timer.Stop(); await FlushWatchChanges(); };
        return timer;
    }

    private async Task FlushWatchChanges()
    {
        if (_disposed || _flushing || State.CurrentFolder.Length == 0 || IsThisPc(State.CurrentFolder)) return;
        _flushing = true;
        string folder = State.CurrentFolder;
        var watcher = _watcher;
        bool showHidden = State.ShowHidden;
        bool relist = _watchOverflow || _touched.Count > RelistThreshold;
        string[] touched = _touched.ToArray();
        var renames = _renames.ToArray();
        _touched.Clear();
        _renames.Clear();
        _watchOverflow = false;
        try
        {
            using var deadline = new CancellationTokenSource(NavigationTimeout);
            var observed = await Task.Run(() =>
            {
                string? survivor = folder;
                while (survivor != null && !IsThisPc(survivor) && !Directory.Exists(survivor))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    survivor = ParentOf(survivor);
                }
                var entries = new Dictionary<string, FileEntry?>(StringComparer.OrdinalIgnoreCase);
                var unreadable = new List<string>();
                if (survivor == folder && !relist)
                    foreach (string path in touched)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase)) continue;
                        // A file still being written or locked is left out of this batch rather
                        // than read as deleted, and is looked at again on the next flush.
                        try { entries[path] = ReadWatchEntry(path, folder, showHidden); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unreadable.Add(path); }
                    }
                return (survivor, entries, unreadable);
            }, deadline.Token).WaitAsync(NavigationTimeout, deadline.Token);
            if (_disposed || !ReferenceEquals(watcher, _watcher) || State.CurrentFolder != folder) return;
            foreach (string path in observed.unreadable)
            {
                int tries = _watchRetries.TryGetValue(path, out int seen) ? seen + 1 : 1;
                if (tries > WatchRetryLimit) { _watchRetries.Remove(path); continue; }
                _watchRetries[path] = tries;
                _touched.Add(path);
            }
            foreach (string path in observed.entries.Keys) _watchRetries.Remove(path);
            if (State.IsLoading && (relist || observed.survivor != folder))
            {
                _watchOverflow = true;
                return;
            }
            if (observed.survivor != folder)
            {
                if (observed.survivor != null) await NavigateTo(observed.survivor);
                return;
            }
            if (relist) { await NavigateCore(folder, false, null, default); return; }
            ApplyWatchChanges(observed.entries, renames);
        }
        catch (OperationCanceledException error) { if (!_disposed && ReferenceEquals(watcher, _watcher) && State.CurrentFolder == folder) State.Error = error; }
        catch (Exception error) { if (!_disposed && ReferenceEquals(watcher, _watcher) && State.CurrentFolder == folder) State.Error = error; }
        finally
        {
            _flushing = false;
            if (!_disposed && (_touched.Count > 0 || _watchOverflow))
            {
                _watchDebounce?.Stop();
                _watchDebounce?.Start();
            }
        }
    }

    private void ApplyWatchChanges(Dictionary<string, FileEntry?> observed, (string OldPath, string NewPath)[] renames)
    {
        var existing = State.Results.ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
        int sequence = State.Results.Count == 0 ? 0 : State.Results.Max(entry => entry.Seq) + 1;
        bool renamed = false;
        foreach (var pair in renames)
        {
            if (!existing.TryGetValue(pair.OldPath, out var row) || !observed.TryGetValue(pair.NewPath, out var fresh)) continue;
            if (fresh == null) State.Results.Remove(row);
            else
            {
                row.ApplyRename(fresh.FilePath, fresh.FileName, fresh.TypeName);
                existing[pair.NewPath] = row;
                renamed = true;
            }
            existing.Remove(pair.OldPath);
            observed.Remove(pair.OldPath);
            observed.Remove(pair.NewPath);
        }
        foreach (var pair in observed)
        {
            existing.TryGetValue(pair.Key, out var row);
            if (pair.Value == null) { if (row != null) State.Results.Remove(row); continue; }
            if (row == null)
            {
                pair.Value.Seq = sequence++;
                State.Results.Add(pair.Value);
            }
            else if (row.SizeBytes != pair.Value.SizeBytes || row.Modified != pair.Value.Modified || row.Created != pair.Value.Created)
            {
                // In place, so a file that keeps changing (a download, a log) stays selected. The
                // edit is committed through the view so the row moves to its new date group and
                // sort position, which replacing the row used to do.
                var fresh = pair.Value;
                void Update()
                {
                    var editable = System.Windows.Data.CollectionViewSource.GetDefaultView(State.Results) as System.ComponentModel.IEditableCollectionView;
                    bool editing = editable is { IsAddingNew: false, IsEditingItem: false };
                    if (editing) editable!.EditItem(row);
                    row.ApplyMetadata(fresh.SizeBytes, fresh.Modified, fresh.Created);
                    if (editing) editable!.CommitEdit();
                }
                if (RepositionEntry is { } reposition) reposition(row, Update);
                else Update();
            }
        }
        if (renamed) Sorting.ApplySort(State);
    }

    /// <summary>Wraps an in-place row update so the view can carry its selection across the move.</summary>
    public Action<FileEntry, Action>? RepositionEntry { get; set; }

    /// <summary>Test seam: returns an exception to throw while one watched path is read.</summary>
    internal static Func<string, Exception?>? WatchReadFault { get; set; }

    private static FileEntry? ReadWatchEntry(string path, string folder, bool showHidden)
    {
        try
        {
            if (WatchReadFault?.Invoke(path) is { } fault) throw fault;
            var attributes = File.GetAttributes(path);
            if (ShouldHide(Path.GetFileName(path), attributes, showHidden)) return null;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
            var entry = new FileEntry
            {
                FilePath = info.FullName, FileName = info.Name, Folder = folder, IsDirectory = directory,
                SizeBytes = directory ? 0 : ((FileInfo)info).Length, Modified = info.LastWriteTime, Created = info.CreationTime
            };
            entry.PrimeTypeName();
            return entry;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
