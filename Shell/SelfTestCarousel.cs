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
            foreach (bool vertical in new[] { false, true })
            {
                var flow = vertical ? null : new CoverFlow { ThumbnailSource = _ => image, Items = items };
                var drum = vertical ? new Rolodex { ThumbnailSource = _ => image, Caption = c => c.Window!, Items = items } : null;
                FrameworkElement control = (FrameworkElement?)flow ?? drum!;
                if (control.FocusVisualStyle == null)
                    throw new InvalidOperationException("Keyboard-focusable carousel has no visible focus style.");
                window = new Window { Content = control, Width = vertical ? 300 : 1000, Height = vertical ? 720 : 500,
                    Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await Task.Delay(150);
                var viewport = (Viewport3D)VisualTreeHelper.GetChild(control, 0);
                var scene = (Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
                var firstCard = scene.Children.OfType<Model3DGroup>().Single(c => Math.Abs(c.Transform.Value.OffsetX) < .001 && Math.Abs(c.Transform.Value.OffsetY) < .001);
                if (flow != null) flow.SelectedIndex = 40; else drum!.SelectedIndex = 40;
                await Task.Delay(100);
                if (!scene.Children.Contains(firstCard)) throw new InvalidOperationException($"{control.GetType().Name} skipped past its initial card window within 100ms.");
                await Task.Delay(3000);
                var front = scene.Children.OfType<Model3DGroup>().Select(c => c.Transform.Value).Any(m => Math.Abs(m.OffsetX) < .001 && Math.Abs(m.OffsetY) < .001);
                if (!front) throw new InvalidOperationException("Navigation did not settle on a card.");
                report.Add(control.GetType().Name + ": continuous start and settled destination PASS");
                if (Mouse.LeftButton != MouseButtonState.Released) throw new InvalidOperationException("Release the mouse before the carousel input check.");
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                // Windows can omit button-up when focus or a popup changes during the gesture.
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
                if (control.IsMouseCaptured) throw new InvalidOperationException(control.GetType().Name + " stays attached to the pointer after its left button is released.");
                report.Add(control.GetType().Name + ": a missed button-up cannot leave the carousel stuck to the mouse PASS");
                if (drum != null)
                {
                    foreach (double zoom in new[] { drum.Zoom, Rolodex.MinZoom, Rolodex.MaxZoom })
                    {
                        drum.Zoom = zoom;
                        window.UpdateLayout();
                        await Task.Delay(150);
                        CheckStraightCards(drum, Path.Combine(folder, $"rolodex-zoom-{zoom:F2}.png"), report);
                    }
                    drum.Zoom = 2;
                    if (drum.Zoom < 1.11) throw new InvalidOperationException("The Rolodex cannot zoom the front picture to the sidebar width.");
                    await Task.Delay(150);
                    var preview = new RenderTargetBitmap((int)drum.ActualWidth, (int)drum.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    preview.Render(drum);
                    ImageOutput.SavePng(preview, Path.Combine(folder, "rolodex-max-zoom.png"));
                    report.Add("Rolodex: front picture reaches sidebar width PASS");
                    drum.SelectedIndex = items.Length - 1;
                    drum.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(drum), 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
                    if (drum.SelectedIndex != 0) throw new InvalidOperationException("The Rolodex must wrap from the last card to the first.");
                    drum.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(drum), 0, Key.Up) { RoutedEvent = Keyboard.KeyDownEvent });
                    if (drum.SelectedIndex != items.Length - 1) throw new InvalidOperationException("The Rolodex must wrap backward to the last card.");
                    report.Add("Rolodex: forward and backward continuous wheel PASS");
                }
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

    private static void CheckStraightCards(Rolodex drum, string path, List<string> report)
    {
        var image = new RenderTargetBitmap((int)drum.ActualWidth, (int)drum.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(drum);
        ImageOutput.SavePng(image, path);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var runs = new List<List<int>>();
        List<int>? run = null;
        for (int y = 0; y < image.PixelHeight; y++)
        {
            int first = image.PixelWidth, last = -1;
            for (int x = 0; x < image.PixelWidth; x++)
            {
                int at = (y * image.PixelWidth + x) * 4;
                if (pixels[at] is >= 168 and <= 172 && pixels[at + 1] is >= 108 and <= 112
                    && pixels[at + 2] is >= 58 and <= 62 && pixels[at + 3] > 250)
                {
                    first = Math.Min(first, x);
                    last = x;
                }
            }
            if (last - first < 30) { run = null; continue; }
            if (run == null) { run = []; runs.Add(run); }
            run.Add(last - first + 1);
        }
        var cards = runs.Where(rows => rows.Count >= 6).Select(rows => rows.Skip(1).SkipLast(1).ToArray()).ToArray();
        if (cards.Length < 3) throw new InvalidOperationException("The Rolodex check needs visible cards above and below the front card.");
        int taper = cards.Max(rows => rows.Max() - rows.Min());
        report.Add($"Rolodex: zoom {drum.Zoom:F2}, visible image bands {cards.Length}, largest width variation {taper}px");
        if (taper > 2) throw new InvalidOperationException("The top and bottom Rolodex cards taper instead of keeping straight sides.");
    }
}
