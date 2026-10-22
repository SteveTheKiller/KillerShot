using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-pile-edges &lt;folder&gt;. In the collection
/// sidebar's photo-pile style, the scattered photos of every card in view may reach past the
/// card's edge but are never sliced off by a clip.</summary>
internal static class SelfTestPileEdges
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-pile-edges") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new pile-edges test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            Settings.Set("AlbumStripWidth", "180");
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Synthetic album did not open.");
            var ids = new List<long>();
            for (int n = 0; n < 14; n++)
            {
                int w = n % 3 == 0 ? 120 : 200, h = n % 3 == 0 ? 160 : 120;
                byte shade = (byte)(60 + n * 12);
                var px = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = shade; px[i + 1] = (byte)(255 - shade); px[i + 2] = 200; px[i + 3] = 255; }
                var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                image.Freeze();
                ids.Add(CaptureLibrary.AddNow(image, DateTime.Today.AddMinutes(-n), "region", "Pile " + n, null));
            }
            CaptureLibrary.CreateCollection("Half the pile", ids.Where((_, i) => i % 2 == 0));
            window = new MainWindow { Width = 900, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
            window.Show();
            App.Instance.SetDocument(null);
            await Task.Delay(400);
            window.TestNavigationStrip!.PreviewMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "pile"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(800);
            window.UpdateLayout();

            var browser = Visuals(window).OfType<CollectionBrowser>().First(b => b.IsVisible);
            var viewport = Visuals(browser).OfType<ScrollContentPresenter>().First();
            int checkedFrames = 0;
            var clipped = new List<string>();
            foreach (var item in Visuals(browser).OfType<ListBoxItem>().Where(i => i.IsVisible))
            {
                // Only cards wholly inside the list's visible area: scrolling hides the rest by design.
                var card = item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize));
                if (card.Top < 0 || card.Bottom > viewport.ActualHeight) continue;
                var preview = Visuals(item).OfType<CollectionPreview>().First();
                foreach (var frame in Visuals(preview).OfType<Canvas>().SelectMany(c => c.Children.OfType<Border>()))
                {
                    checkedFrames++;
                    for (DependencyObject? a = VisualTreeHelper.GetParent(frame); a != null && !ReferenceEquals(a, browser); a = VisualTreeHelper.GetParent(a))
                    {
                        if (a is not FrameworkElement element) continue;
                        var bounds = frame.TransformToAncestor(element).TransformBounds(new Rect(frame.RenderSize));
                        Rect? limit = element.ClipToBounds ? new Rect(element.RenderSize)
                            : VisualTreeHelper.GetClip(element)?.Bounds ?? LayoutInformation.GetLayoutClip(element)?.Bounds;
                        if (limit is { } box && !box.Contains(Rect.Inflate(bounds, -0.5, -0.5)))
                        {
                            clipped.Add($"{(item.DataContext as CollectionChoice)?.Name}: clipped by {element.GetType().Name}{(element.Name is { Length: > 0 } n ? "#" + n : "")} frame={bounds} clip={box}");
                            break;
                        }
                    }
                }
            }
            var shot = new RenderTargetBitmap((int)browser.ActualWidth, (int)browser.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            shot.Render(browser);
            ImageOutput.SavePng(shot, Path.Combine(folder, "pile-sidebar.png"));
            report.Add($"frames checked={checkedFrames}");
            report.AddRange(clipped.Distinct().Take(12));
            if (checkedFrames < 6) throw new InvalidOperationException("The pile sidebar showed too few photos to check.");
            if (clipped.Count > 0) throw new InvalidOperationException("Scattered photos in the pile sidebar are sliced by a clip.");
            report.Add("Pile photos reach past their card without being sliced PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "pile-edges-report.txt"), report);
            window?.Close();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
