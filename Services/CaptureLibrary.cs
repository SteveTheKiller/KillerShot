using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

/// <summary>One capture in the library, without its pixels.</summary>
internal sealed record CaptureItem(long Id, DateTime Taken, int Width, int Height, string Mode,
    string? Window, string? Process, long? CollectionId, string? Note, bool HasQr = false);

/// <summary>A collection (a folder inside the library) and how many captures it holds.</summary>
internal sealed record CaptureCollection(long Id, string Name, int Count);

/// <summary>
/// Every capture KillerShot keeps, in one SQLCipher database (Library.kshot, AES-256) under
/// %LOCALAPPDATA%\KillerShot. Each row holds the full PNG, a thumbnail, when it was taken, its
/// size and mode, the window and program it came from, its collection, a note, and the text
/// Windows OCR read from it, which feeds the FTS5 search index.
///
/// THE KEY. By default the database key is a random 256-bit key sealed with DPAPI to the Windows
/// account (Library.key), so the library opens without a prompt and is unreadable to anyone
/// else, on this PC or off it. With a password set, the database is keyed with the password
/// instead and Library.key is removed, so nothing opens it without the password. Both changes go
/// through sqlcipher_export (CaptureLibrary.Password.cs), never PRAGMA rekey.
///
/// The SQLCipher rules from the family apply (KillerHarness "SQLCipher: two traps"): the static
/// provider with the vendored native, the key in the connection string and never as a PRAGMA
/// after Open, a forced key check right after Open, and ClearAllPools before any file swap.
///
/// One connection serves the whole app; every use of it holds _gate. Writes that encode images
/// run off the UI thread. Changed is raised on whichever thread made the change.
/// </summary>
internal static partial class CaptureLibrary
{
    // The provider installs once per process, before the first open, and the native has to be on
    // the loader path first. raw.SetProvider, not Batteries_V2.Init().
    static CaptureLibrary()
    {
        SqlCipherBootstrap.EnsureLoaded();
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
    }

    private const int SchemaVersion = 2;
    private const int ThumbLongSide = 512;
    private static readonly byte[] KeyEntropy = "KillerShot.Library.v1"u8.ToArray();

    private static readonly object _gate = new();
    private static SqliteConnection? _db;
    private static string _folder = DefaultFolder;
    private static bool _keyedByPassword;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot");

    public static string FilePath => Path.Combine(_folder, "Library.kshot");
    private static string KeyPath => Path.Combine(_folder, "Library.key");

    /// <summary>The old plain-PNG history folder that the first open imports and empties.</summary>
    private static string _recentFolder = RecentCaptures.Folder;

    /// <summary>Points the library at another folder. Used by the self-tests and the demo, so
    /// neither touches the user's captures. The old history folder moves with it, to a Recent
    /// folder inside, so the import never takes files from the user's real history. Call before
    /// the first open.</summary>
    internal static void UseFolder(string dir)
    {
        Close();
        _folder = dir;
        _recentFolder = Path.Combine(dir, "Recent");
    }

    public static bool IsOpen => _db != null;

    /// <summary>Off in the command line, whose process can end at any moment: no import of the
    /// old Recent folder and no OCR there.</summary>
    public static bool BackgroundWork { get; set; } = true;

    /// <summary>The library exists but is keyed with a password that has not been given yet.</summary>
    public static bool NeedsPassword { get; private set; }

    /// <summary>The library is protected by the user's password rather than the Windows account.</summary>
    public static bool HasPassword => IsOpen ? _keyedByPassword : NeedsPassword;

    /// <summary>Why the last open failed, when it failed for anything but a missing password.</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Raised after captures or collections change, on the thread that changed them.</summary>
    public static event Action? Changed;

    private static void RaiseChanged() => Changed?.Invoke();

    // ============================================================
    // Open, unlock, close
    // ============================================================

    /// <summary>
    /// Opens the library with the Windows-account key, creating it on first use. Returns false
    /// when it needs a password (NeedsPassword) or could not be opened (LoadError). Never throws.
    /// </summary>
    /// <summary>The open started at startup. The library screen shows its loading screen until
    /// this finishes.</summary>
    public static Task<bool> Opening { get; private set; } = Task.FromResult(false);

    /// <summary>Starts OpenAuto off the interface thread, so the window can paint first.</summary>
    public static Task<bool> BeginOpen() => Opening = Task.Run(OpenAuto);

    /// <summary>BeginOpen, then <paramref name="afterOpen"/> on the same background task once the
    /// library is open. Demo mode fills its scratch library this way.</summary>
    public static Task<bool> BeginOpen(Action afterOpen) => Opening = Task.Run(() =>
    {
        bool open = OpenAuto();
        if (open) afterOpen();
        return open;
    });

    public static bool OpenAuto()
    {
        lock (_gate)
        {
            if (_db != null) return true;
            LoadError = null;
            NeedsPassword = false;
            try
            {
                Directory.CreateDirectory(_folder);
                if (File.Exists(FilePath))
                {
                    if (!File.Exists(KeyPath)) { NeedsPassword = true; return false; }
                    try { OpenWith(ReadSealedKey(), byPassword: false); }
                    catch (SqliteException ex) when (IsKeyFailure(ex))
                    {
                        // A key file that no longer opens the library means a password was set
                        // and the old key file outlived the swap. The password is what opens it.
                        NeedsPassword = true;
                        return false;
                    }
                }
                else
                {
                    string key = NewRawKey();
                    WriteSealedKey(key);
                    OpenWith(key, byPassword: false);
                }
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                return false;
            }
        }
        AfterOpen();
        return true;
    }

    /// <summary>Opens a password-protected library. Returns false for a wrong password.</summary>
    public static bool Unlock(string password)
    {
        lock (_gate)
        {
            if (_db != null) return true;
            try { OpenWith(password, byPassword: true); }
            catch (SqliteException ex) when (IsKeyFailure(ex)) { return false; }
            NeedsPassword = false;
            // The swap that set the password may have been interrupted before it removed the
            // old key file. The password opened it, so the stale key goes.
            try { if (File.Exists(KeyPath)) File.Delete(KeyPath); } catch (IOException) { }
        }
        AfterOpen();
        return true;
    }

    public static void Close()
    {
        lock (_gate)
        {
            _db?.Dispose();
            _db = null;
            _keyedByPassword = false;
            ClearThumbCache();
            // Dispose does NOT release the file handle; the pool keeps it.
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Opens and proves the connection before publishing it to _db, so IsOpen never
    /// reports a connection that failed to open or failed its key check.</summary>
    private static void OpenWith(string key, bool byPassword)
    {
        var db = new SqliteConnection(Cs(FilePath, key));
        try
        {
            db.Open();
            // Open() on its own validates nothing; this is where a wrong key fails.
            using var probe = db.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master";
            probe.ExecuteScalar();
        }
        catch
        {
            db.Dispose();
            SqliteConnection.ClearAllPools();
            throw;
        }
        _db = db;
        _keyedByPassword = byPassword;
        Exec("PRAGMA foreign_keys = ON");
        EnsureSchema();
    }

    /// <summary>The key rides in the connection string, which is part of the pool key, so a wrong
    /// key gets its own handle and fails with SQLite error 26 instead of reusing a keyed one.</summary>
    private static string Cs(string path, string key)
    {
        var b = new SqliteConnectionStringBuilder { DataSource = path, Password = key, DefaultTimeout = 30 };
        return b.ConnectionString;
    }

    /// <summary>SQLCipher reports a bad key as "file is not a database" (26).</summary>
    private static bool IsKeyFailure(SqliteException ex) =>
        ex.SqliteErrorCode == 26 || ex.SqliteExtendedErrorCode == 26;

    /// <summary>Work that follows every successful open, off the caller's thread: pending
    /// captures from while the library was locked, the old Recent folder, the history limit and
    /// the OCR backlog.</summary>
    private static void AfterOpen()
    {
        RaiseChanged();
        if (!BackgroundWork) return;
        Task.Run(() =>
        {
            FlushPending();
            ImportRecentFolder();
            Prune();
            StartOcr();
            StartQrReading();
        });
    }

    // ============================================================
    // The Windows-account key
    // ============================================================

    /// <summary>A random 256-bit key in SQLCipher's raw-key form, which skips the passphrase
    /// key derivation because the key is already full strength.</summary>
    private static string NewRawKey() => "x'" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "'";

    private static void WriteSealedKey(string key)
    {
        byte[] sealedKey = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(key), KeyEntropy, DataProtectionScope.CurrentUser);
        string tmp = KeyPath + ".tmp";
        File.WriteAllBytes(tmp, sealedKey);
        File.Move(tmp, KeyPath, true);
    }

    private static string ReadSealedKey() =>
        System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), KeyEntropy, DataProtectionScope.CurrentUser));

    // ============================================================
    // Schema
    // ============================================================

    private static void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS collections(
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                created_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS captures(
                id INTEGER PRIMARY KEY,
                taken_utc INTEGER NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                mode TEXT NOT NULL,
                window_title TEXT,
                process_name TEXT,
                collection_id INTEGER REFERENCES collections(id) ON DELETE SET NULL,
                note TEXT,
                ocr_text TEXT,
                thumb BLOB NOT NULL,
                image BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS captures_taken ON captures(taken_utc DESC);
            CREATE INDEX IF NOT EXISTS captures_collection ON captures(collection_id);
            CREATE TABLE IF NOT EXISTS capture_qr(
                capture_id INTEGER PRIMARY KEY REFERENCES captures(id) ON DELETE CASCADE,
                results TEXT NOT NULL,
                text TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS capture_search USING fts5(
                title, process, note, ocr, collection, tokenize = 'unicode61 remove_diacritics 2');
            """);
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ('schema', $v)";
        cmd.Parameters.AddWithValue("$v", SchemaVersion);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(string sql)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Db => _db ?? throw new InvalidOperationException(Loc("Str_Lib_NotOpen"));

    private static string Loc(string key) => LocaleManager.Loc(key);

    // ============================================================
    // Adding captures
    // ============================================================

    private static readonly List<(BitmapSource Bmp, DateTime Taken, string Mode, string? Window, string? Process)> _pending = [];

    /// <summary>Stores a capture in the background. While the library is locked the capture
    /// waits in memory and is stored when it is unlocked.</summary>
    public static void Add(BitmapSource bmp, DateTime taken, string mode, string? window, string? process)
    {
        if (!bmp.IsFrozen) bmp.Freeze();
        Task.Run(() =>
        {
            try
            {
                // Checked under the gate, so an open finishing at the same moment cannot flush the
                // pending list before this capture is on it.
                lock (_gate)
                {
                    if (_db == null)
                    {
                        lock (_pending) _pending.Add((bmp, taken, mode, window, process));
                        return;
                    }
                }
                AddNow(bmp, taken, mode, window, process);
            }
            catch { /* the library is a convenience; the clipboard and any saved file already have the capture */ }
        });
    }

    /// <summary>Stores a capture before returning and gives its id. Throws when the library is
    /// not open. The command line uses this, because its process can end before a background
    /// write finishes.</summary>
    public static long AddNow(BitmapSource bmp, DateTime taken, string mode, string? window, string? process)
    {
        if (!bmp.IsFrozen) bmp.Freeze();
        byte[] image = ImageOutput.EncodePng(bmp);
        byte[] thumb = EncodeThumb(bmp);
        long id;
        SqliteConnection album;
        lock (_gate)
        {
            album = Db;
            using var cmd = Db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO captures(taken_utc, width, height, mode, window_title, process_name, thumb, image)
                VALUES ($t, $w, $h, $m, $win, $proc, $thumb, $image);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$t", taken.ToUniversalTime().Ticks);
            cmd.Parameters.AddWithValue("$w", bmp.PixelWidth);
            cmd.Parameters.AddWithValue("$h", bmp.PixelHeight);
            cmd.Parameters.AddWithValue("$m", mode);
            cmd.Parameters.AddWithValue("$win", (object?)Trimmed(window) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$proc", (object?)Trimmed(process) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumb", thumb);
            cmd.Parameters.AddWithValue("$image", image);
            id = (long)cmd.ExecuteScalar()!;
            IndexText(id);
        }
        if (mode == "qr") StoreQr(id, QrImages.Read(bmp), album);
        Prune();
        RaiseChanged();
        if (BackgroundWork)
        {
            StartOcr();
            StartQrReading();
        }
        return id;
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void FlushPending()
    {
        List<(BitmapSource Bmp, DateTime Taken, string Mode, string? Window, string? Process)> items;
        lock (_pending)
        {
            items = [.. _pending];
            _pending.Clear();
        }
        foreach (var p in items)
        {
            try { AddNow(p.Bmp, p.Taken, p.Mode, p.Window, p.Process); }
            catch { /* best effort, as for any capture */ }
        }
    }

    /// <summary>Captures waiting for the library to be unlocked.</summary>
    public static int PendingCount { get { lock (_pending) return _pending.Count; } }

    /// <summary>A JPEG of at most 512 pixels on its long side. Thumbnails are what the library
    /// screen draws, so a screen of them never decodes a full capture.</summary>
    private static byte[] EncodeThumb(BitmapSource bmp)
    {
        double scale = Math.Min(1.0, (double)ThumbLongSide / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
        BitmapSource small = scale < 1.0 ? new TransformedBitmap(bmp, new ScaleTransform(scale, scale)) : bmp;
        var enc = new JpegBitmapEncoder { QualityLevel = 88 };
        enc.Frames.Add(BitmapFrame.Create(small));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Moves the PNGs the old Recent history kept in plain files into the library, then
    /// removes them, so no capture stays readable outside it.</summary>
    private static void ImportRecentFolder()
    {
        try
        {
            var dir = new DirectoryInfo(_recentFolder);
            if (!dir.Exists) return;
            foreach (var file in dir.GetFiles("*.png").OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                try
                {
                    var bmp = ImageOutput.Load(file.FullName);
                    AddNow(bmp, RecentCaptures.TakenFrom(file) ?? file.LastWriteTime, "", null, null);
                    file.Delete();
                }
                catch { /* a file that will not load or delete stays where it was */ }
            }
            if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
        }
        catch { }
    }

    // ============================================================
    // Reading
    // ============================================================

    /// <summary>Every capture, newest first, or those matching a search. A search matches words
    /// in the window title, the program, the note, the collection and the text in the picture,
    /// each word as a prefix, all words required.</summary>
    public static IReadOnlyList<CaptureItem> Items(string? search = null)
    {
        string? match = MatchExpression(search);
        lock (_gate)
        {
            if (_db == null) return [];
            using var cmd = _db.CreateCommand();
            cmd.CommandText = match == null
                ? "SELECT id, taken_utc, width, height, mode, window_title, process_name, collection_id, note, EXISTS(SELECT 1 FROM capture_qr q WHERE q.capture_id=captures.id AND q.results <> '[]') FROM captures ORDER BY taken_utc DESC"
                : """
                  SELECT id, taken_utc, width, height, mode, window_title, process_name, collection_id, note,
                  EXISTS(SELECT 1 FROM capture_qr q WHERE q.capture_id=captures.id AND q.results <> '[]') FROM captures
                  WHERE id IN (SELECT rowid FROM capture_search WHERE capture_search MATCH $q)
                  ORDER BY taken_utc DESC
                  """;
            if (match != null) cmd.Parameters.AddWithValue("$q", match);
            var list = new List<CaptureItem>();
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new CaptureItem(r.GetInt64(0),
                        new DateTime(r.GetInt64(1), DateTimeKind.Utc).ToLocalTime(),
                        r.GetInt32(2), r.GetInt32(3), r.GetString(4),
                        r.IsDBNull(5) ? null : r.GetString(5),
                        r.IsDBNull(6) ? null : r.GetString(6),
                        r.IsDBNull(7) ? null : r.GetInt64(7),
                        r.IsDBNull(8) ? null : r.GetString(8), r.GetBoolean(9)));
                }
            }
            catch (SqliteException) { return []; }   // a search FTS5 cannot parse matches nothing
            return list;
        }
    }

    /// <summary>Turns typed words into an FTS5 expression: each word quoted, as a prefix.</summary>
    private static string? MatchExpression(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var words = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\"", ""))
            .Where(w => w.Length > 0)
            .Select(w => "\"" + w + "\"*");
        string expr = string.Join(" ", words);
        return expr.Length == 0 ? null : expr;
    }

    public static int Count
    {
        get
        {
            lock (_gate)
            {
                if (_db == null) return 0;
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM captures";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    public static IReadOnlyList<CaptureCollection> Collections()
    {
        lock (_gate)
        {
            if (_db == null) return [];
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.name, (SELECT count(*) FROM captures WHERE collection_id = c.id)
                FROM collections c ORDER BY c.name COLLATE NOCASE
                """;
            var list = new List<CaptureCollection>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new CaptureCollection(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
            return list;
        }
    }

    /// <summary>The full capture at its exact pixel size.</summary>
    public static BitmapSource LoadImage(long id)
    {
        if (FolderCaptures.IsFolderId(id)) return FolderCaptures.LoadImage(id);
        byte[] png;
        lock (_gate)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT image FROM captures WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            png = cmd.ExecuteScalar() as byte[] ?? throw new InvalidOperationException(Loc("Str_Lib_Missing"));
        }
        return Decode(png, exactPixels: true);
    }

    /// <summary>The PNG exactly as stored, for exporting without re-encoding.</summary>
    public static byte[] LoadPng(long id)
    {
        if (FolderCaptures.IsFolderId(id)) return FolderCaptures.LoadPng(id);
        lock (_gate)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT image FROM captures WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as byte[] ?? throw new InvalidOperationException(Loc("Str_Lib_Missing"));
        }
    }

    private static readonly Dictionary<long, BitmapSource> _thumbs = [];
    private static readonly LinkedList<long> _thumbOrder = new();
    private const int ThumbCacheSize = 600;

    /// <summary>A capture's thumbnail, cached. Null when it no longer exists.</summary>
    public static BitmapSource? LoadThumb(long id)
    {
        if (FolderCaptures.IsFolderId(id)) return FolderCaptures.LoadThumb(id);
        lock (_gate)
        {
            if (_thumbs.TryGetValue(id, out var cached)) return cached;
            if (_db == null) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT thumb FROM captures WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteScalar() is not byte[] jpg) return null;
            var bmp = Decode(jpg, exactPixels: false);
            _thumbs[id] = bmp;
            _thumbOrder.AddLast(id);
            while (_thumbOrder.Count > ThumbCacheSize)
            {
                _thumbs.Remove(_thumbOrder.First!.Value);
                _thumbOrder.RemoveFirst();
            }
            return bmp;
        }
    }

    private static void ClearThumbCache()
    {
        _thumbs.Clear();
        _thumbOrder.Clear();
    }

    private static BitmapSource Decode(byte[] bytes, bool exactPixels)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        if (!exactPixels) { img.Freeze(); return img; }
        // At its true pixel size, as ImageOutput.Load does for files.
        BitmapSource src = img.Format == PixelFormats.Bgra32 ? img : new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[(long)w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var copy = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        copy.Freeze();
        return copy;
    }

    // ============================================================
    // Changing captures and collections
    // ============================================================

    public static void Delete(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var tx = Db.BeginTransaction();
            foreach (long id in ids)
            {
                using var cmd = Db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM captures WHERE id = $id; DELETE FROM capture_search WHERE rowid = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                if (_thumbs.Remove(id)) _thumbOrder.Remove(id);
            }
            tx.Commit();
        }
        RaiseChanged();
    }

    /// <summary>Puts captures in a collection, or takes them out of any with null.</summary>
    public static void Move(IEnumerable<long> ids, long? collectionId)
    {
        lock (_gate)
        {
            using var tx = Db.BeginTransaction();
            foreach (long id in ids)
            {
                using var cmd = Db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE captures SET collection_id = $c WHERE id = $id";
                cmd.Parameters.AddWithValue("$c", (object?)collectionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
                IndexText(id, tx);
            }
            tx.Commit();
        }
        RaiseChanged();
    }

    public static void SetNote(long id, string? note)
    {
        lock (_gate)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE captures SET note = $n WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", (object?)Trimmed(note) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            IndexText(id);
        }
        RaiseChanged();
    }

    public static long CreateCollection(string name)
    {
        long id;
        lock (_gate)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "INSERT INTO collections(name, created_utc) VALUES ($n, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
            id = (long)cmd.ExecuteScalar()!;
        }
        RaiseChanged();
        return id;
    }

    public static void RenameCollection(long id, string name)
    {
        lock (_gate)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE collections SET name = $n WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            ReindexCollection(id);
        }
        RaiseChanged();
    }

    /// <summary>Removes a collection. Its captures stay in the library, in no collection.</summary>
    public static void DeleteCollection(long id)
    {
        lock (_gate)
        {
            var members = MembersOf(id);
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE captures SET collection_id = NULL WHERE collection_id = $id; DELETE FROM collections WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            foreach (long m in members) IndexText(m);
        }
        RaiseChanged();
    }

    private static List<long> MembersOf(long collectionId)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = "SELECT id FROM captures WHERE collection_id = $id";
        cmd.Parameters.AddWithValue("$id", collectionId);
        var ids = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private static void ReindexCollection(long collectionId)
    {
        foreach (long m in MembersOf(collectionId)) IndexText(m);
    }

    /// <summary>Rewrites one capture's row in the search index from its current values.</summary>
    private static void IndexText(long id, SqliteTransaction? tx = null)
    {
        using var cmd = Db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM capture_search WHERE rowid = $id;
            INSERT INTO capture_search(rowid, title, process, note, ocr, collection)
            SELECT c.id, c.window_title, c.process_name, c.note, COALESCE(c.ocr_text, '') || char(10) || COALESCE(q.text, ''), k.name
            FROM captures c LEFT JOIN collections k ON k.id = c.collection_id
            LEFT JOIN capture_qr q ON q.capture_id = c.id WHERE c.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ============================================================
    // History limit
    // ============================================================

    /// <summary>"all" (the default), "count:N" or "days:N", chosen in Capture settings.</summary>
    public static string Keep
    {
        get => Settings.Get("LibraryKeep") is string k && ParseKeep(k).Kind != null ? k : "all";
        set { Settings.Set("LibraryKeep", value); Task.Run(Prune); }
    }

    public static (string? Kind, int Amount) ParseKeep(string keep)
    {
        if (keep == "all") return ("all", 0);
        var parts = keep.Split(':');
        if (parts.Length == 2 && parts[0] is ("count" or "days") &&
            int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0)
            return (parts[0], n);
        return (null, 0);
    }

    /// <summary>Removes the captures beyond the history limit, oldest first.</summary>
    private static void Prune()
    {
        var (kind, amount) = ParseKeep(Keep);
        if (kind is not ("count" or "days")) return;
        int removed;
        lock (_gate)
        {
            if (_db == null) return;
            using var cmd = _db.CreateCommand();
            if (kind == "count")
            {
                cmd.CommandText = """
                    DELETE FROM capture_search WHERE rowid IN (SELECT id FROM captures ORDER BY taken_utc DESC LIMIT -1 OFFSET $n);
                    DELETE FROM captures WHERE id IN (SELECT id FROM captures ORDER BY taken_utc DESC LIMIT -1 OFFSET $n);
                    """;
                cmd.Parameters.AddWithValue("$n", amount);
            }
            else
            {
                cmd.CommandText = """
                    DELETE FROM capture_search WHERE rowid IN (SELECT id FROM captures WHERE taken_utc < $t);
                    DELETE FROM captures WHERE taken_utc < $t;
                    """;
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.AddDays(-amount).Ticks);
            }
            removed = cmd.ExecuteNonQuery();
            if (removed > 0) ClearThumbCache();
        }
        if (removed > 0) RaiseChanged();
    }
}
