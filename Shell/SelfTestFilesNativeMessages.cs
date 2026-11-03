using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesNativeMessages
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateProvider(int level, int status, out IntPtr unknown);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AliveProviders();
    [StructLayout(LayoutKind.Sequential)]
    private struct MeasureItem
    {
        public uint Type, Control, Item, Width, Height;
        public UIntPtr Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawItem
    {
        public uint Type, Control, Item, Action, State;
        public IntPtr Window, Device;
        public NativeRect Bounds;
        public UIntPtr Data;
    }

    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-files-native-messages", var folder, var provider]) return false;
        Run(Path.GetFullPath(folder), Path.GetFullPath(provider));
        return true;
    }

    private static void Run(string folder, string provider)
    {
        int checks = 0, exit = 1;
        var report = new StringBuilder();
        IntPtr module = IntPtr.Zero;
        AliveProviders? nativeAlive = null;
        Window? window = null;
        var originalAcquire = ShellContextMenu.AcquireNativeMenu;
        var originalTrack = ShellContextMenu.TrackNativeMenu;
        void Check(bool condition, string message)
        {
            checks++;
            report.AppendLine((condition ? "PASS: " : "FAIL: ") + message);
            if (!condition) throw new InvalidOperationException(message);
        }
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a fresh native message fixture.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            module = NativeLibrary.Load(provider);
            var create = Marshal.GetDelegateForFunctionPointer<CreateProvider>(NativeLibrary.GetExport(module, "CreateFixtureMenu"));
            var alive = Marshal.GetDelegateForFunctionPointer<AliveProviders>(NativeLibrary.GetExport(module, "FixtureObjectsAlive"));
            nativeAlive = alive;
            window = new Window { ShowActivated = false, ShowInTaskbar = false };
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int baseline = alive();
            int level = 3, status = 0;
            bool probeRelease = false, detachedOnRelease = false;
            ShellContextMenu.AcquireNativeMenu = (owner, _) =>
            {
                IntPtr menu = CreatePopupMenu();
                object? native = null;
                try
                {
                    if (menu == IntPtr.Zero || create(level, status, out IntPtr unknown) < 0 || unknown == IntPtr.Zero)
                        throw new InvalidOperationException("Native fixture creation failed.");
                    try { native = Marshal.GetObjectForIUnknown(unknown); }
                    finally { Marshal.Release(unknown); }
                    var context = (ShellContextMenu.IContextMenu)native;
                    if (context.QueryContextMenu(menu, 0, 1, 0x7fff, 4) < 0)
                        throw new InvalidOperationException("Native fixture menu creation failed.");
                    var owned = native;
                    return new ShellContextMenu.ShellMenuSession(new WindowInteropHelper(owner).Handle, menu, context, () =>
                    {
                        if (probeRelease)
                        {
                            IntPtr child = GetSubMenu(menu, 1);
                            SendMessage(new WindowInteropHelper(owner).Handle, 0x117, child, IntPtr.Zero);
                            detachedOnRelease = GetMenuItemCount(child) == 0;
                        }
                        DestroyMenu(menu);
                        Marshal.ReleaseComObject(owned);
                    });
                }
                catch
                {
                    if (menu != IntPtr.Zero) DestroyMenu(menu);
                    if (native != null) Marshal.ReleaseComObject(native);
                    throw;
                }
            };
            int initialized = -1;
            ShellContextMenu.TrackNativeMenu = (owner, menu) =>
            {
                IntPtr child = GetSubMenu(menu, 1);
                SendMessage(owner, 0x117, child, IntPtr.Zero);
                initialized = GetMenuItemCount(child);
                return 0;
            };
            bool shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
            Check(shown && initialized == 1, "public Show forwards native submenu initialization through the real HWND and COM provider");
            Check(alive() == baseline, "public Show releases its native provider and menus after cancellation");
            IntPtr characterResult = IntPtr.Zero;
            ShellContextMenu.TrackNativeMenu = (owner, menu) =>
            {
                IntPtr child = GetSubMenu(menu, 1);
                SendMessage(owner, 0x117, child, IntPtr.Zero);
                characterResult = SendMessage(owner, 0x120, new IntPtr((0x10 << 16) | 'L'), child);
                return 0;
            };
            shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
            Check(shown && characterResult == new IntPtr(0x30000), "public Show returns the native MENUCHAR selection result unchanged");
            IntPtr measurePointer = Marshal.AllocHGlobal(Marshal.SizeOf<MeasureItem>());
            try
            {
                Marshal.StructureToPtr(new MeasureItem { Type = 1 }, measurePointer, false);
                IntPtr measureResult = IntPtr.Zero;
                ShellContextMenu.TrackNativeMenu = (owner, menu) =>
                {
                    measureResult = SendMessage(owner, 0x2c, IntPtr.Zero, measurePointer);
                    return 0;
                };
                shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                var measured = Marshal.PtrToStructure<MeasureItem>(measurePointer);
                Check(shown && measured.Width == 61 && measured.Height == 23 && measureResult == new IntPtr(1),
                    "public Show lets the native provider measure a real menu structure and returns its result");
                foreach (int control in new[] { 0, 7 })
                {
                    Marshal.StructureToPtr(new MeasureItem { Type = 4, Control = (uint)control, Width = 7, Height = 9 }, measurePointer, false);
                    ShellContextMenu.TrackNativeMenu = (owner, menu) => { SendMessage(owner, 0x2c, new IntPtr(control), measurePointer); return 0; };
                    shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                    measured = Marshal.PtrToStructure<MeasureItem>(measurePointer);
                    Check(shown && measured.Width == 7 && measured.Height == 9,
                        "native menu measurement leaves non-menu control unchanged, id=" + control);
                }
                level = 2;
                Marshal.StructureToPtr(new MeasureItem { Type = 1 }, measurePointer, false);
                ShellContextMenu.TrackNativeMenu = (owner, menu) => { measureResult = SendMessage(owner, 0x2c, IntPtr.Zero, measurePointer); return 0; };
                shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                measured = Marshal.PtrToStructure<MeasureItem>(measurePointer);
                Check(shown && measured.Width == 61 && measured.Height == 23 && measureResult == new IntPtr(1),
                    "IContextMenu2 native fallback measures menu items and returns TRUE");
                level = 3;
            }
            finally { Marshal.FreeHGlobal(measurePointer); }
            IntPtr device = CreateCompatibleDC(IntPtr.Zero);
            IntPtr bitmap = CreateBitmap(8, 8, 1, 32, IntPtr.Zero);
            IntPtr previous = IntPtr.Zero, drawPointer = IntPtr.Zero;
            try
            {
                if (device == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Fixture memory bitmap creation failed.");
                previous = SelectObject(device, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new InvalidOperationException("Fixture bitmap selection failed.");
                SetPixel(device, 1, 1, 0);
                drawPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DrawItem>());
                Marshal.StructureToPtr(new DrawItem { Type = 1, Device = device,
                    Bounds = new NativeRect { Right = 8, Bottom = 8 } }, drawPointer, false);
                IntPtr drawResult = IntPtr.Zero;
                ShellContextMenu.TrackNativeMenu = (owner, menu) =>
                {
                    drawResult = SendMessage(owner, 0x2b, IntPtr.Zero, drawPointer);
                    return 0;
                };
                shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                Check(shown && GetPixel(device, 1, 1) == 0x905816 && drawResult == new IntPtr(1),
                    "public Show lets the native provider draw into an owned memory bitmap and returns its result");
                foreach (int control in new[] { 0, 7 })
                {
                    SetPixel(device, 1, 1, 0);
                    Marshal.StructureToPtr(new DrawItem { Type = 4, Control = (uint)control, Device = device,
                        Bounds = new NativeRect { Right = 8, Bottom = 8 } }, drawPointer, false);
                    ShellContextMenu.TrackNativeMenu = (owner, menu) => { SendMessage(owner, 0x2b, new IntPtr(control), drawPointer); return 0; };
                    shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                    Check(shown && GetPixel(device, 1, 1) == 0, "native menu drawing leaves non-menu control unchanged, id=" + control);
                }
                level = 2;
                SetPixel(device, 1, 1, 0);
                Marshal.StructureToPtr(new DrawItem { Type = 1, Device = device,
                    Bounds = new NativeRect { Right = 8, Bottom = 8 } }, drawPointer, false);
                ShellContextMenu.TrackNativeMenu = (owner, menu) => { drawResult = SendMessage(owner, 0x2b, IntPtr.Zero, drawPointer); return 0; };
                shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                Check(shown && GetPixel(device, 1, 1) == 0x905816 && drawResult == new IntPtr(1),
                    "IContextMenu2 native fallback draws menu items and returns TRUE");
                level = 3;
            }
            finally
            {
                if (drawPointer != IntPtr.Zero) Marshal.FreeHGlobal(drawPointer);
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(device, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (device != IntPtr.Zero) DeleteDC(device);
            }
            foreach (var variant in new[] { (Level: 2, Status: 0, Expected: 1), (Level: 1, Status: 0, Expected: 0),
                (Level: 3, Status: 1, Expected: 0), (Level: 3, Status: unchecked((int)0x80004005), Expected: 0) })
            {
                level = variant.Level;
                status = variant.Status;
                initialized = -1;
                ShellContextMenu.TrackNativeMenu = (owner, menu) =>
                {
                    IntPtr child = GetSubMenu(menu, 1);
                    SendMessage(owner, 0x117, child, IntPtr.Zero);
                    initialized = GetMenuItemCount(child);
                    return 0;
                };
                shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
                Check(shown && initialized == variant.Expected,
                    $"native interface/HRESULT path level={level} status={status} keeps expected provider behavior");
            }
            level = 3;
            status = 0;
            probeRelease = true;
            ShellContextMenu.TrackNativeMenu = (_, _) => 0;
            shown = ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]);
            Check(shown && detachedOnRelease, "forwarding hook is removed before native release after popup cancellation");
            detachedOnRelease = false;
            ShellContextMenu.TrackNativeMenu = (_, _) => throw new InvalidOperationException("Owned tracking failure.");
            bool trackingFailed = false;
            try { ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]); }
            catch (InvalidOperationException error) when (error.Message == "Owned tracking failure.") { trackingFailed = true; }
            Check(trackingFailed && detachedOnRelease && alive() == baseline,
                "tracking failure removes the hook and releases the actual native provider");
            probeRelease = false;
            ShellContextMenu.TrackNativeMenu = (_, _) => 1;
            Check(ShellContextMenu.Show(window, [Path.Combine(folder, "owned.txt")]),
                "selected native command runs with correct ordinal after forwarding hook removal");
            Check(alive() == baseline, "all actual native COM providers are destroyed after the public operations");
            report.AppendLine("Owner was never shown; foreground, cursor and popup tracking were replaced only at the Windows boundary.");
            exit = 0;
        }
        catch (Exception error) { report.AppendLine("FAIL: " + error); }
        finally
        {
            ShellContextMenu.AcquireNativeMenu = originalAcquire;
            ShellContextMenu.TrackNativeMenu = originalTrack;
            window?.Close();
            if (module != IntPtr.Zero && (nativeAlive == null || nativeAlive() == 0)) NativeLibrary.Free(module);
            report.AppendLine($"Assertions: {checks}; Exit: {exit}");
            File.WriteAllText(Path.Combine(folder, "files-native-messages-report.txt"), report.ToString());
            App.Current.Shutdown(exit);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr device);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, IntPtr data);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr device, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr device);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr device, int x, int y);
    [DllImport("gdi32.dll")] private static extern uint SetPixel(IntPtr device, int x, int y, uint color);
}
