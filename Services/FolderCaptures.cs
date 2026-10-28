using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

internal interface IFolderDirectoryReader
{
    IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory, EnumerationOptions options);
    IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory, EnumerationOptions options);
}

internal sealed class SystemFolderDirectoryReader : IFolderDirectoryReader
{
    public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory, EnumerationOptions options) => directory.EnumerateFiles("*", options);
    public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory, EnumerationOptions options) => directory.EnumerateDirectories("*", options);
}

/// <summary>
/// The Windows Screenshots folder (Pictures\Screenshots), shown in the library as a live
/// collection read from the files as they are. Nothing is copied into the encrypted library, and
/// the files are never changed. Its pictures carry negative ids, so the library's thumbnail and
/// image calls can tell them from stored captures and send them here.
/// </summary>
internal static partial class FolderCaptures
{
    /// <summary>The CollectionId every folder picture carries.</summary>
    public const long CollectionId = -1;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
    private static readonly object _gate = new();
    private static readonly Dictionary<string, long> _ids = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<long, string> _paths = [];
    private static readonly Dictionary<string, (DateTime Stamp, int W, int H)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private static long _nextId = -2;
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;
    private static string? _folderOverride;
    private static IFolderDirectoryReader _directoryReader = new SystemFolderDirectoryReader();
    private static bool _folderAvailable = true;
    private static IReadOnlyList<CaptureItem> _screenshots = [];
    private static Task? _screenshotsScan;
    private static DateTime _nextScreenshotsScan;
    private static bool _screenshotsLoading;
    private static int _screenshotsGeneration;

    /// <summary>A file was added, removed, renamed or changed in the folder.</summary>
    public static event Action? Changed;

    public static string Folder => _folderOverride ?? OutputFiles.DefaultFolder;

    /// <summary>The folder's name as the collection title.</summary>
    public static string Name => Path.GetFileName(Folder.TrimEnd('\\')) is { Length: > 0 } n ? n : Folder;

    public static FolderCollection? StatusForSource(string source)
    {
        if (source != "folder") return LinkedCollections().FirstOrDefault(folder => folder.Source == source);
        lock (_gate) return new FolderCollection(source, Folder, _folderAvailable, _screenshotsLoading);
    }

    /// <summary>Shown as a collection unless turned off in Capture settings.</summary>
    public static bool Enabled
    {
        get => Settings.GetBool("LibraryShowFolder", true);
        set { Settings.SetBool("LibraryShowFolder", value); Changed?.Invoke(); }
    }

    /// <summary>For the self-test: another folder in place of the user's.</summary>
    internal static void UseFolder(string dir, IFolderDirectoryReader? directoryReader = null)
    {
        ResetLinkedFolders();
        lock (_gate)
        {
            _folderOverride = dir;
            _directoryReader = directoryReader ?? new SystemFolderDirectoryReader();
            _folderAvailable = true;
            _screenshots = [];
            _screenshotsScan = null;
            _nextScreenshotsScan = DateTime.MinValue;
            _screenshotsLoading = false;
            _screenshotsGeneration++;
            _watcher?.Dispose();
            _watcher = null;
            _ids.Clear(); _paths.Clear(); _sizes.Clear();
            ThumbnailCache.Clear(folders: true);
        }
    }

    public static bool IsFolderId(long id) => id < 0;

    public static string? PathOf(long id) { lock (_gate) return _paths.GetValueOrDefault(id); }

    /// <summary>Cached Screenshots entries for UI callers. Directory and image-header work shares
    /// the linked-folder local/network limits and never runs on the caller's dispatcher.</summary>
    public static IReadOnlyList<CaptureItem> Snapshot(string? search = null)
    {
        if (!Enabled) return [];
        IReadOnlyList<CaptureItem> items;
        lock (_gate)
        {
            if (DateTime.UtcNow >= _nextScreenshotsScan) _ = RefreshScreenshotsAsync();
            items = _screenshots;
        }
        string[] words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 ? items : items.Where(item => words.All(word =>
            (item.Window ?? "").Contains(word, StringComparison.CurrentCultureIgnoreCase))).ToArray();
    }

    public static Task RefreshScreenshotsAsync()
    {
        lock (_gate)
        {
            if (_screenshotsScan is { IsCompleted: false }) return _screenshotsScan;
            int generation = _screenshotsGeneration;
            _nextScreenshotsScan = DateTime.UtcNow.AddSeconds(10);
            _screenshotsLoading = true;
            return _screenshotsScan = Task.Run(async () =>
            {
                SemaphoreSlim? slots = null;
                try
                {
                    string path = Folder;
                    bool network = path.StartsWith(@"\\", StringComparison.Ordinal)
                        || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network;
                    slots = network ? NetworkScanSlots : ScanSlots;
                    await slots.WaitAsync();
                    var items = Items();
                    lock (_gate) if (generation == _screenshotsGeneration) _screenshots = items;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    lock (_gate) if (generation == _screenshotsGeneration) _folderAvailable = false;
                }
                finally
                {
                    slots?.Release();
                    lock (_gate) if (generation == _screenshotsGeneration) _screenshotsLoading = false;
                    Changed?.Invoke();
                }
            });
        }
    }

    /// <summary>The pictures in the folder, newest first, matching every word of a search in their
    /// file name. Empty when the folder is turned off or missing.</summary>
    public static IReadOnlyList<CaptureItem> Items(string? search = null)
    {
        if (!Enabled) return [];
        string folder = Folder;
        var found = new List<FileInfo>();
        bool available = true;
        try
        {
            var dir = new DirectoryInfo(folder);
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
            foreach (var file in ReadDirectoryEntries(() => _directoryReader.EnumerateFiles(dir, options), error =>
                { if (error is not DirectoryNotFoundException || found.Count != 0) available = false; }))
                if (Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) found.Add(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { available = false; }
        bool statusChanged;
        lock (_gate) { statusChanged = _folderAvailable != available; _folderAvailable = available; }
        var files = found.ToArray();
        if (available) { Reconcile(files); Watch(folder); }
        if (statusChanged) Changed?.Invoke();
        string[] words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<CaptureItem>();
        foreach (var f in files.OrderByDescending(f => f.LastWriteTime))
        {
            string name = Path.GetFileNameWithoutExtension(f.Name);
            if (words.Any(w => !name.Contains(w, StringComparison.CurrentCultureIgnoreCase))) continue;
            var (w, h) = SizeOf(f);
            list.Add(new CaptureItem(IdOf(f.FullName), f.LastWriteTime, w, h, "file", name, null, CollectionId, null));
        }
        if (search == null) lock (_gate) _screenshots = list;
        return list;
    }

    /// <summary>Prunes only after a complete scan, including files hidden by the search.</summary>
    private static void Reconcile(FileInfo[] files)
    {
        var current = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (string path in _ids.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase)
                && !current.Contains(path)).ToArray())
            {
                long id = _ids[path];
                _ids.Remove(path);
                _paths.Remove(id);
                _sizes.Remove(path);
                RemoveThumb(id);
            }
            foreach (string path in _sizes.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase)
                && !current.Contains(path)).ToArray())
                _sizes.Remove(path);
            foreach (FileInfo file in files)
            {
                if (!_sizes.TryGetValue(file.FullName, out var size) || size.Stamp == file.LastWriteTimeUtc) continue;
                _sizes.Remove(file.FullName);
                if (_ids.TryGetValue(file.FullName, out long id)) RemoveThumb(id);
            }
        }
    }

    private static long IdOf(string path)
    {
        lock (_gate)
        {
            if (_ids.TryGetValue(path, out long id)) return id;
            id = _nextId--;
            _ids[path] = id;
            _paths[id] = path;
            return id;
        }
    }

    /// <summary>The pixel size from the file's header, cached until the file changes.</summary>
    private static (int, int) SizeOf(FileInfo f)
    {
        lock (_gate)
            if (_sizes.TryGetValue(f.FullName, out var s) && s.Stamp == f.LastWriteTimeUtc) return (s.W, s.H);
        int w = 0, h = 0;
        try
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            w = frame.PixelWidth; h = frame.PixelHeight;
        }
        catch { }
        if (w > 0 && h > 0)
            lock (_gate) _sizes[f.FullName] = (f.LastWriteTimeUtc, w, h);
        return (w, h);
    }

    public static BitmapSource? LoadThumb(long id)
    {
        if (ThumbnailCache.Get(0, id) is { } cached) return cached;
        if (PathOf(id) is not { } path) return null;
        try
        {
            var file = new FileInfo(path);
            DateTime stamp = file.LastWriteTimeUtc;
            long length = file.Length;
            var (width, height) = SizeOf(new FileInfo(path));
            if (width <= 0 || height <= 0) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height) bmp.DecodePixelWidth = Math.Min(512, width);
            else bmp.DecodePixelHeight = Math.Min(512, height);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bmp.StreamSource = stream;
                bmp.EndInit();
            }
            bmp.Freeze();
            file.Refresh();
            if (!file.Exists || file.LastWriteTimeUtc != stamp || file.Length != length) return null;
            lock (_gate)
            {
                if (!_paths.TryGetValue(id, out string? current) || !string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                    return null;
                RemoveThumb(id);
                ThumbnailCache.Put(0, id, bmp);
            }
            return bmp;
        }
        catch { return null; }
    }

    private static void RemoveThumb(long id)
    {
        ThumbnailCache.Remove(0, id);
    }

    public static BitmapSource LoadImage(long id) =>
        ImageOutput.Load(PathOf(id) ?? throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_Missing")));

    public static byte[] LoadPng(long id)
    {
        string path = PathOf(id) ?? throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_Missing"));
        return Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(path)
            : ImageOutput.EncodePng(ImageOutput.Load(path));
    }

    /// <summary>Whether a delete in this folder can go to the Recycle Bin. A network share or a
    /// removable drive has none, and Windows would delete the file for good without a word, so
    /// only a local fixed drive whose Recycle Bin answers counts (audit BF-001, 2026-10-27).</summary>
    public static bool CanRecycle(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBinW(root, ref info) == 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>Sends a folder picture to the Recycle Bin, so a mistaken delete can be undone from
    /// there, and rescans the collections that showed it. Call it only where CanRecycle says the
    /// folder has a Recycle Bin; if Windows still cannot recycle the file (too large, or the bin is
    /// turned off) it asks before destroying it rather than doing so silently.</summary>
    public static void Recycle(long id, IntPtr owner = default)
    {
        string path = PathOf(id) ?? throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_Missing"));
        var operation = new ShFileOperation
        {
            Window = owner,
            Func = FoDelete,
            From = path + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
        };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.AnyOperationsAborted || File.Exists(path))
            throw new IOException(new System.ComponentModel.Win32Exception(result == 0 ? 5 : result).Message);
        Forget(path);
    }

    /// <summary>Deletes a folder picture for good, for a folder with no Recycle Bin, after the
    /// user has confirmed exactly that. Off the interface thread, so a slow share cannot freeze it.</summary>
    public static async Task DeleteForGoodAsync(long id)
    {
        string path = PathOf(id) ?? throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_Missing"));
        await Task.Run(() => File.Delete(path));
        if (File.Exists(path)) throw new IOException(new System.ComponentModel.Win32Exception(5).Message);
        Forget(path);
    }

    /// <summary>After a delete: the collections that showed the file read their folders again.</summary>
    private static void Forget(string path)
    {
        Touched(path);
        _ = RefreshScreenshotsAsync();
        string[] sources;
        lock (LinkedGate) sources = Linked.Values.Where(folder => path.StartsWith(folder.Path.TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase)).Select(folder => folder.Source).ToArray();
        foreach (string source in sources) _ = RefreshFolderAsync(source);
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string rootPath, ref ShQueryRbInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOperation operation);

    /// <summary>Watches the folder so the collection follows new and removed files.</summary>
    private static void Watch(string folder)
    {
        lock (_gate)
        {
            if (_watcher != null && string.Equals(_watcher.Path, folder, StringComparison.OrdinalIgnoreCase)) return;
            _watcher?.Dispose();
            try
            {
                _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                FileSystemEventHandler on = (_, e) => Touched(e.FullPath);
                _watcher.Created += on; _watcher.Deleted += on; _watcher.Changed += on;
                _watcher.Renamed += (_, e) => { Touched(e.OldFullPath); Touched(e.FullPath); };
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }
        }
    }

    private static void Touched(string path)
    {
        lock (_gate)
        {
            _nextScreenshotsScan = DateTime.MinValue;
            if (_ids.TryGetValue(path, out long id)) RemoveThumb(id);
            _sizes.Remove(path);
            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(), null, 400, Timeout.Infinite);
        }
    }

    private static IEnumerable<T> ReadDirectoryEntries<T>(Func<IEnumerable<T>> enumerate, Action<Exception> failed) where T : class
    {
        IEnumerator<T>? iterator = null;
        try { iterator = enumerate().GetEnumerator(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failed(error); }
        if (iterator == null) yield break;
        using (iterator)
        {
            while (true)
            {
                bool moved = false;
                T? value = null;
                try { moved = iterator.MoveNext(); if (moved) value = iterator.Current; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { moved = false; failed(error); }
                if (!moved) break;
                yield return value!;
            }
        }
    }
}
