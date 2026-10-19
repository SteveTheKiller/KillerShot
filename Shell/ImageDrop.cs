using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private DropTargetHelper? _dropImage;

    private void InitImageDrop()
    {
        AllowDrop = true;
        PreviewDragEnter += (_, e) =>
        {
            UpdateDropEffect(e);
            EndDropImage();
            if ((object)e.Data is System.Runtime.InteropServices.ComTypes.IDataObject data)
            {
                _dropImage = new DropTargetHelper();
                _dropImage.Enter(new WindowInteropHelper(this).Handle, data, PointToScreen(e.GetPosition(this)), (int)e.Effects);
            }
        };
        PreviewDragOver += (_, e) =>
        {
            UpdateDropEffect(e);
            _dropImage?.Over(PointToScreen(e.GetPosition(this)), (int)e.Effects);
        };
        PreviewDragLeave += (_, _) => EndDropImage();
        PreviewDrop += (_, e) =>
        {
            UpdateDropEffect(e);
            if ((object)e.Data is System.Runtime.InteropServices.ComTypes.IDataObject data)
                _dropImage?.Drop(data, PointToScreen(e.GetPosition(this)), (int)e.Effects);
            EndDropImage();
            if (e.Effects == DragDropEffects.None) return;
            try { if (!DropImages(e.Data)) e.Effects = DragDropEffects.None; }
            catch (Exception ex) { StatusText.Text = string.Format(Loc("Str_Status_OpenFailed"), ex.Message); e.Effects = DragDropEffects.None; }
        };
        Closed += (_, _) => EndDropImage();
    }

    private void UpdateDropEffect(DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = !ImageDrag.Active && (e.AllowedEffects & DragDropEffects.Copy) != 0 && HasImages(e.Data)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private static bool HasImages(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
                return data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && paths.All(IsImagePath);
            return data.GetDataPresent(DataFormats.Bitmap);
        }
        catch { return false; }
    }

    private static bool IsImagePath(string path) => File.Exists(path) && Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";

    internal bool DropImages(IDataObject data)
    {
        if (ImageDrag.Active || !HasImages(data)) return false;
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            // A single dropped file follows Open, including the existing unsaved-edit prompt.
            if (!Library.IsVisible) return OpenPath(paths[0]);
            foreach (string path in paths) AddDroppedImage(ImageOutput.Load(path), File.GetLastWriteTime(path), Path.GetFileNameWithoutExtension(path));
        }
        else if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if (Library.IsVisible) AddDroppedImage(bitmap, DateTime.Now, "");
            else return App.Instance.SetDocument(new Editor.EditorDocument(bitmap));
        }
        return true;
    }

    private void AddDroppedImage(BitmapSource image, DateTime taken, string title)
    {
        if (FolderCaptures.IsSource(_navigationStrip?.Source)) { App.Instance.SetDocument(new Editor.EditorDocument(image)); return; }
        if (!CaptureLibrary.IsOpen) throw new InvalidOperationException(CaptureLibrary.LoadError ?? Loc("Str_Lib_LockedTitle"));
        long? collection = _navigationStrip?.Source is { } source && source.StartsWith('c') && long.TryParse(source.AsSpan(1), NumberStyles.None,
            CultureInfo.InvariantCulture, out long id) && id > 0 ? id : null;
        long added = CaptureLibrary.AddNow(image, taken, "", title, null);
        if (collection.HasValue) CaptureLibrary.Move([added], collection);
    }

    private void EndDropImage()
    {
        _dropImage?.Leave();
        _dropImage?.Dispose();
        _dropImage = null;
    }
}
