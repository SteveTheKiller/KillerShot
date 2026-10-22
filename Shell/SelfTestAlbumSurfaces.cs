using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestAlbumSurfaces
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-album-surfaces", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        MainWindow? window = null;
        int exit = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            App.CloseToTray = true;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Scratch album did not open.");
            var pixels = new byte[240 * 140 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 70; pixels[i + 1] = 130; pixels[i + 2] = 210; pixels[i + 3] = 255;
            }
            var picture = BitmapSource.Create(240, 140, 96, 96, PixelFormats.Bgra32, null, pixels, 960);
            picture.Freeze();
            await CheckCarouselBackingAsync(folder, report, picture);
            CaptureLibrary.AddNow(picture, DateTime.Today, "region", "Surface sample", null);
            window = new MainWindow { Width = 1100, Height = 720, Left = -20000, Top = -20000,
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.Black, Theme.Malaise })
            {
                ThemeManager.Apply(theme);
                foreach (string view in new[] { "grid", "timeline", "flow", "shelves" })
                {
                    window.TestShowLibraryView(view);
                    await Task.Delay(180);
                    window.UpdateLayout();
                    var pane = (Border)window.FindName("ContentPane");
                    var expected = (SolidColorBrush)window.FindResource("ContentPaneBrush");
                    if (pane.Background is not SolidColorBrush background || background.Color != expected.Color || background.Color.A != 255)
                        throw new InvalidOperationException($"{theme}/{view}: album lost its opaque content pane.");
                    if (view is "grid" or "timeline")
                    {
                        var tile = Visuals(window.Library).OfType<Button>().First(b => b.Tag is CaptureItem);
                        if (tile.Background is not SolidColorBrush card || card.Color.A != 255 || card.Color == background.Color)
                            throw new InvalidOperationException($"{theme}/{view}: info card does not separate from its content pane.");
                        var thumbnail = Visuals(tile).OfType<Image>().Single();
                        for (int wait = 0; wait < 100 && thumbnail.Source == null; wait++) await Task.Delay(30);
                        if (thumbnail.Source == null) throw new InvalidOperationException("The card thumbnail did not load.");
                        window.UpdateLayout();
                    }
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render((Visual)window.Content);
                    ImageOutput.SavePng(bitmap, Path.Combine(folder, $"{theme}-{view}.png"));
                    report.Add($"{theme}/{view}: opaque pane and separate cards PASS");
                }
            }
            window.TestShowLibraryView("grid");
            CardColors.SetCard(Colors.MediumPurple);
            await Task.Delay(180);
            window.UpdateLayout();
            var custom = Visuals(window.Library).OfType<Button>().First(b => b.Tag is CaptureItem);
            if (custom.Background is not SolidColorBrush chosen || chosen.Color != Colors.MediumPurple)
                throw new InvalidOperationException("The chosen card color was replaced by the default surface.");
            report.Add("Chosen card color remains intact PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "album-surfaces-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task CheckCarouselBackingAsync(string folder, List<string> report, BitmapSource picture)
    {
        var flow = new KillerShot.Controls.CoverFlow { ThumbnailSource = _ => picture };
        var panel = new Grid { Background = Brushes.Gray };
        panel.Children.Add(flow);
        var host = new Window { Content = panel, Width = 420, Height = 300, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show();
            await Task.Delay(100);
            host.UpdateLayout();
            byte empty = Render("empty");
            flow.Items = [new CaptureItem(501, DateTime.Today, 240, 140, "region", "Backing sample", null, null, null)];
            await Task.Delay(80);
            byte populated = Render("populated");
            flow.Items = [];
            await Task.Delay(80);
            byte cleared = Render("cleared");
            report.Add($"Carousel backing edge pixel: empty={empty}, populated={populated}, cleared={cleared}");
            if (populated != empty || cleared != empty)
                throw new InvalidOperationException("The carousel shadow recolored the surrounding theme surface.");
        }
        finally { host.Close(); }

        byte Render(string name)
        {
            var bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            ImageOutput.SavePng(bitmap, Path.Combine(folder, $"backing-{name}.png"));
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(5, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
            return pixel[0];
        }
    }
}
