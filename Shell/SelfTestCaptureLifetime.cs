using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using KillerShot.Capture;
using KillerShot.Native;

namespace KillerShot.Shell;

/// <summary>Checks native COM ownership through the capture factory's system boundaries.</summary>
internal static class SelfTestCaptureLifetime
{
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    public static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-capture-lifetime") return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        _ = RunAsync(dir, args[2]);
        return true;
    }

    private static async Task RunAsync(string dir, string scenario)
    {
        var log = new StringBuilder();
        int exit = 0;
        try
        {
            await Task.Run(() => CheckFailedCreation(log, scenario));
            log.AppendLine("result=ok");
        }
        catch (Exception error) { log.AppendLine($"result=error {error}"); exit = 1; }
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Application.Current.Shutdown(exit);
    }

    private static int References(IntPtr pointer)
    {
        int count = Marshal.AddRef(pointer) - 1;
        Marshal.Release(pointer);
        return count;
    }

    private static void CheckFailedCreation(StringBuilder log, string scenario)
    {
        if (scenario is not ("device" or "part" or "success")) throw new ArgumentException("Unknown lifetime scenario.");
        if (!WindowCapture.IsSupported) throw new InvalidOperationException("Windows Graphics Capture is unavailable.");
        var monitors = FrozenDesktop.EnumerateMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("No monitor is available for the fixture.");
        int x = monitors.Max(monitor => monitor.Bounds.Right) + 100;
        var rect = new Win32.RECT(x, 0, x + 2, 2);
        bool requestedSecondMonitor = false;
        IEnumerable<MonitorBounds> PartialMonitors()
        {
            yield return monitors[0];
            // Enumeration advances only after the first monitor's resources are initialized.
            requestedSecondMonitor = true;
            yield return monitors[0] with { Handle = IntPtr.Zero };
        }
        if (scenario is "part" or "success")
            rect = new Win32.RECT(monitors[0].Bounds.Left, monitors[0].Bounds.Top,
                monitors[0].Bounds.Left + 2, monitors[0].Bounds.Top + 2);
        int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out IntPtr device, out _, out IntPtr context);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            int deviceBefore = References(device), contextBefore = References(context), allocations = 0;
            (IntPtr Device, IntPtr Context) Allocate()
            {
                allocations++;
                // Keep the fixture's original references and transfer a separate owned pair.
                Marshal.AddRef(device);
                Marshal.AddRef(context);
                return (device, context);
            }
            using var grabber = MonitorGrabber.TryCreate(rect, false, Allocate, scenario == "part" ? PartialMonitors : null);
            bool captured = scenario == "success" && grabber?.Grab(new byte[16]) == true;
            if (scenario == "success") grabber?.Dispose();
            // Allow WinRT wrappers to finalize. Raw owned references must also be released.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int deviceAfter = References(device), contextAfter = References(context);
            log.AppendLine($"fallback_null={grabber == null} allocations={allocations} device_refs={deviceBefore}->{deviceAfter} context_refs={contextBefore}->{contextAfter}");
            if (scenario == "part") log.AppendLine($"first_monitor_initialized={requestedSecondMonitor}");
            if (scenario == "part" && !requestedSecondMonitor)
                throw new InvalidOperationException("The fixture failed before its first monitor initialized.");
            if (scenario == "success") log.AppendLine($"normal_frame_captured={captured}");
            bool expectedResult = scenario == "success" ? captured : grabber == null;
            if (!expectedResult || allocations != 1 || deviceBefore != deviceAfter || contextBefore != contextAfter)
                throw new InvalidOperationException("Failed monitor creation retained native COM ownership.");
        }
        finally { Marshal.Release(context); Marshal.Release(device); }
    }
}
