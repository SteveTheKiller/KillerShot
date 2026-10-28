using System.IO;
using System.Windows;
using System.Windows.Interop;
using KillerShot.Native;
using KillerShot.Services;
using Microsoft.Win32;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-print-screen-key &lt;folder&gt;: the Print Screen switch against a
/// scratch registry key and scratch settings. Turning it on frees the key and saves what it
/// replaced; turning it off puts that back, but never over a choice made since in Settings; a
/// portable copy cannot turn it on; uninstall undoes only what KillerShot did.
///
/// KillerShot.exe --selftest-print-screen-live &lt;folder&gt;: the real Windows setting, turned on
/// for a moment and put back exactly. It also reports whether Print Screen registers either way;
/// on Windows 11 26300 it registers even while Windows' setting is on (Windows takes the press
/// itself instead), so registration is reported, never treated as proof that the key is free.
/// </summary>
internal static class SelfTestPrintScreenKey
{
    private const string ScratchKey = @"Software\KillerShot\SelfTest\Keyboard";

    public static bool TryRun(string[] args)
    {
        if (args is ["--selftest-print-screen-key", var folder]) { Run(Path.GetFullPath(folder), Logic); return true; }
        if (args is ["--selftest-print-screen-live", var live]) { Run(Path.GetFullPath(live), Live); return true; }
        return false;
    }

    private static void Run(string folder, Action<List<string>> test)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        var report = new List<string>();
        int exit = 1;
        try { test(report); exit = 0; }
        catch (Exception error) { report.Add(error.ToString()); }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerShot\SelfTest", throwOnMissingSubKey: false); }
        File.WriteAllLines(Path.Combine(folder, "print-screen-key-report.txt"), report);
        Application.Current.Shutdown(exit);
    }

    private static void Logic(List<string> report)
    {
        var installed = new PrintScreenKey(portable: false, ScratchKey);
        var portable = new PrintScreenKey(portable: true, ScratchKey);

        SetValue(1);
        Check(installed.State == PrintScreenKeyState.Off && installed.CanChange && installed.NoteKey == "Str_CS_PrintScreenKeyHint", "Windows owning the key must read Off.");
        Check(installed.TurnOn() && Value() == 0 && Settings.Get(PrintScreenKey.PreviousSetting) == "1" && installed.State == PrintScreenKeyState.On,
            "Turning it on must free the key and save the value it replaced.");
        installed.TurnOff();
        Check(Value() == 1 && Settings.Get(PrintScreenKey.PreviousSetting) == null && installed.State == PrintScreenKeyState.Off,
            "Turning it off must put the saved value back and forget it.");
        report.Add("on frees the key and saves the old value; off puts it back: PASS");

        installed.TurnOn();
        SetValue(1);   // Settings gave the key back to Windows
        Check(installed.State == PrintScreenKeyState.ChangedElsewhere && installed.NoteKey == "Str_CS_PrintScreenKeyChanged",
            "A key taken back in Settings must read Changed elsewhere.");
        SetValue(5);
        installed.TurnOff();
        Check(Value() == 5 && Settings.Get(PrintScreenKey.PreviousSetting) == null, "Turning it off must never overwrite a choice made since in Settings.");
        report.Add("a choice made later in Settings is reported and never overwritten: PASS");

        DeleteValue();
        Check(installed.TurnOn() && Value() == 0 && Settings.Get(PrintScreenKey.PreviousSetting) == "", "A missing value must be saved as missing.");
        installed.TurnOff();
        Check(Value() == null, "Turning it off must remove a value that was not there before.");
        report.Add("a value that did not exist is removed again on the way out: PASS");

        SetValue(1);
        Check(!portable.TurnOn() && Value() == 1 && !portable.CanChange && portable.NoteKey == "Str_CS_PrintScreenKeyPortable",
            "A portable copy must not turn it on.");
        SetValue(0);
        Check(portable.CanChange, "A portable copy that finds it on must still be able to turn it off.");
        report.Add("a portable copy cannot turn it on but can turn it off: PASS");

        SetValue(0);   // freed by the user, not by KillerShot
        installed.Undo();
        Check(Value() == 0, "Uninstall must leave a key KillerShot did not free as the user left it.");
        SetValue(1);
        installed.TurnOn();
        installed.Undo();
        Check(Value() == 1 && Settings.Get(PrintScreenKey.PreviousSetting) == null, "Uninstall must undo what KillerShot did.");
        report.Add("uninstall undoes only what KillerShot did: PASS");
    }

    /// <summary>Changes the user's real setting for about a second and always puts it back.</summary>
    private static void Live(List<string> report)
    {
        using var keyboard = Registry.CurrentUser.OpenSubKey(PrintScreenKey.KeyboardKey);
        object? original = keyboard?.GetValue(PrintScreenKey.ValueName);
        report.Add($"original_value={original ?? "missing"}");
        var key = new PrintScreenKey(portable: false);
        var window = new HwndSource(new HwndSourceParameters("KillerShot Print Screen probe") { ParentWindow = new IntPtr(-3) });
        try
        {
            bool before = TryRegister(window.Handle);
            report.Add($"register_with_windows_setting_as_found={before}");
            key.TurnOn();
            bool registered = false;
            for (int attempt = 0; attempt < 10 && !registered; attempt++)
            {
                registered = TryRegister(window.Handle);
                if (!registered) Thread.Sleep(100);
            }
            report.Add($"register_after_turning_on={registered}");
            key.TurnOff();
            using var after = Registry.CurrentUser.OpenSubKey(PrintScreenKey.KeyboardKey);
            object? restored = after?.GetValue(PrintScreenKey.ValueName);
            report.Add($"restored_value={restored ?? "missing"}");
            Check(Equals(restored, original), "Turning it off must put the original value back.");
            Check(registered, "Print Screen must be registrable while the switch is on.");
            report.Add("live: the real setting turns on and is put back exactly: PASS");
        }
        finally
        {
            // Whatever happened above, the user's setting ends as it was found.
            using (var restore = Registry.CurrentUser.CreateSubKey(PrintScreenKey.KeyboardKey, writable: true))
            {
                if (original == null) restore.DeleteValue(PrintScreenKey.ValueName, throwOnMissingValue: false);
                else restore.SetValue(PrintScreenKey.ValueName, original, RegistryValueKind.DWord);
            }
            window.Dispose();
        }
    }

    private static bool TryRegister(IntPtr window)
    {
        bool ok = Win32.RegisterHotKey(window, 0x5C21, Win32.MOD_NOREPEAT, Win32.VK_SNAPSHOT);
        if (ok) Win32.UnregisterHotKey(window, 0x5C21);
        return ok;
    }

    private static void SetValue(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ScratchKey, writable: true);
        key.SetValue(PrintScreenKey.ValueName, value, RegistryValueKind.DWord);
    }

    private static void DeleteValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(ScratchKey, writable: true);
        key.DeleteValue(PrintScreenKey.ValueName, throwOnMissingValue: false);
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
