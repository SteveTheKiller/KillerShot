using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestScreenshotsLoading
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-screenshots-loading", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        using var reader = new DelayedReader();
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            string screenshots = Path.Combine(folder, "Screenshots");
            Directory.CreateDirectory(screenshots);
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            ImageOutput.Save(pixel, Path.Combine(screenshots, "accessible.png"));
            FolderCaptures.UseFolder(screenshots, reader);
            FolderCaptures.Enabled = true;
            App.InitializeKit();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            App.CloseToTray = true;
            // Deadline bounds the failing synchronous implementation without blocking this test forever.
            _ = Task.Run(async () => { await Task.Delay(1500); reader.Release.Set(); });
            var watch = Stopwatch.StartNew();
            var view = new LibraryView();
            view.Reload();
            watch.Stop();
            if (watch.ElapsedMilliseconds >= 1000)
                throw new InvalidOperationException($"Library construction/reload blocked {watch.ElapsedMilliseconds} ms on Screenshots I/O.");
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await App.Current.Dispatcher.InvokeAsync(() => report.Add("Dispatcher progresses while Screenshots reader is blocked: PASS"));
            if (reader.Release.IsSet || FolderCaptures.StatusForSource("folder") is not { Loading: true })
                throw new InvalidOperationException("Loading state was not published while directory work remained blocked.");
            reader.Release.Set();
            for (int i = 0; i < 100 && FolderCaptures.StatusForSource("folder") is not { Loading: false }; i++) await Task.Delay(20);
            view.Reload();
            if (FolderCaptures.ItemsForSource("folder").Count != 1 || FolderCaptures.StatusForSource("folder") is not { Available: false, Loading: false })
                throw new InvalidOperationException("Partial failure did not publish the accessible image and unavailable status.");
            report.Add("Background Screenshots read publishes accessible partial result and unavailable status: PASS");
            reader.Release.Reset();
            Task refresh = FolderCaptures.RefreshFolderAsync("folder");
            var cachedWatch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) view.Reload();
            cachedWatch.Stop();
            if (cachedWatch.ElapsedMilliseconds >= 1000 || FolderCaptures.ItemsForSource("folder", "accessible").Count != 1)
                throw new InvalidOperationException("Refresh or search blocked or discarded the cached partial snapshot.");
            reader.Release.Set();
            await refresh.WaitAsync(TimeSpan.FromSeconds(3));
            report.Add("Repeated reload/search retains cached entries during a blocked refresh: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            reader.Release.Set();
            File.WriteAllLines(Path.Combine(folder, "screenshots-loading-report.txt"), report);
            CaptureLibrary.Close();
            App.Current.Shutdown(code);
        }
    }

    private sealed class DelayedReader : IFolderDirectoryReader, IDisposable
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory, EnumerationOptions options)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new IOException("Controlled read deadline expired.");
            foreach (var file in directory.EnumerateFiles()) yield return file;
            throw new IOException("Controlled failure after accessible image.");
        }
        public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory, EnumerationOptions options) => [];
        public void Dispose() => Release.Dispose();
    }
}
