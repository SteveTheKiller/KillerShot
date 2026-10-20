using System.IO;
using System.Text.Json;

namespace KillerShot.Services;

internal sealed record FolderCollection(string Source, string Path, bool Available, bool Loading)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
}

internal static partial class FolderCaptures
{
    private sealed class LinkedFolder(string source, string path)
    {
        internal readonly string Source = source;
        internal readonly string Path = path;
        internal IReadOnlyList<CaptureItem> Items = [];
        internal bool Available;
        internal bool Initialized;
        internal Task? Scan;
        internal DateTime NextScan;
    }

    private static readonly object LinkedGate = new();
    private static readonly object LinkedWriteGate = new();
    private static readonly Dictionary<string, LinkedFolder> Linked = [];
    private static string? _linkedConfig;
    private static readonly SemaphoreSlim ScanSlots = new(2);
    private static readonly SemaphoreSlim NetworkScanSlots = new(2);
    private static readonly Timer LinkedPoll = new(_ => PollLinkedFolders(), null, 10000, 10000);

    public static bool IsSource(string? source) => source == "folder" || source?.StartsWith("folder:", StringComparison.Ordinal) == true;

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
                var paths = Linked.ToDictionary(pair => pair.Key, pair => pair.Value.Path);
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
        lock (LinkedWriteGate)
        {
            EnsureLinkedFolders();
            string json;
            lock (LinkedGate)
            {
                if (!Linked.ContainsKey(source)) return;
                var paths = Linked.Where(pair => pair.Key != source).ToDictionary(pair => pair.Key, pair => pair.Value.Path);
                json = JsonSerializer.Serialize(paths);
            }
            Settings.SetRequired("LinkedFolderCollections", json);
            lock (LinkedGate)
            {
                _linkedConfig = json;
                Linked.Remove(source);
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
        source == "folder" ? Items(search) : LinkedItems(source, search);

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
                Linked.Clear();
                foreach (var (source, path) in paths)
                    if (source.StartsWith("folder:", StringComparison.Ordinal) && Path.IsPathFullyQualified(path))
                        Linked[source] = new LinkedFolder(source, path);
                _linkedConfig = config;
                start = Linked.Keys.ToArray();
            }
        }
        foreach (string source in start) _ = RefreshFolderAsync(source);
    }

    private static void ResetLinkedFolders()
    {
        lock (LinkedWriteGate)
        lock (LinkedGate)
        {
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
        EnsureLinkedFolders();
        lock (LinkedGate)
        {
            if (!Linked.TryGetValue(source, out var folder)) return Task.CompletedTask;
            if (folder.Scan is { IsCompleted: false }) return folder.Scan;
            folder.NextScan = DateTime.UtcNow.AddSeconds(10);
            return folder.Scan = Task.Run(async () =>
            {
                // A stalled share must not occupy the local-directory scan slots.
                bool network = folder.Path.StartsWith(@"\\", StringComparison.Ordinal)
                    || new DriveInfo(Path.GetPathRoot(folder.Path)!).DriveType == DriveType.Network;
                var slots = network ? NetworkScanSlots : ScanSlots;
                await slots.WaitAsync();
                try { ScanLinkedFolder(folder); }
                finally { slots.Release(); }
            });
        }
    }

    private static void ScanLinkedFolder(LinkedFolder folder)
    {
        var items = new List<CaptureItem>();
        bool available = false;
        try
        {
            var directory = new DirectoryInfo(folder.Path);
            if (!directory.Exists) throw new DirectoryNotFoundException();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
            foreach (var file in directory.EnumerateFiles("*", options))
            {
                if (!Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                lock (LinkedGate)
                    if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) return;
                try
                {
                    lock (_gate)
                        if (_sizes.TryGetValue(file.FullName, out var cached) && cached.Stamp != file.LastWriteTimeUtc)
                        {
                            _sizes.Remove(file.FullName);
                            if (_ids.TryGetValue(file.FullName, out long id)) RemoveThumb(id);
                        }
                    var (width, height) = SizeOf(file);
                    if (width == 0 || height == 0) continue;
                    items.Add(new CaptureItem(IdOf(file.FullName), file.LastWriteTime, width, height,
                        "file", Path.GetFileNameWithoutExtension(file.Name), null, CollectionId, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            available = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        bool changed;
        lock (LinkedGate)
        {
            if (!Linked.TryGetValue(folder.Source, out var current) || !ReferenceEquals(current, folder)) return;
            var ordered = items.OrderByDescending(item => item.Taken).ThenBy(item => item.Id).ToArray();
            changed = !folder.Initialized || folder.Available != available || !folder.Items.SequenceEqual(ordered);
            var retained = ordered.Select(item => item.Id).ToHashSet();
            foreach (long removed in folder.Items.Select(item => item.Id).Where(id => !retained.Contains(id)
                && !Linked.Values.Any(other => !ReferenceEquals(other, folder) && other.Items.Any(item => item.Id == id))))
                lock (_gate)
                    if (_paths.Remove(removed, out string? path))
                    {
                        _ids.Remove(path);
                        _sizes.Remove(path);
                        RemoveThumb(removed);
                    }
            folder.Items = ordered;
            folder.Available = available;
            folder.Initialized = true;
        }
        if (changed) Changed?.Invoke();
    }
}
