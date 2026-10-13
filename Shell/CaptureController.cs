using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Runs one capture at a time: freeze the desktop, pick the pixels for the mode,
/// then copy to the clipboard. A cancelled capture produces no output and no clipboard change.</summary>
internal sealed class CaptureController
{
    private readonly TrayHost _tray;
    private bool _busy;

    public BitmapSource? Last { get; private set; }

    /// <summary>Raised on the UI thread with each finished capture and when it was taken.</summary>
    public event Action<BitmapSource, DateTime>? Captured;

    /// <summary>Hides the editor before the desktop is frozen; returns how to bring it back.</summary>
    public Func<Task<Action?>>? HideEditor { get; set; }

    private static string Loc(string key) => LocaleManager.Loc(key);

    public CaptureController(TrayHost tray)
    {
        _tray = tray;
        _tray.CaptureRequested += (action, mode, fg) => _ = RunAsync(action, mode, fg);
        _tray.SaveLastRequested += SaveLast;
        _tray.HasLastCapture = () => Last != null;
    }

    /// <summary>Starts a capture from the editor or the tray menu.</summary>
    public Task Start(CaptureMode mode) => RunAsync(null, mode, IntPtr.Zero);

    private static CaptureMode Resolve(ShortcutAction? action, CaptureMode? mode) => mode ?? action switch
    {
        ShortcutAction.Window => CaptureMode.Window,
        ShortcutAction.Monitor => CaptureMode.Monitor,
        ShortcutAction.AllMonitors => CaptureMode.AllMonitors,
        _ => CaptureSettings.LastMode,
    };

    private async Task RunAsync(ShortcutAction? action, CaptureMode? requested, IntPtr foreground)
    {
        if (_busy) return;
        _busy = true;
        Action? restore = null;
        try
        {
            var mode = Resolve(action, requested);
            // Our own editor never belongs in the picture.
            if (HideEditor != null) restore = await HideEditor();
            var frozen = FrozenDesktop.Capture();
            BitmapSource? result;
            switch (mode)
            {
                case CaptureMode.Region:
                    var rect = await new RegionSelector(frozen).RunAsync();
                    result = rect is { } r ? frozen.Crop(r) : null;
                    break;
                case CaptureMode.Window:
                    var target = WindowCapture.CapturableRoot(foreground);
                    if (target == IntPtr.Zero)
                    {
                        // Nothing capturable in front (desktop, taskbar, our own window): use the monitor.
                        Win32.GetCursorPos(out var cp);
                        result = frozen.Crop(frozen.MonitorAt(cp).Bounds);
                    }
                    else
                    {
                        result = (await WindowCapture.CaptureAsync(target, frozen)).Bitmap;
                    }
                    break;
                case CaptureMode.Monitor:
                    Win32.GetCursorPos(out var p);
                    result = frozen.Crop(frozen.MonitorAt(p).Bounds);
                    break;
                default:
                    result = frozen.Crop(frozen.Bounds);
                    break;
            }

            if (result == null) return;   // cancelled: no output, last mode unchanged
            CaptureSettings.LastMode = mode;
            Last = result;
            var taken = DateTime.Now;
            RecentCaptures.Add(result, taken);
            bool copied = ImageOutput.TryCopy(result, out var error);
            Captured?.Invoke(result, taken);
            if (!copied)
                _tray.ShowWarning(Loc("Str_Tray_ClipboardBusyTitle"), string.Format(Loc("Str_Tray_ClipboardBusy"), error));
            else if (CaptureSettings.Notify)
                _tray.ShowInfo(Loc("Str_Tray_CopiedTitle"), string.Format(Loc("Str_Tray_Copied"), result.PixelWidth, result.PixelHeight));
        }
        catch (Exception ex)
        {
            _tray.ShowWarning(Loc("Str_Tray_FailedTitle"), ex.Message);
        }
        finally
        {
            _busy = false;
            restore?.Invoke();
        }
    }

    private void SaveLast()
    {
        if (Last == null) return;
        var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = Loc("Str_Dlg_SaveTitle"),
            Filter = Loc("Str_Dlg_ImageFilter"),
            DefaultExt = ".png",
            FileName = $"KillerShot {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(null) != true) return;
        try
        {
            ImageOutput.Save(Last, dlg.FileName);
            _tray.ShowInfo(Loc("Str_Tray_SavedTitle"), dlg.FileName);
        }
        catch (Exception ex)
        {
            _tray.ShowWarning(Loc("Str_Tray_SaveFailedTitle"), ex.Message);
        }
    }
}
