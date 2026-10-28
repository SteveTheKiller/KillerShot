using System.Diagnostics;
using System.Runtime.InteropServices;
using KillerShot.Native;

namespace KillerShot.Capture;

/// <summary>A source of recorded frames: one fixed screen rectangle, read again and again.</summary>
internal interface IFrameGrabber : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Fills <paramref name="bgra"/> (Width * Height * 4 bytes, top-down) with the screen now.</summary>
    bool Grab(byte[] bgra);
}

/// <summary>
/// Records through Windows Graphics Capture, with one capture session for each monitor the
/// rectangle covers. The compositor hands each monitor's frames over on the GPU and only the
/// recorded part is copied back, so a large area costs far less than a GDI copy of the screen.
/// Windows excluded from capture, such as the recording frame and bar, stay out of the video.
/// </summary>
internal sealed unsafe class MonitorGrabber : IFrameGrabber
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

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    // Vtable slots. ID3D11Device: CreateTexture2D = 5. ID3D11DeviceContext: Map = 14,
    // Unmap = 15, CopySubresourceRegion = 46.
    private const int Slot_CreateTexture2D = 5, Slot_Map = 14, Slot_Unmap = 15, Slot_CopySubresourceRegion = 46;
    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, D3D11_USAGE_STAGING = 3, D3D11_MAP_READ = 1;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;

    /// <summary>One monitor's share of the recorded rectangle.</summary>
    private sealed class Part
    {
        public IntPtr Item, Pool, Session;
        public GraphicsCapture.SizeInt32 PoolSize;
        public IntPtr Staging;
        public int X, Y, W, H;   // where the part goes in the recorded frame
        public int SrcX, SrcY;   // where the part starts on its monitor
        public bool HasFrame;
    }

    private readonly List<Part> _parts = new();
    private GraphicsCapture.Device? _device;
    private IntPtr _d3dDevice, _context;

    public int Width { get; }
    public int Height { get; }

    /// <summary>A grabber for the rectangle, or null when Windows Graphics Capture is not
    /// available or a monitor does not deliver a first frame, so the caller can use GDI.</summary>
    /// <param name="createNativeDevice">Optional native allocation boundary. Each returned COM
    /// pointer transfers one owned reference to the grabber.</param>
    /// <param name="enumerateMonitors">Optional display-enumeration boundary.</param>
    public static MonitorGrabber? TryCreate(Win32.RECT rect, bool includeCursor,
        Func<(IntPtr Device, IntPtr Context)>? createNativeDevice = null,
        Func<IEnumerable<MonitorBounds>>? enumerateMonitors = null)
    {
        if (!WindowCapture.IsSupported) return null;
        var grabber = new MonitorGrabber(rect);
        try
        {
            grabber.Initialize(rect, includeCursor, createNativeDevice, enumerateMonitors);
            if (grabber.WaitForFirstFrames(1500)) return grabber;
        }
        catch { }
        grabber.Dispose();
        return null;
    }

    private MonitorGrabber(Win32.RECT rect)
    {
        Width = rect.Width;
        Height = rect.Height;
    }

    // The factory owns this instance before any native allocation can fail. Each part is
    // likewise registered before acquiring its item, texture, frame pool, and capture session.
    private void Initialize(Win32.RECT rect, bool includeCursor,
        Func<(IntPtr Device, IntPtr Context)>? createNativeDevice,
        Func<IEnumerable<MonitorBounds>>? enumerateMonitors)
    {
        // A device of its own, because a D3D11 device context must not be shared between the
        // recording thread and window captures on the UI thread.
        _device = new GraphicsCapture.Device(createNativeDevice);
        _d3dDevice = _device.D3D;
        _context = _device.Context;
        GraphicsCapture.RequestBorderless(1000);

        foreach (var monitor in (enumerateMonitors ?? FrozenDesktop.EnumerateMonitors)())
        {
            var r = Win32.RECT.Intersect(rect, monitor.Bounds);
            if (r.IsEmpty) continue;

            var part = new Part
            {
                X = r.Left - rect.Left, Y = r.Top - rect.Top, W = r.Width, H = r.Height,
                SrcX = r.Left - monitor.Bounds.Left, SrcY = r.Top - monitor.Bounds.Top,
            };
            _parts.Add(part);
            part.Item = GraphicsCapture.CreateItemForMonitor(monitor.Handle);
            part.PoolSize = GraphicsCapture.ItemSize(part.Item);
            // The part offsets assume the capture covers the monitor pixel for pixel.
            if (part.PoolSize.Width != monitor.Bounds.Width || part.PoolSize.Height != monitor.Bounds.Height)
                throw new InvalidOperationException("The monitor capture does not match the monitor's size.");
            part.Staging = CreateStaging(r.Width, r.Height);
            part.Pool = GraphicsCapture.CreateFreeThreadedPool(_device.WinRT, 2, part.PoolSize);
            part.Session = GraphicsCapture.CreateSession(part.Pool, part.Item);
            try { GraphicsCapture.ConfigureSession(part.Session, includeCursor); } catch { }
        }
        if (_parts.Count == 0) throw new InvalidOperationException("The area is not on any monitor.");
        foreach (var part in _parts) GraphicsCapture.StartCapture(part.Session);
    }

    private bool WaitForFirstFrames(int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            if (_parts.TrueForAll(p => p.HasFrame)) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    public bool Grab(byte[] bgra)
    {
        Pump();
        if (!_parts.TrueForAll(p => p.HasFrame)) return false;
        fixed (byte* dst = bgra)
        {
            foreach (var part in _parts)
            {
                D3D11_MAPPED_SUBRESOURCE map;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Slot(_context, Slot_Map))(_context, part.Staging, 0, D3D11_MAP_READ, 0, &map);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    int row = part.W * 4;
                    for (int y = 0; y < part.H; y++)
                        Buffer.MemoryCopy((byte*)map.pData + (long)y * map.RowPitch, dst + ((long)(part.Y + y) * Width + part.X) * 4, row, row);
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, Slot_Unmap))(_context, part.Staging, 0);
                }
            }
        }
        return true;
    }

    /// <summary>Takes the newest frame from each monitor, when one arrived, and copies the
    /// recorded part of it into that monitor's staging texture. A monitor that has not changed
    /// sends no frame and keeps its last picture.</summary>
    private void Pump()
    {
        foreach (var part in _parts)
        {
            IntPtr latest = IntPtr.Zero;
            for (IntPtr f = GraphicsCapture.TryGetNextFrame(part.Pool); f != IntPtr.Zero; f = GraphicsCapture.TryGetNextFrame(part.Pool))
            {
                GraphicsCapture.Close(ref latest);
                latest = f;
            }
            if (latest == IntPtr.Zero) continue;
            GraphicsCapture.SizeInt32 size;
            try
            {
                size = GraphicsCapture.FrameContentSize(latest);
                CopyPart(part, latest, size.Width, size.Height);
            }
            finally { GraphicsCapture.Close(ref latest); }
            if (size.Width != part.PoolSize.Width || size.Height != part.PoolSize.Height)
            {
                part.PoolSize = size;
                GraphicsCapture.RecreatePool(part.Pool, _device!.WinRT, 2, size);
            }
        }
    }

    private void CopyPart(Part part, IntPtr frame, int contentW, int contentH)
    {
        int right = Math.Min(part.SrcX + part.W, contentW), bottom = Math.Min(part.SrcY + part.H, contentH);
        if (right <= part.SrcX || bottom <= part.SrcY) return;
        IntPtr tex = GraphicsCapture.FrameTexture(frame);
        try
        {
            var box = new D3D11_BOX { Left = (uint)part.SrcX, Top = (uint)part.SrcY, Front = 0, Right = (uint)right, Bottom = (uint)bottom, Back = 1 };
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Slot(_context, Slot_CopySubresourceRegion))(_context, part.Staging, 0, 0, 0, 0, tex, 0, &box);
            part.HasFrame = true;
        }
        finally { Marshal.Release(tex); }
    }

    private IntPtr CreateStaging(int w, int h)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        IntPtr tex;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Slot(_d3dDevice, Slot_CreateTexture2D))(_d3dDevice, &desc, IntPtr.Zero, &tex);
        Marshal.ThrowExceptionForHR(hr);
        return tex;
    }

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    public void Dispose()
    {
        foreach (var part in _parts)
        {
            try { GraphicsCapture.Close(ref part.Session); } catch { }
            try { GraphicsCapture.Close(ref part.Pool); } catch { }
            try { GraphicsCapture.Close(ref part.Item); } catch { }
            if (part.Staging != IntPtr.Zero) Marshal.Release(part.Staging);
        }
        _parts.Clear();
        try { _device?.Dispose(); } catch { }
        _device = null;
        _context = IntPtr.Zero;
        _d3dDevice = IntPtr.Zero;
    }
}
