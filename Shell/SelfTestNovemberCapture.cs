using System.IO;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestNovemberCapture
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length == 2 && args[0] == "--selftest-november-scroll")
        {
            _ = ScrollKeysAsync(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length == 2 && args[0] == "--selftest-november-monitor")
        {
            _ = MonitorResizeAsync(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length != 2 || args[0] != "--selftest-november-capture") return false;
        _ = RunAsync(args);
        return true;
    }

    private static async Task RunAsync(string[] args)
    {
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        int code = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            int copies = 0, warnings = 0;
            var call = System.Diagnostics.Stopwatch.StartNew();
            var copy = VideoController.CopyFileToClipboardAsync("recording.mp4", (_, _) => warnings++, _ =>
            {
                copies++;
                throw new System.Runtime.InteropServices.COMException("Clipboard fixture is busy.");
            });
            Expect(call.ElapsedMilliseconds < 100, "Recording clipboard retries blocked the calling UI thread.");
            await copy;
            Expect(copies == 3 && warnings == 1, "Recording clipboard failure was not bounded and reported.");
            QrBudget();
            QrMultipleCodes();
            using (var api = new BufferedGdi())
            using (var grabber = new ScreenGrabber(new Win32.RECT(0, 0, 1, 1), false, api))
            {
                var pixel = new byte[4];
                Expect(grabber.Grab(pixel) && pixel[0] == 123, "GDI pixels were read before pending drawing finished.");
            }
            var bounds = new Win32.RECT(0, 0, 100, 100);
            var desktop = new FrozenDesktop(bounds, new byte[100 * 100 * 4], [new MonitorBounds(IntPtr.Zero, bounds, true)], []);
            CaptureSettings.LastMode = CaptureMode.Region;
            var selector = new RegionSelector(desktop);
            selector.SetMode(CaptureMode.Window);
            selector.Cancel();
            Expect(CaptureSettings.LastMode == CaptureMode.Window, "Cancel lost the dropdown choice.");
            Expect(CaptureController.Resolve(null, null) == CaptureMode.Window, "Generic capture ignored remembered Window mode.");
            Expect(CaptureController.Resolve(ShortcutAction.LastMode, null) == CaptureMode.Window, "Print Screen ignored remembered Window mode.");
            Expect(CaptureController.Resolve(ShortcutAction.Monitor, null) == CaptureMode.Monitor, "Explicit Monitor shortcut was overridden.");
            Expect(CaptureController.Resolve(null, CaptureMode.Region) == CaptureMode.Region, "Explicit Region request was overridden.");
            selector = new RegionSelector(desktop);
            selector.SetMode(CaptureMode.Qr);
            selector.Cancel();
            Expect(CaptureSettings.LastMode == CaptureMode.Window, "QR replaced the remembered photo mode.");
            selector = new RegionSelector(desktop, video: true);
            selector.SetMode(CaptureMode.Monitor);
            selector.Cancel();
            Expect(CaptureSettings.LastMode == CaptureMode.Window, "Video replaced the remembered photo mode.");
            File.WriteAllText(Path.Combine(folder, "capture-report.txt"), "PASS: capture dropdown memory, cancel, generic and explicit routing, QR and video exclusion.");
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "capture-report.txt"), "FAIL: " + error); }
        System.Windows.Application.Current.Shutdown(code);
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private static async Task MonitorResizeAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        int code = 1;
        System.Windows.Window? window = null;
        try
        {
            App.InitializeKit();
            var monitor = FrozenDesktop.EnumerateMonitors().First(m => m.Primary);
            var rect = new Win32.RECT(monitor.Bounds.Left + 10, monitor.Bounds.Top + 10, monitor.Bounds.Left + 74, monitor.Bounds.Top + 74);
            bool changed = false;
            using var grabber = MonitorGrabber.TryCreate(rect, false, contentSize: frame =>
            {
                var size = GraphicsCapture.FrameContentSize(frame);
                if (changed) size.Width--;
                return size;
            }) ?? throw new InvalidOperationException("Windows Graphics Capture unavailable for monitor resize fixture.");
            window = new System.Windows.Window { Width = 100, Height = 100, Left = rect.Left, Top = rect.Top, ShowInTaskbar = false };
            window.Show();
            changed = true;
            bool rejected = false;
            for (int i = 0; i < 20 && !rejected; i++)
            {
                window.Background = i % 2 == 0 ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Blue;
                await Task.Delay(30);
                try { grabber.Grab(new byte[64 * 64 * 4]); }
                catch (InvalidOperationException error) when (error.Message == LocaleManager.Loc("Str_Video_MonitorChanged")) { rejected = true; }
            }
            Expect(rejected, "Changed monitor dimensions were accepted into the existing recording.");
            File.WriteAllText(Path.Combine(folder, "monitor-report.txt"), "PASS: actual WGC Pump rejected a native frame content size change.");
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "monitor-report.txt"), "FAIL: " + error); }
        finally { window?.Close(); }
        App.Instance.Shutdown(code);
    }

    private static async Task ScrollKeysAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        int code = 1;
        ScrollCaptureSession? session = null;
        using var owner = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("KillerShot hotkey conflict fixture") { ParentWindow = new IntPtr(-3) });
        try
        {
            App.InitializeKit();
            Expect(Win32.RegisterHotKey(owner.Handle, 1, Win32.MOD_NOREPEAT, 0x0D), "Could not reserve Enter for the conflict fixture.");
            Expect(Win32.RegisterHotKey(owner.Handle, 2, Win32.MOD_NOREPEAT, 0x1B), "Could not reserve Esc for the conflict fixture.");
            session = new ScrollCaptureSession(new Win32.RECT(100, 100, 164, 164), () => new EmptyFrames());
            var result = session.RunAsync();
            var bar = System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().Single(w => Visuals(w).OfType<System.Windows.Controls.Button>().Any(b => Equals(b.Content, LocaleManager.Loc("Str_Scroll_Done"))));
            Expect(Visuals(bar).OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains(LocaleManager.Loc("Str_Scroll_KeysUnavailable"), StringComparison.Ordinal)), "Scrolling capture hid the hotkey conflict hint.");
            bar.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(bar), 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Expect(await result.WaitAsync(TimeSpan.FromSeconds(1)) == null, "Focused Escape did not cancel scrolling capture.");
            File.WriteAllText(Path.Combine(folder, "scroll-report.txt"), "PASS: owned Enter/Esc conflict displays hint and focused Esc cancels.");
            code = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "scroll-report.txt"), "FAIL: " + error); }
        finally
        {
            session?.TestFinish(false);
            Win32.UnregisterHotKey(owner.Handle, 1);
            Win32.UnregisterHotKey(owner.Handle, 2);
        }
        App.Instance.Shutdown(code);
    }

    private static IEnumerable<System.Windows.DependencyObject> Visuals(System.Windows.DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class EmptyFrames : IFrameGrabber
    {
        public int Width => 64;
        public int Height => 64;
        public bool Grab(byte[] bgra) => false;
        public void Dispose() { }
    }

    private static void QrBudget()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool stopped = false;
        try { KillerTools.Qr.QrReader.Read([255], 1, 1, canceled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Expect(stopped, "QR reading ignored caller cancellation.");
        const int width = 3840, height = 2160;
        var gray = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int a = x % 120 / 12, b = y % 120 / 12;
                if (a < 7 && b < 7 && (a == 0 || a == 6 || b == 0 || b == 6 || a is >= 2 and <= 4 && b is >= 2 and <= 4)) gray[y * width + x] = 0;
            }
        var read = Task.Run(() => KillerTools.Qr.QrReader.Read(gray, width, height));
        Expect(read.Wait(3000), "QR finder-pattern image exceeded three seconds.");
    }

    private static void QrMultipleCodes()
    {
        // Independent QRCoder 1.8.0 fixture: HELLO, error correction M, including the quiet zone.
        string[] modules =
        [
            "00000000000000000000000000000",
            "00000000000000000000000000000",
            "00000000000000000000000000000",
            "00000000000000000000000000000",
            "00001111111000111011111110000",
            "00001000001000010010000010000",
            "00001011101010101010111010000",
            "00001011101010011010111010000",
            "00001011101011111010111010000",
            "00001000001011001010000010000",
            "00001111111010101011111110000",
            "00000000000010011000000000000",
            "00001011111000001011111000000",
            "00000000100111001001011110000",
            "00001100111100010100110010000",
            "00000011000001000001100000000",
            "00000000111100110100101000000",
            "00000000000010111110010110000",
            "00001111111000001011001010000",
            "00001000001011111110010010000",
            "00001011101010101001001000000",
            "00001011101010001001001000000",
            "00001011101010010100101000000",
            "00001000001000100001101010000",
            "00001111111010110100101000000",
            "00000000000000000000000000000",
            "00000000000000000000000000000",
            "00000000000000000000000000000",
            "00000000000000000000000000000",
        ];
        const int scale = 4, gap = 4, count = 3;
        int size = modules.Length * scale;
        int width = count * size + (count - 1) * gap * scale;
        byte[] gray = Enumerable.Repeat((byte)255, width * size).ToArray();
        for (int code = 0; code < count; code++)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (modules[y / scale][x / scale] == '1')
                        gray[y * width + code * (size + gap * scale) + x] = 0;
        var results = KillerTools.Qr.QrReader.Read(gray, width, size);
        Expect(results.Count == 3 && results.All(result => result.Text == "HELLO"),
            "A clean three-code image decoded " + results.Count + " QR codes instead of three.");
    }

    private sealed class BufferedGdi : ScreenGrabber.GdiResources, IDisposable
    {
        private readonly IntPtr _bits = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        internal override IntPtr GetScreen() => (IntPtr)1;
        internal override IntPtr CreateMemory(IntPtr screen) => (IntPtr)2;
        internal override IntPtr CreateBitmap(IntPtr screen, ref Win32.BITMAPINFOHEADER info, out IntPtr bits)
        {
            bits = _bits;
            System.Runtime.InteropServices.Marshal.WriteInt32(bits, 0);
            return (IntPtr)3;
        }
        internal override IntPtr Select(IntPtr memory, IntPtr bitmap) => (IntPtr)4;
        internal override bool CopyScreen(IntPtr memory, IntPtr screen, Win32.RECT rect) => true;
        internal override void Flush() => System.Runtime.InteropServices.Marshal.WriteByte(_bits, 123);
        internal override void DeleteBitmap(IntPtr bitmap) { }
        internal override void DeleteMemory(IntPtr memory) { }
        internal override void ReleaseScreen(IntPtr screen) { }
        public void Dispose() => System.Runtime.InteropServices.Marshal.FreeHGlobal(_bits);
    }
}
