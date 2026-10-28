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
/// </summary>
internal sealed class PrintScreenHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const uint VkSnapshot = 0x2C;

    private readonly Func<uint, bool> _press;
    private readonly HookProc _proc;
    private IntPtr _hook;
    private bool _sawDown, _swallowing;

    /// <param name="press">Given the modifiers held (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN),
    /// runs the matching KillerShot shortcut and returns true, or returns false to let Windows
    /// have the key.</param>
    public PrintScreenHook(Func<uint, bool> press)
    {
        _press = press;
        _proc = Callback;   // kept in a field: the hook holds only a native pointer to it
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
    }

    public bool Installed => _hook != IntPtr.Zero;

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
        if (code >= 0 && Marshal.ReadInt32(data) == VkSnapshot)
        {
            int msg = message.ToInt32();
            if (msg is WmKeyDown or WmSysKeyDown)
            {
                // One action per press: a held key's repeats are swallowed with it.
                if (_swallowing) return 1;
                _sawDown = true;
                if (_press(HeldModifiers())) { _swallowing = true; return 1; }
            }
            else if (msg is WmKeyUp or WmSysKeyUp)
            {
                bool swallow = _swallowing;
                // Some keyboards send Print Screen as a key-up alone.
                if (!swallow && !_sawDown) swallow = _press(HeldModifiers());
                _sawDown = _swallowing = false;
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
