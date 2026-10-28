using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerFamily.Layout;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// The rail's big menus (theme, language) open in the content pane's bottom corner on the rail's
/// side: the visible card 8px in from the pane's right edge with the sidebar on the right, and
/// from its left edge with the sidebar on the left, and 8px above the pane's foot.
/// </summary>
internal static class SelfTestFlyoutSide
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-flyout-side") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new flyout-side test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            // On screen: a popup is kept on a monitor, so an off-screen window would measure that
            // clamp instead of the placement.
            window = new MainWindow { ShowActivated = false, Left = 40, Top = 40, Width = 1100, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(300);
            var pane = (FrameworkElement)window.FindName("ContentPane");
            foreach (var side in new[] { DockSide.Right, DockSide.Left })
            {
                window.TestNavigationDock.DockTo(side);
                window.UpdateLayout();
                await Task.Delay(400);
                foreach (var (buttonName, menuName) in new[] { ("ThemeButton", "ThemeMenu"), ("LangButton", "LangMenu") })
                {
                    var button = (Button)window.FindName(buttonName);
                    var menu = (ContextMenu)window.FindName(menuName);
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Task.Delay(300);
                    if (!menu.IsOpen) throw new InvalidOperationException(menuName + " did not open.");
                    var card = (FrameworkElement)menu.Template.FindName("MenuRoot", menu);
                    Point cardTopLeft = card.PointToScreen(new Point());
                    Point cardBottomRight = card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight));
                    Point paneTopLeft = pane.PointToScreen(new Point());
                    Point paneBottomRight = pane.PointToScreen(new Point(pane.ActualWidth, pane.ActualHeight));
                    double inset = pane.PointToScreen(new Point(8, 0)).X - paneTopLeft.X;
                    double horizontal = side == DockSide.Right
                        ? paneBottomRight.X - inset - cardBottomRight.X
                        : cardTopLeft.X - (paneTopLeft.X + inset);
                    double vertical = paneBottomRight.Y - inset - cardBottomRight.Y;
                    bool inside = cardTopLeft.X >= paneTopLeft.X && cardBottomRight.X <= paneBottomRight.X;
                    report.Add($"{side} {menuName}: horizontal_error_px={horizontal:0.##} vertical_error_px={vertical:0.##} inside={inside}");
                    // What the screen actually shows, for the rendered check.
                    Point windowTopLeft = window.PointToScreen(new Point());
                    Point windowBottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
                    var shot = Capture.FrozenDesktop.Capture().Crop(new Native.Win32.RECT(
                        (int)windowTopLeft.X, (int)windowTopLeft.Y, (int)windowBottomRight.X, (int)windowBottomRight.Y));
                    if (shot != null) ImageOutput.SavePng(shot, Path.Combine(folder, $"{side}-{menuName}.png"));
                    menu.IsOpen = false;
                    await Task.Delay(100);
                    if (Math.Abs(horizontal) > 2 || Math.Abs(vertical) > 2 || !inside)
                        throw new InvalidOperationException($"{menuName} missed the pane's bottom {side.ToString().ToLowerInvariant()} corner.");
                }
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "flyout-side-report.txt"), report);
            window?.Close();
            App.Instance.Shutdown(code);
        }
    }
}
