using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-capture-latency &lt;new folder&gt;: presses Print Screen for real, through
/// the tray's hook and the capture controller the app uses, and times the press until the region
/// overlay is on screen. Each press is canceled with Escape. The desktop freeze and its window list
/// are also timed on their own, so a slow press can be traced to its stage. Fails when the median
/// press takes longer than the budget. Opens windows, so it runs on a lab machine.
/// </summary>
internal static class SelfTestCaptureLatency
{
    private const int Presses = 5;
    private const double BudgetMs = 300;

    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-capture-latency", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("The report folder must be new.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            PrintScreenKey.Enabled = true;
            report.Add($"Monitors: {FrozenDesktop.EnumerateMonitors().Count}, virtual desktop " +
                $"{Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN)}x{Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN)}");

            for (int pass = 1; pass <= 3; pass++)
            {
                var clock = Stopwatch.StartNew();
                FrozenDesktop.Capture();
                double freeze = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                int windows = FrozenDesktop.EnumerateWindows().Count;
                report.Add($"Freeze pass {pass}: Capture {freeze:F0} ms (includes the window list), window list alone {clock.Elapsed.TotalMilliseconds:F0} ms for {windows} windows");
            }

            using var tray = new TrayHost();
            var clockAll = Stopwatch.StartNew();
            double requested = -1;
            tray.CaptureRequested += (_, _, _) => requested = clockAll.Elapsed.TotalMilliseconds;
            _ = new CaptureController(tray);
            tray.RegisterShortcuts();
            await Task.Delay(500);

            var totals = new List<double>();
            for (int press = 1; press <= Presses; press++)
            {
                requested = -1;
                clockAll.Restart();
                Press(0x2C);
                double shown = -1;
                while (clockAll.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (Application.Current.Windows.OfType<RegionOverlay>().Any(w => w.IsVisible))
                    {
                        shown = clockAll.Elapsed.TotalMilliseconds;
                        break;
                    }
                    await Task.Delay(5);
                }
                report.Add($"Press {press}: request {requested:F0} ms, overlay {shown:F0} ms");
                if (shown < 0) throw new InvalidOperationException($"Press {press} never showed the overlay.");
                totals.Add(shown);
                Press(0x1B);
                for (int i = 0; i < 200 && Application.Current.Windows.OfType<RegionOverlay>().Any(w => w.IsVisible); i++) await Task.Delay(10);
                await Task.Delay(400);
            }

            // A press after the app has sat in the tray: Windows trims an idle process's memory, so
            // this is the press people actually make. The working set is trimmed the same way.
            for (int idle = 1; idle <= 2; idle++)
            {
                await Task.Delay(TimeSpan.FromSeconds(45));
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                await Task.Delay(TimeSpan.FromSeconds(15));
                clockAll.Restart();
                Press(0x2C);
                double shown = -1;
                while (clockAll.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (Application.Current.Windows.OfType<RegionOverlay>().Any(w => w.IsVisible)) { shown = clockAll.Elapsed.TotalMilliseconds; break; }
                    await Task.Delay(5);
                }
                report.Add($"Idle press {idle} (60 s idle, memory trimmed): overlay {shown:F0} ms");
                if (shown < 0) throw new InvalidOperationException("An idle press never showed the overlay.");
                totals.Add(shown);
                Press(0x1B);
                for (int i = 0; i < 200 && Application.Current.Windows.OfType<RegionOverlay>().Any(w => w.IsVisible); i++) await Task.Delay(10);
            }

            totals.Sort();
            double median = totals[totals.Count / 2];
            report.Add($"Median press to overlay: {median:F0} ms (budget {BudgetMs:F0} ms)");
            code = median <= BudgetMs ? 0 : 1;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            PrintScreenKey.Enabled = false;
            report.Add(code == 0 ? "result=ok" : "result=error");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
            Application.Current.Shutdown(code);
        }
    }

    private static void Press(ushort key)
    {
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = key } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = key, dwFlags = 2 } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
