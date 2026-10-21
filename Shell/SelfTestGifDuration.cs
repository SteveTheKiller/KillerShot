using System.IO;
using System.Windows.Media.Imaging;
using KillerShot.Video;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestGifDuration
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-gif-duration") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            foreach (double seconds in new[] { 1.0, 655.34, 655.35, 655.36, 1310.71, 3600.0 })
            {
                string path = Path.Combine(folder, "static-" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".gif");
                using (var encoder = new GifEncoder(path, 16, 1, false))
                {
                    await encoder.StartAsync(2, 2, 30);
                    encoder.AddFrame(Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray(), TimeSpan.Zero);
                    await encoder.FinishAsync(TimeSpan.FromSeconds(seconds));
                }
                using var stream = File.OpenRead(path);
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int total = 0;
                foreach (var frame in decoder.Frames)
                {
                    var metadata = (BitmapMetadata)frame.Metadata;
                    int delay = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"));
                    if (delay < 2) throw new InvalidOperationException("GIF contains a browser-clamped short delay.");
                    total += delay;
                    var pixels = new byte[16];
                    new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 8, 0);
                    if (pixels[2] != 255 || pixels[3] != 255) throw new InvalidOperationException("Static GIF frame changed its pixels.");
                }
                report.Add($"duration_seconds={seconds} decoded_centiseconds={total} frames={decoder.Frames.Count}");
                if (Math.Abs(total / 100.0 - seconds) > 0.001) throw new InvalidOperationException("Static GIF duration was truncated.");
            }
            // Changing frames: each frame's delay must not drift away from the real clock.
            foreach (int fps in VideoSettings.GifFpsChoices)
                foreach (double seconds in new[] { 10.0, 180.0 })
                {
                    string path = Path.Combine(folder, $"changing-{fps}-{seconds}.gif");
                    int frames = (int)(seconds * fps);
                    using (var encoder = new GifEncoder(path, 16, 1, false))
                    {
                        await encoder.StartAsync(2, 2, fps);
                        for (int f = 0; f < frames; f++)
                        {
                            byte shade = (byte)(f % 2 == 0 ? 0 : 255);
                            encoder.AddFrame(Enumerable.Repeat(new byte[] { shade, shade, shade, 255 }, 4).SelectMany(pixel => pixel).ToArray(),
                                TimeSpan.FromTicks(TimeSpan.TicksPerSecond * f / fps));
                        }
                        await encoder.FinishAsync(TimeSpan.FromSeconds(seconds));
                    }
                    using var stream = File.OpenRead(path);
                    var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    int total = decoder.Frames.Sum(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay")));
                    report.Add($"changing fps={fps} seconds={seconds} decoded_centiseconds={total} frames={decoder.Frames.Count}");
                    if (Math.Abs(total - seconds * 100) > 1) throw new InvalidOperationException($"Changing GIF at {fps} fps drifted to {total / 100.0} s.");
                }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "gif-duration-report.txt"), report);
        App.Instance.Shutdown(code);
    }
}
