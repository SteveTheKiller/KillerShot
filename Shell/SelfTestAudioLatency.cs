using System.IO;
using System.Windows;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Sound must stay with the picture when a device delivers every packet a little late, as
/// microphones do: a minute of simulated recording, each 10 ms packet arriving 5 ms after it ends.</summary>
internal static class SelfTestAudioLatency
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-audio-latency") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        try
        {
            Directory.CreateDirectory(folder);
            LatePacketsStayInSync(report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "audio-latency-report.txt"), report);
        Application.Current.Shutdown(code);
        return true;
    }

    private const int Rate = AudioCapture.Rate;

    private static void LatePacketsStayInSync(List<string> report)
    {
        var device = new LateDevice();
        int worst = 0, measured = 0;
        void Output(byte[] pcm, TimeSpan at)
        {
            short first = (short)(pcm[0] | pcm[1] << 8);
            if (first == 0) return;   // silence carries no position
            long emitted = (long)Math.Round(at.TotalSeconds * Rate);
            long content = first - 1;
            long offset = ((emitted - content) % Period + Period) % Period;
            if (offset > Period / 2) offset -= Period;
            worst = Math.Max(worst, (int)Math.Abs(offset));
            measured++;
        }
        var audio = new AudioCapture([device], () => TimeSpan.Zero, () => false, Output);
        // Millisecond steps: the device hands over each 10 ms packet up to 30 ms after it ends, in
        // order, as a busy microphone driver does, and stalls for 200 ms once, ten seconds in. The
        // mixer wakes every 10 ms on its own schedule. Measured from twenty seconds on, after the
        // stall has had ample time to recover.
        int ms = Rate / 1000, packet = 10;
        var jitter = new Random(5);
        long delivered = 0, due = packet + jitter.Next(31);
        for (int t = 1; t <= 60_000; t++)
        {
            while (due <= t)
            {
                device.Deliver(delivered * ms, packet * ms);
                delivered += packet;
                due = Math.Max(due, delivered + packet + jitter.Next(31));
                if (delivered == 10_000) due = Math.Max(due, 10_200);
            }
            if (t == 20_000) worst = measured = 0;
            if (t % 10 == 0) audio.Pump(TimeSpan.FromMilliseconds(t));
        }
        double worstMs = worst * 1000.0 / Rate;
        report.Add($"chunks={measured} worst_offset_ms={worstMs:F1}");
        if (measured < 1500) throw new InvalidOperationException("Too few audio chunks were emitted to measure.");
        if (worstMs >= 50) throw new InvalidOperationException($"Sound drifted {worstMs:F0} ms from the picture with late packets.");
        report.Add("late microphone packets keep sound within 50 ms of the picture PASS");
    }

    private const int Period = 30000;

    /// <summary>Each sample carries its own position in time, so the output shows where it was placed.</summary>
    private sealed class LateDevice : IAudioCaptureSource
    {
        private readonly Queue<float> _buffer = new();
        public Exception? Error => null;
        public int Buffered => _buffer.Count / AudioCapture.Channels;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public void Deliver(long from, int frames)
        {
            for (long f = from; f < from + frames; f++)
            {
                float value = (1 + f % Period) / 32767f;
                for (int c = 0; c < AudioCapture.Channels; c++) _buffer.Enqueue(value);
            }
        }

        public void Drain(int frames, float[]? mix)
        {
            int available = Math.Min(frames, _buffer.Count / AudioCapture.Channels);
            for (int i = 0; i < available * AudioCapture.Channels; i++)
            {
                float v = _buffer.Dequeue();
                if (mix != null) mix[i] += v;
            }
        }
    }
}
