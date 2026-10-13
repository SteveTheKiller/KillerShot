namespace KillerShot.Video;

/// <summary>An encoder that receives recorded frames in order. Frames are top-down BGRA at the
/// recording's size; the timestamp is time since recording started, with pauses removed.</summary>
internal interface IVideoSink : IDisposable
{
    /// <summary>Called once, before the first frame.</summary>
    Task StartAsync(int width, int height, int fps);

    /// <summary>Adds a frame. The sink may keep the array until the call returns, not longer.</summary>
    void AddFrame(byte[] bgra, TimeSpan timestamp);

    /// <summary>Adds 48 kHz stereo 16-bit sound. Sinks without sound ignore it.</summary>
    void AddAudio(byte[] pcm, TimeSpan timestamp) { }

    /// <summary>Writes everything still buffered and closes the file.</summary>
    Task FinishAsync(TimeSpan duration);
}
