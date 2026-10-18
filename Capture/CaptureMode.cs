namespace KillerShot.Capture;

internal enum CaptureMode { Region, Window, Monitor, AllMonitors, Qr }

/// <summary>What a global shortcut does. LastMode repeats whichever mode was used last.</summary>
internal enum ShortcutAction { LastMode, Window, Monitor, AllMonitors, Video }
