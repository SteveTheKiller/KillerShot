using System.Runtime.InteropServices;
using KillerShot.Capture;
using KillerShot.Services;

namespace KillerShot.Native;

/// <summary>
/// "Use KillerShot for the Print Screen key": a low-level keyboard hook, alive only while
/// KillerShot runs with the switch on, that takes the Print Screen presses KillerShot has
/// shortcuts for before Windows sees them. Windows 11 hands Print Screen to Snipping Tool ahead of
/// any registered hotkey, so the hotkey alone never reached KillerShot from another window.
///
/// Windows' own setting is never touched. The first version of this switch turned it off in the
/// registry instead, and Windows did not reliably pick that up until a sign-out, and Print Screen
/// went dead whenever KillerShot was not running (Steve, 2026-10-27). With a hook, closing
/// KillerShot gives Print Screen straight back to Snipping Tool.
///
/// A press with no matching KillerShot shortcut passes through untouched. The callback only reads
/// the key and queues the action, as a low-level hook must answer within the system's timeout.
///
/// The hook lives on its own thread with its own message loop. Windows silently removes a
/// low-level hook whose thread does not answer in time, so on the UI thread one long stall (a big
/// folder loading, a slow save) cost the Print Screen key for the rest of the session.
///
/// A swallowed press with Alt or Windows held sends one unassigned key (0xE8) before the modifier
/// comes back up. Windows reads an Alt pressed and released with nothing in between as a press of
/// Alt alone, which put the captured window's menu bar in keyboard mode, and a lone Windows key
/// opened Start.
/// </summary>
internal sealed class PrintScreenHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const uint WmQuit = 0x12;
    private const uint VkSnapshot = 0x2C;
    private const ushort VkMask = 0xE8;   // unassigned: no app or Windows acts on it

    private readonly Func<uint, bool> _press;
    private readonly Action _sendMask;
    private readonly HookProc _proc;
    private readonly Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _sawDown, _swallowing;

    /// <param name="press">Given the modifiers held (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN),
    /// runs the matching KillerShot shortcut and returns true, or returns false to let Windows
    /// have the key. Called on the hook's own thread.</param>
    public PrintScreenHook(Func<uint, bool> press) : this(press, SendMask, install: true) { }

    /// <summary>Self-tests drive <see cref="Handle"/> without a hook and count the mask keys.</summary>
    internal PrintScreenHook(Func<uint, bool> press, Action sendMask, bool install)
    {
        _press = press;
        _sendMask = sendMask;
        _proc = Callback;   // kept in a field: the hook holds only a native pointer to it
        if (!install) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "KillerShot Print Screen hook" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>The hook's thread, for the self-test that checks it is not the UI thread.</summary>
    internal Thread? HookThread => _thread;

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        // Create this thread's message queue before anyone can post WM_QUIT to it.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        ready.Set();
        if (_hook == IntPtr.Zero) return;
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>The KillerShot shortcut a Print Screen press with these modifiers runs, if any.</summary>
    public static bool Matches(IReadOnlyDictionary<ShortcutAction, Shortcut> active, uint modifiers, out ShortcutAction action)
    {
        foreach (var (candidate, shortcut) in active)
            if (shortcut.Key == VkSnapshot && (shortcut.Modifiers & ~Win32.MOD_NOREPEAT) == modifiers)
            {
                action = candidate;
                return true;
            }
        action = default;
        return false;
    }

    /// <summary>The modifiers held right now, in RegisterHotKey's MOD_ flags.</summary>
    public static uint HeldModifiers()
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        uint mods = 0;
        if (Down(0x12)) mods |= Win32.MOD_ALT;
        if (Down(0x11)) mods |= Win32.MOD_CONTROL;
        if (Down(0x10)) mods |= Win32.MOD_SHIFT;
        if (Down(0x5B) || Down(0x5C)) mods |= Win32.MOD_WIN;
        return mods;
    }

    private IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Handle(message.ToInt32(), (uint)Marshal.ReadInt32(data), HeldModifiers())) return 1;
        return CallNextHookEx(_hook, code, message, data);
    }

    /// <summary>One keyboard event: true when KillerShot takes it and Windows never sees it.</summary>
    internal bool Handle(int message, uint key, uint modifiers)
    {
        if (key != VkSnapshot) return false;
        if (message is WmKeyDown or WmSysKeyDown)
        {
            // One action per press: a held key's repeats are swallowed with it.
            if (_swallowing) return true;
            _sawDown = true;
            if (!_press(modifiers)) return false;
            _swallowing = true;
            MaskModifiers(modifiers);
            return true;
        }
        if (message is WmKeyUp or WmSysKeyUp)
        {
            bool swallow = _swallowing;
            // Some keyboards send Print Screen as a key-up alone.
            if (!swallow && !_sawDown && _press(modifiers))
            {
                swallow = true;
                MaskModifiers(modifiers);
            }
            _sawDown = _swallowing = false;
            return swallow;
        }
        return false;
    }

    private void MaskModifiers(uint modifiers)
    {
        if ((modifiers & (Win32.MOD_ALT | Win32.MOD_WIN)) != 0) _sendMask();
    }

    private static void SendMask()
    {
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VkMask } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VkMask, dwFlags = 2 } },   // KEYEVENTF_KEYUP
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_thread == null) return;
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // The union's largest member is MOUSEINPUT (32 bytes on x64); padding keeps cbSize right.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
