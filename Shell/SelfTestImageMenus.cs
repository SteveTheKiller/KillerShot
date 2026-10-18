using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Editor;
using KillerShot.Services;
using KillerFamily.Layout;
using KillerShot.Controls;

namespace KillerShot.Shell;

internal static class SelfTestImageMenus
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-image-menus") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Contains("--show"));
        return true;
    }

    private static async Task RunAsync(string folder, bool show)
    {
        int code = 1;
        MainWindow? window = null;
        string? reportPath = null;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new image-menu test folder.");
            Directory.CreateDirectory(folder);
            reportPath = Path.Combine(folder, "image-menu-report.txt");
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            var pixels = new byte[640 * 400 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 110; pixels[i + 1] = 65; pixels[i + 2] = 35; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(640, 400, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
            image.Freeze();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            for (int i = 0; i < 12; i++) CaptureLibrary.AddNow(image, DateTime.Now.AddMinutes(-i), "window", "Picture " + i, null);
            var doc = new EditorDocument(image);
            App.Instance.SetDocument(doc);
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(200);
            window.TestStripDock.DockTo(DockSide.Top);
            window.UpdateLayout();
            var contentPane = (FrameworkElement)window.FindName("ContentPane");
            double leftGutter = contentPane.TranslatePoint(new Point(), window.TestStripDock).X;
            if (Math.Abs(leftGutter - 8) > .01)
                throw new InvalidOperationException("Top-docked editor left gutter must be 8px; actual=" + leftGutter);
            report.Add("Top-docked editor: 8px outer left gutter PASS");
            var editor = (EditorCanvas)window.FindName("Editor");
            var stage = (FrameworkElement)window.FindName("EditorStage");
            var menu = stage.ContextMenu ?? throw new InvalidOperationException("Right-clicking the editor has no context menu.");
            void Open() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            MenuItem Item(string id) => Descendants(menu).OfType<MenuItem>().Single(m => Equals(m.Tag, id));
            Open();
            if (Item("copy").InputGestureText != "Ctrl+C" || Item("saveas").InputGestureText != "Ctrl+Shift+S"
                || !Item("copy").IsEnabled || Item("undo").IsEnabled || Item("redo").IsEnabled)
                throw new InvalidOperationException("The menu's actions, shortcuts or initial edit states are wrong.");
            doc.Add(new ShapeAnnotation(ShapeKind.Rectangle, new Point(20, 20), new Point(120, 100), Colors.Red, 4));
            Open();
            Item("undo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (doc.State.Items.Count != 0 || !doc.CanRedo) throw new InvalidOperationException("Menu Undo did not undo the mark.");
            Open();
            Item("redo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (doc.State.Items.Count != 1) throw new InvalidOperationException("Menu Redo did not restore the mark.");
            Open();
            Item("pen").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Open();
            if (editor.Tool != ToolKind.Pen || !Item("pen").IsChecked || Item("pen").InputGestureText != "P")
                throw new InvalidOperationException("The Tools menu did not select and check the Pen tool.");
            double previousZoom = editor.Zoom;
            Item("zoomin").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (editor.Zoom <= previousZoom) throw new InvalidOperationException("Menu Zoom in did not zoom the image.");
            Open();
            Item("actualsize").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (Math.Abs(editor.Zoom - 1) > .001) throw new InvalidOperationException("Menu 100% did not restore actual size.");
            Open();
            menu.ApplyTemplate();
            var surface = (FrameworkElement)VisualTreeHelper.GetChild(menu, 0);
            surface.Opacity = 1; surface.Margin = new Thickness(0);
            surface.Measure(new Size(500, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
            var capture = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            capture.Render(surface);
            ImageOutput.SavePng(capture, Path.Combine(folder, "editor-menu.png"));
            report.Add("Editor menu: shortcuts, enabled states, Undo, Redo, tool selection and zoom PASS");
            foreach (var (side, view) in new[] { (DockSide.Top, "flow"), (DockSide.Left, "list"), (DockSide.Left, "rolodex") })
            {
                window.TestStripDock.DockTo(side);
                if (side == DockSide.Left) window.TestStrip!.SetSideView(view);
                await Task.Delay(150);
                FrameworkElement browser = Visuals(window.TestStrip!).OfType<FrameworkElement>().First(e => e.IsVisible &&
                    (view == "flow" ? e is CoverFlow : view == "list" ? e is ListBox : e is Rolodex));
                if (browser is ListBox list) list.SelectedIndex = 0;
                browser.Focus();
                browser.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(browser), 0, Key.Apps) { RoutedEvent = Keyboard.KeyDownEvent });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var opened = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(s => s.RootVisual != null)
                    .SelectMany(s => Visuals(s.RootVisual)).OfType<ContextMenu>().SingleOrDefault(m => m.IsOpen);
                if (opened == null || !opened.Items.OfType<MenuItem>().Any(i => Equals(i.Header, LocaleManager.Loc("Str_Lib_Copy"))))
                    throw new InvalidOperationException(view + " has no image menu through the context-menu key.");
                opened.IsOpen = false;
                await Task.Delay(250);
                if (!ReferenceEquals(App.Instance.Document, doc)) throw new InvalidOperationException("Opening a strip menu replaced the editor document.");
                report.Add("Strip " + view + ": context menu and unchanged editor PASS");
            }
            window.TestStripDock.DockTo(DockSide.Top);
            App.Instance.SetDocument(null);
            Open();
            if (Item("copy").IsEnabled || Item("save").IsEnabled || Item("undo").IsEnabled)
                throw new InvalidOperationException("Editor menu actions must be disabled without an image.");
            App.Instance.SetDocument(doc);
            File.WriteAllLines(reportPath, report);
            if (show)
            {
                var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.SetResult();
                window.Left = 120; window.Top = 80;
                await closed.Task;
                window = null;
            }
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            window?.Close();
            if (reportPath != null) File.WriteAllLines(reportPath, report);
            App.Instance.Shutdown(code);
        }
    }

    private static IEnumerable<object> Descendants(ItemsControl parent)
    {
        foreach (object item in parent.Items)
        {
            yield return item;
            if (item is ItemsControl child) foreach (object nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
