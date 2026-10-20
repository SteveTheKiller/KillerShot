using System.IO;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    // ============================================================
    // Password. Ported from Killendar's EventStore.SetPassword, including the two parts that
    // look like ceremony and are not: the forced finalization before the file swap, and
    // ReplaceWithRetry (KillerHarness "SQLCipher: two traps", rule 3).
    // ============================================================

    /// <summary>
    /// Sets or changes the library's password, or removes it with null, by rewriting the file
    /// through sqlcipher_export with the new key and reopening. With no password the new key is
    /// a fresh random key sealed to the Windows account. The library must be open, which proves
    /// the current key.
    /// </summary>
    /// <summary>True when the open library is keyed by this password.</summary>
    public static bool CheckPassword(string password)
    {
        lock (_gate)
        {
            if (_db == null || !_keyedByPassword) return false;
            var current = System.Text.Encoding.UTF8.GetBytes(new SqliteConnectionStringBuilder(_db.ConnectionString).Password);
            var typed = System.Text.Encoding.UTF8.GetBytes(password);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(current, typed);
        }
    }

    public static void SetPassword(string? newPassword)
        => SetPassword(newPassword, File.Replace);

    /// <summary>Changes the password using the supplied filesystem replacement operation.</summary>
    public static void SetPassword(string? newPassword, Action<string, string, string> replaceFile)
    {
        ArgumentNullException.ThrowIfNull(replaceFile);
        if (string.IsNullOrEmpty(newPassword)) newPassword = null;
        lock (_gate)
        {
            if (_db == null) throw new InvalidOperationException(Loc("Str_Lib_NotOpen"));
            using var transitionLock = AcquireTransitionLock();

            string oldKey = _db.ConnectionString;   // reopen fallback, key included
            string oldPassword = new SqliteConnectionStringBuilder(oldKey).Password;
            bool oldByPassword = _keyedByPassword;
            string newKey = newPassword ?? NewRawKey();
            string path = FilePath;
            string tmp = path + ".rekey";
            if (File.Exists(tmp)) File.Delete(tmp);
            // The main connection opens in ReadWrite mode. ATTACH inherits that mode and needs
            // an existing target file, even though SQLCipher initializes its contents.
            File.Create(tmp).Dispose();

            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "ATTACH DATABASE $file AS rekeyed KEY $key";
                    cmd.Parameters.AddWithValue("$file", tmp);
                    cmd.Parameters.AddWithValue("$key", newKey);
                    cmd.ExecuteNonQuery();
                }
                Exec("SELECT sqlcipher_export('rekeyed')");
                Exec("DETACH DATABASE rekeyed");
            }
            catch (Exception exportError)
            {
                // Export can fail after ATTACH has opened the target. Closing and clearing
                // the pool releases both files and the alias before another attempt.
                Close();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                SqliteConnection.ClearAllPools();
                try { OpenWith(oldPassword, oldByPassword); }
                catch (Exception recoveryError)
                {
                    throw new IOException("The album could not reopen after the password export failed. Recovery files were kept.",
                        new AggregateException(exportError, recoveryError));
                }
                try { File.Delete(tmp); } catch (IOException) { }
                throw;
            }

            // Seal the account key before changing the active database file. Startup can use
            // this staged key if the process stops after the swap.
            string stagedKey = KeyPath + ".next";
            if (newPassword == null) WriteSealedKey(newKey, stagedKey);

            _db.Dispose();
            _db = null;
            ClearThumbCache();

            // Clearing the pool alone is not always enough: a sqlite3 handle kept alive by a
            // finalizer still has the old file mapped, and the swap then fails however long it
            // retries. Force finalization so every native handle on both files is closed.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();

            string bak = path + ".bak";
            try
            {
                ReplaceWithRetry(tmp, path, bak, replaceFile);
            }
            catch (Exception swapError)
            {
                // A failed move-based swap may have left the only old album in the backup.
                // Keep every recovery file until that album has reopened with its old key.
                try
                {
                    RestoreReplacementDestination(path, bak);
                    OpenWith(oldPassword, oldByPassword);
                }
                catch (Exception recoveryError)
                {
                    throw new IOException("The album could not reopen after the password change failed. Recovery files were kept.",
                        new AggregateException(swapError, recoveryError));
                }
                try { File.Delete(tmp); } catch { }
                if (newPassword == null) try { File.Delete(stagedKey); } catch { }
                throw;
            }

            try
            {
                OpenWith(newKey, newPassword != null);
                if (newPassword == null) File.Move(stagedKey, KeyPath, true);
                NeedsPassword = false;
                try { File.Delete(bak); } catch (IOException) { }
            }
            catch
            {
                Close();
                // Keep the staged key and backup for recovery on the next open.
                throw;
            }
        }
        RaiseChanged();
    }

    /// <summary>File.Replace with backoff retries, falling back to a move-based swap that puts
    /// the original back if the second move fails. Antivirus and indexer scans of the freshly
    /// written file cause transient sharing violations.</summary>
    private static void ReplaceWithRetry(string source, string dest, string backup, Action<string, string, string> replaceFile)
    {
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            try { replaceFile(source, dest, backup); return; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                RestoreReplacementDestination(dest, backup);
                Thread.Sleep(150 * attempt);
            }
        }
        RestoreReplacementDestination(dest, backup);
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(dest, backup);
        try { File.Move(source, dest); }
        catch
        {
            File.Move(backup, dest);
            throw;
        }
    }

    private static void RestoreReplacementDestination(string destination, string backup)
    {
        if (File.Exists(destination)) return;
        if (!File.Exists(backup)) throw new IOException("The original album is unavailable. Recovery files were kept.");
        string restored = destination + ".restore-" + Guid.NewGuid().ToString("N");
        File.Copy(backup, restored);
        File.Move(restored, destination);
    }
}
