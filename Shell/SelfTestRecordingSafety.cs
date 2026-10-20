using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using KillerShot.Native;
using KillerShot.Services;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Recording lifecycle checks with synthetic audio devices and a bounded encoder delay.</summary>
internal static class SelfTestRecordingSafety
{
    public static bool TryRun(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--selftest-recording-safety", StringComparison.OrdinalIgnoreCase)) return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        App.InitializeKit();
        _ = RunAsync(dir);
        return true;
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
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);
            using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero, 160, 120,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumbnail);
            if (decoder.PixelWidth != 160 || decoder.PixelHeight != 120 || clip.OriginalDuration <= TimeSpan.Zero)
                throw new InvalidOperationException("The retained recording could not be decoded.");
            report.Add($"Audio source failure retains a playable MP4: PASS ({new FileInfo(path).Length} bytes, {clip.OriginalDuration.TotalMilliseconds:F1} ms)");
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
            throw new InvalidOperationException("Exit did not await countdown cancellation and startup cleanup.");
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
