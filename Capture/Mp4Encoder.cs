using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace KillerShot.Video;

/// <summary>
/// H.264 MP4 through the Windows Media Foundation transcoder. Frames are handed to a
/// MediaStreamSource that the transcoder pulls from, so encoding runs while recording and the
/// hardware encoder is used when the machine has one. Nothing ships with the app: the encoder is
/// part of Windows.
/// </summary>
internal sealed class Mp4Encoder : IVideoSink
{
    private readonly string _path;
    private readonly double _bitsPerPixel;
    private readonly BlockingCollection<(byte[] Frame, TimeSpan At)?> _queue = new(boundedCapacity: 8);
    private readonly BlockingCollection<(byte[] Pcm, TimeSpan At)?> _audio = new(boundedCapacity: 400);
    private readonly int _audioBitrate;
    private readonly Func<Task>? _transcodeFactory;
    private AudioStreamDescriptor? _audioDesc;
    private Task? _transcode;
    private IRandomAccessStream? _stream;
    private int _w, _h, _fps;
    private TimeSpan _last = TimeSpan.Zero;
    private readonly object _videoSamples = new();
    private (byte[] Frame, TimeSpan At)? _pendingVideo;
    private TimeSpan _finishedAt;

    /// <summary>Whether Media Foundation reads this input bottom-up. Settled by the self-test,
    /// which decodes a recorded frame and checks which way up it is.</summary>
    internal static bool FlipRows = true;

    /// <param name="bitsPerPixel">Target bits per pixel per frame: about 0.05 is small, 0.1 is
    /// balanced and 0.2 is high quality for screen content.</param>
    /// <param name="audioBitrate">AAC bits per second, or 0 for a silent video.</param>
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

    public async Task StartAsync(int width, int height, int fps)
    {
        _w = width; _h = height; _fps = fps;
        // Frames go in at the recorded size. When the output size is smaller, the transcoder
        // scales each frame down before it reaches the encoder.
        var (outW, outH) = OutputSize(width, height);
        OutputWidth = outW; OutputHeight = outH;

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        input.FrameRate.Numerator = (uint)fps;
        input.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(input));
        if (_audioBitrate > 0)
        {
            _audioDesc = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioCapture.Rate, AudioCapture.Channels, 16));
            source.AddStreamDescriptor(_audioDesc);
        }
        source.BufferTime = TimeSpan.Zero;
        source.CanSeek = false;
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = _audioBitrate > 0
            ? AudioEncodingProperties.CreateAac(AudioCapture.Rate, AudioCapture.Channels, (uint)_audioBitrate)
            : null;
        profile.Video.Width = (uint)outW;
        profile.Video.Height = (uint)outH;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(outW * (double)outH * fps * _bitsPerPixel, 500_000, 80_000_000);

        if (_transcodeFactory != null)
        {
            ObserveTranscode(_transcodeFactory());
            return;
        }
        System.IO.File.Create(_path).Dispose();
        var file = await StorageFile.GetFileFromPathAsync(_path);
        _stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, _stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException("Windows could not set up the MP4 encoder: " + prepared.FailureReason);
        ObserveTranscode(prepared.TranscodeAsync().AsTask());
    }

    private void ObserveTranscode(Task transcode)
    {
        _transcode = transcode;
        _ = transcode.ContinueWith(completed =>
        {
            if (!completed.IsFaulted && !completed.IsCanceled) return;
            _ = completed.Exception;
            // Wake blocked producers and sample requests when the consumer can no longer run.
            CompleteQueues();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs e)
    {
        var request = e.Request;
        var deferral = request.GetDeferral();
        if (request.StreamDescriptor is AudioStreamDescriptor)
        {
            Task.Run(() =>
            {
                try
                {
                    var item = _audio.Take();
                    if (item is { } a)
                    {
                        var sample = MediaStreamSample.CreateFromBuffer(a.Pcm.AsBuffer(), a.At);
                        sample.Duration = TimeSpan.FromSeconds(a.Pcm.Length / 4.0 / AudioCapture.Rate);
                        request.Sample = sample;
                    }
                    else request.Sample = null;
                }
                catch (InvalidOperationException) { request.Sample = null; }
                finally { deferral.Complete(); }
            });
            return;
        }
        Task.Run(() =>
        {
            try
            {
                lock (_videoSamples)
                {
                    var item = _pendingVideo;
                    if (item == null) _queue.TryTake(out item, Timeout.Infinite);
                    if (item is { } f)
                    {
                        // Look ahead one frame so sparse captures hold until the next frame,
                        // and the final picture holds until the recording's Stop timestamp.
                        _queue.TryTake(out var next, Timeout.Infinite);
                        _pendingVideo = next;
                        TimeSpan end = next?.At ?? _finishedAt;
                        var sample = MediaStreamSample.CreateFromBuffer(f.Frame.AsBuffer(), f.At);
                        sample.Duration = end > f.At ? end - f.At : TimeSpan.FromSeconds(1.0 / _fps);
                        sample.KeyFrame = true;
                        request.Sample = sample;
                    }
                    else request.Sample = null;
                }
            }
            catch (InvalidOperationException)
            {
                request.Sample = null;   // queue completed
            }
            finally
            {
                deferral.Complete();
            }
        });
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
        if (_audioDesc != null) Add(_audio, (pcm, timestamp));
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
            ThrowIfFailed();   // queue closure must not hide the original transcoder error
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
        finally { DisposeStream(); }
    }

    public void Dispose()
    {
        CompleteQueues();
        DisposeStream();
    }

    private void CompleteQueues()
    {
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        if (!_audio.IsAddingCompleted) _audio.CompleteAdding();
    }

    private void DisposeStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

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
