using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFooterLayout
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-footer-layout", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        MainWindow? window = null;
        var report = new List<string>();
        int exit = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Scratch album did not open.");
            var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32);
            image.Freeze();
            window = new MainWindow { Width = 800, Height = 600, Left = -20000, Top = -20000,
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            var footer = (FrameworkElement)window.FindName("FooterBar");
            var portable = (Button)window.FindName("PortableBadge");
            portable.Visibility = Visibility.Visible;
            foreach (var theme in new[] { Theme.Dark, Theme.SE98 })
            foreach (var locale in new[] { Locale.EnUS, Locale.De })
            foreach (bool editor in new[] { false, true })
            {
                ThemeManager.Apply(theme);
                LocaleManager.Apply(locale);
                if (!App.Instance.SetDocument(editor ? new EditorDocument(image) : null))
                    throw new InvalidOperationException("The footer fixture could not change workspace.");
                foreach (int width in new[] { 640, 800, 1200 })
                {
                    window.Width = width;
                    await Task.Delay(120);
                    var information = (TextBlock)window.FindName("ImageInfoText");
                    if (editor) information.Text = "10000 x 10000 px   9999, 9999";
                    window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    window.UpdateLayout();
                    if (editor && information.Text != "10000 x 10000 px   9999, 9999")
                        throw new InvalidOperationException("The long footer readout fixture changed before its layout assertion.");
                    Rect badge = Bounds(portable);
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render((Visual)window.Content);
                    ImageOutput.SavePng(bitmap, Path.Combine(folder, $"footer-{theme}-{locale}-{editor}-{width}.png"));
                    if (badge.Left < 0 || badge.Right > footer.ActualWidth + 1)
                        throw new InvalidOperationException("Portable is outside the footer.");
                    if (editor && Rect.Intersect(badge, Bounds(information)) is { IsEmpty: false, Width: > .5 })
                        throw new InvalidOperationException("Portable overlaps the image dimensions and cursor readout.");
                    foreach (var button in Visuals(footer).OfType<ButtonBase>().Where(b => b != portable && b.IsVisible))
                    {
                        Rect bounds = Bounds(button);
                        if (bounds.Left < -.5 || bounds.Right > footer.ActualWidth + .5 || bounds.Width < 8)
                            throw new InvalidOperationException($"{theme}/{locale}/editor={editor}/{width}: footer action '{System.Windows.Automation.AutomationProperties.GetName(button)}' bounds={bounds}, footer={footer.ActualWidth}, Portable={badge}, image information={Bounds(information)}, view size={((FrameworkElement)window.FindName("FooterViewCell")).RenderSize}, view desired={((FrameworkElement)window.FindName("FooterViewCell")).DesiredSize}.");
                        Rect overlap = Rect.Intersect(badge, bounds);
                        if (!overlap.IsEmpty && overlap.Width > .5 && overlap.Height > .5)
                            throw new InvalidOperationException($"{theme}/{locale}/editor={editor}/{width}: Portable overlaps {button.Name} by {overlap.Width:F1} pixels.");
                    }
                    report.Add($"{theme}/{locale}/editor={editor}/{width}: Portable and footer controls do not overlap PASS");
                }
            }
            portable.Visibility = Visibility.Collapsed;
            window.Width = 640;
            await Task.Delay(120);
            window.UpdateLayout();
            if (((FrameworkElement)window.FindName("FooterCopyright")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Installed-mode attribution was changed by portable layout handling.");
            report.Add("Installed-mode attribution remains visible PASS");
            exit = 0;

            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(footer).TransformBounds(new Rect(element.RenderSize));
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "footer-layout-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
