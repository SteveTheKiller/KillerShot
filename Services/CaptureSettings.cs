using KillerShot.Capture;

namespace KillerShot.Services;

/// <summary>Typed access to the capture choices stored in <see cref="Settings"/>.</summary>
internal static class CaptureSettings
{
    /// <summary>Default shortcut per action. Plain Print Screen repeats the last mode used.</summary>
    public static readonly IReadOnlyDictionary<ShortcutAction, string> Defaults = new Dictionary<ShortcutAction, string>
    {
        [ShortcutAction.LastMode] = "Print Screen",
        [ShortcutAction.Window] = "Alt+Print Screen",
        [ShortcutAction.Monitor] = "Ctrl+Print Screen",
        [ShortcutAction.AllMonitors] = "Shift+Print Screen",
    };

    public static CaptureMode LastMode
    {
        get => Enum.TryParse<CaptureMode>(Settings.Get("LastMode"), out var m) ? m : CaptureMode.Region;
        set => Settings.Set("LastMode", value.ToString());
    }

    /// <summary>The stored shortcut text, the default when nothing is stored, or empty when the
    /// user turned the shortcut off.</summary>
    public static string ShortcutText(ShortcutAction action) =>
        Settings.Get("Shortcut." + action) ?? Defaults[action];

    public static void SetShortcutText(ShortcutAction action, string text) =>
        Settings.Set("Shortcut." + action, text);

    /// <summary>Open the editor after every capture. Off by default: the clipboard is the point.</summary>
    public static bool OpenEditorAfterCapture
    {
        get => Settings.GetBool("OpenEditorAfterCapture", false);
        set => Settings.SetBool("OpenEditorAfterCapture", value);
    }

    /// <summary>Show a notification after each capture.</summary>
    public static bool Notify
    {
        get => Settings.GetBool("NotifyAfterCapture", true);
        set => Settings.SetBool("NotifyAfterCapture", value);
    }
}
