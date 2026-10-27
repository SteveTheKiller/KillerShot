using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KillerShot.Shell;

// KillerShot opens as a short window. A saved size that fills the monitor's height (a window
// dragged or snapped to full height) comes back at the default height, centered where it was.
public partial class MainWindow
{
    private const double OpeningHeight = 620;

    partial void BeforeWorkAreaFit()
    {
        if (WindowState != WindowState.Normal || Height <= OpeningHeight) return;
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        IntPtr monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        double scale = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        double areaTop = info.rcWork.top / scale, areaHeight = (info.rcWork.bottom - info.rcWork.top) / scale;
        if (Height < areaHeight * 0.85) return;
        Height = OpeningHeight;
        Top = areaTop + (areaHeight - OpeningHeight) / 2;
    }
}
