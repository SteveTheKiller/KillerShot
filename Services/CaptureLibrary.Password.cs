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
    {
        if (string.IsNullOrEmpty(newPassword)) newPassword = null;
        lock (_gate)
        {
            if (_db == null) throw new InvalidOperationException(Loc("Str_Lib_NotOpen"));

            string oldKey = _db.ConnectionString;   // reopen fallback, key included
            bool oldByPassword = _keyedByPassword;
            string newKey = newPassword ?? NewRawKey();
            string path = FilePath;
            string tmp = path + ".rekey";
            if (File.Exists(tmp)) File.Delete(tmp);

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "ATTACH DATABASE $file AS rekeyed KEY $key";
                cmd.Parameters.AddWithValue("$file", tmp);
                cmd.Parameters.AddWithValue("$key", newKey);
                cmd.ExecuteNonQuery();
            }
            Exec("SELECT sqlcipher_export('rekeyed')");
            Exec("DETACH DATABASE rekeyed");

            string oldPassword = new SqliteConnectionStringBuilder(oldKey).Password;
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
                ReplaceWithRetry(tmp, path, bak);
            }
            catch
            {
                // The original is untouched; reopen it with the old key so the app is never
                // left without its library, and let the caller report the failure.
                try { File.Delete(tmp); } catch { }
                OpenWith(oldPassword, oldByPassword);
                throw;
            }

            // The key file follows the file it opens: written for the account key, removed for
            // a password. A crash between the swap and this point leaves a key file that no
            // longer opens the library, which OpenAuto reads as "needs the password".
            if (newPassword == null) WriteSealedKey(newKey);
            else if (File.Exists(KeyPath)) File.Delete(KeyPath);

            OpenWith(newKey, newPassword != null);
            NeedsPassword = false;
            File.Delete(bak);
        }
        RaiseChanged();
    }

    /// <summary>File.Replace with backoff retries, falling back to a move-based swap that puts
    /// the original back if the second move fails. Antivirus and indexer scans of the freshly
    /// written file cause transient sharing violations.</summary>
    private static void ReplaceWithRetry(string source, string dest, string backup)
    {
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            try { File.Replace(source, dest, backup); return; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Thread.Sleep(150 * attempt);
            }
        }
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(dest, backup);
        try { File.Move(source, dest); }
        catch
        {
            File.Move(backup, dest);
            throw;
        }
    }
}
