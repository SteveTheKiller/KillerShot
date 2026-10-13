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

    public CaptureController(TrayHost tray)
    {
        _tray = tray;
        _tray.CaptureRequested += (action, mode, fg) => _ = RunAsync(action, mode, fg);
        _tray.SaveLastRequested += SaveLast;
        _tray.HasLastCapture = () => Last != null;
    }

    private static CaptureMode Resolve(ShortcutAction? action, CaptureMode? mode) => mode ?? action switch
    {
        ShortcutAction.Window => CaptureMode.Window,
        ShortcutAction.Monitor => CaptureMode.Monitor,
        ShortcutAction.AllMonitors => CaptureMode.AllMonitors,
        _ => Settings.Current.LastMode,
    };

    private async Task RunAsync(ShortcutAction? action, CaptureMode? requested, IntPtr foreground)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var mode = Resolve(action, requested);
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
            Settings.Current.LastMode = mode;
            Settings.Save();
            Last = result;
            if (ImageOutput.TryCopy(result, out var error))
                _tray.ShowInfo("Copied to the clipboard", $"{result.PixelWidth} x {result.PixelHeight}");
            else
                _tray.ShowWarning("The clipboard is busy", $"Another program is holding the clipboard ({error}). Use Save last capture from the tray menu, or capture again.");
        }
        catch (Exception ex)
        {
            _tray.ShowWarning("Capture failed", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SaveLast()
    {
        if (Last == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"KillerShot {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ImageOutput.SavePng(Last, dlg.FileName);
            _tray.ShowInfo("Saved", dlg.FileName);
        }
        catch (Exception ex)
        {
            _tray.ShowWarning("Save failed", ex.Message);
        }
    }
}
