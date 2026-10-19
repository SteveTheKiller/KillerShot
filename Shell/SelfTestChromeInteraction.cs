using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

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
            foreach (Theme theme in new[] { Theme.Dark, Theme.Light, Theme.SE98, Theme.Dark })
            {
                ThemeManager.Apply(theme);
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var hwnd = new WindowInteropHelper(window).Handle;
                int expectedCorner = theme == Theme.SE98 ? 1 : 2;
                CheckCorner(expectedCorner, theme + " normal");
                window.WindowState = WindowState.Maximized;
                CheckCorner(1, theme + " maximized");
                window.WindowState = WindowState.Normal;
                CheckCorner(expectedCorner, theme + " restored");
                void CheckCorner(int expected, string state)
                {
                    int result = DwmGetWindowAttribute(hwnd, 33, out int actual, sizeof(int));
                    report.Add($"{state} native_corner_preference={actual} expected={expected} result={result}");
                    if (result != 0 || actual != expected) failures.Add(state + ": native corner preference does not match the theme and window state.");
                }
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
            window.Left = 100;
            window.Top = 100;
            window.Width = 1100;
            window.Height = 700;
            foreach (Theme theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                await Task.Delay(100);
                window.UpdateLayout();
                var pane = (FrameworkElement)window.FindName("ContentPane");
                ((Button)window.FindName("CaptureBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(200);
                var menu = (ContextMenu)window.FindName("CaptureMenu");
                var face = (FrameworkElement)menu.Template.FindName("MenuRoot", menu);
                CheckPaneInset(face, pane, false, theme + " capture menu");
                menu.IsOpen = false;
                ((Button)window.FindName("LibrarySettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(200);
                var popup = (Popup)window.FindName("LibrarySettingsPopup");
                CheckPaneInset((FrameworkElement)popup.Child, pane, true, theme + " album manager");
                popup.IsOpen = false;
            }
            window.Close();

            void CheckPaneInset(FrameworkElement face, FrameworkElement pane, bool right, string name)
            {
                Point start = face.PointToScreen(new Point());
                Point paneStart = pane.PointToScreen(new Point());
                Point paneEnd = pane.PointToScreen(new Point(pane.ActualWidth, 0));
                Point end = face.PointToScreen(new Point(face.ActualWidth, 0));
                var dpi = VisualTreeHelper.GetDpi(pane);
                double horizontal = (right ? paneEnd.X - end.X : start.X - paneStart.X) / dpi.DpiScaleX;
                double vertical = (start.Y - paneStart.Y) / dpi.DpiScaleY;
                report.Add($"{name} visible inset={horizontal:F2},{vertical:F2}");
                if (Math.Abs(horizontal - 8) > 1.1 || Math.Abs(vertical - 8) > 1.1)
                    failures.Add(name + " must sit eight pixels inside the content pane.");
            }
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
