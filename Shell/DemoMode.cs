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
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", folder);
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
        if (string.IsNullOrEmpty(Settings.Get("OutputFolder"))) Settings.Set("OutputFolder", shots);
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
    public static bool TryRun(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--selftest-demo", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest-demo");
        Directory.CreateDirectory(dir);
        DemoMode.TryEnter(["--demo"], "DemoSelfTest");
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
            foreach (var (v, g) in new[] { ("shelves", "collection"), ("flow", "date"), ("grid", "collection"), ("timeline", "date") })
            {
                view.TestSet(v, g, "");
                await Shot($"demo-{v}-{g}");
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
            w.TestStripDock.DockTo(KillerFamily.Layout.DockSide.Left);
            w.TestStrip?.SetSideView("list");
            await Shot("demo-strip-left");
            w.TestStrip?.SetSideView("rolodex");
            await Shot("demo-strip-rolodex");
            w.TestStrip?.TestRolodexZoom(Controls.Rolodex.MaxZoom);
            await Shot("demo-strip-rolodex-zoomed-in");
            w.TestStrip?.TestRolodexZoom(0.5);
            await Shot("demo-strip-rolodex-zoomed-out");
            w.TestStrip?.TestRolodexZoom(0.85);
            w.TestStrip?.SetSideView("list");
            w.TestStripDock.DockTo(KillerFamily.Layout.DockSide.Top);
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
