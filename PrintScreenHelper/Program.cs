using System.Globalization;
using System.Runtime.InteropServices;
using KillerShot.Native;

namespace KillerShot.PrintScreen;

/// <summary>
/// KillerShot.PrintScreen.exe &lt;process id&gt; &lt;window&gt; &lt;modifiers&gt; &lt;stop event&gt;
///
/// Windows hides keys pressed in a window run as administrator from ordinary programs, so
/// KillerShot's own hook never sees Print Screen there and Snipping Tool gets it. This helper runs
/// with UI Access, which Windows grants only to a signed file in Program Files, and catches the
/// Print Screen presses KillerShot has shortcuts for everywhere, admin windows included. It never
/// captures anything itself: it posts each press to KillerShot's tray window and KillerShot runs
/// the capture.
///
/// KillerShot starts it with its own process ID, its tray window, the modifier sets it has
/// Print Screen shortcuts for (comma separated MOD_ flags, "0" for Print Screen alone) and the
/// name of an event that stops it. It quits when KillerShot sets that event or exits, so Print
/// Screen never stays taken once KillerShot is gone.
/// </summary>
internal static class Program
{
    /// <summary>The message KillerShot's tray window receives: wParam is the modifiers held, or -1
    /// once the helper is listening; lParam is the window in front when the key was pressed.</summary>
    internal const string MessageName = "KillerShot.PrintScreenPressed";

    private const uint Synchronize = 0x00100000;

    private static int Main(string[] args)
    {
        if (args.Length != 4
            || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long windowValue))
            return 2;
        var taken = new HashSet<uint>();
        foreach (string part in args[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint mods) || mods > 0xF) return 2;
            taken.Add(mods);
        }
        if (taken.Count == 0) return 2;

        // The window must belong to the process named, so the helper cannot be pointed at another
        // program's window.
        var window = new IntPtr(windowValue);
        GetWindowThreadProcessId(window, out uint owner);
        if (owner == 0 || owner != (uint)processId) return 3;
        IntPtr parent = OpenProcess(Synchronize, false, (uint)processId);
        if (parent == IntPtr.Zero) return 3;
        EventWaitHandle stop;
        try { stop = EventWaitHandle.OpenExisting(args[3]); }
        catch (Exception) { CloseHandle(parent); return 3; }

        uint message = RegisterWindowMessageW(MessageName);
        using (stop)
        using (var parentExit = new ProcessExit(parent))
        using (var hook = new PrintScreenHook(mods =>
        {
            if (!taken.Contains(mods)) return false;
            PostMessageW(window, message, new IntPtr(mods), GetForegroundWindow());
            return true;
        }))
        {
            if (!hook.Installed) return 4;
            PostMessageW(window, message, new IntPtr(-1), IntPtr.Zero);
            WaitHandle.WaitAny([stop, parentExit]);
        }
        return 0;
    }

    /// <summary>KillerShot's process handle as a wait handle; it signals when KillerShot exits.</summary>
    private sealed class ProcessExit : WaitHandle
    {
        public ProcessExit(IntPtr process) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process, ownsHandle: true);
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
