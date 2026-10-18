using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestCarousel
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-carousel") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        Window? window = null;
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        try
        {
            var items = Enumerable.Range(0, 80).Select(i => new CaptureItem(i, DateTime.Today, 320, 200, "window", "Picture " + i, null, null, null)).ToArray();
            var pixels = new byte[320 * 200 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 170; pixels[i + 1] = 110; pixels[i + 2] = 60; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
            image.Freeze();
            foreach (bool vertical in new[] { false, true })
            {
                var flow = vertical ? null : new CoverFlow { ThumbnailSource = _ => image, Items = items };
                var drum = vertical ? new Rolodex { ThumbnailSource = _ => image, Caption = c => c.Window!, Items = items } : null;
                FrameworkElement control = (FrameworkElement?)flow ?? drum!;
                window = new Window { Content = control, Width = vertical ? 300 : 1000, Height = vertical ? 720 : 500,
                    Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await Task.Delay(150);
                var viewport = (Viewport3D)VisualTreeHelper.GetChild(control, 0);
                var scene = (Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
                var firstCard = scene.Children.OfType<Model3DGroup>().First();
                if (flow != null) flow.SelectedIndex = 40; else drum!.SelectedIndex = 40;
                await Task.Delay(100);
                if (!scene.Children.Contains(firstCard)) throw new InvalidOperationException($"{control.GetType().Name} skipped past its initial card window within 100ms.");
                await Task.Delay(3000);
                var front = scene.Children.OfType<Model3DGroup>().Select(c => c.Transform.Value).Any(m => Math.Abs(m.OffsetX) < .001 && Math.Abs(m.OffsetY) < .001);
                if (!front) throw new InvalidOperationException("Navigation did not settle on a card.");
                report.Add(control.GetType().Name + ": continuous start and settled destination PASS");
                window.Close(); window = null;
            }
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            window?.Close();
            File.WriteAllLines(Path.Combine(folder, "carousel-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
