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
    private static readonly string ExportFolder = Path.Combine(Path.GetTempPath(), "KillerShot-Drag", Guid.NewGuid().ToString("N"));
    internal static bool Active { get; private set; }

    // Keep successful exports readable for targets that finish reading after Drop returns.
    internal static void Cleanup()
    {
        try { if (Directory.Exists(ExportFolder)) Directory.Delete(ExportFolder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    internal static ImageTransfer CreateTransfer(BitmapSource image) => new(image, ExportFolder);

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

    internal static void Attach(FrameworkElement owner, Func<Point, Func<BitmapSource?>?> picture, bool? verticalNavigation = null, bool capturePointer = false)
    {
        Point press = default;
        Func<BitmapSource?>? pending = null;
        owner.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pending = e.ClickCount == 1 ? picture(e.GetPosition(owner)) : null;
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
            var load = pending;
            pending = null;
            e.Handled = true;
            try { if (load() is { } image) Start(image); }
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
