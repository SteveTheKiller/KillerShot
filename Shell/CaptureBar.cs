using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerFamily.Accessibility;
using KillerShot.Capture;
using KillerShot.Controls;
using KillerShot.Native;
using KillerShot.Services;
using CaptureMode = KillerShot.Capture.CaptureMode;

namespace KillerShot.Shell;

/// <summary>The movable capture choices, shown only after the desktop has been frozen.</summary>
internal sealed class CaptureBar : Window
{
    private readonly RegionSelector _selector;
    private readonly ComboBox _modes = new() { MinWidth = 130, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _folder;
    private readonly ToggleButton _photo, _video;
    private bool _syncing;

    public CaptureBar(RegionSelector selector, MonitorBounds primary)
    {
        _selector = selector;
        Title = "KillerShot: " + LocaleManager.Loc("Str_Btn_Capture");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000; Top = -20000;
        SetResourceReference(ForegroundProperty, "TextBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        var dots = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(0, 2, 0, 2) };
            dot.SetResourceReference(Shape.FillProperty, "MutedTextBrush");
            dots.Children.Add(dot);
        }
        var grip = new Border { Background = Brushes.Transparent, Padding = new Thickness(2, 0, 10, 0), Child = dots, Cursor = Cursors.Hand };
        AutomationProperties.SetName(grip, LocaleManager.Loc("Str_Dock_Resize"));
        grip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            DragMove();
            RememberPosition();
        };
        row.Children.Add(grip);
        _photo = Choice("Str_Capture_Photo", 0xE722, false);
        _video = Choice("Str_Capture_Video", 0xE714, true);
        row.Children.Add(_photo); row.Children.Add(_video);
        foreach (var mode in Enum.GetValues<CaptureMode>())
            _modes.Items.Add(new ComboBoxItem { Tag = mode, Content = LocaleManager.Loc(mode switch
            {
                CaptureMode.Region => "Str_Mode_Region", CaptureMode.Window => "Str_Mode_Window",
                CaptureMode.Monitor => "Str_Mode_Monitor", CaptureMode.Qr => "Str_Mode_Qr", _ => "Str_Mode_AllMonitors"
            }) });
        AutomationProperties.SetName(_modes, LocaleManager.Loc("Str_CS_Title"));
        _modes.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _modes.SelectedItem is ComboBoxItem { Tag: CaptureMode mode }) _selector.SetMode(mode);
        };
        row.Children.Add(_modes);
        _folder = Action("Str_CS_Folder", 0xE8B7, ChooseFolder);
        row.Children.Add(_folder);
        row.Children.Add(Action("Str_Btn_Capture", 0xE73E, _selector.CaptureSelection, primary: true));
        row.Children.Add(Action("Str_Btn_Cancel", 0xE711, _selector.Cancel));
        var layers = new Grid();
        layers.Children.Add(row);
        layers.Children.Add(new Border { Style = (Style)FindResource("FlyoutGrain") });
        Content = new PickerBorder { Style = (Style)FindResource("FlyoutCard"), Child = layers };
        void Place()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            int x = primary.Bounds.Left + (primary.Bounds.Width - width) / 2, y = primary.Bounds.Top + 12;
            if (int.TryParse(Settings.Get("CaptureBar.X"), out int storedX) && int.TryParse(Settings.Get("CaptureBar.Y"), out int storedY))
            {
                var monitor = FrozenDesktop.EnumerateMonitors().FirstOrDefault(m => m.Bounds.Contains(new Win32.POINT { X = storedX + width / 2, Y = storedY + height / 2 }));
                if (monitor != null)
                {
                    x = Math.Clamp(storedX, monitor.Bounds.Left, Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - width));
                    y = Math.Clamp(storedY, monitor.Bounds.Top, Math.Max(monitor.Bounds.Top, monitor.Bounds.Bottom - height));
                }
            }
            Win32.SetWindowPos(new WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST, x, y, width, height, Win32.SWP_NOACTIVATE);
        }
        Loaded += (_, _) =>
        {
            Place();
            // Moving from the owner's monitor can change DPI and the measured window size.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (IsVisible) Place();
            }));
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _selector.Cancel(); } };
        Closed += (_, _) => _selector.Cancel();
        Sync();
    }

    internal void FocusFirstControl()
    {
        Activate();
        Keyboard.Focus(_photo);
    }

    private static StackPanel Label(string key, int glyph)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new Glyph { Text = char.ConvertFromUtf32(glyph), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = LocaleManager.Loc(key), VerticalAlignment = VerticalAlignment.Center });
        return label;
    }

    private ToggleButton Choice(string key, int glyph, bool video)
    {
        var button = new ToggleButton { Content = Label(key, glyph), Style = (Style)FindResource("ChipToggle"), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(2, 0, 2, 0) };
        AutomationProperties.SetName(button, LocaleManager.Loc(key));
        button.Click += (_, _) => { _selector.SetVideo(video); Sync(); };
        return button;
    }

    private Button Action(string key, int glyph, Action run, bool primary = false)
    {
        var button = new Button { Content = Label(key, glyph), Style = (Style)FindResource(primary ? "PrimaryButton" : "SurfaceButton"), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(2, 0, 2, 0) };
        AutomationProperties.SetName(button, LocaleManager.Loc(key));
        button.Click += (_, _) => run();
        return button;
    }

    internal void Sync()
    {
        _syncing = true;
        _photo.IsChecked = !_selector.IsVideo;
        _video.IsChecked = _selector.IsVideo;
        foreach (var item in _modes.Items.OfType<ComboBoxItem>())
        {
            item.IsEnabled = !_selector.IsVideo || (CaptureMode)item.Tag != CaptureMode.Qr;
            if ((CaptureMode)item.Tag == _selector.Mode) _modes.SelectedItem = item;
        }
        _folder.ToolTip = _selector.IsVideo ? VideoSettings.OutputFolder : CaptureSettings.OutputFolder;
        _syncing = false;
    }

    private void ChooseFolder()
    {
        var dialog = new Controls.FileDialog(FileDialogMode.Folder)
        {
            Title = LocaleManager.Loc(_selector.IsVideo ? "Str_Dlg_VideoFolderTitle" : "Str_Dlg_FolderTitle"),
            InitialDirectory = _selector.IsVideo ? VideoSettings.OutputFolder : CaptureSettings.OutputFolder,
            Topmost = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_selector.IsVideo) VideoSettings.OutputFolder = dialog.FileName;
        else CaptureSettings.OutputFolder = dialog.FileName;
        Sync();
    }

    private void RememberPosition()
    {
        var position = PointToScreen(new Point());
        Settings.Set("CaptureBar.X", ((int)position.X).ToString(CultureInfo.InvariantCulture));
        Settings.Set("CaptureBar.Y", ((int)position.Y).ToString(CultureInfo.InvariantCulture));
    }
}
