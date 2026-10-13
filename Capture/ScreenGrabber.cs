using System.Runtime.InteropServices;
using KillerShot.Native;

namespace KillerShot.Capture;

/// <summary>Copies one fixed screen rectangle again and again for recording. The device
/// contexts and the DIB are created once and reused, so a frame costs one BitBlt and one copy.</summary>
internal sealed class ScreenGrabber : IDisposable
{
    private readonly Win32.RECT _rect;
    private readonly IntPtr _screen, _mem, _dib, _old, _bits;
    private readonly bool _cursor;

    public int Width => _rect.Width;
    public int Height => _rect.Height;

    public ScreenGrabber(Win32.RECT rect, bool includeCursor)
    {
        _rect = rect;
        _cursor = includeCursor;
        _screen = Win32.GetDC(IntPtr.Zero);
        _mem = Win32.CreateCompatibleDC(_screen);
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = rect.Width,
            biHeight = -rect.Height,
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = Win32.CreateDIBSection(_screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the recording buffer.");
        _old = Win32.SelectObject(_mem, _dib);
    }

    /// <summary>Fills <paramref name="bgra"/> (Width * Height * 4 bytes, top-down) with the screen now.</summary>
    public bool Grab(byte[] bgra)
    {
        if (!Win32.BitBlt(_mem, 0, 0, Width, Height, _screen, _rect.Left, _rect.Top, Win32.SRCCOPY | Win32.CAPTUREBLT)) return false;
        if (_cursor) FrozenDesktop.DrawCursor(_mem, _rect.Left, _rect.Top);
        Marshal.Copy(_bits, bgra, 0, Width * Height * 4);
        return true;
    }

    public void Dispose()
    {
        if (_old != IntPtr.Zero) Win32.SelectObject(_mem, _old);
        if (_dib != IntPtr.Zero) Win32.DeleteObject(_dib);
        if (_mem != IntPtr.Zero) Win32.DeleteDC(_mem);
        Win32.ReleaseDC(IntPtr.Zero, _screen);
    }
}
