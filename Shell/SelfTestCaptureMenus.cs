using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using KillerShot.Services;
using KillerShot.Native;
using KillerShot.Capture;
using CaptureMode = KillerShot.Capture.CaptureMode;

namespace KillerShot.Shell;

internal static class SelfTestCaptureMenus
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-capture-menus") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Contains("--show"));
        return true;
    }

    private static async Task RunAsync(string folder, bool show)
    {
        Window? fixture = null;
        RegionSelector? selector = null;
        int exit = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new capture-menu test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            CaptureLibrary.OpenAuto();
            CaptureSettings.CopyToClipboard = CaptureSettings.SaveToFolder = CaptureSettings.Notify = CaptureSettings.AskToSave = false;
            App.InitializeKit();
            ThemeManager.Apply(Theme.Dark);
            ThemeManager.ApplyAccent(Theme.Dark, Accent.Red);
            var target = new Border { Background = Brushes.DarkBlue };
            fixture = new Window { Title = "KillerShot capture check", Width = 500, Height = 300, Left = 150, Top = 100,
                WindowStartupLocation = WindowStartupLocation.Manual, Content = target, Topmost = true };
            fixture.Show();
            fixture.Activate();
            var marker = new Border { Width = 160, Height = 48, Background = Brushes.Lime };
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Center };
            menu.Items.Add(new MenuItem { Header = marker });
            menu.IsOpen = true;
            await Task.Delay(350);
            Point sample = marker.PointToScreen(new Point(80, 24));
            var baseline = FrozenDesktop.Capture().CopyPixels(new Win32.RECT((int)sample.X, (int)sample.Y, (int)sample.X + 1, (int)sample.Y + 1)).Pixels;
            if (baseline[1] < 180 || baseline[0] > 60 || baseline[2] > 60)
                throw new InvalidOperationException("The test menu must be visible before capture starts.");
            using var tray = new TrayHost();
            bool hidden = false;
            var controller = new CaptureController(tray) { HideEditor = () => { hidden = true; fixture.Hide(); return Task.FromResult<Action?>(null); } };
            await controller.StartFromShortcut(ShortcutAction.AllMonitors, IntPtr.Zero);
            var captured = controller.Last ?? throw new InvalidOperationException("The shortcut produced no image.");
            int x = (int)sample.X - Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
            int y = (int)sample.Y - Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
            var pixel = new byte[4];
            captured.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (Enumerable.Range(0, 3).Any(i => Math.Abs(pixel[i] - baseline[i]) > 12))
                throw new InvalidOperationException("The open context menu disappeared before the shortcut copied the desktop.");
            if (hidden || !menu.IsOpen) throw new InvalidOperationException("An instant shortcut must leave the open context menu in place.");
            report.Add("Shortcut capture: open context menu remains visible in the pixels and on screen PASS");
            var desktop = FrozenDesktop.Capture();
            selector = new RegionSelector(desktop, showBar: true);
            var selection = selector.RunAsync();
            await Task.Delay(250);
            var bar = Application.Current.Windows.OfType<CaptureBar>().SingleOrDefault();
            if (bar == null || !bar.IsVisible) throw new InvalidOperationException("Region mode must immediately display the movable capture bar.");
            var frame = Application.Current.Windows.OfType<RegionOverlay>().SelectMany(Visuals).OfType<Border>()
                .First(b => b.BorderThickness == new Thickness(3));
            if (frame.BorderBrush is not SolidColorBrush accent || accent.Color != Color.FromRgb(0xEA, 0x52, 0x4C))
                throw new InvalidOperationException("The capture frame must use the deeper Dark Red accent.");
            var primary = desktop.Monitors.Single(m => m.Primary).Bounds;
            var barPosition = bar.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(bar);
            if (Math.Abs(barPosition.X + bar.ActualWidth * dpi.DpiScaleX / 2 - (primary.Left + primary.Width / 2.0)) > 3
                || Math.Abs(barPosition.Y - primary.Top - 12) > 3)
                throw new InvalidOperationException("The capture bar must default near the top center of the primary monitor.");
            var photo = Visuals(bar).OfType<ToggleButton>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Capture_Photo"));
            var video = Visuals(bar).OfType<ToggleButton>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Capture_Video"));
            var modes = Visuals(bar).OfType<ComboBox>().Single();
            if (!bar.IsActive || !photo.IsKeyboardFocused)
                throw new InvalidOperationException("The visible capture bar must begin with keyboard focus on Photo.");
            if (!photo.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) || !video.IsKeyboardFocused)
                throw new InvalidOperationException("Tab must move from Photo to Video.");
            if (!video.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) || !modes.IsKeyboardFocused)
                throw new InvalidOperationException("Tab must move from Video to capture mode.");
            if (!modes.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)) || !video.IsKeyboardFocused)
                throw new InvalidOperationException("Shift+Tab must return from capture mode to Video.");
            var folderButton = Visuals(bar).OfType<Button>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_CS_Folder"));
            var captureButton = Visuals(bar).OfType<Button>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Btn_Capture"));
            var cancelButton = Visuals(bar).OfType<Button>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Btn_Cancel"));
            var controls = new UIElement[] { video, modes, folderButton, captureButton, cancelButton };
            for (int i = 1; i < controls.Length; i++)
                if (!controls[i - 1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) || !controls[i].IsKeyboardFocused)
                    throw new InvalidOperationException("Tab must reach every capture bar control.");
            if (!cancelButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)) || !captureButton.IsKeyboardFocused)
                throw new InvalidOperationException("Shift+Tab must return from Cancel to Capture.");
            video.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!selector.IsVideo || video.IsChecked != true || photo.IsChecked != false)
                throw new InvalidOperationException("Video must select recording in the same capture picker.");
            if (modes.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, CaptureMode.Qr)).IsEnabled)
                throw new InvalidOperationException("QR reading is a photo mode.");
            photo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (selector.IsVideo || photo.IsChecked != true) throw new InvalidOperationException("Photo must switch back without a second selector.");
            var rendered = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rendered.Render((Visual)bar.Content);
            ImageOutput.SavePng(rendered, Path.Combine(folder, "capture-bar.png"));
            Visuals(bar).OfType<Button>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Btn_Cancel"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (await selection != null || bar.IsVisible) throw new InvalidOperationException("Cancel must close the bar and produce no capture.");
            report.Add("Region capture: capture bar is visible immediately and Cancel produces no image PASS");
            selector = new RegionSelector(desktop, showBar: true);
            selection = selector.RunAsync();
            await Task.Delay(200);
            bar = Application.Current.Windows.OfType<CaptureBar>().Single();
            modes = Visuals(bar).OfType<ComboBox>().Single();
            modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, CaptureMode.AllMonitors));
            Visuals(bar).OfType<Button>().Single(b => AutomationProperties.GetName(b) == LocaleManager.Loc("Str_Btn_Capture"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var rectangle = await selection;
            if (rectangle is not { } bounds || bounds.Width != desktop.Bounds.Width || bounds.Height != desktop.Bounds.Height)
                throw new InvalidOperationException("The capture bar must capture the chosen all-monitors area.");
            report.Add("Capture bar: primary-monitor position, Photo/Video, mode switching and Capture PASS");
            if (show)
            {
                selector = new RegionSelector(FrozenDesktop.Capture(), showBar: true);
                var displayedSelection = selector.RunAsync();
                var displayedBar = Application.Current.Windows.OfType<CaptureBar>().Single();
                displayedBar.Owner = null;
                displayedBar.ShowInTaskbar = true;
                var displayedArea = await displayedSelection;
                report.Add(displayedArea is { } area ? "Displayed region released: " + area.Width + " x " + area.Height : "Displayed capture canceled");
            }
            exit = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            selector?.Cancel();
            fixture?.Close();
            if (Directory.Exists(folder)) File.WriteAllLines(Path.Combine(folder, "capture-menu-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
