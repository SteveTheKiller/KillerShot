using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestAlbums
{
    private static async Task CheckManagerAsync(string folder, List<string> report)
    {
        foreach (string name in new[] { "Work", "Personal", "Support captures" }) CaptureLibrary.CreateAlbum(name);
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.Enabled = false;
        foreach (var (theme, locale) in new[] { (Theme.Dark, Locale.EnUS), (Theme.SE98, Locale.EnUS), (Theme.Dark, Locale.De) })
        {
            ThemeManager.Apply(theme);
            LocaleManager.Apply(locale);
            var window = new MainWindow();
            window.TestPrepareAlbumManager(650);
            var card = (FrameworkElement)window.FindName("LibrarySettingsCard");
            var scroll = (ScrollViewer)window.FindName("LibrarySettingsScroll");
            var list = (ListBox)window.FindName("AlbumList");
            Layout(card);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Layout(card);
            Check(list.Items.Count == 4, "The manager must list every managed album.");
            string original = CaptureLibrary.FilePath;
            list.SelectedIndex = 1;
            Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1,
                "Browsing album rows must leave the current album live.");
            string prefix = theme + "-" + locale;
            RenderCard(card, Path.Combine(folder, prefix + "-normal.png"));
            window.TestPrepareAlbumManager(280);
            Layout(card);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Layout(card);
            Check(scroll.ScrollableHeight > 0, "The short manager must scroll.");
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            Rect bounds = bar.TransformToAncestor(card).TransformBounds(new Rect(bar.RenderSize));
            Check(Math.Abs(bounds.Right - card.ActualWidth + 1) <= 1.1, "The manager scrollbar must stay at the card edge.");
            RenderCard(card, Path.Combine(folder, prefix + "-short-top.png"));
            scroll.ScrollToBottom();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Layout(card);
            RenderCard(card, Path.Combine(folder, prefix + "-short-bottom.png"));
            report.Add($"{prefix}: {list.Items.Count} albums, card {card.ActualWidth:F0}x{card.ActualHeight:F0}, scroll extent {scroll.ScrollableHeight:F0}, scrollbar right {bounds.Right:F0}");
            window.Close();
        }
        report.Add("Detached real manager renders preserve the active album while browsing, fit a short viewport, and keep the scrollbar at the edge: PASS");
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
    }

    private static void RenderCard(FrameworkElement card, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(card.DesiredSize.Width), (int)Math.Ceiling(card.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        image.Render(card);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
