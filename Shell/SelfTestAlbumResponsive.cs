using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-album-responsive &lt;folder&gt;. Switching to an
/// album whose transition lock another process holds keeps the window responsive while it waits,
/// then opens the album once the lock is free, with the first album's captures intact.</summary>
internal static class SelfTestAlbumResponsive
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-album-responsive") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new album-responsive test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            pixel.Freeze();
            CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Kept", null);
            string first = CaptureLibrary.FilePath;
            string second = CaptureLibrary.CreateAlbum("Second");
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1000, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(200);

            // Another process holds the second album's transition lock for 1.5 seconds.
            var held = new FileStream(Path.ChangeExtension(second, ".open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _ = Task.Run(async () => { await Task.Delay(1500); held.Dispose(); });

            var beat = Stopwatch.StartNew();
            long last = 0, worst = 0;
            var heartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Input, (_, _) =>
            {
                long now = beat.ElapsedMilliseconds;
                worst = Math.Max(worst, now - last);
                last = now;
            }, window.Dispatcher);
            heartbeat.Start();
            var took = Stopwatch.StartNew();
            await window.TestOpenAlbumAsync(second);
            heartbeat.Stop();
            report.Add($"switch took {took.ElapsedMilliseconds} ms; longest interface pause {worst} ms");
            if (!string.Equals(CaptureLibrary.FilePath, second, StringComparison.OrdinalIgnoreCase) || !CaptureLibrary.IsOpen)
                throw new InvalidOperationException("The album did not open once the lock was released.");
            if (took.ElapsedMilliseconds < 1300) throw new InvalidOperationException("The switch did not wait for the other process's lock.");
            if (worst > 250) throw new InvalidOperationException($"The interface froze for {worst} ms while the album switch waited.");

            await window.TestOpenAlbumAsync(first);
            if (CaptureLibrary.Count != 1 || CaptureLibrary.Items()[0].Window != "Kept")
                throw new InvalidOperationException("Switching albums lost the first album's capture.");
            report.Add("album switch waits for the lock without freezing the window, and data is intact PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "album-responsive-report.txt"), report);
            window?.Close();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
