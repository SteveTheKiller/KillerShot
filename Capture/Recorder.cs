using System.Collections.Concurrent;
using System.Diagnostics;
using KillerShot.Capture;
using KillerShot.Native;

namespace KillerShot.Video;

/// <summary>
/// Records one screen rectangle into a sink. A capture thread grabs frames on a steady clock and
/// an encoder thread feeds them to the sink, so a slow encoder costs dropped frames rather than
/// a drifting clock. Paused time is cut out of the timestamps.
/// </summary>
internal sealed class Recorder
{
    private readonly Win32.RECT _rect;
    private readonly IVideoSink _sink;
    private readonly int _fps;
    private readonly bool _cursor;
    private readonly BlockingCollection<(byte[] Frame, TimeSpan At)> _queue = new(boundedCapacity: 6);
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly Stopwatch _clock = new();
    private TimeSpan _pausedTotal, _pausedAt;
    private volatile bool _stop, _paused;
    private Thread? _captureThread, _encodeThread;
    private Exception? _error;
    private readonly SoundSource _sound;
    private AudioCapture? _audio;

    public int Frames { get; private set; }
    public int Dropped { get; private set; }
    public bool IsPaused => _paused;

    public Recorder(Win32.RECT rect, IVideoSink sink, int fps, bool includeCursor, SoundSource sound = SoundSource.None)
    {
        _sound = sound;
        _rect = rect;
        _sink = sink;
        _fps = Math.Clamp(fps, 1, 60);
        _cursor = includeCursor;
    }

    /// <summary>Recording time with pauses removed.</summary>
    public TimeSpan Elapsed => (_paused ? _pausedAt : _clock.Elapsed) - _pausedTotal;

    public async Task StartAsync()
    {
        await _sink.StartAsync(_rect.Width, _rect.Height, _fps);
        if (_sound != SoundSource.None)
            _audio = new AudioCapture(_sound, () => Elapsed, () => _paused, (pcm, at) => _sink.AddAudio(pcm, at));
        _clock.Start();
        _audio?.Start();
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "KillerShot capture", Priority = ThreadPriority.AboveNormal };
        _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "KillerShot encode" };
        _encodeThread.Start();
        _captureThread.Start();
    }

    public void Pause()
    {
        if (_paused) return;
        _pausedAt = _clock.Elapsed;
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused) return;
        _pausedTotal += _clock.Elapsed - _pausedAt;
        _paused = false;
    }

    /// <summary>Stops capturing, lets the encoder drain, and closes the file.</summary>
    public async Task StopAsync()
    {
        var duration = Elapsed;
        _stop = true;
        await Task.Run(() => { _captureThread?.Join(); _encodeThread?.Join(); _audio?.Stop(); });
        _audio?.Dispose();
        if (_error != null) throw new InvalidOperationException(_error.Message, _error);
        await _sink.FinishAsync(duration);
    }

    private void CaptureLoop()
    {
        Win32.timeBeginPeriod(1);
        try
        {
            using var grabber = new ScreenGrabber(_rect, _cursor);
            int size = grabber.Width * grabber.Height * 4;
            double frameMs = 1000.0 / _fps;
            double next = 0;
            while (!_stop)
            {
                if (_paused) { Thread.Sleep(15); continue; }
                double now = Elapsed.TotalMilliseconds;
                if (now < next)
                {
                    int wait = (int)(next - now);
                    if (wait > 1) Thread.Sleep(wait - 1); else Thread.SpinWait(200);
                    continue;
                }
                var buffer = _pool.TryTake(out var b) ? b : new byte[size];
                if (grabber.Grab(buffer))
                {
                    if (_queue.TryAdd((buffer, TimeSpan.FromMilliseconds(now)))) Frames++;
                    else { Dropped++; _pool.Add(buffer); }
                }
                next += frameMs;
                if (now - next > frameMs) next = now + frameMs;   // fell behind: skip ahead, don't burst
            }
        }
        catch (Exception ex) { _error ??= ex; }
        finally
        {
            Win32.timeEndPeriod(1);
            _queue.CompleteAdding();
        }
    }

    private void EncodeLoop()
    {
        try
        {
            foreach (var (frame, at) in _queue.GetConsumingEnumerable())
            {
                if (_error == null) _sink.AddFrame(frame, at);
                _pool.Add(frame);
            }
        }
        catch (Exception ex)
        {
            _error ??= ex;
            _stop = true;
            foreach (var _ in _queue.GetConsumingEnumerable()) { }
        }
    }
}
