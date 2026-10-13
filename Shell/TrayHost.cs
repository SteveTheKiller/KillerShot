using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>The notification-area icon, its menu and the global shortcuts. A hidden top-level
/// window receives WM_HOTKEY, the icon callback and TaskbarCreated, so the icon comes back after
/// Explorer restarts.</summary>
internal sealed class TrayHost : IDisposable
{
    private const int CallbackMessage = Win32.WM_APP + 1;
    private const uint IconId = 1;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _icon;
    private readonly Dictionary<int, ShortcutAction> _hotkeyIds = new();
    private readonly Dictionary<ShortcutAction, Shortcut> _active = new();
    private readonly List<string> _failed = new();
    private bool _iconAdded;

    /// <summary>Raised for a shortcut or a menu command, with the window that was in front.</summary>
    public event Action<ShortcutAction?, CaptureMode?, IntPtr>? CaptureRequested;
    public event Action? SaveLastRequested;
    public Func<bool>? HasLastCapture { get; set; }

    public IReadOnlyList<string> FailedShortcuts => _failed;
    public IntPtr Handle => _source.Handle;

    public TrayHost()
    {
        // WS_POPUP, never shown. A message-only window would miss the TaskbarCreated broadcast.
        var p = new HwndSourceParameters("KillerShotTrayHost") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        // Placeholder until the KillerShot icon exists; IDI_APPLICATION is shared and never destroyed.
        _icon = Win32.LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Start()
    {
        AddIcon();
        RegisterShortcuts();
        if (_failed.Count > 0)
            ShowWarning("Print Screen is not available",
                $"{string.Join(", ", _failed)} already belongs to another program or to the Windows setting that opens Snipping Tool. Right-click the KillerShot icon to open that setting.");
    }

    public void RegisterShortcuts()
    {
        foreach (var id in _hotkeyIds.Keys) Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
        _active.Clear();
        _failed.Clear();

        int next = 1;
        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
        {
            if (!Settings.Current.Shortcuts.TryGetValue(action.ToString(), out var text) || string.IsNullOrWhiteSpace(text)) continue;
            if (!Shortcut.TryParse(text, out var sc)) { _failed.Add(text); continue; }
            int id = next++;
            if (Win32.RegisterHotKey(Handle, id, sc.Modifiers | Win32.MOD_NOREPEAT, sc.Key))
            {
                _hotkeyIds[id] = action;
                _active[action] = sc;
            }
            else
            {
                _failed.Add(sc.Text);
            }
        }
        UpdateTip();
    }

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
            if (mouse == Win32.WM_LBUTTONUP) DeferCapture(null, Settings.Current.LastMode == CaptureMode.Window ? CaptureMode.Region : Settings.Current.LastMode);
            else if (mouse == Win32.WM_RBUTTONUP) ShowMenu();
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

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        MenuItem Item(string header, string gesture, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }

        Item("Capture region", "", () => DeferCapture(null, CaptureMode.Region));
        Item("Capture monitor", Gesture(ShortcutAction.Monitor), () => DeferCapture(null, CaptureMode.Monitor));
        Item("Capture all monitors", Gesture(ShortcutAction.AllMonitors), () => DeferCapture(null, CaptureMode.AllMonitors));
        menu.Items.Add(new Separator());
        Item("Save last capture as PNG...", "", () => SaveLastRequested?.Invoke(), HasLastCapture?.Invoke() == true);
        menu.Items.Add(new Separator());
        if (_failed.Count > 0)
        {
            Item("Open the Windows Print Screen setting", "", OpenPrintScreenSetting);
            Item("Try the shortcuts again", "", () =>
            {
                RegisterShortcuts();
                if (_failed.Count == 0) ShowInfo("Shortcuts ready", "Print Screen now opens KillerShot.");
                else ShowWarning("Still not available", string.Join(", ", _failed));
            });
            menu.Items.Add(new Separator());
        }
        Item("Exit", "", () => Application.Current.Shutdown());

        // The menu closes on an outside click only when this process owns the foreground.
        Win32.SetForegroundWindow(Handle);
        menu.IsOpen = true;
    }

    private static void OpenPrintScreenSetting()
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

    private string TipText()
    {
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

    public void ShowInfo(string title, string text) => Balloon(title, text, Win32.NIIF_INFO);
    public void ShowWarning(string title, string text) => Balloon(title, text, Win32.NIIF_WARNING);

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
        if (_iconAdded)
        {
            var d = NewData(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
            _iconAdded = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
