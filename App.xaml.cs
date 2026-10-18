using System.IO;
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

    /// <summary>Set by KillerShot.Cli.exe before it runs the application, so the console build
    /// takes the command line path without needing the --cli switch.</summary>
    internal static string[]? PendingCli { get; set; }

    /// <summary>Where a second launch leaves the image it was asked to open, for the running
    /// instance to pick up when the wake event arrives.</summary>
    internal static readonly string OpenRequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", "open-request.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        KillerFamily.Accessibility.AccessibleControls.Initialize();
        base.OnStartup(e);

        // The command line runs in its own process without the tray, the shortcuts or the
        // single-instance claim, so scripts and KillerMCP can use it beside a running KillerShot.
        string[]? cli = PendingCli ?? (e.Args.Length > 0 && e.Args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase) ? e.Args[1..] : null);
        if (cli != null)
        {
            Cli.Program.Start(cli, attachConsole: PendingCli == null, Shutdown);
            return;
        }

        if (SelfTest.TryRun(e.Args, out int code)) { Shutdown(code); return; }
        // The interface self-test runs without the tray, the shortcuts or the single-instance
        // claim, so it can run beside a working copy of KillerShot.
        if (SelfTestUi.TryRun(e.Args)) return;
        if (SelfTestLibrary.TryRun(e.Args)) return;

        // One instance per user session. A second launch asks the first to open its editor.
        _instance = new Mutex(true, @"Local\KillerShot-SingleInstance", out bool first);
        string? openPath = OpenArgument(e.Args);
        if (!first)
        {
            if (openPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OpenRequestPath)!);
                    File.WriteAllText(OpenRequestPath, openPath);
                }
                catch { }
            }
            try { using var h = EventWaitHandle.OpenExisting(@"Local\KillerShot-Wake"); h.Set(); } catch { }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KillerShot-Wake");
        _wakeWait = ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) => Dispatcher.BeginInvoke(() =>
            {
                OpenRequested();
                ShowEditor();
            }),
            null, Timeout.Infinite, false);

        InitializeKit();
        // The encrypted library opens with the Windows-account key without a prompt, off this
        // thread so the editor paints first. With a password set it stays locked until the
        // library screen unlocks it.
        CaptureLibrary.BeginOpen();

        Tray = new TrayHost();
        Capture = new CaptureController(Tray) { HideEditor = HideEditorForCapture };
        Capture.Captured += OnCaptured;
        Video = new VideoController(Tray) { HideEditor = HideEditorForCapture };
        Tray.RecordRequested += () => Video.Toggle();
        Tray.OpenEditorRequested += ShowEditor;
        Tray.BalloonClicked += ShowEditor;
        Tray.Start();

        // Started with Windows: stay in the notification area. Started by hand or asked to open
        // an image: show the editor.
        if (openPath != null) OpenImage(openPath);
        if (openPath != null || !e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
            ShowEditor();
    }

    /// <summary>The path after --open, as a full path, or null.</summary>
    private static string? OpenArgument(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return null;
        if (LibraryId(args[i + 1]) != null) return args[i + 1].Trim();
        try { return Path.GetFullPath(args[i + 1]); } catch { return null; }
    }

    /// <summary>Opens the image a second launch left behind, if there is one.</summary>
    private void OpenRequested()
    {
        string? path = null;
        try
        {
            if (!File.Exists(OpenRequestPath)) return;
            path = File.ReadAllText(OpenRequestPath).Trim();
            File.Delete(OpenRequestPath);
        }
        catch { }
        if (!string.IsNullOrEmpty(path)) OpenImage(path);
    }

    /// <summary>Loads an image file, or a library capture named library:id, into the editor.</summary>
    private void OpenImage(string path)
    {
        try
        {
            if (LibraryId(path) is long id)
            {
                CaptureLibrary.Opening.Wait();
                if (!CaptureLibrary.IsOpen) throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_LockedTitle"));
                var item = CaptureLibrary.Items().FirstOrDefault(c => c.Id == id)
                    ?? throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_Missing"));
                OpenLibraryCapture(item);
                return;
            }
            var bmp = ImageOutput.Load(path);
            SetDocument(new EditorDocument(bmp, path, File.GetLastWriteTime(path)));
        }
        catch (Exception ex)
        {
            Tray?.ShowWarning(LocaleManager.Loc("Str_Tray_FailedTitle"), ex.Message);
        }
    }

    /// <summary>The id in a library:id reference, or null.</summary>
    private static long? LibraryId(string text) =>
        text.Trim().StartsWith("library:", StringComparison.OrdinalIgnoreCase) &&
        long.TryParse(text.Trim()[8..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
            ? id : null;

    /// <summary>Opens a library capture in the editor. It opens as a capture, not a file, so Save
    /// asks where to put it.</summary>
    internal void OpenLibraryCapture(CaptureItem item)
    {
        // A picture from the Screenshots folder opens as the file it is, so Save writes it back.
        if (FolderCaptures.IsFolderId(item.Id) && FolderCaptures.PathOf(item.Id) is { } path)
        {
            OpenImage(path);
            return;
        }
        var bmp = CaptureLibrary.LoadImage(item.Id);
        SetDocument(new EditorDocument(bmp, null, item.Taken));
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
            _editor.Closed += (_, _) =>
            {
                _editor = null;
                // With Close to system tray off, closing the window quits. Queued, so an Exit from
                // the tray menu, which closes the window on its way out, is not shut down twice.
                if (!CloseToTray) Dispatcher.BeginInvoke(new Action(() => Shutdown()));
            };
            _editor.Show();
        }
        if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
        if (!_editor.IsVisible) _editor.Show();
        _editor.Activate();
        _editor.Topmost = true;
        _editor.Topmost = false;   // foreground nudge past the focus rules
    }

    internal MainWindow? EditorWindow => _editor;

    /// <summary>Whether closing the editor leaves KillerShot running in the notification area,
    /// which keeps the capture shortcuts working. On unless turned off in About.</summary>
    internal static bool CloseToTray
    {
        get => Settings.GetBool("CloseToTray", true);
        set => Settings.SetBool("CloseToTray", value);
    }

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
