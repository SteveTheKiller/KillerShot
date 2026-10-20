using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFlyoutScale
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-flyout-scale") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        var pane = new Border { Background = Brushes.Gray, Margin = new Thickness(65, 40, 0, 0) };
        var button = new Button { Width = 40, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid(); grid.Children.Add(pane); grid.Children.Add(button);
        var host = new Border { Child = grid };
        var window = new Window { Width = 1000, Height = 700, Content = host, ShowActivated = false };
        try
        {
            window.Show();
            FlyoutPlacement.UsePane(pane);
            foreach (double scale in new[] { 1.0, 1.5 })
            {
                host.LayoutTransform = new ScaleTransform(scale, scale);
                window.UpdateLayout();
                foreach (bool right in new[] { false, true })
                foreach (bool menuMode in new[] { false, true })
                {
                    var face = new Border { Width = 160, Height = 80, Background = Brushes.White, Margin = new Thickness(5, 7, 9, 11) };
                    Popup? popup = null;
                    ContextMenu? menu = null;
                    FrameworkElement visibleFace;
                    if (menuMode)
                    {
                        menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header = "Capture" });
                        FlyoutPlacement.AttachInsidePane(menu, button, right);
                        menu.IsOpen = true;
                        Anim.FadeIn(menu);
                        await Task.Delay(150);
                        visibleFace = (FrameworkElement)menu.Template.FindName("MenuRoot", menu);
                    }
                    else
                    {
                        popup = new Popup { Child = face, AllowsTransparency = true };
                        FlyoutPlacement.AttachInsidePane(popup, button, right);
                        popup.IsOpen = true;
                        await Task.Delay(150);
                        visibleFace = face;
                    }
                    Point actual = visibleFace.PointToScreen(new Point(right ? visibleFace.ActualWidth : 0, 0));
                    Point expected = pane.PointToScreen(new Point(right ? pane.ActualWidth - 8 : 8, 8));
                    double error = (actual - expected).Length;
                    report.Add($"scale={scale} menu={menuMode} right={right} error_px={error:0.##}");
                    if (popup != null) popup.IsOpen = false;
                    if (menu != null) menu.IsOpen = false;
                    if (error > 2) throw new InvalidOperationException("The flyout face missed the scaled pane inset.");
                }
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "flyout-scale-report.txt"), report);
        App.Instance.Shutdown(code);
    }
}
