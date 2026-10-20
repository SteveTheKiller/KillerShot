using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestSidebarSelection
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-sidebar-selection") return false;
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
        var browser = new CollectionBrowser();
        var window = new Window { Width = 280, Height = 320, Content = browser, ShowActivated = false };
        window.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        try
        {
            window.Show();
            foreach (var theme in Enum.GetValues<Theme>())
            {
                ThemeManager.Apply(theme);
                foreach (string style in new[] { "stack", "grid", "mini", "pile" })
                {
                    browser.ItemsSource = new[]
                    {
                        new CollectionChoice("all", "All captures", [], 12, style),
                        new CollectionChoice("none", "Not in a collection", [], 3, style),
                    };
                    browser.SelectedIndex = 0;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(browser.ActualWidth),
                        (int)Math.Ceiling(browser.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(browser);
                    ImageOutput.SavePng(bitmap, Path.Combine(folder, $"selection-{theme}-{style}.png"));
                    // KillerNotes uses a fill across the row, with no separate accent stroke.
                    var row = (ListBoxItem)browser.ItemContainerGenerator.ContainerFromIndex(0);
                    var rowBitmap = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth),
                        (int)Math.Ceiling(row.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        drawing.DrawRectangle(new VisualBrush(row), null, new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                    rowBitmap.Render(visual);
                    var pixels = new byte[12];
                    rowBitmap.CopyPixels(new Int32Rect(0, (int)(row.ActualHeight / 2), 3, 1), pixels, 12, 0);
                    if (Enumerable.Range(0, 4).Any(channel => Math.Abs(pixels[channel] - pixels[channel + 8]) > 3))
                        throw new InvalidOperationException($"Selected {style} row has an accent outline in {theme}: {string.Join(',', pixels)}.");
                    report.Add($"{theme} {style}: filled selection without accent outline PASS");
                }
            }
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "sidebar-selection-report.txt"), report);
        Application.Current.Shutdown(code);
    }
}
