using System.Runtime.InteropServices;
using System.Numerics;

namespace KillerShot.Video;

internal static class AudioDownmix
{
    public static (float Left, float Right) Frame(IntPtr data, int frame, int channels, int bits, bool isFloat, uint channelMask)
    {
        if (channels == 1)
        {
            float mono = Sample(data, frame, bits, isFloat);
            return (mono, mono);
        }
        if (channelMask == 0 && channels == 2) channelMask = 3;
        float left = 0, right = 0;
        int channel = 0;
        // WAVEFORMATEXTENSIBLE orders channels by ascending speaker-mask bits.
        // Front L/R retain unity gain; center and surrounds use -3 dB, LFE/back center -6 dB.
        // Limit the summed output to full scale so correlated channels cannot overflow PCM.
        for (uint speaker = 1; speaker <= 0x20000; speaker <<= 1)
        {
            if ((channelMask & speaker) == 0) continue;
            float sample = Sample(data, frame * channels + channel++, bits, isFloat);
            if (!float.IsFinite(sample)) sample = 0;
            switch (speaker)
            {
                case 1: left += sample; break;
                case 2: right += sample; break;
                case 0x10 or 0x40 or 0x200 or 0x1000 or 0x8000: left += sample * .70710678f; break;
                case 0x20 or 0x80 or 0x400 or 0x4000 or 0x20000: right += sample * .70710678f; break;
                case 4: left += sample * .70710678f; right += sample * .70710678f; break;
                default: left += sample * .5f; right += sample * .5f; break;
            }
        }
        return (Math.Clamp(left, -1, 1), Math.Clamp(right, -1, 1));
    }

    public static void Validate(int channels, int bits, bool isFloat, uint mask)
    {
        if (channels < 1 || (isFloat ? bits != 32 : bits is not (16 or 24 or 32)))
            throw new NotSupportedException("Unsupported audio sample format.");
        if (mask == 0 && channels <= 2) return;
        if ((mask & ~0x3FFFFu) != 0 || BitOperations.PopCount(mask) != channels)
            throw new NotSupportedException("The audio device does not specify a supported speaker layout.");
    }

    private static float Sample(IntPtr data, int index, int bits, bool isFloat)
    {
        if (isFloat && bits == 32) return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, index * 4));
        return bits switch
        {
            16 => Marshal.ReadInt16(data, index * 2) / 32768f,
            24 => (Marshal.ReadByte(data, index * 3) | (Marshal.ReadByte(data, index * 3 + 1) << 8)
                | ((sbyte)Marshal.ReadByte(data, index * 3 + 2) << 16)) / 8388608f,
            32 => Marshal.ReadInt32(data, index * 4) / 2147483648f,
            _ => throw new NotSupportedException("Unsupported audio sample format."),
        };
    }
}
