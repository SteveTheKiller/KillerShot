using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFlyoutScroll
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-flyout-scroll") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        var failures = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            foreach (Theme theme in new[] { Theme.Dark, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                ThemeManager.ApplyAccent(theme, Accent.Red);
                var window = new MainWindow();
                var root = (FrameworkElement)window.Content;
                var contentRoot = (FrameworkElement)window.FindName("RootGrid");
                contentRoot.Opacity = 1;
                // Measure the real window content without creating an HWND or moving focus.
                foreach (string kind in new[] { "Capture", "Video", "Help" })
                {
                    Layout(root, 1024, 460);
                    if (kind == "Capture") window.TestShowCaptureSettings();
                    else if (kind == "Video") window.TestShowVideoSettings();
                    else window.TestShowShortcuts(false);
                    var overlay = (FrameworkElement)window.FindName(kind == "Help" ? "ShortcutsOverlay" : kind + "SettingsOverlay");
                    var scroll = (ScrollViewer)window.FindName(kind == "Help" ? "KsListHostScroll" : kind + "SettingsScroll");
                    overlay.BeginAnimation(UIElement.OpacityProperty, null);
                    overlay.Opacity = 1;
                    await Settle(root, 1024, 460);
                    var card = (FrameworkElement)VisualTreeHelper.GetParent(scroll);
                    if (kind == "Help") card = (FrameworkElement)VisualTreeHelper.GetParent(card);
                    var close = Descendants<Button>(card).First(b => b.Visibility == Visibility.Visible
                        && b.ToolTip?.ToString() == LocaleManager.Loc("Str_Sys_Close"));
                    var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                    string prefix = theme + "-" + kind;
                    Rect barBounds = Bounds(bar, card), closeBounds = Bounds(close, card);
                    report.Add($"{prefix} card={card.ActualWidth:F1}x{card.ActualHeight:F1} bar={barBounds} close={closeBounds} extent={scroll.ScrollableHeight:F1}");
                    Check(scroll.ScrollableHeight > 0, prefix + " short card must overflow", failures);
                    Check(Math.Abs(card.ActualWidth - barBounds.Right) <= 1, prefix + " scrollbar must touch the card's inner right edge", failures);
                    Check(!PositiveIntersection(barBounds, closeBounds), prefix + " scrollbar and Close hit areas must not overlap", failures);
                    Render(card, Path.Combine(folder, prefix + "-top.png"));
                    CheckCues(scroll, false, true, theme, prefix + " top", failures);
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
                    await Settle(root, 1024, 460);
                    CheckCues(scroll, true, true, theme, prefix + " middle", failures);
                    Render(card, Path.Combine(folder, prefix + "-middle.png"));
                    scroll.ScrollToBottom();
                    await Settle(root, 1024, 460);
                    CheckCues(scroll, true, false, theme, prefix + " bottom", failures);
                    Render(card, Path.Combine(folder, prefix + "-bottom.png"));
                    if (kind == "Capture")
                    {
                        var print = (CheckBox)window.FindName("PrintShrinkToggle");
                        var after = (CheckBox)window.FindName("NotifyToggle");
                        // Printing sits in the right column so the two columns end at about the same height.
                        Check(Bounds(print, card).Left > Bounds(after, card).Right,
                            prefix + " Printing must sit in the right column, beside After capture", failures);
                    }
                    window.TestCloseOverlays();
                }
                Layout(root, 1024, 820);
                window.TestShowCaptureSettings();
                var normalOverlay = (FrameworkElement)window.FindName("CaptureSettingsOverlay");
                normalOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                normalOverlay.Opacity = 1;
                await Settle(root, 1024, 820);
                var normalScroll = (ScrollViewer)window.FindName("CaptureSettingsScroll");
                Check(normalScroll.ScrollableHeight == 0, theme + " normal capture settings must fit", failures);
                CheckCues(normalScroll, false, false, theme, theme + " normal", failures);
                Render((FrameworkElement)VisualTreeHelper.GetParent(normalScroll), Path.Combine(folder, theme + "-Capture-normal.png"));
                window.TestCloseOverlays();
                Layout(root, 640, 460);
                window.TestShowCaptureSettings();
                normalOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                normalOverlay.Opacity = 1;
                await Settle(root, 640, 460);
                normalScroll.ScrollToTop();
                await Settle(root, 640, 460);
                var narrowCard = (FrameworkElement)VisualTreeHelper.GetParent(normalScroll);
                var narrowBar = (ScrollBar)normalScroll.Template.FindName("PART_VerticalScrollBar", normalScroll);
                Check(narrowCard.ActualWidth <= 592, theme + " narrow settings must fit inside the window", failures);
                Check(Math.Abs(narrowCard.ActualWidth - Bounds(narrowBar, narrowCard).Right) <= 1,
                    theme + " narrow scrollbar must remain at the card edge", failures);
                CheckCues(normalScroll, false, true, theme, theme + " narrow", failures);
                Render(narrowCard, Path.Combine(folder, theme + "-Capture-narrow.png"));
                window.TestCloseOverlays();
                window.Close();
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        report.AddRange(failures.Select(f => "FAIL " + f));
        report.Add(failures.Count == 0 ? "result=ok" : "result=error");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        App.Instance.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private static void CheckCues(ScrollViewer scroll, bool top, bool bottom, Theme theme, string name, List<string> failures)
    {
        var cues = Descendants<Border>(scroll).Where(b => b.Background is LinearGradientBrush brush
            && brush.GradientStops.Any(s => s.Color == Color.FromArgb(0x50, 0, 0, 0))
            && brush.GradientStops.Any(s => s.Color == Color.FromArgb(0x22, 0, 0, 0))).ToArray();
        var topCue = cues.SingleOrDefault(b => b.VerticalAlignment == VerticalAlignment.Top);
        var bottomCue = cues.SingleOrDefault(b => b.VerticalAlignment == VerticalAlignment.Bottom);
        Check(topCue != null && bottomCue != null, name + " must provide the canonical top and bottom scroll shadows", failures);
        if (topCue == null || bottomCue == null) return;
        Check(topCue.Visibility == (top ? Visibility.Visible : Visibility.Collapsed), name + " top shadow must follow overflow", failures);
        Check(bottomCue.Visibility == (bottom ? Visibility.Visible : Visibility.Collapsed), name + " bottom shadow must follow overflow", failures);
        Check(!topCue.IsHitTestVisible && !bottomCue.IsHitTestVisible, name + " shadows must not block controls", failures);
        Check(theme == Theme.SE98 ? topCue.Opacity == 0 && bottomCue.Opacity == 0 : topCue.Opacity > 0 && bottomCue.Opacity > 0,
            name + " shadow opacity must follow the theme", failures);
    }

    private static bool PositiveIntersection(Rect a, Rect b)
    {
        a.Intersect(b);
        return !a.IsEmpty && a.Width > 0 && a.Height > 0;
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void Check(bool passed, string message, List<string> failures)
    {
        if (!passed) failures.Add(message);
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static async Task Settle(FrameworkElement root, double width, double height)
    {
        Layout(root, width, height);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Layout(root, width, height);
    }

    private static void Render(FrameworkElement root, string path)
    {
        if (VisualTreeHelper.GetParent(root) is Border frame) root = frame;
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        ImageOutput.SavePng(image, path);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
