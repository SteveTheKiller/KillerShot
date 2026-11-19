using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using KillerFamily.Controls;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;
using Shortcut = KillerShot.Services.Shortcut;

namespace KillerShot.Shell;

/// <summary>The notification-area icon, its menu and the global shortcuts. A hidden top-level
/// window receives WM_HOTKEY, the icon callback and TaskbarCreated, so the icon comes back after
/// Explorer restarts.</summary>
internal sealed class TrayHost : IDisposable
{
    internal const int CallbackMessage = Win32.WM_APP + 1;
    private const uint IconId = 1;
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly Dictionary<int, ShortcutAction> _hotkeyIds = new();
    private readonly Dictionary<ShortcutAction, Shortcut> _active = new();
    private readonly List<string> _failed = new();
    private readonly HashSet<ShortcutAction> _failedActions = new();
    private bool _iconAdded;
    private bool _suspended;
    /// <summary>The shortcuts on the Print Screen key, which the hook serves when the switch is on.</summary>
    private readonly Dictionary<ShortcutAction, Shortcut> _printScreen = new();
    /// <summary>A copy of <see cref="_printScreen"/> for the hook, which reads it on its own thread
    /// while RegisterShortcuts refills the original.</summary>
    private volatile IReadOnlyDictionary<ShortcutAction, Shortcut> _hookShortcuts = new Dictionary<ShortcutAction, Shortcut>();
    private PrintScreenHook? _printScreenHook;
    /// <summary>The UI Access helper's message, its stop event while it runs, the modifier sets
    /// it was started with, and whether it has reported in (Services/PrintScreenHelper.cs).</summary>
    private readonly uint _helperMessage;
    private EventWaitHandle? _helperStop;
    private string _helperModifiers = "";
    private int _helperRuns;
    private bool _helperListening;

    /// <summary>Raised for a shortcut or a menu command, with the window that was in front.</summary>
    public event Action<ShortcutAction?, CaptureMode?, IntPtr>? CaptureRequested;
    public event Action? SaveLastRequested;
    public event Action? OpenEditorRequested;
    public event Action? PrintLastRequested;
    /// <summary>Record video from the menu, or stop the recording in progress.</summary>
    public event Action? RecordRequested;
    public event Action? PauseRecordingRequested;
    public event Action? DiscardRecordingRequested;
    public Func<bool>? RecordingPaused { get; set; }
    private bool _recording;
    private Action? _balloonAction;
    /// <summary>The user clicked the last notification.</summary>
    public event Action? BalloonClicked;
    public Func<bool>? HasLastCapture { get; set; }
    /// <summary>Repeat, saved region and interval commands, supplied by the capture controller.</summary>
    public Func<Func<ShortcutAction, string>, IReadOnlyList<IReadOnlyList<CardTile>>>? RegionItems { get; set; }

    public IReadOnlyList<string> FailedShortcuts => _failed;
    public bool IsFailed(ShortcutAction action) => _failedActions.Contains(action);

    private static string Loc(string key) => LocaleManager.Loc(key);
    public IntPtr Handle => _source.Handle;

    public TrayHost()
    {
        // WS_POPUP, never shown. A message-only window would miss the TaskbarCreated broadcast.
        var p = new HwndSourceParameters("KillerShotTrayHost") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        _helperMessage = Win32.RegisterWindowMessage(PrintScreenHelper.MessageName);
        // The app icon from the exe's own resources at the small-icon size. The SDK embeds
        // ApplicationIcon as icon group 32512. If that fails, the shared IDI_APPLICATION stands in.
        int size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        _icon = Win32.LoadImage(Win32.GetModuleHandle(null), new IntPtr(32512), Win32.IMAGE_ICON, size, size, 0);
        _ownsIcon = _icon != IntPtr.Zero;
        if (!_ownsIcon) _icon = Win32.LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Start()
    {
        AddIcon();
        RegisterShortcuts();
        WarnIfFailed();
    }

    private void WarnIfFailed()
    {
        if (_failed.Count > 0)
            ShowWarning(Loc("Str_Tray_ShortcutTakenTitle"), string.Format(Loc("Str_Tray_ShortcutTaken"), string.Join(", ", _failed)));
    }

    /// <summary>Releases every global shortcut while a recorder in the settings is listening, so
    /// the keys reach the recorder instead of starting a capture.</summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        foreach (var id in _hotkeyIds.Keys) Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
        // Recording a new shortcut needs Print Screen to reach the recorder.
        UpdatePrintScreenHook();
    }

    internal bool IsSuspended => _suspended;
    internal int TestRegisteredCount => _hotkeyIds.Count;
    internal bool TestRegistered(ShortcutAction action) => _hotkeyIds.ContainsValue(action);

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        RegisterShortcuts();
    }

    public void Relocalize() => UpdateTip();

    public void RegisterShortcuts()
    {
        foreach (var id in _hotkeyIds.Keys) Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
        _active.Clear();
        _failed.Clear();
        _failedActions.Clear();
        _printScreen.Clear();
        if (_suspended) { UpdatePrintScreenHook(); UpdateTip(); return; }

        int next = 1;
        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
        {
            var text = CaptureSettings.ShortcutText(action);
            if (string.IsNullOrWhiteSpace(text)) continue;   // turned off
            if (!Shortcut.TryParse(text, out var sc)) { _failed.Add(text); _failedActions.Add(action); continue; }
            if (sc.Key == Win32.VK_SNAPSHOT) _printScreen[action] = sc;
            int id = next++;
            if (Win32.RegisterHotKey(Handle, id, sc.Modifiers | Win32.MOD_NOREPEAT, sc.Key))
            {
                _hotkeyIds[id] = action;
                _active[action] = sc;
            }
            else
            {
                _failed.Add(sc.Short);
                _failedActions.Add(action);
            }
        }
        UpdatePrintScreenHook();
        // The hook serves Print Screen shortcuts without a registration, so with it on a refused
        // registration is not a lost shortcut, and the tray menu still shows its key.
        if (_printScreenHook?.Installed == true)
            foreach (var (action, sc) in _printScreen)
            {
                if (_failedActions.Remove(action)) _failed.Remove(sc.Short);
                _active[action] = sc;
            }
        UpdateTip();
    }

    /// <summary>The Print Screen hook, on while the switch is on, the shortcuts are claimed (not
    /// paused for recording a new one) and at least one shortcut is on Print Screen.</summary>
    private void UpdatePrintScreenHook()
    {
        bool want = !_suspended && PrintScreenKey.Enabled && _printScreen.Count > 0;
        _hookShortcuts = new Dictionary<ShortcutAction, Shortcut>(_printScreen);
        // A hook Windows refused is tried again on the next claim (the tray's Retry, a settings
        // change) instead of being kept as a dead object until the switch is toggled.
        if (_printScreenHook is { Installed: false })
        {
            _printScreenHook.Dispose();
            _printScreenHook = null;
        }
        if (want && _printScreenHook == null)
        {
            var dispatcher = Application.Current.Dispatcher;
            _printScreenHook = new PrintScreenHook(mods =>
            {
                // On the hook's thread: read the foreground window now, then leave at once. The
                // capture runs on the UI thread after the hook answers.
                if (!PrintScreenHook.Matches(_hookShortcuts, mods, out var action)) return false;
                var fg = Win32.GetForegroundWindow();
                dispatcher.BeginInvoke(new Action(() => CaptureRequested?.Invoke(action, null, fg)));
                return true;
            });
        }
        else if (!want && _printScreenHook != null)
        {
            _printScreenHook.Dispose();
            _printScreenHook = null;
        }
        SyncHelper(want);
    }

    /// <summary>
    /// The UI Access helper, while the switch is on and it is installed: it takes the same Print
    /// Screen shortcuts over windows run as administrator, which Windows hides from the hook above.
    /// The hook stays on beside it, so ordinary windows still work if Windows refuses the helper;
    /// a press reaches only whichever of the two sees it first, so it never runs twice. A change
    /// of shortcuts restarts the helper with the new set.
    /// </summary>
    private void SyncHelper(bool want)
    {
        string modifiers = want
            ? string.Join(",", _hookShortcuts.Values.Select(s => s.Modifiers & ~Win32.MOD_NOREPEAT).Distinct().OrderBy(m => m))
            : "";
        if (modifiers == _helperModifiers && (_helperStop != null || modifiers.Length == 0)) return;
        StopHelper();
        if (modifiers.Length == 0 || !PrintScreenHelper.IsInstalled) return;
        string name = $@"Local\KillerShot.PrintScreen.{Environment.ProcessId}.{++_helperRuns}";
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var helper = PrintScreenHelper.Start(Handle, modifiers, name);
        if (helper == null) { stop.Dispose(); return; }
        _helperStop = stop;
        _helperModifiers = modifiers;
    }

    private void StopHelper()
    {
        _helperListening = false;
        _helperModifiers = "";
        if (_helperStop == null) return;
        _helperStop.Set();
        _helperStop.Dispose();
        _helperStop = null;
    }

    /// <summary>True once the UI Access helper reported it is listening.</summary>
    internal bool HelperListening => _helperListening;
    /// <summary>Print Screen presses the helper delivered, for its self-test.</summary>
    internal int HelperPresses { get; private set; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _hotkeyIds.TryGetValue(wParam.ToInt32(), out var action))
        {
            // Read the foreground window now, before anything of ours can take focus.
            var fg = Win32.GetForegroundWindow();
            CaptureRequested?.Invoke(action, null, fg);
            handled = true;
        }
        else if (msg == CallbackMessage)
        {
            int mouse = lParam.ToInt32() & 0xFFFF;
            if (mouse == Win32.WM_LBUTTONUP)
            {
                if (_recording) RecordRequested?.Invoke();   // a click on the tray stops a recording
                else OpenEditorRequested?.Invoke();
            }
            else if (mouse is Win32.WM_RBUTTONUP or 0x7B) ShowMenu();
            else if (mouse == NIN_BALLOONUSERCLICK)
            {
                if (_balloonAction != null) _balloonAction();
                else BalloonClicked?.Invoke();
            }
            handled = true;
        }
        else if (_helperMessage != 0 && msg == (int)_helperMessage)
        {
            // From the UI Access helper: -1 when it starts listening, otherwise a Print Screen
            // press with these modifiers and the window that was in front.
            long modifiers = wParam.ToInt64();
            if (modifiers == -1) _helperListening = _helperStop != null;
            else if (_helperStop != null && modifiers is >= 0 and <= 0xF
                && PrintScreenHook.Matches(_hookShortcuts, (uint)modifiers, out var pressed))
            {
                HelperPresses++;
                CaptureRequested?.Invoke(pressed, null, lParam);
            }
            handled = true;
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            _iconAdded = false;
            AddIcon();
        }
        return IntPtr.Zero;
    }

    /// <summary>Menu-started captures wait for the menu to fade so it is not frozen into the shot.</summary>
    private void DeferCapture(ShortcutAction? action, CaptureMode? mode)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); CaptureRequested?.Invoke(action, mode, IntPtr.Zero); };
        timer.Start();
    }

    private string Gesture(ShortcutAction action) => _active.TryGetValue(action, out var sc) ? sc.Short : "";
    internal string TestGesture(ShortcutAction action) => Gesture(action);

    private void ShowMenu()
    {
        // The tray card. Bottom-left corner at the pointer, rising out of the tray, as every family
        // app's tray menu opens. Three tiles across, one group per row: Open first, then captures, the
        // regions, recording, the last capture, shortcut repair, and Exit last.
        var menu = ShotCards.Menu();
        KillerFamily.Shell.CloseToTray.AnchorToPointer(menu);
        menu.Items.Add(ShotCards.Header(menu, "KillerShot"));
        static string? Keys(string text) => text.Length == 0 ? null : text;
        CardTile Tile(string glyph, string caption, string gesture, Action click, bool enabled = true)
            => new(glyph, caption, Keys(gesture), click, false, enabled);

        var captures = new List<CardTile>
        {
            Tile("\uE722", Loc("Str_Btn_Capture"), "", () => DeferCapture(null, null)),
            Tile("\uE73F", Loc("Str_Mode_Window"), Gesture(ShortcutAction.Window), () => DeferCapture(null, CaptureMode.Window)),
            Tile("\uE7F4", Loc("Str_Mode_Monitor"), Gesture(ShortcutAction.Monitor), () => DeferCapture(null, CaptureMode.Monitor)),
            Tile("\uE7C4", Loc("Str_Mode_AllMonitors"), Gesture(ShortcutAction.AllMonitors), () => DeferCapture(null, CaptureMode.AllMonitors)),
            Tile("\uED14", Loc("Str_Mode_Qr"), "", () => DeferCapture(null, CaptureMode.Qr)),
        };
        // Open KillerShot leads, as the bold first row did. The Print Screen switch sits beside it, where a tray
        // app's user looks for it: filled while the switch is on, the saved setting, whether or not a hook is
        // live right now. The same switch as About's.
        var groups = new List<IEnumerable<CardTile>>
        {
            new[]
            {
                Tile("\uE8A7", Loc("Str_Tray_Open"), "", () => OpenEditorRequested?.Invoke()),
                new CardTile("\uE765", Loc("Str_Tray_PrintScreenKey"), null, TogglePrintScreenKey)
                    { Checked = PrintScreenKey.Enabled, KeepsOpen = true, Hint = Loc("Str_CS_PrintScreenKey") },
            },
            captures,
        };
        if (RegionItems != null) groups.AddRange(RegionItems(Gesture));
        // Pause and Discard keep their places, dimmed, while nothing is recording.
        bool paused = _recording && RecordingPaused?.Invoke() == true;
        groups.Add(
        [
            Tile(_recording ? "\uE71A" : "\uE7C8", Loc(_recording ? "Str_Tray_StopRecording" : "Str_Tray_Record"), Gesture(ShortcutAction.Video), () =>
            {
                if (_recording) RecordRequested?.Invoke();
                else
                {
                    // Wait for the menu to fade so it is not frozen into the region picker.
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    t.Tick += (_, _) => { t.Stop(); RecordRequested?.Invoke(); };
                    t.Start();
                }
            }),
            Tile(paused ? "\uE768" : "\uE769", Loc(paused ? "Str_Rec_Resume" : "Str_Rec_Pause"), "Ctrl+Shift+R", () => PauseRecordingRequested?.Invoke(), _recording),
            Tile("\uE711", Loc("Str_Rec_Cancel"), "Ctrl+Shift+D", () => DiscardRecordingRequested?.Invoke(), _recording),
        ]);
        bool last = HasLastCapture?.Invoke() == true;
        groups.Add(
        [
            Tile("\uE74E", Loc("Str_Tray_SaveLast"), "", () => SaveLastRequested?.Invoke(), last),
            Tile("\uE749", Loc("Str_Tray_PrintLast"), "", () => PrintLastRequested?.Invoke(), last),
            Tile("\uE838", Loc("Str_Tray_OpenFolder"), "", () =>
            {
                try
                {
                    var folder = CaptureSettings.OutputFolder;
                    System.IO.Directory.CreateDirectory(folder);
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                }
                catch (Exception ex) { ShowWarning(Loc("Str_Tray_FailedTitle"), ex.Message); }
            }),
        ]);
        // The shortcut repair tiles keep their row, dimmed while every shortcut is claimed.
        bool failed = _failed.Count > 0;
        groups.Add(
        [
            Tile("\uE713", Loc("Str_CS_OpenWindowsSetting"), "", OpenPrintScreenSetting, failed) with { Shown = Loc("Str_Card_WindowsSettingShort") },
            Tile("\uE72C", Loc("Str_Tray_Retry"), "", () =>
            {
                RegisterShortcuts();
                if (_failed.Count == 0) ShowInfo(Loc("Str_Tray_ShortcutsReadyTitle"), Loc("Str_Tray_ShortcutsReady"));
                else WarnIfFailed();
            }, failed),
        ]);
        groups.Add([Tile("\uE7E8", Loc("Str_Tray_Exit"), "", () => App.Instance.RequestExit())]);
        menu.Items.Add(ShotCards.Grid(menu, [.. groups]));

        // The menu closes on an outside click only when this process owns the foreground.
        Win32.SetForegroundWindow(Handle);
        menu.IsOpen = true;
    }

    /// <summary>The Print Screen switch changed from the tray menu; About shows it if open.</summary>
    public event Action? PrintScreenKeyChanged;

    /// <summary>Turns the Print Screen switch over, then claims the shortcuts again, which puts
    /// the hook on or takes it off.</summary>
    private void TogglePrintScreenKey()
    {
        try { PrintScreenKey.Enabled = !PrintScreenKey.Enabled; }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            ShowInfo(Loc("Str_CS_PrintScreenKey"), error.Message);
        }
        RegisterShortcuts();
        PrintScreenKeyChanged?.Invoke();
        // After the menu has closed: the offer to make it work over admin windows too.
        if (PrintScreenKey.Enabled)
            Application.Current.Dispatcher.BeginInvoke(new Action(App.OfferPrintScreenHelper));
    }

    public static void OpenPrintScreenSetting()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
        catch { }
    }

    private Win32.NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = TipText(),
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Shows that a recording is running in the tooltip, and makes a left click stop it.</summary>
    public void SetRecording(bool recording)
    {
        _recording = recording;
        UpdateTip();
    }

    private string TipText()
    {
        if (_recording) return "KillerShot: " + Loc("Str_Tray_Recording");
        string g = Gesture(ShortcutAction.LastMode);
        string tip = g.Length > 0 ? $"KillerShot ({g})" : "KillerShot";
        return tip.Length > 127 ? tip[..127] : tip;
    }

    private void AddIcon()
    {
        if (_iconAdded) return;
        var d = NewData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
        _iconAdded = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
    }

    private void UpdateTip()
    {
        if (!_iconAdded) return;
        var d = NewData(Win32.NIF_TIP);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
    }

    public void ShowInfo(string title, string text) => ShowInfo(title, text, null);

    /// <summary>A notification whose click runs <paramref name="onClick"/> instead of opening the editor.</summary>
    public void ShowInfo(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        Balloon(title, text, Win32.NIIF_INFO);
    }

    /// <summary>Test seam: every warning, whether or not the icon is there to show it.</summary>
    internal event Action<string, string>? WarningShown;

    public void ShowWarning(string title, string text)
    {
        WarningShown?.Invoke(title, text);
        _balloonAction = null;
        Balloon(title, text, Win32.NIIF_WARNING);
    }

    private void Balloon(string title, string text, uint kind)
    {
        if (!_iconAdded) return;
        var d = NewData(Win32.NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = kind;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        foreach (var id in _hotkeyIds.Keys) Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
        _printScreenHook?.Dispose();
        _printScreenHook = null;
        StopHelper();
        if (_iconAdded)
        {
            var d = NewData(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
            _iconAdded = false;
        }
        if (_ownsIcon) Win32.DestroyIcon(_icon);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
