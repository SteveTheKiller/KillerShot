using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    public static event Action<CaptureItem>? ThumbnailReady;
    private static readonly ConcurrentDictionary<CaptureItem, TaskCompletionSource<BitmapSource?>> PendingThumbnails = new();
    private static readonly ThumbnailQueue LocalThumbnails = new();
    private static readonly ThumbnailQueue NetworkThumbnails = new();

    /// <summary>Newest request first. A fast drag through a large folder asks for every picture it
    /// passes; the ones it lands on must not wait behind the ones it flew past. Past the limit the
    /// oldest requests are dropped and forgotten, so the next request for them starts over.</summary>
    private sealed class ThumbnailQueue
    {
        private const int Limit = 256;
        private readonly LinkedList<CaptureItem> _items = new();
        private readonly SemaphoreSlim _ready = new(0);

        public ThumbnailQueue()
        {
            for (int n = 0; n < 2; n++) _ = Task.Run(WorkAsync);
        }

        public void Add(CaptureItem item)
        {
            CaptureItem? dropped = null;
            lock (_items)
            {
                _items.AddFirst(item);
                if (_items.Count > Limit) { dropped = _items.Last!.Value; _items.RemoveLast(); }
                else _ready.Release();
            }
            if (dropped != null && PendingThumbnails.TryRemove(dropped, out var stale)) stale.TrySetResult(null);
        }

        private async Task WorkAsync()
        {
            while (true)
            {
                await _ready.WaitAsync();
                CaptureItem item;
                lock (_items)
                {
                    if (_items.First == null) continue;
                    item = _items.First.Value;
                    _items.RemoveFirst();
                }
                if (!PendingThumbnails.TryGetValue(item, out var completion)) continue;
                try
                {
                    var image = LoadThumb(item);
                    completion.TrySetResult(image);
                    if (image != null && IsCurrent(item)) ThumbnailReady?.Invoke(item);
                }
                catch (Exception) { completion.TrySetResult(null); }
                finally { PendingThumbnails.TryRemove(new KeyValuePair<CaptureItem, TaskCompletionSource<BitmapSource?>>(item, completion)); }
            }
        }
    }

    public static BitmapSource? RequestThumb(CaptureItem item)
    {
        bool folder = FolderCaptures.IsFolderId(item.Id);
        if (!folder && item.Session != Volatile.Read(ref _session)) return null;
        var cached = ThumbnailCache.Get(folder ? 0 : item.Session, item.Id);
        if (cached != null) return cached;
        _ = LoadThumbAsync(item);
        return null;
    }

    public static Task<BitmapSource?> LoadThumbAsync(CaptureItem item)
    {
        bool folder = FolderCaptures.IsFolderId(item.Id);
        if (!folder && item.Session != Volatile.Read(ref _session)) return Task.FromResult<BitmapSource?>(null);
        if (ThumbnailCache.Get(folder ? 0 : item.Session, item.Id) is { } cached) return Task.FromResult<BitmapSource?>(cached);
        while (true)
        {
            if (PendingThumbnails.TryGetValue(item, out var pending)) return pending.Task;
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!PendingThumbnails.TryAdd(item, completion)) continue;
            (folder && FolderCaptures.IsNetworkItem(item.Id) ? NetworkThumbnails : LocalThumbnails).Add(item);
            return completion.Task;
        }
    }
}
