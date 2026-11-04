using System.IO;
using System.Windows;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>An exception nobody caught on the UI thread is logged and survived, and the user is told.</summary>
internal static class SelfTestCrashLog
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-crash-log") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new crash log test folder.");
            Directory.CreateDirectory(folder);
            CrashLog.Folder = Path.Combine(folder, "app-data");
            string? warned = null;
            CrashLog.Warn = message => warned = message;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CrashLog.Install(Application.Current);
            Application.Current.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("probe")));
            await Task.Delay(500);
            string log = File.Exists(CrashLog.LogPath) ? File.ReadAllText(CrashLog.LogPath) : "";
            if (!log.Contains("probe", StringComparison.Ordinal) || warned == null || !warned.Contains("probe", StringComparison.Ordinal))
                throw new InvalidOperationException($"An unhandled UI exception was not logged and reported: log={log.Length} chars, warned={warned}.");
            report.Add("an unhandled UI-thread exception is logged to crash.log, reported, and the app keeps running PASS");
            Task.Run(() => throw new InvalidOperationException("unobserved probe"));
            await Task.Delay(300);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(300);
            if (!File.ReadAllText(CrashLog.LogPath).Contains("unobserved probe", StringComparison.Ordinal))
                throw new InvalidOperationException("An unobserved task exception was not logged.");
            report.Add("an unobserved task exception is logged PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "crash-log-report.txt"), report);
            Application.Current.Shutdown(code);
        }
    }
}
