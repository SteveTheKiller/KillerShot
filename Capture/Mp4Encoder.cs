using System.Collections.Concurrent;
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
    private AudioStreamDescriptor? _audioDesc;
    private Task? _transcode;
    private IRandomAccessStream? _stream;
    private int _w, _h, _fps;
    private TimeSpan _last = TimeSpan.Zero;

    /// <summary>Whether Media Foundation reads this input bottom-up. Settled by the self-test,
    /// which decodes a recorded frame and checks which way up it is.</summary>
    internal static bool FlipRows = true;

    /// <param name="bitsPerPixel">Target bits per pixel per frame: about 0.05 is small, 0.1 is
    /// balanced and 0.2 is high quality for screen content.</param>
    /// <param name="audioBitrate">AAC bits per second, or 0 for a silent video.</param>
    public Mp4Encoder(string path, double bitsPerPixel, int audioBitrate = 0)
    {
        _path = path;
        _bitsPerPixel = bitsPerPixel;
        _audioBitrate = audioBitrate;
    }

    /// <summary>Frames dropped because the encoder fell behind.</summary>
    public int Dropped { get; private set; }

    public async Task StartAsync(int width, int height, int fps)
    {
        _w = width; _h = height; _fps = fps;

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
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(width * (double)height * fps * _bitsPerPixel, 500_000, 80_000_000);

        System.IO.File.Create(_path).Dispose();
        var file = await StorageFile.GetFileFromPathAsync(_path);
        _stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, _stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException("Windows could not set up the MP4 encoder: " + prepared.FailureReason);
        _transcode = prepared.TranscodeAsync().AsTask();
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
                var item = _queue.Take();
                if (item is { } f)
                {
                    var sample = MediaStreamSample.CreateFromBuffer(f.Frame.AsBuffer(), f.At);
                    sample.Duration = TimeSpan.FromSeconds(1.0 / _fps);
                    sample.KeyFrame = true;
                    request.Sample = sample;
                }
                else
                {
                    request.Sample = null;   // end of stream
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
        var copy = new byte[bgra.Length];
        int stride = _w * 4;
        if (FlipRows)
            for (int y = 0; y < _h; y++)
                System.Buffer.BlockCopy(bgra, y * stride, copy, (_h - 1 - y) * stride, stride);
        else
            System.Buffer.BlockCopy(bgra, 0, copy, 0, copy.Length);
        if (timestamp <= _last && _last != TimeSpan.Zero) timestamp = _last + TimeSpan.FromMilliseconds(1);
        _last = timestamp;
        if (!_queue.TryAdd((copy, timestamp), 2000)) Dropped++;
    }

    public void AddAudio(byte[] pcm, TimeSpan timestamp)
    {
        if (_audioDesc != null) _audio.TryAdd((pcm, timestamp), 2000);
    }

    public async Task FinishAsync(TimeSpan duration)
    {
        _queue.Add(null);
        if (_audioDesc != null) _audio.Add(null);
        if (_transcode != null) await _transcode;
        _queue.CompleteAdding();
        _audio.CompleteAdding();
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted) { try { _queue.Add(null); _queue.CompleteAdding(); } catch { } }
        _stream?.Dispose();
    }

    /// <summary>The widest frame the Windows H.264 encoder accepts. It is built for video up to
    /// 4K, so a recording of several monitors side by side is wider than it will encode.</summary>
    public const int MaxWidth = 4096;

    /// <summary>H.264 needs even dimensions.</summary>
    public static (int W, int H) EvenSize(int w, int h) => (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
}
