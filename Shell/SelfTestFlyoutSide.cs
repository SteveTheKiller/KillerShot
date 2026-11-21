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
/// from its left edge with the sidebar on the left, and 8px above the pane's foot. The theme
/// menu's accent pills are drawn whole.
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
                await PaneSettled(window, pane);
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
                    // Every accent pill is drawn whole: nothing in its slot cuts it short.
                    foreach (var pill in Descendants(card).OfType<Border>().Where(b => b.Width == 26))
                    {
                        var clip = LayoutInformation.GetLayoutClip(pill);
                        if (clip != null && clip.Bounds.Width < pill.Width)
                            throw new InvalidOperationException($"An accent pill is clipped to {clip.Bounds.Width:0.#}px of {pill.Width}px.");
                    }
                    // What the screen actually shows, for the rendered check.
                    Point windowTopLeft = window.PointToScreen(new Point());
                    Point windowBottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
                    SaveShot(windowTopLeft, windowBottomRight, Path.Combine(folder, $"{side}-{menuName}.png"));
                    menu.IsOpen = false;
                    await Task.Delay(100);
                    if (Math.Abs(horizontal) > 2 || Math.Abs(vertical) > 2 || !inside)
                        throw new InvalidOperationException($"{menuName} missed the pane's bottom {side.ToString().ToLowerInvariant()} corner.");
                }
            }

            // A short window, at 100% and a larger app size: the big menus stay inside the pane
            // and scroll, instead of running past the bottom of the window.
            window.TestNavigationDock.DockTo(DockSide.Left);
            // 580 tall: the pane is shorter than the theme menu but the window is not, so the whole
            // menu shows, its foot in the pane's corner, rising as high as it needs. The minimum
            // height is the one case with too little window, where it scrolls instead.
            foreach (var (scale, height) in new[] { (1.0, 580.0), (1.0, 0.0), (1.2, 0.0) })
            {
                window.ApplyAppScale(scale);
                window.Height = height > 0 ? height : window.MinHeight;
                window.UpdateLayout();
                await Task.Delay(400);
                await PaneSettled(window, pane);
                foreach (var (buttonName, menuName) in new[] { ("ThemeButton", "ThemeMenu"), ("LangButton", "LangMenu") })
                {
                    var button = (Button)window.FindName(buttonName);
                    var menu = (ContextMenu)window.FindName(menuName);
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Task.Delay(300);
                    if (!menu.IsOpen) throw new InvalidOperationException(menuName + " did not open in the short window.");
                    var card = (FrameworkElement)menu.Template.FindName("MenuRoot", menu);
                    double top = card.PointToScreen(new Point()).Y, bottom = card.PointToScreen(new Point(0, card.ActualHeight)).Y;
                    double paneTop = pane.PointToScreen(new Point()).Y, paneBottom = pane.PointToScreen(new Point(0, pane.ActualHeight)).Y;
                    var scroll = Descendants(card).OfType<ScrollViewer>().First();
                    report.Add($"window {window.ActualHeight:0} at {scale:0.##} {menuName}: card {top:0}-{bottom:0}, pane {paneTop:0}-{paneBottom:0}, scrollable {scroll.ScrollableHeight:0.#}");
                    Point windowTopLeft = window.PointToScreen(new Point());
                    Point windowBottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
                    string shotName = $"short-{window.ActualHeight:0}-{scale:0.##}-{menuName}";
                    SaveShot(windowTopLeft, windowBottomRight, Path.Combine(folder, shotName + ".png"));
                    var face = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    face.Render(menu);
                    ImageOutput.Save(face, Path.Combine(folder, shotName + "-card.png"));
                    var client = (FrameworkElement)window.Content;
                    double clientTop = client.PointToScreen(new Point()).Y, clientBottom = client.PointToScreen(new Point(0, client.ActualHeight)).Y;
                    double inset8 = pane.PointToScreen(new Point(0, 8)).Y - paneTop;
                    bool fits = top >= clientTop - 1 && bottom <= clientBottom + 1
                        && Math.Abs(bottom - (paneBottom - inset8)) <= 2;
                    if (height > 0 && scroll.ScrollableHeight > 0)
                        throw new InvalidOperationException($"{menuName} scrolls in a {height}-tall window that has room for all of it ({top:0}-{bottom:0}, window {clientTop:0}-{clientBottom:0}).");
                    bool reachable = true;
                    if (scroll.ScrollableHeight > 0)
                    {
                        scroll.ScrollToEnd();
                        await Task.Delay(100);
                        var last = Descendants(card).OfType<FrameworkElement>().Where(e => e is CheckBox or MenuItem && e.IsVisible).Last();
                        double lastBottom = last.TranslatePoint(new Point(0, last.ActualHeight), scroll).Y;
                        reachable = lastBottom <= scroll.ActualHeight + 1;
                    }
                    menu.IsOpen = false;
                    await Task.Delay(100);
                    if (!fits) throw new InvalidOperationException($"{menuName} must sit in the pane's corner and inside the window at {scale:0.##} ({top:0}-{bottom:0}, pane foot {paneBottom:0}, window {clientTop:0}-{clientBottom:0}).");
                    if (!reachable) throw new InvalidOperationException($"{menuName}'s last row cannot be scrolled into view at {scale:0.##}.");
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

    // A moved sidebar slides open, and a loaded test desktop runs the slide late. A menu opened
    // mid-slide is placed against a pane that is still changing size, so wait until the pane
    // holds still on screen for three checks in a row.
    private static async Task PaneSettled(Window window, FrameworkElement pane)
    {
        Rect last = Rect.Empty;
        int still = 0;
        for (int tries = 0; tries < 120 && still < 3; tries++)
        {
            window.UpdateLayout();
            var now = new Rect(pane.PointToScreen(new Point()), pane.PointToScreen(new Point(pane.ActualWidth, pane.ActualHeight)));
            still = now == last ? still + 1 : 0;
            last = now;
            await Task.Delay(50);
        }
    }

    // What the screen shows, for looking at by eye. A hidden test desktop refuses the copy, and
    // the geometry checks above carry the verdict either way.
    private static void SaveShot(Point topLeft, Point bottomRight, string path)
    {
        try
        {
            var shot = Capture.FrozenDesktop.Capture().Crop(new Native.Win32.RECT(
                (int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y));
            if (shot != null) ImageOutput.SavePng(shot, path);
        }
        catch (InvalidOperationException) { }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
