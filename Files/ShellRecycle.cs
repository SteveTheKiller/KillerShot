using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace KillerShot.Files
{
    /// <summary>
    /// The Recycle Bin through the shell's IFileOperation. It replaced SHFileOperation, which is
    /// capped at MAX_PATH and reports one code for a whole batch: IFileOperation takes shell
    /// items, so a path past 260 characters works, and its progress sink reports a real HRESULT
    /// for every item. IFileOperation only runs in a single-threaded apartment, so the work goes
    /// on a dedicated STA thread with the KillerShot window as owner of the shell's progress and
    /// "cannot be recycled" dialogs.
    /// </summary>
    internal static class ShellRecycle
    {
        /// <summary>For the self-test: the apartment the shell call ran in and the owner window it was given.</summary>
        internal static Action<ApartmentState, IntPtr>? Observed;

        internal sealed class ItemResult
        {
            public string Path = "";
            public int HResult = unchecked((int)0x80004005);
            public bool Recycled;   // a Recycle Bin item came back, not a permanent delete
            public bool Reported;
        }

        internal static (List<ItemResult> Items, bool Aborted, int Hr) Run(IReadOnlyList<string> paths, uint flags, IntPtr owner)
        {
            (List<ItemResult>, bool, int) outcome = default;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { outcome = RunHere(paths, flags, owner); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "KillerShot recycle" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new IOException(failure.Message, failure);
            return outcome;
        }

        private static (List<ItemResult>, bool, int) RunHere(IReadOnlyList<string> paths, uint flags, IntPtr owner)
        {
            Observed?.Invoke(Thread.CurrentThread.GetApartmentState(), owner);
            var items = new List<ItemResult>();
            var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation, true)!)!;
            var sink = new Sink(items);
            uint cookie = 0;
            try
            {
                Check(op.SetOperationFlags(flags));
                if (owner != IntPtr.Zero) Check(op.SetOwnerWindow(owner));
                Check(op.Advise(sink, out cookie));
                int queued = 0;
                foreach (string path in paths)
                {
                    var item = new ItemResult { Path = path };
                    items.Add(item);
                    IShellItem? shell = Item(path, out int hr);
                    if (shell == null) { item.HResult = hr; item.Reported = true; continue; }
                    sink.Expect(shell, item);
                    hr = op.DeleteItem(shell, null);
                    if (hr != 0) { item.HResult = hr; item.Reported = true; continue; }
                    queued++;
                }
                int perform = queued > 0 ? op.PerformOperations() : 0;
                op.GetAnyOperationsAborted(out bool aborted);
                return (items, aborted, perform);
            }
            finally
            {
                if (cookie != 0) op.Unadvise(cookie);
                Marshal.FinalReleaseComObject(op);
            }
        }

        /// <summary>A shell item for the path. A long path is tried again in its \\?\ form.</summary>
        private static IShellItem? Item(string path, out int hr)
        {
            Guid iid = IID_IShellItem;
            hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem? item);
            if (hr == 0 && item != null) return item;
            if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                int retry = SHCreateItemFromParsingName(@"\\?\" + path, IntPtr.Zero, ref iid, out item);
                if (retry == 0 && item != null) { hr = 0; return item; }
            }
            return null;
        }

        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        private static string Parsing(IShellItem item)
        {
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr name) != 0 || name == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(name) ?? ""; }
            finally { Marshal.FreeCoTaskMem(name); }
        }

        private static string Normal(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            return path.TrimEnd('\\');
        }

        [ComVisible(true)]
        private sealed class Sink(List<ItemResult> items) : IFileOperationProgressSink
        {
            private readonly Dictionary<string, ItemResult> _byPath = new(StringComparer.OrdinalIgnoreCase);

            public void Expect(IShellItem item, ItemResult result)
            {
                string path = Normal(Parsing(item));
                _byPath[path.Length > 0 ? path : Normal(result.Path)] = result;
            }

            public int PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem? newlyCreated)
            {
                string path = Normal(Parsing(item));
                if (!_byPath.TryGetValue(path, out var result))
                    result = items.Find(i => !i.Reported && string.Equals(Normal(i.Path), path, StringComparison.OrdinalIgnoreCase))!;
                if (result != null)
                {
                    result.HResult = hrDelete;
                    result.Recycled = hrDelete >= 0 && newlyCreated != null;
                    result.Reported = true;
                }
                return 0;
            }

            public int StartOperations() => 0;
            public int FinishOperations(int hrResult) => 0;
            public int PreRenameItem(uint flags, IShellItem item, string? newName) => 0;
            public int PostRenameItem(uint flags, IShellItem item, string? newName, int hr, IShellItem? created) => 0;
            public int PreMoveItem(uint flags, IShellItem item, IShellItem folder, string? newName) => 0;
            public int PostMoveItem(uint flags, IShellItem item, IShellItem folder, string? newName, int hr, IShellItem? created) => 0;
            public int PreCopyItem(uint flags, IShellItem item, IShellItem folder, string? newName) => 0;
            public int PostCopyItem(uint flags, IShellItem item, IShellItem folder, string? newName, int hr, IShellItem? created) => 0;
            public int PreDeleteItem(uint flags, IShellItem item) => 0;
            public int PreNewItem(uint flags, IShellItem folder, string? newName) => 0;
            public int PostNewItem(uint flags, IShellItem folder, string? newName, string? template, uint attributes, int hr, IShellItem? created) => 0;
            public int UpdateProgress(uint total, uint done) => 0;
            public int ResetTimer() => 0;
            public int PauseTimer() => 0;
            public int ResumeTimer() => 0;
        }

        // ── interop ───────────────────────────────────────────────

        private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
        private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr properties);
            [PreserveSig] int SetOwnerWindow(IntPtr owner);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
            [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IFileOperationProgressSink? sink);
            [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
            [PreserveSig] int MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
            [PreserveSig] int MoveItems(IntPtr items, IShellItem folder);
            [PreserveSig] int CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
            [PreserveSig] int CopyItems(IntPtr items, IShellItem folder);
            [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
            [PreserveSig] int DeleteItems(IntPtr items);
            [PreserveSig] int NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
                [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int hrResult);
            [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
            [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int hr, IShellItem? created);
            [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
            [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int hr, IShellItem? created);
            [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
            [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int hr, IShellItem? created);
            [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
            [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem? newlyCreated);
            [PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
            [PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
                [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int hr, IShellItem? created);
            [PreserveSig] int UpdateProgress(uint total, uint done);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }
    }
}
