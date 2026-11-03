using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestNovemberVisuals
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-november-visuals") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            LocaleManager.Apply(Locale.EnUS);
            string files = Path.Combine(folder, "files");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(Path.Combine(files, "Folder"));
            Settings.Set("FileDlgPinned", files);
            var pixels = new byte[96 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 201; pixels[i + 1] = 37; pixels[i + 2] = 227; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 384);
            image.Freeze();
            ImageOutput.SavePng(image, Path.Combine(files, "000 Picture.png"));
            for (int i = 0; i < 100; i++) File.WriteAllText(Path.Combine(files, $"Document {i:000}.txt"), "sample");
            foreach (Theme theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 })
            {
                ThemeManager.ApplyAccent(theme, Accent.Teal);
                ThemeManager.Apply(theme);
                var dialog = new Controls.FileDialog { InitialDirectory = files, Width = 1000, Height = 700,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
                var done = new TaskCompletionSource();
                _ = dialog.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await dialog.DirectoryListingTask.WaitAsync(TimeSpan.FromSeconds(10));
                        var places = (ListBox)dialog.FindName("PlacesList");
                        var ready = System.Diagnostics.Stopwatch.StartNew();
                        while (places.Items.Count == 0 && ready.Elapsed < TimeSpan.FromSeconds(11)) await Task.Delay(25);
                        if (places.Items.Count == 0) throw new InvalidOperationException("The owned pinned place did not finish loading.");
                        places.SelectedItem = places.Items.OfType<PickerPlace>().First(place => place.Path == files);
                        await dialog.DirectoryListingTask.WaitAsync(TimeSpan.FromSeconds(10));
                        await Task.Delay(250);
                        var list = (ListBox)dialog.FindName("FileList");
                        var picture = list.Items.OfType<PickerEntry>().Single(entry => entry.Name == "000 Picture.png");
                        report.Add(theme + " pinned place loaded and selected PASS");
                        foreach (string mode in new[] { "List", "Icons", "Details" })
                        {
                            ((Button)dialog.FindName("View" + mode + "Btn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            dialog.UpdateLayout();
                            list.SelectedItem = picture;
                            list.ScrollIntoView(picture);
                            _ = picture.IconLarge;
                            await Task.Delay(200);
                            dialog.UpdateLayout();
                            var content = (FrameworkElement)dialog.Content;
                            var rendered = Render(content);
                            ImageOutput.SavePng(rendered, Path.Combine(folder, theme + "-" + mode + ".png"));
                            if (mode == "Icons")
                            {
                                int present = TargetPixels(rendered);
                                list.Visibility = Visibility.Hidden;
                                dialog.UpdateLayout();
                                int absent = TargetPixels(Render(content));
                                list.Visibility = Visibility.Visible;
                                if (present < 100 || absent >= 20) throw new InvalidOperationException("Thumbnail presence/absence controls failed in " + theme + ": " + present + "/" + absent);
                                report.Add(theme + " thumbnail target present=" + present + ", absent=" + absent + " PASS");
                            }
                            report.Add(theme + " " + mode + " rendered selected picture PASS");
                        }
                        done.TrySetResult();
                    }
                    catch (Exception error) { done.TrySetException(error); }
                    finally { dialog.Close(); }
                }));
                dialog.ShowDialog(null);
                await done.Task;
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "november-visuals-report.txt"), report);
        Application.Current.Shutdown(code);
    }

    private static BitmapSource Render(FrameworkElement content)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();
        return bitmap;
    }

    private static int TargetPixels(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        byte[] pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - 201) < 4 && Math.Abs(pixels[i + 1] - 37) < 4 && Math.Abs(pixels[i + 2] - 227) < 4) count++;
        return count;
    }
}
