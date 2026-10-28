using System.IO;
using System.Windows;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;
using Microsoft.Win32;
using Shortcut = KillerShot.Services.Shortcut;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-print-screen-key &lt;folder&gt;: the Print Screen switch. It is a
/// plain setting; the hook matches each Print Screen press to the KillerShot shortcut for its
/// modifiers and leaves every other press to Windows; the hook installs and comes off cleanly;
/// and the first version's registry change is put back once, against a scratch key, without
/// overwriting a choice made since in Settings. Windows' real setting is never touched.
/// </summary>
internal static class SelfTestPrintScreenKey
{
    private const string ScratchKey = @"Software\KillerShot\SelfTest\Keyboard";

    public static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-print-screen-key", var folder]) return false;
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        var report = new List<string>();
        int exit = 1;
        try { Run(report); exit = 0; }
        catch (Exception error) { report.Add(error.ToString()); }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerShot\SelfTest", throwOnMissingSubKey: false); }
        File.WriteAllLines(Path.Combine(folder, "print-screen-key-report.txt"), report);
        Application.Current.Shutdown(exit);
        return true;
    }

    private static void Run(List<string> report)
    {
        Check(!PrintScreenKey.Enabled, "The switch must start off.");
        PrintScreenKey.Enabled = true;
        Check(PrintScreenKey.Enabled && Settings.Get(PrintScreenKey.SettingName) == "1", "Turning it on must be saved.");
        PrintScreenKey.Enabled = false;
        Check(!PrintScreenKey.Enabled, "Turning it off must be saved.");
        report.Add("the switch is a plain saved setting: PASS");

        var active = new Dictionary<ShortcutAction, Shortcut>
        {
            [ShortcutAction.LastMode] = new(Win32.MOD_NOREPEAT, Win32.VK_SNAPSHOT, "Print Screen"),
            [ShortcutAction.Window] = new(Win32.MOD_ALT, Win32.VK_SNAPSHOT, "Alt+Print Screen"),
            [ShortcutAction.Video] = new(Win32.MOD_CONTROL | Win32.MOD_SHIFT, Win32.VK_SNAPSHOT, "Ctrl+Shift+Print Screen"),
            [ShortcutAction.Monitor] = new(Win32.MOD_CONTROL, 0x4D, "Ctrl+M"),
        };
        Check(PrintScreenHook.Matches(active, 0, out var plain) && plain == ShortcutAction.LastMode, "Plain Print Screen must run its capture.");
        Check(PrintScreenHook.Matches(active, Win32.MOD_ALT, out var alt) && alt == ShortcutAction.Window, "Alt+Print Screen must run Window.");
        Check(PrintScreenHook.Matches(active, Win32.MOD_CONTROL | Win32.MOD_SHIFT, out var video) && video == ShortcutAction.Video, "Ctrl+Shift+Print Screen must run Video.");
        Check(!PrintScreenHook.Matches(active, Win32.MOD_WIN, out _), "Win+Print Screen has no KillerShot shortcut and must pass to Windows.");
        Check(!PrintScreenHook.Matches(active, Win32.MOD_CONTROL, out _), "A shortcut on another key must never match Print Screen.");
        report.Add("each Print Screen press runs its own shortcut, and the rest pass to Windows: PASS");

        // A real hook, passing every press through, put on and taken off.
        using (var hook = new PrintScreenHook(_ => false))
            Check(hook.Installed, "The keyboard hook must install.");
        report.Add("the keyboard hook installs and comes off: PASS");

        // The first version's record: nothing recorded leaves the key alone.
        SetValue(0);
        PrintScreenKey.RestoreLegacy(ScratchKey);
        Check(Value() == 0 && !PrintScreenKey.Enabled, "With nothing recorded, the key must stay as it is.");
        // The value it replaced goes back while the key is still the 0 it wrote.
        Settings.SetRequired(PrintScreenKey.LegacyPreviousSetting, "1");
        PrintScreenKey.RestoreLegacy(ScratchKey);
        Check(Value() == 1 && Settings.Get(PrintScreenKey.LegacyPreviousSetting) == null && PrintScreenKey.Enabled,
            "The recorded value must go back, the record must go, and the switch must stay on.");
        // A value that did not exist is removed again.
        SetValue(0);
        Settings.SetRequired(PrintScreenKey.LegacyPreviousSetting, "");
        PrintScreenKey.RestoreLegacy(ScratchKey);
        Check(Value() == null, "A value that did not exist must be removed again.");
        // A choice made since in Settings stays.
        SetValue(5);
        Settings.SetRequired(PrintScreenKey.LegacyPreviousSetting, "1");
        PrintScreenKey.RestoreLegacy(ScratchKey);
        Check(Value() == 5 && Settings.Get(PrintScreenKey.LegacyPreviousSetting) == null, "A choice made since in Settings must stay.");
        report.Add("the first version's registry change is put back once, never over a newer choice: PASS");
    }

    private static void SetValue(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ScratchKey, writable: true);
        key.SetValue(PrintScreenKey.ValueName, value, RegistryValueKind.DWord);
    }

    private static int? Value()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ScratchKey);
        return key?.GetValue(PrintScreenKey.ValueName) is int value ? value : null;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
