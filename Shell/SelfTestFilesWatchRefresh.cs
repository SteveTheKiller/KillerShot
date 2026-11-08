using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using KillerShot.Files;

namespace KillerShot.Shell;

internal static class SelfTestFilesWatchRefresh
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-files-watch-refresh", var folder]) return false;
        _ = Run(Path.GetFullPath(folder));
        return true;
    }

    private static async Task Run(string folder)
    {
        int exit = 1;
        bool created = false;
        var report = new StringBuilder();
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a fresh watcher fixture.");
            Directory.CreateDirectory(folder);
            created = true;
            Settings.UseFolder(Path.Combine(folder, "settings"));
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            for (int trial = 0; trial < 5; trial++)
            {
                string watched = Directory.CreateDirectory(Path.Combine(folder, "trial-" + trial)).FullName;
                using var browser = new Browser();
                if (!await browser.NavigateTo(watched)) throw new IOException("Fixture navigation failed.");
                string file = Path.Combine(watched, "continuous.txt");
                using var stop = new CancellationTokenSource();
                var clock = Stopwatch.StartNew();
                double firstVisibleMs = -1;
                browser.State.Results.CollectionChanged += (_, _) =>
                {
                    if (firstVisibleMs < 0 && browser.State.Results.Any(entry => entry.FilePath == file))
                        firstVisibleMs = clock.Elapsed.TotalMilliseconds;
                };
                long writes = 0;
                var producer = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        File.WriteAllText(file, Interlocked.Increment(ref writes).ToString());
                        Thread.Yield();
                    }
                });
                bool visible;
                try
                {
                    while (clock.Elapsed < TimeSpan.FromSeconds(1)
                        && !browser.State.Results.Any(entry => entry.FilePath == file)) await Task.Delay(10);
                    visible = firstVisibleMs is >= 0 and < 1000;
                }
                finally { stop.Cancel(); await producer; }
                report.AppendLine($"Trial {trial}: visible={visible}, firstVisibleMs={firstVisibleMs:F1}, elapsedMs={clock.ElapsedMilliseconds}, writes={writes}, error={browser.State.Error}");
                if (!visible) throw new InvalidOperationException("Continuous filesystem events postponed the visible listing beyond one second.");
            }
            report.AppendLine("PASS: five sustained filesystem change streams refresh within one second.");
            exit = 0;
        }
        catch (Exception error) { report.AppendLine("FAIL: " + error); }
        finally
        {
            if (created) File.WriteAllText(Path.Combine(folder, "watch-refresh-report.txt"), report.ToString());
            App.Instance.Shutdown(exit);
        }
    }
}
