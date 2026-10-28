using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            {
                var flow = new CoverFlow { ThumbnailSource = _ => image, Items = items };
                FrameworkElement control = flow;
                if (control.FocusVisualStyle == null)
                    throw new InvalidOperationException("Keyboard-focusable carousel has no visible focus style.");
                window = new Window { Content = control, Width = 1000, Height = 500,
                    Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await Task.Delay(150);
                var viewport = (Viewport3D)VisualTreeHelper.GetChild(control, 0);
                var scene = (Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
                var firstCard = scene.Children.OfType<Model3DGroup>().Single(c => Math.Abs(c.Transform.Value.OffsetX) < .001 && Math.Abs(c.Transform.Value.OffsetY) < .001);
                // A long jump (the slider dragged far) keeps up with the pointer: it leaves the
                // starting cards at once and settles well inside a second, instead of gliding past
                // every picture on the way.
                flow.SelectedIndex = 40;
                await Task.Delay(100);
                if (scene.Children.Contains(firstCard)) throw new InvalidOperationException($"{control.GetType().Name} was still crawling past its starting cards 100ms after a 40-picture jump.");
                await Task.Delay(900);
                var front = scene.Children.OfType<Model3DGroup>().Select(c => c.Transform.Value).Any(m => Math.Abs(m.OffsetX) < .001 && Math.Abs(m.OffsetY) < .001);
                if (!front) throw new InvalidOperationException("A 40-picture jump did not settle on a card within one second.");
                report.Add(control.GetType().Name + ": long jump keeps up with the pointer and settles within one second PASS");
                if (Mouse.LeftButton != MouseButtonState.Released) throw new InvalidOperationException("Release the mouse before the carousel input check.");
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                // Windows can omit button-up when focus or a popup changes during the gesture.
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
                if (control.IsMouseCaptured) throw new InvalidOperationException(control.GetType().Name + " stays attached to the pointer after its left button is released.");
                report.Add(control.GetType().Name + ": a missed button-up cannot leave the carousel stuck to the mouse PASS");
                window.Close(); window = null;
            }
            {
                // A maximized shelf: as wide as a 2560px monitor and 240 high, with plenty of
                // pictures. Its cards must reach both ends, not bunch in a column in the middle.
                var many = Enumerable.Range(0, 60).Select(i => new CaptureItem(i, DateTime.Today, 320, 200, "window", "Picture " + i, null, null, null)).ToArray();
                var shelf = new CoverFlow { ThumbnailSource = _ => image, Items = many, ViewHeight = 1.52, PerspectiveZoomEnabled = true, ViewCenterY = 0.255 };
                window = new Window { Content = shelf, Width = 2560, Height = 280, SizeToContent = SizeToContent.Manual,
                    Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                shelf.SelectedIndex = 30;
                await Task.Delay(1200);
                var viewport = (Viewport3D)VisualTreeHelper.GetChild(shelf, 0);
                var scene = (Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
                int cards = scene.Children.OfType<Model3DGroup>().Count();
                var picture = new RenderTargetBitmap((int)shelf.ActualWidth, (int)shelf.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                picture.Render(shelf);
                ImageOutput.SavePng(picture, Path.Combine(folder, "wide-shelf.png"));
                // Covered columns: any opaque pixel in the top two thirds (the pictures, not reflections).
                int stride = picture.PixelWidth * 4;
                var shot = new byte[stride * picture.PixelHeight];
                picture.CopyPixels(shot, stride, 0);
                int firstColumn = -1, lastColumn = -1;
                for (int x = 0; x < picture.PixelWidth; x++)
                    for (int y = 0; y < picture.PixelHeight * 2 / 3; y++)
                        if (shot[y * stride + x * 4 + 3] > 0) { if (firstColumn < 0) firstColumn = x; lastColumn = x; break; }
                report.Add($"wide shelf: {cards} cards, pictures from x={firstColumn} to x={lastColumn} of {picture.PixelWidth}");
                if (cards <= 19) throw new InvalidOperationException("A wide shelf still builds only nine pictures on each side.");
                if (firstColumn > picture.PixelWidth * .05 || lastColumn < picture.PixelWidth * .95)
                    throw new InvalidOperationException("A wide shelf's pictures must reach both ends.");
                report.Add("CoverFlow: a maximized shelf with enough pictures reaches both ends PASS");
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
