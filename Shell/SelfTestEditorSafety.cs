using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerFamily.Layout;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestEditorSafety
{
    public static bool TryRun(string[] args)
    {
        if (SelfTestSaveSafety.TryRun(args)) return true;
        if (SelfTestCarouselPerspective.TryRun(args)) return true;
        if (args.Length != 2 || args[0] != "--selftest-editor-safety") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        Settings.UseFolder(Path.Combine(folder, "settings"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        App.InitializeKit();
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            CheckImageReload(folder);
            report.Add("Loading an overwritten image reads current pixels and preserves alpha, dimensions and normalized DPI PASS");
            CheckAlpha(folder);
            report.Add("Annotated PNG preserves transparent, semitransparent and opaque pixels PASS");
            await CheckDelayedSelection();
            report.Add("Collection strip cancels superseded selections and opens the settled selection PASS");
            await CheckStripLifetime();
            report.Add("Disposed collection strip ignores queued updates and is collectible; active strip updates once PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        CaptureLibrary.Close();
        File.WriteAllLines(Path.Combine(folder, "editor-safety-report.txt"), report);
        App.Instance.Shutdown(code);
    }

    private static async Task CheckDelayedSelection()
    {
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        long aId = CaptureLibrary.AddNow(pixel, DateTime.Today, "region", "First", null);
        long bId = CaptureLibrary.AddNow(pixel, DateTime.Today.AddSeconds(1), "region", "Second", null);
        var a = CaptureLibrary.Items().Single(i => i.Id == aId);
        var b = CaptureLibrary.Items().Single(i => i.Id == bId);
        var host = new DockHost { Side = DockSide.Left };
        var strip = new CollectionStrip(host, new Style(typeof(Slider)));
        host.Pane = strip;
        strip.Follow(a, null);
        host.Measure(new Size(400, 700));
        host.Arrange(new Rect(0, 0, 400, 700));
        var list = Descendants<ListBox>(strip).Single();
        var opened = new List<long>();
        strip.OpenRequested += item => opened.Add(item.Id);
        try
        {
            list.SelectedItem = b;
            list.SelectedItem = a;
            await Task.Delay(350);
            if (opened.Count != 0) throw new InvalidOperationException("Returning from B to current A still opened B.");
            list.SelectedItem = b;
            list.SelectedIndex = -1;
            await Task.Delay(350);
            if (opened.Count != 0) throw new InvalidOperationException("Clearing B still opened B.");
            list.SelectedItem = b;
            await Task.Delay(350);
            if (!opened.SequenceEqual(new[] { bId })) throw new InvalidOperationException("The settled selection did not open exactly once.");
        }
        finally
        {
            if ((object)strip is IDisposable lifetime) lifetime.Dispose();
            host.Pane = null;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task CheckStripLifetime()
    {
        var activeHost = new DockHost { Side = DockSide.Left };
        var active = new CollectionStrip(activeHost, new Style(typeof(Slider)));
        activeHost.Pane = active;
        int activeReloads = 0, retiredReloads = 0;
        active.SourceChanged += () => activeReloads++;
        var retired = RetireStrip(() => retiredReloads++);
        CaptureLibrary.CreateCollection("After closing");
        await Task.Delay(250);
        if (retiredReloads != 0) throw new InvalidOperationException($"The disposed strip reloaded {retiredReloads} times after closing.");
        if (activeReloads != 2) throw new InvalidOperationException($"The active strip reloaded {activeReloads} times for two album changes.");
        CardColors.SetCard(Colors.DarkCyan);
        FolderCaptures.Enabled = false;
        for (int attempt = 0; attempt < 3 && retired.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }
        if (retired.IsAlive) throw new InvalidOperationException("The disposed strip remains retained after publisher updates and collection.");
        if ((object)active is IDisposable lifetime) lifetime.Dispose();
        activeHost.Pane = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireStrip(Action reloaded)
    {
        var host = new DockHost { Side = DockSide.Left };
        var strip = new CollectionStrip(host, new Style(typeof(Slider)));
        host.Pane = strip;
        strip.SourceChanged += reloaded;
        CaptureLibrary.CreateCollection("Queued before closing");
        if ((object)strip is IDisposable lifetime)
        {
            lifetime.Dispose();
            lifetime.Dispose();
        }
        host.Pane = null;
        return new WeakReference(strip);
    }

    private static void CheckImageReload(string folder)
    {
        string path = Path.Combine(folder, "reloaded.png");
        var white = BitmapSource.Create(2, 1, 144, 144, PixelFormats.Bgra32, null,
            new byte[] { 255, 255, 255, 255, 0, 0, 255, 128 }, 8);
        ImageOutput.SavePng(white, path);
        AssertPixel(ImageOutput.Load(path), 0, 0, 255, 255, 255, 255, "Original image");
        var black = BitmapSource.Create(2, 1, 144, 144, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 0, 255, 0, 0, 255, 128 }, 8);
        ImageOutput.SavePng(black, path);
        var reloaded = ImageOutput.Load(path);
        AssertPixel(reloaded, 0, 0, 0, 0, 0, 255, "Overwritten image");
        AssertPixel(reloaded, 1, 0, 0, 0, 255, 128, "Reloaded semitransparent pixel");
        if (reloaded.PixelWidth != 2 || reloaded.PixelHeight != 1 || reloaded.DpiX != 96 || reloaded.DpiY != 96)
            throw new InvalidOperationException("Reloading changed the image's pixel dimensions or normalized DPI.");
    }

    private static void CheckAlpha(string folder)
    {
        const int width = 12, height = 12;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 4; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = (byte)(x < 8 ? 128 : 255);
            }
        var input = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        string path = Path.Combine(folder, "annotated-alpha.png");
        ImageOutput.SavePng(input, path);
        var document = new EditorDocument(ImageOutput.Load(path), path);
        document.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(1, 1), new Point(11, 4), Colors.Black, 1));
        ImageOutput.SavePng(document.Render(), path);
        // Decode the bytes on disk directly so WPF's URI cache cannot return the input image.
        using var saved = File.OpenRead(path);
        var actual = new FormatConvertedBitmap(BitmapDecoder.Create(saved, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        AssertPixel(actual, 2, 9, 0, 0, 0, 0, "Untouched transparent pixel");
        AssertPixel(actual, 5, 9, 0, 0, 255, 128, "Untouched semitransparent pixel");
        AssertPixel(actual, 9, 9, 0, 0, 255, 255, "Untouched opaque pixel");
        AssertPixel(actual, 2, 2, 0, 0, 0, 255, "Redaction over transparency");

        var opaque = new byte[width * height * 4];
        for (int i = 0; i < opaque.Length; i += 4) { opaque[i + 1] = 255; opaque[i + 3] = 255; }
        var capture = new EditorDocument(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, opaque, width * 4));
        capture.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(1, 1), new Point(4, 4), Colors.Black, 1));
        string opaquePath = Path.Combine(folder, "annotated-opaque.png");
        ImageOutput.SavePng(capture.Render(), opaquePath);
        AssertPixel(ImageOutput.Load(opaquePath), 9, 9, 0, 255, 0, 255, "Opaque capture export");
    }

    private static void AssertPixel(BitmapSource image, int x, int y, byte b, byte g, byte r, byte a, string description)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        if (!pixel.SequenceEqual(new byte[] { b, g, r, a }))
            throw new InvalidOperationException($"{description}: expected BGRA {b},{g},{r},{a}; got {string.Join(',', pixel)}.");
    }
}
