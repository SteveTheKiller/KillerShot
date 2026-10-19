using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Editor;
using KillerShot.Services;
using KillerFamily.Layout;
using KillerShot.Controls;

namespace KillerShot.Shell;

internal static class SelfTestImageMenus
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-image-menus") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Contains("--show"));
        return true;
    }

    private static async Task RunAsync(string folder, bool show)
    {
        int code = 1;
        MainWindow? window = null;
        string? reportPath = null;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new image-menu test folder.");
            Directory.CreateDirectory(folder);
            reportPath = Path.Combine(folder, "image-menu-report.txt");
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            var pixels = new byte[640 * 400 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 110; pixels[i + 1] = 65; pixels[i + 2] = 35; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(640, 400, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
            image.Freeze();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            for (int i = 0; i < 12; i++) CaptureLibrary.AddNow(image, DateTime.Now.AddMinutes(-i), "window", "Picture " + i, null);
            var doc = new EditorDocument(image);
            App.Instance.SetDocument(doc);
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(200);
            if (SystemParameters.HighContrast)
            {
                var frame = (FrameworkElement)window.FindName("FrameHost");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(frame);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, 0, 1, 1), pixel, 4, 0);
                var expected = SystemColors.WindowTextColor;
                if (pixel[0] != expected.B || pixel[1] != expected.G || pixel[2] != expected.R || pixel[3] != 255)
                    throw new InvalidOperationException("The contrast theme must draw the outer app border in Windows text color.");
                ImageOutput.SavePng(bitmap, Path.Combine(folder, "contrast-window.png"));
                report.Add("Windows contrast theme: visible outer app border PASS");
            }
            window.TestStripDock.DockTo(DockSide.Top);
            window.UpdateLayout();
            var contentPane = (FrameworkElement)window.FindName("ContentPane");
            double leftGutter = contentPane.TranslatePoint(new Point(), window.TestStripDock).X;
            if (Math.Abs(leftGutter - 8) > .01)
                throw new InvalidOperationException("Top-docked editor left gutter must be 8px; actual=" + leftGutter);
            report.Add("Top-docked editor: 8px outer left gutter PASS");
            foreach (var side in new[] { DockSide.Top, DockSide.Left, DockSide.Right, DockSide.Bottom })
            {
                window.TestStripDock.DockTo(side);
                window.UpdateLayout();
                var handle = Visuals(window.TestStripDock).OfType<GridSplitter>().Single();
                var center = handle.TranslatePoint(new Point(handle.ActualWidth / 2, handle.ActualHeight / 2), window);
                var hit = window.InputHitTest(center) as DependencyObject;
                while (hit != null && hit is not GridSplitter) hit = VisualTreeHelper.GetParent(hit);
                if (!ReferenceEquals(hit, handle)) throw new InvalidOperationException(side + " resize handle is covered by " + window.InputHitTest(center)?.GetType().Name
                    + "; hit=" + (window.InputHitTest(center) as FrameworkElement)?.Name + "; bounds=" + center + " " + handle.RenderSize + "; enabled=" + handle.IsEnabled + "; visible=" + handle.IsVisible + ".");
                double before = window.TestStripDock.IsHorizontal ? window.TestStrip!.ActualHeight : window.TestStrip!.ActualWidth;
                handle.Focus();
                handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(handle), 0,
                    window.TestStripDock.IsHorizontal ? Key.Down : Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
                window.UpdateLayout();
                double after = window.TestStripDock.IsHorizontal ? window.TestStrip!.ActualHeight : window.TestStrip!.ActualWidth;
                if (Math.Abs(after - before) < 1) throw new InvalidOperationException(side + " resize handle cannot resize its pane.");
            }
            window.TestStripDock.DockTo(DockSide.Top);
            report.Add("Resize handles: hit target and working pane resize on all four edges PASS");
            var rail = (StackPanel)window.FindName("RailStack");
            var dockButton = rail.Children.OfType<Button>().SingleOrDefault(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == LocaleManager.Loc("Str_Dock_Menu"));
            if (dockButton == null || !dockButton.IsVisible)
                throw new InvalidOperationException("Strip position must be available in the rail.");
            report.Add("Collection-strip position button: available in rail PASS");
            var stripModes = (StackPanel)window.FindName("FooterStripControls");
            if (!stripModes.IsVisible || stripModes.Children.OfType<RadioButton>().Count(b => b.IsChecked == true) != 1)
                throw new InvalidOperationException("The footer must mark the displayed collection-strip view.");
            report.Add("Footer: displayed collection-strip view is marked PASS");
            var leftBar = (Panel)window.FindName("LeftBar");
            var rightBar = (Panel)window.FindName("RightBar");
            if (leftBar.ActualHeight > 50 || rightBar.Children.Count == 0)
                throw new InvalidOperationException("The toolbar must have one row with annotations on the right.");
            report.Add("Toolbar: one row with file actions left and annotations right PASS");
            if (leftBar.Children.OfType<Button>().Any(b => b.IsVisible && b.Name is "RecentBtn" or "SaveAsBtn" or "FitBtn" or "ActualSizeBtn" or "CollectionPickerBtn"))
                throw new InvalidOperationException("The compact toolbar must group file actions and leave collection and zoom in the footer.");
            var collectionPicker = (Button)window.FindName("CollectionPickerBtn");
            if (!collectionPicker.IsVisible || collectionPicker.TranslatePoint(new Point(), window).Y < window.ActualHeight - 45)
                throw new InvalidOperationException("The collection picker must be in the footer.");
            if (window.TestStrip!.TestCarousel.TranslatePoint(new Point(), window.TestStrip).Y > 8)
                throw new InvalidOperationException("The carousel must start immediately below its top padding, without a caption row.");
            report.Add("Compact toolbar: grouped file actions, footer collection picker and no carousel header PASS");
            var librarySettings = window.FindName("LibrarySettingsButton") as Button;
            if (librarySettings == null || !librarySettings.IsVisible)
                throw new InvalidOperationException("Library settings must be available in the title bar.");
            var buttonPosition = librarySettings.TranslatePoint(new Point(), window);
            if (buttonPosition.Y >= 40 || buttonPosition.X <= window.ActualWidth / 2)
                throw new InvalidOperationException("Library settings must be on the right of the title bar.");
            string keepBefore = CaptureLibrary.Keep;
            librarySettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            var libraryPopup = (System.Windows.Controls.Primitives.Popup)window.FindName("LibrarySettingsPopup");
            if (!libraryPopup.IsOpen || !ReferenceEquals(libraryPopup.PlacementTarget, librarySettings)
                || !((FrameworkElement)window.FindName("LibraryKeepCombo")).IsVisible
                || ((FrameworkElement)window.FindName("CaptureSettingsOverlay")).IsVisible)
                throw new InvalidOperationException("The title-bar button must open its own library settings card.");
            libraryPopup.IsOpen = false;
            if (CaptureLibrary.Keep != keepBefore) throw new InvalidOperationException("Opening library settings changed retention.");
            report.Add("Library settings: right title-bar button, anchored card and unchanged retention PASS");
            var editor = (EditorCanvas)window.FindName("Editor");
            var stage = (FrameworkElement)window.FindName("EditorStage");
            var menu = stage.ContextMenu ?? throw new InvalidOperationException("Right-clicking the editor has no context menu.");
            void Open() => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            MenuItem Item(string id) => Descendants(menu).OfType<MenuItem>().Single(m => Equals(m.Tag, id));
            Open();
            menu.IsOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (Descendants(menu).OfType<MenuItem>().Any(i => i.Icon == null))
                throw new InvalidOperationException("Missing menu icons: " + string.Join(", ", Descendants(menu).OfType<MenuItem>().Where(i => i.Icon == null).Select(i => i.Header)));
            menu.IsOpen = false;
            if (Item("copy").InputGestureText != "Ctrl+C" || Item("saveas").InputGestureText != "Ctrl+Shift+S"
                || !Item("copy").IsEnabled || Item("undo").IsEnabled || Item("redo").IsEnabled)
                throw new InvalidOperationException("The menu's actions, shortcuts or initial edit states are wrong.");
            doc.Add(new ShapeAnnotation(ShapeKind.Rectangle, new Point(20, 20), new Point(120, 100), Colors.Red, 4));
            Open();
            Item("undo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (doc.State.Items.Count != 0 || !doc.CanRedo) throw new InvalidOperationException("Menu Undo did not undo the mark.");
            Open();
            Item("redo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (doc.State.Items.Count != 1) throw new InvalidOperationException("Menu Redo did not restore the mark.");
            Open();
            Item("pen").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Open();
            if (editor.Tool != ToolKind.Pen || !Item("pen").IsChecked || Item("pen").InputGestureText != "P")
                throw new InvalidOperationException("The Tools menu did not select and check the Pen tool.");
            double previousZoom = editor.Zoom;
            Item("zoomin").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (editor.Zoom <= previousZoom) throw new InvalidOperationException("Menu Zoom in did not zoom the image.");
            Open();
            Item("actualsize").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (Math.Abs(editor.Zoom - 1) > .001) throw new InvalidOperationException("Menu 100% did not restore actual size.");
            Open();
            menu.ApplyTemplate();
            var surface = (FrameworkElement)VisualTreeHelper.GetChild(menu, 0);
            surface.Opacity = 1; surface.Margin = new Thickness(0);
            surface.Measure(new Size(500, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
            var capture = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            capture.Render(surface);
            ImageOutput.SavePng(capture, Path.Combine(folder, "editor-menu.png"));
            report.Add("Editor menu: shortcuts, enabled states, Undo, Redo, tool selection and zoom PASS");
            foreach (var (side, view) in new[] { (DockSide.Top, "flow"), (DockSide.Left, "list"), (DockSide.Left, "rolodex") })
            {
                window.TestStripDock.DockTo(side);
                if (side == DockSide.Left) window.TestStrip!.SetSideView(view);
                await Task.Delay(150);
                FrameworkElement browser = Visuals(window.TestStrip!).OfType<FrameworkElement>().First(e => e.IsVisible &&
                    (view == "flow" ? e is CoverFlow : view == "list" ? e is ListBox : e is Rolodex));
                if (browser is ListBox list)
                {
                    list.SelectedIndex = 0;
                    window.UpdateLayout();
                    var thumb = Visuals(list).OfType<StripThumb>().First(t => t.IsVisible);
                    var splitter = Visuals(window.TestStripDock).OfType<GridSplitter>().Single();
                    double gap = splitter.TranslatePoint(new Point(), window.TestStripDock).X
                        - thumb.TranslatePoint(new Point(thumb.ActualWidth, 0), window.TestStripDock).X;
                    if (gap > 26) throw new InvalidOperationException("Sidebar card-to-splitter gap exceeds the padding and scrollbar: " + gap);
                    report.Add("Sidebar card-to-splitter gap: " + gap + "px PASS");
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    var rowBitmap = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth), (int)Math.Ceiling(row.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rowBitmap.Render(row);
                    var bottom = new byte[4];
                    rowBitmap.CopyPixels(new Int32Rect(rowBitmap.PixelWidth / 2, rowBitmap.PixelHeight - 1, 1, 1), bottom, 4, 0);
                    if (bottom[1] > bottom[2] + 20) throw new InvalidOperationException("A selected sidebar card must not paint an accent stripe underneath the photo.");
                    if (thumb.Effect is not System.Windows.Media.Effects.DropShadowEffect)
                        throw new InvalidOperationException("The active sidebar card must rise with a drop shadow.");
                    report.Add("Sidebar card: top color only, with an active drop shadow PASS");
                }
                browser.Focus();
                browser.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(browser), 0, Key.Apps) { RoutedEvent = Keyboard.KeyDownEvent });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var opened = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(s => s.RootVisual != null)
                    .SelectMany(s => Visuals(s.RootVisual)).OfType<ContextMenu>().SingleOrDefault(m => m.IsOpen);
                if (opened == null || !opened.Items.OfType<MenuItem>().Any(i => Equals(i.Header, LocaleManager.Loc("Str_Lib_Copy"))))
                    throw new InvalidOperationException(view + " has no image menu through the context-menu key.");
                opened.IsOpen = false;
                await Task.Delay(250);
                if (!ReferenceEquals(App.Instance.Document, doc)) throw new InvalidOperationException("Opening a strip menu replaced the editor document.");
                report.Add("Strip " + view + ": context menu and unchanged editor PASS");
            }
            window.TestStripDock.DockTo(DockSide.Top);
            App.Instance.SetDocument(null);
            window.UpdateLayout();
            if (rail.Orientation != Orientation.Vertical || rail.TranslatePoint(new Point(), window).X > 30)
                throw new InvalidOperationException("The album must put its rail on the left instead of inheriting the editor's top strip.");
            Open();
            if (Item("copy").IsEnabled || Item("save").IsEnabled || Item("undo").IsEnabled)
                throw new InvalidOperationException("Editor menu actions must be disabled without an image.");
            long collection = CaptureLibrary.CreateCollection("Resize checks");
            CaptureLibrary.Move(CaptureLibrary.Items().Take(3).Select(i => i.Id), collection);
            CardColors.SetCollection(collection, Colors.LimeGreen);
            await Task.Delay(100);
            collectionPicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            ContextMenu OpenMenu() => PresentationSource.CurrentSources.Cast<PresentationSource>().Where(s => s.RootVisual != null)
                .SelectMany(s => Visuals(s.RootVisual)).OfType<ContextMenu>().Single(m => m.IsOpen);
            var sourceMenu = OpenMenu();
            sourceMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Resize checks")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            sourceMenu.IsOpen = false;
            window.TestLibrary.TestSet("grid", "collection", "");
            await Task.Delay(150);
            window.UpdateLayout();
            var tileButtons = Visuals(window.TestLibrary).OfType<Button>().Where(b => b.IsVisible && b.Style == window.TestLibrary.FindResource("LibTile")).ToList();
            if (tileButtons.Count != 3 || ((TextBlock)window.FindName("CollectionCountText")).Text != "3 captures")
                throw new InvalidOperationException("The footer collection choice must filter the album and update its count.");
            if (tileButtons.Any(b => b.BorderThickness.Left != 0 || b.BorderThickness.Right != 0 || b.BorderThickness.Bottom != 0 || b.BorderThickness.Top != 3))
                throw new InvalidOperationException("Album cards must have a 3px top stripe without colored side or bottom borders.");
            report.Add("Album: footer collection filter, matching count and top-only card color PASS");
            collectionPicker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            sourceMenu = OpenMenu();
            sourceMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, LocaleManager.Loc("Str_Strip_All"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            sourceMenu.IsOpen = false;
            App.Instance.SetDocument(doc);
            await Task.Delay(100);
            if (window.TestStripDock.Side != DockSide.Top) throw new InvalidOperationException("Returning to the editor must preserve its saved top strip.");
            if (rail.TranslatePoint(new Point(), window).X > window.ActualWidth / 2)
                throw new InvalidOperationException("The horizontal rail must default to the left after the editor reopens.");
            var toolbarMenu = (ContextMenu)window.FindName("ToolbarMenu");
            var hideToolbar = toolbarMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "hide"));
            hideToolbar.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var overflow = (Button)window.FindName("ToolbarOverflowButton");
            window.UpdateLayout();
            if (!overflow.IsVisible || ((FrameworkElement)window.FindName("ToolbarGrid")).IsVisible)
                throw new InvalidOperationException("A hidden toolbar must have a visible title-bar overflow.");
            overflow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var more = (ContextMenu)window.FindName("MoreMenu");
            foreach (string key in new[] { "Str_Btn_Capture", "Str_Btn_Print", "Str_Tool_Pen", "Str_Btn_Color", "Str_Btn_Width" })
                if (!more.Items.OfType<MenuItem>().Any(m => Equals(m.Header, LocaleManager.Loc(key))))
                    throw new InvalidOperationException("The hidden toolbar lost " + key);
            more.IsOpen = false;
            hideToolbar.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            report.Add("Rail and toolbar: left default survives layout; hidden actions remain in title-bar overflow PASS");
            var railMenu = rail.ContextMenu!;
            void OpenRail() => railMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            OpenRail();
            var flipRail = railMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocaleManager.Loc("Str_Rail_Flip")));
            flipRail.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.UpdateLayout();
            if (rail.TranslatePoint(new Point(), window).X < window.ActualWidth / 2)
                throw new InvalidOperationException("The rail menu did not move the icons to the right.");
            var themeButton = (Button)window.FindName("ThemeButton");
            themeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var themeMenu = (ContextMenu)window.FindName("ThemeMenu");
            if (!themeMenu.IsOpen || !ReferenceEquals(themeMenu.PlacementTarget, themeButton)
                || themeMenu.Placement == System.Windows.Controls.Primitives.PlacementMode.Custom)
                throw new InvalidOperationException("The theme flyout is not anchored to its own right-side button.");
            themeMenu.IsOpen = false;
            FrameworkElement[] leadingRail = [themeButton, (Button)window.FindName("LangButton"), (Button)window.FindName("ShortcutsButton"), (Button)window.FindName("CaptureSettingsButton")];
            if (!rail.Children.Cast<UIElement>().Take(4).SequenceEqual(leadingRail)) throw new InvalidOperationException("Rail order must start Theme, Locale, Help, Settings.");
            var firstExtra = rail.Children[4];
            OpenRail();
            railMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocaleManager.Loc("Str_Rail_Reverse")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (!ReferenceEquals(firstExtra, rail.Children[rail.Children.Count - 1]) || !rail.Children.Cast<UIElement>().Take(4).SequenceEqual(leadingRail))
                throw new InvalidOperationException("Reversing app controls must preserve Theme, Locale, Help, Settings in order.");
            railMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocaleManager.Loc("Str_Rail_Reverse")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            flipRail.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            report.Add("Rail menu: flips sides, reverses order and anchors the theme flyout to its button PASS");
            var printPreview = new PrintPreviewWindow(window, doc.Render(), "Edited screenshot");
            printPreview.Show();
            await Task.Delay(100);
            var previewImage = Visuals(printPreview).OfType<Image>().Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "Edited screenshot");
            if (previewImage.Source is not BitmapSource previewBitmap || previewBitmap.PixelWidth != 640)
                throw new InvalidOperationException("The print preview did not show the edited screenshot.");
            var markPixel = new byte[4];
            previewBitmap.CopyPixels(new Int32Rect(20, 20, 1, 1), markPixel, 4, 0);
            if (markPixel[2] < 200 || markPixel[1] > 80) throw new InvalidOperationException("The print preview lost the red annotation.");
            var printShot = new RenderTargetBitmap((int)printPreview.ActualWidth, (int)printPreview.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            printShot.Render((Visual)printPreview.Content);
            ImageOutput.SavePng(printShot, Path.Combine(folder, "print-preview.png"));
            printPreview.Close();
            if (printPreview.Printed) throw new InvalidOperationException("Closing print preview sent a job.");
            report.Add("Print preview: edited image and red annotation visible; closing does not print PASS");
            ColorPickerDialog? colorPicker = null;
            var originalColor = editor.Color;
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                await Task.Delay(200);
                colorPicker = Application.Current.Windows.OfType<ColorPickerDialog>().SingleOrDefault();
                colorPicker?.Close();
            }));
            ((Button)window.FindName("ColorBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(350);
            if (colorPicker == null || editor.Color != originalColor)
                throw new InvalidOperationException("Annotation color must open the full family picker and preserve the color when canceled.");
            if (leftBar.Children.OfType<Button>().Concat(rightBar.Children.OfType<Button>()).Any(b => b.Name == "LibraryBtn" && b.IsVisible))
                throw new InvalidOperationException("Library views belong in the footer, without a duplicate toolbar button.");
            report.Add("Annotation color: full family picker and unchanged color after Cancel PASS");
            ThemeManager.Apply(Theme.Dark);
            ThemeManager.ApplyAccent(Theme.Dark, Accent.Red);
            window.UpdateLayout();
            if (((Button)window.FindName("ToolPenBtn")).Foreground is not SolidColorBrush activeInk || activeInk.Color != Color.FromRgb(0xEA, 0x52, 0x4C))
                throw new InvalidOperationException("Dark Red active tool icons must use the deeper red accent.");
            var sliderPill = Visuals(window.TestStrip!).OfType<Border>().Single(b => b.Name == "pill");
            if (sliderPill.Background is not SolidColorBrush sliderInk || sliderInk.Color != Color.FromRgb(0xEA, 0x52, 0x4C))
                throw new InvalidOperationException("The Dark Red carousel slider must use the deeper red accent.");
            var finalShot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            finalShot.Render((Visual)window.Content);
            ImageOutput.SavePng(finalShot, Path.Combine(folder, "compact-editor-red.png"));
            report.Add("Dark Red active tool: deeper red accent PASS");
            File.WriteAllLines(reportPath, report);
            if (show)
            {
                var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.SetResult();
                window.Left = 120; window.Top = 80;
                await closed.Task;
                window = null;
            }
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            window?.Close();
            if (reportPath != null) File.WriteAllLines(reportPath, report);
            App.Instance.Shutdown(code);
        }
    }

    private static IEnumerable<object> Descendants(ItemsControl parent)
    {
        foreach (object item in parent.Items)
        {
            yield return item;
            if (item is ItemsControl child) foreach (object nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
