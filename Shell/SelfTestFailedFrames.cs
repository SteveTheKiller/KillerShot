using System.IO;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Injected recording boundaries only. Never captures a real screen or audio device.</summary>
internal static class SelfTestFailedFrames
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-failed-frames") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int exit = 1;
        var grabber = new Frames();
        var sink = new Sink();
        var recorder = new Recorder(new Win32.RECT(0, 0, 2, 2), sink, 30, false, grabberFactory: () => grabber);
        try
        {
            await recorder.StartAsync();
            for (int i = 0; i < 200 && grabber.Calls < 12; i++) await Task.Delay(10);
            report.Add($"calls={grabber.Calls}; distinct_buffers={grabber.DistinctBuffers}; encoded_frames={sink.Count}; failed={recorder.HasFailed}");
            if (grabber.Calls < 12 || grabber.DistinctBuffers > 2)
                throw new InvalidOperationException("Failed frame grabs lost reusable buffers.");
            for (int i = 0; i < 200 && !recorder.HasFailed; i++) await Task.Delay(10);
            if (!recorder.HasFailed) throw new InvalidOperationException("Permanent no-frame capture never reported failure.");
            bool reported = false;
            try { await recorder.StopAsync(); }
            catch (InvalidOperationException) { reported = true; }
            if (!reported || !recorder.IsFinalized || sink.Count != 1 || sink.Finished != 1)
                throw new InvalidOperationException("Capture failure lost valid content or reported a successful full recording.");
            report.Add("permanent false returns reuse buffers, report failure and finalize partial video once PASS");
            await Controls(report);
            await QueuedCaptureFailure(report);
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            if (!recorder.IsFinalized) try { await recorder.StopAsync(); } catch { }
            File.WriteAllLines(Path.Combine(folder, "failed-frames-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }

    private static async Task Controls(List<string> report)
    {
        foreach (string scenario in new[] { "transient", "exception", "full-queue" })
        {
            using var release = new ManualResetEventSlim(scenario != "full-queue");
            var frames = new Frames(call => scenario switch
            {
                "transient" => call == 1 || call >= 13,
                "exception" => call == 1 ? true : throw new IOException("Synthetic capture error"),
                _ => true
            });
            var sink = new Sink(release);
            var recorder = new Recorder(new Win32.RECT(0, 0, 2, 2), sink, 60, false, grabberFactory: () => frames);
            try
            {
                await recorder.StartAsync();
                for (int i = 0; i < 300 && !recorder.HasFailed && frames.Calls < 20; i++) await Task.Delay(5);
                if (scenario == "exception")
                {
                    bool failed = false;
                    try { await recorder.StopAsync(); }
                    catch (InvalidOperationException error) when (error.InnerException is IOException) { failed = true; }
                    if (!failed || !recorder.IsFinalized || sink.Finished != 1 || frames.DistinctBuffers > 2)
                        throw new InvalidOperationException("Capture exception did not preserve ownership and report failure.");
                }
                else
                {
                    if (recorder.HasFailed || frames.Calls < 20 || frames.DistinctBuffers > 8)
                        throw new InvalidOperationException("Transient or queued recording lost bounded frame ownership.");
                    if (scenario == "full-queue" && recorder.Dropped == 0) throw new InvalidOperationException("Full-queue fixture did not drop frames.");
                    recorder.Pause();
                    await Task.Delay(40);
                    int paused = frames.Calls;
                    await Task.Delay(60);
                    if (frames.Calls != paused) throw new InvalidOperationException("Paused recorder kept grabbing.");
                    recorder.Resume();
                    for (int i = 0; i < 100 && frames.Calls == paused; i++) await Task.Delay(5);
                    if (frames.Calls == paused) throw new InvalidOperationException("Recorder did not resume.");
                    release.Set();
                    await recorder.StopAsync();
                    if (!recorder.IsFinalized || sink.Finished != 1 || sink.Count < 2)
                        throw new InvalidOperationException("Recovered or queued video was not finalized.");
                }
                report.Add(scenario + " bounded buffers, outcome, pause/resume and stop PASS");
            }
            finally
            {
                release.Set();
                if (!recorder.IsFinalized) try { await recorder.StopAsync(); } catch { }
            }
        }
    }

    private static async Task QueuedCaptureFailure(List<string> report)
    {
        using var release = new ManualResetEventSlim(false);
        var frames = new Frames(call => call <= 6 ? true : throw new IOException("Synthetic capture error with queued frames"));
        var sink = new Sink(release);
        var recorder = new Recorder(new Win32.RECT(0, 0, 2, 2), sink, 60, false, grabberFactory: () => frames);
        try
        {
            await recorder.StartAsync();
            for (int i = 0; i < 400 && !recorder.HasFailed; i++) await Task.Delay(5);
            if (!recorder.HasFailed || recorder.Frames != 6)
                throw new InvalidOperationException("Queued capture-failure fixture did not reach its required schedule.");
            release.Set();
            bool reported = false;
            try { await recorder.StopAsync(); }
            catch (InvalidOperationException error) when (error.InnerException is IOException) { reported = true; }
            if (!reported || !recorder.IsFinalized || sink.Finished != 1 || sink.Count != recorder.Frames)
                throw new InvalidOperationException($"Capture failure discarded queued valid frames: captured={recorder.Frames}, encoded={sink.Count}.");
            report.Add("capture exception drains all queued valid frames and reports partial failure PASS");
        }
        finally
        {
            release.Set();
            if (!recorder.IsFinalized) try { await recorder.StopAsync(); } catch { }
        }
    }

    private sealed class Frames(Func<int, bool>? grab = null) : IFrameGrabber
    {
        private int _calls;
        private readonly HashSet<byte[]> _buffers = [];
        public int Width => 2;
        public int Height => 2;
        public int Calls => Volatile.Read(ref _calls);
        public int DistinctBuffers { get { lock (_buffers) return _buffers.Count; } }
        public bool Grab(byte[] buffer)
        {
            lock (_buffers) _buffers.Add(buffer);
            int call = Interlocked.Increment(ref _calls);
            return grab?.Invoke(call) ?? call == 1;
        }
        public void Dispose() { }
    }

    private sealed class Sink(ManualResetEventSlim? release = null) : IVideoSink
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public int Finished { get; private set; }
        public Task StartAsync(int width, int height, int fps) => Task.CompletedTask;
        public void AddFrame(byte[] frame, TimeSpan timestamp)
        {
            if (release != null && !release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic sink was not released.");
            Interlocked.Increment(ref _count);
        }
        public Task FinishAsync(TimeSpan duration) { Finished++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
