using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Input;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-ksfg &lt;new folder&gt;. One check per
/// review finding of the KS-F and KS-G slices (FIL-002 to FIL-007, LIB-003, SEC-005). Settings,
/// playback copies, the Screenshots folder and every file it recycles live inside the folder.
/// The window it shows sits far off screen; run it on a hidden desktop. report.txt lists each
/// finding as pass or FAIL.</summary>
internal static class SelfTestKsfg
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-ksfg") return false;
        string dir = Path.GetFullPath(args[1]);
        if (Directory.Exists(dir) || File.Exists(dir)) throw new IOException("Use a new self-test folder.");
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        Settings.UseFolder(Path.Combine(dir, "settings"));
        VideoCaptures.UsePlaybackFolder(Path.Combine(dir, "playback"));
        FolderCaptures.UseFolder(Path.Combine(dir, "screenshots"));
        FolderCaptures.Enabled = false;
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAsync(string dir)
    {
        var log = new StringBuilder();
        int failures = 0;
        async Task Check(string id, Func<Task> body)
        {
            try { await body(); log.AppendLine($"{id}=pass"); }
            catch (Exception error) { failures++; log.AppendLine($"{id}=FAIL {error.GetType().Name}: {error.Message}"); }
        }
        Task Sync(Action body) { body(); return Task.CompletedTask; }

        Window? window = null;
        try
        {
            App.InitializeKit();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Settings.Set("Files.Places", "");
            var view = new FilesView();
            window = new TestWindow { Content = view, Width = 900, Height = 600, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();

            await Check("FIL-002", () => Sync(DataObjectReportsMissingFormat));
            await Check("FIL-003", () => Task.Run(() => LongPathRecycles(dir)));
            await Check("FIL-004", () => RecycleDialogsOwned(dir, view, window));
            await Check("FIL-005", () => SameFolderCopy(dir, view));
            await Check("FIL-006", () => RenameCommitsOnLeave(dir, view, (TestWindow)window));
            await Check("FIL-007", () => Sync(() => TrailingDotsAndSpaces(dir)));
            await Check("LIB-003", () => LateErrorKeepsReplacement(dir));
            await Check("SEC-005", () => Task.Run(() => HeldCopiesRetried(dir)));
        }
        catch (Exception error) { failures++; log.AppendLine("setup=FAIL " + error); }
        finally { window?.Close(); }

        log.AppendLine(failures == 0 ? "result=ok" : $"result=failed {failures}");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    private sealed class TestWindow : Window
    {
        public void Deactivate() => OnDeactivated(EventArgs.Empty);
    }

    // ---- FIL-002: a native GetData for a missing format answers DV_E_FORMATETC ----

    private static void DataObjectReportsMissingFormat()
    {
        var data = new NativeDataObject();
        try
        {
            data.SetHGlobal(NativeDataObject.CF_UNICODETEXT, NativeDataObject.BuildUnicodeText("hello"));
            // The managed path still answers without throwing, for in-process drops.
            var missing = new FORMATETC { cfFormat = NativeDataObject.CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            ((System.Runtime.InteropServices.ComTypes.IDataObject)data).GetData(ref missing, out var empty);
            Require(empty.tymed == TYMED.TYMED_NULL, "The managed GetData handed out a medium for a missing format.");

            IntPtr unknown = Marshal.GetIUnknownForObject(data);
            Guid iid = new("0000010e-0000-0000-C000-000000000046");
            Require(Marshal.QueryInterface(unknown, in iid, out IntPtr pointer) == 0, "QueryInterface for IDataObject failed.");
            Marshal.Release(unknown);
            try
            {
                IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), 3 * IntPtr.Size);
                var getData = Marshal.GetDelegateForFunctionPointer<RawGetData>(slot);
                STGMEDIUM medium = default;
                int hr = getData(pointer, ref missing, out medium);
                Require(hr == unchecked((int)0x80040064), $"GetData for a missing format returned 0x{hr:X8}, not DV_E_FORMATETC.");
                var present = new FORMATETC { cfFormat = NativeDataObject.CF_UNICODETEXT, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
                hr = getData(pointer, ref present, out medium);
                Require(hr == 0 && medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero,
                    $"GetData for a held format returned 0x{hr:X8}.");
                ReleaseStgMedium(ref medium);
            }
            finally { Marshal.Release(pointer); }
        }
        finally { data.ReleaseAll(); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RawGetData(IntPtr self, ref FORMATETC format, out STGMEDIUM medium);

    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    // ---- FIL-003: an item past MAX_PATH goes to the Recycle Bin, failures say why per item ----

    private static void LongPathRecycles(string dir)
    {
        string deep = Path.Combine(dir, "long");
        while (deep.Length < 300) deep = Path.Combine(deep, "segment-" + new string('x', 40));
        Directory.CreateDirectory(deep);
        string file = Path.Combine(deep, "long-path-file.txt");
        File.WriteAllText(file, "recycle me");
        Require(file.Length > 260, "The fixture path is not past MAX_PATH.");
        string missing = Path.Combine(dir, "never-there.txt");
        var result = FileOps.Recycle([file, missing], silent: true);
        Require(!File.Exists(file), $"The long-path file was not recycled ({string.Join("; ", result.Failed.Select(f => f.Error))}).");
        Require(result.Succeeded == 1, $"Recycle counted {result.Succeeded} successes, expected 1.");
        Require(result.Failed.Count == 1 && string.Equals(result.Failed[0].Path, missing, StringComparison.OrdinalIgnoreCase),
            "The missing item was not the one failure.");
        Require(!result.Failed[0].Error.Contains("SHFileOperation", StringComparison.Ordinal) && result.Failed[0].Error.Length > 0,
            $"The failure is not a real per-item reason: '{result.Failed[0].Error}'.");
    }

    // ---- FIL-004: the shell's recycle dialogs run on an STA thread owned by the window ----

    private static async Task RecycleDialogsOwned(string dir, FilesView view, Window window)
    {
        string folder = Directory.CreateDirectory(Path.Combine(dir, "owned")).FullName;
        File.WriteAllText(Path.Combine(folder, "bin-me.txt"), "x");
        Require(await view.NavigateTo(folder), "The recycle fixture did not open.");
        await WaitUntil(() => view.Navigation.Results.Any(e => e.FileName == "bin-me.txt"));
        view.ResultsList.SelectedItem = view.Navigation.Results.Single(e => e.FileName == "bin-me.txt");
        ApartmentState? apartment = null;
        IntPtr owner = IntPtr.Zero;
        Files.ShellRecycle.Observed = (state, hwnd) => { apartment = state; owner = hwnd; };
        try { await view.RecycleSelection(); }
        finally { Files.ShellRecycle.Observed = null; }
        IntPtr expected = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Require(apartment == ApartmentState.STA, $"The recycle ran in {apartment?.ToString() ?? "no observed"} apartment, not STA.");
        Require(owner == expected && owner != IntPtr.Zero, $"The recycle dialogs were owned by 0x{owner:X}, not the window 0x{expected:X}.");
        Require(!File.Exists(Path.Combine(folder, "bin-me.txt")), "The selected file was not recycled.");
    }

    // ---- FIL-005: Ctrl+drag copy into the item's own folder makes "name - Copy" ----

    private static async Task SameFolderCopy(string dir, FilesView view)
    {
        string folder = Directory.CreateDirectory(Path.Combine(dir, "same")).FullName;
        string file = Path.Combine(folder, "report.txt");
        File.WriteAllText(file, "report");
        Require(await view.NavigateTo(folder), "The drop fixture did not open.");
        DataObject Drop() { var d = new DataObject(); d.SetFileDropList([file]); return d; }
        var copied = await view.DropFiles(Drop(), folder, DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.Control);
        Require(File.Exists(Path.Combine(folder, "report - Copy.txt")) && File.ReadAllText(Path.Combine(folder, "report - Copy.txt")) == "report",
            $"A Ctrl+drag copy into the same folder made no 'report - Copy.txt' (result {(copied == null ? "null" : copied.Succeeded.ToString())}).");
        await view.DropFiles(Drop(), folder, DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.Control);
        Require(File.Exists(Path.Combine(folder, "report - Copy (2).txt")), "A second copy did not make 'report - Copy (2).txt'.");
        int before = Directory.GetFiles(folder).Length;
        var moved = await view.DropFiles(Drop(), folder, DragDropEffects.Copy | DragDropEffects.Move, ModifierKeys.Shift);
        Require(moved == null && File.Exists(file) && Directory.GetFiles(folder).Length == before, "A same-folder move was not a no-op.");
    }

    // ---- FIL-006: leaving the rename box commits; Escape cancels ----

    private static async Task RenameCommitsOnLeave(string dir, FilesView view, TestWindow window)
    {
        string folder = Directory.CreateDirectory(Path.Combine(dir, "rename")).FullName;
        foreach (string name in new[] { "one.txt", "two.txt", "three.txt" }) File.WriteAllText(Path.Combine(folder, name), name);
        Require(await view.NavigateTo(folder), "The rename fixture did not open.");
        await WaitUntil(() => view.Navigation.Results.Count == 3);
        window.UpdateLayout();
        var box = (System.Windows.Controls.TextBox)view.FindName("RenameBox");

        async Task Rename(string from, string to, Action leave)
        {
            view.ResultsList.SelectedItem = view.Navigation.Results.Single(e => e.FileName == from);
            window.UpdateLayout();
            Require(view.BeginRename(), "Rename did not open for " + from);
            box.Focus();
            box.Text = to;
            leave();
            await view.CurrentCommand;
            await WaitUntil(() => view.Navigation.Results.Any(e => e.FileName == to) || !File.Exists(Path.Combine(folder, from)));
        }

        await Rename("one.txt", "one-renamed.txt", () => Keyboard.Focus(view.ResultsList));
        Require(File.Exists(Path.Combine(folder, "one-renamed.txt")) && !File.Exists(Path.Combine(folder, "one.txt")),
            "Moving focus out of the rename box threw the typed name away.");
        await Rename("two.txt", "two-renamed.txt", window.Deactivate);
        Require(File.Exists(Path.Combine(folder, "two-renamed.txt")) && !File.Exists(Path.Combine(folder, "two.txt")),
            "Deactivating the window threw the typed name away.");
        await Rename("three.txt", "three-canceled.txt", () => box.RaiseEvent(
            new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent }));
        Require(File.Exists(Path.Combine(folder, "three.txt")) && !File.Exists(Path.Combine(folder, "three-canceled.txt"))
            && box.Visibility != Visibility.Visible, "Escape did not cancel the rename.");
    }

    // ---- FIL-007: trailing dots and spaces are dropped the way Explorer drops them ----

    private static void TrailingDotsAndSpaces(string dir)
    {
        string folder = Directory.CreateDirectory(Path.Combine(dir, "dots")).FullName;
        string a = Path.Combine(folder, "a.txt");
        File.WriteAllText(a, "a");
        string? error = FileOps.Rename(a, "a.txt.");
        Require(error == null && File.Exists(a), $"Renaming a.txt to 'a.txt.' answered '{error}' instead of leaving it as it is.");
        error = FileOps.Rename(a, "b.txt. . ");
        Require(error == null && File.Exists(Path.Combine(folder, "b.txt")) && !File.Exists(a),
            $"Renaming to 'b.txt. . ' answered '{error}' instead of making b.txt.");
        error = FileOps.Rename(Path.Combine(folder, "b.txt"), ". .");
        Require(error == "empty name", $"A name of only dots and spaces answered '{error}', not the empty-name message.");
    }

    // ---- LIB-003: a late error from a replaced linked-folder watcher leaves the new one alone ----

    private sealed class LateWatcher(string path) : FileSystemWatcher(path)
    {
        public bool WasDisposed;
        public void Fail() => OnError(new ErrorEventArgs(new InternalBufferOverflowException()));
        // Keeps its handlers, as a watcher whose error is already in flight on another thread does.
        protected override void Dispose(bool disposing) { WasDisposed = true; EnableRaisingEvents = false; }
        public void RealDispose() => base.Dispose(true);
    }

    private static async Task LateErrorKeepsReplacement(string dir)
    {
        string photos = Directory.CreateDirectory(Path.Combine(dir, "linked")).FullName;
        File.WriteAllText(Path.Combine(photos, "a.txt"), "a");
        var previous = FolderCaptures.WatcherFactory;
        var made = new List<LateWatcher>();
        FolderCaptures.WatcherFactory = path => { var w = new LateWatcher(path); made.Add(w); return w; };
        string? source = null;
        try
        {
            source = FolderCaptures.AddFolder(photos);
            await FolderCaptures.RefreshFolderAsync(source);
            Require(made.Count == 1, $"The linked folder made {made.Count} watchers on its first scan.");
            made[0].Fail();
            await FolderCaptures.RefreshFolderAsync(source);
            Require(made.Count == 2, $"The watcher was not replaced after its error ({made.Count} made).");
            made[0].Fail();   // late error from the replaced watcher
            await FolderCaptures.RefreshFolderAsync(source);
            Require(!made[1].WasDisposed, "A late error from the old watcher disposed its replacement.");
            Require(made.Count == 2, $"A late error from the old watcher forced another rebuild ({made.Count} made).");
        }
        finally
        {
            if (source != null) FolderCaptures.RemoveFolder(source);
            FolderCaptures.WatcherFactory = previous;
            foreach (var w in made) w.RealDispose();
        }
    }

    // ---- SEC-005: copies still held at the wipe are retried, not left for the next start ----

    private static async Task HeldCopiesRetried(string dir)
    {
        string playback = VideoCaptures.PlaybackFolder;
        Directory.CreateDirectory(playback);
        string clip = Path.Combine(playback, "KillerShot recording leftover.mp4");
        File.WriteAllText(clip, "plaintext");
        string pdf = Directory.CreateDirectory(Path.Combine(playback, "PDF leftover")).FullName;
        string picture = Path.Combine(pdf, "1.png");
        File.WriteAllText(picture, "plaintext");
        var interval = VideoCaptures.PlaybackRetryInterval;
        VideoCaptures.PlaybackRetryInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            using (new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (new FileStream(picture, FileMode.Open, FileAccess.Read, FileShare.Read))
                VideoCaptures.WipePlayback();
            Require(File.Exists(clip) && Directory.Exists(pdf), "The held fixture was deleted while held, so the test proves nothing.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && (File.Exists(clip) || Directory.Exists(pdf))) await Task.Delay(50);
            Require(!File.Exists(clip), "A playback copy held during the wipe stayed on disk after it was released.");
            Require(!Directory.Exists(pdf), "A Make PDF folder held during the wipe stayed on disk after it was released.");
        }
        finally { VideoCaptures.PlaybackRetryInterval = interval; }
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
