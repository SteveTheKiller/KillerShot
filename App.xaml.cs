using System.Windows;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;
using KillerShot.Shell;

namespace KillerShot;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _wake;
    private RegisteredWaitHandle? _wakeWait;
    private MainWindow? _editor;

    internal static App Instance => (App)Current;
    internal TrayHost? Tray { get; private set; }
    internal CaptureController? Capture { get; private set; }
    internal VideoController? Video { get; private set; }

    /// <summary>The picture in the editor. It lives here rather than in the window, so closing the
    /// editor keeps the work and its undo history until the next capture replaces it.</summary>
    internal EditorDocument? Document { get; private set; }
    internal event Action? DocumentChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (SelfTest.TryRun(e.Args, out int code)) { Shutdown(code); return; }
        // The interface self-test runs without the tray, the shortcuts or the single-instance
        // claim, so it can run beside a working copy of KillerShot.
        if (SelfTestUi.TryRun(e.Args)) return;

        // One instance per user session. A second launch asks the first to open its editor.
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
        _wakeWait = ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) => Dispatcher.BeginInvoke(ShowEditor),
            null, Timeout.Infinite, false);

        InitializeKit();

        Tray = new TrayHost();
        Capture = new CaptureController(Tray) { HideEditor = HideEditorForCapture };
        Capture.Captured += OnCaptured;
        Video = new VideoController(Tray) { HideEditor = HideEditorForCapture };
        Tray.RecordRequested += () => Video.Toggle();
        Tray.OpenEditorRequested += ShowEditor;
        Tray.BalloonClicked += ShowEditor;
        Tray.Start();

        // Started with Windows: stay in the notification area. Started by hand: show the editor.
        if (!e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
            ShowEditor();
    }

    /// <summary>Theme, locale, grain and rendering, in the order the family kit expects, before
    /// any window exists.</summary>
    internal static void InitializeKit()
    {
        // Every window takes the family UI face from the active theme unless it sets its own.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w && DependencyPropertyHelper.GetValueSource(w, System.Windows.Controls.Control.FontFamilyProperty).BaseValueSource == BaseValueSource.Default)
                w.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "UiFont");
        }));
        RenderModeService.Apply();
        ThemeManager.GetSetting = Settings.Get;
        ThemeManager.SetSetting = Settings.Set;
        ThemeManager.Initialize();
        FilmGrain.Initialize();
        Controls.GrainTexture.Publish();
        LocaleManager.GetSetting = Settings.Get;
        LocaleManager.SetSetting = Settings.Set;
        LocaleManager.Initialize();
    }

    private void OnCaptured(BitmapSource bmp, DateTime taken, string? savedPath)
    {
        // A capture saved to the folder opens as that file, so Save in the editor updates it.
        SetDocument(new EditorDocument(bmp, savedPath, taken));
        if (CaptureSettings.OpenEditorAfterCapture) ShowEditor();
    }

    internal void SetDocument(EditorDocument? doc)
    {
        Document = doc;
        DocumentChanged?.Invoke();
    }

    internal void ShowEditor()
    {
        if (_editor == null)
        {
            _editor = new MainWindow();
            _editor.Closed += (_, _) => _editor = null;
            _editor.Show();
        }
        if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
        if (!_editor.IsVisible) _editor.Show();
        _editor.Activate();
        _editor.Topmost = true;
        _editor.Topmost = false;   // foreground nudge past the focus rules
    }

    internal MainWindow? EditorWindow => _editor;

    /// <summary>Hides a visible editor and waits for the desktop to repaint without it.</summary>
    private async Task<Action?> HideEditorForCapture()
    {
        var w = _editor;
        if (w == null || !w.IsVisible || w.WindowState == WindowState.Minimized) return null;
        w.Hide();
        await Task.Delay(220);
        return () =>
        {
            if (_editor != w) return;
            w.Show();
            w.Activate();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeWait?.Unregister(null);
        _wake?.Dispose();
        Tray?.Dispose();
        if (_instance != null)
        {
            try { _instance.ReleaseMutex(); } catch { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }
}
