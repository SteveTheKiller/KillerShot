using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestViewCards
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-view-cards") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>
        {
            "H1: The renderer always includes or excludes card stock; a per-view toggle must visibly change stock pixels.",
            "H2: Only the current instance changes; switching hosts must retain each view's independent preference.",
            "H3: Rebuilding the face changes selection or interaction; selection and Enter activation must survive both states."
        };
        MainWindow? window = null;
        int code = 1;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new view-card test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            ThemeManager.Apply(Theme.Black);
            ThemeManager.ApplyAccent(Theme.Black, Accent.Teal);
            CardColors.SetCard(Colors.Magenta);
            Check(CaptureLibrary.OpenAuto(), "Synthetic album could not open.");
            byte[] pixels = new byte[180 * 120 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 220; pixels[i + 1] = 220; pixels[i + 2] = 20; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(180, 120, 96, 96, PixelFormats.Bgra32, null, pixels, 720);
            image.Freeze();
            CaptureLibrary.AddNow(image, DateTime.Now, "region", "Card sample", null);
            window = new MainWindow { Width = 1000, Height = 650, Left = 20, Top = 20, ShowActivated = false };
            window.Show();
            await Task.Delay(350);
            var library = (LibraryView)window.FindName("Library");
            library.SetView("flow");
            window.UpdateLayout();
            var flow = (CoverFlow)library.FindName("BigFlow");
            long? selected = flow.SelectedItem?.Id;
            int plain = RenderStock(flow, Path.Combine(folder, "carousel-plain.png"));
            Check(plain == 0, "Carousel defaults must preserve image-only rendering.");
            await ToggleMenu(library, expected: false);
            int stock = RenderStock(flow, Path.Combine(folder, "carousel-cards.png"));
            Check(stock > 100 && flow.SelectedItem?.Id == selected, "Carousel cards did not render or changed selection.");
            await ToggleMenu(library, expected: true);
            Check(RenderStock(flow, Path.Combine(folder, "carousel-plain-restored.png")) == 0,
                "Turning carousel cards off did not restore image-only rendering.");
            report.Add($"carousel_card_stock_pixels={stock} plain_pixels={plain}; reversible=True selection_preserved=True");
            await ToggleMenu(library, expected: false);
            App.Instance.SetDocument(new EditorDocument(image));
            var dock = (DockHost)window.FindName("StripDock");
            var strip = (CollectionStrip)dock.Pane!;
            dock.DockTo(DockSide.Bottom);
            dock.Restore();
            window.UpdateLayout();
            await Task.Delay(80);
            var stripFlow = Visuals(strip).OfType<CoverFlow>().Single();
            Check(RenderStock(stripFlow, Path.Combine(folder, "strip-carousel-cards.png")) > 100,
                "The collection carousel did not retain the album carousel's card preference.");
            await ToggleMenu(strip, expected: true);
            Check(RenderStock(stripFlow, Path.Combine(folder, "strip-carousel-plain.png")) == 0,
                "Turning the strip carousel's info cards off did not remove card stock.");
            await ToggleMenu(strip, expected: false);
            report.Add("per_view_preferences_shared_across_hosts=True");
            ThemeManager.Apply(Theme.SE98);
            await Task.Delay(100);
            window.UpdateLayout();
            await Task.Delay(80);
            Check(RenderStock(stripFlow, Path.Combine(folder, "carousel-SE98-cards.png")) > 100,
                "98SE carousel cards did not render.");
            long? flowActivated = null;
            stripFlow.ItemActivated += item => flowActivated = item.Id;
            stripFlow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(stripFlow), 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(flowActivated == selected, "Enter did not open the selected carousel card.");
            report.Add("98SE_carousel_cards_and_activation_preserved=True");
            var narrow = new CoverFlow
            {
                ShowInfoCards = true, ThumbnailSource = _ => image,
                Items = [new CaptureItem(501, DateTime.Today, 1, 4000, "region", "Tall image", null, null, null)],
            };
            narrow.Measure(new Size(400, 280));
            narrow.Arrange(new Rect(0, 0, 400, 280));
            RenderStock(narrow, Path.Combine(folder, "carousel-narrow-cards.png"));
            report.Add("strip_thumbnail_hand_cursor_and_narrow_card_render=True");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "view-cards-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    private static async Task ToggleMenu(FrameworkElement element, bool expected)
    {
        var menu = element.ContextMenu;
        Check(menu != null, "The view has no existing context menu.");
        menu!.PlacementTarget = element;
        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();
        RoutedEventHandler onOpened = (_, _) => opened.TrySetResult();
        RoutedEventHandler onClosed = (_, _) => closed.TrySetResult();
        menu.Opened += onOpened;
        menu.Closed += onClosed;
        menu.IsOpen = true;
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var item = menu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Header?.ToString() == "Show info cards");
        Check(item != null, "The view context menu needs a Show info cards toggle.");
        Check(item!.IsCheckable && item.IsChecked == expected, "The info-card menu state does not match its view.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        menu.Opened -= onOpened;
        menu.Closed -= onClosed;
        await Task.Delay(80);
        element.UpdateLayout();
    }

    private static int RenderStock(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        ImageOutput.SavePng(bitmap, path);
        byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        int stock = 0;
        for (int i = 0; i < data.Length; i += 4)
            if (data[i] > 170 && data[i + 1] < 70 && data[i + 2] > 170 && data[i + 3] > 200) stock++;
        return stock;
    }


    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
