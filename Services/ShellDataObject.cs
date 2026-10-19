using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace KillerShot.Services
{
    internal static class ShellDataObject
    {
        internal static ComDataObject? Create(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return null;

            var pidls = new IntPtr[paths.Count];
            IntPtr desktop = IntPtr.Zero;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    int hr = SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _);
                    if (hr < 0 || pidls[i] == IntPtr.Zero) return null;
                }

                if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DESKTOP, out desktop) < 0
                    || desktop == IntPtr.Zero)
                    return null;

                int hrCreate = CIDLData_CreateFromIDArray(desktop, (uint)pidls.Length, pidls, out ComDataObject data);
                return hrCreate < 0 ? null : data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DragDiag] ShellDataObject.Create threw: {ex}");
                return null;
            }
            finally
            {
                foreach (var p in pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
                if (desktop != IntPtr.Zero) Marshal.FreeCoTaskMem(desktop);
            }
        }

        private const int CSIDL_DESKTOP = 0x0000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindingContext,
                                                     out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int CIDLData_CreateFromIDArray(IntPtr pidlFolder, uint cidl,
                                                             [In] IntPtr[] apidl,
                                                             [MarshalAs(UnmanagedType.Interface)] out ComDataObject dataObject);
    }
}
