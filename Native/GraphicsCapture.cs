using System.Runtime.InteropServices;

namespace KillerShot.Native;

/// <summary>
/// Windows Graphics Capture called through its raw COM interfaces, so KillerShot does not ship
/// Microsoft's 25 MB projection of the whole Windows Runtime for the handful of calls it makes.
/// Every pointer here is an owned COM reference; whoever holds one releases it.
///
/// Interface GUIDs and vtable slots come from Windows.winmd (UnionMetadata 10.0.26100.0), where
/// method order is vtable order. IInspectable takes slots 0 to 5, so a WinRT interface's first
/// method is slot 6.
/// </summary>
internal static unsafe class GraphicsCapture
{
    internal static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureSessionStatics = new("2224A540-5974-49AA-B232-0882536F4CB5");
    private static readonly Guid IID_IGraphicsCaptureSession = new("814E42A9-F70F-4AD7-939B-FDDCC6EB880D");
    private static readonly Guid IID_IGraphicsCaptureSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IID_IGraphicsCaptureAccessStatics = new("743ED370-06EC-5040-A58A-901F0F757095");
    private static readonly Guid IID_IFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>DirectXPixelFormat.B8G8R8A8UIntNormalized.</summary>
    internal const int PixelFormatBgra8 = 87;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SizeInt32 { public int Width, Height; }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    internal static void Close(ref IntPtr com) => WinRt.Close(ref com);

    public static bool IsSupported
    {
        get
        {
            try
            {
                IntPtr statics = WinRt.Factory("Windows.Graphics.Capture.GraphicsCaptureSession", IID_IGraphicsCaptureSessionStatics);
                try
                {
                    byte supported;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)WinRt.Slot(statics, 6))(statics, &supported);
                    return hr >= 0 && supported != 0;
                }
                finally { Marshal.Release(statics); }
            }
            catch { return false; }
        }
    }

    /// <summary>Asks for borderless capture and waits up to the timeout. It is a request, not a
    /// requirement, so any failure is ignored.</summary>
    public static void RequestBorderless(int timeoutMs)
    {
        IntPtr statics = IntPtr.Zero, operation = IntPtr.Zero, info = IntPtr.Zero;
        try
        {
            statics = WinRt.Factory("Windows.Graphics.Capture.GraphicsCaptureAccess", IID_IGraphicsCaptureAccessStatics);
            // RequestAccessAsync(GraphicsCaptureAccessKind.Borderless = 0)
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)WinRt.Slot(statics, 6))(statics, 0, &operation) < 0) return;
            info = WinRt.Query(operation, new Guid("00000036-0000-0000-C000-000000000046"));
            if (info == IntPtr.Zero) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                int status;
                if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)WinRt.Slot(info, 7))(info, &status) < 0 || status != 0) return;
                Thread.Sleep(10);
            }
        }
        catch { }
        finally
        {
            WinRt.Release(ref info);
            WinRt.Release(ref operation);
            WinRt.Release(ref statics);
        }
    }

    /// <summary>A D3D11 device, its immediate context and the WinRT IDirect3DDevice wrapping it.</summary>
    internal sealed class Device : IDisposable
    {
        public IntPtr D3D, Context, WinRT;

        /// <param name="createNative">Optional native allocation boundary for tests; each returned
        /// pointer transfers one owned reference.</param>
        public Device(Func<(IntPtr Device, IntPtr Context)>? createNative = null)
        {
            try
            {
                if (createNative != null) (D3D, Context) = createNative();
                else
                {
                    // 0x20 = D3D11_CREATE_DEVICE_BGRA_SUPPORT. Hardware first, WARP if no usable adapter.
                    int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out D3D, out _, out Context);
                    if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out D3D, out _, out Context);
                    Marshal.ThrowExceptionForHR(hr);
                }
                var iid = IID_IDXGIDevice;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(D3D, in iid, out IntPtr dxgi));
                try { Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out WinRT)); }
                finally { Marshal.Release(dxgi); }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            WinRt.Close(ref WinRT);
            WinRt.Release(ref Context);
            WinRt.Release(ref D3D);
        }
    }

    private static IntPtr CreateItem(bool window, IntPtr handle)
    {
        IntPtr interop = WinRt.Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
        try
        {
            var iid = IID_IGraphicsCaptureItem;
            IntPtr item;
            // IGraphicsCaptureItemInterop is a plain IUnknown interface: CreateForWindow = 3, CreateForMonitor = 4.
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)WinRt.Slot(interop, window ? 3 : 4))(interop, handle, &iid, &item);
            Marshal.ThrowExceptionForHR(hr);
            return item;
        }
        finally { Marshal.Release(interop); }
    }

    public static IntPtr CreateItemForWindow(IntPtr hwnd) => CreateItem(true, hwnd);
    public static IntPtr CreateItemForMonitor(IntPtr monitor) => CreateItem(false, monitor);

    public static SizeInt32 ItemSize(IntPtr item)
    {
        SizeInt32 size;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)WinRt.Slot(item, 7))(item, &size));
        return size;
    }

    public static IntPtr CreateFreeThreadedPool(IntPtr device, int buffers, SizeInt32 size)
    {
        IntPtr statics = WinRt.Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IID_IFramePoolStatics2);
        try
        {
            IntPtr pool;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)WinRt.Slot(statics, 6))(statics, device, PixelFormatBgra8, buffers, size, &pool);
            Marshal.ThrowExceptionForHR(hr);
            return pool;
        }
        finally { Marshal.Release(statics); }
    }

    public static void RecreatePool(IntPtr pool, IntPtr device, int buffers, SizeInt32 size)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, int>)WinRt.Slot(pool, 6))(pool, device, PixelFormatBgra8, buffers, size));

    /// <summary>The next frame, or zero when none is waiting.</summary>
    public static IntPtr TryGetNextFrame(IntPtr pool)
    {
        IntPtr frame;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)WinRt.Slot(pool, 7))(pool, &frame));
        return frame;
    }

    public static IntPtr CreateSession(IntPtr pool, IntPtr item)
    {
        IntPtr session;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Slot(pool, 10))(pool, item, &session));
        return session;
    }

    /// <summary>Sets cursor capture and, where Windows allows it, drops the yellow capture border.
    /// Older Windows builds lack these interfaces, so a missing one is skipped.</summary>
    public static void ConfigureSession(IntPtr session, bool includeCursor)
    {
        IntPtr s2 = WinRt.Query(session, IID_IGraphicsCaptureSession2);
        if (s2 != IntPtr.Zero)
        {
            try { ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)WinRt.Slot(s2, 7))(s2, includeCursor ? (byte)1 : (byte)0); }
            finally { Marshal.Release(s2); }
        }
        IntPtr s3 = WinRt.Query(session, IID_IGraphicsCaptureSession3);
        if (s3 != IntPtr.Zero)
        {
            try { ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)WinRt.Slot(s3, 7))(s3, 0); }
            finally { Marshal.Release(s3); }
        }
    }

    public static void StartCapture(IntPtr session)
    {
        IntPtr s = WinRt.Query(session, IID_IGraphicsCaptureSession);
        if (s == IntPtr.Zero) throw new InvalidOperationException("The capture session has no IGraphicsCaptureSession.");
        try { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)WinRt.Slot(s, 6))(s)); }
        finally { Marshal.Release(s); }
    }

    public static SizeInt32 FrameContentSize(IntPtr frame)
    {
        SizeInt32 size;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)WinRt.Slot(frame, 8))(frame, &size));
        return size;
    }

    /// <summary>The frame's ID3D11Texture2D; the caller releases it.</summary>
    public static IntPtr FrameTexture(IntPtr frame)
    {
        IntPtr surface;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)WinRt.Slot(frame, 6))(frame, &surface));
        try
        {
            IntPtr access = WinRt.Query(surface, IID_IDirect3DDxgiInterfaceAccess);
            if (access == IntPtr.Zero) throw new InvalidOperationException("The capture surface has no DXGI access.");
            try
            {
                var iid = IID_ID3D11Texture2D;
                IntPtr texture;
                // IDirect3DDxgiInterfaceAccess is IUnknown-based: GetInterface = 3.
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)WinRt.Slot(access, 3))(access, &iid, &texture));
                return texture;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(surface); }
    }
}
