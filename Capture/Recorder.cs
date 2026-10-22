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
    private readonly IEnumerable<IAudioCaptureSource>? _audioSources;
    private readonly Func<IFrameGrabber>? _grabberFactory;
    private AudioCapture? _audio;
    private IFrameGrabber? _grabber;

    public int Frames { get; private set; }
    public int Dropped { get; private set; }
    public bool IsPaused => _paused;
    /// <summary>A capture, encoder or audio error. Audio counts, so a lost microphone stops the
    /// recording at once instead of recording silence until Stop.</summary>
    public bool HasFailed => Volatile.Read(ref _error) != null || _audio?.Error != null || _sink.Error != null;
    /// <summary>The encoder completed the file, even if a capture source reported an error.</summary>
    public bool IsFinalized { get; private set; }

    /// <summary>Whether frames come from Windows Graphics Capture rather than the GDI fallback.</summary>
    public bool UsesGraphicsCapture { get; private set; }

    public Recorder(Win32.RECT rect, IVideoSink sink, int fps, bool includeCursor, SoundSource sound = SoundSource.None,
        IEnumerable<IAudioCaptureSource>? audioSources = null, Func<IFrameGrabber>? grabberFactory = null)
    {
        _sound = sound;
        _audioSources = audioSources;
        _grabberFactory = grabberFactory;
        _rect = rect;
        _sink = sink;
        _fps = Math.Clamp(fps, 1, 60);
        _cursor = includeCursor;
    }

    /// <summary>Recording time with pauses removed.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_clock) return (_paused ? _pausedAt : _clock.Elapsed) - _pausedTotal; }
    }

    public async Task StartAsync()
    {
        try
        {
            await _sink.StartAsync(_rect.Width, _rect.Height, _fps);
            // Created off the UI thread, before the clock starts, so the first frame is ready on time.
            _grabber = await Task.Run(() => _grabberFactory?.Invoke() ?? (IFrameGrabber?)MonitorGrabber.TryCreate(_rect, _cursor) ?? new ScreenGrabber(_rect, _cursor));
            UsesGraphicsCapture = _grabber is MonitorGrabber;
            if (_sound != SoundSource.None || _audioSources != null)
            {
                _audio = _audioSources == null
                    ? new AudioCapture(_sound, () => Elapsed, () => _paused, (pcm, at) => _sink.AddAudio(pcm, at))
                    : new AudioCapture(_audioSources, () => Elapsed, () => _paused, (pcm, at) => _sink.AddAudio(pcm, at));
                await Task.Run(_audio.Start);
            }
            _audio?.DiscardBuffered();
            _clock.Start();
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "KillerShot capture", Priority = ThreadPriority.AboveNormal };
            _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "KillerShot encode" };
            _encodeThread.Start();
            _captureThread.Start();
        }
        catch
        {
            _stop = true;
            try { _queue.CompleteAdding(); } catch { }
            try { if (_encodeThread?.IsAlive == true) await Task.Run(_encodeThread.Join); } catch { }
            try { _audio?.Dispose(); } catch { }
            try { _grabber?.Dispose(); } catch { }
            // The MP4 transcoder may still own the staging file after recording startup fails.
            try { await _sink.FinishAsync(TimeSpan.Zero); } catch { }
            try { _sink.Dispose(); } catch { }
            throw;
        }
    }

    public void Pause()
    {
        lock (_clock)
        {
            if (_paused) return;
            _pausedAt = _clock.Elapsed;
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_clock)
        {
            if (!_paused) return;
            _pausedTotal += _clock.Elapsed - _pausedAt;
            _paused = false;
        }
    }

    /// <summary>Stops capturing, lets the encoder drain, and closes the file.</summary>
    public async Task StopAsync()
    {
        TimeSpan duration;
        lock (_clock)
        {
            _clock.Stop();
            duration = Elapsed;
        }
        _stop = true;
        Exception? failure = null;
        try
        {
            await Task.Run(() =>
            {
                try { _audio?.Stop(); }
                catch (Exception ex) { failure = ex; }
                _captureThread?.Join();
                _encodeThread?.Join();
            });
        }
        catch (Exception ex) { failure ??= ex; }
        failure ??= _error ?? _audio?.Error ?? _sink.Error;
        try
        {
            await _sink.FinishAsync(duration);
            IsFinalized = true;
        }
        catch (Exception ex) { failure ??= ex; }
        try { _audio?.Dispose(); }
        catch (Exception ex) { failure ??= ex; }
        try { _sink.Dispose(); }
        catch (Exception ex) { failure ??= ex; }
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
    }

    private void CaptureLoop()
    {
        Win32.timeBeginPeriod(1);
        try
        {
            using var grabber = _grabber!;
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
        catch (Exception ex) { Fail(ex); }
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
            Fail(ex);
            foreach (var _ in _queue.GetConsumingEnumerable()) { }
        }
    }

    private void Fail(Exception error)
    {
        lock (_clock) _clock.Stop();
        Interlocked.CompareExchange(ref _error, error, null);
        _stop = true;
    }
}
