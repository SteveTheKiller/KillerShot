using System.IO;
using System.Text.Json;

namespace KillerShot.Services;

internal sealed record FolderCollection(string Source, string Path, bool Available, bool Loading)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
}

internal static partial class FolderCaptures
{
    private sealed class LinkedFolder(string source, string path, bool videos = false) : IDisposable
    {
        internal readonly string Source = source;
        internal readonly string Path = path;
        /// <summary>The Recordings folder: its MP4 and GIF files, top level only, as videos.</summary>
        internal readonly bool Videos = videos;
        internal readonly HashSet<string> ScanningPaths = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyList<CaptureItem> Items = [];
        internal bool Available;
        internal bool Initialized;
        internal bool Network;
        internal Task? Scan;
        internal DateTime NextScan;
        internal FileSystemWatcher? Watcher;
        internal Timer? Debounce;
        internal long ChangedAt;
        internal bool Dirty;
        /// <summary>A change-driven rescan loop is running for this folder.</summary>
        internal bool Refreshing;
        internal int PollSeconds = 30;
        public void Dispose() { Watcher?.Dispose(); Debounce?.Dispose(); }
    }

    private static readonly object LinkedGate = new();
    private static readonly object LinkedWriteGate = new();
    private static readonly Dictionary<string, LinkedFolder> Linked = [];
    private static string? _linkedConfig;
    private static readonly SemaphoreSlim ScanSlots = new(2);
    private static readonly SemaphoreSlim NetworkScanSlots = new(2);
    internal static Func<string, DriveType> DriveTypeReader { get; set; } = root => new DriveInfo(root).DriveType;
    private static readonly Timer LinkedPoll = new(_ => PollLinkedFolders(), null, 10000, 10000);

    public static bool IsNetworkItem(long id)
    {
        string? path = PathOf(id);
        if (path == null) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        lock (LinkedGate) return Linked.Values.Any(folder => folder.Network &&
            path.StartsWith(folder.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSource(string? source) => source == "folder" || source?.StartsWith("folder:", StringComparison.Ordinal) == true;

    /// <summary>The built-in collection for the screen recording folder. It is never saved with
    /// the folders the user linked; it follows the recording folder setting instead.</summary>
    public const string RecordingsSource = "folder:recordings";

    /// <summary>Shown as a collection unless turned off in Video settings.</summary>
    public static bool RecordingsEnabled
    {
        get => Settings.GetBool("LibraryShowRecordings", true);
        set { Settings.SetBool("LibraryShowRecordings", value); SyncRecordings(); Changed?.Invoke(); }
    }

    /// <summary>For the self-test: another recording folder in place of the user's. A test that
    /// swapped in its own Screenshots folder (UseFolder) shows no Recordings collection until it
    /// sets this, so the user's real recordings never leak into a test's library.</summary>
    internal static string? RecordingsFolderOverride
    {
        get => _recordingsOverride;
        set { _recordingsOverride = value; SyncRecordings(); }
    }
    private static string? _recordingsOverride;
    private static bool _testFolders;

    /// <summary>The recording folder setting changed: the Recordings collection moves with it.
    /// Collection reads never consult settings themselves, so a settings write in progress cannot
    /// hold up the library (folder-fixes self-test).</summary>
    public static void RecordingsFolderChanged() => SyncRecordings();

    private static string RecordingsFolder => RecordingsFolderOverride ?? VideoSettings.OutputFolder;

    /// <summary>Keeps the Recordings collection on the folder recordings are saved to, and away
    /// when it is turned off. Cheap when nothing changed.</summary>
    private static void SyncRecordings()
    {
        string? path = null;
        if (RecordingsEnabled && !(_testFolders && RecordingsFolderOverride == null))
        {
            try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecordingsFolder)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { }
        }
        bool refresh = false;
        lock (LinkedGate)
        {
            if (_linkedConfig == null) return;
            Linked.TryGetValue(RecordingsSource, out var current);
            if (path == null)
            {
                if (current == null) return;
                current.Dispose();
                Linked.Remove(RecordingsSource);
                PruneLinkedMetadata();
            }
            else if (current == null || !string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                current?.Dispose();
                Linked[RecordingsSource] = new LinkedFolder(RecordingsSource, path, videos: true);
                refresh = true;
            }
            else return;
        }
        if (refresh) _ = RefreshFolderAsync(RecordingsSource);
        Changed?.Invoke();
    }

    /// <summary>The folders the user linked, without the built-in Recordings one, as saved JSON.</summary>
    private static string SavedFolders(IEnumerable<KeyValuePair<string, LinkedFolder>> folders) =>
        JsonSerializer.Serialize(folders.Where(pair => !pair.Value.Videos).ToDictionary(pair => pair.Key, pair => pair.Value.Path));

    public static IReadOnlyList<FolderCollection> LinkedCollections()
    {
        EnsureLinkedFolders();
        lock (LinkedGate) return Linked.Values.Select(folder => new FolderCollection(folder.Source, folder.Path,
            folder.Available, !folder.Initialized)).ToArray();
    }

    public static string AddFolder(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)));
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(nameof(path));
        string source;
        lock (LinkedWriteGate)
        {
            EnsureLinkedFolders();
            string json;
            lock (LinkedGate)
            {
                if (Linked.Values.FirstOrDefault(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)) is { } existing)
                    return existing.Source;
                source = "folder:" + Guid.NewGuid().ToString("N");
                var paths = Linked.Where(pair => !pair.Value.Videos).ToDictionary(pair => pair.Key, pair => pair.Value.Path);
                paths[source] = path;
                json = JsonSerializer.Serialize(paths);
            }
            Settings.SetRequired("LinkedFolderCollections", json);
            lock (LinkedGate)
            {
                _linkedConfig = json;
                Linked[source] = new LinkedFolder(source, path);
            }
        }
        _ = RefreshFolderAsync(source);
        Changed?.Invoke();
        return source;
    }

    public static void RemoveFolder(string source)
    {
        // The Recordings collection is not a saved link: removing it turns it off.
        if (source == RecordingsSource) { RecordingsEnabled = false; return; }
        lock (LinkedWriteGate)
        {
            EnsureLinkedFolders();
            string json;
            lock (LinkedGate)
            {
                if (!Linked.ContainsKey(source)) return;
                json = SavedFolders(Linked.Where(pair => pair.Key != source));
            }
            Settings.SetRequired("LinkedFolderCollections", json);
            lock (LinkedGate)
            {
                _linkedConfig = json;
                if (Linked.Remove(source, out var removed)) removed.Dispose();
                PruneLinkedMetadata();
            }
        }
        Changed?.Invoke();
    }

    public static IReadOnlyList<CaptureItem> LinkedItems(string source, string? search = null)
    {
        EnsureLinkedFolders();
        IReadOnlyList<CaptureItem> items;
        lock (LinkedGate) items = Linked.TryGetValue(source, out var folder) ? folder.Items : [];
        string[] words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 ? items : items.Where(item => words.All(word =>
            (item.Window ?? "").Contains(word, StringComparison.CurrentCultureIgnoreCase))).ToArray();
    }

    public static IReadOnlyList<CaptureItem> ItemsForSource(string source, string? search = null) =>
        source == "folder" ? Snapshot(search) : LinkedItems(source, search);

    public static string PathForSource(string source) => source == "folder" ? Folder
        : LinkedCollections().FirstOrDefault(folder => folder.Source == source)?.Path ?? "";

    public static string NameForSource(string source) => source == "folder" ? Name
        : LinkedCollections().FirstOrDefault(folder => folder.Source == source)?.Name ?? "";

    public static string SourceForItem(CaptureItem item)
    {
        lock (LinkedGate)
            return Linked.Values.FirstOrDefault(folder => folder.Items.Any(photo => photo.Id == item.Id))?.Source ?? "folder";
    }

    private static void EnsureLinkedFolders()
    {
        lock (LinkedGate) if (_linkedConfig != null) return;
        string[] start;
        lock (LinkedWriteGate)
        {
            lock (LinkedGate) if (_linkedConfig != null) return;
            string config = Settings.Get("LinkedFolderCollections") ?? "{}";
            Dictionary<string, string> paths;
            try { paths = JsonSerializer.Deserialize<Dictionary<string, string>>(config) ?? []; }
            catch (JsonException) { paths = []; }
            lock (LinkedGate)
            {
                foreach (var folder in Linked.Values) folder.Dispose();
                Linked.Clear();
                foreach (var (source, path) in paths)
                    if (source.StartsWith("folder:", StringComparison.Ordinal) && source != RecordingsSource && Path.IsPathFullyQualified(path))
                        Linked[source] = new LinkedFolder(source, path);
                _linkedConfig = config;
                start = Linked.Keys.ToArray();
            }
        }
        foreach (string source in start) _ = RefreshFolderAsync(source);
        SyncRecordings();
    }

    private static void ResetLinkedFolders()
    {
        lock (LinkedWriteGate)
        lock (LinkedGate)
        {
            foreach (var folder in Linked.Values) folder.Dispose();
            Linked.Clear();
            _linkedConfig = null;
        }
    }

    private static void PollLinkedFolders()
    {
        string[] sources;
        lock (LinkedGate) sources = Linked.Values.Where(folder => DateTime.UtcNow >= folder.NextScan
            && folder.Scan is not { IsCompleted: false }).Select(folder => folder.Source).ToArray();
        foreach (string source in sources) _ = RefreshFolderAsync(source);
    }

    public static Task RefreshFolderAsync(string source)
    {
        if (source == "folder") return RefreshScreenshotsAsync();
        EnsureLinkedFolders();
        lock (LinkedGate)
        {
            if (!Linked.TryGetValue(source, out var folder)) return Task.CompletedTask;
            if (folder.Scan is { IsCompleted: false }) return folder.Scan;
            folder.NextScan = DateTime.UtcNow.AddSeconds(folder.PollSeconds);
            folder.Dirty = false;
            return folder.Scan = Task.Run(async () =>
            {
                SemaphoreSlim? slots = null;
                try
                {
                    // A stalled share must not occupy the local-directory scan slots.
                    bool network = folder.Path.StartsWith(@"\\", StringComparison.Ordinal)
                        || DriveTypeReader(Path.GetPathRoot(folder.Path)!) == DriveType.Network;
                    folder.Network = network;
                    slots = network ? NetworkScanSlots : ScanSlots;
                    await slots.WaitAsync();
                    ConfigureLinkedWatch(folder, beforeScan: true);
                    ScanLinkedFolder(folder);
                    ConfigureLinkedWatch(folder);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    lock (LinkedGate)
                    {
                        folder.Available = false;
                        folder.Initialized = true;
                        folder.PollSeconds = Math.Min(300, folder.PollSeconds * 2);
                        folder.NextScan = DateTime.UtcNow.AddSeconds(folder.PollSeconds);
                    }
                    Changed?.Invoke();
                }
                finally { slots?.Release(); }
            });
        }
    }

    private static void ConfigureLinkedWatch(LinkedFolder folder, bool beforeScan = false)
    {
        lock (LinkedGate)
        {
            if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) return;
            if (!beforeScan)
            {
                folder.PollSeconds = folder.Available ? (folder.Network ? 60 : 30) : Math.Min(300, folder.PollSeconds * 2);
                folder.NextScan = DateTime.UtcNow.AddSeconds(folder.PollSeconds);
            }
            if (folder.Network || !beforeScan && !folder.Available) { folder.Watcher?.Dispose(); folder.Watcher = null; return; }
            if (folder.Watcher != null) { folder.NextScan = DateTime.MaxValue; return; }
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = WatcherFactory(folder.Path);
                watcher.IncludeSubdirectories = !folder.Videos;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                FileSystemEventHandler changed = (_, _) => LinkedTouched(folder);
                watcher.Created += changed; watcher.Deleted += changed; watcher.Changed += changed;
                watcher.Renamed += (_, _) => LinkedTouched(folder);
                watcher.Error += (_, _) =>
                {
                    lock (LinkedGate)
                    {
                        folder.Watcher?.Dispose();
                        folder.Watcher = null;
                        folder.NextScan = DateTime.MinValue;
                    }
                    LinkedTouched(folder);
                };
                watcher.EnableRaisingEvents = true;
                folder.Watcher = watcher;
                folder.NextScan = DateTime.MaxValue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { watcher?.Dispose(); }
        }
    }

    private static void LinkedTouched(LinkedFolder folder)
    {
        lock (LinkedGate)
        {
            if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) return;
            folder.Dirty = true;
            long now = Environment.TickCount64;
            if (folder.ChangedAt == 0) folder.ChangedAt = now;
            int delay = (int)Math.Min(400, Math.Max(0, 2000 - (now - folder.ChangedAt)));
            folder.Debounce ??= new Timer(_ => _ = RefreshLinkedChangesAsync(folder), null, Timeout.Infinite, Timeout.Infinite);
            folder.Debounce.Change(delay, Timeout.Infinite);
        }
    }

    /// <summary>A folder that keeps changing while it is scanned is scanned again, but each scan
    /// starts at least this long after the one before it, so a folder written to without a pause
    /// is not read back to back. The first scan after a change is not delayed.</summary>
    private const int LinkedRescanGapMs = 2000;

    private static async Task RefreshLinkedChangesAsync(LinkedFolder folder)
    {
        // One loop per folder: changes that arrive while it runs only mark the folder dirty.
        lock (LinkedGate)
        {
            if (folder.Refreshing) return;
            folder.Refreshing = true;
        }
        try
        {
            do
            {
                long started = Environment.TickCount64;
                await RefreshFolderAsync(folder.Source).ConfigureAwait(false);
                if (!StillDirty(folder)) return;
                long wait = LinkedRescanGapMs - (Environment.TickCount64 - started);
                if (wait > 0) await Task.Delay((int)wait).ConfigureAwait(false);
            } while (true);
        }
        catch (Exception error)
        {
            lock (LinkedGate) { folder.Refreshing = false; folder.ChangedAt = 0; }
            System.Diagnostics.Trace.TraceWarning("Linked folder refresh failed: {0}", error.Message);
        }
    }

    /// <summary>True while the folder changed since its scan began. Otherwise the loop ends, in the
    /// same step that a later change would see, so no change is left without a scan.</summary>
    private static bool StillDirty(LinkedFolder folder)
    {
        lock (LinkedGate)
        {
            if (Linked.TryGetValue(folder.Source, out var current) && ReferenceEquals(current, folder) && folder.Dirty) return true;
            folder.ChangedAt = 0;
            folder.Refreshing = false;
            return false;
        }
    }

    private static void ScanLinkedFolder(LinkedFolder folder)
    {
        var items = new List<CaptureItem>();
        bool available = false;
        // A large tree takes a while; the pictures found so far show up as the scan goes, so the
        // collection fills in instead of sitting on a spinner until the last file is read.
        var published = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            bool complete = true;
            foreach (var file in LinkedFiles(folder, _directoryReader, () => complete = false))
            {
                if (folder.Videos ? !VideoCaptures.IsVideoFile(file.FullName)
                    : !Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                lock (LinkedGate)
                {
                    if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) return;
                    folder.ScanningPaths.Add(file.FullName);
                }
                try
                {
                    lock (_gate)
                        if (_sizes.TryGetValue(file.FullName, out var cached) && cached.Stamp != file.LastWriteTimeUtc)
                        {
                            _sizes.Remove(file.FullName);
                            if (_ids.TryGetValue(file.FullName, out long id)) RemoveThumb(id);
                        }
                    var (width, height) = folder.Videos ? VideoSizeOf(file) : SizeOf(file);
                    if (width == 0 || height == 0) continue;
                    lock (LinkedGate)
                    {
                        if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder))
                        {
                            PruneLinkedMetadata();
                            return;
                        }
                        long id = IdOf(file.FullName);
                        if (folder.Videos) lock (_gate) _videoIds.Add(id);
                        items.Add(new CaptureItem(id, file.LastWriteTime, width, height,
                            folder.Videos ? VideoCaptures.Mode : "file", Path.GetFileNameWithoutExtension(file.Name), null, CollectionId, null));
                    }
                    if (!folder.Initialized && published.Elapsed.TotalSeconds >= 2)
                    {
                        published.Restart();
                        var partial = items.OrderByDescending(item => item.Taken).ThenBy(item => item.Id).ToArray();
                        lock (LinkedGate)
                        {
                            if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) return;
                            folder.Items = partial;
                        }
                        Changed?.Invoke();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            available = complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        bool changed;
        var ordered = items.OrderByDescending(item => item.Taken).ThenBy(item => item.Id).ToArray();
        lock (LinkedGate)
        {
            if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder))
            {
                PruneLinkedMetadata();
                return;
            }
            changed = !folder.Initialized || folder.Available != available || !folder.Items.SequenceEqual(ordered);
            folder.Items = ordered;
            folder.Available = available;
            folder.Initialized = true;
            folder.ScanningPaths.Clear();
            PruneLinkedMetadata();
        }
        if (changed) Changed?.Invoke();
    }

    private static IEnumerable<FileInfo> LinkedFiles(LinkedFolder folder, IFolderDirectoryReader reader, Action incomplete)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder.Path));
        while (pending.TryPop(out var directory))
        {
            lock (LinkedGate)
                if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) yield break;
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
            // Recordings are saved flat. Its subfolders include the hidden staging folder a
            // recording is written in before it is finished, which must never show as a clip.
            if (!folder.Videos)
                foreach (var child in ReadDirectoryEntries(() => reader.EnumerateDirectories(directory, options), _ => incomplete())) pending.Push(child);
            foreach (var file in ReadDirectoryEntries(() => reader.EnumerateFiles(directory, options), _ => incomplete())) yield return file;
        }
    }

    private static void PruneLinkedMetadata()
    {
        var retained = Linked.Values.SelectMany(folder => folder.Items).Select(item => item.Id).ToHashSet();
        var scanning = Linked.Values.SelectMany(folder => folder.ScanningPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string screenshots = Folder.TrimEnd(Path.DirectorySeparatorChar);
        bool IsScreenshotsPath(string path) => string.Equals(Path.GetDirectoryName(path), screenshots, StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (id, path) in _paths.ToArray())
            {
                if (retained.Contains(id) || scanning.Contains(path) || IsScreenshotsPath(path)) continue;
                _paths.Remove(id);
                _videoIds.Remove(id);
                _ids.Remove(path);
                _sizes.Remove(path);
                RemoveThumb(id);
            }
            foreach (string path in _sizes.Keys.Where(path => !_ids.ContainsKey(path) && !scanning.Contains(path)
                && !IsScreenshotsPath(path)).ToArray()) _sizes.Remove(path);
        }
    }
}
