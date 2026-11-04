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
