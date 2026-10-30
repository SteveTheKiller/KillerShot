using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace KillerShot.Services;

internal static class ImageDrag
{
    private static string ExportRoot = Path.Combine(Path.GetTempPath(), "KillerShot-Drag");
    private static string ExportFolder = Path.Combine(ExportRoot, Guid.NewGuid().ToString("N"));
    private static FileStream? _owner;
    private static System.Windows.Threading.DispatcherTimer? _cleanupTimer;
    internal static bool Active { get; private set; }

    internal static void Initialize(string? root = null)
    {
        if (root != null)
        {
            ExportRoot = Path.GetFullPath(root);
            ExportFolder = Path.Combine(ExportRoot, Guid.NewGuid().ToString("N"));
        }
        ReclaimAbandoned();
        if (_cleanupTimer != null) return;
        _cleanupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _cleanupTimer.Tick += (_, _) => ReclaimAbandoned();
        _cleanupTimer.Start();
    }

    internal static void ReclaimAbandoned()
    {
        try
        {
            if (!Directory.Exists(ExportRoot) || IsLink(ExportRoot)) return;
            foreach (string session in Directory.EnumerateDirectories(ExportRoot))
                DeleteSession(session, DateTime.UtcNow.AddHours(-24));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void DeleteSession(string session, DateTime cutoff)
    {
        var handles = new List<FileStream>();
        try
        {
            if (!Guid.TryParseExact(Path.GetFileName(session), "N", out _) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(session)), Path.GetFullPath(ExportRoot), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(session) || IsLink(ExportRoot) || IsLink(session) ||
                Directory.GetLastWriteTimeUtc(session) > cutoff || Directory.EnumerateDirectories(session).Any()) return;
            string[] files = Directory.GetFiles(session);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                // Drag exports carry a GUID; files handed to other programs carry the capture time.
                bool png = name.StartsWith("KillerShot ", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    (Guid.TryParseExact(name[11..^4], "N", out _) || DateTime.TryParseExact(name[11..^4], "yyyy-MM-dd HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _));
                if ((name != ".session.lock" && !png) || IsLink(file) || File.GetLastWriteTimeUtc(file) > cutoff) return;
                // Hold every file before deleting any. A live owner or consumer preserves the session.
                handles.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
            }
            foreach (string file in files)
            {
                if (IsLink(ExportRoot) || IsLink(session) || IsLink(file)) return;
                File.Delete(file);
            }
            foreach (var handle in handles) handle.Dispose();
            handles.Clear();
            if (!IsLink(ExportRoot) && !IsLink(session)) Directory.Delete(session, false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    // Keep successful exports readable for targets that finish reading after Drop returns.
    internal static void Cleanup()
    {
        _cleanupTimer?.Stop();
        _owner?.Dispose();
        _owner = null;
        DeleteSession(ExportFolder, DateTime.MaxValue);
    }

    internal static void Start(BitmapSource image)
    {
        if (Active) return;
        using var transfer = CreateTransfer(image);
        int effect = 0;
        var cursor = Mouse.OverrideCursor;
        try
        {
            Active = true;
            Mouse.Capture(null);
            Mouse.OverrideCursor = DragCursors.Closed;
            NativeDoDragDrop(transfer.Data, new ImageDropSource(), (int)DragDropEffects.Copy, out effect);
            transfer.Completed = effect == (int)DragDropEffects.Copy;
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
            Active = false;
        }
    }

    /// <summary>A PNG copy for handing to another program, in this session's export folder, so it
    /// is cleaned up with the drag exports when it expires.</summary>
    internal static string ExportFile(BitmapSource image)
    {
        Directory.CreateDirectory(ExportRoot);
        if (IsLink(ExportRoot)) throw new IOException("The drag export folder cannot be a link.");
        Directory.CreateDirectory(ExportFolder);
        if (IsLink(ExportFolder)) throw new IOException("The drag session folder cannot be a link.");
        _owner ??= new FileStream(Path.Combine(ExportFolder, ".session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        string path = Path.Combine(ExportFolder, "KillerShot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
        if (File.Exists(path)) path = Path.Combine(ExportFolder, "KillerShot " + Guid.NewGuid().ToString("N") + ".png");
        ImageOutput.SavePng(image, path);
        return path;
    }

    internal static ImageTransfer CreateTransfer(BitmapSource image)
    {
        Directory.CreateDirectory(ExportRoot);
        if (IsLink(ExportRoot)) throw new IOException("The drag export folder cannot be a link.");
        Directory.CreateDirectory(ExportFolder);
        if (IsLink(ExportFolder)) throw new IOException("The drag session folder cannot be a link.");
        _owner ??= new FileStream(Path.Combine(ExportFolder, ".session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        return new(image, ExportFolder);
    }

    internal sealed class ImageTransfer : IDisposable
    {
        private readonly string _path;
        private readonly DragImage.DragSourceHelper? _helper;
        internal ComDataObject Data { get; }
        internal bool Completed { get; set; }
        private bool _disposed;

        internal ImageTransfer(BitmapSource image, string folder)
        {
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "KillerShot " + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                ImageOutput.SavePng(image, _path);
                Data = ShellDataObject.Create([_path]) ?? throw new IOException("Windows could not prepare the image for dragging.");
                _helper = DragImage.Attach(Data, image, 112, .8);
            }
            catch { try { File.Delete(_path); } catch (IOException) { } throw; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _helper?.Dispose();
            Marshal.FinalReleaseComObject(Data);
            if (!Completed) try { File.Delete(_path); } catch (IOException) { }
        }
    }

    /// <summary>The file a drag of this capture hands over, or null for a picture. A recording
    /// drags as its video: a folder clip as the file itself, a stored clip as a decrypted copy in
    /// the playback folder, which is wiped with the playback copies.</summary>
    internal static string? DragFile(CaptureItem item) => VideoCaptures.IsVideo(item) ? VideoCaptures.PlayablePath(item) : null;

    /// <summary>Drags a library capture: a recording as its video file, anything else as the
    /// flattened picture every album view drags, so a redaction is never bypassed.</summary>
    internal static void AttachCapture(FrameworkElement owner, Func<Point, CaptureItem?> capture, bool? verticalNavigation = null, bool capturePointer = false) =>
        AttachCore(owner, point => capture(point) is { } item ? () => StartCapture(item) : null, verticalNavigation, capturePointer);

    private static void StartCapture(CaptureItem item)
    {
        if (DragFile(item) is { } file) StartFile(file, CaptureLibrary.LoadThumb(item));
        else if (CaptureLibrary.LoadOutput(item) is { } image) Start(image);
    }

    /// <summary>Drags an existing file, with its preview under the pointer.</summary>
    internal static void StartFile(string path, BitmapSource? preview)
    {
        if (Active) return;
        var data = ShellDataObject.Create([path]) ?? throw new IOException("Windows could not prepare the file for dragging.");
        var helper = preview != null ? DragImage.Attach(data, preview, 112, .8) : null;
        var cursor = Mouse.OverrideCursor;
        try
        {
            Active = true;
            Mouse.Capture(null);
            Mouse.OverrideCursor = DragCursors.Closed;
            NativeDoDragDrop(data, new ImageDropSource(), (int)DragDropEffects.Copy, out _);
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
            Active = false;
            helper?.Dispose();
            Marshal.FinalReleaseComObject(data);
        }
    }

    internal static void Attach(FrameworkElement owner, Func<Point, Func<BitmapSource?>?> picture, bool? verticalNavigation = null, bool capturePointer = false) =>
        AttachCore(owner, point => picture(point) is { } load ? () => { if (load() is { } image) Start(image); } : null, verticalNavigation, capturePointer);

    private static void AttachCore(FrameworkElement owner, Func<Point, Action?> drag, bool? verticalNavigation, bool capturePointer)
    {
        Point press = default;
        Action? pending = null;
        owner.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pending = e.ClickCount == 1 ? drag(e.GetPosition(owner)) : null;
            press = e.GetPosition(owner);
            if (capturePointer && pending != null) owner.CaptureMouse();
        };
        owner.PreviewMouseLeftButtonUp += (_, _) => { pending = null; if (capturePointer && owner.IsMouseCaptured) owner.ReleaseMouseCapture(); };
        owner.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) { pending = null; return; }
            if (pending == null) return;
            var delta = e.GetPosition(owner) - press;
            double x = Math.Abs(delta.X), y = Math.Abs(delta.Y);
            bool ready = verticalNavigation switch
            {
                true => x >= 12 && x > y * 1.25,
                false => y >= 12 && y > x * 1.25,
                _ => x >= SystemParameters.MinimumHorizontalDragDistance || y >= SystemParameters.MinimumVerticalDragDistance,
            };
            if (!ready) return;
            var start = pending;
            pending = null;
            e.Handled = true;
            try { start(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        };
    }

    [DllImport("ole32.dll", EntryPoint = "DoDragDrop")]
    private static extern int NativeDoDragDrop(ComDataObject data, IDropSource source, int effects, out int effect);

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(bool escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    private sealed class ImageDropSource : IDropSource
    {
        public int QueryContinueDrag(bool escapePressed, int keyState) => escapePressed ? 0x40101 : (keyState & 1) == 0 ? 0x40100 : 0;
        public int GiveFeedback(int effect)
        {
            if (effect == 0) return 0x40102;
            Mouse.SetCursor(DragCursors.Closed);
            return 0;
        }
    }
}
