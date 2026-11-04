using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

/// <summary>A recording too large for one SQLite value. It stays in its folder.</summary>
internal sealed class VideoTooLargeException(long size, long limit)
    : IOException($"The recording is {size} bytes and the library takes at most {limit}.")
{
    public long Size { get; } = size;
    public long Limit { get; } = limit;
}

/// <summary>
/// Recordings kept in the encrypted library. The captures row holds a poster frame as its image
/// and thumbnail, so every view draws it like a screenshot; the clip itself is in
/// capture_videos, written and read through SQLite's incremental blob I/O so a long recording is
/// never held in memory whole. One value is capped by SQLITE_MAX_LENGTH, which MaxVideoBytes
/// reads from the open connection rather than assuming the stock default.
/// </summary>
internal static partial class CaptureLibrary
{
    private const string VideosSchema = """
        CREATE TABLE IF NOT EXISTS capture_videos(
            capture_id INTEGER PRIMARY KEY REFERENCES captures(id) ON DELETE CASCADE,
            ext TEXT NOT NULL,
            duration_ticks INTEGER NOT NULL,
            data BLOB NOT NULL);
        """;

    private const int PosterLongSide = 1920;
    private static int _videoOperations;
    public static bool VideoBusy => Volatile.Read(ref _videoOperations) != 0;
    private static void RequireVideoIdle()
    {
        if (VideoBusy) throw new InvalidOperationException(Loc("Str_Rec_Saving"));
    }

    private sealed class VideoOperation : IDisposable
    {
        public VideoOperation() { Interlocked.Increment(ref _videoOperations); RaiseChanged(); }
        public void Dispose() { Interlocked.Decrement(ref _videoOperations); RaiseChanged(); }
    }

    /// <summary>For the self-test: a smaller cap, so the over-limit path runs without a 1 GB file.</summary>
    internal static long? MaxVideoBytesOverride { get; set; }

    /// <summary>The largest clip one row can hold: SQLITE_LIMIT_LENGTH on the open library.</summary>
    public static long MaxVideoBytes
    {
        get
        {
            if (MaxVideoBytesOverride is long limit) return limit;
            lock (_gate) return SQLitePCL.raw.sqlite3_limit(Db.Handle, 0, -1);
        }
    }

    /// <summary>Copies a recording into the library and gives its id. The file is left alone; the
    /// caller decides whether to remove it. Throws VideoTooLargeException over MaxVideoBytes.</summary>
    public static long AddVideo(string path, DateTime taken, string? title, long? collectionId = null,
        long? expectedSession = null, bool recordingsCollection = false)
    {
        long session = expectedSession ?? Session;
        using var operation = new VideoOperation();
        long size = new FileInfo(path).Length;
        long limit = MaxVideoBytes;
        if (size > limit) throw new VideoTooLargeException(size, limit);
        var (width, height, duration) = VideoCaptures.Probe(path);
        var poster = VideoCaptures.Poster(path, PosterLongSide) ?? BlankPoster(width, height);
        if (width <= 0 || height <= 0) (width, height) = (poster.PixelWidth, poster.PixelHeight);
        byte[] image = ImageOutput.EncodePng(poster);
        byte[] thumb = EncodeThumb(poster);
        long id;
        lock (PendingWriter)
        {
            // The clip can be a gigabyte, so it is written through its own connection, outside the
            // gate every listing and edit takes. The album keeps a write-ahead log, so readers see
            // the album as it was until this commits, and the new row appears whole or not at all.
            string connectionString;
            lock (_gate)
            {
                RequireSession(session);
                if (recordingsCollection) collectionId = RecordingsCollectionCore(notify: false);
                connectionString = new SqliteConnectionStringBuilder(Db.ConnectionString) { Pooling = false }.ConnectionString;
            }
            using var album = new SqliteConnection(connectionString);
            album.Open();
            Exec(album, "PRAGMA foreign_keys = ON");
            using var transaction = album.BeginTransaction();
            id = AllocateCaptureId(album, transaction);
            using (var cmd = album.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO captures(id, taken_utc, width, height, mode, window_title, collection_id, thumb, image)
                    VALUES ($id, $t, $w, $h, $m, $win, $c, $thumb, $image);
                    INSERT INTO capture_videos(capture_id, ext, duration_ticks, data) VALUES ($id, $ext, $d, zeroblob($n));
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$t", taken.ToUniversalTime().Ticks);
                cmd.Parameters.AddWithValue("$w", width);
                cmd.Parameters.AddWithValue("$h", height);
                cmd.Parameters.AddWithValue("$m", VideoCaptures.Mode);
                cmd.Parameters.AddWithValue("$win", (object?)Trimmed(title) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)collectionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$thumb", thumb);
                cmd.Parameters.AddWithValue("$image", image);
                cmd.Parameters.AddWithValue("$ext", Path.GetExtension(path).ToLowerInvariant());
                cmd.Parameters.AddWithValue("$d", duration.Ticks);
                cmd.Parameters.AddWithValue("$n", size);
                cmd.ExecuteNonQuery();
            }
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var blob = new SqliteBlob(album, "capture_videos", "data", id))
                source.CopyTo(blob, 1 << 20);
            IndexText(id, transaction);
            transaction.Commit();
        }
        RaiseChanged();
        return id;
    }

    /// <summary>Writes a stored clip out to a file, byte for byte, through a temporary name so a
    /// failed copy never leaves half a video at the destination.</summary>
    public static void ExportVideo(long id, string destination, long? expectedSession = null)
    {
        using var operation = new VideoOperation();
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            lock (PendingWriter)
            {
                string connectionString;
                lock (_gate)
                {
                    RequireSession(expectedSession);
                    connectionString = new SqliteConnectionStringBuilder(Db.ConnectionString)
                    { Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ConnectionString;
                }
                using var album = new SqliteConnection(connectionString);
                album.Open();
                using var blob = new SqliteBlob(album, "capture_videos", "data", id, readOnly: true);
                using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                blob.CopyTo(target, 1 << 20);
                target.Flush(true);
            }
            File.Move(temp, destination, true);
        }
        catch (SqliteException) { TryDeleteFile(temp); throw new InvalidOperationException(Loc("Str_Lib_Missing")); }
        catch { TryDeleteFile(temp); throw; }
    }

    /// <summary>The stored clip's file extension with its dot, such as ".mp4".</summary>
    public static string VideoExtension(long id) => VideoExtension(id, null);

    public static string VideoExtension(long id, long? expectedSession)
    {
        lock (_gate)
        {
            RequireSession(expectedSession);
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT ext FROM capture_videos WHERE capture_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string ?? throw new InvalidOperationException(Loc("Str_Lib_Missing"));
        }
    }

    internal static (long Session, long Revision, string Extension) VideoPlaybackIdentity(long id)
    {
        lock (_gate) return (_session, MarksRevision(id), VideoExtension(id));
    }

    /// <summary>A stored clip's length, or zero when Windows could not read it at import.</summary>
    public static TimeSpan VideoDuration(long id)
    {
        lock (_gate)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT duration_ticks FROM capture_videos WHERE capture_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is long ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
        }
    }

    /// <summary>The album's Recordings collection, made the first time a recording is stored and
    /// made again if the user deleted it. Its id lives in the album, so each album has its own.</summary>
    public static long RecordingsCollection() => RecordingsCollectionCore(notify: true);

    private static long RecordingsCollectionCore(bool notify)
    {
        long id;
        lock (_gate)
        {
            using var transaction = Db.BeginTransaction();
            using var read = Db.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT c.id FROM meta m JOIN collections c ON c.id = CAST(m.value AS INTEGER)
                WHERE m.key = 'recordings_collection'
                """;
            if (read.ExecuteScalar() is long existing) return existing;
            using var create = Db.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = "INSERT INTO collections(name,created_utc) VALUES($name,$taken); SELECT last_insert_rowid();";
            create.Parameters.AddWithValue("$name", Loc("Str_Rec_CollectionName"));
            create.Parameters.AddWithValue("$taken", DateTime.UtcNow.Ticks);
            id = (long)create.ExecuteScalar()!;
            using var write = Db.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ('recordings_collection', $id)";
            write.Parameters.AddWithValue("$id", id);
            write.ExecuteNonQuery();
            transaction.Commit();
        }
        if (notify) RaiseChanged();
        return id;
    }

    private static long AllocateCaptureId(SqliteConnection album, SqliteTransaction transaction)
    {
        // Keep a persistent high-water mark. SQLite's implicit row IDs can reuse a
        // deleted capture while OCR or QR recognition still holds its old identity.
        using var allocate = album.CreateCommand();
        allocate.Transaction = transaction;
        allocate.CommandText = """
            UPDATE meta SET value = CAST(value AS INTEGER) + 1
            WHERE key = 'capture_id' AND CAST(value AS INTEGER) < 9223372036854775807
            RETURNING CAST(value AS INTEGER);
            """;
        return Convert.ToInt64(allocate.ExecuteScalar()
            ?? throw new InvalidOperationException("The album cannot allocate another capture ID."));
    }

    /// <summary>A dark frame at the clip's shape, for a recording Windows makes no thumbnail for.</summary>
    private static BitmapSource BlankPoster(int width, int height)
    {
        double scale = width > 0 && height > 0 ? Math.Min(1.0, 640.0 / Math.Max(width, height)) : 1;
        int w = width > 0 && height > 0 ? Math.Max(1, (int)(width * scale)) : 640;
        int h = width > 0 && height > 0 ? Math.Max(1, (int)(height * scale)) : 360;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0x20; pixels[i + 3] = 0xFF; }
        var blank = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        blank.Freeze();
        return blank;
    }
}
