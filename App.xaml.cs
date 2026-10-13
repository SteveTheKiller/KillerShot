using System.Windows;
using KillerShot.Services;
using KillerShot.Shell;

namespace KillerShot;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _wake;
    private RegisteredWaitHandle? _wakeWait;
    private TrayHost? _tray;
    private CaptureController? _capture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (SelfTest.TryRun(e.Args, out int code)) { Shutdown(code); return; }

        // One tray instance per user session. A second launch tells the first one and exits.
        _instance = new Mutex(true, @"Local\KillerShot-SingleInstance", out bool first);
        if (!first)
        {
            try { using var h = EventWaitHandle.OpenExisting(@"Local\KillerShot-Wake"); h.Set(); } catch { }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KillerShot-Wake");
        _wakeWait = ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) =>
            Dispatcher.BeginInvoke(() => _tray?.ShowInfo("KillerShot is already running", "Use Print Screen, or the KillerShot icon in the notification area.")),
            null, Timeout.Infinite, false);

        Settings.Load();
        _tray = new TrayHost();
        _capture = new CaptureController(_tray);
        _tray.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeWait?.Unregister(null);
        _wake?.Dispose();
        _tray?.Dispose();
        if (_instance != null)
        {
            try { _instance.ReleaseMutex(); } catch { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }
}
