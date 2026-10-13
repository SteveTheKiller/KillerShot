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

    /// <summary>Copy each capture to the clipboard. On by default: the clipboard is the point.</summary>
    public static bool CopyToClipboard
    {
        get => Settings.GetBool("CopyToClipboard", true);
        set => Settings.SetBool("CopyToClipboard", value);
    }

    /// <summary>Save each capture straight to the output folder, with no dialog.</summary>
    public static bool SaveToFolder
    {
        get => Settings.GetBool("SaveToFolder", true);
        set => Settings.SetBool("SaveToFolder", value);
    }

    /// <summary>The output folder, or Windows' own Screenshots folder when none is chosen.</summary>
    public static string OutputFolder
    {
        get => Settings.Get("OutputFolder") is { Length: > 0 } f ? f : OutputFiles.DefaultFolder;
        set => Settings.Set("OutputFolder", value);
    }

    public const string DefaultFileNamePattern = "KillerShot {yyyy}-{MM}-{dd} {HH}{mm}{ss}";

    public static string FileNamePattern
    {
        get => Settings.Get("FileNamePattern") is { Length: > 0 } p ? p : DefaultFileNamePattern;
        set => Settings.Set("FileNamePattern", value);
    }

    /// <summary>"png" or "jpg".</summary>
    public static string Format
    {
        get => Settings.Get("Format") == "jpg" ? "jpg" : "png";
        set => Settings.Set("Format", value == "jpg" ? "jpg" : "png");
    }

    public static int JpegQuality
    {
        get => int.TryParse(Settings.Get("JpegQuality"), out int q) ? Math.Clamp(q, 10, 100) : 90;
        set => Settings.Set("JpegQuality", Math.Clamp(value, 10, 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Seconds to wait before freezing the desktop, so a menu or hover state can be opened.</summary>
    public static int DelaySeconds
    {
        get => int.TryParse(Settings.Get("DelaySeconds"), out int d) ? Math.Clamp(d, 0, 30) : 0;
        set => Settings.Set("DelaySeconds", Math.Clamp(value, 0, 30).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Show the save dialog after each capture.</summary>
    public static bool AskToSave
    {
        get => Settings.GetBool("AskToSave", false);
        set => Settings.SetBool("AskToSave", value);
    }

    public static bool IncludeCursor
    {
        get => Settings.GetBool("IncludeCursor", false);
        set => Settings.SetBool("IncludeCursor", value);
    }

    public static bool PlaySound
    {
        get => Settings.GetBool("PlaySound", false);
        set => Settings.SetBool("PlaySound", value);
    }

    /// <summary>Window mode lets the user click the window to capture instead of taking the
    /// active one.</summary>
    public static bool PickWindow
    {
        get => Settings.GetBool("PickWindow", false);
        set => Settings.SetBool("PickWindow", value);
    }

    public static bool PrintShrink
    {
        get => Settings.GetBool("PrintShrink", true);
        set => Settings.SetBool("PrintShrink", value);
    }

    public static bool PrintEnlarge
    {
        get => Settings.GetBool("PrintEnlarge", false);
        set => Settings.SetBool("PrintEnlarge", value);
    }

    public static bool PrintRotate
    {
        get => Settings.GetBool("PrintRotate", true);
        set => Settings.SetBool("PrintRotate", value);
    }

    /// <summary>Show a notification after each capture.</summary>
    public static bool Notify
    {
        get => Settings.GetBool("NotifyAfterCapture", true);
        set => Settings.SetBool("NotifyAfterCapture", value);
    }
}
