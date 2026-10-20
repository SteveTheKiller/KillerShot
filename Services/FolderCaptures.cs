using System.IO;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

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
    private static readonly Dictionary<long, BitmapSource> _thumbs = [];
    private static readonly LinkedList<long> _thumbOrder = new();
    private static readonly Dictionary<long, LinkedListNode<long>> _thumbNodes = [];
    private const int ThumbCacheSize = 128;
    private static long _nextId = -2;
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;
    private static string? _folderOverride;

    /// <summary>A file was added, removed, renamed or changed in the folder.</summary>
    public static event Action? Changed;

    public static string Folder => _folderOverride ?? OutputFiles.DefaultFolder;

    /// <summary>The folder's name as the collection title.</summary>
    public static string Name => Path.GetFileName(Folder.TrimEnd('\\')) is { Length: > 0 } n ? n : Folder;

    /// <summary>Shown as a collection unless turned off in Capture settings.</summary>
    public static bool Enabled
    {
        get => Settings.GetBool("LibraryShowFolder", true);
        set { Settings.SetBool("LibraryShowFolder", value); Changed?.Invoke(); }
    }

    /// <summary>For the self-test: another folder in place of the user's.</summary>
    internal static void UseFolder(string dir)
    {
        ResetLinkedFolders();
        lock (_gate)
        {
            _folderOverride = dir;
            _watcher?.Dispose();
            _watcher = null;
            _ids.Clear(); _paths.Clear(); _sizes.Clear();
            _thumbs.Clear(); _thumbOrder.Clear(); _thumbNodes.Clear();
        }
    }

    public static bool IsFolderId(long id) => id < 0;

    public static string? PathOf(long id) { lock (_gate) return _paths.GetValueOrDefault(id); }

    /// <summary>The pictures in the folder, newest first, matching every word of a search in their
    /// file name. Empty when the folder is turned off or missing.</summary>
    public static IReadOnlyList<CaptureItem> Items(string? search = null)
    {
        if (!Enabled) return [];
        string folder = Folder;
        FileInfo[] files;
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists)
            {
                Reconcile([]);
                return [];
            }
            files = dir.GetFiles().Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        catch { return []; }
        Reconcile(files);
        Watch(folder);
        string[] words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<CaptureItem>();
        foreach (var f in files.OrderByDescending(f => f.LastWriteTime))
        {
            string name = Path.GetFileNameWithoutExtension(f.Name);
            if (words.Any(w => !name.Contains(w, StringComparison.CurrentCultureIgnoreCase))) continue;
            var (w, h) = SizeOf(f);
            list.Add(new CaptureItem(IdOf(f.FullName), f.LastWriteTime, w, h, "file", name, null, CollectionId, null));
        }
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
        lock (_gate)
        {
            if (_thumbs.TryGetValue(id, out var t))
            {
                var node = _thumbNodes[id];
                _thumbOrder.Remove(node);
                _thumbOrder.AddLast(node);
                return t;
            }
        }
        if (PathOf(id) is not { } path) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 512;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bmp.StreamSource = stream;
                bmp.EndInit();
            }
            bmp.Freeze();
            lock (_gate)
            {
                if (!_paths.TryGetValue(id, out string? current) || !string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                    return null;
                RemoveThumb(id);
                _thumbs[id] = bmp;
                _thumbNodes[id] = _thumbOrder.AddLast(id);
                while (_thumbOrder.Count > ThumbCacheSize) RemoveThumb(_thumbOrder.First!.Value);
            }
            return bmp;
        }
        catch { return null; }
    }

    private static void RemoveThumb(long id)
    {
        _thumbs.Remove(id);
        if (_thumbNodes.Remove(id, out var node)) _thumbOrder.Remove(node);
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
            if (_ids.TryGetValue(path, out long id)) RemoveThumb(id);
            _sizes.Remove(path);
            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(), null, 400, Timeout.Infinite);
        }
    }
}
