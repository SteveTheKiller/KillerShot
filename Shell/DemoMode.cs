using System.Globalization;
using System.IO;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// Screenshot and demo mode: KillerShot.exe --demo. The library is a scratch one, rebuilt on
/// every demo launch in %LOCALAPPDATA%\KillerShot\Demo and filled from code\Demo\KillerShot, the
/// folder beside the repos that holds real screenshots of the Killer Tools apps and a demo.txt
/// listing their collections, programs, titles, ages and notes. Its Screenshots folder stands in
/// for Pictures\Screenshots. Settings live in the demo folder too, so the real library, the real
/// Screenshots folder and the user's settings are never opened. No tray icon, no global
/// shortcuts and no single-instance claim, so it runs beside a working KillerShot; closing the
/// window quits. Only reachable through the launch flag, the KillerNotes and Killendar pattern.
/// A missing demo folder is not an error: the library simply starts empty.
/// </summary>
internal static class DemoMode
{
    public static bool IsDemo { get; private set; }

    private static string? _source;

    /// <summary>Points settings, the library and the Screenshots folder at the demo copies.
    /// The self-test passes its own folder so it can run while a demo window is open.</summary>
    public static bool TryEnter(string[] args, string folder = "Demo")
    {
        if (!args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("/demo", StringComparison.OrdinalIgnoreCase)))
            return false;
        IsDemo = true;
        string root = Path.IsPathFullyQualified(folder) ? Path.GetFullPath(folder)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", folder);
        string library = Path.Combine(root, "Library");
        try { if (Directory.Exists(library)) Directory.Delete(library, true); } catch { /* a locked old copy is reused */ }
        Settings.UseFolder(Path.Combine(root, "Settings"));
        Settings.Set("ActiveAlbum", "Library.kshot");
        CaptureLibrary.UseFolder(library);
        _source = SourceFolder();
        string shots = _source != null ? Path.Combine(_source, "Screenshots") : Path.Combine(root, "Screenshots");
        Directory.CreateDirectory(shots);
        FolderCaptures.UseFolder(shots);
        // A capture taken in the demo saves into the demo's Screenshots folder, not the real one.
        Settings.Set("OutputFolder", Path.Combine(root, "Output"));
        return true;
    }

    /// <summary>Opens the scratch library and fills it from demo.txt, off the interface thread.</summary>
    public static void BeginSeed() => CaptureLibrary.BeginOpen(Seed);

    /// <summary>code\Demo\KillerShot, found by walking up from the running exe.</summary>
    private static string? SourceFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 7 && dir != null; up++, dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "Demo", "KillerShot"), Path.Combine(dir.FullName, "..", "Demo", "KillerShot") })
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static void Seed()
    {
        if (_source == null) return;
        string list = Path.Combine(_source, "Library", "demo.txt");
        if (!File.Exists(list)) return;
        var collections = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        foreach (var raw in File.ReadAllLines(list))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var f = raw.Split('\t');
            if (f.Length < 5) continue;
            string path = Path.Combine(_source, "Library", f[0]);
            if (!File.Exists(path)) continue;
            try
            {
                var bmp = ImageOutput.Load(path);
                double days = double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
                long id = CaptureLibrary.AddNow(bmp, now.AddDays(-days), "window", f[3], f[2]);
                if (f[1].Length > 0)
                {
                    if (!collections.TryGetValue(f[1], out long cid)) collections[f[1]] = cid = CaptureLibrary.CreateCollection(f[1]);
                    CaptureLibrary.Move([id], cid);
                }
                if (f.Length > 5 && f[5].Length > 0) CaptureLibrary.SetNote(id, f[5]);
            }
            catch { /* an unreadable picture is skipped */ }
        }
    }
}

/// <summary>Demo check: KillerShot.exe --selftest-demo &lt;folder&gt;. Builds the demo library the
/// way --demo does, then renders the library views and the collection strip off screen into the
/// folder, so the demo can be looked at without a window on the user's screen.</summary>
internal static class SelfTestDemo
{
    /// <summary>--landing: only the landing page set, at the page's 1600 by 1000 size.</summary>
    private static bool _landing;

    public static bool TryRun(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--selftest-demo", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest-demo");
        _landing = args.Any(a => a.Equals("--landing", StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        DemoMode.TryEnter(["--demo"], Path.Combine(Path.GetFullPath(dir), "state"));
        App.InitializeKit();
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAsync(string dir)
    {
        var log = new System.Text.StringBuilder();
        int code = 0;
        try
        {
            bool open = await Task.Run(() => { DemoMode.BeginSeed(); return CaptureLibrary.Opening; });
            log.AppendLine($"open={open} count={CaptureLibrary.Count} collections={string.Join(",", CaptureLibrary.Collections().Select(c => c.Name + ":" + c.Count))} folder={FolderCaptures.Items().Count}");
            if (CaptureLibrary.Count == 0) throw new InvalidOperationException("The demo library is empty; code\\Demo\\KillerShot was not found.");
            // A collection color, so the shots show how one marks its cards. The demo has its own
            // settings folder, so this never touches the user's colors.
            if (CaptureLibrary.Collections().FirstOrDefault(c => c.Name == "KillerScan") is { } scan)
                CardColors.SetCollection(scan.Id, System.Windows.Media.Color.FromRgb(0xE8, 0x96, 0x2C));
            App.Instance.SetDocument(null);
            var w = new MainWindow { ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1440, Height = 900 };
            w.Show();
            async Task Shot(string name)
            {
                await Task.Delay(1200);
                w.UpdateLayout();
                var root = (System.Windows.FrameworkElement)w.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(root);
                ImageOutput.SavePng(rtb, Path.Combine(dir, name + ".png"));
                log.AppendLine("shot " + name);
            }
            var view = w.TestLibrary;
            if (_landing)
            {
                // The landing page set: each shot its own theme and accent, so the page shows the
                // family palettes as well as the views, docking sides and the settings card.
                w.Width = 1600; w.Height = 1000;
                // The settings card shows the output folder; a generic one keeps the machine's user name off the page.
                CaptureSettings.OutputFolder = @"C:\Users\You\Pictures\Screenshots";
                void Look(Theme theme, Accent accent)
                {
                    ThemeManager.ApplyAccent(theme, accent);
                    ThemeManager.Apply(theme);
                }
                void Dock(KillerFamily.Layout.DockSide sidebar, KillerFamily.Layout.DockSide strip)
                {
                    w.TestNavigationDock.DockTo(sidebar);
                    w.TestStripDock.DockTo(strip);
                }
                void Preview(string style)
                {
                    foreach (var choice in w.TestStrip!.PreviewMenu().Items.OfType<System.Windows.Controls.MenuItem>())
                        if ((string)choice.Tag == style)
                            choice.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                }
                var keepAlive = CaptureLibrary.Items().First(c => c.Window?.Contains("Keep Alive") == true);
                var pdf = CaptureLibrary.Items().FirstOrDefault(c => c.Process == "KillerPDF") ?? keepAlive;

                Look(Theme.Dark, Accent.Teal);
                w.Width = 1600; w.Height = 1000;
                Dock(KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Top);
                Preview("pile");
                view.TestSet("shelves", "collection", "");
                await Shot("landing-01-shelves-dark");

                Look(Theme.Blood, Accent.Red);
                w.Width = 1400; w.Height = 900;
                Dock(KillerFamily.Layout.DockSide.Right, KillerFamily.Layout.DockSide.Top);
                Preview("stack");
                w.TestStrip!.TestScrollTo(0.35);
                view.TestSet("flow", "date", "");
                await Shot("landing-02-carousel-blood");

                Look(Theme.Light, Accent.Blue);
                w.Width = 1280; w.Height = 960;
                Dock(KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Top);
                Preview("grid");
                w.TestStrip!.TestScrollTo(0.7);
                view.TestSet("grid", "date", "");
                await Shot("landing-03-grid-light");

                Look(Theme.Black, Accent.Purple);
                w.Width = 1500; w.Height = 860;
                Dock(KillerFamily.Layout.DockSide.Right, KillerFamily.Layout.DockSide.Top);
                Preview("mini");
                w.TestStrip!.TestScrollTo(1.0);
                view.TestSet("timeline", "date", "");
                await Shot("landing-04-timeline-black");

                Look(Theme.Cyanotic, Accent.Blue);
                w.Width = 1200; w.Height = 900;
                Dock(KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Top);
                Preview("stack");
                w.TestStrip!.TestScrollTo(0.5);
                view.TestSet("grid", "date", "theme");
                await Shot("landing-05-search-cyanotic");

                Look(Theme.Dark, Accent.Orange);
                w.Width = 1600; w.Height = 1000;
                w.TestOpenLibraryCapture(keepAlive);
                Dock(KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Top);
                await Shot("landing-06-editor-strip-top");

                Look(Theme.Ectoplasm, Accent.Green);
                w.Width = 1360; w.Height = 1000;
                w.TestOpenLibraryCapture(pdf);
                Dock(KillerFamily.Layout.DockSide.Right, KillerFamily.Layout.DockSide.Bottom);
                await Shot("landing-07-editor-right-bottom");

                Look(Theme.Delirium, Accent.Magenta);
                w.Width = 1440; w.Height = 900;
                Dock(KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Top);
                w.TestShowCaptureSettings();
                await Shot("landing-08-capture-settings");
                w.TestCloseOverlays();

                Look(Theme.SE98, Accent.Teal);
                w.Width = 1024; w.Height = 768;
                Preview("grid");
                w.TestStrip!.TestScrollTo(0.2);
                w.TestToggleLibrary();
                view.TestSet("shelves", "date", "");
                await Shot("landing-09-shelves-98se");

                w.Close();
                log.AppendLine("result=ok");
                File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
                System.Windows.Application.Current.Shutdown(0);
                return;
            }
            foreach (var (v, g) in new[] { ("shelves", "collection"), ("flow", "date"), ("grid", "collection"), ("timeline", "date") })
            {
                view.TestSet(v, g, "");
                await Shot($"demo-{v}-{g}");
            }
            view.TestSet("flow", "date", "");
            foreach (var choice in w.TestStrip!.PreviewMenu().Items.OfType<System.Windows.Controls.MenuItem>())
            {
                choice.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                await Shot("demo-collection-" + choice.Tag);
            }
            view.TestSet("grid", "date", "theme");
            await Shot("demo-search");
            var item = CaptureLibrary.Items().First(c => c.Window?.Contains("Keep Alive") == true);
            w.TestOpenLibraryCapture(item);
            await Shot("demo-strip-top");
            // Each icon size with each text mode, as Ctrl+Shift+1 to 6 set them.
            foreach (int size in new[] { 1, 2 })
                foreach (int text in new[] { 3, 4, 5, 6 })
                {
                    w.TestToolbarStyle(size);
                    w.TestToolbarStyle(text);
                    await Shot($"demo-toolbar-{size}-{text}");
                }
            w.TestToolbarStyle(2);
            w.TestToolbarStyle(3);
            // Buttons on both sides of the bar and two in the More menu, then Customize toolbar.
            w.TestToolbarLayout(new MainWindow.ToolbarLayout(
                ["capture", "record", "library", "-", "open", "save", "saveas", "copy", "-", "undo", "redo",
                 "-", "select", "pen", "highlighter", "arrow", "rectangle", "ellipse", "text", "redact", "crop"],
                ["color", "width", "-", "fit", "actualsize"],
                ["recent", "print"]));
            await Shot("demo-toolbar-sides");
            var customize = w.TestCustomizeToolbarDialog();
            await Task.Delay(1200);
            customize.UpdateLayout();
            if (customize.Content is System.Windows.FrameworkElement croot)
            {
                var cdpi = System.Windows.Media.VisualTreeHelper.GetDpi(customize);
                var crtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(croot.ActualWidth * cdpi.DpiScaleX), (int)Math.Ceiling(croot.ActualHeight * cdpi.DpiScaleY),
                    96 * cdpi.DpiScaleX, 96 * cdpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                crtb.Render(croot);
                ImageOutput.SavePng(crtb, Path.Combine(dir, "demo-customize-toolbar.png"));
                log.AppendLine("shot demo-customize-toolbar");
            }
            customize.Close();
            w.TestToolbarLayout(null);
            foreach (var sidebar in new[] { KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Right })
            foreach (var carousel in new[] { KillerFamily.Layout.DockSide.Top, KillerFamily.Layout.DockSide.Bottom })
            {
                w.TestNavigationDock.DockTo(sidebar);
                w.TestStripDock.DockTo(carousel);
                await Shot($"demo-editor-{sidebar}-{carousel}");
            }
            w.Close();
            log.AppendLine("result=ok");
        }
        catch (Exception ex)
        {
            log.AppendLine($"result=error {ex}");
            code = 1;
        }
        try { File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString()); } catch { }
        System.Windows.Application.Current.Shutdown(code);
    }
}
