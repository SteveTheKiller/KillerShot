using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Native;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Headless checks for the capture and editor review findings:
/// KillerShot.exe --selftest-review-capture &lt;folder&gt;. Each check names its finding.</summary>
internal static class SelfTestReviewCapture
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-review-capture", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int failures = 0;
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); report.Add(name + " PASS"); }
            catch (Exception error) { failures++; report.Add(name + " FAIL: " + error.Message); }
            File.WriteAllLines(Path.Combine(folder, "review-capture-report.txt"), report);
        }
        Window? window = null;
        try
        {
            await Check("CP-001 audio writer stops before the failed encoder releases its writer", () => CheckAudioAfterFailure(folder));
            await Check("CP-002 borderless capture is requested once per process", () => { CheckBorderlessOnce(); return Task.CompletedTask; });
            var canvas = new EditorCanvas();
            window = new Window { Content = canvas, Width = 400, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            await Check("ED-001 Escape during a region pick drag leaves the picture unchanged", () => CheckRegionPickEscape(window, canvas));
            await Check("ED-002 shrinking a stroke already at its smallest adds no undo step", () => { CheckStrokeNoOp(window, canvas); return Task.CompletedTask; });
            await Check("ED-006 stored text, callout and step sizes past text layout are rejected", () => { CheckMarkSizes(); return Task.CompletedTask; });
            await Check("ED-007 a mark's automation element survives a move", () => { CheckPeerFollows(window, canvas); return Task.CompletedTask; });
        }
        catch (Exception error) { failures++; report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            report.Add(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
            File.WriteAllLines(Path.Combine(folder, "review-capture-report.txt"), report);
            App.Instance.Shutdown(failures == 0 ? 0 : 1);
        }
    }

    private static async Task CheckAudioAfterFailure(string folder)
    {
        var injected = new InvalidOperationException("Injected video write failure.");
        using var encoder = new Mp4Encoder(Path.Combine(folder, "audio-fault.mp4"), .2, audioBitrate: 128_000);
        var release = new ManualResetEventSlim(false);
        int finished = 0, lateAudio = 0, audioWrites = 0;
        encoder.BeforeSampleForTest = audio =>
        {
            if (!audio) { release.Wait(5000); throw injected; }
            Interlocked.Increment(ref audioWrites);
            Thread.Sleep(25);
            if (Volatile.Read(ref finished) != 0) Interlocked.Increment(ref lateAudio);
        };
        await encoder.StartAsync(64, 64, 30);
        int chunk = AudioCapture.Rate / 100 * AudioCapture.Channels * 2;   // 10 ms
        for (int i = 0; i < 40; i++) encoder.AddAudio(new byte[chunk], TimeSpan.FromMilliseconds(i * 10));
        var pixels = new byte[64 * 64 * 4];
        encoder.AddFrame(pixels, TimeSpan.Zero);
        encoder.AddFrame(pixels, TimeSpan.FromMilliseconds(33));
        while (Volatile.Read(ref audioWrites) == 0) await Task.Delay(5);
        release.Set();
        Exception? thrown = null;
        try { await encoder.FinishAsync(TimeSpan.FromMilliseconds(400)).WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception error) { thrown = error; }
        Volatile.Write(ref finished, 1);
        await Task.Delay(600);
        if (!ReferenceEquals(thrown, injected))
            throw new InvalidOperationException($"FinishAsync did not report the injected failure: {thrown?.GetType().Name ?? "no error"}.");
        if (lateAudio > 0 || encoder.SamplesAfterClose > 0)
            throw new InvalidOperationException($"{lateAudio} audio samples were written after FinishAsync released the writer.");
    }

    private static void CheckBorderlessOnce()
    {
        for (int i = 0; i < 3; i++) GraphicsCapture.RequestBorderless(50);
        int asked = GraphicsCapture.BorderlessRequestsForTest;
        if (asked != 1) throw new InvalidOperationException($"Borderless access was requested {asked} times.");
    }

    private static BitmapSource Image()
    {
        var bmp = BitmapSource.Create(200, 150, 96, 96, PixelFormats.Bgra32, null, new byte[200 * 150 * 4], 200 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void Mouse(UIElement target, RoutedEvent routed)
    {
        if (routed == UIElement.MouseMoveEvent)
            target.RaiseEvent(new MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = routed });
        else
            target.RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed });
    }

    private static Task CheckRegionPickEscape(Window window, EditorCanvas canvas)
    {
        foreach (var tool in new[] { ToolKind.Rectangle, ToolKind.Crop })
        {
            var doc = new EditorDocument(Image());
            canvas.SetTool(tool);
            canvas.Document = doc;
            window.UpdateLayout();
            Int32Rect? picked = new Int32Rect(1, 1, 1, 1);
            canvas.BeginRegionPick(area => picked = area);
            Mouse(canvas, UIElement.MouseLeftButtonDownEvent);
            if (!canvas.CancelGesture() || picked != null) throw new InvalidOperationException($"{tool}: Escape did not cancel the pick.");
            Mouse(canvas, UIElement.MouseMoveEvent);
            Mouse(canvas, UIElement.MouseLeftButtonUpEvent);
            if (doc.Dirty || doc.PixelWidth != 200 || doc.PixelHeight != 150 || !doc.State.Items.IsEmpty)
                throw new InvalidOperationException($"{tool}: the cancelled pick finished as a {tool} ({doc.State.Items.Count} marks, {doc.PixelWidth} x {doc.PixelHeight}).");
            if (canvas.IsMouseCaptured) throw new InvalidOperationException($"{tool}: the cancelled pick kept the mouse captured.");
        }
        return Task.CompletedTask;
    }

    private static void CheckStrokeNoOp(Window window, EditorCanvas canvas)
    {
        var doc = new EditorDocument(Image());
        canvas.SetTool(ToolKind.Select);
        canvas.Document = doc;
        window.UpdateLayout();
        var stroke = new StrokeAnnotation(ImmutableArray.Create(new Point(10, 10), new Point(12, 12)), Colors.Red, 3, false);
        doc.Add(stroke);
        canvas.Select(stroke);
        canvas.ResizeSelected(new Vector(-5, -5));
        doc.Undo();   // undoes the shrink if it made a step, otherwise the Add
        if (!doc.State.Items.IsEmpty)
            throw new InvalidOperationException("Shrinking a 2 px stroke that could not shrink added an undo step.");
        var copy = new StrokeAnnotation(ImmutableArray.Create(new Point(10, 10), new Point(12, 12)), Colors.Red, 3, false);
        _ = copy.Geometry;   // a cached outline is not part of the mark
        if (!Equals(stroke, copy) || stroke.GetHashCode() != copy.GetHashCode())
            throw new InvalidOperationException("Strokes with the same points do not compare equal.");
        if (Equals(stroke, copy with { Points = ImmutableArray.Create(new Point(10, 10), new Point(13, 12)) }))
            throw new InvalidOperationException("Strokes with different points compare equal.");
    }

    private static void CheckMarkSizes()
    {
        foreach (var mark in new[]
        {
            "{\"t\":\"text\",\"x\":1,\"y\":1,\"s\":\"A\",\"c\":\"#FFFF0000\",\"w\":1e7}",
            "{\"t\":\"callout\",\"tx\":1,\"ty\":1,\"x\":5,\"y\":5,\"s\":\"A\",\"c\":\"#FFFF0000\",\"w\":1e7}",
            "{\"t\":\"step\",\"x\":1,\"y\":1,\"n\":1,\"c\":\"#FFFF0000\",\"w\":1e7}",
        })
            if (MarksFormat.Read("{\"v\":1,\"marks\":[" + mark + "]}") != null)
                throw new InvalidOperationException("Read accepted " + mark);
        var ordinary = MarksFormat.Read("{\"v\":1,\"marks\":[{\"t\":\"text\",\"x\":1,\"y\":1,\"s\":\"A\",\"c\":\"#FFFF0000\",\"w\":400}]}");
        if (ordinary is not { Count: 1 }) throw new InvalidOperationException("Read rejected an ordinary 400 px text mark.");
    }

    private static void CheckPeerFollows(Window window, EditorCanvas canvas)
    {
        var doc = new EditorDocument(Image());
        canvas.SetTool(ToolKind.Select);
        canvas.Document = doc;
        window.UpdateLayout();
        doc.Add(new ShapeAnnotation(ShapeKind.Rectangle, new Point(20, 20), new Point(60, 50), Colors.Red, 3));
        var canvasPeer = (EditorCanvasAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(canvas);
        var peer = canvasPeer.GetChildren().OfType<AnnotationAutomationPeer>().Single();
        var at = ((AutomationPeer)peer).GetBoundingRectangle();
        ((ITransformProvider)peer).Move(at.X + 10, at.Y + 10);
        if (doc.State.Items.Single().Bounds.X <= 18.6) throw new InvalidOperationException("The automation move did not move the mark.");
        canvasPeer.ResetChildrenCache();
        var after = canvasPeer.GetChildren().OfType<AnnotationAutomationPeer>().Single();
        if (!ReferenceEquals(after, peer)) throw new InvalidOperationException("The moved mark got a new automation element.");
    }
}
