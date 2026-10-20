using System.IO;
using KillerShot.Capture;
using KillerShot.Native;

namespace KillerShot.Shell;

internal static class SelfTestGdiLifetime
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-gdi-lifetime") return false;
        string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            foreach (string failure in new[] { "screen", "memory", "bitmap", "bits", "select-zero", "select-error", "success" })
            {
                var api = new FailingGdi(failure);
                bool rejected = false;
                ScreenGrabber? grabber = null;
                try
                {
                    grabber = new ScreenGrabber(new Win32.RECT(0, 0, 2, 2), false, api);
                }
                catch (InvalidOperationException) { rejected = true; }
                grabber?.Dispose();
                grabber?.Dispose();
                if (rejected != (failure != "success") || api.Live.Count != 0)
                    throw new InvalidOperationException($"GDI {failure}: rejected={rejected} live_handles={api.Live.Count}");
                report.Add(failure + ": no leaked or double-released handles PASS");
            }
            using (var real = new ScreenGrabber(new Win32.RECT(0, 0, 16, 16), false))
                if (!real.Grab(new byte[16 * 16 * 4])) throw new InvalidOperationException("Real GDI capture failed.");
            report.Add("real 16x16 GDI capture PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "gdi-lifetime-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    private sealed class FailingGdi(string failure) : ScreenGrabber.GdiResources
    {
        internal HashSet<IntPtr> Live { get; } = [];
        private IntPtr Acquire(string stage, int handle)
        {
            if (failure == stage) return IntPtr.Zero;
            Live.Add((IntPtr)handle); return (IntPtr)handle;
        }
        private void Release(IntPtr handle)
        {
            if (!Live.Remove(handle)) throw new InvalidOperationException("A GDI handle was released twice or was never acquired.");
        }
        internal override IntPtr GetScreen() => Acquire("screen", 1);
        internal override IntPtr CreateMemory(IntPtr screen) => Acquire("memory", 2);
        internal override IntPtr CreateBitmap(IntPtr screen, ref Win32.BITMAPINFOHEADER info, out IntPtr bits)
        {
            bits = failure == "bits" ? IntPtr.Zero : (IntPtr)5;
            return Acquire("bitmap", 3);
        }
        internal override IntPtr Select(IntPtr memory, IntPtr bitmap)
            => bitmap == (IntPtr)4 ? (IntPtr)3 : failure == "select-zero" ? IntPtr.Zero : failure == "select-error" ? (IntPtr)(-1) : (IntPtr)4;
        internal override void DeleteBitmap(IntPtr bitmap) => Release(bitmap);
        internal override void DeleteMemory(IntPtr memory) => Release(memory);
        internal override void ReleaseScreen(IntPtr screen) => Release(screen);
    }
}
