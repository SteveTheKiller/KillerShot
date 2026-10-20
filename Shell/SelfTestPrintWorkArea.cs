using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestPrintWorkArea
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-print-workarea", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        int code = 1;
        PrintPreviewWindow? preview = null;
        var report = new List<string>();
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            App.InitializeKit();
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            preview = new PrintPreviewWindow(null, pixel, "Synthetic work-area fixture");
            preview.Show();
            await Task.Delay(400);
            preview.UpdateLayout();
            var hwnd = new WindowInteropHelper(preview).Handle;
            if (!Win32.GetWindowRect(hwnd, out var bounds)) throw new InvalidOperationException("Could not measure the preview HWND.");
            var info = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = "" };
            if (!Win32.GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) throw new InvalidOperationException("Could not measure the monitor work area.");
            var dpi = VisualTreeHelper.GetDpi(preview);
            var controls = Descendants(preview).OfType<Button>().Where(b => b.IsVisible).Select(b =>
            {
                Point point = b.PointToScreen(new Point());
                string name = System.Windows.Automation.AutomationProperties.GetName(b);
                return new { Name = name.Length > 0 ? name : b.Content?.ToString(), X = point.X, Y = point.Y,
                    Width = b.ActualWidth * dpi.DpiScaleX, Height = b.ActualHeight * dpi.DpiScaleY };
            }).ToArray();
            var image = new RenderTargetBitmap((int)Math.Ceiling(preview.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(preview.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(preview);
            File.WriteAllBytes(Path.Combine(folder, "print-preview.png"), ImageOutput.EncodePng(image));
            File.WriteAllText(Path.Combine(folder, "bounds.json"), JsonSerializer.Serialize(new
            {
                DpiX = dpi.PixelsPerInchX, DpiY = dpi.PixelsPerInchY,
                Window = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
                WorkArea = new { info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom }, Controls = controls
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (bounds.Left < info.rcWork.Left || bounds.Top < info.rcWork.Top || bounds.Right > info.rcWork.Right || bounds.Bottom > info.rcWork.Bottom)
                throw new InvalidOperationException("Print preview extends beyond the actual monitor work area.");
            if (controls.Any(b => b.X < info.rcWork.Left || b.Y < info.rcWork.Top || b.X + b.Width > info.rcWork.Right || b.Y + b.Height > info.rcWork.Bottom))
                throw new InvalidOperationException("A print-preview control lies outside the work area.");
            report.Add("Actual print preview and visible controls fit inside monitor work area. No print job was submitted: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            preview?.Close();
            File.WriteAllLines(Path.Combine(folder, "print-workarea-report.txt"), report);
            App.Current.Shutdown(code);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
