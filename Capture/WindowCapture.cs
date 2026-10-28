using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using KillerShot.Native;

namespace KillerShot.Capture;

/// <summary>Captures a single window through Windows Graphics Capture, which reads the window's
/// own surface, so a covered window still comes back whole. Rounded-corner transparency is
/// filled with the frozen desktop behind the window, because clipboard consumers handle alpha
/// inconsistently.</summary>
internal static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch, DepthPitch;
    }

    // Vtable slots. ID3D11Device: CreateTexture2D = 5. ID3D11Texture2D: GetDesc = 10.
    // ID3D11DeviceContext: Map = 14, Unmap = 15, CopyResource = 47.
    private const int Slot_CreateTexture2D = 5, Slot_GetDesc = 10, Slot_Map = 14, Slot_Unmap = 15, Slot_CopyResource = 47;

    private static GraphicsCapture.Device? _device;

    public static bool IsSupported => GraphicsCapture.IsSupported;

    /// <summary>Extended frame bounds: the visible window without the invisible resize border.</summary>
    public static Win32.RECT? FrameBounds(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) == 0 && !r.IsEmpty)
            return r;
        return null;
    }

    private static readonly HashSet<IntPtr> OwnCapturable = [];

    /// <summary>Lets one of KillerShot's own windows be picked and captured, as any other app's
    /// is: the editor, so KillerShot can take a picture of itself. Its overlays, the capture bar
    /// and its popups stay out of the picker.</summary>
    public static void AllowOwnWindow(IntPtr hwnd, bool allow)
    {
        lock (OwnCapturable)
            if (allow) OwnCapturable.Add(hwnd); else OwnCapturable.Remove(hwnd);
    }

    /// <summary>The top-level window worth capturing, or zero for the desktop, the taskbar,
    /// a minimized or cloaked window, or one of KillerShot's own windows other than the editor.</summary>
    public static IntPtr CapturableRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        hwnd = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return IntPtr.Zero;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return IntPtr.Zero;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) lock (OwnCapturable) if (!OwnCapturable.Contains(hwnd)) return IntPtr.Zero;
        string cls = Win32.ClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return IntPtr.Zero;
        return hwnd;
    }

    /// <summary>Captures the window, composites it over the frozen desktop and returns a
    /// 96-DPI bitmap. Falls back to cropping the frozen desktop when capture is unavailable.</summary>
    public static async Task<(BitmapSource? Bitmap, bool UsedGraphicsCapture)> CaptureAsync(IntPtr hwnd, FrozenDesktop frozen, int timeoutMs = 1500, bool? includeCursor = null)
    {
        var bounds = FrameBounds(hwnd);
        if (bounds is null) return (null, false);

        (byte[] Px, int W, int H)? shot = null;
        if (IsSupported)
        {
            try { shot = await GrabAsync(hwnd, timeoutMs, includeCursor ?? Services.CaptureSettings.IncludeCursor); }
            catch { shot = null; }
        }

        var (desk, deskRect) = frozen.CopyPixels(bounds.Value);
        if (shot is null)
            return (deskRect.IsEmpty ? null : FrozenDesktop.ToBitmap(desk, deskRect.Width, deskRect.Height), false);

        var (px, w, h) = shot.Value;
        CompositeOverDesktop(px, w, h, bounds.Value, desk, deskRect);
        return (FrozenDesktop.ToBitmap(px, w, h), true);
    }

    /// <summary>Premultiplied BGRA over the desktop pixels at the same screen position. Pixels
    /// outside the frozen desktop are composited over black.</summary>
    private static void CompositeOverDesktop(byte[] px, int w, int h, Win32.RECT bounds, byte[] desk, Win32.RECT deskRect)
    {
        for (int y = 0; y < h; y++)
        {
            int sy = bounds.Top + y;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int a = px[i + 3];
                if (a == 255) continue;
                int sx = bounds.Left + x;
                byte db = 0, dg = 0, dr = 0;
                if (sx >= deskRect.Left && sx < deskRect.Right && sy >= deskRect.Top && sy < deskRect.Bottom)
                {
                    int d = ((sy - deskRect.Top) * deskRect.Width + (sx - deskRect.Left)) * 4;
                    db = desk[d]; dg = desk[d + 1]; dr = desk[d + 2];
                }
                int inv = 255 - a;
                px[i] = (byte)Math.Min(255, px[i] + (db * inv + 127) / 255);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] + (dg * inv + 127) / 255);
                px[i + 2] = (byte)Math.Min(255, px[i + 2] + (dr * inv + 127) / 255);
                px[i + 3] = 255;
            }
        }
    }

    private static void EnsureDevice() => _device ??= new GraphicsCapture.Device();

    private static async Task<(byte[], int, int)?> GrabAsync(IntPtr hwnd, int timeoutMs, bool includeCursor)
    {
        EnsureDevice();
        GraphicsCapture.RequestBorderless(1000);
        IntPtr item = IntPtr.Zero, pool = IntPtr.Zero, session = IntPtr.Zero, frame = IntPtr.Zero;
        try
        {
            item = GraphicsCapture.CreateItemForWindow(hwnd);
            pool = GraphicsCapture.CreateFreeThreadedPool(_device!.WinRT, 1, GraphicsCapture.ItemSize(item));
            session = GraphicsCapture.CreateSession(pool, item);
            try { GraphicsCapture.ConfigureSession(session, includeCursor); } catch { }
            GraphicsCapture.StartCapture(session);

            // The pool is free-threaded, so the first frame is polled rather than awaited on an event.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while ((frame = GraphicsCapture.TryGetNextFrame(pool)) == IntPtr.Zero)
            {
                if (clock.ElapsedMilliseconds >= timeoutMs) return null;
                await Task.Delay(5);
            }
            var size = GraphicsCapture.FrameContentSize(frame);
            return ReadSurface(frame, size.Width, size.Height);
        }
        finally
        {
            GraphicsCapture.Close(ref frame);
            GraphicsCapture.Close(ref session);
            GraphicsCapture.Close(ref pool);
            GraphicsCapture.Close(ref item);
        }
    }

    private static unsafe void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static unsafe (byte[], int, int) ReadSurface(IntPtr frame, int contentW, int contentH)
    {
        IntPtr tex = GraphicsCapture.FrameTexture(frame);
        IntPtr device = _device!.D3D, context = _device.Context;
        try
        {
            D3D11_TEXTURE2D_DESC desc;
            ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Slot(tex, Slot_GetDesc))(tex, &desc);
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.SampleCount = 1;
            desc.SampleQuality = 0;
            desc.Usage = 3;               // D3D11_USAGE_STAGING
            desc.BindFlags = 0;
            desc.CPUAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
            desc.MiscFlags = 0;

            IntPtr staging;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Slot(device, Slot_CreateTexture2D))(device, &desc, IntPtr.Zero, &staging);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(context, Slot_CopyResource))(context, staging, tex);
                D3D11_MAPPED_SUBRESOURCE map;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Slot(context, Slot_Map))(context, staging, 0, 1, 0, &map);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    int w = Math.Min(contentW, (int)desc.Width), h = Math.Min(contentH, (int)desc.Height);
                    var px = new byte[w * h * 4];
                    fixed (byte* dst = px)
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy((byte*)map.pData + (long)y * map.RowPitch, dst + (long)y * w * 4, w * 4, w * 4);
                    return (px, w, h);
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(context, Slot_Unmap))(context, staging, 0);
                }
            }
            finally { Marshal.Release(staging); }
        }
        finally { Marshal.Release(tex); }
    }
}
