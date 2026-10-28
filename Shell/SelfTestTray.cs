using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// The tray icon: a left click opens the window instead of starting a capture, and with the
/// Print Screen switch on, the tray menu still shows the Print Screen shortcuts that the hook
/// serves when Windows keeps the key from RegisterHotKey.
/// </summary>
internal static class SelfTestTray
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-tray") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new tray test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            App.InitializeKit();

            using (var tray = new TrayHost())
            {
                int opened = 0, captures = 0;
                tray.OpenEditorRequested += () => opened++;
                tray.CaptureRequested += (_, _, _) => captures++;
                SendMessage(tray.Handle, TrayHost.CallbackMessage, IntPtr.Zero, new IntPtr(Win32.WM_LBUTTONUP));
                // Longer than the capture's wait for a menu to fade, so a deferred capture would show.
                await Task.Delay(600);
                report.Add($"Left click: opened={opened} captures={captures}");
                if (opened != 1 || captures != 0)
                    throw new InvalidOperationException("A left click on the tray icon must open the window and start no capture.");
            }

            // Another window holds Alt+Print Screen, as Windows does when it keeps the key for
            // Snipping Tool, so KillerShot's own registration of it fails. When the test cannot
            // take it, something else already holds it, which is the same condition.
            var blocker = new HwndSource(new HwndSourceParameters("KillerShotTrayTestBlocker") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
            try
            {
                bool held = Win32.RegisterHotKey(blocker.Handle, 1, Win32.MOD_ALT | Win32.MOD_NOREPEAT, Win32.VK_SNAPSHOT);
                report.Add(held ? "Alt+Print Screen held by the test" : "Alt+Print Screen already held elsewhere");
                PrintScreenKey.Enabled = true;
                using var tray = new TrayHost();
                tray.RegisterShortcuts();
                string hint = tray.TestGesture(ShortcutAction.Window);
                report.Add($"Window hint with the switch on: \"{hint}\" failed={tray.IsFailed(ShortcutAction.Window)}");
                if (tray.IsFailed(ShortcutAction.Window))
                    throw new InvalidOperationException("The hook serves Alt+Print Screen, so it is not a lost shortcut.");
                if (hint != "Alt+PrtScn")
                    throw new InvalidOperationException("The tray menu lost the Print Screen hint for a shortcut the hook serves.");
            }
            finally
            {
                Win32.UnregisterHotKey(blocker.Handle, 1);
                blocker.Dispose();
                PrintScreenKey.Enabled = false;
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "tray-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
