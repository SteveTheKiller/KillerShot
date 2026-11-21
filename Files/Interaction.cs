using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerShot.Files;

public partial class FilesView
{
    public Task CurrentCommand { get; private set; } = Task.CompletedTask;
    public DragTransfer? CreateDragData()
    {
        Dispatcher.VerifyAccess();
        _dragSeed ??= _dragSeedItem == null ? null : DataFor(_dragSeedItem);
        var rows = _dragSeed != null && _dragSelection.Contains(_dragSeed) ? _dragSelection
            : _dragSeed != null ? [_dragSeed] : ResultsList.SelectedItems.OfType<FileEntry>().ToList();
        string[] paths = rows.Select(entry => entry.FilePath).Where(path => System.IO.File.Exists(path)
            || System.IO.Directory.Exists(path)).ToArray();
        if (paths.Length == 0) return null;
        var data = Services.ShellDataObject.Create(paths);
        var effects = rows.All(MutableEntry) ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy;
        if (data != null) return new DragTransfer(data, () => System.Runtime.InteropServices.Marshal.FinalReleaseComObject(data), paths[0], effects);
        var native = new NativeDataObject();
        try
        {
            native.SetHGlobal(NativeDataObject.CF_HDROP, NativeDataObject.BuildHDrop(paths));
            native.SetHGlobal(NativeDataObject.CF_UNICODETEXT, NativeDataObject.BuildUnicodeText(string.Join(Environment.NewLine, paths)));
            return new DragTransfer(native, native.ReleaseAll, paths[0], effects);
        }
        catch { native.ReleaseAll(); throw; }
    }

    public sealed class DragTransfer(System.Runtime.InteropServices.ComTypes.IDataObject data, Action release,
        string firstPath, DragDropEffects effects) : IDisposable
    {
        public System.Runtime.InteropServices.ComTypes.IDataObject Data { get; } = data;
        public string FirstPath { get; } = firstPath;
        public DragDropEffects AllowedEffects { get; } = effects;
        private Action? _release = release;
        private IDisposable? _image;
        internal void AttachImage(System.Windows.Media.ImageSource? image) => _image = Services.DragImage.Attach(Data, image);
        public void Dispose()
        {
            Interlocked.Exchange(ref _image, null)?.Dispose();
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
    private Point _pressAt;
    private bool _marqueeOn;
    private bool _marqueeAdditive;
    private FileEntry? _dragSeed;
    private bool _dragArmed;
    private ListBoxItem? _dragSeedItem;
    private List<FileEntry> _dragSelection = [];

    /// <summary>Empty space starts a marquee. Rows and scrollbars keep native control behavior.</summary>
    public bool BeginPointer(Point position, DependencyObject source, ModifierKeys modifiers)
    {
        EndPointer();
        _pressAt = position;
        _dragSeedItem = null;
        _dragSeed = null;
        _dragSelection.Clear();
        for (var node = source; node != null; node = InteractionParent(node))
            if (node is System.Windows.Controls.Primitives.ScrollBar || InsideTextField(node)) return false;
        ResultsList.Focus();
        if (ItemUnder(source) is { } item)
        {
            // Snapshot before ListBox mouse-down collapses a multi-selection.
            _dragSeedItem = item;
            _dragSeed = DataFor(item);
            _dragSelection = ResultsList.SelectedItems.OfType<FileEntry>().ToList();
            _dragArmed = true;
            return false;
        }
        _marqueeAdditive = (modifiers & ModifierKeys.Control) != 0;
        if (!_marqueeAdditive) ResultsList.SelectedItems.Clear();
        _marqueeOn = true;
        MarqueeRect.Visibility = Visibility.Visible;
        PlaceMarquee(new Rect(position, position));
        ResultsList.CaptureMouse();
        return true;
    }

    public void MovePointer(Point position)
    {
        if (!_marqueeOn)
        {
            if (_dragArmed && (Math.Abs(position.X - _pressAt.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(position.Y - _pressAt.Y) >= SystemParameters.MinimumVerticalDragDistance))
            {
                _dragArmed = false;
                StartCommand(StartFileDrag());
            }
            return;
        }
        var band = new Rect(_pressAt, position);
        PlaceMarquee(band);
        // Only realized containers can intersect a band inside the viewport.
        foreach (var row in Descendants<ListBoxItem>(ResultsList))
        {
            Rect bounds;
            try { bounds = row.TransformToAncestor(ResultsList).TransformBounds(new Rect(row.RenderSize)); }
            catch (InvalidOperationException) { continue; }
            if (band.IntersectsWith(bounds)) row.IsSelected = true;
            else if (!_marqueeAdditive) row.IsSelected = false;
        }
    }

    private void PlaceMarquee(Rect band)
    {
        Point at = ResultsList.TranslatePoint(band.TopLeft, InteractionLayer);
        Canvas.SetLeft(MarqueeRect, at.X);
        Canvas.SetTop(MarqueeRect, at.Y);
        MarqueeRect.Width = band.Width;
        MarqueeRect.Height = band.Height;
    }

    public void EndPointer()
    {
        _marqueeOn = false;
        _dragArmed = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (ResultsList.IsMouseCaptured) ResultsList.ReleaseMouseCapture();
    }

    private static ListBoxItem? ItemUnder(DependencyObject? source)
    {
        for (var node = source; node != null; node = InteractionParent(node))
            if (node is ListBoxItem row) return row;
        return null;
    }

    private FileEntry? DataFor(ListBoxItem row)
        => ResultsList.ItemContainerGenerator.ItemFromContainer(row) as FileEntry ?? row.DataContext as FileEntry;

    private async Task StartFileDrag()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        using var transfer = CreateDragData();
        if (transfer == null) return;
        transfer.AttachImage(await IconCache.ForAsync(transfer.FirstPath, 48));
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        NativeDragDrop.DoDragDrop(transfer.Data, new SimpleDropSource(), (int)transfer.AllowedEffects, out _);
    }

    private void InitInteraction()
    {
        ResultsList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source)
                e.Handled = BeginPointer(e.GetPosition(ResultsList), source, Keyboard.Modifiers);
        };
        ResultsList.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) MovePointer(e.GetPosition(ResultsList));
        };
        ResultsList.PreviewMouseLeftButtonUp += (_, _) => EndPointer();
        ResultsList.AllowDrop = true;
        ResultsList.DragOver += Files_DragOver;
        ResultsList.Drop += Files_Drop;
        ResultsList.DragEnter += Files_DragEnter;
        ResultsList.DragLeave += Files_DragLeave;
        Window? host = null;
        EventHandler deactivated = (_, _) => CommitRenameOnLeave();
        Loaded += (_, _) =>
        {
            if (host != null) host.Deactivated -= deactivated;
            host = Window.GetWindow(this);
            if (host != null) host.Deactivated += deactivated;
        };
        Unloaded += (_, _) =>
        {
            if (host != null) host.Deactivated -= deactivated;
            host = null;
            // A tab switch or a closing window commits the typed name, the same as clicking away,
            // and closes the box when the name cannot be committed.
            EndPointer(); ReleaseDropImage(); LeaveRename();
        };
    }

    private string DropTarget(DependencyObject? source)
        => ItemUnder(source) is { } item && DataFor(item) is { IsDirectory: true } folder
            ? folder.FilePath : Navigation.CurrentFolder;

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        _dragOverView = true;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (InsideTextField(e.OriginalSource as DependencyObject)) return;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                e.Effects = DropEffect(paths, DropTarget(e.OriginalSource as DependencyObject), e.AllowedEffects, DropModifiers(e.KeyStates));
        }
        catch (Exception error) { SetCommandError(error.Message); }
        _dropImage?.Over(PointToScreen(e.GetPosition(this)), (int)e.Effects);
    }

    private Services.DropTargetHelper? _dropImage;
    private bool _dragOverView;

    private void Files_DragEnter(object sender, DragEventArgs e)
    {
        _dragOverView = true;
        if (_dropImage != null || e.Data is not System.Runtime.InteropServices.ComTypes.IDataObject native) return;
        var owner = Window.GetWindow(this);
        if (owner == null) return;
        var helper = new Services.DropTargetHelper();
        if (helper.Enter(new System.Windows.Interop.WindowInteropHelper(owner).Handle, native,
            PointToScreen(e.GetPosition(this)), (int)e.AllowedEffects)) _dropImage = helper;
        else helper.Dispose();
    }

    private void Files_DragLeave(object sender, DragEventArgs e)
    {
        // WPF raises Leave/Enter synchronously for child crossings. Only a real exit tears down the helper.
        _dragOverView = false;
        Dispatcher.BeginInvoke(() => { if (!_dragOverView) ReleaseDropImage(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ReleaseDropImage()
    {
        _dropImage?.Leave();
        _dropImage?.Dispose();
        _dropImage = null;
    }

    private void Files_Drop(object sender, DragEventArgs e)
    {
        if (InsideTextField(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        string target = DropTarget(e.OriginalSource as DependencyObject);
        var modifiers = DropModifiers(e.KeyStates);
        // The answer goes back to the drag source before the operation runs. A copy is reported
        // as a copy. A move is one this view performs itself (the shell's optimized move), so it
        // answers None: a source told Move would delete the originals on its own, racing ours.
        var effect = DragDropEffects.None;
        try
        {
            if (!IsFileOperationBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                effect = DropEffect(paths, target, e.AllowedEffects, modifiers);
                // A drop into itself or a descendant is refused by DropFiles; answer None now.
                string destination = System.IO.Path.GetFullPath(target).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if (effect != DragDropEffects.None && paths.Any(path =>
                    {
                        string full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                        return destination.Equals(full, StringComparison.OrdinalIgnoreCase)
                            || destination.StartsWith(full + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    }))
                {
                    effect = DragDropEffects.None;
                    SetCommandError(Loc("Str_Status_DropIntoSelf"));
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.IOException
            or System.Runtime.InteropServices.COMException) { }
        e.Effects = effect == DragDropEffects.Copy ? DragDropEffects.Copy : DragDropEffects.None;
        // Optimized move per the shell's data transfer rules: Performed DropEffect = DROPEFFECT_NONE.
        if (effect == DragDropEffects.Move)
        {
            try { e.Data.SetData("Performed DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(0))); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
                or NotSupportedException or ArgumentException) { }
        }
        try
        {
            if (e.Data is System.Runtime.InteropServices.ComTypes.IDataObject native)
                _dropImage?.Drop(native, PointToScreen(e.GetPosition(this)), (int)e.Effects);
        }
        finally { _dropImage?.Dispose(); _dropImage = null; }
        if (effect != DragDropEffects.None)
            StartCommand(DropFiles(e.Data, target, e.AllowedEffects, modifiers));
    }

    private static ModifierKeys DropModifiers(DragDropKeyStates keys)
        => ((keys & DragDropKeyStates.ControlKey) != 0 ? ModifierKeys.Control : ModifierKeys.None)
            | ((keys & DragDropKeyStates.ShiftKey) != 0 ? ModifierKeys.Shift : ModifierKeys.None);

    /// <summary>Returns ownership before starting asynchronous work. Application keys pass through.</summary>
    public bool HandleFileKey(Key key, ModifierKeys modifiers, DependencyObject? source = null)
    {
        if (InsideTextField(source) || (source == null && !ResultsList.IsKeyboardFocusWithin)) return false;
        if (source != null)
        {
            var node = source;
            while (node != null && node != ResultsList) node = InteractionParent(node);
            if (node == null) return false;
        }
        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.A: ResultsList.SelectAll(); return true;
                case Key.C: CopySelection(); return true;
                case Key.X: CopySelection(true); return true;
                case Key.V: StartCommand(PasteFiles()); return true;
                case Key.D: FavoriteStar_Click(FavoriteStarBtn, new RoutedEventArgs()); return true;
                case Key.OemPlus or Key.Add: StepResultsSize(1); return true;
                case Key.OemMinus or Key.Subtract: StepResultsSize(-1); return true;
            }
        }
        if (modifiers == ModifierKeys.None && key == Key.F2) { BeginRename(); return true; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N)
        { StartCommand(NewFolder()); return true; }
        if (modifiers == ModifierKeys.None && key == Key.Back) { StartCommand(GoUp()); return true; }
        if (modifiers == ModifierKeys.None && key == Key.Enter)
        {
            if (ResultsList.SelectedItem is FileEntry entry) StartCommand(Browser.ActivateEntry(entry));
            return true;
        }
        if ((modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift) && key == Key.Delete)
        { StartCommand(RecycleSelection()); return true; }
        return false;
    }

    private void StartCommand(Task command)
    {
        // A rejected repeat must not hide the completion task for work already in flight.
        var observed = ObserveCommand(command);
        if (CurrentCommand.IsCompleted || !command.IsCompleted) CurrentCommand = observed;
    }
    private async Task ObserveCommand(Task command)
    {
        try { await command; }
        catch (Exception error) { SetCommandError(error.Message); }
    }

    private static bool InsideTextField(DependencyObject? source)
    {
        for (var node = source; node != null; node = InteractionParent(node))
            if (node is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return true;
        return false;
    }

    private static DependencyObject? InteractionParent(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
