using System.IO;
using System.Windows;
using KillerFamily.Layout;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// Landing page candidates: KillerShot.exe --selftest-gallery &lt;out folder&gt; &lt;screenshots folder&gt; [count].
/// Fills a scratch demo library from a real screenshots folder (read only: pictures are loaded and
/// added to the demo's own encrypted library, the folder itself is never written), one collection
/// per week, and shows the same folder as the Screenshots collection and the Files tab. Then it
/// renders every library view, the three Files layouts, the editor and the settings cards, each in
/// three different themes and accents, with the sidebar on both sides and the photo carousel on
/// top and bottom, so the page's shots can be chosen from a real spread instead of one fixed set.
/// </summary>
internal static class SelfTestGallery
{
    public static bool TryRun(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--selftest-gallery", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= args.Length) return false;
        string dir = Path.GetFullPath(args[i + 1]);
        string source = Path.GetFullPath(args[i + 2]);
        int count = i + 3 < args.Length && int.TryParse(args[i + 3], out int n) ? n : 240;
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        DemoMode.TryEnter(["--demo"], Path.Combine(dir, "state"));
        // The Screenshots collection and the Files tab show a screened copy, never the source
        // folder, so a picture the filter drops cannot reach the page through them either. It
        // lives in the run's own folder and is removed when the run ends.
        Shown = Path.Combine(dir, "Screenshots");
        if (Directory.Exists(Shown)) throw new InvalidOperationException(Shown + " already exists; use a new gallery folder.");
        Directory.CreateDirectory(Shown);
        FolderCaptures.UseFolder(Shown);
        App.InitializeKit();
        _ = RunAsync(dir, source, count);
        return true;
    }

    private static string Shown = "";

    private record Scene(string Name, bool Editor, Func<MainWindow, LibraryView, Task> Show);

    private static async Task RunAsync(string dir, string source, int count)
    {
        var log = new System.Text.StringBuilder();
        int code = 0;
        try
        {
            await Task.Run(() => { DemoMode.BeginSeed(); return CaptureLibrary.Opening; });
            await Task.Run(() => Seed(source, count, log));
            log.AppendLine($"count={CaptureLibrary.Count} collections={string.Join(",", CaptureLibrary.Collections().Select(c => c.Name + ":" + c.Count))}");
            if (CaptureLibrary.Count == 0) throw new InvalidOperationException("No pictures were added from " + source);
            CaptureSettings.OutputFolder = Shown;
            App.Instance.SetDocument(null);
            var w = new MainWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1600, Height = 1000 };
            w.Show();
            var view = w.TestLibrary;
            var items = CaptureLibrary.Items().ToList();
            // The editor opens big captures, so the picture fills the canvas instead of floating in it.
            var big = items.Where(c => c.Width >= 1400 && c.Height >= 800).OrderByDescending(c => (long)c.Width * c.Height).ToList();
            if (big.Count == 0) big = items;

            var scenes = new List<Scene>
            {
                new("shelves-collection", false, (_, v) => { v.TestSet("shelves", "collection", ""); return Task.CompletedTask; }),
                new("shelves-date", false, (_, v) => { v.TestSet("shelves", "date", ""); return Task.CompletedTask; }),
                new("grid-collection", false, (_, v) => { v.TestSet("grid", "collection", ""); return Task.CompletedTask; }),
                new("grid-date", false, (_, v) => { v.TestSet("grid", "date", ""); return Task.CompletedTask; }),
                new("timeline", false, (_, v) => { v.TestSet("timeline", "date", ""); return Task.CompletedTask; }),
                new("files-icons", false, (_, v) => FilesMode(v, 1, 160)),
                new("files-details", false, (_, v) => FilesMode(v, 2, 0)),
                new("files-list", false, (_, v) => FilesMode(v, 0, 0)),
                new("editor", true, (win, _) => { win.TestOpenLibraryCapture(big[(scenesShown * 5) % big.Count]); return Task.CompletedTask; }),
                new("capture-settings", false, (win, v) => { v.TestSet("grid", "date", ""); win.TestShowCaptureSettings(); return Task.CompletedTask; }),
                new("video-settings", false, (win, v) => { v.TestSet("shelves", "collection", ""); win.TestShowVideoSettings(); return Task.CompletedTask; }),
                new("shortcuts", false, (win, v) => { v.TestSet("grid", "collection", ""); win.TestShowShortcuts(true); return Task.CompletedTask; }),
            };
            var themes = Enum.GetValues<Theme>();
            var accents = Enum.GetValues<Accent>();
            string[] previews = ["pile", "stack", "grid", "mini"];
            int shot = 0;
            for (int s = 0; s < scenes.Count; s++)
            {
                for (int k = 0; k < 3; k++, shot++)
                {
                    scenesShown = shot;
                    var scene = scenes[s];
                    var theme = themes[(s * 3 + k) % themes.Length];
                    var accent = accents[(s * 3 + k * 3 + 1) % accents.Length];
                    var sidebar = (s + k) % 2 == 0 ? DockSide.Left : DockSide.Right;
                    var carousel = k % 2 == 0 ? DockSide.Top : DockSide.Bottom;
                    string preview = previews[(s + k) % previews.Length];

                    w.TestCloseOverlays();
                    bool inEditor = !w.TestLibrary.IsVisible;
                    if (scene.Editor == false && inEditor) w.TestToggleLibrary();
                    if (!scene.Editor && view.FilesActive && !scene.Name.StartsWith("files")) view.SetView("grid");
                    ThemeManager.ApplyAccent(theme, accent);
                    ThemeManager.Apply(theme);
                    w.NavigationDock.DockTo(sidebar);
                    w.StripDock.DockTo(carousel);
                    foreach (var choice in w.TestNavigationStrip!.PreviewMenu().Items.OfType<System.Windows.Controls.MenuItem>())
                        if ((string)choice.Tag == preview)
                            choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    await scene.Show(w, view);
                    string name = $"{shot + 1:00}-{scene.Name}-{theme}-{accent}-{sidebar}-{carousel}".ToLowerInvariant();
                    await Shot(w, Path.Combine(dir, name + ".png"));
                    log.AppendLine("shot " + name);
                }
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
        try { if (Shown.Length > 0) Directory.Delete(Shown, true); } catch { }
        Application.Current.Shutdown(code);
    }

    private static int scenesShown;

    private static readonly System.Text.RegularExpressions.Regex Private = new(
        @"balance|withdraw|deposit|statement|account|routing|checking|savings|\bapt\b|\b[A-Z]{2}\s+\d{5}(-\d{4})?\b|@[a-z0-9-]+\.[a-z]{2,}|password|claude|anthropic|opus|sonnet|ran (a|\d+) command|edited \S+|thought for|\bssn\b|invoice|payment|salary|summary|withdrawal|\bI (moved|fixed|added|found|checked|ran|changed|removed|updated)\b|\bgoals?\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task FilesMode(LibraryView view, int mode, int tile)
    {
        view.SetView("files");
        for (int i = 0; i < 40 && !view.FilesActive; i++) await Task.Delay(100);
        view.Files.SetView(mode);
        if (tile > 0) view.Files.ViewState.TileSize = tile;
    }

    private static async Task Shot(MainWindow w, string path)
    {
        // Thumbnails load in the background; give them time to land before the render.
        await Task.Delay(3000);
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(root);
        ImageOutput.SavePng(rtb, path);
    }

    /// <summary>Two thirds of the count are the newest pictures in a row, so the date views fill
    /// every day's row; the last third is spaced over the older ones so the weeks spread out.
    /// One collection per week. Dates come from the files, so the timeline shows real days.</summary>
    private static void Seed(string source, int count, System.Text.StringBuilder log)
    {
        var files = new DirectoryInfo(source).EnumerateFiles()
            .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime).ToList();
        int dense = Math.Min(files.Count, count * 2 / 3);
        int step = Math.Max(1, (files.Count - dense) / Math.Max(1, count - dense));
        var picks = files.Take(dense).Concat(files.Skip(dense).Where((_, i) => i % step == 0)).Take(count).ToList();
        var collections = new Dictionary<DateTime, long>();
        var recognize = TextRecognition.CreateRecognizer();
        if (recognize == null) throw new InvalidOperationException("Windows text recognition is unavailable, so private pictures cannot be screened out.");
        int added = 0;
        foreach (var f in picks)
        {
            try
            {
                var bmp = ImageOutput.Load(f.FullName);
                // A QR code can carry an invite or pairing token, which never belongs on a public page.
                if (QrImages.Read(bmp).Count > 0) { log.AppendLine("skip qr " + f.Name); continue; }
                // Bank statements opened in a PDF viewer, addresses, mail and AI chat transcripts
                // stay off the page too: Windows reads the picture's text and any of these drops it.
                if (recognize != null)
                {
                    // Small print is where statements hide, so the text is read at twice the size
                    // (as far as the recognizer's size limit allows).
                    double scale = Math.Min(2.0, 9000.0 / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
                    System.Windows.Media.Imaging.BitmapSource read = scale > 1.05
                        ? new System.Windows.Media.Imaging.TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(scale, scale))
                        : bmp;
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(read));
                    using var png = new MemoryStream();
                    encoder.Save(png);
                    string text = recognize(png.ToArray()).GetAwaiter().GetResult() ?? "";
                    var hit = Private.Match(text);
                    if (hit.Success) { log.AppendLine($"skip text '{hit.Value}' {f.Name}"); continue; }
                }
                long id = CaptureLibrary.AddNow(bmp, f.LastWriteTime, "window", null, null);
                var week = f.LastWriteTime.Date.AddDays(-(int)f.LastWriteTime.DayOfWeek);
                if (!collections.TryGetValue(week, out long cid))
                    collections[week] = cid = CaptureLibrary.CreateCollection("Week of " + week.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture));
                CaptureLibrary.Move([id], cid);
                string copy = Path.Combine(Shown, f.Name);
                File.Copy(f.FullName, copy);
                File.SetLastWriteTime(copy, f.LastWriteTime);
                File.SetCreationTime(copy, f.CreationTime);
                added++;
            }
            catch (Exception ex) { log.AppendLine("skip " + f.Name + ": " + ex.Message); }
        }
        log.AppendLine($"seeded {added} of {files.Count}, step {step}");
    }
}
