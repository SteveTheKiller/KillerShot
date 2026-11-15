using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

/// <summary>Seams for the service self-test (Shell/SelfTestServiceFixes.cs). Each is null or
/// unused in normal runs; the self-test sets them on a scratch album only.</summary>
internal static partial class CaptureLibrary
{
    /// <summary>Called after each piece of a recording import is written, with the bytes so far.</summary>
    internal static Action<long>? VideoChunkWrittenForSelfTest { get; set; }

    /// <summary>Called inside an album rename after the renamed album is published, so the
    /// self-test can fail the rename at its latest point.</summary>
    internal static Action? RenameFaultForSelfTest { get; set; }

    /// <summary>Called during an album import after the source has been copied.</summary>
    internal static Action? ImportCopiedForSelfTest { get; set; }

    /// <summary>The open album's key, so the self-test can read the file after a refused open.</summary>
    internal static string? CurrentKeyForSelfTest()
    {
        lock (_gate) return _db == null ? null : new SqliteConnectionStringBuilder(_db.ConnectionString).Password;
    }

    /// <summary>Reads, and optionally first writes, one meta value straight from the active album
    /// file with its own connection, bypassing the open checks.</summary>
    internal static string? RawMetaForSelfTest(string key, string metaKey, string? write = null)
    {
        string path;
        lock (_gate) path = FilePath;
        var builder = new SqliteConnectionStringBuilder(Cs(path, key, false)) { Pooling = false };
        using var db = new SqliteConnection(builder.ConnectionString);
        db.Open();
        if (write != null)
        {
            using var set = db.CreateCommand();
            set.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            set.Parameters.AddWithValue("$k", metaKey);
            set.Parameters.AddWithValue("$v", write);
            set.ExecuteNonQuery();
        }
        using var get = db.CreateCommand();
        get.CommandText = "SELECT CAST(value AS TEXT) FROM meta WHERE key = $k";
        get.Parameters.AddWithValue("$k", metaKey);
        return get.ExecuteScalar() as string;
    }
}
