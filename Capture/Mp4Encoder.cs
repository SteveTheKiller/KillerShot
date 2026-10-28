using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace KillerShot.Video;

/// <summary>
/// H.264 MP4 through the Windows Media Foundation sink writer. Frames are queued and written by
/// a background consumer, so encoding runs while recording and the hardware encoder is used when
/// the machine has one. Nothing ships with the app: the encoder is part of Windows, and it is
/// called through its plain COM interfaces rather than a Windows Runtime projection.
///
/// GUIDs and vtable slots come from mfapi.h, mfobjects.h and mfreadwrite.h (Windows SDK
/// 10.0.26100.0). IMFAttributes: SetUINT32 = 21, SetUINT64 = 22, SetGUID = 24. IMFSample:
/// SetSampleTime = 36, SetSampleDuration = 38, AddBuffer = 42. IMFMediaBuffer: Lock = 3,
/// Unlock = 4, SetCurrentLength = 6. IMFSinkWriter: AddStream = 3, SetInputMediaType = 4,
/// BeginWriting = 5, WriteSample = 6, Finalize = 11.
/// </summary>
internal sealed class Mp4Encoder : IVideoSink
{
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    private static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    private static readonly Guid MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

    private const int MF_VERSION = 0x00020070;

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr mediaType);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(int maxLength, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr sinkWriter);

    private readonly string _path;
    private readonly double _bitsPerPixel;
    private readonly BlockingCollection<(byte[] Frame, TimeSpan At)?> _queue = new(boundedCapacity: 8);
    private readonly BlockingCollection<(byte[] Pcm, TimeSpan At)?> _audio = new(boundedCapacity: 400);
    private readonly int _audioBitrate;
    private readonly Func<Task>? _transcodeFactory;
    private readonly object _writerLock = new();
    private bool _hasAudio;
    private Task? _transcode;
    private IntPtr _writer;
    private bool _started;
    private int _videoStream, _audioStream;
    private int _w, _h, _fps;
    private bool _scaleOnCpu;
    private TimeSpan _last = TimeSpan.Zero;
    private TimeSpan _finishedAt;

    /// <summary>Kept for the self-tests, which decode a recorded frame and check which way up it
    /// is. Frames go to Media Foundation top-down with a positive default stride, so no rows are
    /// flipped.</summary>
    internal static bool FlipRows = false;

    /// <param name="bitsPerPixel">Target bits per pixel per frame: about 0.05 is small, 0.1 is
    /// balanced and 0.2 is high quality for screen content.</param>
    /// <param name="audioBitrate">AAC bits per second, or 0 for a silent video.</param>
    /// <param name="transcodeFactory">Test seam: replaces the background writer, so the queues
    /// fill and fail exactly as they would behind a stalled or failed encoder.</param>
    public Mp4Encoder(string path, double bitsPerPixel, int audioBitrate = 0, Func<Task>? transcodeFactory = null)
    {
        _path = path;
        _bitsPerPixel = bitsPerPixel;
        _audioBitrate = audioBitrate;
        _transcodeFactory = transcodeFactory;
    }

    /// <summary>The size of the encoded video, which is smaller than the recorded area when the
    /// area is larger than the encoder accepts.</summary>
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>Frames dropped because the encoder fell behind.</summary>
    public int Dropped { get; private set; }

    public Exception? Error => _transcode is { IsFaulted: true } failed ? failed.Exception!.InnerException
        : _transcode is { IsCanceled: true } canceled ? new TaskCanceledException(canceled) : null;

    public Task StartAsync(int width, int height, int fps)
    {
        _w = width; _h = height; _fps = fps;
        var (outW, outH) = OutputSize(width, height);
        OutputWidth = outW; OutputHeight = outH;
        _hasAudio = _audioBitrate > 0;

        if (_transcodeFactory != null)
        {
            ObserveTranscode(_transcodeFactory());
            return Task.CompletedTask;
        }
        Open(outW, outH);
        ObserveTranscode(Task.Run(RunAsync));
        return Task.CompletedTask;
    }

    private unsafe void Open(int outW, int outH)
    {
        Marshal.ThrowExceptionForHR(MFStartup(MF_VERSION, 0));
        _started = true;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 3));
            SetUInt32(attributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            // The two streams are written from separate threads; throttling would let one
            // stream's write wait on the other while holding the writer lock.
            SetUInt32(attributes, MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            SetGuid(attributes, MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
            System.IO.File.Create(_path).Dispose();
            Marshal.ThrowExceptionForHR(MFCreateSinkWriterFromURL(_path, IntPtr.Zero, attributes, out _writer));
        }
        finally { Release(ref attributes); }

        uint bitrate = (uint)Math.Clamp(outW * (double)outH * _fps * _bitsPerPixel, 500_000, 80_000_000);
        _videoStream = AddStream(VideoType(MFVideoFormat_H264, outW, outH, bitrate, stride: 0));
        // The recorded size goes in, and Media Foundation's video processor scales it down when
        // the output is smaller. Where it cannot, frames are scaled here instead.
        try { SetInput(_videoStream, VideoType(MFVideoFormat_RGB32, _w, _h, 0, stride: _w * 4)); }
        catch (COMException) when (outW != _w || outH != _h)
        {
            _scaleOnCpu = true;
            SetInput(_videoStream, VideoType(MFVideoFormat_RGB32, outW, outH, 0, stride: outW * 4));
        }
        if (_hasAudio)
        {
            _audioStream = AddStream(AudioType(MFAudioFormat_AAC, (uint)_audioBitrate / 8));
            SetInput(_audioStream, AudioType(MFAudioFormat_PCM, (uint)(AudioCapture.Rate * AudioCapture.Channels * 2)));
        }
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_writer, 5))(_writer));
    }

    private IntPtr VideoType(Guid subtype, int w, int h, uint bitrate, int stride)
    {
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out IntPtr type));
        try
        {
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(type, MF_MT_SUBTYPE, subtype);
            SetUInt32(type, MF_MT_INTERLACE_MODE, 2);   // progressive
            SetUInt64(type, MF_MT_FRAME_SIZE, ((ulong)(uint)w << 32) | (uint)h);
            SetUInt64(type, MF_MT_FRAME_RATE, ((ulong)(uint)_fps << 32) | 1);
            SetUInt64(type, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            if (bitrate > 0) SetUInt32(type, MF_MT_AVG_BITRATE, bitrate);
            if (stride > 0)
            {
                SetUInt32(type, MF_MT_DEFAULT_STRIDE, (uint)stride);   // positive: top-down rows
                SetUInt32(type, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            }
            return type;
        }
        catch { Release(ref type); throw; }
    }

    private static IntPtr AudioType(Guid subtype, uint bytesPerSecond)
    {
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out IntPtr type));
        try
        {
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(type, MF_MT_SUBTYPE, subtype);
            SetUInt32(type, MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)AudioCapture.Rate);
            SetUInt32(type, MF_MT_AUDIO_NUM_CHANNELS, (uint)AudioCapture.Channels);
            SetUInt32(type, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetUInt32(type, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytesPerSecond);
            if (subtype == MFAudioFormat_PCM) SetUInt32(type, MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(AudioCapture.Channels * 2));
            return type;
        }
        catch { Release(ref type); throw; }
    }

    private unsafe int AddStream(IntPtr type)
    {
        try
        {
            int index;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)Slot(_writer, 3))(_writer, type, &index));
            return index;
        }
        finally { Release(ref type); }
    }

    private unsafe void SetInput(int stream, IntPtr type)
    {
        try { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int>)Slot(_writer, 4))(_writer, stream, type, IntPtr.Zero)); }
        finally { Release(ref type); }
    }

    private void ObserveTranscode(Task transcode)
    {
        _transcode = transcode;
        _ = transcode.ContinueWith(completed =>
        {
            if (!completed.IsFaulted && !completed.IsCanceled) return;
            _ = completed.Exception;
            // Wake blocked producers when the consumer can no longer run.
            CompleteQueues();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Writes both streams until the queues complete, then finalizes the file.</summary>
    private async Task RunAsync()
    {
        try
        {
            var audio = _hasAudio ? Task.Run(WriteAudio) : Task.CompletedTask;
            WriteVideo();
            await audio;
            FinalizeWriter();
        }
        catch
        {
            CompleteQueues();
            throw;
        }
    }

    private unsafe void FinalizeWriter()
    {
        lock (_writerLock)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_writer, 11))(_writer));
    }

    private void WriteVideo()
    {
        // Look ahead one frame so sparse captures hold until the next frame, and the final
        // picture holds until the recording's Stop timestamp.
        (byte[] Frame, TimeSpan At)? pending = null;
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item is not { } next) break;
            if (pending is { } f) WriteFrame(f.Frame, f.At, next.At > f.At ? next.At - f.At : TimeSpan.FromSeconds(1.0 / _fps));
            pending = next;
        }
        if (pending is { } last)
            WriteFrame(last.Frame, last.At, _finishedAt > last.At ? _finishedAt - last.At : TimeSpan.FromSeconds(1.0 / _fps));
    }

    private void WriteAudio()
    {
        foreach (var item in _audio.GetConsumingEnumerable())
        {
            if (item is not { } a) break;
            WriteSample(_audioStream, a.Pcm, a.At, TimeSpan.FromSeconds(a.Pcm.Length / 4.0 / AudioCapture.Rate));
        }
    }

    private void WriteFrame(byte[] bgra, TimeSpan at, TimeSpan duration)
        => WriteSample(_videoStream, _scaleOnCpu ? Scale(bgra, _w, _h, OutputWidth, OutputHeight) : bgra, at, duration);

    private unsafe void WriteSample(int stream, byte[] data, TimeSpan at, TimeSpan duration)
    {
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(data.Length, out buffer));
            byte* target;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int*, int*, int>)Slot(buffer, 3))(buffer, &target, null, null));
            try { Marshal.Copy(data, 0, (IntPtr)target, data.Length); }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer); }
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(buffer, 6))(buffer, data.Length));

            Marshal.ThrowExceptionForHR(MFCreateSample(out sample));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(sample, 42))(sample, buffer));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 36))(sample, at.Ticks));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 38))(sample, duration.Ticks));
            lock (_writerLock)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)Slot(_writer, 6))(_writer, stream, sample));
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer);
        }
    }

    /// <summary>Area-averaged downscale, used only where Media Foundation will not scale.</summary>
    private static byte[] Scale(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * sw + sx) * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3]; n++;
                    }
                int o = (y * dw + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        }
        return dst;
    }

    public void AddFrame(byte[] bgra, TimeSpan timestamp)
    {
        ThrowIfFailed();
        var copy = new byte[bgra.Length];
        int stride = _w * 4;
        if (FlipRows)
            for (int y = 0; y < _h; y++)
                System.Buffer.BlockCopy(bgra, y * stride, copy, (_h - 1 - y) * stride, stride);
        else
            System.Buffer.BlockCopy(bgra, 0, copy, 0, copy.Length);
        if (timestamp <= _last && _last != TimeSpan.Zero) timestamp = _last + TimeSpan.FromMilliseconds(1);
        _last = timestamp;
        if (!Add(_queue, (copy, timestamp))) Dropped++;
    }

    public void AddAudio(byte[] pcm, TimeSpan timestamp)
    {
        if (_hasAudio) Add(_audio, (pcm, timestamp));
    }

    private bool Add<T>(BlockingCollection<T> queue, T item)
    {
        ThrowIfFailed();
        try
        {
            bool added = queue.TryAdd(item, 2000);
            ThrowIfFailed();
            return added;
        }
        catch (InvalidOperationException)
        {
            ThrowIfFailed();   // queue closure must not hide the original encoder error
            throw;
        }
    }

    private void ThrowIfFailed()
    {
        if (Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public async Task FinishAsync(TimeSpan duration)
    {
        _finishedAt = duration;
        CompleteQueues();
        try { if (_transcode != null) await _transcode; }
        finally { Close(); }
    }

    public void Dispose()
    {
        CompleteQueues();
        // A writer still running finishes on its own thread; it releases nothing until it stops.
        if (_transcode is { IsCompleted: false } running && _transcodeFactory == null)
            _ = running.ContinueWith(_ => Close(), TaskScheduler.Default);
        else Close();
    }

    private void CompleteQueues()
    {
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        if (!_audio.IsAddingCompleted) _audio.CompleteAdding();
    }

    private void Close()
    {
        lock (_writerLock)
        {
            Release(ref _writer);
            if (_started)
            {
                _started = false;
                MFShutdown();
            }
        }
    }

    private unsafe static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        Marshal.Release(com);
        com = IntPtr.Zero;
    }

    private unsafe static void SetUInt32(IntPtr attributes, Guid key, uint value)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value));

    private unsafe static void SetUInt64(IntPtr attributes, Guid key, ulong value)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(attributes, 22))(attributes, &key, value));

    private unsafe static void SetGuid(IntPtr attributes, Guid key, Guid value)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value));

    /// <summary>The largest frame the Windows H.264 encoder accepts: no side over 4096 pixels and
    /// no more than 36,864 blocks of 16 x 16 pixels, which is 4096 x 2304. That is the frame size
    /// limit of H.264 level 5.1. Larger recordings are encoded smaller, keeping their shape.</summary>
    public const int MaxSide = 4096;
    public const int MaxMacroblocks = 36_864;

    /// <summary>The size the video is encoded at: the recorded size when the encoder accepts it,
    /// otherwise the largest even size with the same shape that it does.</summary>
    public static (int W, int H) OutputSize(int width, int height)
    {
        var (w, h) = EvenSize(width, height);
        double scale = Math.Min(1.0, Math.Min((double)MaxSide / w, (double)MaxSide / h));
        scale = Math.Min(scale, Math.Sqrt(MaxMacroblocks * 256.0 / ((double)w * h)));
        while (true)
        {
            var (ow, oh) = scale >= 1.0 ? (w, h) : EvenSize((int)(w * scale), (int)(h * scale));
            if (Fits(ow, oh)) return (ow, oh);
            scale = Math.Min(scale, 1.0) * 0.995;
        }
    }

    private static bool Fits(int w, int h) =>
        w <= MaxSide && h <= MaxSide && ((w + 15) / 16) * ((h + 15) / 16) <= MaxMacroblocks;

    /// <summary>H.264 needs even dimensions.</summary>
    public static (int W, int H) EvenSize(int w, int h) => (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
}
