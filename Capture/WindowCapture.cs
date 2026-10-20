using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using KillerShot.Native;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace KillerShot.Capture;

/// <summary>Captures a single window through Windows Graphics Capture, which reads the window's
/// own surface, so a covered window still comes back whole. Rounded-corner transparency is
/// filled with the frozen desktop behind the window, because clipboard consumers handle alpha
/// inconsistently.</summary>
internal static class WindowCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

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

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // Vtable slots. ID3D11Device: CreateTexture2D = 5. ID3D11Texture2D: GetDesc = 10.
    // ID3D11DeviceContext: Map = 14, Unmap = 15, CopyResource = 47.
    private const int Slot_CreateTexture2D = 5, Slot_GetDesc = 10, Slot_Map = 14, Slot_Unmap = 15, Slot_CopyResource = 47;

    private static IntPtr _d3dDevice, _d3dContext;
    private static IDirect3DDevice? _device;

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    /// <summary>Extended frame bounds: the visible window without the invisible resize border.</summary>
    public static Win32.RECT? FrameBounds(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) == 0 && !r.IsEmpty)
            return r;
        return null;
    }

    /// <summary>The top-level window worth capturing, or zero for the desktop, the taskbar,
    /// a minimized or cloaked window, or one of KillerShot's own windows.</summary>
    public static IntPtr CapturableRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        hwnd = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return IntPtr.Zero;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return IntPtr.Zero;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return IntPtr.Zero;
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

    private static void EnsureDevice()
    {
        if (_device != null) return;
        // 0x20 = D3D11_CREATE_DEVICE_BGRA_SUPPORT. Hardware first, WARP if no usable adapter.
        int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _d3dDevice, out _, out _d3dContext);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _d3dDevice, out _, out _d3dContext);
        Marshal.ThrowExceptionForHR(hr);

        var iid = IID_IDXGIDevice;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_d3dDevice, in iid, out IntPtr dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr inspectable));
            try { _device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally { Marshal.Release(dxgi); }
    }

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = IID_IGraphicsCaptureItem;
        IntPtr ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static async Task<(byte[], int, int)?> GrabAsync(IntPtr hwnd, int timeoutMs, bool includeCursor)
    {
        EnsureDevice();
        var item = CreateItem(hwnd);
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); }
        catch { /* Borderless is a request, not a requirement. */ }

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = includeCursor; } catch { }
        try { session.IsBorderRequired = false; } catch { }

        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            var f = p.TryGetNextFrame();
            if (f != null && !tcs.TrySetResult(f)) f.Dispose();
        };
        session.StartCapture();
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task) return null;

        using var frame = tcs.Task.Result;
        return ReadSurface(frame.Surface, frame.ContentSize.Width, frame.ContentSize.Height);
    }

    private static unsafe void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static unsafe (byte[], int, int) ReadSurface(IDirect3DSurface surface, int contentW, int contentH)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var texIid = IID_ID3D11Texture2D;
        IntPtr tex = access.GetInterface(ref texIid);
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
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Slot(_d3dDevice, Slot_CreateTexture2D))(_d3dDevice, &desc, IntPtr.Zero, &staging);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_d3dContext, Slot_CopyResource))(_d3dContext, staging, tex);
                D3D11_MAPPED_SUBRESOURCE map;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Slot(_d3dContext, Slot_Map))(_d3dContext, staging, 0, 1, 0, &map);
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
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_d3dContext, Slot_Unmap))(_d3dContext, staging, 0);
                }
            }
            finally { Marshal.Release(staging); }
        }
        finally { Marshal.Release(tex); }
    }
}
