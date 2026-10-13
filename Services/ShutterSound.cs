using System.IO;
using System.Media;

namespace KillerShot.Services;

/// <summary>A short camera shutter click, synthesized at startup so no sound file ships.</summary>
internal static class ShutterSound
{
    private static byte[]? _wav;

    public static void Play()
    {
        try
        {
            _wav ??= Build();
            var player = new SoundPlayer(new MemoryStream(_wav));
            player.Play();
        }
        catch { /* no audio device: capture still works */ }
    }

    /// <summary>Two fast noise bursts with a sharp decay, the open and close of a shutter.</summary>
    private static byte[] Build()
    {
        const int rate = 44100;
        int samples = rate * 120 / 1000;
        var data = new short[samples];
        var rng = new Random(7);
        void Burst(int start, int length, double gain)
        {
            for (int i = 0; i < length && start + i < samples; i++)
            {
                double env = Math.Exp(-i / (length / 6.0));
                double v = (rng.NextDouble() * 2 - 1) * env * gain;
                data[start + i] = (short)Math.Clamp(data[start + i] + v * short.MaxValue, short.MinValue, short.MaxValue);
            }
        }
        Burst(0, rate * 25 / 1000, 0.55);
        Burst(rate * 55 / 1000, rate * 35 / 1000, 0.45);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in data) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
