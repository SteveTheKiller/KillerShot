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
        private static string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KillerShot");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        /// <summary>Points the settings at another folder. Used by the self-tests so a test run
        /// never touches the user's choices. Call before the first read.</summary>
        internal static void UseFolder(string dir)
        {
            Dir = dir;
            _cache = null;
        }

        private static Dictionary<string, string>? _cache;

        private static Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            try
            {
                _cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch { _cache = []; }
            return _cache;
        }

        internal static string? Get(string key) => Load().TryGetValue(key, out var v) ? v : null;

        internal static void Set(string key, string value)
        {
            var d = Load();
            if (d.TryGetValue(key, out var old) && old == value) return;
            d[key] = value;
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch { /* unwritable profile: the setting just does not persist */ }
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
