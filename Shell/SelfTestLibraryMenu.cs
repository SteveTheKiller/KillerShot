using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>KillerShot.exe --selftest-library-menu &lt;folder&gt;: the sidebar's right-click menu
/// has no Library row, and the title-bar Library button opens the album manager window, with its
/// close button, without stalling the editor. The editor sits off-screen and the manager is fully transparent, so nothing shows.</summary>
internal static class SelfTestLibraryMenu
{
    public static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-library-menu", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int exit = 1;
        MainWindow? window = null;
        var errors = new List<string>();
        Application.Current.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            App.InitializeKit();
            ThemeManager.Initialize();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, Width = 1280, Height = 820 };
            window.Show();
            await Idle();
            // Shown without blocking and fully transparent, so the test can look at it and nothing shows.
            window.PresentAlbums = dialog => { dialog.ShowActivated = false; dialog.Opacity = 0; dialog.Show(); return null; };
            var scroll = (FrameworkElement)window.FindName("LibrarySettingsScroll");

            // The sidebar's own menu, opened for real (transparent): it must not offer Library, which
            // has its own button in the title bar.
            var sidebar = (FrameworkElement)window.FindName("NavigationDock");
            var menu = sidebar.ContextMenu;
            menu.Opacity = 0;
            menu.IsOpen = true;
            await Idle();
            report.Add("menu_rows=" + string.Join(" | ", menu.Items.OfType<MenuItem>().Select(item => item.Header)));
            bool noLibraryRow = !menu.Items.OfType<MenuItem>().Any(item => (string)item.Header == LocaleManager.Loc("Str_Btn_Library"));
            report.Add($"sidebar_menu_without_library={noLibraryRow}");
            menu.IsOpen = false;
            await Idle();

            // The title-bar button, clicked through its automation peer as a mouse click would.
            var button = (Button)window.FindName("LibrarySettingsButton");
            File.WriteAllLines(Path.Combine(folder, "library-menu-report.txt"), [.. report, "clicking_library=STARTED"]);
            var timer = Stopwatch.StartNew();
            var peer = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
            peer.Invoke();
            timer.Stop();
            report.Add($"library_click_ms={timer.ElapsedMilliseconds}");
            var idle = Stopwatch.StartNew();
            var wait = Idle();
            if (await Task.WhenAny(wait, Task.Delay(5000)) != wait) report.Add("ui_thread_stalled_after_library_click=TRUE");
            report.Add($"idle_after_click_ms={idle.ElapsedMilliseconds}");
            var dialog = window.TestAlbumsDialog;
            report.Add($"albums_window_open={dialog?.IsVisible == true}");
            bool inside = dialog != null && ReferenceEquals(Window.GetWindow(scroll), dialog);
            report.Add($"album_controls_in_window={inside}");
            bool hasClose = dialog != null && Descendants(dialog).OfType<Button>().Any(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == LocaleManager.Loc("Str_Sys_Close")
                || Equals(button.ToolTip, LocaleManager.Loc("Str_Sys_Close")));
            report.Add($"close_button={hasClose}");
            report.Add($"title={dialog?.Title}");
            if (dialog?.Content is FrameworkElement face)
            {
                await Task.Delay(500);   // past the fade-in
                var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(face.ActualWidth),
                    (int)Math.Ceiling(face.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                picture.Render(face);
                ImageOutput.SavePng(picture, Path.Combine(folder, "albums-window.png"));
            }
            dialog?.Close();
            await Idle();
            bool home = ReferenceEquals(Window.GetWindow(scroll), window);
            report.Add($"controls_back_home_after_close={home}");
            foreach (string error in errors) report.Add("unhandled=" + error);
            exit = errors.Count == 0 && noLibraryRow && inside && hasClose && home && window.TestAlbumsDialog == null ? 0 : 1;
        }
        catch (Exception error) { report.Add(error.ToString()); foreach (string e in errors) report.Add("unhandled=" + e); }
        finally { try { window?.Close(); } catch { } }
        File.WriteAllLines(Path.Combine(folder, "library-menu-report.txt"), report);
        Application.Current.Shutdown(exit);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static Task Idle() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
