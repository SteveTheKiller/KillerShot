using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-scroll-capture &lt;folder&gt;. A small topmost test
/// window with a sticky header and many uniquely colored rows is scrolled in steps while a scrolling
/// capture grabs it from the screen. The stitched page must hold every row once, in order, with the
/// header once. Only that test window is captured.</summary>
internal static class SelfTestScrollCapture
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-scroll-capture") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private const int Rows = 120, RowHeight = 12, HeaderHeight = 30, ViewHeight = 240, Width = 160;

    private static Color RowColor(int i) => Color.FromRgb((byte)(40 + i * 37 % 200), (byte)(30 + i * 91 % 210), (byte)(20 + i * 53 % 220));

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        Window? fixture = null;
        try
        {
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            App.InitializeKit();
            var rows = new StackPanel();
            for (int i = 0; i < Rows; i++) rows.Children.Add(new Rectangle { Height = RowHeight, Fill = new SolidColorBrush(RowColor(i)) });
            var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Height = ViewHeight };
            var page = new DockPanel();
            var header = new Border { Height = HeaderHeight, Background = Brushes.Black };
            DockPanel.SetDock(header, Dock.Top);
            page.Children.Add(header);
            page.Children.Add(scroller);
            fixture = new Window
            {
                Content = page, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
                Left = 40, Top = 40, SizeToContent = SizeToContent.WidthAndHeight, Width = Width, ShowActivated = false, Title = "KillerShot scroll fixture",
            };
            page.Width = Width;
            fixture.Show();
            await Task.Delay(300);
            var h = new WindowInteropHelper(fixture).Handle;
            Win32.GetWindowRect(h, out var rect);
            double scale = VisualTreeHelper.GetDpi(fixture).DpiScaleY;
            report.Add($"fixture {rect} scale {scale}");

            var session = new ScrollCaptureSession(rect);
            var result = session.RunAsync();
            await Task.Delay(400);
            double offset = 0, max = Rows * RowHeight - ViewHeight;
            foreach (double step in new[] { 30.0, 55, 12, 90, 100, 40, 160, 70, 130, 200, 120, 200 })
            {
                offset = Math.Min(max, offset + step);
                scroller.ScrollToVerticalOffset(offset);
                await Task.Delay(350);
            }
            scroller.ScrollToVerticalOffset(max);
            await Task.Delay(500);
            session.TestFinish(keep: true);
            var stitched = await result ?? throw new InvalidOperationException("The scrolling capture returned nothing.");
            ImageOutput.SavePng(stitched, Path.Combine(folder, "scroll-capture.png"));

            // Read the row colors down the middle column and collapse runs: every row once, in order.
            var px = new byte[stitched.PixelWidth * stitched.PixelHeight * 4];
            new FormatConvertedBitmap(stitched, PixelFormats.Bgra32, null, 0).CopyPixels(px, stitched.PixelWidth * 4, 0);
            int x = stitched.PixelWidth / 2, headerPx = (int)Math.Round(HeaderHeight * scale);
            var seen = new List<int>();
            for (int y = headerPx; y < stitched.PixelHeight; y++)
            {
                int i = (y * stitched.PixelWidth + x) * 4;
                var c = Color.FromRgb(px[i + 2], px[i + 1], px[i]);
                int row = Enumerable.Range(0, Rows).FirstOrDefault(r => RowColor(r) == c, -1);
                if (row >= 0 && (seen.Count == 0 || seen[^1] != row)) seen.Add(row);
            }
            int blackHeaders = 0;
            for (int y = 0; y < stitched.PixelHeight; y++)
            {
                int i = (y * stitched.PixelWidth + x) * 4;
                if (px[i] == 0 && px[i + 1] == 0 && px[i + 2] == 0 && (y == 0 || px[i - stitched.PixelWidth * 4] != 0)) blackHeaders++;
            }
            report.Add($"stitched {stitched.PixelWidth} x {stitched.PixelHeight}; rows seen {seen.Count}; header bands {blackHeaders}");
            if (!seen.SequenceEqual(Enumerable.Range(0, Rows))) throw new InvalidOperationException("Rows are missing, repeated or out of order: " + string.Join(",", seen.Take(40)));
            if (blackHeaders != 1) throw new InvalidOperationException("The sticky header was not kept exactly once.");
            report.Add("live scrolling capture keeps every row once and the header once PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            fixture?.Close();
            File.WriteAllLines(Path.Combine(folder, "scroll-capture-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
