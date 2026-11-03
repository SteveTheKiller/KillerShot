using System.IO;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestVideoOutput
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-video-output") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args[2]);
        return true;
    }

    private static async Task RunAsync(string folder, string scenario)
    {
        Directory.CreateDirectory(folder);
        int code = 1;
        var evidence = new List<string>();
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            VideoSettings.OutputFolder = folder;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            if (scenario == "session") await SessionAsync(folder);
            else if (scenario == "save") await SaveAsync(folder, evidence);
            else if (scenario == "open") await OpenAsync(folder, evidence);
            else if (scenario == "cleanup-warning") await CleanupWarningAsync(evidence);
            else throw new ArgumentException("Unknown video output scenario.");
            File.WriteAllLines(Path.Combine(folder, "video-output-report.txt"), new[] { "PASS: " + scenario }.Concat(evidence));
            code = 0;
        }
        catch (Exception error) { File.WriteAllLines(Path.Combine(folder, "video-output-report.txt"), evidence.Concat(new[] { "FAIL: " + error })); }
        finally { CaptureLibrary.Close(); }
        App.Instance.Shutdown(code);
    }

    private static string Clip(string folder, string name, byte marker)
    {
        string path = Path.Combine(folder, name + ".gif");
        File.WriteAllBytes(path, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7").Concat(new[] { marker }).ToArray());
        return path;
    }

    private static async Task SessionAsync(string folder)
    {
        string albumA = CaptureLibrary.FilePath;
        long idA = CaptureLibrary.AddVideo(Clip(folder, "A", 17), DateTime.Now, "A recording");
        var itemA = CaptureLibrary.Items().Single();
        string albumB = CaptureLibrary.CreateAlbum("B");
        CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
        long idB = CaptureLibrary.AddVideo(Clip(folder, "B", 33), DateTime.Now, "B recording");
        if (idA != idB) throw new InvalidOperationException("The video fixture did not produce colliding local IDs.");
        CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
        itemA = CaptureLibrary.Items().Single();
        var view = new LibraryView();
        string output = Path.Combine(folder, "A-export.gif");
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.StatusMessage += message => completed.TrySetResult(message);
        view.PresentExportDialog = (dialog, owner) =>
        {
            if (!CaptureLibrary.TrySwitchAlbum(albumB, null, out _)) throw new InvalidOperationException("The dialog boundary did not switch albums.");
            dialog.FileName = output;
            return true;
        };
        view.SaveAs(itemA);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (File.Exists(output)) throw new InvalidOperationException("A's Save As exported B's same-ID recording after the dialog boundary.");
        if (Directory.EnumerateFiles(folder, "*.partial").Any()) throw new InvalidOperationException("Stale video export left a partial file.");
        bool extensionRejected = false, playbackRejected = false;
        try { CaptureLibrary.VideoExtension(itemA.Id, itemA.Session); }
        catch (InvalidOperationException error) when (error.Message == LocaleManager.Loc("Str_Album_Changed")) { extensionRejected = true; }
        try { await VideoCaptures.PreparePlaybackAsync(itemA.Id, itemA.Session); }
        catch (InvalidOperationException error) when (error.Message == LocaleManager.Loc("Str_Album_Changed")) { playbackRejected = true; }
        if (!extensionRejected || !playbackRejected) throw new InvalidOperationException("Video metadata or playback accepted a stale recording session.");
        if (Directory.Exists(VideoCaptures.PlaybackFolder) && Directory.EnumerateFiles(VideoCaptures.PlaybackFolder).Any())
            throw new InvalidOperationException("Stale playback prepared a copy of the replacement album's recording.");
    }

    private static string LargeClip(string folder, string name)
    {
        string path = Clip(folder, name, 17);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(200L * 1024 * 1024);
        stream.Seek(-1, SeekOrigin.End);
        stream.WriteByte(77);
        return path;
    }

    private static async Task SaveAsync(string folder, List<string> evidence)
    {
        string clip = LargeClip(folder, "large");
        long id = await Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "Large recording"));
        var item = CaptureLibrary.Items().Single(capture => capture.Id == id);
        var view = new LibraryView();
        string output = Path.Combine(folder, "saved.gif");
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.StatusMessage += message => completed.TrySetResult(message);
        var elapsed = new System.Diagnostics.Stopwatch();
        long last = 0;
        double maxGap = 0;
        int pings = 0;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            maxGap = Math.Max(maxGap, (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            last = now;
            pings++;
        };
        view.PresentExportDialog = (dialog, owner) =>
        {
            dialog.FileName = output;
            last = System.Diagnostics.Stopwatch.GetTimestamp();
            timer.Start();
            elapsed.Restart();
            return true;
        };
        try
        {
            view.SaveAs(item);
            double returned = elapsed.Elapsed.TotalMilliseconds;
            string status = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(20);
            evidence.Add($"post_dialog_return_ms={returned:F1}; dispatcher_pings={pings}; max_gap_ms={maxGap:F1}");
            if (status != output || new FileInfo(output).Length != 200L * 1024 * 1024)
                throw new InvalidOperationException("The real Save As action did not publish the complete recording.");
            if (returned >= 100 || maxGap >= 100)
                throw new InvalidOperationException("The real post-dialog video Save As blocked the dispatcher.");
        }
        finally { timer.Stop(); }
    }

    private static async Task OpenAsync(string folder, List<string> evidence)
    {
        VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
        VideoSettings.Format = "gif";
        RecordingStorage.Choice = null;
        using var tray = new TrayHost();
        var warnings = new List<string>();
        var frames = new TestFrames();
        var controller = new VideoController(tray, sinkFactory: path => new PaddedGifSink(path),
            grabberFactory: () => frames, showWarning: (_, message) => warnings.Add(message));
        controller.AskStorage = _ => (RecordingStorage.Library, true);
        bool tracking = false;
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        int pings = 0, opens = 0;
        double maxGap = 0;
        void Tick()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref tracking))
            {
                maxGap = Math.Max(maxGap, (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                pings++;
            }
            last = now;
        }
        void Changed() { if (!CaptureLibrary.VideoBusy) Volatile.Write(ref tracking, true); }
        CaptureLibrary.Changed += Changed;
        controller.OpenClip = path =>
        {
            Tick();
            opens++;
            if (!path.StartsWith(VideoCaptures.PlaybackFolder, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(path).Length != 200L * 1024 * 1024)
                throw new InvalidOperationException("Immediate Open did not prepare the complete stored recording.");
        };
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => Tick();
        try
        {
            timer.Start();
            await controller.StartAsync(new Native.Win32.RECT(100, 100, 164, 164));
            await frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(30));
            evidence.Add($"immediate_open_dispatcher_pings={pings}; max_gap_ms={maxGap:F1}; player_launches={opens}; warnings={warnings.Count}");
            if (opens != 1 || warnings.Count != 0) throw new InvalidOperationException("Immediate Open failed: " + string.Join("; ", warnings));
            if (maxGap >= 100) throw new InvalidOperationException("Immediate Open after importing the finished recording blocked the dispatcher.");
        }
        finally
        {
            timer.Stop();
            CaptureLibrary.Changed -= Changed;
            await controller.StopAsync(true);
        }
    }

    private sealed class TestFrames : Capture.IFrameGrabber
    {
        private int _frames;
        public int Width => 64;
        public int Height => 64;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Grab(byte[] bgra)
        {
            Array.Fill(bgra, (byte)255);
            if (Interlocked.Increment(ref _frames) >= 3) Ready.TrySetResult();
            return true;
        }
        public void Dispose() { }
    }

    private static async Task CleanupWarningAsync(List<string> evidence)
    {
        VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
        VideoSettings.Format = "gif";
        RecordingStorage.Choice = null;
        using var tray = new TrayHost();
        var warnings = new List<string>();
        var frames = new TestFrames();
        var controller = new VideoController(tray, sinkFactory: path => new PaddedGifSink(path, large: false, readOnly: true),
            grabberFactory: () => frames, showWarning: (_, message) => warnings.Add(message));
        controller.AskStorage = _ => (RecordingStorage.Library, false);
        try
        {
            await controller.StartAsync(new Native.Win32.RECT(100, 100, 164, 164));
            await frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
            string path = controller.LastVideoPath ?? throw new InvalidOperationException("The actual recorder did not publish a source file.");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
                throw new InvalidOperationException("The real read-only plaintext cleanup failure was not exercised.");
            if (CaptureLibrary.Items().Count != 1 || CaptureLibrary.Items().Single().Mode != VideoCaptures.Mode)
                throw new InvalidOperationException("The recorder cleanup path lost or duplicated its committed import.");
            if (warnings.Count != 1 || !warnings[0].Contains(path, StringComparison.Ordinal)
                || !warnings[0].Contains("unencrypted", StringComparison.Ordinal))
                throw new InvalidOperationException("The recording caller did not report its retained plaintext source: " + string.Join("; ", warnings));
            evidence.Add("committed_clips=1; retained_plaintext=True; warning_count=1; warning=" + warnings[0]);
        }
        finally
        {
            await controller.StopAsync(true);
            if (controller.LastVideoPath is { } path && File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private sealed class PaddedGifSink(string path, bool large = true, bool readOnly = false) : Video.IVideoSink
    {
        private readonly Video.GifEncoder _encoder = new(path, 16, 1, false);
        public Task StartAsync(int width, int height, int fps) => _encoder.StartAsync(width, height, fps);
        public void AddFrame(byte[] bgra, TimeSpan timestamp) => _encoder.AddFrame(bgra, timestamp);
        public async Task FinishAsync(TimeSpan duration)
        {
            await _encoder.FinishAsync(duration);
            if (large)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(200L * 1024 * 1024);
                stream.Seek(-1, SeekOrigin.End);
                stream.WriteByte(77);
            }
            if (readOnly) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        public void Dispose() => _encoder.Dispose();
    }
}
