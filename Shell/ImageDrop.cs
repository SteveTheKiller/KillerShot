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
            if (Library.IsVisible && Library.FilesActive) { EndDropImage(); return; }
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
            if (Library.IsVisible && Library.FilesActive) return;
            UpdateDropEffect(e);
            _dropImage?.Over(PointToScreen(e.GetPosition(this)), (int)e.Effects);
        };
        PreviewDragLeave += (_, _) => EndDropImage();
        PreviewDrop += (_, e) =>
        {
            if (Library.IsVisible && Library.FilesActive) { EndDropImage(); return; }
            UpdateDropEffect(e);
            if ((object)e.Data is System.Runtime.InteropServices.ComTypes.IDataObject data)
                _dropImage?.Drop(data, PointToScreen(e.GetPosition(this)), (int)e.Effects);
            EndDropImage();
            if (e.Effects == DragDropEffects.None) return;
            if (ImageDrag.DraggedCapture is { } dragged)
            {
                try { if (!DropCapture(dragged, e.OriginalSource as DependencyObject)) e.Effects = DragDropEffects.None; }
                catch (Exception ex) { StatusText.Text = ex.Message; e.Effects = DragDropEffects.None; }
                return;
            }
            try { if (!DropImages(e.Data)) e.Effects = DragDropEffects.None; }
            catch (Exception ex) { StatusText.Text = string.Format(Loc("Str_Status_OpenFailed"), ex.Message); e.Effects = DragDropEffects.None; }
        };
        Closed += (_, _) => EndDropImage();
    }

    private void UpdateDropEffect(DragEventArgs e)
    {
        e.Handled = true;
        bool accept = (e.AllowedEffects & DragDropEffects.Copy) != 0 && (ImageDrag.DraggedCapture is { } dragged
            ? CanDropCapture(dragged, e.OriginalSource as DependencyObject, out _)
            : !ImageDrag.Active && HasImages(e.Data));
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>A capture dragged out of the album and dropped on a collection row in the sidebar
    /// moves into that collection, the same as Move to. The Unfiled row takes it out of its
    /// collection. All captures, folders and the capture's own collection refuse it.</summary>
    internal bool CanDropCapture(CaptureItem? item, DependencyObject? over, out long? collection)
    {
        collection = null;
        return item != null && !FolderCaptures.IsFolderId(item.Id) && CaptureLibrary.IsOpen && CaptureLibrary.IsCurrent(item)
            && TryCollectionTarget(CollectionRowSource(over), out collection) && collection != item.CollectionId;
    }

    internal bool DropCapture(CaptureItem item, DependencyObject? over)
    {
        if (!CanDropCapture(item, over, out long? collection)) return false;
        CaptureLibrary.Move([item.Id], collection, item.Session);
        ImageDrag.MarkHandledInApp();
        return true;
    }

    internal static bool TryCollectionTarget(string? source, out long? collection)
    {
        collection = null;
        if (source is not { Length: > 1 } || source[0] != 'c'
            || !long.TryParse(source.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long id)) return false;
        collection = id > 0 ? id : null;
        return true;
    }

    private static string? CollectionRowSource(DependencyObject? over)
    {
        for (var node = over; node != null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.ListBoxItem { DataContext: CollectionChoice choice }
                && System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(node) is CollectionBrowser)
                return choice.Source;
        }
        return null;
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
        if (Library.IsVisible && Library.FilesActive) return false;
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
