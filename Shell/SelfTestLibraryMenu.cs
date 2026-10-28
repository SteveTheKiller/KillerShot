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

/// <summary>KillerShot.exe --selftest-library-menu &lt;folder&gt;: Library from the sidebar's
/// right-click menu opens the themed album flyout without stalling the window. The window sits
/// off-screen and the flyout is fully transparent, so nothing shows.</summary>
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
            var popup = (Popup)window.FindName("LibrarySettingsPopup");
            if (popup.Child is UIElement card) { card.Opacity = 0; card.IsHitTestVisible = false; }

            // The sidebar's own menu, opened for real (transparent), and Library clicked through its
            // automation peer: the same path as a mouse click, with the menu holding the mouse.
            var sidebar = (FrameworkElement)window.FindName("NavigationDock");
            var menu = sidebar.ContextMenu;
            menu.Opacity = 0;
            menu.IsOpen = true;
            await Idle();
            var library = menu.Items.OfType<MenuItem>().First(item => (string)item.Header == LocaleManager.Loc("Str_Btn_Library"));
            report.Add("menu_rows=" + string.Join(" | ", menu.Items.OfType<MenuItem>().Select(item => item.Header)));
            File.WriteAllLines(Path.Combine(folder, "library-menu-report.txt"), [.. report, "clicking_library=STARTED"]);
            var timer = Stopwatch.StartNew();
            var peer = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.MenuItemAutomationPeer(library)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
            peer.Invoke();
            timer.Stop();
            report.Add($"library_click_ms={timer.ElapsedMilliseconds}");
            var idle = Stopwatch.StartNew();
            var wait = Idle();
            if (await Task.WhenAny(wait, Task.Delay(5000)) != wait) report.Add("ui_thread_stalled_after_library_click=TRUE");
            report.Add($"idle_after_click_ms={idle.ElapsedMilliseconds}");
            report.Add($"popup_open={popup.IsOpen}");
            var opened = PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                .Select(source => source.RootVisual).OfType<Window>().Where(w => w != window).Select(w => w.GetType().FullName + " title=" + w.Title);
            report.Add("other_windows=" + string.Join(", ", opened));
            if (popup.Child is Border border)
                report.Add($"card_background={(border.Background as SolidColorBrush)?.Color.ToString() ?? border.Background?.ToString() ?? "none"}");
            foreach (string error in errors) report.Add("unhandled=" + error);
            exit = errors.Count == 0 && popup.IsOpen ? 0 : 1;
            popup.IsOpen = false;
        }
        catch (Exception error) { report.Add(error.ToString()); foreach (string e in errors) report.Add("unhandled=" + e); }
        finally { try { window?.Close(); } catch { } }
        File.WriteAllLines(Path.Combine(folder, "library-menu-report.txt"), report);
        Application.Current.Shutdown(exit);
    }

    private static Task Idle() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
