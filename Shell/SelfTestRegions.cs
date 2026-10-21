using System.IO;
using System.Runtime.InteropServices;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-regions &lt;folder&gt;. The last region repeats only
/// on the monitor layout it was taken on; saved regions keep their place or size; and interval
/// capture takes exactly its count, stops cleanly with nothing more afterward, and never touches
/// the clipboard. The interval part captures a 1 x 1 pixel region into the test folder.</summary>
internal static class SelfTestRegions
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-regions") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        TrayHost? tray = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new regions test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);

            // Layout: two side-by-side monitors; the region repeats only on this layout.
            MonitorBounds[] two = [new(IntPtr.Zero, new Win32.RECT(0, 0, 1920, 1080), true), new(IntPtr.Zero, new Win32.RECT(1920, 0, 3840, 1080), false)];
            MonitorBounds[] one = [two[0]];
            var area = new Win32.RECT(1800, 100, 2100, 300);   // spans both monitors
            RegionMemory.Remember(area, two);
            Expect(RegionMemory.TryLast(two, out var again) && again.Equals(area), "The last region did not repeat on the same layout.");
            Expect(!RegionMemory.TryLast(one, out _), "The last region repeated after a monitor was removed.");
            MonitorBounds[] rescaled = [two[0], new(IntPtr.Zero, new Win32.RECT(1920, 0, 4480, 1440), false)];
            Expect(!RegionMemory.TryLast(rescaled, out _), "The last region repeated after a monitor changed resolution.");
            report.Add("repeat region only on the layout it was taken on PASS");

            // Saved regions: replace by name, fixed sizes stay inside the pointer's monitor.
            RegionMemory.SavePreset(new RegionPreset("Ticket", area, area.Width, area.Height));
            RegionMemory.SavePreset(new RegionPreset("Square", null, 400, 400));
            RegionMemory.SavePreset(new RegionPreset("ticket", new Win32.RECT(10, 10, 110, 60), 100, 50));
            Expect(RegionMemory.Presets.Count == 2 && RegionMemory.Presets.Single(p => p.Name == "ticket").Width == 100, "Saving by an existing name did not replace it.");
            Expect(RegionMemory.Resolve(RegionMemory.Presets.Single(p => p.Name == "Square"), new Win32.POINT { X = 3830, Y = 5 }, two, out var square)
                && square.Equals(new Win32.RECT(3440, 0, 3840, 400)), "A fixed size near the edge was not kept inside its monitor: " + square);
            Expect(!RegionMemory.Resolve(new RegionPreset("Gone", new Win32.RECT(2000, 0, 2100, 50), 100, 50), default, one, out _),
                "A saved place off the screen was captured.");
            for (int i = 0; i < RegionMemory.MaxPresets + 3; i++) RegionMemory.SavePreset(new RegionPreset("P" + i, null, 100 + i, 100));
            Expect(RegionMemory.Presets.Count == RegionMemory.MaxPresets, "Saved regions grew past their limit.");
            RegionMemory.RemovePreset("P14");
            Expect(RegionMemory.Presets.All(p => p.Name != "P14"), "Removing a saved region did not remove it.");
            report.Add("saved regions replace, clamp, refuse offscreen, cap and remove PASS");

            // Interval: a real 1 x 1 capture, three times, then nothing after it stops.
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            string output = Path.Combine(folder, "output");
            Directory.CreateDirectory(output);
            CaptureSettings.OutputFolder = output;
            CaptureSettings.SaveToFolder = true;
            CaptureSettings.CopyToClipboard = true;   // interval capture must ignore it
            CaptureSettings.PlaySound = false;
            CaptureSettings.DelaySeconds = 0;
            var primary = FrozenDesktop.EnumerateMonitors().First(m => m.Primary).Bounds;
            RegionMemory.Remember(new Win32.RECT(primary.Left, primary.Top, primary.Left + 1, primary.Top + 1), FrozenDesktop.EnumerateMonitors());
            tray = new TrayHost();
            var capture = new CaptureController(tray);
            uint clipboard = GetClipboardSequenceNumber();
            Expect(capture.StartInterval(1, count: 3), "Interval capture did not start.");
            for (int i = 0; i < 80 && capture.IntervalRunning; i++) await Task.Delay(50);
            await Task.Delay(1500);
            int files = Directory.GetFiles(output).Length;
            report.Add($"interval: taken={capture.IntervalTaken} files={files} album={CaptureLibrary.Count} clipboard_changed={GetClipboardSequenceNumber() != clipboard}");
            Expect(!capture.IntervalRunning && capture.IntervalTaken == 3 && files == 3 && CaptureLibrary.Count == 3, "Interval capture did not take exactly its count.");
            Expect(GetClipboardSequenceNumber() == clipboard, "Interval capture wrote to the clipboard.");

            Expect(capture.StartInterval(1), "Unlimited interval capture did not start.");
            await Task.Delay(300);
            capture.StopInterval();
            int stoppedAt = Directory.GetFiles(output).Length;
            await Task.Delay(2500);
            Expect(Directory.GetFiles(output).Length == stoppedAt && stoppedAt == 4, "Captures kept coming after interval capture stopped.");
            report.Add("interval takes exactly its count, stops cleanly, leaves the clipboard alone PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "regions-report.txt"), report);
            tray?.Dispose();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
