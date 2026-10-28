using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Interface check: KillerShot.exe --selftest-ui &lt;folder&gt;. Builds a test picture,
/// exercises every mark, crop, undo and export, then renders the editor window off screen in
/// several themes and with each overlay open, writing PNGs and report.txt. The window is placed
/// off screen and never activated, so it does not take focus, and settings live in the output
/// folder so the user's choices are untouched.</summary>
internal static class SelfTestUi
{
    public static bool TryRun(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--selftest-audio-start", StringComparison.OrdinalIgnoreCase))
        {
            string testDir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(testDir);
            _ = RunAudioStartAsync(testDir);
            return true;
        }
        if (args.Length >= 2 && args[0].Equals("--selftest-toolbar-theme", StringComparison.OrdinalIgnoreCase))
        {
            string testDir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(testDir);
            Settings.UseFolder(Path.Combine(testDir, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(testDir, "library"));
            App.InitializeKit();
            _ = RunToolbarThemeAsync(testDir);
            return true;
        }
        int i = Array.FindIndex(args, a => a.Equals("--selftest-ui", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest-ui");
        Directory.CreateDirectory(dir);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        CaptureLibrary.UseFolder(Path.Combine(dir, "library"));
        FolderCaptures.UseFolder(Path.Combine(dir, "Screenshots"));
        FolderCaptures.Enabled = false;
        App.InitializeKit();
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAudioStartAsync(string dir)
    {
        AudioCapture? audio = null;
        int code = 1;
        string report;
        try
        {
            var first = new FakeAudioSource();
            var second = new FakeAudioSource();
            int[] clockMs = [0];
            var output = new TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously);
            audio = new AudioCapture([first, second],
                () => TimeSpan.FromMilliseconds(Volatile.Read(ref clockMs[0])),
                () => false,
                (pcm, _) => output.TrySetResult(BitConverter.ToInt16(pcm, 0)));
            audio.Start();
            audio.DiscardBuffered();
            first.Append(0.1f);
            second.Append(0.1f);
            Volatile.Write(ref clockMs[0], 20);
            short firstSample = await output.Task.WaitAsync(TimeSpan.FromSeconds(3));
            audio.Stop();
            if (firstSample is < 6000 or > 7000)
                throw new InvalidOperationException($"Audio captured before time zero was emitted: {firstSample}.");
            report = "Pre-clock audio is discarded before the first timed PCM chunk: PASS";
            code = 0;
        }
        catch (Exception ex) { report = ex.ToString(); }
        finally { audio?.Dispose(); }
        File.WriteAllText(Path.Combine(dir, "audio-start-report.txt"), report);
        Application.Current.Shutdown(code);
    }

    private sealed class FakeAudioSource : IAudioCaptureSource
    {
        private readonly Queue<float> _samples = new();
        private readonly object _gate = new();
        public Exception? Error => null;
        public void Start() => Append(0.7f);
        public void Stop() { }
        public void Dispose() { }

        public void Append(float sample)
        {
            lock (_gate)
                for (int i = 0; i < AudioCapture.Rate * AudioCapture.Channels / 50; i++)
                    _samples.Enqueue(sample);
        }

        public void Drain(int frames, float[]? mix)
        {
            lock (_gate)
            {
                int samples = Math.Min(_samples.Count / AudioCapture.Channels, frames) * AudioCapture.Channels;
                for (int i = 0; i < samples; i++)
                {
                    float value = _samples.Dequeue();
                    if (mix != null) mix[i] += value;
                }
            }
        }
    }

    private static async Task RunToolbarThemeAsync(string dir)
    {
        MainWindow? window = null;
        int code = 1;
        string report;
        try
        {
            ThemeManager.Apply(Theme.Dark);
            window = new MainWindow
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
            };
            window.Show();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var width = window.FindName("WidthBtn") as Button
                ?? throw new InvalidOperationException("The Width toolbar button is missing.");
            var bar = FindWidthBar(width.Content as DependencyObject
                ?? throw new InvalidOperationException("The Width toolbar icon is missing."));
            if (bar.RadiusX != 1 || bar.RadiusY != 1)
                throw new InvalidOperationException("The modern Width toolbar icon lost its original corners.");

            ThemeManager.Apply(Theme.SE98);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (bar.RadiusX != 0 || bar.RadiusY != 0)
                throw new InvalidOperationException("The Width toolbar icon stayed rounded after switching to 98SE.");

            ThemeManager.Apply(Theme.Dark);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (bar.RadiusX != 1 || bar.RadiusY != 1)
                throw new InvalidOperationException("The Width toolbar icon did not restore modern corners.");

            report = "Width toolbar corners follow Dark, 98SE, and Dark live theme switches: PASS";
            code = 0;
        }
        catch (Exception ex) { report = ex.ToString(); }
        finally
        {
            try { window?.Close(); } catch { }
        }
        File.WriteAllText(Path.Combine(dir, "toolbar-theme-report.txt"), report);
        Application.Current.Shutdown(code);
    }

    private static System.Windows.Shapes.Rectangle FindWidthBar(DependencyObject root)
    {
        if (root is System.Windows.Shapes.Rectangle rectangle) return rectangle;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return FindWidthBar(VisualTreeHelper.GetChild(root, i)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("The Width toolbar icon has no bar.");
    }

    private static async Task RunAsync(string dir)
    {
        var log = new StringBuilder();
        int code = 0;
        try
        {
            var pattern = TestPattern(960, 600);
            CheckModel(pattern, log);
            await CheckVideo(dir, log);

            var doc = new EditorDocument(pattern, null, new DateTime(2026, 10, 13, 9, 30, 0));
            doc.Add(new StrokeAnnotation([new(80, 420), new(140, 380), new(220, 430), new(300, 370)], Color.FromRgb(0xE5, 0x39, 0x35), 6, false));
            doc.Add(new StrokeAnnotation([new(80, 120), new(380, 120)], Color.FromRgb(0xFD, 0xD8, 0x35), 24, true));
            doc.Add(new ShapeAnnotation(ShapeKind.Arrow, new(520, 380), new(700, 250), Color.FromRgb(0xE5, 0x39, 0x35), 6));
            doc.Add(new ShapeAnnotation(ShapeKind.Rectangle, new(600, 80), new(860, 200), Color.FromRgb(0x2E, 0xC4, 0xB6), 4));
            doc.Add(new ShapeAnnotation(ShapeKind.Ellipse, new(380, 300), new(500, 380), Color.FromRgb(0x1E, 0x88, 0xE5), 4));
            doc.Add(new ShapeAnnotation(ShapeKind.Redact, new(620, 440), new(880, 500), Colors.Black, 4));
            doc.Add(new TextAnnotation(new(90, 220), "KillerShot", Colors.White, 40));
            ImageOutput.SavePng(doc.Render(), Path.Combine(dir, "export.png"));
            log.AppendLine($"export={doc.Render().PixelWidth}x{doc.Render().PixelHeight}");

            App.Instance.SetDocument(doc);
            var w = new MainWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1280, Height = 820 };
            w.Show();
            await Wait(700);

            async Task Shot(string name)
            {
                await Wait(450);
                w.UpdateLayout();
                var root = (FrameworkElement)w.Content;
                var dpi = VisualTreeHelper.GetDpi(w);
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(root);
                ImageOutput.SavePng(rtb, Path.Combine(dir, name + ".png"));
                log.AppendLine($"shot {name} {rtb.PixelWidth}x{rtb.PixelHeight}");
            }

            ThemeManager.Apply(Theme.Black);
            ThemeManager.ApplyAccent(Theme.Black, Accent.Teal);
            LocaleManager.Apply(Locale.EnUS);
            w.TestRelocalize();
            await Shot("editor-black-teal");
            w.TestShowShortcuts(false); await Shot("shortcuts-list");
            w.TestShowShortcuts(true); await Shot("shortcuts-map");
            w.TestCloseOverlays();
            w.TestShowAbout(); await Shot("about");
            w.TestCloseOverlays();
            w.TestShowCaptureSettings(); await Shot("capture-settings");
            w.TestCloseOverlays();
            w.TestShowVideoSettings(); await Shot("video-settings");
            w.TestCloseOverlays();
            w.TestSetLabels(true); await Shot("toolbar-labels");
            w.TestSetLabels(false);

            foreach (var t in new[] { Theme.Dark, Theme.Light, Theme.SE98, Theme.Blood, Theme.Delirium, Theme.Malaise })
            {
                ThemeManager.Apply(t);
                await Shot("theme-" + t);
            }
            ThemeManager.Apply(Theme.Black);

            foreach (var loc in new[] { Locale.De, Locale.JaJP, Locale.RuRU })
            {
                LocaleManager.Apply(loc);
                w.TestRelocalize();
                w.TestShowCaptureSettings();
                await Shot("locale-" + loc);
                w.TestCloseOverlays();
            }
            LocaleManager.Apply(Locale.EnUS);
            w.TestRelocalize();

            doc.MarkSaved();
            App.Instance.SetDocument(null);
            await Shot("empty");
            w.Close();
            log.AppendLine("result=ok");
        }
        catch (Exception ex)
        {
            log.AppendLine($"result=error {ex}");
            code = 1;
        }
        try { File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString()); } catch { }
        Application.Current.Shutdown(code);
    }

    private static Task Wait(int ms) => Task.Delay(ms);

    /// <summary>Encodes synthetic frames to GIF and MP4, reads them back, and records one second
    /// of a small screen rectangle in each format through the real recorder.</summary>
    private static async Task CheckVideo(string dir, StringBuilder log)
    {
        const int W = 320, H = 240;
        byte[] Frame(int t)
        {
            var f = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    bool top = y < H / 2;
                    f[o] = (byte)(top ? 0 : 255); f[o + 1] = 0; f[o + 2] = (byte)(top ? 255 : 0); f[o + 3] = 255;   // red top, blue bottom
                    if (x >= t * 10 && x < t * 10 + 30 && y > 100 && y < 140) { f[o] = 255; f[o + 1] = 255; f[o + 2] = 255; }
                }
            return f;
        }

        string gifPath = Path.Combine(dir, "synthetic.gif");
        var gif = new KillerShot.Video.GifEncoder(gifPath, 255, 1.0, true);
        await gif.StartAsync(W, H, 10);
        for (int t = 0; t < 20; t++) gif.AddFrame(Frame(t), TimeSpan.FromMilliseconds(t * 100));
        await gif.FinishAsync(TimeSpan.FromSeconds(2));
        var dec = new GifBitmapDecoder(new Uri(gifPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        log.AppendLine($"gif_frames={dec.Frames.Count} bytes={new FileInfo(gifPath).Length}");
        if (dec.Frames.Count != 20) throw new InvalidOperationException("GIF frame count is wrong.");

        string mp4Path = Path.Combine(dir, "synthetic.mp4");
        var mp4 = new KillerShot.Video.Mp4Encoder(mp4Path, 0.2, 128_000);
        await mp4.StartAsync(W, H, 10);
        // Two seconds of a 440 Hz tone in 20 ms chunks alongside the frames.
        int chunk = KillerShot.Video.AudioCapture.Rate / 50;
        for (int c = 0; c < 100; c++)
        {
            var pcm = new byte[chunk * 4];
            for (int i = 0; i < chunk; i++)
            {
                short v = (short)(Math.Sin(2 * Math.PI * 440 * (c * chunk + i) / KillerShot.Video.AudioCapture.Rate) * 8000);
                pcm[i * 4] = (byte)v; pcm[i * 4 + 1] = (byte)(v >> 8); pcm[i * 4 + 2] = (byte)v; pcm[i * 4 + 3] = (byte)(v >> 8);
            }
            mp4.AddAudio(pcm, TimeSpan.FromMilliseconds(c * 20));
            if (c % 5 == 0) mp4.AddFrame(Frame(c / 5), TimeSpan.FromMilliseconds(c * 20));
        }
        await mp4.FinishAsync(TimeSpan.FromSeconds(2));
        long mp4Bytes = new FileInfo(mp4Path).Length;
        var probe = SelfTestMp4Probe.Read(mp4Path, TimeSpan.FromMilliseconds(500));
        var top = SelfTestMp4Probe.PixelAt(probe.Pixels, probe.Width, probe.Height, 20.0 / W, 20.0 / H);
        bool topRed = top.R > 150 && top.B < 100;
        log.AppendLine($"mp4_audio_tracks={probe.AudioTracks}");
        if (probe.AudioTracks != 1) throw new InvalidOperationException("MP4 has no sound track.");
        log.AppendLine($"mp4_bytes={mp4Bytes} duration={probe.Duration.TotalSeconds:F1}s top_pixel_bgr={top.B},{top.G},{top.R} upright={topRed}");
        if (mp4Bytes < 1000) throw new InvalidOperationException("MP4 is empty.");
        if (!topRed) throw new InvalidOperationException("MP4 frames are upside down.");

        // The real recorder on a small rectangle of the primary monitor, one second each.
        var rect = new KillerShot.Native.Win32.RECT(0, 0, 320, 240);
        foreach (var fmt in new[] { "gif", "mp4" })
        {
            string path = Path.Combine(dir, "recorded." + fmt);
            KillerShot.Video.IVideoSink sink = fmt == "gif"
                ? new KillerShot.Video.GifEncoder(path, 255, 1.0, true)
                : new KillerShot.Video.Mp4Encoder(path, 0.09, 128_000);
            var rec = new KillerShot.Video.Recorder(rect, sink, fmt == "gif" ? 10 : 30, true,
                fmt == "gif" ? KillerShot.Video.SoundSource.None : KillerShot.Video.SoundSource.System);
            await rec.StartAsync();
            await Task.Delay(1000);
            await rec.StopAsync();
            log.AppendLine($"recorded_{fmt} frames={rec.Frames} dropped={rec.Dropped} bytes={new FileInfo(path).Length} graphics_capture={rec.UsesGraphicsCapture}");
            if (rec.Frames < 5 || new FileInfo(path).Length < 500) throw new InvalidOperationException("Recording produced no video.");
            if (KillerShot.Capture.WindowCapture.IsSupported && !rec.UsesGraphicsCapture)
                throw new InvalidOperationException("Recording fell back to GDI although Windows Graphics Capture is available.");
        }
    }

    /// <summary>Model checks that need no window: exact export of an unedited capture, mark
    /// export size, crop and undo.</summary>
    private static void CheckModel(BitmapSource pattern, StringBuilder log)
    {
        var when = new DateTime(2026, 10, 13, 9, 5, 7);
        var fname = OutputFiles.FileName("Shot {yyyy}-{MM}-{dd} {HH}{mm}{ss} {mode} {width}x{height} a:b", when, KillerShot.Capture.CaptureMode.Window, 800, 600, "png");
        log.AppendLine($"file_name={fname}");
        if (fname != "Shot 2026-10-13 090507 window 800x600 a_b.png") throw new InvalidOperationException("File name pattern expanded wrongly.");
        string tmp = Path.Combine(Path.GetTempPath(), "KillerShot-selftest-names");
        Directory.CreateDirectory(tmp);
        foreach (var f in Directory.GetFiles(tmp)) File.Delete(f);
        File.WriteAllText(Path.Combine(tmp, "a.png"), "");
        var next = OutputFiles.NextPath(tmp, "a.png");
        log.AppendLine($"collision_path={Path.GetFileName(next)}");
        if (Path.GetFileName(next) != "a (2).png") throw new InvalidOperationException("Existing file would be overwritten.");
        log.AppendLine($"default_folder={OutputFiles.DefaultFolder}");

        var doc = new EditorDocument(pattern);
        bool same = ReferenceEquals(doc.Render(), pattern);
        log.AppendLine($"unedited_export_is_capture={same}");
        if (!same) throw new InvalidOperationException("Unedited export is not the captured bitmap.");

        doc.Add(new ShapeAnnotation(ShapeKind.Redact, new(10, 10), new(60, 40), Colors.Black, 1));
        var r = doc.Render();
        var px = new byte[4];
        r.CopyPixels(new Int32Rect(20, 20, 1, 1), px, 4, 0);
        bool black = px[0] == 0 && px[1] == 0 && px[2] == 0;
        var outside = new byte[4];
        r.CopyPixels(new Int32Rect(100, 100, 1, 1), outside, 4, 0);
        var orig = new byte[4];
        pattern.CopyPixels(new Int32Rect(100, 100, 1, 1), orig, 4, 0);
        bool untouched = outside[0] == orig[0] && outside[1] == orig[1] && outside[2] == orig[2];
        log.AppendLine($"redact_opaque={black} outside_untouched={untouched} size={r.PixelWidth}x{r.PixelHeight}");
        if (!black || !untouched || r.PixelWidth != pattern.PixelWidth) throw new InvalidOperationException("Redaction export check failed.");

        bool cropped = doc.Crop(new Int32Rect(5, 5, 300, 200));
        log.AppendLine($"crop={cropped} size={doc.PixelWidth}x{doc.PixelHeight} marks_moved={((ShapeAnnotation)doc.State.Items[0]).A}");
        doc.Undo();
        log.AppendLine($"undo size={doc.PixelWidth}x{doc.PixelHeight} can_redo={doc.CanRedo}");
        if (doc.PixelWidth != pattern.PixelWidth) throw new InvalidOperationException("Undo did not restore the crop.");
        doc.Redo();
        if (doc.PixelWidth != 300) throw new InvalidOperationException("Redo did not reapply the crop.");

        for (int k = 0; k < EditorDocument.UndoLimit + 10; k++)
            doc.Add(new TextAnnotation(new(1, 1), "x", Colors.Red, 10));
        int undos = 0;
        while (doc.CanUndo) { doc.Undo(); undos++; }
        log.AppendLine($"undo_bounded={undos}");
        if (undos != EditorDocument.UndoLimit) throw new InvalidOperationException("Undo history is not bounded.");
    }

    /// <summary>A picture with sharp single-pixel detail so scaling errors are visible.</summary>
    private static BitmapSource TestPattern(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bool grid = x % 40 == 0 || y % 40 == 0;
                bool checker = ((x / 2) + (y / 2)) % 2 == 0 && x > w - 120 && y > h - 120;
                byte b = (byte)(60 + 120 * x / w), g = (byte)(50 + 100 * y / h), r = (byte)(70 + 60 * (x + y) / (w + h));
                if (grid) { b = 200; g = 200; r = 200; }
                if (checker) { b = 255; g = 255; r = 255; }
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
