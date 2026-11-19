using System.IO;
using System.Windows;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-native-cache-lock &lt;folder&gt;: another process of the same version
/// holds a different e_sqlcipher.dll open in the shared version cache. Two fresh probe processes
/// must still open an album, leave the held copy untouched and share one hash-keyed copy. Writes
/// result.txt in the folder and exits 0 on success. Opens no window.
/// </summary>
internal static class SelfTestNativeCacheLock
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-native-cache-lock") return false;
        string dir = Path.GetFullPath(args[1]);
        _ = Task.Run(async () =>
        {
            var report = new List<string>();
            int failures = 0;
            void Check(bool pass, string name) { if (!pass) failures++; report.Add((pass ? "PASS " : "FAIL ") + name); }
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                string cache = Path.Combine(dir, "cache");
                string version = typeof(SqlCipherBootstrap).Assembly.GetName().Version?.ToString() ?? "0";
                string versionDir = Path.Combine(cache, version);
                Directory.CreateDirectory(versionDir);
                string held = Path.Combine(versionDir, "e_sqlcipher.dll");
                byte[] other = new byte[4096];
                for (int i = 0; i < other.Length; i++) other[i] = (byte)(i * 31);
                File.WriteAllBytes(held, other);
                // Like a loaded DLL: readers allowed, no writer, no delete, no rename over it.
                using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    for (int run = 1; run <= 2; run++)
                    {
                        string probe = Path.Combine(dir, $"probe{run}", "report.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
                        int code = await RunProbe(cache, probe);
                        string text = File.Exists(probe) ? File.ReadAllText(probe) : "(no report)";
                        Check(code == 0 && text == "ok", $"probe {run} opens an album beside a locked differing cache: {(text == "ok" ? "ok" : text.Split('\n')[0])}");
                    }
                }
                Check(File.ReadAllBytes(held).SequenceEqual(other), "held copy is untouched");
                var hashed = Directory.GetDirectories(versionDir);
                Check(hashed.Length == 1 && File.Exists(Path.Combine(hashed[0], "e_sqlcipher.dll")),
                    $"identical builds share one hash-keyed copy ({hashed.Length} folders)");
                Check(!Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(), "no temporary files left");
            }
            catch (Exception ex) { failures++; report.Add("FAIL " + ex); }
            report.Add(failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({failures})");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "result.txt"), report);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown(failures == 0 ? 0 : 1));
        });
        return true;
    }

    private static async Task<int> RunProbe(string cache, string report)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--selftest-library-native-probe");
        start.ArgumentList.Add(cache);
        start.ArgumentList.Add(report);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Native probe did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); return -1; }
        return process.ExitCode;
    }
}
