using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;
using KillerTools.Qr;

namespace KillerShot.Shell;

internal static class SelfTestQrUi
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--selftest-qr-ui") return false;
        _ = RunAsync(args);
        return true;
    }

    private static async Task RunAsync(string[] args)
    {
        int code = 1;
        string? report = null;
        MainWindow? window = null;
        try
        {
            if (args.Length != 3) throw new ArgumentException("Use --selftest-qr-ui <new-scratch-folder> <fixture.png>.");
            string folder = Path.GetFullPath(args[1]);
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new QR UI test folder.");
            Directory.CreateDirectory(folder);
            report = Path.Combine(folder, "qr-ui-report.txt");
            Settings.UseFolder(Path.Combine(folder, "settings"));
            Settings.Set("LibraryView", "grid");
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            App.Instance.SetDocument(new EditorDocument(QrImages.Load(File.ReadAllBytes(args[2]))));
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 980, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual };
            if (window.FindName("QrReadingButton") is not Button button) throw new InvalidOperationException("The QR rail toggle is missing.");
            if (CaptureLibrary.QrReadingEnabled || Equals(button.Tag, "on")) throw new InvalidOperationException("The QR rail toggle must start off.");
            if (button.Content?.ToString() != "\uED14" || button.ToolTip?.ToString() != "QR code reading") throw new InvalidOperationException("The QR rail glyph or tooltip is wrong.");
            var captureMenu = (ContextMenu)window.FindName("CaptureMenu");
            if (!captureMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Read QR code"))
                throw new InvalidOperationException("The capture menu is missing Read QR code.");
            captureMenu.ApplyTemplate();
            var menuSurface = (FrameworkElement)VisualTreeHelper.GetChild(captureMenu, 0);
            menuSurface.Opacity = 1;
            menuSurface.Margin = new Thickness(0);
            menuSurface.Measure(new Size(400, double.PositiveInfinity));
            menuSurface.Arrange(new Rect(menuSurface.DesiredSize)); menuSurface.UpdateLayout();
            var menuImage = new RenderTargetBitmap((int)Math.Ceiling(menuSurface.ActualWidth), (int)Math.Ceiling(menuSurface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            menuImage.Render(menuSurface);
            ImageOutput.SavePng(menuImage, Path.Combine(folder, "qr-capture-menu.png"));
            window.Show();
            await Task.Delay(200);
            App.Instance.SetDocument(new EditorDocument(QrImages.Load(File.ReadAllBytes(args[2]))) { ReadQr = true });
            var forcedEditor = (EditorCanvas)window.FindName("Editor");
            var forcedDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!forcedEditor.Children.OfType<Button>().Any(b => b.ToolTip?.ToString() == "https://killershot.net/qr") && DateTime.UtcNow < forcedDeadline) await Task.Delay(20);
            if (!forcedEditor.Children.OfType<Button>().Any(b => b.ToolTip?.ToString() == "https://killershot.net/qr") || CaptureLibrary.QrReadingEnabled)
                throw new InvalidOperationException("Read QR code must work while background reading stays off.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!CaptureLibrary.QrReadingEnabled || !Equals(button.Tag, "on")) throw new InvalidOperationException("The rail toggle did not turn reading on.");
            var editor = (EditorCanvas)window.FindName("Editor");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!editor.Children.OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("QrBadge-")) && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!editor.Children.OfType<Button>().Any(b => b.ToolTip?.ToString() == "https://killershot.net/qr"))
                throw new InvalidOperationException("The editor did not mark the decoded QR code.");
            foreach (var theme in new[] { Theme.Black, Theme.Light, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                ThemeManager.ApplyAccent(theme, theme == Theme.Light ? Accent.Yellow : Accent.Teal);
                await Task.Delay(150);
                window.UpdateLayout();
                var root = (FrameworkElement)window.Content;
                var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(root);
                ImageOutput.SavePng(image, Path.Combine(folder, "qr-" + theme + ".png"));
                editor.Children.OfType<Button>().Single(b => b.ToolTip?.ToString() == "https://killershot.net/qr")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (window.FindName("QrResultCard") is not FrameworkElement card)
                    throw new InvalidOperationException("Clicking the QR badge did not create a result card.");
                card.Measure(new Size(420, double.PositiveInfinity));
                card.Arrange(new Rect(card.DesiredSize));
                card.UpdateLayout();
                if (!Buttons(card).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QrCopy")
                    || !Buttons(card).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QrOpen"))
                    throw new InvalidOperationException("An allowed web result needs Copy and Open.");
                card.RenderTransform = new TranslateTransform(12, 12);
                var cardImage = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth) + 24, (int)Math.Ceiling(card.ActualHeight) + 24, 96, 96, PixelFormats.Pbgra32);
                cardImage.Render(card);
                card.RenderTransform = Transform.Identity;
                ImageOutput.SavePng(cardImage, Path.Combine(folder, "qr-card-" + theme + ".png"));
            }
            var actual = QrImages.Read(App.Instance.Document!.Image).Single();
            ThemeManager.Apply(Theme.Black);
            ThemeManager.ApplyAccent(Theme.Black, Accent.Teal);
            await Task.Delay(100);
            foreach (string payload in new[] { "javascript:alert(1)", "A note to copy", "http://example.com/path", "WIFI:S:Shop;P:p@ss\\;word;;" })
            {
                editor.SetQrCodes([actual with { Text = payload }]);
                editor.Children.OfType<Button>().Single(b => b.ToolTip?.ToString() == payload).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var card = (FrameworkElement)window.FindName("QrResultCard");
                card.Measure(new Size(420, double.PositiveInfinity)); card.Arrange(new Rect(card.DesiredSize)); card.UpdateLayout();
                bool hasOpen = Buttons(card).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QrOpen");
                if (hasOpen != payload.StartsWith("http://", StringComparison.Ordinal)) throw new InvalidOperationException("The card offered Open for a display-only or blocked payload.");
                if (!Buttons(card).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QrCopy")) throw new InvalidOperationException("Every payload must offer Copy.");
                if (payload.StartsWith("WIFI:", StringComparison.Ordinal)
                    && !Buttons(card).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QrCopyPassword"))
                    throw new InvalidOperationException("A Wi-Fi payload must offer Copy password.");
                if (payload.StartsWith("http://", StringComparison.Ordinal)
                    && !TextBlocks(card).Any(t => t.Text == LocaleManager.Loc("Str_QrWarn_PlainHttp")))
                    throw new InvalidOperationException("The warning card did not explain the HTTP risk.");
                string kind = payload.StartsWith("WIFI:", StringComparison.Ordinal) ? "wifi" : payload.StartsWith("http://", StringComparison.Ordinal) ? "warning" : payload.StartsWith("javascript:", StringComparison.Ordinal) ? "blocked" : "text";
                var detailImage = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                detailImage.Render(card);
                ImageOutput.SavePng(detailImage, Path.Combine(folder, "qr-card-" + kind + ".png"));
            }
            editor.SetQrCodes([actual]);
            var source = new FormatConvertedBitmap(App.Instance.Document.Image, PixelFormats.Bgra32, null, 0);
            var export = new FormatConvertedBitmap(App.Instance.Document.Render(), PixelFormats.Bgra32, null, 0);
            int stride = source.PixelWidth * 4;
            var originalPixels = new byte[stride * source.PixelHeight];
            var exportedPixels = new byte[originalPixels.Length];
            source.CopyPixels(originalPixels, stride, 0); export.CopyPixels(exportedPixels, stride, 0);
            if (!originalPixels.AsSpan().SequenceEqual(exportedPixels)) throw new InvalidOperationException("QR outlines were burned into the exported image.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (CaptureLibrary.QrReadingEnabled || Equals(button.Tag, "on")) throw new InvalidOperationException("The rail toggle did not turn reading off.");
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The preview album did not open.");
            long capture = CaptureLibrary.AddNow(App.Instance.Document.Image, DateTime.Now, "qr", "QR fixture", "qr-check");
            CaptureLibrary.StoreQr(capture, [actual]);
            ((Button)window.FindName("LibraryBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(350);
            window.UpdateLayout();
            await Task.Delay(350);
            window.UpdateLayout();
            if (!Descendants((FrameworkElement)window.Content).Any(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "QrAlbumMark"))
                throw new InvalidOperationException("The album card did not show its QR mark.");
            var albumRoot = (FrameworkElement)window.Content;
            var albumImage = new RenderTargetBitmap((int)Math.Ceiling(albumRoot.ActualWidth), (int)Math.Ceiling(albumRoot.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            albumImage.Render(albumRoot);
            ImageOutput.SavePng(albumImage, Path.Combine(folder, "qr-album.png"));
            CaptureLibrary.StoreOcr(capture, "See http://bit.ly/code.");
            var library = (LibraryView)window.FindName("Library");
            var linksMenu = library.BuildOcrLinksMenu(capture, library) ?? throw new InvalidOperationException("OCR links menu is missing.");
            ((MenuItem)linksMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var ocrCard = (FrameworkElement)window.FindName("QrResultCard");
            ocrCard.Measure(new Size(420, double.PositiveInfinity)); ocrCard.Arrange(new Rect(ocrCard.DesiredSize)); ocrCard.UpdateLayout();
            if (!TextBlocks(ocrCard).Any(t => t.Text == LocaleManager.Loc("Str_QrWarn_Shortener"))
                || !Buttons(ocrCard).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QrOpen"))
                throw new InvalidOperationException("OCR links did not use the checked result card.");
            var ocrImage = new RenderTargetBitmap((int)Math.Ceiling(ocrCard.ActualWidth), (int)Math.Ceiling(ocrCard.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            ocrImage.Render(ocrCard);
            ImageOutput.SavePng(ocrImage, Path.Combine(folder, "ocr-link-card.png"));
            File.WriteAllText(report, "PASS: QR rail, outlines, unchanged export, themed result cards, Copy/Open rules, HTTP warning, Wi-Fi Copy password and album mark.");
            code = 0;
        }
        catch (Exception error) { if (report != null) File.WriteAllText(report, "FAIL: " + error.Message); }
        finally
        {
            CaptureLibrary.QrReadingEnabled = false;
            CaptureLibrary.Close();
            window?.Close();
            Application.Current.Shutdown(code);
        }
    }

    private static IEnumerable<Button> Buttons(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is Button button) yield return button;
            foreach (var nested in Buttons(child)) yield return nested;
        }
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is FrameworkElement view) yield return view;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is TextBlock text) yield return text;
            foreach (var nested in TextBlocks(child)) yield return nested;
        }
    }
}
