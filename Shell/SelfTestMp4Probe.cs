using System.Runtime.InteropServices;

namespace KillerShot.Shell;

/// <summary>
/// Reads back a recorded MP4 for the self-tests through the Media Foundation source reader:
/// its duration, how many audio tracks it has, and the decoded picture at a time.
///
/// Slots from mfreadwrite.h and mfobjects.h (SDK 10.0.26100.0). IMFSourceReader:
/// SetStreamSelection = 4, GetNativeMediaType = 5, GetCurrentMediaType = 6,
/// SetCurrentMediaType = 7, SetCurrentPosition = 8, ReadSample = 9,
/// GetPresentationAttribute = 12. IMFAttributes: GetUINT32 = 7, GetUINT64 = 8, SetUINT32 = 21,
/// SetGUID = 24. IMFMediaType: GetMajorType = 33. IMFSample: ConvertToContiguousBuffer = 41.
/// IMFMediaBuffer: Lock = 3, Unlock = 4, GetCurrentLength = 5.
/// </summary>
internal static unsafe class SelfTestMp4Probe
{
    private static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private const uint FirstVideoStream = 0xfffffffc, MediaSource = 0xffffffff, EndOfStream = 0x2;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr mediaType);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(PropVariant* value);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    /// <summary>Duration, audio track count, and the frame nearest <paramref name="at"/> as
    /// top-down BGRA with its size.</summary>
    public static (TimeSpan Duration, int AudioTracks, byte[] Pixels, int Width, int Height) Read(string path, TimeSpan at)
    {
        Check(MFStartup(0x00020070, 0));
        IntPtr attributes = IntPtr.Zero, reader = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attributes, 1));
            var key = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, 1));
            Check(MFCreateSourceReaderFromURL(path, attributes, out reader));

            var durationKey = MF_PD_DURATION;
            PropVariant duration;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, PropVariant*, int>)Slot(reader, 12))(reader, MediaSource, &durationKey, &duration));
            var length = TimeSpan.FromTicks(duration.Value);
            PropVariantClear(&duration);

            int audio = 0;
            for (uint stream = 0; ; stream++)
            {
                IntPtr native;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Slot(reader, 5))(reader, stream, 0, &native) < 0) break;
                Guid major;
                ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)Slot(native, 33))(native, &major);
                if (major == MFMediaType_Audio) audio++;
                Marshal.Release(native);
            }

            // Only the video stream, decoded to RGB32.
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, 0xfffffffe, 0);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, FirstVideoStream, 1));
            Check(MFCreateMediaType(out IntPtr output));
            try
            {
                var majorKey = MF_MT_MAJOR_TYPE; var video = MFMediaType_Video;
                var subKey = MF_MT_SUBTYPE; var rgb = MFVideoFormat_RGB32;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(output, 24))(output, &majorKey, &video));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(output, 24))(output, &subKey, &rgb));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(reader, 7))(reader, FirstVideoStream, IntPtr.Zero, output));
            }
            finally { Marshal.Release(output); }

            IntPtr current;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(reader, 6))(reader, FirstVideoStream, &current));
            int width, height, stride;
            try
            {
                var sizeKey = MF_MT_FRAME_SIZE; ulong size;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(current, 8))(current, &sizeKey, &size));
                width = (int)(size >> 32); height = (int)(size & 0xffffffff);
                var strideKey = MF_MT_DEFAULT_STRIDE; uint rawStride;
                stride = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(current, 7))(current, &strideKey, &rawStride) >= 0
                    ? unchecked((int)rawStride) : width * 4;
            }
            finally { Marshal.Release(current); }

            // Decode forward from the start and keep the last frame that begins at or before the time.
            byte[]? last = null;
            while (true)
            {
                uint actual, flags; long timestamp; IntPtr sample;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(
                    reader, FirstVideoStream, 0, &actual, &flags, &timestamp, &sample));
                if (sample == IntPtr.Zero)
                {
                    if ((flags & EndOfStream) != 0) break;
                    continue;
                }
                try
                {
                    if (last != null && timestamp > at.Ticks) break;
                    last = Pixels(sample, width, height, stride);
                }
                finally { Marshal.Release(sample); }
                if ((flags & EndOfStream) != 0) break;
            }
            if (last == null) throw new InvalidOperationException("The MP4 has no decodable video frame.");
            return (length, audio, last, width, height);
        }
        finally
        {
            if (reader != IntPtr.Zero) Marshal.Release(reader);
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            MFShutdown();
        }
    }

    private static byte[] Pixels(IntPtr sample, int width, int height, int stride)
    {
        IntPtr buffer;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer));
        try
        {
            byte* data; int max, current;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int*, int*, int>)Slot(buffer, 3))(buffer, &data, &max, &current));
            try
            {
                var pixels = new byte[width * height * 4];
                int row = width * 4, pitch = Math.Abs(stride);
                for (int y = 0; y < height; y++)
                {
                    // A negative default stride means the rows are stored bottom-up.
                    int source = stride < 0 ? (height - 1 - y) * pitch : y * pitch;
                    Marshal.Copy((IntPtr)(data + source), pixels, y * row, row);
                }
                return pixels;
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); }
        }
        finally { Marshal.Release(buffer); }
    }

    /// <summary>The BGRA pixel at a position given as a fraction of the frame.</summary>
    public static (byte B, byte G, byte R) PixelAt(byte[] pixels, int width, int height, double fx, double fy)
    {
        int x = Math.Clamp((int)(fx * width), 0, width - 1), y = Math.Clamp((int)(fy * height), 0, height - 1);
        int i = (y * width + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }
}
