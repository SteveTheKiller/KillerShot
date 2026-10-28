using System.Runtime.InteropServices;

namespace KillerShot.Native;

/// <summary>
/// The few Windows Runtime plumbing calls KillerShot needs, made directly against combase, so
/// it does not ship Microsoft's 25 MB projection of the whole Windows Runtime. Every IntPtr
/// handed out is an owned COM reference that the caller releases.
/// </summary>
internal static class WinRt
{
    private static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern unsafe char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    /// <summary>A vtable entry. IInspectable takes slots 0 to 5, so a WinRT interface's first
    /// method is slot 6; a plain IUnknown interface starts at 3.</summary>
    internal static unsafe void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    internal static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        Marshal.Release(com);
        com = IntPtr.Zero;
    }

    /// <summary>The interface, or zero when the object does not implement it.</summary>
    internal static IntPtr Query(IntPtr com, Guid iid)
        => Marshal.QueryInterface(com, in iid, out IntPtr result) >= 0 ? result : IntPtr.Zero;

    /// <summary>An HSTRING the caller deletes with <see cref="WindowsDeleteString"/>.</summary>
    internal static IntPtr CreateString(string value)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(value, value.Length, out IntPtr hstring));
        return hstring;
    }

    /// <summary>Reads and deletes an HSTRING.</summary>
    internal static unsafe string TakeString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        try
        {
            uint length;
            char* buffer = WindowsGetStringRawBuffer(hstring, &length);
            return new string(buffer, 0, (int)length);
        }
        finally { WindowsDeleteString(hstring); }
    }

    internal static IntPtr Factory(string runtimeClass, Guid iid)
    {
        IntPtr name = CreateString(runtimeClass);
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in iid, out IntPtr factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    /// <summary>Calls IClosable.Close when the object has it, then releases the reference.</summary>
    internal static unsafe void Close(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        IntPtr closable = Query(com, IID_IClosable);
        if (closable != IntPtr.Zero)
        {
            try { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, 6))(closable); }
            finally { Marshal.Release(closable); }
        }
        Release(ref com);
    }

    /// <summary>Waits for an IAsyncAction or IAsyncOperation to leave Started (0), then throws
    /// for Canceled (2) or Error (3).</summary>
    internal static async Task WaitAsync(IntPtr operation, int pollMs = 5)
    {
        IntPtr info = Query(operation, IID_IAsyncInfo);
        if (info == IntPtr.Zero) throw new InvalidOperationException("The Windows operation has no IAsyncInfo.");
        try
        {
            while (true)
            {
                int status = AsyncStatus(info);
                if (status == 1) return;
                if (status == 2) throw new OperationCanceledException("The Windows operation was canceled.");
                if (status == 3)
                {
                    int code = AsyncError(info);
                    Marshal.ThrowExceptionForHR(code < 0 ? code : unchecked((int)0x80004005));
                }
                await Task.Delay(pollMs);
            }
        }
        finally { Marshal.Release(info); }
    }

    private static unsafe int AsyncStatus(IntPtr info)
    {
        int status;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status));
        return status;
    }

    private static unsafe int AsyncError(IntPtr info)
    {
        int code;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &code) < 0 ? 0 : code;
    }

    /// <summary>IAsyncOperation&lt;T&gt;.GetResults for an interface result; the caller releases it.</summary>
    internal static unsafe IntPtr GetResults(IntPtr operation)
    {
        IntPtr result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, 8))(operation, &result));
        return result;
    }

    /// <summary>IVectorView&lt;T&gt;.Size.</summary>
    internal static unsafe uint VectorSize(IntPtr vector)
    {
        uint size;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(vector, 7))(vector, &size));
        return size;
    }

    /// <summary>IVectorView&lt;T&gt;.GetAt for an interface element; the caller releases it.</summary>
    internal static unsafe IntPtr VectorAt(IntPtr vector, uint index)
    {
        IntPtr item;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(vector, 6))(vector, index, &item));
        return item;
    }

    /// <summary>A getter returning an HSTRING at the given slot.</summary>
    internal static unsafe string GetString(IntPtr com, int slot)
    {
        IntPtr value;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(com, slot))(com, &value));
        return TakeString(value);
    }
}
