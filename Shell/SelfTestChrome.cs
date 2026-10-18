using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

// Focused off-screen checks with isolated settings: --selftest-chrome <folder>.
internal static partial class SelfTestChrome
{
    public static bool TryRun(string[] args)
    {
        int index = Array.FindIndex(args, a => a.Equals("--selftest-chrome", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (index + 1 >= args.Length) throw new ArgumentException("The chrome check requires an output folder.");
        string folder = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool passed, string label)
        {
            log.AppendLine($"{(passed ? "PASS" : "FAIL")} {label}");
            if (!passed) failures++;
        }
        try
        {
            var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                Width = 1000, Height = 640 };
            window.Show();
            window.TestShowShortcuts(false);
            await Task.Delay(500);
            window.UpdateLayout();
            var viewer = (ScrollViewer)window.FindName("KsListHostScroll");
            var parent = (FrameworkElement)viewer.Parent;
            var bounds = viewer.TransformToAncestor(parent).TransformBounds(new Rect(viewer.RenderSize));
            Check(Math.Abs(bounds.Right - parent.ActualWidth - parent.Margin.Right) < 1,
                "Shortcut scrollbar reaches the card edge");
            var content = (FrameworkElement)viewer.Content;
            Check(Math.Abs(content.Margin.Right + viewer.Margin.Right) < 1,
                "Shortcut text preserves the card inset");
            viewer.ScrollToEnd();
            window.UpdateLayout();
            Check(viewer.ScrollableHeight > 0 && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 1,
                "Shortcut list reaches its final row");
            Save((FrameworkElement)window.Content, Path.Combine(folder, "shortcuts.png"));
            window.TestCloseOverlays();
            var sample = new DrawingVisual();
            using (var drawing = sample.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 640, 360));
                drawing.DrawRectangle(Brushes.Gold, null, new Rect(40, 40, 200, 160));
            }
            var image = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
            image.Render(sample);
            App.Instance.SetDocument(new KillerShot.Editor.EditorDocument(image));

            var confirm = new ConfirmDialog("Confirm", "Check the dialog frame.", "OK");
            var picker = new FileDialog(FileDialogMode.Open);
            // Keep the same dialog instances alive through every palette replacement.
            foreach (Theme theme in Enum.GetValues<Theme>())
            {
                ThemeManager.Apply(theme);
                var accents = theme is Theme.Dark or Theme.Light or Theme.Black or Theme.SE98
                    ? Enum.GetValues<Accent>() : new[] { Accent.Green };
                foreach (Accent accent in accents)
                {
                    ThemeManager.ApplyAccent(theme, accent);
                    await Task.Delay(1);
                    CheckSurfaces(window, theme, accent, folder, Check);
                    Check(((TextBlock)confirm.FindName("HeadingText")).Foreground.ToString() == Application.Current.FindResource("TextBrush").ToString(),
                        $"{theme}/{accent} confirmation heading follows the current theme");
                    Check(((TextBlock)confirm.FindName("DetailText")).Foreground.ToString() == Application.Current.FindResource("MutedTextBrush").ToString(),
                        $"{theme}/{accent} confirmation detail follows the current theme");
                    Brush expected = theme == Theme.SE98 ? Brushes.Black
                        : (Brush)Application.Current.FindResource("AppBorderBrush");
                    foreach (Window dialog in new Window[] { confirm, picker })
                    {
                        var border = (Border)dialog.FindName("RootBorder");
                        Check(border.BorderBrush?.ToString() == expected.ToString(),
                            $"{theme}/{accent} {dialog.GetType().Name} frame follows the current theme");
                        Check(border.Background?.ToString() == Application.Current.FindResource("BackgroundBrush").ToString(),
                            $"{theme}/{accent} {dialog.GetType().Name} surface follows the current theme");
                        if (accent == Accent.Green && theme is Theme.Dark or Theme.Light or Theme.SE98)
                        {
                            var root = (FrameworkElement)dialog.Content;
                            root.Measure(new Size(dialog.Width, 520));
                            root.Arrange(new Rect(0, 0, dialog.Width, 520));
                            root.UpdateLayout();
                            border.BeginAnimation(UIElement.OpacityProperty, null);
                            border.Opacity = 1;
                            Save(root, Path.Combine(folder, $"{theme}-{dialog.GetType().Name}.png"));
                        }
                    }
                }
            }
            App.Instance.SetDocument(null);
            var library = (LibraryView)window.FindName("Library");
            var shadow = (Border)window.FindName("ContentPaneShadow");
            library.SetView("shelves");
            Check(shadow.Visibility == Visibility.Collapsed, "Library shelves hide the pane shadow");
            library.SetView("flow");
            Check(shadow.Visibility == Visibility.Visible, "Library carousel restores the pane shadow");
            ThemeManager.Apply(Theme.SE98);
            Check(shadow.Effect == null, "Carousel clears the shadow when switching to 98SE");
            ThemeManager.Apply(Theme.Dark);
            Check(shadow.Effect != null, "Carousel restores the shadow when leaving 98SE");
            confirm.Close();
            picker.Close();
            window.Close();
        }
        catch (Exception error)
        {
            failures++;
            log.AppendLine(error.ToString());
        }
        log.AppendLine($"failures={failures}");
        File.WriteAllText(Path.Combine(folder, "report.txt"), log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
            (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
