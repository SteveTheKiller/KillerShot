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
    private long _watchStarted;
    private const int RelistThreshold = 200;

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
        _watchOverflow = false;
        _watchStarted = 0;
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
            if (_watchStarted == 0) _watchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _watchDebounce ??= CreateWatchTimer();
            _watchDebounce.Stop();
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_watchStarted).TotalMilliseconds;
            _watchDebounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(400, 1500 - elapsed)));
            _watchDebounce.Start();
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
        _watchStarted = 0;
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
                if (survivor == folder && !relist)
                    foreach (string path in touched)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase))
                            entries[path] = ReadWatchEntry(path, folder, showHidden);
                    }
                return (survivor, entries);
            }, deadline.Token).WaitAsync(NavigationTimeout, deadline.Token);
            if (_disposed || !ReferenceEquals(watcher, _watcher) || State.CurrentFolder != folder) return;
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
                pair.Value.Seq = row.Seq;
                int index = State.Results.IndexOf(row);
                if (index >= 0) State.Results[index] = pair.Value;
            }
        }
        if (renamed) Sorting.ApplySort(State);
    }

    private static FileEntry? ReadWatchEntry(string path, string folder, bool showHidden)
    {
        try
        {
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
