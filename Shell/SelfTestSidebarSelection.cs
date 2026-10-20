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
        if (args.Length != 2 || args[0] is not ("--selftest-sidebar-selection" or "--selftest-live-sidebar-language")) return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        if (args[0] == "--selftest-live-sidebar-language")
        {
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
        }
        App.InitializeKit();
        _ = args[0] == "--selftest-live-sidebar-language" ? RunLanguageAsync(folder) : RunAsync(folder);
        return true;
    }

    private static async Task RunLanguageAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Language sample", null);
            CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Second language sample", null);
            var window = new MainWindow { Width = 900, Height = 650, ShowActivated = false };
            window.Show();
            await Task.Delay(150);
            var strip = window.TestNavigationStrip!;
            strip.PreviewMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "mini"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var collectionBrowser = Descendants<CollectionBrowser>(strip).Single();
            for (int n = 0; n < 100 && !Descendants<Image>(collectionBrowser).Any(image => image.Source != null); n++) await Task.Delay(20);
            var photos = Descendants<Image>(collectionBrowser).Where(image => image.Source != null)
                .Select(image => (Image: image, Source: image.Source)).ToArray();
            if (photos.Length == 0) throw new InvalidOperationException("The language fixture needs loaded mini-carousel previews.");
            string source = strip.Source;
            foreach (var locale in new[] { Locale.De, Locale.Ja, Locale.EnUS })
            {
                ((Button)window.FindName("LangButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var languageMenu = (ContextMenu)window.FindName("LangMenu");
                var language = Descendants<RadioButton>(languageMenu).Single(button => Equals(button.Tag, locale.ToString()));
                language.IsChecked = true;
                language.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
                languageMenu.IsOpen = false;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var browser = Descendants<CollectionBrowser>(strip).Single();
                var all = browser.Items.OfType<CollectionChoice>().Single(choice => choice.Source == "all");
                string expected = LocaleManager.Loc("Str_Strip_All");
                if (all.Name != expected || strip.SourceCaption != expected || strip.Source != source)
                    throw new InvalidOperationException("Live language change left collection captions stale or changed the source: " + locale);
                if (!Descendants<TextBlock>(browser).Any(text => text.Text == expected))
                    throw new InvalidOperationException("The rendered collection caption did not update its language.");
                if (photos.Any(photo => !Descendants<Image>(browser).Contains(photo.Image) || !ReferenceEquals(photo.Source, photo.Image.Source)))
                    throw new InvalidOperationException("A language change rebuilt loaded photo previews.");
                if (((TextBlock)window.FindName("CollectionNameText")).Text != expected)
                    throw new InvalidOperationException("Live language change left the footer collection caption stale.");
                var content = (FrameworkElement)window.Content;
                var rendered = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(content);
                ImageOutput.Save(rendered, Path.Combine(folder, "language-" + locale + ".png"));
                report.Add(locale + ": sidebar and footer update without reload or selection loss PASS");
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        CaptureLibrary.Close();
        File.WriteAllLines(Path.Combine(folder, "live-sidebar-language-report.txt"), report);
        App.Instance.Shutdown(code);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int n = 0; n < VisualTreeHelper.GetChildrenCount(root); n++)
        {
            var child = VisualTreeHelper.GetChild(root, n);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
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
