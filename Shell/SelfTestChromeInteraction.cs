using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestChromeInteraction
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-chrome-interaction") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        var failures = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new chrome test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            // Query native hit testing on an invisible, nonactivating window. No pointer input.
            var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.Show();
            window.Left = -20000;
            window.Top = -20000;
            foreach (Theme theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var hwnd = new WindowInteropHelper(window).Handle;
                if (!GetWindowRect(hwnd, out var bounds)) throw new InvalidOperationException("No native window bounds.");
                double scale = VisualTreeHelper.GetDpi(window).DpiScaleY;
                foreach (int inset in new[] { 1, 3, 6 })
                {
                    int hit = Hit(hwnd, (bounds.Left + bounds.Right) / 2,
                        bounds.Bottom - Math.Max(1, (int)Math.Round(inset * scale)));
                    report.Add($"{theme} bottom inset={inset} hit={hit}");
                    if (hit != 15) failures.Add($"{theme}: bottom {inset}px must resize (HTBOTTOM), got {hit}.");
                }
                var viewButton = Visuals((DependencyObject)window.FindName("FooterBar")).OfType<Button>()
                    .First(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0);
                Point center = viewButton.PointToScreen(new Point(viewButton.ActualWidth / 2, viewButton.ActualHeight / 2));
                int client = Hit(hwnd, (int)center.X, (int)center.Y);
                report.Add($"{theme} footer button center hit={client}");
                if (client != 1) failures.Add($"{theme}: footer button center must remain clickable.");
            }
            window.Close();
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        report.AddRange(failures.Select(f => "FAIL " + f));
        report.Add(failures.Count == 0 ? "result=ok" : "result=error");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        App.Instance.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private static int Hit(IntPtr hwnd, int x, int y)
    {
        long packed = ((long)(y & 0xffff) << 16) | (uint)(x & 0xffff);
        return (int)SendMessage(hwnd, 0x0084, IntPtr.Zero, new IntPtr(packed));
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
