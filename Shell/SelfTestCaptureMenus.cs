using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    internal static bool TryRun(string[] args)
    {
        if (args.Length == 2 && args[0] == "--selftest-selector-close")
        {
            _ = RunSelectorClose(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length >= 2 && args[0] == "--selftest-region-gesture")
        {
            _ = RunRegionGesture(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length < 2 || args[0] != "--selftest-capture-menus") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Contains("--show"));
        return true;
    }

    private static async Task RunSelectorClose(string folder)
    {
        Directory.CreateDirectory(folder);
        int exit = 1;
        var report = new List<string>();
        RegionSelector? selector = null;
        try
        {
            var bounds = new Win32.RECT(0, 0, 200, 100);
            var desktop = new FrozenDesktop(bounds, new byte[200 * 100 * 4],
                [new MonitorBounds(IntPtr.Zero, new Win32.RECT(0, 0, 100, 100), true),
                 new MonitorBounds(IntPtr.Zero, new Win32.RECT(100, 0, 200, 100), false)], []);
            foreach (var mode in new[] { CaptureMode.Region, CaptureMode.Qr })
            {
                selector = new RegionSelector(desktop, mode: mode);
                Task<Win32.RECT?> result = selector.RunAsync();
                var overlays = Application.Current.Windows.OfType<RegionOverlay>().ToArray();
                if (overlays.Length != 2) throw new InvalidOperationException("Expected two synthetic monitor overlays.");
                overlays[0].Close();
                if (await result.WaitAsync(TimeSpan.FromSeconds(1)) != null)
                    throw new InvalidOperationException("Closing an overlay returned a capture instead of cancellation.");
                if (Application.Current.Windows.OfType<RegionOverlay>().Any())
                    throw new InvalidOperationException("A sibling overlay survived cancellation.");
                selector.Cancel();
                report.Add(mode + " external close cancels once and closes both overlays: PASS");
            }
            exit = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            selector?.Cancel();
            File.WriteAllLines(Path.Combine(folder, "selector-close-report.txt"), report);
            App.Current.Shutdown(exit);
        }
    }

    private static async Task RunRegionGesture(string folder)
    {
        int exit = 1;
        string report;
        RegionSelector? modeSelector = null;
        try
        {
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureSettings.LastMode = CaptureMode.Window;
            if (CaptureController.Resolve(ShortcutAction.LastMode, null) != CaptureMode.Region)
                throw new InvalidOperationException("Print Screen must start in Region even after a Window capture.");
            var desktopBounds = new Win32.RECT(0, 0, 200, 200);
            var windowBounds = new Win32.RECT(10, 10, 150, 150);
            var desktop = new FrozenDesktop(desktopBounds, new byte[200 * 200 * 4],
                [new MonitorBounds(IntPtr.Zero, desktopBounds, true)],
                [new WindowBounds((IntPtr)123, windowBounds)]);
            var start = new Win32.POINT { X = 30, Y = 40 };
            var end = new Win32.POINT { X = 65, Y = 70 };
            var region = new RegionSelector(desktop);
            if (region.Mode != CaptureMode.Region)
                throw new InvalidOperationException("Rectangle region must be the default capture mode.");
            region.MoveAt(start);
            if (region.CurrentSelection != null)
                throw new InvalidOperationException("Region hover highlighted a window before a rectangle drag.");
            region.BeginAt(start);
            region.End();
            if (region.Result.IsCompleted)
                throw new InvalidOperationException("A click without a drag captured a window in Region mode.");
            region.BeginAt(start);
            region.MoveAt(end);
            region.End();
            var picked = region.Result.GetAwaiter().GetResult();
            if (picked is not { Left: 30, Top: 40, Right: 66, Bottom: 71 } || region.PickedWindow != IntPtr.Zero)
                throw new InvalidOperationException("Region drag did not return the selected rectangle.");
            var window = new RegionSelector(desktop, mode: CaptureMode.Window);
            window.MoveAt(start);
            window.BeginAt(start);
            window.End();
            if (window.Result.GetAwaiter().GetResult() is not { Left: 10, Top: 10, Right: 150, Bottom: 150 }
                || window.PickedWindow != (IntPtr)123)
                throw new InvalidOperationException("Window mode must keep click-to-pick.");
            modeSelector = new RegionSelector(desktop, mode: CaptureMode.Window);
            _ = modeSelector.RunAsync();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            modeSelector.MoveAt(start);
            var overlay = Application.Current.Windows.OfType<RegionOverlay>().Single();
            var outline = Visuals(overlay).OfType<System.Windows.Shapes.Rectangle>()
                .Single(r => r.StrokeThickness == 1);
            if (outline.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Window mode did not draw a selection outline.");
            modeSelector.SetMode(CaptureMode.Region);
            if (outline.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Switching to Region left the Window selection outline visible.");
            report = "Print Screen default, Region hover, click, drag, Window click, and mode switch: PASS";
            exit = 0;
        }
        catch (Exception ex) { report = ex.ToString(); }
        finally { modeSelector?.Cancel(); }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "region-gesture-report.txt"), report);
        App.Instance.Shutdown(exit);
    }

    private static async Task RunAsync(string folder, bool show)
    {
        Window? fixture = null;
        Process? windowFixture = null;
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
            var primary = desktop.Monitors.Single(m => m.Primary).Bounds;
            var initialMonitor = desktop.Monitors.FirstOrDefault(m => !m.Primary) ?? desktop.Monitors[0];
            SetCursorPos(initialMonitor.Bounds.Left + initialMonitor.Bounds.Width / 2,
                initialMonitor.Bounds.Top + initialMonitor.Bounds.Height / 2);
            selector = new RegionSelector(desktop, showBar: true);
            var selection = selector.RunAsync();
            await Task.Delay(250);
            var bar = Application.Current.Windows.OfType<CaptureBar>().SingleOrDefault();
            if (bar == null || !bar.IsVisible) throw new InvalidOperationException("Region mode must immediately display the movable capture bar.");
            var frame = Application.Current.Windows.OfType<RegionOverlay>().SelectMany(Visuals).OfType<Border>()
                .First(b => b.BorderThickness == new Thickness(3));
            if (frame.BorderBrush is not SolidColorBrush accent || accent.Color != Color.FromRgb(0xEA, 0x52, 0x4C))
                throw new InvalidOperationException("The capture frame must use the deeper Dark Red accent.");
            var barPosition = bar.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(bar);
            report.Add($"Capture bar physical position={barPosition.X},{barPosition.Y}; logical size={bar.ActualWidth}x{bar.ActualHeight}; DPI={dpi.DpiScaleX},{dpi.DpiScaleY}; primary={primary.Left},{primary.Top},{primary.Right},{primary.Bottom}.");
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

            // Pick an exposed part of a window while another window covers its center.
            menu.IsOpen = false;
            string fixtureScript = Path.Combine(folder, "window-fixture.ps1");
            File.WriteAllText(fixtureScript, """
                Add-Type -AssemblyName System.Windows.Forms
                $form = New-Object System.Windows.Forms.Form
                $form.Text = 'KillerShot window fixture'
                $form.BackColor = [System.Drawing.Color]::Lime
                $form.StartPosition = 'Manual'
                $form.Location = New-Object System.Drawing.Point(100, 100)
                $form.Size = New-Object System.Drawing.Size(600, 400)
                [System.Windows.Forms.Application]::Run($form)
                """);
            var fixtureStart = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            fixtureStart.ArgumentList.Add("-NoProfile");
            fixtureStart.ArgumentList.Add("-STA");
            fixtureStart.ArgumentList.Add("-File");
            fixtureStart.ArgumentList.Add(fixtureScript);
            windowFixture = Process.Start(fixtureStart)
                ?? throw new InvalidOperationException("The external window fixture did not start.");
            for (int i = 0; i < 40 && windowFixture.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(100);
                windowFixture.Refresh();
            }
            IntPtr pickedHandle = windowFixture.MainWindowHandle;
            if (pickedHandle == IntPtr.Zero) throw new InvalidOperationException("The external fixture did not show a window for capture.");
            Win32.SetWindowPos(pickedHandle, Win32.HWND_TOPMOST, primary.Left + 150, primary.Top + 100, 600, 400, 0);
            Win32.SetWindowPos(new WindowInteropHelper(fixture).Handle, Win32.HWND_TOPMOST,
                primary.Left + 300, primary.Top + 220, 300, 180, 0);
            await Task.Delay(350);
            var pickedBounds = WindowCapture.FrameBounds(pickedHandle)
                ?? throw new InvalidOperationException("The external fixture has no capture bounds.");
            int pickX = pickedBounds.Left + 35, pickY = pickedBounds.Top + 18;
            if (!SetCursorPos(pickX, pickY)) throw new InvalidOperationException("Could not point to the exposed window.");
            var coveredDesktop = FrozenDesktop.Capture();
            if (coveredDesktop.WindowAt(new Win32.POINT { X = pickX, Y = pickY })?.Handle != pickedHandle)
                throw new InvalidOperationException("The external fixture must be the topmost capturable window at the pick point.");
            var (expectedWindow, usedGraphicsCapture) = await WindowCapture.CaptureAsync(pickedHandle, coveredDesktop);
            if (!usedGraphicsCapture || expectedWindow == null)
                throw new InvalidOperationException("Windows Graphics Capture is needed for the covered-window check.");
            int px = expectedWindow.PixelWidth / 2, py = expectedWindow.PixelHeight / 2;
            var visibleCrop = coveredDesktop.Crop(pickedBounds)
                ?? throw new InvalidOperationException("The covered window is outside the desktop.");
            byte[] expectedPixel = Pixel(expectedWindow, px, py);
            byte[] coveredPixel = Pixel(visibleCrop, Math.Min(px, visibleCrop.PixelWidth - 1), Math.Min(py, visibleCrop.PixelHeight - 1));
            report.Add($"Expected HWND={pickedHandle.ToInt64():X}; bounds={pickedBounds.Left},{pickedBounds.Top},{pickedBounds.Right},{pickedBounds.Bottom}; pick={pickX},{pickY}; WGC={usedGraphicsCapture}; image={expectedWindow.PixelWidth}x{expectedWindow.PixelHeight}; center={px},{py}; expected BGRA={string.Join(',', expectedPixel)}; covered BGRA={string.Join(',', coveredPixel)}.");
            if (Enumerable.Range(0, 3).All(i => Math.Abs(expectedPixel[i] - coveredPixel[i]) < 40))
                throw new InvalidOperationException("The fixture must visibly cover the selected window.");

            controller.HideEditor = null;
            var pickedCapture = controller.Start(CaptureMode.Window);
            await Task.Delay(200);
            var pickedOverlay = Application.Current.Windows.OfType<RegionOverlay>()
                .First(o => o.Monitor.Contains(new Win32.POINT { X = pickX, Y = pickY }));
            if (!SetCursorPos(pickX, pickY)) throw new InvalidOperationException("Could not point to the exposed window.");
            pickedOverlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
            pickedOverlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            pickedOverlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await pickedCapture.WaitAsync(TimeSpan.FromSeconds(5));
            var pickedImage = controller.Last ?? throw new InvalidOperationException("The window picker produced no image.");
            byte[] pickedPixel = Pixel(pickedImage, pickedImage.PixelWidth / 2, pickedImage.PixelHeight / 2);
            report.Add($"Picked image={pickedImage.PixelWidth}x{pickedImage.PixelHeight}; center={pickedImage.PixelWidth / 2},{pickedImage.PixelHeight / 2}; BGRA={string.Join(',', pickedPixel)}.");
            if (Enumerable.Range(0, 3).Any(i => Math.Abs(pickedPixel[i] - expectedPixel[i]) > 25))
                throw new InvalidOperationException("The picker captured covered desktop pixels instead of the selected window surface.");
            report.Add("Window picker: the covered part comes from the selected window PASS");
            var regionCapture = controller.Start(CaptureMode.Region);
            await Task.Delay(200);
            var regionOverlay = Application.Current.Windows.OfType<RegionOverlay>()
                .First(o => o.Monitor.Contains(new Win32.POINT { X = pickX, Y = pickY }));
            var regionOutline = Visuals(regionOverlay).OfType<System.Windows.Shapes.Rectangle>()
                .Single(r => r.StrokeThickness == 1);
            if (regionOutline.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Region mode must not highlight a window before a rectangle drag.");
            if (!SetCursorPos(pickedBounds.Left, pickedBounds.Top)) throw new InvalidOperationException("Could not point to the region start.");
            regionOverlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            if (!SetCursorPos(pickedBounds.Right - 1, pickedBounds.Bottom - 1)) throw new InvalidOperationException("Could not point to the region end.");
            regionOverlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
            regionOverlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await regionCapture.WaitAsync(TimeSpan.FromSeconds(5));
            var regionImage = controller.Last ?? throw new InvalidOperationException("The region picker produced no image.");
            byte[] regionPixel = Pixel(regionImage, regionImage.PixelWidth / 2, regionImage.PixelHeight / 2);
            if (Enumerable.Range(0, 3).Any(i => Math.Abs(regionPixel[i] - coveredPixel[i]) > 25))
                throw new InvalidOperationException("Region mode did not retain the visible desktop overlap.");
            report.Add("Region picker: the visible desktop overlap remains in the capture PASS");
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
            if (windowFixture is { HasExited: false }) windowFixture.Kill();
            windowFixture?.Dispose();
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

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }
}
