using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    public static event Action<CaptureItem>? ThumbnailReady;
    private static readonly ConcurrentDictionary<CaptureItem, TaskCompletionSource<BitmapSource?>> PendingThumbnails = new();
    private static readonly Channel<CaptureItem> LocalThumbnails = CreateThumbnailQueue();
    private static readonly Channel<CaptureItem> NetworkThumbnails = CreateThumbnailQueue();

    private static Channel<CaptureItem> CreateThumbnailQueue()
    {
        var queue = Channel.CreateBounded<CaptureItem>(256);
        for (int n = 0; n < 2; n++)
            _ = Task.Run(async () =>
            {
                await foreach (var item in queue.Reader.ReadAllAsync())
                {
                    if (!PendingThumbnails.TryGetValue(item, out var completion)) continue;
                    try
                    {
                        var image = LoadThumb(item);
                        completion.TrySetResult(image);
                        if (image != null && IsCurrent(item)) ThumbnailReady?.Invoke(item);
                    }
                    catch (Exception) { completion.TrySetResult(null); }
                    finally { PendingThumbnails.TryRemove(item, out _); }
                }
            });
        return queue;
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
            var queue = folder && FolderCaptures.IsNetworkItem(item.Id) ? NetworkThumbnails : LocalThumbnails;
            if (!queue.Writer.TryWrite(item))
            {
                PendingThumbnails.TryRemove(item, out _);
                completion.TrySetResult(null);
            }
            return completion.Task;
        }
    }
}
