using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Native;

namespace KillerShot.Capture;

/// <summary>One monitor in physical virtual-desktop coordinates.</summary>
internal sealed record MonitorBounds(IntPtr Handle, Win32.RECT Bounds, bool Primary);

/// <summary>A copy of the whole virtual desktop taken the moment a capture starts. Region,
/// monitor and all-monitor modes crop from it, so open menus and tooltips stay in the picture
/// and the selection overlay can never appear in the result.</summary>
internal sealed class FrozenDesktop
{
    public Win32.RECT Bounds { get; }
    public IReadOnlyList<MonitorBounds> Monitors { get; }
    private readonly byte[] _pixels;   // BGRA rows, top-down, stride Bounds.Width * 4

    private FrozenDesktop(Win32.RECT bounds, byte[] pixels, IReadOnlyList<MonitorBounds> monitors)
    {
        Bounds = bounds;
        _pixels = pixels;
        Monitors = monitors;
    }

    public static IReadOnlyList<MonitorBounds> EnumerateMonitors()
    {
        var list = new List<MonitorBounds>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = "" };
            if (Win32.GetMonitorInfo(h, ref info))
                list.Add(new MonitorBounds(h, info.rcMonitor, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static FrozenDesktop Capture()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("Windows reported an empty desktop.");

        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = Win32.CreateCompatibleDC(screen);
            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,            // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = Win32.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the desktop copy.");
            old = Win32.SelectObject(mem, dib);
            if (!Win32.BitBlt(mem, 0, 0, w, h, screen, x, y, Win32.SRCCOPY | Win32.CAPTUREBLT))
                throw new InvalidOperationException("Windows refused to copy the desktop.");
            var pixels = new byte[(long)w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new FrozenDesktop(new Win32.RECT(x, y, x + w, y + h), pixels, EnumerateMonitors());
        }
        finally
        {
            if (old != IntPtr.Zero) Win32.SelectObject(mem, old);
            if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
            if (mem != IntPtr.Zero) Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public MonitorBounds MonitorAt(Win32.POINT p) =>
        Monitors.FirstOrDefault(m => m.Bounds.Contains(p))
        ?? Monitors.FirstOrDefault(m => m.Primary)
        ?? new MonitorBounds(IntPtr.Zero, Bounds, true);

    /// <summary>Raw BGRA copy of a rectangle in virtual-desktop coordinates, clamped to the desktop.</summary>
    public (byte[] Pixels, Win32.RECT Rect) CopyPixels(Win32.RECT r)
    {
        var c = Win32.RECT.Intersect(r, Bounds);
        if (c.IsEmpty) return (Array.Empty<byte>(), c);
        int srcStride = Bounds.Width * 4, dstStride = c.Width * 4;
        var dst = new byte[(long)dstStride * c.Height];
        for (int row = 0; row < c.Height; row++)
        {
            long src = (long)(c.Top - Bounds.Top + row) * srcStride + (long)(c.Left - Bounds.Left) * 4;
            Buffer.BlockCopy(_pixels, (int)src, dst, row * dstStride, dstStride);
        }
        return (dst, c);
    }

    /// <summary>A frozen 96-DPI bitmap of the rectangle, so WPF never rescales it by monitor DPI.</summary>
    public BitmapSource? Crop(Win32.RECT r)
    {
        var (px, c) = CopyPixels(r);
        return c.IsEmpty ? null : ToBitmap(px, c.Width, c.Height);
    }

    public static BitmapSource ToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
