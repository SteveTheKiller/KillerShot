using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using KillerShot.Native;
using KillerShot.Services;
using KillerShot.Video;
using KillerShot.Capture;

namespace KillerShot.Shell;

/// <summary>Recording lifecycle checks with synthetic audio devices and a bounded encoder delay.</summary>
internal static class SelfTestRecordingSafety
{
    public static bool TryRun(string[] args)
    {
        if (SelfTestMp4Timing.TryRun(args)) return true;
        if (args.Length < 2 || args[0] is not ("--selftest-recording-safety" or "--selftest-recording-fault" or "--selftest-recording-restore" or "--selftest-audio-downmix" or "--selftest-recording-commands" or "--selftest-audio-fault" or "--selftest-recording-countdown" or "--selftest-mp4-fault")) return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        App.InitializeKit();
        _ = args[0] switch
        {
            "--selftest-recording-fault" => RunFaultAsync(dir),
            "--selftest-recording-restore" => RunRestoreAsync(dir),
            "--selftest-audio-downmix" => RunDownmixAsync(dir),
            "--selftest-recording-commands" => RunCommandsAsync(dir),
            "--selftest-audio-fault" => RunAudioFaultAsync(dir),
            "--selftest-recording-countdown" => RunCountdownAsync(dir),
            "--selftest-mp4-fault" => RunMp4FaultAsync(dir),
            _ => RunAsync(dir)
        };
        return true;
    }

    private static async Task RunMp4FaultAsync(string dir)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            VideoSettings.Format = "mp4";
            VideoSettings.Sound = SoundSource.None;
            VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
            foreach (bool paused in new[] { false, true })
            {
                VideoSettings.OutputFolder = Path.Combine(dir, paused ? "paused" : "active");
                var originalWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
                using var tray = new TrayHost();
                var transcode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var frames = new SyntheticVideoFrames();
                var warnings = new List<string>();
                int restores = 0;
                Mp4Encoder? sink = null;
                var controller = new VideoController(tray, sinkFactory: path => sink = new Mp4Encoder(path, .1, transcodeFactory: () => transcode.Task),
                    grabberFactory: () => frames, showWarning: (_, message) => warnings.Add(message));
                try
                {
                    await controller.StartAsync(new Win32.RECT(100, 100, 164, 164), () => restores++);
                    await frames.ThreeFrames.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    if (paused) controller.TogglePause();
                    var error = new IOException("Injected asynchronous MP4 failure.");
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    transcode.SetException(error);
                    while (controller.IsRecording && elapsed.ElapsedMilliseconds < 1500) await Task.Delay(10);
                    report.Add($"MP4 async fault paused={paused}: active={controller.IsRecording}, elapsed={elapsed.ElapsedMilliseconds} ms, warnings={warnings.Count}, disposals={frames.Disposals}");
                    if (controller.IsRecording) throw new InvalidOperationException("The MP4 transcoder fault stayed hidden until manual Stop.");
                    if (!warnings.SequenceEqual(new[] { error.Message }) || frames.Disposals != 1 || restores != 1 || controller.LastVideoPath != null)
                        throw new InvalidOperationException("The MP4 fault did not preserve its error, release capture, or restore the editor exactly once.");
                    if (Application.Current.Windows.Cast<Window>().Except(originalWindows).Any() || Directory.EnumerateFiles(VideoSettings.OutputFolder, "*", SearchOption.AllDirectories).Any())
                        throw new InvalidOperationException("MP4 failure left capture chrome or a published output.");
                    await controller.StopAsync(false);
                    if (warnings.Count != 1 || restores != 1) throw new InvalidOperationException("Repeated Stop repeated fault reporting or editor restoration.");
                }
                finally
                {
                    transcode.TrySetResult();
                    sink?.Dispose();
                    await controller.StopAsync(true);
                }
            }
            report.Add("Asynchronous MP4 failure stops active and paused recording without manual Stop: PASS");
            await Mp4QueueFaultAsync(dir, report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(dir, "mp4-fault-report.txt"), report);
        App.Instance.Shutdown(code);
    }

    private static async Task Mp4QueueFaultAsync(string dir, List<string> report)
    {
        var transcode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sink = new Mp4Encoder(Path.Combine(dir, "queue.mp4"), .1, 128000, () => transcode.Task);
        await sink.StartAsync(2, 2, 30);
        var frame = new byte[16];
        for (int i = 0; i < 9; i++) sink.AddFrame(frame, TimeSpan.FromMilliseconds(i * 34));
        if (sink.Dropped != 1 || sink.Error != null) throw new InvalidOperationException("Ordinary queue saturation was mistaken for an encoder fault.");
        for (int i = 0; i < 400; i++) sink.AddAudio(new byte[4], TimeSpan.FromMilliseconds(i));
        var enteredVideo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?> video = Task.Run(() => Write(() => sink.AddFrame(frame, TimeSpan.FromSeconds(1)), enteredVideo));
        Task<Exception?> audio = Task.Run(() => Write(() => sink.AddAudio(new byte[4], TimeSpan.FromSeconds(1)), enteredAudio));
        try
        {
            await Task.WhenAll(enteredVideo.Task, enteredAudio.Task).WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            if (video.IsCompleted || audio.IsCompleted) throw new InvalidOperationException("The fault fixture did not fill both input queues.");
            var error = new IOException("Injected fault while both MP4 input queues are full.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            transcode.SetException(error);
            var failures = await Task.WhenAll(video, audio).WaitAsync(TimeSpan.FromMilliseconds(750));
            if (failures.Any(failure => !ReferenceEquals(failure, error)) || !ReferenceEquals(sink.Error, error) || sink.Dropped != 1)
                throw new InvalidOperationException("A failed MP4 producer lost the original error or counted failure as a dropped frame.");
            report.Add($"Full MP4 video/audio queues release both writers with the original failure in {elapsed.ElapsedMilliseconds} ms; ordinary saturation drops one frame: PASS");
        }
        finally
        {
            transcode.TrySetResult();
            sink.Dispose();
            await Task.WhenAll(video, audio);
        }

        static Exception? Write(Action add, TaskCompletionSource entered)
        {
            entered.TrySetResult();
            try { add(); return null; }
            catch (Exception error) { return error; }
        }
    }

    private sealed class SyntheticVideoFrames : IFrameGrabber
    {
        private int _grabs, _disposals;
        public int Width => 64;
        public int Height => 64;
        public int Disposals => Volatile.Read(ref _disposals);
        public TaskCompletionSource ThreeFrames { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Grab(byte[] bgra)
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            Array.Fill(bgra, (byte)255);
            if (Interlocked.Increment(ref _grabs) == 3) ThreeFrames.TrySetResult();
            return true;
        }
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private static async Task RunCountdownAsync(string dir)
    {
        int code = 1;
        var report = new List<string>();
        var originalWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
        using var tray = new TrayHost();
        int attempts = 0, restores = 0;
        var controller = new VideoController(tray, sinkFactory: _ =>
        {
            attempts++;
            throw new IOException("The countdown test refuses any recording startup.");
        });
        try
        {
            VideoSettings.OutputFolder = Path.Combine(dir, "videos");
            VideoSettings.Format = "gif";
            VideoSettings.Countdown = true;
            VideoSettings.CopyFile = VideoSettings.Notify = false;
            foreach (int seconds in new[] { 3, 2, 1 })
            {
                Task startup = controller.StartAsync(new Win32.RECT(100, 100, 164, 164), () => restores++);
                string text = string.Format(LocaleManager.Loc("Str_Rec_Starting"), seconds);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!Chrome().SelectMany(Visuals).OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == text))
                {
                    if (wait.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Countdown value did not appear: " + seconds);
                    await Task.Delay(10);
                }
                var stop = Chrome().SelectMany(Visuals).OfType<System.Windows.Controls.Button>()
                    .Single(b => Equals(b.Content, LocaleManager.Loc("Str_Rec_Stop")));
                stop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await startup.WaitAsync(TimeSpan.FromSeconds(2));
                bool files = Directory.Exists(VideoSettings.OutputFolder) && Directory.EnumerateFiles(VideoSettings.OutputFolder, "*", SearchOption.AllDirectories).Any();
                report.Add($"Stop at {seconds}: encoder attempts={attempts}, active={controller.IsRecording}, restores={restores}, windows={Chrome().Count()}, output files={files}");
                if (attempts != 0 || controller.IsRecording || restores != 4 - seconds || Chrome().Any() || files || controller.LastVideoPath != null)
                    throw new InvalidOperationException("Countdown Stop attempted recording instead of canceling startup.");
            }
            Task skipped = controller.StartAsync(new Win32.RECT(100, 100, 164, 164), () => restores++);
            controller.Toggle(fromShortcut: true);
            await skipped.WaitAsync(TimeSpan.FromSeconds(2));
            if (attempts != 1 || restores != 4 || controller.IsRecording || Chrome().Any())
                throw new InvalidOperationException("The explicit recording shortcut no longer skips the countdown.");
            report.Add("Stop cancels at every countdown value; the shortcut still attempts immediate startup: PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            await controller.StopAsync(true);
            File.WriteAllLines(Path.Combine(dir, "recording-countdown-report.txt"), report);
            App.Instance.Shutdown(code);
        }

        IEnumerable<Window> Chrome() => Application.Current.Windows.Cast<Window>().Except(originalWindows);
    }

    private static async Task RunCommandsAsync(string dir)
    {
        int code = 1;
        var report = new List<string>();
        using var tray = new TrayHost();
        var controller = new VideoController(tray);
        try
        {
            VideoSettings.OutputFolder = Path.Combine(dir, "videos");
            VideoSettings.Format = "gif";
            VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
            await controller.StartAsync(new Win32.RECT(0, 0, 64, 64));
            InvokeTray("Str_Rec_Pause");
            await Task.Delay(100);
            InvokeTray("Str_Rec_Resume");
            await Task.Delay(100);
            InvokeTray("Str_Rec_Cancel");
            for (int i = 0; i < 100 && controller.IsRecording; i++) await Task.Delay(20);
            if (controller.IsRecording || controller.LastVideoPath != null)
                throw new InvalidOperationException("Keyboard-reachable Discard did not discard recording.");
            report.Add("Keyboard notification menu exposes Pause, Resume and Discard throughout a recording: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            await controller.StopAsync(true);
            File.WriteAllLines(Path.Combine(dir, "recording-commands-report.txt"), report);
            App.Current.Shutdown(code);
        }

        void InvokeTray(string key)
        {
            SendTrayMessage(tray.Handle, 0x8001, IntPtr.Zero, (IntPtr)0x7B);
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(s => s.RootVisual != null)
                .SelectMany(s => Visuals(s.RootVisual)).OfType<System.Windows.Controls.ContextMenu>().Single(m => m.IsOpen);
            try
            {
                var item = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                    .SingleOrDefault(i => Equals(i.Header, LocaleManager.Loc(key)));
                if (item == null) throw new InvalidOperationException("Recording keyboard menu is missing " + key);
                if (string.IsNullOrEmpty(item.InputGestureText)) throw new InvalidOperationException("Recording action has no discoverable shortcut.");
                item.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            }
            finally { menu.IsOpen = false; }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendTrayMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static Task RunDownmixAsync(string dir)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            Check([0, 0, 1, 0, 0, 0], 0x3F, .70710678f, .70710678f, "5.1 center");
            Check([0, 0, 0, 0, 1, 0], 0x3F, .70710678f, 0, "5.1 rear left");
            Check([0, 0, 0, 0, 0, 1], 0x3F, 0, .70710678f, "5.1 rear right");
            Check([0, 0, 0, 0, 0, 0, 1, 0], 0x63F, .70710678f, 0, "7.1 side left");
            Check([0, 0, 0, 0, 0, 0, 0, 1], 0x63F, 0, .70710678f, "7.1 side right");
            Check([.5f], 0, .5f, .5f, "mono");
            Check([.25f, -.5f], 3, .25f, -.5f, "stereo");
            Check([0, 0, 0, 1, 0, 0], 0x3F, .5f, .5f, "LFE");
            Check([1, 1, 1, 1, 1, 1], 0x3F, 1, 1, "clipping control");
            report.Add("Mono, stereo, 5.1 center/rear/LFE, 7.1 sides and clipping retain expected stereo samples: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(dir, "audio-downmix-report.txt"), report);
        App.Current.Shutdown(code);
        return Task.CompletedTask;

        static void Check(float[] samples, uint mask, float left, float right, string name)
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(samples, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var actual = AudioDownmix.Frame(handle.AddrOfPinnedObject(), 0, samples.Length, 32, true, mask);
                if (Math.Abs(actual.Left - left) > .00001 || Math.Abs(actual.Right - right) > .00001)
                    throw new InvalidOperationException($"{name}: expected {left},{right}; got {actual.Left},{actual.Right}.");
            }
            finally { handle.Free(); }
        }
    }

    private static async Task RunRestoreAsync(string dir)
    {
        int code = 1;
        var report = new List<string>();
        using var tray = new TrayHost();
        var controller = new VideoController(tray);
        try
        {
            VideoSettings.OutputFolder = Path.Combine(dir, "videos");
            VideoSettings.Format = "gif";
            VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
            CaptureSettings.DelaySeconds = 0;
            int restored = 0;
            var capture = new CaptureController(tray, controller)
            {
                HideEditor = () => Task.FromResult<Action?>(() => restored++)
            };
            Task picking = capture.StartVideo(false);
            for (int i = 0; i < 100 && !Application.Current.Windows.OfType<CaptureBar>().Any(); i++) await Task.Delay(20);
            var bar = Application.Current.Windows.OfType<CaptureBar>().Single();
            var combo = Visuals(bar).OfType<System.Windows.Controls.ComboBox>().Single();
            combo.SelectedItem = combo.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .Single(i => Equals(i.Tag, KillerShot.Capture.CaptureMode.Monitor));
            var button = Visuals(bar).OfType<System.Windows.Controls.Button>()
                .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Btn_Capture"));
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await picking.WaitAsync(TimeSpan.FromSeconds(5));
            if (!controller.IsRecording || restored != 0)
                throw new InvalidOperationException("The editor restore callback ran while its subject was still recording.");
            await Task.Delay(150);
            await controller.StopAsync(false);
            await controller.StopAsync(false);
            if (restored != 1 || controller.IsRecording)
                throw new InvalidOperationException("Recording completion did not restore exactly once.");
            report.Add("Capture picker transfers editor restoration until recording completion, repeated Stop restores once: PASS");
            await controller.StartAsync(new Win32.RECT(0, 0, 64, 64), () => restored++);
            await Task.Delay(100);
            await controller.StopAsync(true);
            if (restored != 2) throw new InvalidOperationException("Discard did not restore once.");
            VideoSettings.Countdown = true;
            Task countdown = controller.StartAsync(new Win32.RECT(0, 0, 64, 64), () => restored++);
            await controller.StopAsync(true);
            await countdown;
            if (restored != 3) throw new InvalidOperationException("Countdown cancellation did not restore once.");
            VideoSettings.Countdown = false;
            var failed = new VideoController(tray, sinkFactory: _ => throw new IOException("Injected startup failure."));
            await failed.StartAsync(new Win32.RECT(0, 0, 64, 64), () => restored++);
            if (restored != 4 || failed.IsRecording) throw new InvalidOperationException("Startup failure did not restore once.");
            report.Add("Discard, canceled countdown and startup failure each restore exactly once: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            foreach (var bar in Application.Current.Windows.OfType<CaptureBar>().ToArray()) bar.Close();
            await controller.StopAsync(true);
            File.WriteAllLines(Path.Combine(dir, "recording-restore-report.txt"), report);
            App.Current.Shutdown(code);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return item;
    }

    private static async Task RunFaultAsync(string dir)
    {
        int code = 1;
        var report = new List<string>();
        using var tray = new TrayHost();
        FaultSink? sink = null;
        VideoSettings.OutputFolder = Path.Combine(dir, "videos");
        VideoSettings.Format = "gif";
        VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
        var controller = new VideoController(tray, sinkFactory: path => sink = new FaultSink(path));
        try
        {
            await controller.StartAsync(new Win32.RECT(0, 0, 64, 64));
            if (sink == null) throw new InvalidOperationException("Recording did not start through the sink boundary.");
            await sink.Failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int i = 0; i < 100 && controller.IsRecording; i++) await Task.Delay(20);
            if (controller.IsRecording) throw new InvalidOperationException("Controller still appears active after a terminal encoder I/O failure.");
            if (controller.LastVideoPath is not { } path || !File.Exists(path))
                throw new InvalidOperationException("The valid recording prefix was discarded.");
            using var stream = File.OpenRead(path);
            var decoded = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoded.Frames.Count < 1 || sink.Finishes != 1)
                throw new InvalidOperationException("Fault recovery did not finalize a playable prefix exactly once.");
            await Task.WhenAll(controller.StopAsync(false), controller.PrepareForExitAsync());
            if (sink.Finishes != 1) throw new InvalidOperationException("Repeated stop finalized the sink again.");
            report.Add("Started recording reaches injected encoder I/O fault, leaves active state, retains decodable GIF, and finalizes once: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            await controller.StopAsync(true);
            File.WriteAllLines(Path.Combine(dir, "recording-fault-report.txt"), report);
            App.Current.Shutdown(code);
        }
    }

    private sealed class FaultSink(string path) : IVideoSink
    {
        private readonly GifEncoder _inner = new(path, 16, 1, false);
        private int _frames;
        public int Finishes { get; private set; }
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(int width, int height, int fps) => _inner.StartAsync(width, height, fps);
        public void AddFrame(byte[] bgra, TimeSpan timestamp)
        {
            if (++_frames == 3)
            {
                Failed.TrySetResult();
                throw new IOException("Injected recording sink write failure.");
            }
            _inner.AddFrame(bgra, timestamp);
        }
        public void AddAudio(byte[] pcm, TimeSpan timestamp) { }
        public Task FinishAsync(TimeSpan duration) { Finishes++; return _inner.FinishAsync(duration); }
        public void Dispose() => _inner.Dispose();
    }

    private static async Task RunAsync(string dir)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            await StopCutsOffAudioAsync(report);
            await StopCutsOffAudioAsync(report, paused: true);
            await AudioFailureKeepsVideoAsync(dir, report);
            await DiscardStillRemovesVideoAsync(dir, report);
            await ConcurrentStopWaitsAsync(dir, report);
            await ExitDuringCountdownAsync(dir, report);
            await FailedFinalizationStaysFailedAsync(report);
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(dir, "recording-safety-report.txt"), report);
        Application.Current.Shutdown(code);
    }

    /// <summary>An audio device that fails mid-recording marks the recording failed within a
    /// status tick, instead of only at Stop, and the video recorded so far is still finalized.</summary>
    private static async Task RunAudioFaultAsync(string dir)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            using var sink = new DelayedEncoder();
            sink.Release.Set();
            var audio = new TestAudioDevice();
            var recorder = new Recorder(new Win32.RECT(0, 0, 64, 64), sink, 30, false, audioSources: [audio]);
            await recorder.StartAsync();
            await sink.FirstAudio.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (recorder.HasFailed) throw new InvalidOperationException("The recording failed before the audio fault.");
            audio.Fail();
            var seen = System.Diagnostics.Stopwatch.StartNew();
            while (!recorder.HasFailed && seen.ElapsedMilliseconds < 2000) await Task.Delay(10);
            report.Add($"Audio fault visible after {seen.ElapsedMilliseconds} ms; failed={recorder.HasFailed}.");
            if (!recorder.HasFailed || seen.ElapsedMilliseconds > 250)
                throw new InvalidOperationException("An audio device failure stayed hidden while recording continued.");
            Exception? failure = null;
            try { await recorder.StopAsync(); }
            catch (Exception ex) { failure = ex; }
            if (failure == null || !recorder.IsFinalized)
                throw new InvalidOperationException("The audio fault was not reported, or the recorded prefix was not finalized.");
            report.Add("Audio fault stops promptly and keeps the recorded part: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(dir, "audio-fault-report.txt"), report);
        Application.Current.Shutdown(code);
    }

    private static async Task StopCutsOffAudioAsync(List<string> report, bool paused = false)
    {
        using var sink = new DelayedEncoder();
        var audio = new TestAudioDevice();
        var recorder = new Recorder(new Win32.RECT(0, 0, 64, 64), sink, 30, false, audioSources: [audio]);
        await recorder.StartAsync();
        Task? stop = null;
        try
        {
            await sink.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await sink.FirstAudio.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (paused)
            {
                recorder.Pause();
                await Task.Delay(50);
            }
            stop = recorder.StopAsync();
            TimeSpan cutoff = recorder.Elapsed;
            await Task.Delay(350);
            bool stoppedWhileDraining = audio.Stopped;
            TimeSpan drainingElapsed = recorder.Elapsed;
            sink.Release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            TimeSpan audioEnd = sink.AudioEnds.DefaultIfEmpty().Max();
            report.Add($"Stop cutoff {cutoff.TotalMilliseconds:F1} ms; last audio {audioEnd.TotalMilliseconds:F1} ms; draining clock {drainingElapsed.TotalMilliseconds:F1} ms; paused {paused}.");
            if (audioEnd > cutoff + TimeSpan.FromMilliseconds(1))
                throw new InvalidOperationException("Audio continued past Stop while the encoder drained.");
            if (!stoppedWhileDraining)
                throw new InvalidOperationException("The audio device stayed active while the encoder drained.");
            if (drainingElapsed != cutoff)
                throw new InvalidOperationException("The recording clock continued after Stop.");
            report.Add("Stop freezes recording time and stops the audio device before encoder drain: PASS");
        }
        finally
        {
            sink.Release.Set();
            if (stop == null) await recorder.StopAsync();
            else await stop;
        }
    }

    private static async Task AudioFailureKeepsVideoAsync(string dir, List<string> report)
    {
        VideoSettings.OutputFolder = Path.Combine(dir, "videos");
        VideoSettings.FileNamePattern = "audio-failure";
        VideoSettings.Format = "mp4";
        VideoSettings.Sound = SoundSource.Microphone;
        VideoSettings.Countdown = false;
        VideoSettings.Notify = false;
        VideoSettings.CopyFile = false;
        using var tray = new TrayHost();
        var audio = new TestAudioDevice { FailOnStop = true };
        var controller = new VideoController(tray, [audio]);
        try
        {
            await controller.StartAsync(new Win32.RECT(0, 0, 320, 240));
            if (!controller.IsRecording) throw new InvalidOperationException("The test recording did not start.");
            await Task.Delay(350);
            await controller.StopAsync(discard: false);
            if (controller.LastVideoPath is not { } path || !File.Exists(path))
                throw new InvalidOperationException("An audio source error discarded the finalized recording.");
            var probe = SelfTestMp4Probe.Read(path, TimeSpan.Zero);
            if (probe.Width != 320 || probe.Height != 240 || probe.Duration <= TimeSpan.Zero)
                throw new InvalidOperationException("The retained recording could not be decoded.");
            report.Add($"Audio source failure retains a playable MP4: PASS ({new FileInfo(path).Length} bytes, {probe.Duration.TotalMilliseconds:F1} ms)");
        }
        finally
        {
            if (controller.IsRecording) await controller.StopAsync(discard: true);
        }
    }

    private static async Task DiscardStillRemovesVideoAsync(string dir, List<string> report)
    {
        VideoSettings.OutputFolder = Path.Combine(dir, "discarded");
        using var tray = new TrayHost();
        var controller = new VideoController(tray, [new TestAudioDevice { FailOnStop = true }]);
        await controller.StartAsync(new Win32.RECT(0, 0, 320, 240));
        if (!controller.IsRecording) throw new InvalidOperationException("The discard test recording did not start.");
        await Task.Delay(350);
        await controller.StopAsync(discard: true);
        if (controller.LastVideoPath != null || Directory.EnumerateFiles(VideoSettings.OutputFolder, "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Explicitly discarded video was retained after an audio error.");
        report.Add("Explicit discard still removes finalized video after an audio error: PASS");
    }

    private static async Task ConcurrentStopWaitsAsync(string dir, List<string> report)
    {
        VideoSettings.OutputFolder = Path.Combine(dir, "concurrent-stop");
        using var tray = new TrayHost();
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new VideoController(tray, [new TestAudioDevice { StopGate = gate, StopEntered = entered }]);
        await controller.StartAsync(new Win32.RECT(0, 0, 320, 240));
        await Task.Delay(150);
        Task first = controller.StopAsync(false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task second = controller.StopAsync(false);
            Task<bool> exit = controller.PrepareForExitAsync();
            if (second.IsCompleted || exit.IsCompleted)
                throw new InvalidOperationException("A second Stop returned before finalization and publication completed.");
            gate.Set();
            await Task.WhenAll(first, second, exit).WaitAsync(TimeSpan.FromSeconds(10));
            if (!await exit) throw new InvalidOperationException("Successful publication blocked exit.");
            if (controller.IsRecording || controller.LastVideoPath == null || !File.Exists(controller.LastVideoPath))
                throw new InvalidOperationException("Stop completed without publishing the finalized video.");
            report.Add("Concurrent Stop waits for finalization and publication: PASS");
        }
        finally { gate.Set(); await first; }
    }

    private static async Task ExitDuringCountdownAsync(string dir, List<string> report)
    {
        VideoSettings.OutputFolder = Path.Combine(dir, "countdown-exit");
        VideoSettings.Countdown = true;
        using var tray = new TrayHost();
        var controller = new VideoController(tray);
        Task startup = controller.StartAsync(new Win32.RECT(0, 0, 320, 240));
        bool ready = await controller.PrepareForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (!ready || !startup.IsCompleted || controller.IsRecording || controller.LastVideoPath != null)
            throw new InvalidOperationException($"Exit did not await countdown cancellation and startup cleanup: ready={ready}, startup={startup.Status}, recording={controller.IsRecording}, path={controller.LastVideoPath}.");
        await startup;
        VideoSettings.Countdown = false;
        report.Add("Exit during countdown waits for startup cleanup: PASS");
    }

    private static async Task FailedFinalizationStaysFailedAsync(List<string> report)
    {
        using var sink = new DelayedEncoder { FailFinalization = true };
        sink.Release.Set();
        var recorder = new Recorder(new Win32.RECT(0, 0, 64, 64), sink, 30, false);
        await recorder.StartAsync();
        await sink.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Exception? failure = null;
        try { await recorder.StopAsync(); }
        catch (Exception ex) { failure = ex; }
        if (failure == null || recorder.IsFinalized)
            throw new InvalidOperationException("A failed encoder finalization was treated as a completed recording.");
        report.Add("Encoder finalization failure is not marked completed: PASS");
    }

    private sealed class TestAudioDevice : IAudioCaptureSource
    {
        public bool Stopped => Volatile.Read(ref _stopped);
        private bool _stopped;
        public bool FailOnStop { get; init; }
        public ManualResetEventSlim? StopGate { get; init; }
        public TaskCompletionSource? StopEntered { get; init; }
        public Exception? Error { get; private set; }
        public void Start() { }
        public void Stop()
        {
            StopEntered?.TrySetResult();
            StopGate?.Wait(TimeSpan.FromSeconds(15));
            Volatile.Write(ref _stopped, true);
            if (FailOnStop) Error ??= new IOException("The synthetic audio device disconnected.");
        }
        public void Drain(int frames, float[]? mix) { }
        public void Fail() => Error ??= new IOException("The synthetic audio device was unplugged.");
        public void Dispose() => Stop();
    }

    private sealed class DelayedEncoder : IVideoSink
    {
        public readonly TaskCompletionSource FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ConcurrentQueue<TimeSpan> AudioEnds = new();
        public bool FailFinalization { get; init; }
        public Task StartAsync(int width, int height, int fps) => Task.CompletedTask;
        public void AddFrame(byte[] bgra, TimeSpan timestamp)
        {
            FirstFrame.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The test encoder was not released.");
        }
        public void AddAudio(byte[] pcm, TimeSpan timestamp)
        {
            AudioEnds.Enqueue(timestamp + TimeSpan.FromSeconds((double)pcm.Length / (AudioCapture.Rate * AudioCapture.Channels * 2)));
            FirstAudio.TrySetResult();
        }
        public Task FinishAsync(TimeSpan duration) => FailFinalization
            ? Task.FromException(new IOException("The synthetic encoder could not finalize its output."))
            : Task.CompletedTask;
        public void Dispose() { }
    }
}
