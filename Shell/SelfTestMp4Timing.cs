using System.IO;
using System.Windows;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Encode sparse synthetic frames and independently inspect the finalized MP4.</summary>
internal static class SelfTestMp4Timing
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-mp4-timing", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int exit = 1;
        try
        {
            string path = Path.Combine(folder, "sparse.mp4");
            using (var encoder = new Mp4Encoder(path, .2))
            {
                await encoder.StartAsync(320, 240, 30);
                foreach (int at in new[] { 0, 100, 200 })
                {
                    var pixels = new byte[320 * 240 * 4];
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        pixels[i] = at == 200 ? (byte)240 : (byte)20;
                        pixels[i + 2] = at == 200 ? (byte)20 : (byte)240;
                        pixels[i + 3] = 255;
                    }
                    encoder.AddFrame(pixels, TimeSpan.FromMilliseconds(at));
                }
                await encoder.FinishAsync(TimeSpan.FromMilliseconds(1250)).WaitAsync(TimeSpan.FromSeconds(20));
            }
            var probe = SelfTestMp4Probe.Read(path, TimeSpan.FromMilliseconds(1100));
            report.Add($"Requested 1250 ms, decoded duration {probe.Duration.TotalMilliseconds:F1} ms");
            if (Math.Abs(probe.Duration.TotalMilliseconds - 1250) > 110)
                throw new InvalidOperationException("The MP4 lost the final held frame duration.");
            var center = SelfTestMp4Probe.PixelAt(probe.Pixels, probe.Width, probe.Height, .5, .5);
            if (center.B < 180 || center.R > 80)
                throw new InvalidOperationException("The MP4 tail does not retain the last blue frame.");
            report.Add("Sparse recording retains its final frame through Stop: PASS");
            await CheckRecorderAsync(folder, report, paused: false);
            await CheckRecorderAsync(folder, report, paused: true);
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "mp4-timing-report.txt"), report);
        App.Instance.Shutdown(exit);
    }

    private static async Task CheckRecorderAsync(string folder, List<string> report, bool paused)
    {
        string path = Path.Combine(folder, paused ? "paused.mp4" : "continuous.mp4");
        using var encoder = new Mp4Encoder(path, .2);
        var frames = new Frames(paused);
        var recorder = new Recorder(new KillerShot.Native.Win32.RECT(0, 0, 320, 240), encoder, 30, false,
            grabberFactory: () => frames);
        await recorder.StartAsync();
        try
        {
            await frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(550);
            if (paused)
            {
                recorder.Pause();
                await Task.Delay(300);
            }
        }
        finally { await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        double expected = recorder.Elapsed.TotalMilliseconds;
        var decoded = SelfTestMp4Probe.Read(path, TimeSpan.Zero).Duration;
        report.Add($"Recorder paused={paused}: stopped {expected:F1} ms, decoded {decoded.TotalMilliseconds:F1} ms");
        if (!recorder.IsFinalized || expected < 500 || Math.Abs(decoded.TotalMilliseconds - expected) > 110)
            throw new InvalidOperationException("The finalized Recorder duration does not match its clock with pauses removed.");
        report.Add($"Recorder paused={paused}: duration PASS");
    }

    private sealed class Frames(bool sparse) : KillerShot.Capture.IFrameGrabber
    {
        private int _frames;
        public int Width => 320;
        public int Height => 240;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Grab(byte[] bgra)
        {
            int frame = Interlocked.Increment(ref _frames);
            if (sparse && frame > 3) return false;
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 240; bgra[i + 1] = 20; bgra[i + 2] = 20; bgra[i + 3] = 255;
            }
            if (frame == 3) Ready.TrySetResult();
            return true;
        }
        public void Dispose() { }
    }
}
