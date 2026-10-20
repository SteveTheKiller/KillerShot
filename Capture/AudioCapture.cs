using System.Runtime.InteropServices;

namespace KillerShot.Video;

/// <summary>Which sound a recording carries.</summary>
internal enum SoundSource { None, System, Microphone, Both }

internal interface IAudioCaptureSource : IDisposable
{
    Exception? Error { get; }
    void Start();
    void Stop();
    void Drain(int frames, float[]? mix);
}

/// <summary>
/// Records system sound (WASAPI loopback of the default playback device) and/or the default
/// microphone, converts both to 48 kHz stereo, mixes them, and hands 16-bit PCM chunks to the
/// sink on the recording clock. Loopback delivers nothing while nothing is playing, so the mixer
/// fills those gaps with silence to keep sound and picture in step.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    public const int Rate = 48000;
    public const int Channels = 2;
    private const int ChunkMs = 20;

    private readonly List<IAudioCaptureSource> _sources = new();
    private readonly Func<TimeSpan> _clock;
    private readonly Func<bool> _paused;
    private readonly Action<byte[], TimeSpan> _output;
    private Thread? _mixer;
    private volatile bool _stop;
    private volatile Exception? _error;
    private long _emittedFrames;

    public Exception? Error => _error ?? _sources.Select(s => s.Error).FirstOrDefault(e => e != null);

    public AudioCapture(SoundSource source, Func<TimeSpan> clock, Func<bool> paused, Action<byte[], TimeSpan> output)
    {
        _clock = clock;
        _paused = paused;
        _output = output;
        if (source is SoundSource.System or SoundSource.Both) _sources.Add(new WasapiSource(loopback: true));
        if (source is SoundSource.Microphone or SoundSource.Both) _sources.Add(new WasapiSource(loopback: false));
    }

    internal AudioCapture(IEnumerable<IAudioCaptureSource> sources, Func<TimeSpan> clock,
        Func<bool> paused, Action<byte[], TimeSpan> output) : this(SoundSource.None, clock, paused, output)
        => _sources.AddRange(sources);

    /// <summary>Clears sound captured while the devices were starting, before video time zero.</summary>
    internal void DiscardBuffered()
    {
        foreach (var source in _sources) source.Drain(int.MaxValue, null);
    }

    public void Start()
    {
        foreach (var s in _sources) s.Start();
        _mixer = new Thread(MixLoop) { IsBackground = true, Name = "KillerShot audio" };
        _mixer.Start();
    }

    public void Stop()
    {
        _stop = true;
        _mixer?.Join();
        foreach (var s in _sources) s.Stop();
        if (Error is { } error) throw new InvalidOperationException("Audio capture failed: " + error.Message, error);
        // Close the last partial stretch so the sound runs to the end of the picture.
        Emit(_clock());
    }

    public void Dispose()
    {
        _stop = true;
        foreach (var s in _sources) s.Dispose();
    }

    private void MixLoop()
    {
        try
        {
            while (!_stop)
            {
                Thread.Sleep(ChunkMs / 2);
                if (_paused())
                {
                    // Sound made while paused is thrown away, as the picture is.
                    foreach (var s in _sources) s.Drain(int.MaxValue, null);
                    continue;
                }
                Emit(_clock());
            }
        }
        catch (Exception ex) { _error = ex; _stop = true; }
    }

    /// <summary>Writes mixed audio up to the given recording time, in 20 ms chunks.</summary>
    private void Emit(TimeSpan until)
    {
        long target = (long)(until.TotalSeconds * Rate);
        int chunk = Rate * ChunkMs / 1000;
        while (target - _emittedFrames >= chunk)
        {
            var mix = new float[chunk * Channels];
            foreach (var s in _sources) s.Drain(chunk, mix);
            var pcm = new byte[mix.Length * 2];
            for (int i = 0; i < mix.Length; i++)
            {
                short v = (short)Math.Clamp((int)(mix[i] * 32767f), short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)v;
                pcm[i * 2 + 1] = (byte)(v >> 8);
            }
            _output(pcm, TimeSpan.FromSeconds((double)_emittedFrames / Rate));
            _emittedFrames += chunk;
        }
    }

    /// <summary>One WASAPI shared-mode capture stream, converted to 48 kHz stereo float into a buffer.</summary>
    private sealed class WasapiSource : IAudioCaptureSource
    {
        private readonly bool _loopback;
        private readonly Queue<float> _buffer = new();
        private readonly object _lock = new();
        private readonly ManualResetEventSlim _startDone = new(false);
        private Thread? _thread;
        private volatile bool _stop;
        private volatile Exception? _error;
        private double _resamplePos;
        private float _lastL, _lastR;

        public WasapiSource(bool loopback) => _loopback = loopback;

        public Exception? Error => _error;

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = _loopback ? "KillerShot loopback" : "KillerShot microphone" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            if (!_startDone.Wait(TimeSpan.FromSeconds(5)))
            {
                _stop = true;
                throw new TimeoutException((_loopback ? "System audio" : "Microphone") + " capture did not start.");
            }
            if (_error != null)
                throw new InvalidOperationException((_loopback ? "System audio" : "Microphone") + " capture could not start.", _error);
        }

        public void Stop()
        {
            _stop = true;
            if (_thread?.Join(2000) == false)
                _error ??= new TimeoutException((_loopback ? "System audio" : "Microphone") + " capture did not stop.");
        }

        public void Dispose() => Stop();

        /// <summary>Adds up to <paramref name="frames"/> frames into <paramref name="mix"/>,
        /// silence where the device delivered nothing. A null mix only discards.</summary>
        public void Drain(int frames, float[]? mix)
        {
            lock (_lock)
            {
                int available = Math.Min(frames, _buffer.Count / Channels);
                for (int i = 0; i < available * Channels; i++)
                {
                    float v = _buffer.Dequeue();
                    if (mix != null) mix[i] += v;
                }
                // Never let a device run far ahead of the clock (drift between clocks).
                while (_buffer.Count > Rate * Channels / 2) _buffer.Dequeue();
            }
        }

        private void Run()
        {
            IAudioClient? client = null;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                Check(enumerator.GetDefaultAudioEndpoint(_loopback ? 0 : 1, 0, out var device));
                var iid = typeof(IAudioClient).GUID;
                Check(device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var obj));
                client = (IAudioClient)obj;
                IntPtr fmtPtr = IntPtr.Zero;
                int channels, rate, bits;
                uint channelMask = 0;
                bool isFloat;
                try
                {
                    Check(client.GetMixFormat(out fmtPtr));
                    var fmt = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
                    isFloat = fmt.wFormatTag == 3;
                    if (fmt.wFormatTag == 0xFFFE)   // WAVE_FORMAT_EXTENSIBLE: the subformat follows the base struct
                    {
                        if (fmt.cbSize < 22) throw new NotSupportedException("Incomplete extended audio format.");
                        channelMask = (uint)Marshal.ReadInt32(fmtPtr + 20);
                        var sub = Marshal.PtrToStructure<Guid>(fmtPtr + 24);
                        isFloat = sub == new Guid("00000003-0000-0010-8000-00aa00389b71");
                        if (!isFloat && sub != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                            throw new NotSupportedException("Unsupported audio device encoding.");
                    }
                    else if (fmt.wFormatTag is not (1 or 3)) throw new NotSupportedException("Unsupported audio device encoding.");
                    channels = fmt.nChannels; rate = (int)fmt.nSamplesPerSec; bits = fmt.wBitsPerSample;
                    AudioDownmix.Validate(channels, bits, isFloat, channelMask);
                    if (rate <= 0) throw new NotSupportedException("Invalid audio sample rate.");
                    int flags = _loopback ? 0x00020000 /* AUDCLNT_STREAMFLAGS_LOOPBACK */ : 0;
                    Check(client.Initialize(0, flags, 2_000_000 /* 200 ms */, 0, fmtPtr, IntPtr.Zero));
                }
                finally { Marshal.FreeCoTaskMem(fmtPtr); }
                var cid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref cid, out var capObj));
                var capture = (IAudioCaptureClient)capObj;
                Check(client.Start());
                _startDone.Set();

                while (!_stop)
                {
                    Thread.Sleep(10);
                    while (true)
                    {
                        Check(capture.GetNextPacketSize(out uint packet));
                        if (packet == 0) break;
                        Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint bufFlags, out _, out _));
                        bool silent = (bufFlags & 0x2) != 0;   // AUDCLNT_BUFFERFLAGS_SILENT
                        Convert(data, (int)frames, channels, rate, bits, isFloat, silent, channelMask);
                        Check(capture.ReleaseBuffer(frames));
                    }
                }
            }
            catch (Exception ex) { _error = ex; }
            finally
            {
                _startDone.Set();
                if (client != null)
                {
                    try { Check(client.Stop()); } catch (Exception ex) { _error ??= ex; }
                    try { Marshal.ReleaseComObject(client); } catch (Exception ex) { _error ??= ex; }
                }
            }
        }

        /// <summary>Device format to 48 kHz stereo float, with linear resampling.</summary>
        private void Convert(IntPtr data, int frames, int channels, int rate, int bits, bool isFloat, bool silent, uint channelMask)
        {
            var left = new float[frames];
            var right = new float[frames];
            if (!silent)
            {
                for (int f = 0; f < frames; f++)
                {
                    var (l, r) = AudioDownmix.Frame(data, f, channels, bits, isFloat, channelMask);
                    left[f] = l; right[f] = r;
                }
            }
            double step = (double)rate / Rate;
            lock (_lock)
            {
                while (_resamplePos < frames)
                {
                    int i = (int)_resamplePos;
                    double t = _resamplePos - i;
                    float l0 = i == 0 ? _lastL : left[i - 1], r0 = i == 0 ? _lastR : right[i - 1];
                    float l1 = left[i], r1 = right[i];
                    _buffer.Enqueue((float)(l0 + (l1 - l0) * t));
                    _buffer.Enqueue((float)(r0 + (r1 - r0) * t));
                    _resamplePos += step;
                }
                _resamplePos -= frames;
                if (frames > 0) { _lastL = left[frames - 1]; _lastR = right[frames - 1]; }
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    // ---- WASAPI COM interop ----

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
