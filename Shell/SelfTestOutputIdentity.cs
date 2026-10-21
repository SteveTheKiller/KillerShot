using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-output-identity &lt;folder&gt;. With image A open
/// and the album shown over it, Ctrl+C and Ctrl+P act on the capture the keyboard is on (B), in the
/// grid, timeline and large carousel, and on nothing while the search box has focus.</summary>
internal static class SelfTestOutputIdentity
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-output-identity") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static BitmapSource Solid(byte r, byte g, byte b)
    {
        var px = new byte[64 * 48 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new output-identity test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            var a = Solid(220, 20, 20);
            CaptureLibrary.AddNow(Solid(20, 20, 220), DateTime.Now, "window", "Capture B", null);
            App.Instance.SetDocument(new EditorDocument(a));
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(200);

            foreach (var view in new[] { "grid", "timeline", "flow" })
            {
                window.TestShowLibraryView(view);
                await Task.Delay(300);
                window.UpdateLayout();
                if (!window.Library.IsVisible) throw new InvalidOperationException(view + ": the album is not over the image.");
                var target = Visuals(window.Library).OfType<FrameworkElement>()
                    .FirstOrDefault(e => e.IsVisible && (e is Button { Tag: CaptureItem } || e is CoverFlow { MainView: true }))
                    ?? throw new InvalidOperationException(view + ": no capture control is showing.");
                target.Focus();
                Keyboard.Focus(target);
                var item = window.Library.FocusedCapture() ?? throw new InvalidOperationException(view + ": the focused capture was not found.");
                var px = new byte[4];
                new FormatConvertedBitmap(CaptureLibrary.LoadImage(item), PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1, 1, 1, 1), px, 4, 0);
                if (px[0] != 220 || px[2] != 20) throw new InvalidOperationException(view + ": Copy and Print would use the editor image instead of capture B.");
                report.Add(view + ": focused capture is B PASS");
            }

            var search = (TextBox)window.Library.FindName("SearchBox");
            window.Library.FocusSearch();
            await Task.Delay(200);
            if (!search.IsKeyboardFocusWithin) throw new InvalidOperationException("The search box did not take focus.");
            if (window.Library.FocusedCapture() != null) throw new InvalidOperationException("Search focus still resolves a capture.");
            report.Add("search focus: no capture, editor image not used PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "output-identity-report.txt"), report);
            window?.Close();
            App.Instance.Shutdown(code);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
