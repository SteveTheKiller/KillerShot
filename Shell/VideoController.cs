using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using KillerShot.Capture;
using KillerShot.Controls;
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
    private readonly IEnumerable<IAudioCaptureSource>? _audioSources;
    private readonly Func<string, IVideoSink>? _sinkFactory;
    private Recorder? _recorder;
    private RecordingChrome? _chrome;
    private DispatcherTimer? _tick;
    private string? _path, _stagingFolder, _outputFolder, _outputName;
    private bool _starting, _stopping, _stopRequested, _discard;
    private TaskCompletionSource? _countdownSkip;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopSucceeded = true;
    private Action? _restoreEditor;

    public bool IsRecording => _recorder != null || _starting || _stopping;
    public string? LastVideoPath { get; private set; }

    /// <summary>Raised when recording starts or stops, for the tray and the editor.</summary>
    public event Action? StateChanged;

    private static string Loc(string key) => LocaleManager.Loc(key);

    public VideoController(TrayHost tray, IEnumerable<IAudioCaptureSource>? audioSources = null, Func<string, IVideoSink>? sinkFactory = null)
    {
        _tray = tray;
        _audioSources = audioSources;
        _sinkFactory = sinkFactory;
        _tray.PauseRecordingRequested += TogglePause;
        _tray.DiscardRecordingRequested += () => _ = StopAsync(true);
        _tray.RecordingPaused = () => _recorder?.IsPaused == true;
    }

    /// <summary>The video shortcut: start when idle, stop when recording.</summary>
    public void Toggle(bool fromShortcut = false)
    {
        if (_stopping) return;
        if (_recorder != null) _ = StopAsync(discard: false);
        else if (_starting) _countdownSkip?.TrySetResult();   // pressing again during the countdown starts at once
        else if (App.Instance.Capture is { } capture) _ = capture.StartVideo(fromShortcut);
    }

    public async Task StartAsync(Win32.RECT rect, Action? restoreEditor = null)
    {
        if (IsRecording) { restoreEditor?.Invoke(); return; }
        _restoreEditor = restoreEditor;
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopSucceeded = false;
        _discard = false;
        _stopRequested = false;
        _starting = true;
        StateChanged?.Invoke();
        IVideoSink? sink = null;
        bool recorderStarted = false;
        try
        {
            bool gif = VideoSettings.Format == "gif";
            if (!gif)
            {
                var (w, h) = Mp4Encoder.EvenSize(rect.Width, rect.Height);
                rect = new Win32.RECT(rect.Left, rect.Top, rect.Left + w, rect.Top + h);
            }
            int fps = gif ? VideoSettings.GifFps : VideoSettings.Mp4Fps;
            string ext = gif ? "gif" : "mp4";
            string name = OutputFiles.FileName(VideoSettings.FileNamePattern, DateTime.Now, CaptureMode.Region, rect.Width, rect.Height, ext);
            string outputFolder = VideoSettings.OutputFolder;

            _chrome = new RecordingChrome(rect);
            _chrome.PauseClicked += TogglePause;
            _chrome.StopClicked += () => Toggle();
            _chrome.DiscardClicked += () => _ = StopAsync(discard: true);
            _chrome.Show();

            if (VideoSettings.Countdown)
            {
                _countdownSkip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                for (int s = 3; s > 0 && !_countdownSkip.Task.IsCompleted; s--)
                {
                    _chrome.SetCountdown(s);
                    await Task.WhenAny(Task.Delay(1000), _countdownSkip.Task);
                }
                _countdownSkip = null;
            }
            if (_chrome == null) return;   // discarded during the countdown

            _outputFolder = outputFolder;
            _outputName = name;
            _stagingFolder = Path.Combine(outputFolder, $".KillerShot-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_stagingFolder);
            try { File.SetAttributes(_stagingFolder, File.GetAttributes(_stagingFolder) | FileAttributes.Hidden); }
            catch { }
            _path = OutputFiles.NextPath(_stagingFolder, $"recording.{ext}");
            sink = _sinkFactory?.Invoke(_path) ?? (gif
                ? new GifEncoder(_path, VideoSettings.GifColors >= 256 ? 255 : VideoSettings.GifColors, VideoSettings.GifSize / 100.0, VideoSettings.GifLoop)
                : new Mp4Encoder(_path, VideoSettings.Mp4BitsPerPixel, VideoSettings.Sound == SoundSource.None ? 0 : VideoSettings.AudioKbps * 1000));
            _recorder = new Recorder(rect, sink, fps, VideoSettings.IncludeCursor, gif ? SoundSource.None : VideoSettings.Sound,
                gif ? null : _audioSources);
            await _recorder.StartAsync();
            recorderStarted = true;
            if (_stopRequested) return;
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (_, _) =>
            {
                if (_recorder is not { } recorder) return;
                if (recorder.HasFailed) { _ = StopAsync(discard: false); return; }
                _chrome?.SetRecording(recorder.Elapsed, recorder.IsPaused);
            };
            _tick.Start();
            _chrome.SetRecording(TimeSpan.Zero, false);
            _tray.SetRecording(true);
        }
        catch (Exception ex)
        {
            var failedRecorder = _recorder;
            _recorder = null;
            if (recorderStarted && failedRecorder != null)
            {
                try { await failedRecorder.StopAsync(); } catch { }
            }
            try { sink?.Dispose(); } catch { }
            Cleanup();
            CleanupStaging();
            _tray.ShowWarning(Loc("Str_Tray_VideoFailedTitle"), ex.Message);
        }
        finally
        {
            _starting = false;
            if (_stopRequested && _recorder != null)
            {
                _stopRequested = false;
                await StopAsync(_discard);
            }
            if (!IsRecording) { RestoreEditor(); _idle.TrySetResult(); }
            StateChanged?.Invoke();
        }
    }

    public void TogglePause()
    {
        if (_recorder == null) return;
        if (_recorder.IsPaused) _recorder.Resume(); else _recorder.Pause();
        _chrome?.SetRecording(_recorder.Elapsed, _recorder.IsPaused);
        StateChanged?.Invoke();
    }

    public async Task StopAsync(bool discard)
    {
        if (_stopping) { _discard |= discard; await _idle.Task; return; }
        if (_starting)
        {
            _stopRequested = true;
            _discard |= discard;
            if (_recorder == null)
            {
                _countdownSkip?.TrySetResult();
                Cleanup();
                _stopSucceeded = true;
            }
            await _idle.Task;
            return;
        }
        _discard = discard;
        if (_recorder == null)
        {
            // Stopped during the countdown: nothing was recorded.
            _countdownSkip?.TrySetResult();
            Cleanup();
            CleanupStaging();
            return;
        }
        var recorder = _recorder;
        _stopping = true;
        _recorder = null;
        try { _tick?.Stop(); } catch { }
        try { _chrome?.SetSaving(); } catch { }
        bool retainFinalizedStage = false;
        try
        {
            Exception? captureWarning = null;
            try { await recorder.StopAsync(); }
            catch (Exception ex) when (recorder.IsFinalized) { captureWarning = ex; }
            if (_discard) return;
            string published;
            try { published = OutputFiles.PublishUnique(_path!, _outputFolder!, _outputName!); }
            catch (Exception ex)
            {
                retainFinalizedStage = true;
                LastVideoPath = _path;
                ReportPublicationFailure(_path!, ex);
                return;
            }
            LastVideoPath = published;
            _stopSucceeded = true;
            if (VideoSettings.CopyFile) CopyFileToClipboard(published);
            if (captureWarning != null)
                _tray.ShowWarning(Loc("Str_Tray_VideoSavedTitle"), Path.GetFileName(published) + "\n" + captureWarning.Message);
            else if (VideoSettings.Notify)
                _tray.ShowInfo(Loc("Str_Tray_VideoSavedTitle"), Path.GetFileName(published), () => ShowInFolder(published));
        }
        catch (Exception ex)
        {
            _tray.ShowWarning(Loc("Str_Tray_VideoFailedTitle"), ex.Message);
        }
        finally
        {
            CleanupStaging(retainFinalizedStage);
            Cleanup();
            _stopping = false;
            RestoreEditor();
            _idle.TrySetResult();
            StateChanged?.Invoke();
        }
    }

    public async Task<bool> PrepareForExitAsync()
    {
        if (!IsRecording) return true;
        await StopAsync(discard: false);
        return _stopSucceeded;
    }

    private void RestoreEditor()
    {
        var restore = _restoreEditor;
        _restoreEditor = null;
        restore?.Invoke();
    }

    private void ReportPublicationFailure(string path, Exception error)
    {
        string detail = $"The completed recording could not be moved to the output folder. It is kept at:\n{path}\n\n{error.Message}";
        try
        {
            var dialog = new ConfirmDialog(Loc("Str_Tray_SaveFailedTitle"), detail,
                Loc("Str_Btn_Open"), Loc("Str_Btn_OK"))
            {
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            dialog.ShowDialog();
            if (dialog.Confirmed) ShowInFolder(path);
        }
        catch { _tray.ShowWarning(Loc("Str_Tray_SaveFailedTitle"), detail); }
    }

    private void CleanupStaging(bool retainFile = false)
    {
        string? path = _path;
        string? folder = _stagingFolder;
        _path = _stagingFolder = _outputFolder = _outputName = null;
        if (retainFile) return;
        TryDelete(path);
        if (folder != null) { try { Directory.Delete(folder); } catch { } }
    }

    private void Cleanup()
    {
        var tick = _tick;
        _tick = null;
        var chrome = _chrome;
        _chrome = null;
        try { tick?.Stop(); } catch { }
        try { chrome?.Close(); } catch { }
        try { _tray.SetRecording(false); } catch { }
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
