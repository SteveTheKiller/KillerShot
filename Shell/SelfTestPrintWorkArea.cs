using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Win32.RECT rect);
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
        Window? owner = null;
        var report = new List<string>();
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            App.InitializeKit();
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            foreach (bool owned in new[] { false, true })
            {
            string output = owned ? Path.Combine(folder, "owned") : folder;
            Directory.CreateDirectory(output);
            if (owned)
            {
                owner = new Window { Title = "Synthetic print owner", Width = 800, Height = 600,
                    ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                owner.Show();
                // Exercise owner centering near the edge without moving any real app window.
                owner.Left = SystemParameters.WorkArea.Right - 200;
                owner.Top = SystemParameters.WorkArea.Bottom - 160;
            }
            preview = new PrintPreviewWindow(owner, pixel, "Synthetic work-area fixture");
            preview.Show();
            await Task.Delay(400);
            preview.UpdateLayout();
            var hwnd = new WindowInteropHelper(preview).Handle;
            if (!Win32.GetWindowRect(hwnd, out var bounds)) throw new InvalidOperationException("Could not measure the preview HWND.");
            if (!GetClientRect(hwnd, out var client)) throw new InvalidOperationException("Could not measure the preview client area.");
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
            File.WriteAllBytes(Path.Combine(output, "print-preview.png"), ImageOutput.EncodePng(image));
            File.WriteAllText(Path.Combine(output, "bounds.json"), JsonSerializer.Serialize(new
            {
                Owned = owned,
                DpiX = dpi.PixelsPerInchX, DpiY = dpi.PixelsPerInchY,
                Window = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
                Client = new { client.Left, client.Top, client.Right, client.Bottom },
                WorkArea = new { info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom }, Controls = controls
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (bounds.Left < info.rcWork.Left || bounds.Top < info.rcWork.Top || bounds.Right > info.rcWork.Right || bounds.Bottom > info.rcWork.Bottom)
                throw new InvalidOperationException("Print preview extends beyond the actual monitor work area.");
            if (controls.Any(b => b.X < info.rcWork.Left || b.Y < info.rcWork.Top || b.X + b.Width > info.rcWork.Right || b.Y + b.Height > info.rcWork.Bottom))
                throw new InvalidOperationException("A print-preview control lies outside the work area.");
            var scroller = Descendants(preview).OfType<ScrollViewer>().First(s =>
                s.Content is StackPanel panel && panel.Children.OfType<TextBox>().Any());
            var settings = ((StackPanel)scroller.Content).Children.OfType<Control>().ToArray();
            if (settings.Length != 6) throw new InvalidOperationException("Expected six print settings.");
            var focusChecks = new List<object>();
            if (!settings[0].Focus()) throw new InvalidOperationException("Could not focus the printer setting.");
            for (int i = 0; i < settings.Length; i++)
            {
                if (i > 0 && !settings[i - 1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                    throw new InvalidOperationException("Print settings traversal failed.");
                await Task.Delay(50);
                preview.UpdateLayout();
                var setting = settings[i];
                Rect visible = setting.TransformToAncestor(scroller).TransformBounds(new Rect(setting.RenderSize));
                if (!setting.IsKeyboardFocusWithin || visible.Top < -1 || visible.Bottom > scroller.ActualHeight + 1)
                    throw new InvalidOperationException("A keyboard-focused print setting is outside its scroll viewport.");
                focusChecks.Add(new { Name = System.Windows.Automation.AutomationProperties.GetName(setting),
                    visible.Top, visible.Bottom, Viewport = scroller.ViewportHeight,
                    Extent = scroller.ExtentHeight, Offset = scroller.VerticalOffset });
            }
            File.WriteAllText(Path.Combine(output, "settings-focus.json"), JsonSerializer.Serialize(focusChecks,
                new JsonSerializerOptions { WriteIndented = true }));
            image = new RenderTargetBitmap((int)Math.Ceiling(preview.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(preview.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(preview);
            File.WriteAllBytes(Path.Combine(output, "print-preview-scrolled.png"), ImageOutput.EncodePng(image));
            preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(preview),
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            if (preview.IsVisible) throw new InvalidOperationException("Escape did not close print preview.");
            owner?.Close(); owner = null;
            report.Add($"{(owned ? "Owned" : "Ownerless")} preview, caption, buttons and keyboard settings fit the work area; Escape closes without printing: PASS");
            }
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            preview?.Close();
            owner?.Close();
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
