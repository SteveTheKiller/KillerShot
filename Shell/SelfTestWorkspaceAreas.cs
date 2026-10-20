using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestWorkspaceAreas
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-workspace-areas") return false;
        string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots")); FolderCaptures.Enabled = false;
        App.InitializeKit();
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            for (int n = 0; n < 3; n++)
                CaptureLibrary.AddNow(BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null,
                    Enumerable.Repeat(new byte[] { (byte)(n * 80), 100, 200, 255 }, 160 * 100).SelectMany(p => p).ToArray(), 640),
                    DateTime.Today, "region", "Workspace sample " + n, null);
            var window = new MainWindow { Width = 1100, Height = 760, ShowActivated = false };
            window.Show();
            var sidebar = (DockHost)window.FindName("NavigationDock");
            var carousel = (DockHost)window.FindName("StripDock");
            var library = (LibraryView)window.FindName("Library");
            var root = (FrameworkElement)window.Content;
            foreach (var side in new[] { DockSide.Left, DockSide.Right })
            foreach (var edge in new[] { DockSide.Top, DockSide.Bottom })
            {
                sidebar.DockTo(side); carousel.DockTo(edge); carousel.Restore(); sidebar.Restore();
                foreach (string view in new[] { "flow", "shelves", "timeline", "grid" })
                {
                    library.SetView(view);
                    window.UpdateLayout(); await Task.Delay(80);
                    var toolbar = (FrameworkElement)window.FindName("ToolbarGrid");
                    if (Descendants(toolbar).OfType<ComboBox>().Count(p => p.IsVisible) != 1
                        || Descendants(carousel.Pane!).OfType<ComboBox>().Any())
                        throw new InvalidOperationException("The collection dropdown must be in the main toolbar, outside the carousel.");
                    if (!sidebar.ShowPane || !carousel.ShowPane || !carousel.Pane!.IsVisible || !library.IsVisible)
                        throw new InvalidOperationException("Browsing hides one of the three workspace areas: " + view);
                    var a = ((FrameworkElement)sidebar.Pane!).TransformToAncestor(root).TransformBounds(new Rect(sidebar.Pane.RenderSize));
                    var b = ((FrameworkElement)carousel.Pane).TransformToAncestor(root).TransformBounds(new Rect(carousel.Pane.RenderSize));
                    var c = library.TransformToAncestor(root).TransformBounds(new Rect(library.RenderSize));
                    if (Rect.Intersect(a, b).Width > 1 || Rect.Intersect(b, c).Height > 1 || a.Width < 100 || b.Height < 100 || c.Height < 200)
                        throw new InvalidOperationException("The workspace areas overlap or lose their usable size.");
                    if (((FrameworkElement)window.FindName("FooterStripControls")).Visibility != Visibility.Visible)
                        throw new InvalidOperationException("Carousel placement controls disappear in the library.");
                    var slider = Descendants(carousel.Pane).OfType<Slider>().Single();
                    var splitter = Descendants(carousel).OfType<GridSplitter>().Single();
                    var sliderBounds = slider.TransformToAncestor(root).TransformBounds(new Rect(slider.RenderSize));
                    var splitterBounds = splitter.TransformToAncestor(root).TransformBounds(new Rect(splitter.RenderSize));
                    if (Rect.Intersect(sliderBounds, splitterBounds).Height > 0)
                        throw new InvalidOperationException("The carousel slider overlaps its resize grabber.");
                    report.Add($"{side}/{edge}/{view}: collection sidebar, photo carousel and main view visible PASS");
                }
                carousel.Minimize(); window.UpdateLayout();
                if (sidebar.IsMinimized || !library.IsVisible || !carousel.ShowPane)
                    throw new InvalidOperationException("Collapsing the carousel changed another workspace area.");
                carousel.Restore(); sidebar.Minimize(); window.UpdateLayout();
                if (carousel.IsMinimized || !carousel.Pane!.IsVisible || !library.IsVisible)
                    throw new InvalidOperationException("Collapsing collections hid the carousel or main view.");
                sidebar.Restore(); window.UpdateLayout();
                if (side == DockSide.Left)
                {
                    var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root); ImageOutput.SavePng(bitmap, Path.Combine(folder, "workspace-" + edge + ".png"));
                }
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        CaptureLibrary.Close();
        File.WriteAllLines(Path.Combine(folder, "workspace-areas-report.txt"), report);
        App.Instance.Shutdown(code);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int n = 0; n < VisualTreeHelper.GetChildrenCount(parent); n++)
        {
            var child = VisualTreeHelper.GetChild(parent, n);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
