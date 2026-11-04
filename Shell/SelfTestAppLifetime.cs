using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>What a KillerShot process that is not the running app (a command line call, a second
/// launch, a self-test) may touch when it exits.</summary>
internal static class SelfTestAppLifetime
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-app-lifetime") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new app lifetime test folder.");
            Directory.CreateDirectory(folder);
            string playback = Path.Combine(folder, "playback");
            Directory.CreateDirectory(playback);
            VideoCaptures.UsePlaybackFolder(playback);
            string probe = Path.Combine(playback, "probe.mp4");
            File.WriteAllText(probe, "the running app is playing this");
            App.Instance.ReleaseOnExit();
            if (!File.Exists(probe))
                throw new InvalidOperationException("A process that is not the running app deleted its playback copies on exit.");
            report.Add("exiting without owning the running app leaves its playback copies PASS");
            PrintNeverTakesTheWrongPicture(folder);
            report.Add("a print request whose picture could not open prints nothing PASS");
            RecordingFrameFollowsDpiChange();
            report.Add("the recording frame returns around its rectangle after Windows rescales it PASS");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
                string line = KillerShot.Cli.Program.RegionLine(0, new RegionPreset("Left screen",
                    new KillerShot.Native.Win32.RECT(-1920, -40, 0, 1040), 1920, 1080));
                if (!line.Contains("at -1920, -40", StringComparison.Ordinal))
                    throw new InvalidOperationException("The regions listing printed a culture-specific minus sign: " + line);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
            report.Add("killershot regions prints ASCII coordinates in every region format PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "app-lifetime-report.txt"), report);
            Application.Current.Shutdown(code);
        }
        return true;
    }

    /// <summary>A recording on a monitor with another scale: Windows moves and resizes the frame
    /// window as it rescales it and then raises DpiChanged; the frame must go back around the rectangle.</summary>
    private static void RecordingFrameFollowsDpiChange()
    {
        var rect = new KillerShot.Native.Win32.RECT(200, 150, 600, 450);
        var frame = KillerShot.Video.CaptureChromeWindows.Frame(rect);
        frame.Show();
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(frame).Handle;
            // What a per-monitor rescale does to the window before DpiChanged.
            KillerShot.Native.Win32.SetWindowPos(h, IntPtr.Zero, 260, 190, 300, 200, 0x0010 /* SWP_NOACTIVATE */);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(frame);
            var args = (RoutedEventArgs)typeof(DpiChangedEventArgs).GetConstructors(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Single(c => c.GetParameters().Length == 4)
                .Invoke([dpi, dpi, Window.DpiChangedEvent, frame]);
            frame.RaiseEvent(args);
            KillerShot.Native.Win32.GetWindowRect(h, out var placed);
            int px = KillerShot.Video.CaptureChromeWindows.FramePx;
            if (placed.Left != rect.Left - px || placed.Top != rect.Top - px
                || placed.Width != rect.Width + px * 2 || placed.Height != rect.Height + px * 2)
                throw new InvalidOperationException($"After a rescale the frame sat at {placed.Left},{placed.Top} {placed.Width}x{placed.Height}.");
        }
        finally { frame.Close(); }
    }

    /// <summary>`killershot print other.png` while an edited picture is open and the user keeps it,
    /// and `killershot print` of a file that cannot be read.</summary>
    private static void PrintNeverTakesTheWrongPicture(string folder)
    {
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        BitmapSource Picture(byte shade)
        {
            var pixels = new byte[32 * 32 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = shade; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
            bitmap.Freeze();
            return bitmap;
        }
        string other = Path.Combine(folder, "other.png");
        File.WriteAllBytes(other, ImageOutput.EncodePng(Picture(200)));
        int printed = 0;
        PrintOutput.PrintOverride = (_, _) => { printed++; return true; };
        App.Instance.PresentDocumentReplacement = _ => false;
        try
        {
            var open = new EditorDocument(Picture(40));
            if (!App.Instance.SetDocument(open)) throw new InvalidOperationException("The open picture fixture did not open.");
            open.Add(new KillerShot.Editor.ShapeAnnotation(KillerShot.Editor.ShapeKind.Redact, new Point(2, 2), new Point(9, 9), Colors.Black, 1));
            App.Instance.TestRunRemoteCommand("print:" + other);
            if (printed != 0) throw new InvalidOperationException("Printing other.png printed the open picture after its replacement was declined.");
            App.Instance.TestRunRemoteCommand("print:" + Path.Combine(folder, "missing.png"));
            if (printed != 0) throw new InvalidOperationException("Printing a missing file printed the open picture.");
        }
        finally
        {
            PrintOutput.PrintOverride = null;
            App.Instance.PresentDocumentReplacement = _ => true;
            App.Instance.SetDocument(null);
            App.Instance.PresentDocumentReplacement = null;
        }
    }
}
