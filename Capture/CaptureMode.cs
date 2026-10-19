namespace KillerShot.Capture;

internal enum CaptureMode { Region, Window, Monitor, AllMonitors, Qr }

/// <summary>What a global shortcut does. LastMode is the saved key for the Region shortcut.</summary>
internal enum ShortcutAction { LastMode, Window, Monitor, AllMonitors, Video }
