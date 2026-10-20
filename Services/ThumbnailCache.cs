using System.Windows.Media.Imaging;

namespace KillerShot.Services;

internal static class ThumbnailCache
{
    private const long Budget = 64L * 1024 * 1024;
    private static readonly object Gate = new();
    private sealed record Entry(BitmapSource Image, long Bytes, LinkedListNode<(long Session, long Id)> Node);
    private static readonly Dictionary<(long Session, long Id), Entry> Entries = [];
    private static readonly LinkedList<(long Session, long Id)> Order = [];
    private static long _bytes;

    public static BitmapSource? Get(long session, long id)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue((session, id), out var entry)) return null;
            Order.Remove(entry.Node);
            Order.AddLast(entry.Node);
            return entry.Image;
        }
    }

    public static void Put(long session, long id, BitmapSource image)
    {
        long bytes = ((long)image.PixelWidth * image.Format.BitsPerPixel + 7) / 8 * image.PixelHeight;
        lock (Gate)
        {
            Remove(session, id);
            if (bytes > Budget) return;
            var key = (session, id);
            Entries[key] = new Entry(image, bytes, Order.AddLast(key));
            _bytes += bytes;
            while (_bytes > Budget && Order.First is { } first) Remove(first.Value.Session, first.Value.Id);
        }
    }

    public static void Remove(long session, long id)
    {
        lock (Gate)
            if (Entries.Remove((session, id), out var entry))
            {
                Order.Remove(entry.Node);
                _bytes -= entry.Bytes;
            }
    }

    public static void Clear(bool folders)
    {
        lock (Gate)
            foreach (var key in Entries.Keys.Where(key => (key.Id < 0) == folders).ToArray()) Remove(key.Session, key.Id);
    }
}
