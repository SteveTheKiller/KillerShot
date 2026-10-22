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

    /// <summary>For the self-test: a capture controller wired like the app's own.</summary>
    internal CaptureController TestUseCapture(CaptureController capture)
    {
        Capture = capture;
        capture.Captured += OnCaptured;
        capture.ReviewRequested += OnReviewRequested;
        return capture;
    }
    internal VideoController? Video { get; private set; }

    /// <summary>The picture in the editor. It lives here rather than in the window, so closing the
    /// editor keeps the work and its undo history until the next capture replaces it.</summary>
    internal EditorDocument? Document { get; private set; }

    private AlbumMarks? _marks;
    /// <summary>Keeps the open capture's marks in the album; null in self-tests that never show a document.</summary>
    private AlbumMarks Marks => _marks ??= new AlbumMarks(Dispatcher);
    internal event Action? DocumentChanged;

    /// <summary>Set by KillerShot.Cli.exe before it runs the application, so the console build
    /// takes the command line path without needing the --cli switch.</summary>
    internal static string[]? PendingCli { get; set; }

    /// <summary>Atomic, individually acknowledged image requests from secondary launches.</summary>
    private static readonly ImageOpenRequests OpenRequests = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", "OpenRequests"));

    protected override void OnStartup(StartupEventArgs e)
    {
        // The deferred uninstall helper and the install relaunch helper run before anything else,
        // so neither touches settings, the album or the single-instance claim.
        string runningExe = Environment.ProcessPath ?? "";
        if (DeferredUninstall.IsHelperRequest(e.Args, runningExe, MachineInstallDir, UninstallStagingRoot(false)))
        {
            Shutdown(e.Args.Length > 0 && e.Args[0] == DeferredUninstall.HelperArgument
                ? RunUninstallHelper(e.Args) : KillerFamily.Deployment.InstallerExitCodes.SecurityRejected);
            return;
        }
        if (new InstallRelaunch().TryRunHelper(e.Args, runningExe, out int helperExit))
        {
            Shutdown(helperExit);
            return;
        }

        KillerFamily.Accessibility.AccessibleControls.Initialize();
        KillerFamily.Theming.AccentShadow.Initialize();
        base.OnStartup(e);

        // The command line runs in its own process without the tray, the shortcuts or the
        // single-instance claim, so scripts and KillerMCP can use it beside a running KillerShot.
        string[]? cli = PendingCli ?? KillerFamily.CommandLine.CommandLineEntry.GetArguments(e.Args,
            ["capture", "qr", "record", "recent", "open", "monitors", "windows", "settings", "version", "--version"]);
        if (cli != null)
        {
            Cli.Program.Start(cli, attachConsole: PendingCli == null, Shutdown);
            return;
        }

        if (SelfTest.TryRun(e.Args, out int code)) { Shutdown(code); return; }
        // The interface self-test runs without the tray, the shortcuts or the single-instance
        // claim, so it can run beside a working copy of KillerShot.
        if (SelfTestUi.TryRun(e.Args)) return;
        if (SelfTestChromeInteraction.TryRun(e.Args)) return;
        if (SelfTestReleaseSafety.TryRun(e.Args)) return;
        if (SelfTestExternalAlbumChanges.TryRun(e.Args)) return;
        if (SelfTestAccessiblePickers.TryRun(e.Args)) return;
        if (SelfTestEditorSafety.TryRun(e.Args)) return;
        if (SelfTestRecordingSafety.TryRun(e.Args)) return;
        if (SelfTestLibrary.TryRun(e.Args)) return;
        if (SelfTestQr.TryRun(e.Args)) return;
        if (SelfTestQrUi.TryRun(e.Args)) return;
        if (SelfTestCarousel.TryRun(e.Args)) return;
        if (SelfTestImageMenus.TryRun(e.Args)) return;
        if (SelfTestCaptureMenus.TryRun(e.Args)) return;
        if (SelfTestOutputFiles.TryRun(e.Args)) return;
        if (SelfTestSidebarSelection.TryRun(e.Args)) return;
        if (SelfTestMenuOverflow.TryRun(e.Args)) return;
        if (SelfTestFlyoutScale.TryRun(e.Args)) return;
        if (SelfTestGifDuration.TryRun(e.Args)) return;
        if (SelfTestRedaction.TryRun(e.Args)) return;
        if (SelfTestOutputIdentity.TryRun(e.Args)) return;
        if (SelfTestImageImport.TryRun(e.Args)) return;
        if (SelfTestGestureLifetime.TryRun(e.Args)) return;
        if (SelfTestEditorKeyboard.TryRun(e.Args)) return;
        if (SelfTestCarouselAutomation.TryRun(e.Args)) return;
        if (SelfTestAlbumResponsive.TryRun(e.Args)) return;
        if (SelfTestEditorTools.TryRun(e.Args)) return;
        if (SelfTestEditorObjects.TryRun(e.Args)) return;
        if (SelfTestAlbumMarks.TryRun(e.Args)) return;
        if (SelfTestEditorTransforms.TryRun(e.Args)) return;
        if (SelfTestPasteImages.TryRun(e.Args)) return;
        if (SelfTestRegions.TryRun(e.Args)) return;
        if (SelfTestTextRecognition.TryRun(e.Args)) return;
        if (SelfTestReviewFirst.TryRun(e.Args)) return;
        if (SelfTestScrollStitch.TryRun(e.Args)) return;
        if (SelfTestScrollCapture.TryRun(e.Args)) return;
        if (SelfTestGdiLifetime.TryRun(e.Args)) return;
        if (SelfTestCursorOverride.TryRun(e.Args)) return;
        if (SelfTestDragCleanup.TryRun(e.Args)) return;
        if (SelfTestWorkspaceAreas.TryRun(e.Args)) return;
        if (SelfTestFileDialog.TryRun(e.Args)) return;
        if (SelfTestReviewFixes.TryRun(e.Args)) return;
        if (SelfTestPileEdges.TryRun(e.Args)) return;
        if (SelfTestDemo.TryRun(e.Args)) return;

        // Install switches. /silent installs without UI (machine-wide with admin rights, for the
        // current user without); Add/Remove Programs runs /uninstall and package managers
        // /uninstall-silent. These run beside a live KillerShot, so they come before the
        // single-instance claim.
        if (e.Args.Length > 0 && TryRunInstallSwitch(e.Args, out int installExit))
        {
            Shutdown(installExit);
            return;
        }
        // --demo: a scratch library of real app screenshots (DemoMode.cs), beside a working copy.
        bool demo = DemoMode.TryEnter(e.Args);

        // One instance per user session. A second launch asks the first to open its editor.
        bool first = true;
        if (!demo) _instance = new Mutex(true, @"Local\KillerShot-SingleInstance", out first);
        string? openPath = OpenArgument(e.Args);
        if (!first)
        {
            _instance!.Dispose();
            _instance = null;
            _ = DeliverSecondaryOpenAsync(openPath);
            return;
        }
        if (!demo)
        {
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KillerShot-Wake");
            _wakeWait = ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    OpenRequested();
                    ShowEditor();
                }),
                null, Timeout.Infinite, false);
        }

        InitializeKit();
        if (!demo) OfferInstallConflictRepair();
        ImageDrag.Initialize();
        // The encrypted library opens with the Windows-account key without a prompt, off this
        // thread so the editor paints first. With a password set it stays locked until the
        // library screen unlocks it.
        if (demo) DemoMode.BeginSeed();
        else CaptureLibrary.BeginOpen();

        Tray = new TrayHost();
        CaptureLibrary.WriteFailed += error => Dispatcher.BeginInvoke(new Action(() =>
            Tray?.ShowWarning(LocaleManager.Loc("Str_Tray_SaveFailedTitle"), error)));
        Capture = new CaptureController(Tray) { HideEditor = HideEditorForCapture };
        Capture.Captured += OnCaptured;
        Capture.ReviewRequested += OnReviewRequested;
        Video = new VideoController(Tray);
        Tray.RecordRequested += () => Video.Toggle();
        Tray.OpenEditorRequested += ShowEditor;
        Tray.BalloonClicked += ShowEditor;
        // The demo leaves the notification area, the global shortcuts and the Run value to the
        // real KillerShot.
        if (!demo)
        {
            Tray.Start();
            // Start with Windows (About card): keep the Run value pointing at this exe.
            KillerFamily.Deployment.StartupRegistration.Refresh(KillerShot.Services.AppInfo.DisplayName,
                                                                KillerShot.Features.AboutController.StartArgument);
        }

        // Started with Windows: stay in the notification area. Started by hand or asked to open
        // an image: show the editor.
        if (openPath != null) OpenImage(openPath);
        if (!demo) OpenRequested();
        if (demo)
        {
            // With no tray icon to come back to, closing the demo window quits.
            ShowEditor();
            if (EditorWindow is { } window) window.Closed += (_, _) => Shutdown();
        }
        else if (openPath != null || !e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
            ShowEditor();
    }

    /// <summary>The path after --open, as a full path, or null.</summary>
    private static string? OpenArgument(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return null;
        if (IsLibraryReference(args[i + 1])) return args[i + 1].Trim();
        try { return Path.GetFullPath(args[i + 1]); } catch { return null; }
    }

    /// <summary>Opens the image a second launch left behind, if there is one.</summary>
    private void OpenRequested()
    {
        try
        {
            OpenRequests.Drain(path => { if (path.Length > 0) OpenImage(path); ShowEditor(); });
            if (!OpenRequests.IsDraining && OpenRequests.HasPending)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(OpenRequested));
        }
        catch (Exception ex) { Tray?.ShowWarning(LocaleManager.Loc("Str_Tray_FailedTitle"), ex.Message); }
    }

    private async Task DeliverSecondaryOpenAsync(string? path)
    {
        int code = 0;
        try { await OpenRequests.SendAsync(path ?? "", @"Local\KillerShot-Wake", TimeSpan.FromSeconds(10)); }
        catch (Exception ex)
        {
            code = 1;
            InitializeKit();
            new Controls.ConfirmDialog(LocaleManager.Loc("Str_Tray_FailedTitle"), ex.Message,
                LocaleManager.Loc("Str_Btn_OK"), LocaleManager.Loc("Str_Btn_Cancel")).ShowDialog();
        }
        Shutdown(code);
    }

    /// <summary>Loads an image file or an album-qualified capture reference into the editor.</summary>
    internal void OpenImage(string path)
    {
        try
        {
            if (IsLibraryReference(path))
            {
                CaptureLibrary.Opening.Wait();
                if (!CaptureLibrary.IsOpen) throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_LockedTitle"));
                var item = CaptureLibrary.ResolveReference(path)
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

    private static bool IsLibraryReference(string text) =>
        text.Trim().StartsWith("library:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a library capture in the editor. It opens as a capture, not a file, so Save
    /// asks where to put it.</summary>
    internal void OpenLibraryCapture(CaptureItem item)
    {
        // A picture from the Screenshots folder opens as the file it is, so Save writes it back.
        if (FolderCaptures.IsFolderId(item.Id) && FolderCaptures.PathOf(item.Id) is { } path)
        {
            _opening = item;
            try { OpenImage(path); } finally { _opening = null; }
            return;
        }
        var bmp = CaptureLibrary.LoadImage(item);
        // A capture edited before opens with its marks, still editable.
        var restored = CaptureLibrary.LoadMarks(item.Id, bmp);
        _opening = item;
        SetDocument(restored != null ? EditorDocument.Restore(bmp, restored, item.Taken) : new EditorDocument(bmp, null, item.Taken));
    }

    /// <summary>The library or Screenshots folder picture in the editor, or null for a new
    /// capture or another file. The collection strip follows it.</summary>
    internal CaptureItem? CurrentItem { get; private set; }
    private CaptureItem? _opening;

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
        Controls.MenuIcons.Install();
    }

    private void OnCaptured(BitmapSource bmp, DateTime taken, string? savedPath, bool readQr)
    {
        // A capture saved to the folder opens as that file, so Save in the editor updates it.
        SetDocument(new EditorDocument(bmp, savedPath, taken) { ReadQr = readQr });
        if (readQr || CaptureSettings.OpenEditorAfterCapture) ShowEditor();
    }

    /// <summary>Review first: the capture opens in the editor and waits there to be accepted.</summary>
    private void OnReviewRequested(BitmapSource bmp, DateTime taken, KillerShot.Capture.CaptureMode mode)
    {
        var doc = new EditorDocument(bmp, null, taken) { ReadQr = mode == KillerShot.Capture.CaptureMode.Qr };
        if (!SetDocument(doc)) return;
        ShowEditor();
        _editor?.BeginReview(doc, taken, mode);
    }

    internal bool SetDocument(EditorDocument? doc)
    {
        var opening = _opening;
        _opening = null;
        if (!ReferenceEquals(Document, doc) && !ConfirmDocumentReplacement()) return false;
        Marks.Flush();
        CurrentItem = doc != null ? opening : null;
        Document = doc;
        Marks.Attach(doc, opening != null && doc != null && !FolderCaptures.IsFolderId(opening.Id) ? opening.Id : null);
        DocumentChanged?.Invoke();
        return true;
    }

    internal bool ConfirmDocumentReplacement()
    {
        _editor?.CommitPendingText();
        if (Document?.Dirty != true) return true;
        // An album capture keeps its marks in the album, so replacing it loses nothing.
        if (Document.FilePath == null && Marks.CaptureId != null) { Marks.Flush(); return true; }
        var expectedDocument = Document;
        ShowEditor();
        var dialog = new Controls.ConfirmDialog(LocaleManager.Loc("Str_Strip_DiscardTitle"),
            LocaleManager.Loc("Str_Strip_DiscardDetail"), LocaleManager.Loc("Str_Strip_Discard"),
            LocaleManager.Loc("Str_Btn_Cancel")) { Owner = _editor };
        dialog.ShowDialog();
        return dialog.Confirmed && ReferenceEquals(Document, expectedDocument);
    }

    internal void CompleteAlbumSwitch()
    {
        CurrentItem = null;
        _opening = null;
        Marks.Attach(null, null);
        Document = null;
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
                if (!CloseToTray && !IsExiting) Dispatcher.BeginInvoke(new Action(RequestExit));
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
            if (_editor != w || w.IsVisible) return;
            w.Show();
            w.Activate();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ImageDrag.Cleanup();
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

    private bool _exitRequested;
    internal bool IsExiting { get; private set; }

    internal async Task<bool> CanExitAsync()
    {
        _editor?.CommitPendingText();
        if (Document is { Dirty: true, FilePath: null } && Marks.CaptureId != null) Marks.Flush();
        if (Document?.Dirty == true && !(Document.FilePath == null && Marks.CaptureId != null))
        {
            var expected = Document;
            ShowEditor();
            var edits = new Controls.ConfirmDialog(LocaleManager.Loc("Str_Tray_Exit"),
                LocaleManager.Loc("Str_Strip_DiscardDetail"), LocaleManager.Loc("Str_Btn_Save"),
                LocaleManager.Loc("Str_Btn_Cancel")) { Owner = _editor };
            edits.SetAlternative(LocaleManager.Loc("Str_Rec_Cancel"));
            edits.ShowDialog();
            if (!ReferenceEquals(Document, expected)) return false;
            if (edits.Confirmed)
            {
                _editor!.SaveDocumentForExit();
                if (Document.Dirty) return false;
            }
            else if (!edits.AlternativeChosen) return false;
        }
        if (Video != null && !await Video.PrepareForExitAsync()) return false;
        await CaptureLibrary.RetryPendingAsync();
        int pending = CaptureLibrary.PendingCount;
        if (pending == 0) return true;
        ShowEditor();
        var dialog = new Controls.ConfirmDialog(LocaleManager.Loc("Str_Tray_SaveFailedTitle"),
            string.Format(LocaleManager.Loc("Str_Lib_PendingExit"), pending),
            LocaleManager.Loc("Str_Tray_Exit"), LocaleManager.Loc("Str_Btn_Cancel")) { Owner = _editor };
        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    internal async void RequestExit()
    {
        if (_exitRequested || IsExiting) return;
        _exitRequested = true;
        try
        {
            if (!await CanExitAsync()) return;
            IsExiting = true;
            Shutdown();
        }
        finally { _exitRequested = false; }
    }
}
