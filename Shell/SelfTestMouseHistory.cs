using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Editor;
using KillerShot.Services;
using Path = System.IO.Path;

namespace KillerShot.Shell;

// The mouse's back and forward buttons, pressed for real: WM_XBUTTONDOWN then WM_XBUTTONUP sent to
// the editor window's handle, so WPF's own mouse handling sees the press the way it does on a real
// mouse. XBUTTON1 is the thumb button nearer the user's palm (back), XBUTTON2 the far one (forward).
internal static class SelfTestMouseHistory
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-mouse-history") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    // Alt held, the way the real keyboard reports it to WPF's modifier check.
    private sealed class AltKeyboard() : System.Windows.Input.KeyboardDevice(System.Windows.Input.InputManager.Current)
    {
        protected override System.Windows.Input.KeyStates GetKeyStatesFromSystem(System.Windows.Input.Key key)
            => key == System.Windows.Input.Key.LeftAlt ? System.Windows.Input.KeyStates.Down : System.Windows.Input.KeyStates.None;
    }

    private static async Task Press(MainWindow window, int button, Point at)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var point = new IntPtr(((int)at.Y << 16) | ((int)at.X & 0xFFFF));
        int held = button == 1 ? 0x20 : 0x40;   // MK_XBUTTON1 / MK_XBUTTON2
        SendMessage(hwnd, WM_XBUTTONDOWN, new IntPtr((button << 16) | held), point);
        SendMessage(hwnd, WM_XBUTTONUP, new IntPtr(button << 16), point);
        await Settle();
    }

    // The toolbar as drawn, for a reviewer's eyes; no window reaches the user's screen.
    private static void SaveRender(FrameworkElement element, string path)
    {
        double dpi = VisualTreeHelper.GetDpi(element).PixelsPerInchX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi / 96), (int)Math.Ceiling(element.ActualHeight * dpi / 96),
            dpi, dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = Window.GetWindow(element)?.Background ?? Brushes.Transparent;
            context.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task Settle()
    {
        await Task.Delay(150);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        void Check(bool value, string message)
        {
            report.Add((value ? "PASS: " : "FAIL: ") + message);
            if (!value) throw new InvalidOperationException(message);
        }
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new mouse history test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            string outer = Directory.CreateDirectory(Path.Combine(folder, "outer")).FullName;
            string inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;
            CaptureSettings.OutputFolder = outer;
            App.InitializeKit();
            App.CloseToTray = true;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Synthetic album did not open.");
            var image = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)200, 64 * 64 * 4).ToArray(), 64 * 4);
            image.Freeze();
            var workArea = SystemParameters.WorkArea;
            window = new MainWindow { Width = 1000, Height = 700, ShowActivated = false, Left = workArea.Left + 20, Top = workArea.Top + 20 };
            window.Show();
            await Settle();
            var center = new Point(500, 400);
            bool EditorShown() => window.EditorScroll.Visibility == Visibility.Visible && !window.Library.IsVisible;
            bool LibraryShown() => window.Library.IsVisible && window.EditorScroll.Visibility != Visibility.Visible;

            // Album: an open image, back to the library and forward to the image again.
            window.Library.SetView("grid");
            await Settle();
            App.Instance.SetDocument(new EditorDocument(image));
            await Settle();
            Check(EditorShown(), "album: the opened image is on screen");
            await Press(window, 2, center);
            report.Add($"album: after forward on the image, editor={EditorShown()} library={LibraryShown()}");
            Check(EditorShown(), "album: forward on the open image stays on the image");
            await Press(window, 1, center);
            Check(LibraryShown(), "album: back on the image shows the library");
            await Press(window, 1, center);
            Check(LibraryShown(), "album: back again on the library stays on the library");
            await Press(window, 2, center);
            Check(EditorShown(), "album: forward from the library returns to the image");

            // The toolbar's Back: a real toolbar button, first on the bar, live only over a picture.
            var leftBar = (System.Windows.Controls.Panel)window.FindName("LeftBar");
            var back = window.BackBtn;
            Check(leftBar.Children.IndexOf(back) == 0 && back.IsVisible, "toolbar: Back is the first button on the left of the toolbar");
            Check(ReferenceEquals(back.Style, window.FindResource("ToolbarButton")), "toolbar: Back wears the toolbar's own button style");
            Check(back.ToolTip?.ToString()?.Contains("Alt+Left") == true, $"toolbar: Back's tooltip names Alt+Left ({back.ToolTip})");
            Check(back.IsEnabled, "toolbar: Back is live over an open picture");
            SaveRender(window.ToolbarGrid, Path.Combine(folder, "toolbar-editor.png"));
            back.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, back));
            await Settle();
            Check(LibraryShown() && !back.IsEnabled, "toolbar: Back shows the library and then rests");
            SaveRender(window.ToolbarGrid, Path.Combine(folder, "toolbar-library.png"));
            await Press(window, 2, center);
            Check(EditorShown(), "toolbar: forward after Back returns to the image");
            var source = PresentationSource.FromVisual(window)!;
            var altLeft = new System.Windows.Input.KeyEventArgs(new AltKeyboard(), source, 0, System.Windows.Input.Key.Left)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(altLeft);
            await Settle();
            Check(altLeft.Handled && LibraryShown(), "keyboard: Alt+Left on the open picture shows the library");
            var customized = MainWindow.NormalizeToolbarLayout(MainWindow.ParseToolbarLayout("capture,record,open;undo,redo;"));
            Check(customized.Left.FirstOrDefault() == "back", "toolbar: a layout saved before Back existed gets Back first, not last");
            App.Instance.SetDocument(null);
            await Settle();

            // Files: outer then inner, open an image, then walk back and forward.
            window.Library.SetView("files");
            await window.Library.FilesNavigation;
            await window.Library.Files.NavigateTo(outer);
            await window.Library.Files.NavigateTo(inner);
            await Settle();
            Check(window.Library.Files.Navigation.CurrentFolder == inner, "files: fixture sits in the inner folder");
            await Press(window, 1, center);
            Check(window.Library.Files.Navigation.CurrentFolder == outer, "files: back goes to the outer folder");
            await Press(window, 2, center);
            Check(window.Library.Files.Navigation.CurrentFolder == inner, "files: forward goes to the inner folder");
            App.Instance.SetDocument(new EditorDocument(image));
            await Settle();
            Check(EditorShown(), "files: the opened image is on screen");
            await Press(window, 2, center);
            report.Add($"files: after forward on the image, editor={EditorShown()} folder={window.Library.Files.Navigation.CurrentFolder}");
            Check(EditorShown(), "files: forward on the open image stays on the image");
            await Press(window, 1, center);
            Check(LibraryShown() && window.Library.FilesActive && window.Library.Files.Navigation.CurrentFolder == inner,
                "files: back on the image shows Files in the same folder");
            await Press(window, 2, center);
            Check(EditorShown(), "files: forward from Files returns to the image");
            App.Instance.SetDocument(null);
            await Settle();

            // The way a user opens a picture: activating it in the Files list.
            string picture = Path.Combine(inner, "shot.png");
            ImageOutput.SavePng(image, picture);
            await window.Library.Files.NavigateTo(inner);
            await Settle();
            var entry = window.Library.Files.Navigation.Results.Single(e => e.FileName == "shot.png");
            Check(await window.Library.Files.Browser.ActivateEntry(entry), "files: activating the picture opens it");
            await Settle();
            Check(EditorShown(), "files: the activated picture is on screen");
            await Press(window, 1, center);
            report.Add($"files: back from the activated picture, library={LibraryShown()} files={window.Library.FilesActive} folder={window.Library.Files.Navigation.CurrentFolder} canBack={window.Library.Files.Navigation.CanGoBack} canForward={window.Library.Files.Navigation.CanGoForward}");
            Check(LibraryShown() && window.Library.Files.Navigation.CurrentFolder == inner, "files: back from the activated picture shows its folder");
            await window.Library.Files.Browser.GoUp();
            await Settle();
            await Press(window, 2, center);
            Check(LibraryShown() && window.Library.Files.Navigation.CurrentFolder == outer,
                "files: after moving to another folder, forward no longer jumps to the old picture");
            Check(await window.Library.Files.Browser.ActivateEntry(entry) && EditorShown(), "files: the picture opens again");
            await Settle();
            await Press(window, 1, center);
            window.Library.SetView("grid");
            await Settle();
            await Press(window, 2, center);
            Check(LibraryShown(), "album: after switching views, forward no longer jumps to the old picture");

            // A picture opened after going back in Files is newer than the folder ahead in history.
            string outerPicture = Path.Combine(outer, "outer.png");
            ImageOutput.SavePng(image, outerPicture);
            window.Library.SetView("files");
            await window.Library.FilesNavigation;
            await window.Library.Files.NavigateTo(outer);
            await window.Library.Files.NavigateTo(inner);
            await window.Library.Files.Browser.GoBack();
            await Settle();
            Check(window.Library.Files.Navigation.CurrentFolder == outer && window.Library.Files.Navigation.CanGoForward,
                "files: fixture is back in the outer folder with the inner one ahead");
            var outerEntry = window.Library.Files.Navigation.Results.Single(e => e.FileName == "outer.png");
            Check(await window.Library.Files.Browser.ActivateEntry(outerEntry), "files: the outer picture opens");
            await Settle();
            await Press(window, 1, center);
            Check(LibraryShown() && window.Library.Files.Navigation.CurrentFolder == outer, "files: back from the outer picture shows the outer folder");
            await Press(window, 2, center);
            report.Add($"files: forward from the outer folder, editor={EditorShown()} folder={window.Library.Files.Navigation.CurrentFolder}");
            Check(EditorShown(), "files: forward returns to the picture opened there, not the older folder ahead");

            // The collection changes while the picture is up, so Files moves when the library comes back.
            await Press(window, 1, center);
            await window.Library.Files.NavigateTo(inner);
            await Settle();
            Check(await window.Library.Files.Browser.ActivateEntry(entry), "files: the inner picture opens");
            await Settle();
            window.TestStrip!.SetSource("c0");
            await Settle();
            await Press(window, 1, center);
            await window.Library.FilesNavigation;
            await Settle();
            report.Add($"files: back after a collection change, library={LibraryShown()} folder={window.Library.Files.Navigation.CurrentFolder}");
            await Press(window, 2, center);
            Check(EditorShown(), "files: forward returns to the picture after the library moved for a collection change");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            if (Directory.Exists(folder)) File.WriteAllLines(Path.Combine(folder, "mouse-history-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
