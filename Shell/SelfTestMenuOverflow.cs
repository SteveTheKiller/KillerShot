using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestMenuOverflow
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-menu-overflow") return false;
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
        var button = new Button { Content = "Collections", Width = 120, Height = 30 };
        var window = new Window { Width = 500, Height = 400, Content = button, ShowActivated = false };
        try
        {
            window.Show();
            foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                var menu = new ContextMenu { PlacementTarget = button, MaxHeight = 240 };
                for (int n = 0; n < 125; n++) menu.Items.Add(new MenuItem { Header = "Collection " + n });
                var move = new MenuItem { Header = "Move to" };
                for (int n = 0; n < 125; n++) move.Items.Add(new MenuItem { Header = "Destination " + n });
                menu.Items.Add(move);
                menu.IsOpen = true;
                Anim.FadeIn(menu);
                await Task.Delay(150);
                await CheckLastAsync(menu, move);
                move.ApplyTemplate();
                var popup = (Popup)move.Template.FindName("PART_Popup", move);
                var face = (FrameworkElement)popup.Child;
                face.MaxHeight = 240;
                move.IsSubmenuOpen = true;
                await Task.Delay(150);
                await CheckLastAsync(face, (MenuItem)move.Items[124]);
                Render(face, Path.Combine(folder, "submenu-" + theme + ".png"));
                move.IsSubmenuOpen = false;
                menu.IsOpen = false;
                report.Add(theme + ": 125-entry context menu and submenu scroll to their final command PASS");
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "menu-overflow-report.txt"), report);
        App.Instance.Shutdown(code);
    }

    private static async Task CheckLastAsync(FrameworkElement root, MenuItem last)
    {
        var scroll = Descendants(root).OfType<ScrollViewer>().FirstOrDefault(view => view.ScrollableHeight > 0);
        if (scroll == null) throw new InvalidOperationException("An overflowing menu has no scrollable viewport.");
        scroll.ScrollToEnd();
        last.Focus();
        last.BringIntoView();
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Point center = last.TranslatePoint(new Point(0, last.ActualHeight / 2), scroll);
        if (center.Y < 0 || center.Y > scroll.ActualHeight)
            throw new InvalidOperationException("The final menu command cannot be brought into the viewport.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int n = 0; n < VisualTreeHelper.GetChildrenCount(root); n++)
        {
            var child = VisualTreeHelper.GetChild(root, n);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Render(FrameworkElement face, string path)
    {
        var image = new RenderTargetBitmap((int)face.ActualWidth, (int)face.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(face);
        ImageOutput.Save(image, path);
    }
}
