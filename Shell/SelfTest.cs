using System.IO;
using System.Diagnostics;
using System.Text;
using KillerShot.Capture;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest &lt;folder&gt; [--window &lt;hwnd&gt;].
/// Freezes the desktop, writes every monitor and the whole desktop as PNG, optionally captures
/// one window through Windows Graphics Capture, and writes timings to report.txt. It never
/// shows a window or takes focus.</summary>
internal static class SelfTest
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        int i = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest");
        IntPtr window = IntPtr.Zero;
        int w = Array.FindIndex(args, a => a.Equals("--window", StringComparison.OrdinalIgnoreCase));
        if (w >= 0 && w + 1 < args.Length && long.TryParse(args[w + 1], out long hv)) window = new IntPtr(hv);

        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            var sw = Stopwatch.StartNew();
            var frozen = FrozenDesktop.Capture();
            log.AppendLine($"freeze_ms={sw.Elapsed.TotalMilliseconds:F1}");
            log.AppendLine($"desktop={frozen.Bounds}");
            for (int m = 0; m < frozen.Monitors.Count; m++)
            {
                var mon = frozen.Monitors[m];
                var bmp = frozen.Crop(mon.Bounds)!;
                ImageOutput.SavePng(bmp, Path.Combine(dir, $"monitor{m}.png"));
                log.AppendLine($"monitor{m}={mon.Bounds} primary={mon.Primary} png={bmp.PixelWidth}x{bmp.PixelHeight} dpi={bmp.DpiX}");
            }
            sw.Restart();
            var all = frozen.Crop(frozen.Bounds)!;
            ImageOutput.SavePng(all, Path.Combine(dir, "all.png"));
            log.AppendLine($"all_png_ms={sw.Elapsed.TotalMilliseconds:F1} size={all.PixelWidth}x{all.PixelHeight}");

            if (window != IntPtr.Zero)
            {
                log.AppendLine($"wgc_supported={WindowCapture.IsSupported}");
                var root = WindowCapture.CapturableRoot(window);
                log.AppendLine($"window_root=0x{root.ToInt64():X} bounds={WindowCapture.FrameBounds(window)}");
                sw.Restart();
                var (bmp, usedWgc) = Task.Run(() => WindowCapture.CaptureAsync(window, frozen)).GetAwaiter().GetResult();
                log.AppendLine($"window_ms={sw.Elapsed.TotalMilliseconds:F1} used_wgc={usedWgc} size={(bmp == null ? "none" : $"{bmp.PixelWidth}x{bmp.PixelHeight}")}");
                if (bmp != null) ImageOutput.SavePng(bmp, Path.Combine(dir, "window.png"));
            }
            log.AppendLine("result=ok");
        }
        catch (Exception ex)
        {
            log.AppendLine($"result=error {ex}");
            exitCode = 1;
        }
        try { File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString()); } catch { exitCode = 2; }
        return true;
    }
}
