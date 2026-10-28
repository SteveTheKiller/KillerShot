using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using KillerShot.Capture;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-print-screen-helper &lt;new folder&gt; &lt;test helper exe&gt;: the
/// helper that catches Print Screen over windows run as administrator. The test helper is
/// PrintScreenHelper published with -p:PrintScreenUiAccess=false, since a UI Access file only
/// starts from Program Files with a trusted signature. It checks that KillerShot.exe carries the
/// UI Access build; that the tray starts the helper with the switch on and hears from it; that a
/// real Print Screen press reaches KillerShot through the helper exactly once; that the helper
/// quits when the tray lets it go; and that it refuses a window that is not KillerShot's.
/// Whether Windows then grants UI Access over an admin window needs the signed, installed helper.
/// </summary>
internal static class SelfTestPrintScreenHelper
{
    internal static bool TryRun(string[] args)
    {
        if (args is ["--selftest-print-screen-helper-child", var childFolder, var childHelper])
        {
            // A KillerShot that starts the helper and then waits to be killed, standing in for a crash.
            Settings.UseFolder(Path.Combine(Path.GetFullPath(childFolder), "settings"));
            App.InitializeKit();
            PrintScreenHelper.PathOverride = Path.GetFullPath(childHelper);
            PrintScreenKey.Enabled = true;
            new TrayHost().RegisterShortcuts();
            return true;
        }
        if (args is not ["--selftest-print-screen-helper", var folder, var helper]) return false;
        _ = RunAsync(Path.GetFullPath(folder), Path.GetFullPath(helper));
        return true;
    }

    private static async Task RunAsync(string folder, string helper)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new helper test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            App.InitializeKit();

            // KillerShot.exe carries the UI Access build of the helper.
            string extracted = Path.Combine(folder, "extracted", PrintScreenHelper.FileName);
            PrintScreenHelper.Install(extracted, requireSignature: false);
            bool uiAccess = File.ReadAllText(extracted).Contains("uiAccess=\"true\"", StringComparison.Ordinal);
            report.Add($"Carried helper: {new FileInfo(extracted).Length} bytes, UI Access manifest={uiAccess}");
            Check(uiAccess, "KillerShot.exe must carry the UI Access build of the helper.");

            PrintScreenHelper.PathOverride = helper;
            PrintScreenKey.Enabled = true;
            var captures = new List<ShortcutAction?>();
            var tray = new TrayHost();
            bool released = false;
            try
            {
                tray.CaptureRequested += (action, _, _) => captures.Add(action);
                tray.RegisterShortcuts();
                for (int i = 0; i < 50 && !tray.HelperListening; i++) await Task.Delay(100);
                report.Add($"Helper listening: {tray.HelperListening}");
                Check(tray.HelperListening, "The helper must start and report in while the switch is on.");
                var running = Running(helper);
                Check(running.Count == 1, $"Exactly one helper must run, found {running.Count}.");

                // A real Print Screen press. The helper's hook is the newest, so it sees the key
                // first and takes it; KillerShot hears about it once, through the helper.
                Press(0x2C);
                for (int i = 0; i < 30 && captures.Count == 0; i++) await Task.Delay(100);
                await Task.Delay(300);
                report.Add($"Print Screen press: captures={captures.Count} through helper={tray.HelperPresses} action={captures.FirstOrDefault()}");
                Check(captures.Count == 1 && tray.HelperPresses == 1 && captures[0] == ShortcutAction.LastMode,
                    "One Print Screen press must reach KillerShot once, through the helper, as the Print Screen shortcut.");

                tray.Dispose();
                released = true;
                bool stopped = running[0].WaitForExit(3000);
                report.Add($"Helper quit when the tray let it go: {stopped}");
                Check(stopped, "The helper must quit when KillerShot stops it.");
            }
            finally
            {
                if (!released) tray.Dispose();
                foreach (var left in Running(helper)) { try { left.Kill(); } catch { } }
            }

            // KillerShot killed outright, as in a crash: the helper must still let Print Screen go.
            string exe = Process.GetCurrentProcess().MainModule!.FileName;
            using (var child = Process.Start(new ProcessStartInfo(exe, $"--selftest-print-screen-helper-child \"{Path.Combine(folder, "child")}\" \"{helper}\"") { UseShellExecute = false })!)
            {
                List<Process> orphan = [];
                for (int i = 0; i < 80 && orphan.Count == 0; i++) { await Task.Delay(100); orphan = Running(helper); }
                Check(orphan.Count == 1, "The second KillerShot must start its own helper.");
                child.Kill();
                bool followed = orphan[0].WaitForExit(3000);
                report.Add($"Helper quit after KillerShot was killed: {followed}");
                Check(followed, "The helper must quit when KillerShot exits without stopping it.");
            }

            // Pointed at a window that is not that process's, it refuses to run.
            var explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
            if (explorer != null)
            {
                using var stray = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\KillerShot.PrintScreen.SelfTest." + Environment.ProcessId);
                using var probe = new TrayHost();
                var start = new ProcessStartInfo(helper, $"{explorer.Id} {probe.Handle.ToInt64()} 0 Local\\KillerShot.PrintScreen.SelfTest.{Environment.ProcessId}") { UseShellExecute = false };
                using var refused = Process.Start(start)!;
                bool exited = refused.WaitForExit(3000);
                report.Add($"Window of another process: exited={exited} code={(exited ? refused.ExitCode : -1)}");
                Check(exited && refused.ExitCode == 3, "The helper must refuse a window that is not the named process's.");
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            PrintScreenHelper.PathOverride = null;
            PrintScreenKey.Enabled = false;
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "print-screen-helper-report.txt"), report);
            Application.Current.Shutdown(code);
        }
    }

    private static List<Process> Running(string helper) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(helper))
            .Where(p => { try { return string.Equals(p.MainModule?.FileName, helper, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
            .ToList();

    private static void Press(ushort key)
    {
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = key } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = key, dwFlags = 2 } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
