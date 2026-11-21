using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// --selftest-theme-motif &lt;folder&gt; lifetime|perf. Run it on a hidden desktop: both scenarios open a window.
/// lifetime: motif elements that load, unload, reload and drop to None are collected, and a reloaded element still
/// repaints on a theme change. perf: a 3840x2160 Trash Polka window, the UI-thread time of each accent change and the
/// working set after cycling every accent. Reads and writes no saved setting.
/// </summary>
internal static class SelfTestThemeMotif
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-theme-motif") return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        if (Environment.GetEnvironmentVariable("KS_MOTIF_SW") == "1") RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // The motif disk cache never touches the real LocalAppData here: KS_MOTIF_CACHE names a temp folder, and unset
        // or "off" turns the cache off.
        string? cache = Environment.GetEnvironmentVariable("KS_MOTIF_CACHE");
        MotifDiskCache.Root = string.IsNullOrEmpty(cache) || cache == "off" ? null : Path.GetFullPath(cache);
        ThemeManager.GetSetting = _ => null;
        ThemeManager.SetSetting = (_, _) => { };
        _ = RunAsync(dir, args[2]);
        return true;
    }

    private static async Task RunAsync(string dir, string scenario)
    {
        var log = new StringBuilder();
        int exit = 0;
        try
        {
            ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Red);
            ThemeManager.Apply(scenario is "prewarm" or "noprewarm" ? Theme.Dark : Theme.TrashPolka);
            if (scenario == "lifetime") await Lifetime(log);
            else if (scenario == "perf") await Perf(log, dir);
            else if (scenario is "prewarm" or "noprewarm") await Startup(log, scenario == "prewarm");
            else throw new ArgumentException("Unknown theme motif scenario.");
            log.AppendLine("result=ok");
        }
        catch (Exception error) { log.AppendLine($"result=error {error}"); exit = 1; }
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Application.Current.Shutdown(exit);
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static Window HostWindow(double width, double height, out Grid root)
    {
        root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "ContentPaneBrush");
        ThemeMotif.SetBaseBrush(root, "ContentPaneBrush");
        ThemeMotif.SetPart(root, MotifPart.Window);
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Left = 0, Top = 0, Width = width, Height = height, Content = root,
        };
        window.Show();
        return window;
    }

    // Two composes that never finish hold the crossfade for one bound in all, not one bound each.
    private static void HungComposes(StringBuilder log)
    {
        var hung = new[] { new TaskCompletionSource().Task, new TaskCompletionSource().Task };
        var timer = Stopwatch.StartNew();
        var finished = ThemeMotif.WaitWithin(hung, ThemeMotif.ComposeWaitMs);
        double ms = timer.Elapsed.TotalMilliseconds;
        log.AppendLine(FormattableString.Invariant($"hungComposes waitedMs={ms:0} boundMs={ThemeMotif.ComposeWaitMs}"));
        if (finished.Any(f => f)) throw new InvalidOperationException("A compose that never finishes was reported finished.");
        if (ms > ThemeMotif.ComposeWaitMs * 1.5)
            throw new InvalidOperationException(FormattableString.Invariant($"Two hung composes held the crossfade {ms:0} ms, past the {ThemeMotif.ComposeWaitMs} ms bound."));
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------

    private static async Task Lifetime(StringBuilder log)
    {
        HungComposes(log);
        var window = HostWindow(800, 600, out var root);
        // Wait for the bitmap layers: elements painted before them carry the vector piece.
        await WaitForComposes();
        var refs = new List<WeakReference>();
        // Every element is touched only through non-async helpers, so no state machine field keeps one alive.
        for (int i = 0; i < 50; i++)
        {
            refs.Add(AddPane(root, MotifPart.Chrome));
            await Idle();
            Hold(root);
            await Idle();
            Return(root);
            await Idle();
            Drop(root);
            await Idle();
        }
        // Popups float free: their parts draw from their own corner.
        for (int i = 0; i < 10; i++)
        {
            refs.Add(AddPane(root, MotifPart.Popup));
            await Idle();
            Drop(root);
            await Idle();
        }
        // Set to None while loaded, then dropped.
        for (int i = 0; i < 10; i++)
        {
            refs.Add(AddPane(root, MotifPart.Chrome));
            await Idle();
            ClearPart(root);
            Drop(root);
            await Idle();
        }
        // Set to None while loaded, then dropped before Unloaded can run.
        for (int i = 0; i < 10; i++)
        {
            refs.Add(AddPane(root, MotifPart.Chrome));
            await Idle();
            ClearPart(root);
            Drop(root);
        }
        await Idle();
        int alive = Collect(refs);
        log.AppendLine($"elements={refs.Count} alive_after_gc={alive} raster_paints={ThemeMotif.RasterPaints} vector_paints={ThemeMotif.VectorPaints}");

        // A reloaded element still follows the theme.
        var reloaded = new Border();
        reloaded.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        ThemeMotif.SetBaseBrush(reloaded, "BackgroundBrush");
        ThemeMotif.SetPart(reloaded, MotifPart.Chrome);
        root.Children.Add(reloaded);
        await Idle();
        root.Children.Remove(reloaded);
        await Idle();
        root.Children.Add(reloaded);
        await Idle();
        var painted = reloaded.Background;
        if (painted is not DrawingBrush) throw new InvalidOperationException("The reloaded element shows no motif.");
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Teal);
        await WaitForComposes();
        bool repainted = !ReferenceEquals(painted, reloaded.Background) && reloaded.Background is DrawingBrush;
        log.AppendLine($"reloaded_repaints_on_theme_change={repainted}");
        window.Close();
        if (alive != 0) throw new InvalidOperationException($"{alive} of {refs.Count} motif elements were not collected.");
        if (!repainted) throw new InvalidOperationException("A reloaded element kept the old theme's ink.");
    }

    private static UIElement? _held;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Hold(Grid root) { _held = root.Children[^1]; root.Children.Remove(_held); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Return(Grid root) { root.Children.Add(_held!); _held = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drop(Grid root) => root.Children.RemoveAt(root.Children.Count - 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearPart(Grid root) => ThemeMotif.SetPart((FrameworkElement)root.Children[^1], MotifPart.None);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddPane(Grid root, MotifPart part)
    {
        var pane = new Border { Margin = new Thickness(20, 40, 20, 20) };
        pane.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        ThemeMotif.SetBaseBrush(pane, "BackgroundBrush");
        ThemeMotif.SetPart(pane, part);
        root.Children.Add(pane);
        return new WeakReference(pane);
    }

    private static int Collect(List<WeakReference> refs)
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return refs.Count(r => r.IsAlive);
    }

    // ---- startup prewarm -----------------------------------------------------------------------------------------

    // A Dark 4K window as the app's first window: time from process start to the first idle, the longest UI-thread gap
    // a 16 ms heartbeat sees while the prewarm runs (or the same span without it), Dark memory 30 s after, then the
    // first Trash Polka load.
    private static async Task Startup(StringBuilder log, bool prewarm)
    {
        ThemeMotif.InkScreenOverride = new Size(3840, 2160);
        var window = HostWindow(3840, 2160, out var root);
        await Idle();
        using (var self = Process.GetCurrentProcess())
            log.AppendLine($"startup_to_idle_ms={(DateTime.Now - self.StartTime).TotalMilliseconds:F0} prewarm={prewarm} cache={MotifDiskCache.Root ?? "off"}");
        var beat = Stopwatch.StartNew();
        double maxGap = 0, last = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => { double now = beat.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - last); last = now; };
        timer.Start();
        var span = Stopwatch.StartNew();
        if (prewarm)
        {
            if (MotifDiskCache.Root is null)
                throw new InvalidOperationException("prewarm needs KS_MOTIF_CACHE set to a temp folder: with the cache off nothing is prewarmed.");
            ThemeMotif.PrewarmWhenIdle(window);
            var queued = Stopwatch.StartNew();
            while (ThemeMotif.PrewarmTask is null && queued.Elapsed.TotalSeconds < 30) await Task.Delay(20);
            if (ThemeMotif.PrewarmTask is null) throw new InvalidOperationException("The prewarm was never queued.");
            await ThemeMotif.PrewarmTask;
        }
        else await Task.Delay(TimeSpan.FromSeconds(12));
        timer.Stop();
        log.AppendLine($"prewarm_span_ms={span.Elapsed.TotalMilliseconds:F0} max_ui_gap_ms={maxGap:F1} cache_files={(MotifDiskCache.Root is { } r && Directory.Exists(r) ? Directory.GetFiles(r, "*.png", SearchOption.AllDirectories).Length : 0)} layer_sets={TrashPolkaRaster.BuildCount}");
        await Task.Delay(TimeSpan.FromSeconds(30));
        log.AppendLine($"dark_after_30s ws_mb={WorkingSetMb():F0} private_mb={_privateMb:F0} managed_mb={_managedMb:F0}");
        var sw = Stopwatch.StartNew();
        ThemeManager.Apply(Theme.TrashPolka);
        await Settle();
        log.AppendLine($"first_trash_polka_load_ms={sw.Elapsed.TotalMilliseconds:F0} cache_hits={TrashPolkaRaster.CacheHits} cache_misses={TrashPolkaRaster.CacheMisses}");
        window.Close();
    }

    // ---- perf --------------------------------------------------------------------------------------------------

    private static async Task WaitForComposes()
    {
        var limit = Stopwatch.StartNew();
        await Idle();
        while ((!TrashPolkaRaster.BuildsSettled || ThemeMotif.ComposesPending > 0) && limit.Elapsed.TotalSeconds < 120)
        {
            await Task.Delay(10);
            await Idle();
        }
        await Idle();
    }

    private static async Task Settle()
    {
        await WaitForComposes();
        for (int i = 0; i < 5; i++) { await Task.Delay(100); await Idle(); }
    }

    private static async Task Perf(StringBuilder log, string dir)
    {
        ThemeMotif.InkScreenOverride = new Size(3840, 2160);
        // No warmup: the table starts under Dark before any ink exists.
        ThemeManager.Apply(Theme.Dark);
        var window = HostWindow(3840, 2160, out var root);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
        root.RowDefinitions.Add(new RowDefinition());
        var bar = Pane(root, MotifPart.Chrome, "BackgroundBrush");
        Grid.SetColumnSpan(bar, 3);
        var side = Pane(root, MotifPart.Chrome, "BackgroundBrush");
        Grid.SetRow(side, 1);
        var split = Pane(root, MotifPart.Split, "ContentPaneBrush");
        Grid.SetRow(split, 1);
        Grid.SetColumn(split, 2);
        await Idle();
        log.AppendLine($"render_tier={RenderCapability.Tier >> 16} mode={RenderOptions.ProcessRenderMode} window={root.ActualWidth}x{root.ActualHeight} dpi={VisualTreeHelper.GetDpi(root).DpiScaleX}");

        bool first = true;
        for (int pass = 0; pass < 3; pass++)
        {
            var order = pass % 2 == 0 ? new[] { Theme.Dark, Theme.TrashPolka } : new[] { Theme.TrashPolka, Theme.Dark };
            foreach (var theme in order)
            {
                long peak = 0;
                bool sampling = theme == Theme.TrashPolka && first;
                Thread? sampler = null;
                if (sampling)
                {
                    first = false;
                    sampler = new Thread(() =>
                    {
                        using var self = Process.GetCurrentProcess();
                        while (_sampling)
                        {
                            self.Refresh();
                            long now = self.PrivateMemorySize64;
                            if (now > Interlocked.Read(ref _peak)) Interlocked.Exchange(ref _peak, now);
                            Thread.Sleep(2);
                        }
                    }) { IsBackground = true };
                    _peak = 0;
                    _sampling = true;
                    sampler.Start();
                }
                var sw = Stopwatch.StartNew();
                ThemeManager.Apply(theme);
                await Settle();
                if (sampler is not null)
                {
                    log.AppendLine($"first_trash_polka_load_ms={sw.Elapsed.TotalMilliseconds:F0}");
                    _sampling = false;
                    sampler.Join();
                    peak = Interlocked.Read(ref _peak);
                    log.AppendLine($"peak_private_first_load_mb={peak / 1048576.0:F0}");
                }
                double ws = WorkingSetMb();
                log.AppendLine($"memory pass={pass + 1} theme={theme} ws_mb={ws:F0} private_mb={_privateMb:F0} managed_mb={_managedMb:F0} gc_committed_mb={_committedMb:F0}");
            }
        }

        // 4K shots of every part, two accents, for the pixel diff.
        ThemeManager.Apply(Theme.TrashPolka);
        foreach (var accent in new[] { Accent.Red, Accent.Teal })
        {
            ThemeManager.ApplyAccent(Theme.TrashPolka, accent);
            await Settle();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var file = File.Create(Path.Combine(dir, $"shot-{accent}.png"))) png.Save(file);
        }

        // The accent click: the picker's crossfade waits for the pieces before its incoming snapshot.
        var click = new List<double>();
        for (int pass = 0; pass < 3; pass++)
            foreach (Accent accent in Enum.GetValues<Accent>())
            {
                var sw = Stopwatch.StartNew();
                ThemeManager.ApplyAccent(Theme.TrashPolka, accent);
                ThemeMotif.FinishComposes();
                click.Add(sw.Elapsed.TotalMilliseconds);
                await WaitForComposes();
            }
        log.AppendLine($"accent_click_ui_ms median={Median(click):F1} max={click.Max():F1}");

        // The click with the crossfade's two software snapshots.
        var crossfade = new List<double>();
        for (int pass = 0; pass < 3; pass++)
            foreach (Accent accent in Enum.GetValues<Accent>())
            {
                var sw = Stopwatch.StartNew();
                var before = new System.Windows.Media.Imaging.RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32);
                before.Render(root);
                ThemeManager.ApplyAccent(Theme.TrashPolka, accent);
                ThemeMotif.FinishComposes();
                var after = new System.Windows.Media.Imaging.RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32);
                after.Render(root);
                crossfade.Add(sw.Elapsed.TotalMilliseconds);
                await WaitForComposes();
            }
        log.AppendLine($"accent_crossfade_ms median={Median(crossfade):F1} max={crossfade.Max():F1}");
        log.AppendLine($"ws_after_accents_mb={WorkingSetMb():F0} private_mb={_privateMb:F0} managed_mb={_managedMb:F0} releases={ThemeMotif.Releases} layer_sets={TrashPolkaRaster.BuildCount} cache_hits={TrashPolkaRaster.CacheHits} cache_misses={TrashPolkaRaster.CacheMisses} cache={MotifDiskCache.Root ?? "off"} raster_paints={ThemeMotif.RasterPaints} vector_paints={ThemeMotif.VectorPaints}");
        window.Close();
    }

    private static volatile bool _sampling;
    private static long _peak;

    private static Border Pane(Grid root, MotifPart part, string key)
    {
        var pane = new Border();
        pane.SetResourceReference(Border.BackgroundProperty, key);
        ThemeMotif.SetBaseBrush(pane, key);
        ThemeMotif.SetPart(pane, part);
        root.Children.Add(pane);
        return pane;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static double _privateMb, _managedMb, _committedMb;

    private static double WorkingSetMb()
    {
        for (int i = 0; i < 3; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        using var p = Process.GetCurrentProcess();
        _privateMb = p.PrivateMemorySize64 / 1048576.0;
        _managedMb = GC.GetTotalMemory(true) / 1048576.0;
        _committedMb = GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0;
        return p.WorkingSet64 / 1048576.0;
    }
}
