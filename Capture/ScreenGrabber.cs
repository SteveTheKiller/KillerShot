using System.Runtime.InteropServices;
using KillerShot.Native;

namespace KillerShot.Capture;

/// <summary>Copies one fixed screen rectangle again and again for recording, when Windows
/// Graphics Capture is not available. The device contexts and the DIB are created once and
/// reused, so a frame costs one BitBlt and one copy.</summary>
internal sealed class ScreenGrabber : IFrameGrabber
{
    private readonly Win32.RECT _rect;
    private readonly IntPtr _screen, _mem, _dib, _old, _bits;
    private readonly bool _cursor;
    private readonly GdiResources _gdi;
    private int _disposed;

    public int Width => _rect.Width;
    public int Height => _rect.Height;

    public ScreenGrabber(Win32.RECT rect, bool includeCursor, GdiResources? gdi = null)
    {
        _rect = rect;
        _cursor = includeCursor;
        _gdi = gdi ?? new GdiResources();
        try
        {
            _screen = _gdi.GetScreen();
            if (_screen == IntPtr.Zero) throw new InvalidOperationException("Could not access the recording screen.");
            _mem = _gdi.CreateMemory(_screen);
            if (_mem == IntPtr.Zero) throw new InvalidOperationException("Could not create the recording context.");
            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = rect.Width,
                biHeight = -rect.Height,
                biPlanes = 1,
                biBitCount = 32,
            };
            _dib = _gdi.CreateBitmap(_screen, ref bmi, out _bits);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the recording buffer.");
            _old = _gdi.Select(_mem, _dib);
            if (_old == IntPtr.Zero || _old == (IntPtr)(-1)) throw new InvalidOperationException("Could not select the recording buffer.");
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Fills <paramref name="bgra"/> (Width * Height * 4 bytes, top-down) with the screen now.</summary>
    public bool Grab(byte[] bgra)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!Win32.BitBlt(_mem, 0, 0, Width, Height, _screen, _rect.Left, _rect.Top, Win32.SRCCOPY | Win32.CAPTUREBLT))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (_cursor) FrozenDesktop.DrawCursor(_mem, _rect.Left, _rect.Top);
        Marshal.Copy(_bits, bgra, 0, Width * Height * 4);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_old != IntPtr.Zero && _old != (IntPtr)(-1)) _gdi.Select(_mem, _old);
        if (_dib != IntPtr.Zero) _gdi.DeleteBitmap(_dib);
        if (_mem != IntPtr.Zero) _gdi.DeleteMemory(_mem);
        if (_screen != IntPtr.Zero) _gdi.ReleaseScreen(_screen);
    }

    internal class GdiResources
    {
        internal virtual IntPtr GetScreen() => Win32.GetDC(IntPtr.Zero);
        internal virtual IntPtr CreateMemory(IntPtr screen) => Win32.CreateCompatibleDC(screen);
        internal virtual IntPtr CreateBitmap(IntPtr screen, ref Win32.BITMAPINFOHEADER info, out IntPtr bits)
            => Win32.CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
        internal virtual IntPtr Select(IntPtr memory, IntPtr bitmap) => Win32.SelectObject(memory, bitmap);
        internal virtual void DeleteBitmap(IntPtr bitmap) => Win32.DeleteObject(bitmap);
        internal virtual void DeleteMemory(IntPtr memory) => Win32.DeleteDC(memory);
        internal virtual void ReleaseScreen(IntPtr screen) => Win32.ReleaseDC(IntPtr.Zero, screen);
    }
}
