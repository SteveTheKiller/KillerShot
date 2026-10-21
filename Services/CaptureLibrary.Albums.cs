using System.IO;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    public sealed record AlbumInfo(string Path, string Name, long Bytes, DateTime Modified, bool Active, bool PasswordProtected);

    public static IReadOnlyList<AlbumInfo> Albums()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_folder);
            return new DirectoryInfo(_folder).EnumerateFiles("*.kshot")
                .Select(file => new AlbumInfo(file.FullName, Path.GetFileNameWithoutExtension(file.Name), file.Length,
                    file.LastWriteTime, string.Equals(file.FullName, FilePath, StringComparison.OrdinalIgnoreCase),
                    !File.Exists(Path.ChangeExtension(file.FullName, ".key"))))
                .OrderByDescending(album => album.Active).ThenBy(album => album.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    private sealed record RenameJournal(string OldName, string NewName, bool Active, string[] Sidecars, bool Committed = false);
    private static readonly string[] AlbumSidecars = [".key", ".key.next", ".kshot.bak"];
    private static bool AlbumNameExists(string path)
    {
        if (File.Exists(path) || new[] { ".key", ".key.next", ".key.creating", ".kshot.bak", ".kshot.rekey" }
            .Any(ext => File.Exists(Sidecar(path, ext)))) return true;
        foreach (string journal in Directory.EnumerateFiles(_folder, ".album-rename-*.json"))
        {
            var entry = System.Text.Json.JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(journal));
            if (entry == null || string.Equals(Path.GetFileName(path), entry.OldName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), entry.NewName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Sidecar(string album, string extension) => Path.ChangeExtension(album, extension);

    public static string RenameAlbum(string path, string name)
    {
        lock (PendingWriter)
        {
            FlushPending();
            lock (_gate)
            {
                path = ManagedAlbumPath(path);
                string destination = AlbumPath(name);
                if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)) return path;
                bool active = string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase);
                if (active && PendingCount > 0) throw new InvalidOperationException(Loc("Str_Album_Pending"));
                using var catalogLock = AcquireCatalogLock();
                using var sourceLock = AcquireTransitionLock(path);
                using var destinationLock = AcquireTransitionLock(destination);
                if (!File.Exists(path)) throw new FileNotFoundException(Loc("Str_Album_Missing"), path);
                if (AlbumNameExists(destination))
                    throw new IOException(Loc("Str_Album_Exists"));
                string? key = active && _db != null ? new SqliteConnectionStringBuilder(_db.ConnectionString).Password : null;
                bool byPassword = _keyedByPassword;
                string journal = Path.Combine(_folder, ".album-rename-" + Guid.NewGuid().ToString("N") + ".json");
                var entry = new RenameJournal(Path.GetFileName(path), Path.GetFileName(destination), active,
                    AlbumSidecars.Where(ext => File.Exists(Sidecar(path, ext))).ToArray());
                using (var record = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    System.Text.Json.JsonSerializer.Serialize(record, entry);
                    record.Flush(true);
                }
                bool moved = false, committed = false;
                try
                {
                    foreach (string ext in entry.Sidecars) File.Copy(Sidecar(path, ext), Sidecar(destination, ext));
                    if (active) Close();
                    File.Move(path, destination);
                    moved = true;
                    SqliteConnection? reopened = key != null ? OpenAlbumConnection(destination, key) : null;
                    try
                    {
                        if (active) Settings.SetRequired("ActiveAlbum", Path.GetFileName(destination));
                        if (active)
                        {
                            _activeFileName = Path.GetFileName(destination);
                            _db = reopened;
                            reopened = null;
                            _keyedByPassword = byPassword;
                            ObserveExternalChanges();
                        }
                    }
                    finally { reopened?.Dispose(); SqliteConnection.ClearAllPools(); }
                    committed = true;
                }
                catch
                {
                    if (moved) File.Move(destination, path);
                    if (active && key != null && _db == null) OpenWith(key, byPassword);
                    foreach (string ext in entry.Sidecars) File.Delete(Sidecar(destination, ext));
                    File.Delete(journal);
                    throw;
                }
                if (committed)
                {
                    // Preserve the old keys until the commit marker is durable. Cleanup can be
                    // retried at startup without turning an inactive rename into a rollback.
                    string completed = journal + ".next";
                    try
                    {
                        using (var record = new FileStream(completed, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            System.Text.Json.JsonSerializer.Serialize(record, entry with { Committed = true });
                            record.Flush(true);
                        }
                        File.Move(completed, journal, true);
                        foreach (string ext in entry.Sidecars) File.Delete(Sidecar(path, ext));
                        File.Delete(journal);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { System.Diagnostics.Trace.TraceWarning("Album rename cleanup will retry: {0}", error.Message); }
                }
                RaiseChanged();
                return destination;
            }
        }
    }

    private static void RecoverAlbumRenames()
    {
        foreach (string journal in Directory.EnumerateFiles(_folder, ".album-rename-*.json"))
        {
            var entry = System.Text.Json.JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(journal))
                ?? throw new InvalidDataException(Loc("Str_Album_RecoveryIncomplete"));
            string oldPath = AlbumPath(entry.OldName), newPath = AlbumPath(entry.NewName);
            if (entry.Sidecars == null || entry.Sidecars.Any(ext => !AlbumSidecars.Contains(ext))
                || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc("Str_Album_RecoveryInvalid"));
            using var oldLock = AcquireTransitionLock(oldPath);
            using var newLock = AcquireTransitionLock(newPath);
            bool committed = entry.Committed
                || !entry.Active && !File.Exists(oldPath) && File.Exists(newPath)
                || entry.Active && string.Equals(Settings.Get("ActiveAlbum"), entry.NewName, StringComparison.OrdinalIgnoreCase);
            if (committed && File.Exists(newPath))
            {
                // A foreign creator may have reused the old name without consulting this catalog.
                // Its new key is not part of this rename and must survive recovery.
                if (!File.Exists(oldPath))
                    foreach (string ext in entry.Sidecars) File.Delete(Sidecar(oldPath, ext));
            }
            else
            {
                if (!File.Exists(oldPath) && File.Exists(newPath)) File.Move(newPath, oldPath);
                if (!File.Exists(oldPath)) throw new IOException(Loc("Str_Album_RecoveryMissing"));
                if (File.Exists(newPath)) throw new IOException(Loc("Str_Album_RecoveryConflict"));
                foreach (string ext in entry.Sidecars) File.Delete(Sidecar(newPath, ext));
            }
            File.Delete(journal);
        }
    }

    public static string? ImportAlbum(string path, string? password, out bool needsPassword)
    {
        needsPassword = false;
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase))
                return ManagedAlbumPath(path);
            using var catalogLock = AcquireCatalogLock();
            string? key = password;
            if (key == null)
            {
                string sourceKey = Path.ChangeExtension(path, ".key");
                if (!File.Exists(sourceKey)) { needsPassword = true; return null; }
                try { key = ReadSealedKey(sourceKey); }
                catch (System.Security.Cryptography.CryptographicException error)
                { throw new InvalidDataException(Loc("Str_Album_KeyUnavailable"), error); }
            }
            string destination = AlbumPath(Path.GetFileName(path));
            for (int suffix = 2; AlbumNameExists(destination); suffix++)
                destination = AlbumPath(Path.GetFileNameWithoutExtension(path) + " (" + suffix + ")");
            string staging = destination + ".importing-" + Guid.NewGuid().ToString("N");
            string stagingKey = staging + ".key";
            bool promotedKey = false;
            try
            {
                var connectionString = new SqliteConnectionStringBuilder(Cs(path, key, false)) { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
                using (var source = new SqliteConnection(connectionString.ConnectionString))
                {
                    source.Open();
                    using var check = source.CreateCommand();
                    check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('meta','captures')";
                    if (Convert.ToInt32(check.ExecuteScalar()) != 2) throw new InvalidDataException(Loc("Str_Album_SchemaIncomplete"));
                    using var copy = new SqliteConnection(Cs(staging, key, true));
                    copy.Open();
                    source.BackupDatabase(copy);
                    EnsureSchema(copy);
                    InitializeAlbumSettings(copy, destination);
                }
                SqliteConnection.ClearAllPools();
                using (var validation = OpenAlbumConnection(staging, key)) { }
                SqliteConnection.ClearAllPools();
                if (password == null)
                {
                    WriteSealedKey(key, stagingKey);
                    File.Move(stagingKey, Path.ChangeExtension(destination, ".key"));
                    promotedKey = true;
                }
                File.Move(staging, destination);
                return destination;
            }
            catch (SqliteException error) when (IsKeyFailure(error)) { needsPassword = true; return null; }
            catch
            {
                if (promotedKey) File.Delete(Path.ChangeExtension(destination, ".key"));
                throw;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (string owned in new[] { staging, stagingKey }) try { File.Delete(owned); } catch (IOException) { }
            }
        }
    }

    private static void InitializeAlbumSettings(SqliteConnection database, string path)
    {
        if (PendingQrChoices.TryGetValue(path, out bool pendingQr))
        {
            using var choice = database.CreateCommand();
            choice.CommandText = "INSERT INTO meta(key,value) VALUES('setting.QrReading',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            choice.Parameters.AddWithValue("$value", pendingQr ? "1" : "0");
            choice.ExecuteNonQuery();
            PendingQrChoices.Remove(path);
        }
        bool legacy = string.Equals(Path.GetFileName(path), "Library.kshot", StringComparison.OrdinalIgnoreCase);
        foreach (var (key, fallback) in new[] { ("LibraryKeep", "all"), ("QrReading", "0") })
        {
            using var command = database.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES ($key, $value)";
            command.Parameters.AddWithValue("$key", "setting." + key);
            command.Parameters.AddWithValue("$value", legacy ? Settings.Get(key) ?? fallback : fallback);
            command.ExecuteNonQuery();
        }
        using var migrated = database.CreateCommand();
        migrated.CommandText = "SELECT value FROM meta WHERE key='setting.colorsMigrated'";
        if (migrated.ExecuteScalar() != null) return;
        using var transaction = database.BeginTransaction();
        if (legacy)
        {
            var ids = new List<long>();
            using (var collections = database.CreateCommand())
            {
                collections.Transaction = transaction;
                collections.CommandText = "SELECT id FROM collections";
                using var rows = collections.ExecuteReader();
                while (rows.Read()) ids.Add(rows.GetInt64(0));
            }
            foreach (long id in ids)
            {
                string key = "CollectionColor." + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (Settings.Get(key) is not string color) continue;
                using var command = database.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO meta(key,value) VALUES($key,$value)";
                command.Parameters.AddWithValue("$key", "setting." + key);
                command.Parameters.AddWithValue("$value", color);
                command.ExecuteNonQuery();
            }
        }
        using var complete = database.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = "INSERT INTO meta(key,value) VALUES('setting.colorsMigrated','1')";
        complete.ExecuteNonQuery();
        transaction.Commit();
    }

    public static string? GetAlbumSetting(string key)
    {
        lock (_gate)
        {
            if (_db == null) return null;
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", "setting." + key);
            return command.ExecuteScalar() as string;
        }
    }

    public static void SetAlbumSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = Db.CreateCommand();
            command.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", "setting." + key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public static bool TrySwitchAlbum(string path, string? password, out bool needsPassword, Func<bool>? canActivate = null)
    {
        needsPassword = false;
        lock (PendingWriter)
        {
            FlushPending();
            lock (_gate)
            {
                path = ManagedAlbumPath(path);
                if (string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase) && _db != null) return true;
                if (PendingCount > 0) throw new InvalidOperationException(Loc("Str_Album_Pending"));
            }
            // Waiting for another process's lock, opening the destination and asking about
            // unsaved edits happen outside the album gate, so the open album stays usable (and
            // the interface responsive) meanwhile. The gate is held again only for the swap.
            using var catalogLock = AcquireCatalogLock();
            using var transitionLock = AcquireTransitionLock(path);
            SqliteConnection? destination = PrepareAlbum(path, password, out bool byPassword);
            if (destination == null) { needsPassword = true; return false; }
            try
            {
                if (canActivate != null && !canActivate()) return false;
                // Confirmation runs a nested dispatcher. Captures accepted there still
                // belong to the old album, even though this thread owns the writer lock.
                FlushPending();
                lock (_gate)
                {
                    if (PendingCount > 0) throw new InvalidOperationException(Loc("Str_Album_Pending"));
                    Settings.SetRequired("ActiveAlbum", Path.GetFileName(path));
                    Close();
                    _session++;
                    _activeFileName = Path.GetFileName(path);
                    _db = destination;
                    destination = null;
                    _keyedByPassword = byPassword;
                    NeedsPassword = false;
                    LoadError = null;
                    ObserveExternalChanges();
                }
            }
            finally { destination?.Dispose(); }
        }
        AfterOpen();
        return true;
    }

    private static SqliteConnection? PrepareAlbum(string path, string? password, out bool byPassword)
    {
        byPassword = false;
        if ((!File.Exists(path) || new FileInfo(path).Length == 0) && File.Exists(path + ".bak"))
            File.Copy(path + ".bak", path, true);
        if (!File.Exists(path)) throw new FileNotFoundException(Loc("Str_Album_Missing"), path);
        if (password != null)
        {
            try { var result = OpenPasswordAlbumConnection(path, password); byPassword = true; return result; }
            catch (SqliteException error) when (IsKeyFailure(error)) { return null; }
        }
        string keyPath = Path.ChangeExtension(path, ".key");
        foreach (string candidate in new[] { keyPath, keyPath + ".next" })
        {
            if (!File.Exists(candidate)) continue;
            string key;
            try { key = ReadSealedKey(candidate); }
            catch (System.Security.Cryptography.CryptographicException error)
            { throw new InvalidDataException(Loc("Str_Album_KeyUnavailable"), error); }
            SqliteConnection opened;
            try { opened = OpenAlbumConnection(path, key); }
            catch (SqliteException error) when (IsKeyFailure(error)) { continue; }
            try { if (candidate != keyPath) File.Move(candidate, keyPath, true); }
            catch { opened.Dispose(); throw; }
            return opened;
        }
        return null;
    }

    private static string ManagedAlbumPath(string nameOrPath)
    {
        if (!Path.IsPathRooted(nameOrPath)) return AlbumPath(nameOrPath);
        string path = Path.GetFullPath(nameOrPath);
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(_folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Loc("Str_Album_InvalidName"), nameof(nameOrPath));
        return AlbumPath(Path.GetFileName(path));
    }

    public static string CreateAlbum(string name)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_folder);
            string path = AlbumPath(name);
            string keyPath = Path.ChangeExtension(path, ".key");
            using var catalogLock = AcquireCatalogLock();
            if (AlbumNameExists(path)) throw new IOException(Loc("Str_Album_Exists"));
            string staging = path + ".creating-" + Guid.NewGuid().ToString("N");
            string stagingKey = staging + ".key";
            bool promotedKey = false;
            try
            {
                string key = NewRawKey();
                WriteSealedKey(key, stagingKey);
                using (var database = OpenAlbumConnection(staging, key, create: true)) { }
                SqliteConnection.ClearAllPools();
                File.Move(stagingKey, keyPath);
                promotedKey = true;
                File.Move(staging, path);
                return path;
            }
            catch
            {
                if (promotedKey) try { File.Delete(keyPath); } catch (IOException) { }
                throw;
            }
            finally
            {
                foreach (string owned in new[] { staging, stagingKey })
                    try { File.Delete(owned); } catch (IOException) { }
            }
        }
    }

    private static string AlbumPath(string name)
    {
        name = name.Trim();
        if (!name.EndsWith(".kshot", StringComparison.OrdinalIgnoreCase)) name += ".kshot";
        string stem = Path.GetFileNameWithoutExtension(name);
        string device = stem.Split('.')[0].ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(stem) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name != Path.GetFileName(name) || stem.EndsWith('.') || stem.EndsWith(' ')
            || device is "CON" or "PRN" or "AUX" or "NUL" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9'))
            throw new ArgumentException(Loc("Str_Album_InvalidName"), nameof(name));
        return Path.Combine(_folder, name);
    }

    private static FileStream AcquireCatalogLock()
    {
        string path = Path.Combine(_folder, "albums.lock");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (wait.Elapsed < TimeSpan.FromSeconds(15) &&
                ((uint)error.HResult == 0x80070020u || (uint)error.HResult == 0x80070021u)) { Thread.Sleep(50); }
        }
    }
}
