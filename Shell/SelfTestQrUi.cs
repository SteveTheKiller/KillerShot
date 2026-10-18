using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

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
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            App.Instance.SetDocument(new EditorDocument(QrImages.Load(File.ReadAllBytes(args[2]))));
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 980, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual };
            if (window.FindName("QrReadingButton") is not Button button) throw new InvalidOperationException("The QR rail toggle is missing.");
            if (CaptureLibrary.QrReadingEnabled || Equals(button.Tag, "on")) throw new InvalidOperationException("The QR rail toggle must start off.");
            if (button.Content?.ToString() != "\uED14" || button.ToolTip?.ToString() != "QR code reading") throw new InvalidOperationException("The QR rail glyph or tooltip is wrong.");
            window.Show();
            await Task.Delay(200);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!CaptureLibrary.QrReadingEnabled || !Equals(button.Tag, "on")) throw new InvalidOperationException("The rail toggle did not turn reading on.");
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
            }
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (CaptureLibrary.QrReadingEnabled || Equals(button.Tag, "on")) throw new InvalidOperationException("The rail toggle did not turn reading off.");
            File.WriteAllText(report, "PASS: QR rail starts off, toggles its setting/accent, and renders in Black, Light Yellow and 98SE.");
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
}
