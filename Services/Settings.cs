using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerShot.Capture;

namespace KillerShot.Services;

/// <summary>Per-user settings in %APPDATA%\KillerShot\settings.json. A missing or unreadable
/// file falls back to defaults rather than blocking startup.</summary>
internal sealed class Settings
{
    public CaptureMode LastMode { get; set; } = CaptureMode.Region;

    /// <summary>Shortcut per action, written as "Print Screen", "Alt+Print Screen" and so on.
    /// Editing these in the interface is planned; the file is the source of truth meanwhile.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new()
    {
        [nameof(ShortcutAction.LastMode)] = "Print Screen",
        [nameof(ShortcutAction.Window)] = "Alt+Print Screen",
        [nameof(ShortcutAction.Monitor)] = "Ctrl+Print Screen",
        [nameof(ShortcutAction.AllMonitors)] = "Shift+Print Screen",
    };

    public static Settings Current { get; private set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KillerShot", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { Current = new Settings(); }
        // Fill any action a hand-edited file left out.
        foreach (var kv in new Settings().Shortcuts)
            Current.Shortcuts.TryAdd(kv.Key, kv.Value);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { /* Settings are a convenience; a failed write must not break a capture. */ }
    }
}
