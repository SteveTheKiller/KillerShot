using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KillerShot
{
    /// <summary>
    /// Flat key/value settings in %APPDATA%\KillerShot\settings.json, the same shape the rest of the
    /// family uses. Every read and write is best-effort: a corrupt or unwritable file costs the saved
    /// choices, never a crash on startup or a failed capture.
    /// </summary>
    internal static class Settings
    {
        private static readonly object Gate = new();
        private static string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KillerShot");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        /// <summary>Points the settings at another folder. Used by the self-tests so a test run
        /// never touches the user's choices. Call before the first read.</summary>
        internal static void UseFolder(string dir)
        {
            lock (Gate)
            {
                Dir = dir;
                _cache = null;
            }
        }

        private static Dictionary<string, string>? _cache;

        private static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            try { _cache = ReadCurrent(); }
            catch { _cache = []; }
            return _cache;
        }

        private static Dictionary<string, string> ReadCurrent()
        {
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            catch (JsonException) { return []; }
        }

        private static FileStream AcquireWriteLock()
        {
            string path = Path.Combine(Dir, "settings.json.lock");
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
            }
        }

        internal static string? Get(string key)
        {
            lock (Gate) return Load().TryGetValue(key, out var v) ? v : null;
        }

        internal static void Set(string key, string value)
        {
            try { SetRequired(key, value); }
            catch { lock (Gate) Load()[key] = value; }
        }

        /// <summary>Persists a setting before changing the cache. Lifecycle changes must fail
        /// visibly when they cannot record the file to reopen next time.</summary>
        /// <param name="value">Null removes the setting.</param>
        internal static void SetRequired(string key, string? value)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                using var writeLock = AcquireWriteLock();
                // Re-read while locked so another process's settings survive this write.
                var current = ReadCurrent();
                if (value == null ? !current.ContainsKey(key) : current.TryGetValue(key, out var old) && old == value)
                {
                    _cache = current;
                    return;
                }
                if (value == null) current.Remove(key);
                else current[key] = value;
                string temp = Path.Combine(Dir, $"settings.json.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, current, new JsonSerializerOptions { WriteIndented = true });
                        stream.Flush(true);
                    }
                    File.Move(temp, FilePath, true);
                    _cache = current;
                }
                finally { try { File.Delete(temp); } catch { } }
            }
        }

        internal static bool GetBool(string key, bool fallback) => Get(key) switch
        {
            "1" => true,
            "0" => false,
            _ => fallback,
        };

        internal static void SetBool(string key, bool value) => Set(key, value ? "1" : "0");
    }
}
