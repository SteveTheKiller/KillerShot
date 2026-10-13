using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>
/// Screen recording from start to saved file: pick a region, window or monitor on a frozen copy
/// of the desktop, count down, record into MP4 or GIF, then save, notify and optionally copy the
/// file. The video shortcut, the tray and the editor all start and stop it here.
/// </summary>
internal sealed class VideoController
{
    private readonly TrayHost _tray;
    private Recorder? _recorder;
    private RecordingChrome? _chrome;
    private DispatcherTimer? _tick;
    private string? _path;
    private bool _starting, _discard;
    private TaskCompletionSource? _countdownSkip;

    public bool IsRecording => _recorder != null || _starting;
    public string? LastVideoPath { get; private set; }

    /// <summary>Raised when recording starts or stops, for the tray and the editor.</summary>
    public event Action? StateChanged;

    /// <summary>Hides the editor before the region is chosen; returns how to bring it back.</summary>
    public Func<Task<Action?>>? HideEditor { get; set; }

    private static string Loc(string key) => LocaleManager.Loc(key);

    public VideoController(TrayHost tray) => _tray = tray;

    /// <summary>The video shortcut: start when idle, stop when recording.</summary>
    public void Toggle()
    {
        if (_recorder != null) _ = StopAsync(discard: false);
        else if (_starting) _countdownSkip?.TrySetResult();   // pressing again during the countdown starts at once
        else _ = StartAsync();
    }

    public async Task StartAsync()
    {
        if (IsRecording) return;
        _starting = true;
        StateChanged?.Invoke();
        Action? restore = null;
        try
        {
            if (HideEditor != null) restore = await HideEditor();
            var frozen = FrozenDesktop.Capture();
            var picked = await new RegionSelector(frozen).RunAsync();
            if (picked is not { } rect) return;

            bool gif = VideoSettings.Format == "gif";
            if (!gif)
            {
                var (w, h) = Mp4Encoder.EvenSize(rect.Width, rect.Height);
                rect = new Win32.RECT(rect.Left, rect.Top, rect.Left + w, rect.Top + h);
            }
            int fps = gif ? VideoSettings.GifFps : VideoSettings.Mp4Fps;
            string ext = gif ? "gif" : "mp4";
            string name = OutputFiles.FileName(VideoSettings.FileNamePattern, DateTime.Now, CaptureMode.Region, rect.Width, rect.Height, ext);
            _path = OutputFiles.NextPath(VideoSettings.OutputFolder, name);
            IVideoSink sink = gif
                ? new GifEncoder(_path, VideoSettings.GifColors >= 256 ? 255 : VideoSettings.GifColors, VideoSettings.GifSize / 100.0, VideoSettings.GifLoop)
                : new Mp4Encoder(_path, VideoSettings.Mp4BitsPerPixel, VideoSettings.Sound == SoundSource.None ? 0 : VideoSettings.AudioKbps * 1000);

            _chrome = new RecordingChrome(rect);
            _chrome.PauseClicked += TogglePause;
            _chrome.StopClicked += () => Toggle();
            _chrome.DiscardClicked += () => _ = StopAsync(discard: true);
            _chrome.Show();

            if (VideoSettings.Countdown)
            {
                _countdownSkip = new TaskCompletionSource();
                for (int s = 3; s > 0 && !_countdownSkip.Task.IsCompleted; s--)
                {
                    _chrome.SetCountdown(s);
                    await Task.WhenAny(Task.Delay(1000), _countdownSkip.Task);
                }
                _countdownSkip = null;
            }
            if (_chrome == null) return;   // discarded during the countdown

            _recorder = new Recorder(rect, sink, fps, VideoSettings.IncludeCursor, gif ? SoundSource.None : VideoSettings.Sound);
            await _recorder.StartAsync();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (_, _) => { if (_recorder != null) _chrome?.SetRecording(_recorder.Elapsed, _recorder.IsPaused); };
            _tick.Start();
            _chrome.SetRecording(TimeSpan.Zero, false);
            _tray.SetRecording(true);
        }
        catch (Exception ex)
        {
            Cleanup();
            TryDelete(_path);
            _tray.ShowWarning(Loc("Str_Tray_VideoFailedTitle"), ex.Message);
        }
        finally
        {
            _starting = false;
            restore?.Invoke();
            StateChanged?.Invoke();
        }
    }

    private void TogglePause()
    {
        if (_recorder == null) return;
        if (_recorder.IsPaused) _recorder.Resume(); else _recorder.Pause();
        _chrome?.SetRecording(_recorder.Elapsed, _recorder.IsPaused);
    }

    public async Task StopAsync(bool discard)
    {
        _discard = discard;
        if (_recorder == null)
        {
            // Stopped during the countdown: nothing was recorded.
            _countdownSkip?.TrySetResult();
            Cleanup();
            TryDelete(_path);
            return;
        }
        var recorder = _recorder;
        _recorder = null;
        _tick?.Stop();
        _chrome?.SetSaving();
        string? path = _path;
        try
        {
            await recorder.StopAsync();
            if (_discard) { TryDelete(path); return; }
            LastVideoPath = path;
            if (VideoSettings.CopyFile && path != null) CopyFileToClipboard(path);
            if (VideoSettings.Notify && path != null)
                _tray.ShowInfo(Loc("Str_Tray_VideoSavedTitle"), Path.GetFileName(path), () => ShowInFolder(path));
        }
        catch (Exception ex)
        {
            TryDelete(path);
            _tray.ShowWarning(Loc("Str_Tray_VideoFailedTitle"), ex.Message);
        }
        finally
        {
            Cleanup();
            StateChanged?.Invoke();
        }
    }

    private void Cleanup()
    {
        _tick?.Stop();
        _tick = null;
        _chrome?.Close();
        _chrome = null;
        _tray.SetRecording(false);
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void CopyFileToClipboard(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Clipboard.SetFileDropList(new StringCollection { path });
                return;
            }
            catch { Thread.Sleep(50); }
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }
}
