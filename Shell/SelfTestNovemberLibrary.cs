using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerShot.Services;
using KillerShot.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Shell;

internal static class SelfTestNovemberLibrary
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--selftest-november-library") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : "query");
        return true;
    }

    private static async Task RunAsync(string folder, string scenario)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            CaptureLibrary.OpenAuto();
            string screenshots = Path.Combine(folder, "screenshots");
            Directory.CreateDirectory(screenshots);
            FolderCaptures.UseFolder(screenshots);
            await FolderCaptures.RefreshScreenshotsAsync();
            string recordings = Path.Combine(folder, "recordings");
            Directory.CreateDirectory(recordings);
            VideoSettings.OutputFolder = recordings;
            FolderCaptures.RecordingsFolderOverride = recordings;
            await FolderCaptures.RefreshFolderAsync(FolderCaptures.RecordingsSource);
            App.InitializeKit();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            LocaleManager.Apply(Locale.EnUS);
            if (scenario is "thumbnail" or "thumbnail-duplicates")
            {
                var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 32 * 4], 256);
                image.Freeze();
                for (int i = 0; i < 4; i++) CaptureLibrary.AddNow(image, DateTime.Today.AddMinutes(i), "region", "Image " + i, null);
                var items = CaptureLibrary.Items();
                int requests = 0;
                var flow = new CoverFlow { Items = scenario == "thumbnail-duplicates" ? items.Concat([items[0]]).ToArray() : items,
                    ThumbnailSource = _ => { requests++; return image; } };
                var window = new Window { Content = flow, Width = 800, Height = 300, Left = -20000, Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
                window.Show();
                window.UpdateLayout();
                try
                {
                    await Task.Delay(100);
                    requests = 0;
                    ThumbnailCache.Clear(folders: false);
                    await CaptureLibrary.LoadThumbAsync(items[0]).WaitAsync(TimeSpan.FromSeconds(3));
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    int expected = scenario == "thumbnail-duplicates" ? 2 : 1;
                    if (requests != expected) throw new InvalidOperationException("One thumbnail arrival requested " + requests + " card images instead of " + expected + ".");
                    report.Add("One thumbnail arrival updates only its realized card PASS");
                }
                finally { window.Close(); }
            }
            else if (scenario == "query")
            {
                var view = new LibraryView { ListingReader = (_, _) => { Thread.Sleep(250); return new([], [], [], 0, false); } };
                var watch = Stopwatch.StartNew();
                view.Reload();
                if (watch.ElapsedMilliseconds >= 100) throw new InvalidOperationException("A slow library query blocked its dispatcher caller for " + watch.ElapsedMilliseconds + " ms.");
                await view.ListingTask.WaitAsync(TimeSpan.FromSeconds(3));
                report.Add("Slow query leaves dispatcher caller responsive PASS");
            }
            else if (scenario == "video-busy")
            {
                string clip = Path.Combine(folder, "recording.mp4");
                using (var file = File.Create(clip)) file.SetLength(200L * 1024 * 1024);
                var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await Task.Delay(150);
                await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                using var dispatcherBudget = new DispatcherBudgetProbe(Application.Current.Dispatcher);
                try
                {
                    var import = Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "Recording"));
                    var deadline = Stopwatch.StartNew();
                    while (!CaptureLibrary.VideoBusy && !import.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(1);
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var body = (FrameworkElement)window.FindName("ScaleHost");
                    if (!CaptureLibrary.VideoBusy || body.IsEnabled)
                        throw new InvalidOperationException("Album mutation controls remained enabled during a recording operation.");
                    var ping = Stopwatch.StartNew();
                    double maximumGap = 0;
                    int pings = 0;
                    while (!import.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
                    {
                        await Task.Delay(20);
                        maximumGap = Math.Max(maximumGap, ping.Elapsed.TotalMilliseconds);
                        pings++;
                        ping.Restart();
                    }
                    await import.WaitAsync(TimeSpan.FromSeconds(1));
                    await dispatcherBudget.StopAsync();
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    if (dispatcherBudget.MaximumMilliseconds >= 100 || dispatcherBudget.Count == 0)
                        throw new InvalidOperationException("The actual library window missed its dispatcher budget: "
                            + dispatcherBudget.MaximumMilliseconds + " ms across " + dispatcherBudget.Count + " posted pings.");
                    if (!body.IsEnabled) throw new InvalidOperationException("Album controls did not recover after a recording operation.");
                    report.Add("Recording operation defers album mutations and restores controls PASS");
                    report.Add("200 MB import dispatcher queue maximum=" + dispatcherBudget.MaximumMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                        + " ms; posted pings=" + dispatcherBudget.Count);
                    report.Add("Task.Delay interval diagnostic maximum=" + maximumGap.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms; samples=" + pings);
                }
                finally { await dispatcherBudget.StopAsync(); window.Close(); }
            }
            else if (scenario == "video-exit")
            {
                var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 32 * 4], 256);
                image.Freeze();
                CaptureLibrary.AddNow(image, DateTime.Now, "region", "Picture", null);
                App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single());
                App.Instance.Document!.Scale(32, 16);
                string clip = Path.Combine(folder, "recording.mp4");
                using (var file = File.Create(clip)) file.SetLength(200L * 1024 * 1024);
                var import = Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "Recording"));
                while (!CaptureLibrary.VideoBusy && !import.IsCompleted) await Task.Delay(1);
                var watch = Stopwatch.StartNew();
                Task<bool> approval = App.Instance.CanExitAsync();
                if (watch.ElapsedMilliseconds >= 100)
                    throw new InvalidOperationException("Exit approval blocked the dispatcher behind a recording write for " + watch.ElapsedMilliseconds + " ms.");
                await import.WaitAsync(TimeSpan.FromSeconds(60));
                if (!await approval.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("Exit did not preserve pending marks after the recording finished.");
                report.Add("Exit waits asynchronously for recording work before flushing marks PASS");
            }
            else if (scenario == "opening")
            {
                bool opened = false;
                var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var view = new LibraryView
                {
                    StartupReader = () => (opened, opening.Task),
                    ListingReader = (_, _) => new([], [], [], 0, false),
                };
                var window = new Window { Content = view, Width = 800, Height = 500, ShowActivated = false,
                    Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await Task.Delay(50);
                try
                {
                    for (int i = 0; i < 12; i++) view.Reload();
                    int posted = 0;
                    void Posted(object? sender, System.Windows.Threading.DispatcherHookEventArgs e)
                    {
                        if (e.Operation.Priority == System.Windows.Threading.DispatcherPriority.Normal) posted++;
                    }
                    Application.Current.Dispatcher.Hooks.OperationPosted += Posted;
                    try
                    {
                        opened = true;
                        opening.SetResult();
                        await Task.Delay(100);
                        await view.ListingTask;
                    }
                    finally { Application.Current.Dispatcher.Hooks.OperationPosted -= Posted; }
                    if (posted > 6) throw new InvalidOperationException("Opening the library queued " + posted + " dispatcher operations after repeated reloads.");
                    report.Add("Repeated startup reloads share one opening continuation; dispatcher operations=" + posted + " PASS");
                }
                finally { window.Close(); }
            }
            else throw new ArgumentException("Unknown library scenario " + scenario);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "november-library-report.txt"), report);
        Application.Current.Shutdown(code);
    }

    private sealed class DispatcherBudgetProbe : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _sender;
        public double MaximumMilliseconds { get; private set; }
        public int Count { get; private set; }

        public DispatcherBudgetProbe(System.Windows.Threading.Dispatcher dispatcher)
        {
            _sender = new Thread(() =>
            {
                while (!_stop.Wait(20))
                {
                    long posted = Stopwatch.GetTimestamp();
                    dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
                    {
                        MaximumMilliseconds = Math.Max(MaximumMilliseconds, Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                        Count++;
                    }));
                }
            }) { IsBackground = true };
            _sender.Start();
        }

        public async Task StopAsync()
        {
            _stop.Set();
            if (_sender.IsAlive && !await Task.Run(() => _sender.Join(1000)))
                throw new TimeoutException("The dispatcher timing thread did not stop.");
        }

        public void Dispose()
        {
            _stop.Set();
            if (!_sender.IsAlive) _stop.Dispose();
        }
    }
}
