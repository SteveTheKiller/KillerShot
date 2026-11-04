using System.IO;
using System.Windows;
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
}
