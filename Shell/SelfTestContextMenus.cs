using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-context-menus &lt;folder&gt;: each right-click menu holds what belongs
/// where the click lands (sidebar, carousel, content), the sidebar minimizes to one-line
/// collections, the picture menu shows nothing greyed out, a menu's card opens against the
/// pointer, and the sidebar slides instead of jumping. The editor sits off-screen and no menu is
/// opened on screen.
/// </summary>
internal static class SelfTestContextMenus
{
    public static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-context-menus", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int exit = 1;
        MainWindow? window = null;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            App.InitializeKit();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, Width = 1280, Height = 820 };
            window.Show();
            await Idle();

            var navigation = (CollectionStrip)Field(window, "_navigationStrip")!;
            var carousel = (CollectionStrip)Field(window, "_strip")!;
            var library = (FrameworkElement)window.FindName("Library");
            string[] Rows(FrameworkElement owner)
            {
                var menu = owner.ContextMenu!;
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                return menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible).Select(item => (string)item.Header).ToArray();
            }
            string L(string key) => LocaleManager.Loc(key);

            var sidebar = Rows(navigation);
            report.Add("sidebar=" + string.Join(" | ", sidebar));
            Check(sidebar.Contains(L("Str_Dock_Left")) && sidebar.Contains(L("Str_Dock_Right")) && sidebar.Contains(L("Str_Sidebar_Compact"))
                && sidebar.Contains(L("Str_CollectionPreview")), "The sidebar menu must hold the sidebar's sides, previews and Minimize.");
            Check(!sidebar.Contains(L("Str_Dock_Top")) && !sidebar.Contains(L("Str_Lib_ViewGrid")) && !sidebar.Contains(L("Str_Rail_Reverse"))
                && !sidebar.Contains(L("Str_Btn_Library")), "The sidebar menu must not hold carousel, content or title-bar options.");
            var strip = Rows(carousel);
            report.Add("carousel=" + string.Join(" | ", strip));
            Check(strip.Contains(L("Str_Dock_Top")) && strip.Contains(L("Str_Dock_Bottom")) && !strip.Contains(L("Str_Dock_Left"))
                && !strip.Contains(L("Str_Lib_ViewGrid")), "The carousel menu must hold the carousel's positions only.");
            var content = Rows(library);
            report.Add("content=" + string.Join(" | ", content));
            Check(content.Contains(L("Str_Lib_ViewGrid")) && content.Contains(L("Str_Lib_ViewTimeline"))
                && !content.Any(row => row == L("Str_Dock_Left") || row == L("Str_Dock_Top") || row == L("Str_Sidebar_Compact")),
                "The content menu must hold the views and nothing from the sidebar or carousel.");
            Check(L("Str_Dock_Left") == "Sidebar on the left" && L("Str_Dock_Top") == "Carousel at the top", "The dock options must name what they move.");
            report.Add("menus belong to where the click lands, with plain names: PASS");

            navigation.Compact = true;
            await Idle();
            var browser = (ListBox)Field(navigation, "_browser")!;
            var choices = browser.Items.OfType<CollectionChoice>().ToArray();
            Check(choices.Length > 0 && choices.All(choice => choice.Style == "rows" && choice.Preview.Count == 0),
                "Minimize collections must show every collection as one line without photos.");
            navigation.Compact = false;
            await Idle();
            Check(browser.Items.OfType<CollectionChoice>().All(choice => choice.Style != "rows"), "Turning Minimize off must bring the previews back.");
            report.Add("Minimize collections shows one line each and turns back: PASS");

            // A real album capture in the editor, so the menu has a picture to act on.
            var stage = (FrameworkElement)window.FindName("EditorStage");
            var editor = stage.ContextMenu!;
            var opening = new ContextMenuEventArgsProbe(stage);
            Check(opening.Cancelled, "With no picture open, the picture menu must not open as an empty card.");
            var pixels = BitmapSourceOf(64, 48);
            long id = CaptureLibrary.AddNow(pixels, DateTime.Now, "region", "Menu sample", null);
            App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single(item => item.Id == id));
            await Idle();
            opening = new ContextMenuEventArgsProbe(stage);
            Check(!opening.Cancelled, "With a picture open, the picture menu must open.");
            var greyed = Visible(editor).Where(item => !item.IsEnabled).Select(item => (string)item.Header).ToArray();
            Check(greyed.Length == 0, "The picture menu shows greyed-out items: " + string.Join(", ", greyed));
            var shown = editor.Items.OfType<FrameworkElement>().Where(element => element.Visibility == Visibility.Visible).ToArray();
            report.Add("picture_menu=" + string.Join(" | ", shown.Select(element => element is MenuItem item ? item.Header?.ToString() : "---")));
            Check(shown.Length > 0 && shown[0] is not Separator && shown[^1] is not Separator
                && !shown.Zip(shown.Skip(1)).Any(pair => pair.First is Separator && pair.Second is Separator),
                "Hidden items must not leave separators at the ends or side by side.");
            Check(shown.OfType<MenuItem>().Any(item => Equals(item.Tag, "deletecapture") && (string)item.Header == L("Str_Lib_Delete")),
                "The picture menu must offer Delete for an album capture.");
            Check(!shown.OfType<MenuItem>().Any(item => Equals(item.Tag, "delete")), "Delete the selected mark must be hidden with no mark selected.");
            report.Add("picture menu: Delete for the capture, nothing greyed, no stray separators, no empty card: PASS");

            // 300 x 400 popup with the family halo, at 100% and 150%.
            var halo = new Thickness(22, 18, 22, 26);
            foreach (double scale in new[] { 1.0, 1.5 })
            {
                var pointer = new Vector(500, 600);
                var places = PointerMenus.Candidates(pointer, new Size(300 * scale, 400 * scale), halo, scale, scale);
                Check(Math.Abs(places[0].X + halo.Left * scale - (pointer.X + 2 * scale)) < .01
                    && Math.Abs(places[0].Y + halo.Top * scale - (pointer.Y + 2 * scale)) < .01, "Below right, the card's corner must touch the pointer.");
                Check(Math.Abs(places[1].Y + 400 * scale - halo.Bottom * scale - (pointer.Y - 2 * scale)) < .01,
                    "Above, the card's bottom must touch the pointer, not sit 30px over it.");
                Check(Math.Abs(places[2].X + 300 * scale - halo.Right * scale - (pointer.X - 2 * scale)) < .01,
                    "To the left, the card's right edge must touch the pointer.");
            }
            report.Add("menu cards open against the pointer in every direction, at 100% and 150%: PASS");

            // The capture bar is title-bar height: built but never shown, measured, and rendered.
            var monitor = new KillerShot.Capture.MonitorBounds(IntPtr.Zero, new KillerShot.Native.Win32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }, true);
            var frozen = new KillerShot.Capture.FrozenDesktop(monitor.Bounds, new byte[1920 * 1080 * 4], [monitor], []);
            var bar = new CaptureBar(new RegionSelector(frozen, showBar: true), monitor);
            var card = (FrameworkElement)bar.Content;
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Arrange(new Rect(card.DesiredSize));
            card.UpdateLayout();
            double titleBar = window.TryFindResource("TitleBarHeight") is double t ? t : 32;
            double visible = card.DesiredSize.Height - card.Margin.Top - card.Margin.Bottom;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth + card.Margin.Left + card.Margin.Right),
                (int)Math.Ceiling(card.ActualHeight + card.Margin.Top + card.Margin.Bottom), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(card);
            ImageOutput.SavePng(bitmap, Path.Combine(folder, "capture-bar.png"));
            report.Add($"capture_bar_visible_height={visible:F0} title_bar={titleBar:F0}");
            Check(Math.Abs(visible - titleBar) <= 1, "The capture bar must be title-bar height.");
            bar.Close();
            report.Add("capture bar is title-bar height: PASS");

            var dock = (DockHost)window.FindName("NavigationDock");
            double width = dock.PaneWidth;
            dock.Minimize();
            await Task.Delay(70);
            var track = (ColumnDefinition?)Field(dock, "_paneTrack");
            double midway = track?.ActualWidth ?? -1;
            await Task.Delay(400);
            await Idle();
            Check(midway > 1 && midway < width - 1, $"Minimizing must slide the sidebar, not jump (midway {midway:F0} of {width:F0}).");
            Check(dock.IsMinimized, "The sidebar must end minimized after the slide.");
            dock.Restore();
            await Task.Delay(500);
            await Idle();
            var open = (ColumnDefinition?)Field(dock, "_paneTrack");
            Check(!dock.IsMinimized && open != null && Math.Abs(open.ActualWidth - Math.Max(dock.MinPaneSize, width)) < 2,
                "Restoring must slide the sidebar back to its width.");
            report.Add($"sidebar slides closed and open (midway {midway:F0} of {width:F0} px): PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally { try { window?.Close(); } catch { } }
        File.WriteAllLines(Path.Combine(folder, "context-menus-report.txt"), report);
        Application.Current.Shutdown(exit);
    }

    /// <summary>Raises the right-click opening event the way WPF does, and reads whether the menu
    /// cancelled itself. Nothing is shown.</summary>
    private sealed class ContextMenuEventArgsProbe
    {
        public bool Cancelled { get; }

        public ContextMenuEventArgsProbe(FrameworkElement target)
        {
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [target, true], null)!;
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            target.RaiseEvent(args);
            Cancelled = args.Handled;
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource BitmapSourceOf(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static IEnumerable<MenuItem> Visible(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible))
        {
            yield return item;
            foreach (var child in Visible(item)) yield return child;
        }
    }

    private static object? Field(object owner, string name)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field) return field.GetValue(owner);
        return null;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Task Idle() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
