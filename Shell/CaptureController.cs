using System.IO;
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
    private readonly VideoController? _video;
    private bool _busy;

    public BitmapSource? Last { get; private set; }

    /// <summary>Raised on the UI thread with each finished capture and when it was taken.</summary>
    public event Action<BitmapSource, DateTime, string?, bool>? Captured;

    /// <summary>The file the last capture was saved to, when saving to the folder is on.</summary>
    public string? LastSavedPath { get; private set; }

    /// <summary>Hides the editor before the desktop is frozen; returns how to bring it back.</summary>
    public Func<Task<Action?>>? HideEditor { get; set; }

    private static string Loc(string key) => LocaleManager.Loc(key);

    public CaptureController(TrayHost tray, VideoController? video = null)
    {
        _tray = tray;
        _video = video;
        _tray.CaptureRequested += (action, mode, fg) =>
        {
            if (action == ShortcutAction.Video) App.Instance.Video?.Toggle(fromShortcut: true);
            else if (action is { } shortcut) _ = StartFromShortcut(shortcut, fg);
            else _ = RunAsync(null, mode, fg);
        };
        _tray.SaveLastRequested += SaveLast;
        _tray.PrintLastRequested += PrintLast;
        _tray.HasLastCapture = () => Last != null;
    }

    /// <summary>Starts a capture from the editor or the tray menu.</summary>
    public Task Start(CaptureMode mode) => RunAsync(null, mode, IntPtr.Zero);

    public Task StartFromShortcut(ShortcutAction action, IntPtr foreground) => RunAsync(action, null, foreground);

    public Task StartVideo(bool fromShortcut) => RunAsync(fromShortcut ? ShortcutAction.Video : null, CaptureMode.Region, IntPtr.Zero, video: true);

    internal static CaptureMode Resolve(ShortcutAction? action, CaptureMode? mode) => mode ?? action switch
    {
        ShortcutAction.Window => CaptureMode.Window,
        ShortcutAction.Monitor => CaptureMode.Monitor,
        ShortcutAction.AllMonitors => CaptureMode.AllMonitors,
        _ => CaptureMode.Region,
    };

    private async Task RunAsync(ShortcutAction? action, CaptureMode? requested, IntPtr foreground, bool video = false)
    {
        if (_busy || (_video ?? App.Instance.Video)?.IsRecording == true) return;
        _busy = true;
        Action? restore = null;
        try
        {
            var mode = Resolve(action, requested);
            // A global shortcut preserves the desktop, including the editor's open menus.
            // Commands started inside the editor hide it before capturing other applications.
            if (action == null && HideEditor != null) restore = await HideEditor();
            int delay = CaptureSettings.DelaySeconds;
            if (delay > 0) await Task.Delay(delay * 1000);
            var frozen = FrozenDesktop.Capture(CaptureSettings.IncludeCursor);
            BitmapSource? result;
            // The window the picture came from, for the library's search: the picked or active
            // window, or the topmost window under the middle of a region or under the pointer.
            IntPtr source = IntPtr.Zero;
            if (video || action == ShortcutAction.LastMode || mode is CaptureMode.Region or CaptureMode.Qr
                || mode == CaptureMode.Window && (CaptureSettings.PickWindow || foreground == IntPtr.Zero))
            {
                var picker = new RegionSelector(frozen, showBar: true, mode: mode, video: video);
                var picked = await picker.RunAsync();
                if (picked == null) return;
                mode = picker.Mode;
                if (picker.IsVideo)
                {
                    if ((_video ?? App.Instance.Video) is { } recorder)
                    {
                        var completed = restore;
                        restore = null;
                        await recorder.StartAsync(picked.Value, completed);
                    }
                    return;
                }
                source = picker.PickedWindow != IntPtr.Zero ? picker.PickedWindow
                    : frozen.WindowAt(Center(picked.Value))?.Handle ?? IntPtr.Zero;
                if (mode == CaptureMode.Window && WindowCapture.CapturableRoot(picker.PickedWindow) is var window && window != IntPtr.Zero)
                {
                    var capture = await WindowCapture.CaptureAsync(window, frozen);
                    result = capture.Bitmap;
                    if (!capture.UsedGraphicsCapture && result != null) WarnWindowFallback();
                    source = window;
                }
                else
                {
                    // A region keeps the visible desktop, including menus and overlaps.
                    result = frozen.Crop(picked.Value);
                }
            }
            else switch (mode)
            {
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
                        var capture = await WindowCapture.CaptureAsync(target, frozen);
                        result = capture.Bitmap;
                        if (!capture.UsedGraphicsCapture && result != null) WarnWindowFallback();
                        source = target;
                    }
                    break;
                case CaptureMode.Monitor:
                    Win32.GetCursorPos(out var p);
                    result = frozen.Crop(frozen.MonitorAt(p).Bounds);
                    source = frozen.WindowAt(p)?.Handle ?? IntPtr.Zero;
                    break;
                default:
                    result = frozen.Crop(frozen.Bounds);
                    source = foreground;
                    break;
            }

            if (result == null) return;   // cancelled: no output, last mode unchanged
            if (CaptureSettings.PlaySound) ShutterSound.Play();
            CaptureSettings.LastMode = mode;
            Last = result;
            var taken = DateTime.Now;
            // A locked or failing album holds only so much; past that the capture still goes to
            // the clipboard, folder and editor, but the album keeps what it already holds.
            if (!CaptureLibrary.Add(result, taken, ModeKey(mode),
                source != IntPtr.Zero ? Win32.WindowTitle(source) : null, Win32.ProcessName(source)))
                _tray.ShowWarning(Loc("Str_Lib_PendingFullTitle"), string.Format(Loc("Str_Lib_PendingFull"), CaptureLibrary.PendingCount));

            bool wantCopy = CaptureSettings.CopyToClipboard;
            bool copied = false;
            string? error = null;
            if (wantCopy) copied = ImageOutput.TryCopy(result, out error);

            string? saved = null;
            if (CaptureSettings.SaveToFolder)
            {
                try
                {
                    string ext = CaptureSettings.Format;
                    string name = OutputFiles.FileName(CaptureSettings.FileNamePattern, taken, mode, result.PixelWidth, result.PixelHeight, ext);
                    saved = ImageOutput.SaveUnique(result, CaptureSettings.OutputFolder, name, CaptureSettings.JpegQuality);
                    LastSavedPath = saved;
                }
                catch (Exception ex)
                {
                    saved = null;
                    _tray.ShowWarning(Loc("Str_Tray_SaveFailedTitle"), ex.Message);
                }
            }

            if (CaptureSettings.AskToSave)
            {
                var asked = AskAndSave(result, taken, mode);
                if (asked != null) saved = asked;
            }

            Captured?.Invoke(result, taken, saved, mode == CaptureMode.Qr);
            if (wantCopy && !copied)
                _tray.ShowWarning(Loc("Str_Tray_ClipboardBusyTitle"), string.Format(Loc("Str_Tray_ClipboardBusy"), error));
            else if (CaptureSettings.Notify && (copied || saved != null))
            {
                string title = copied && saved != null ? Loc("Str_Tray_CopiedSavedTitle")
                    : copied ? Loc("Str_Tray_CopiedTitle") : Loc("Str_Tray_SavedTitle");
                string body = string.Format(Loc("Str_Tray_Copied"), result.PixelWidth, result.PixelHeight);
                if (saved != null) body += "\n" + Path.GetFileName(saved);
                _tray.ShowInfo(title, body);
            }
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

    private static Win32.POINT Center(Win32.RECT r) => new() { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };

    private void WarnWindowFallback() => _tray.ShowWarning(Loc("Str_Mode_Window"), Loc("Str_Tray_WindowFallback"));

    /// <summary>The mode as the library and the command line name it.</summary>
    internal static string ModeKey(CaptureMode mode) => mode switch
    {
        CaptureMode.Region => "region",
        CaptureMode.Window => "window",
        CaptureMode.Monitor => "monitor",
        CaptureMode.Qr => "qr",
        _ => "all",
    };

    /// <summary>The save dialog for one capture, starting in the output folder with the pattern's name.</summary>
    private string? AskAndSave(BitmapSource bmp, DateTime taken, CaptureMode mode)
    {
        var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = Loc("Str_Dlg_SaveTitle"),
            Filter = Loc("Str_Dlg_ImageFilter"),
            FilterIndex = CaptureSettings.Format == "jpg" ? 2 : 1,
            DefaultExt = "." + CaptureSettings.Format,
            FileName = OutputFiles.FileName(CaptureSettings.FileNamePattern, taken, mode, bmp.PixelWidth, bmp.PixelHeight, CaptureSettings.Format),
            InitialDirectory = CaptureSettings.OutputFolder,
        };
        if (dlg.ShowDialog(null) != true) return null;
        try
        {
            ImageOutput.Save(bmp, dlg.FileName, CaptureSettings.JpegQuality);
            LastSavedPath = dlg.FileName;
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            _tray.ShowWarning(Loc("Str_Tray_SaveFailedTitle"), ex.Message);
            return null;
        }
    }

    /// <summary>Prints the last capture from the tray.</summary>
    public void PrintLast()
    {
        if (Last == null) return;
        try { PrintOutput.Print(Last, "KillerShot"); }
        catch (Exception ex) { _tray.ShowWarning(Loc("Str_Tray_PrintFailedTitle"), ex.Message); }
    }

    private void SaveLast()
    {
        // Hold this capture: a global shortcut can take another one while the dialog is open.
        if (Last is not { } image) return;
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
            ImageOutput.Save(image, dlg.FileName);
            _tray.ShowInfo(Loc("Str_Tray_SavedTitle"), dlg.FileName);
        }
        catch (Exception ex)
        {
            _tray.ShowWarning(Loc("Str_Tray_SaveFailedTitle"), ex.Message);
        }
    }
}
