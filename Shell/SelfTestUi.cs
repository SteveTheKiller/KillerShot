using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

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
        int i = Array.FindIndex(args, a => a.Equals("--selftest-ui", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest-ui");
        Directory.CreateDirectory(dir);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        App.InitializeKit();
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAsync(string dir)
    {
        var log = new StringBuilder();
        int code = 0;
        try
        {
            var pattern = TestPattern(960, 600);
            CheckModel(pattern, log);

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
            w.TestSetLabels(true); await Shot("toolbar-labels");
            w.TestSetLabels(false);

            foreach (var t in new[] { Theme.Dark, Theme.Light, Theme.SE98, Theme.Blood, Theme.Delirium, Theme.Malaise })
            {
                ThemeManager.Apply(t);
                await Shot("theme-" + t);
            }
            ThemeManager.Apply(Theme.Black);

            foreach (var loc in new[] { Locale.De, Locale.Ja, Locale.RuRU })
            {
                LocaleManager.Apply(loc);
                w.TestRelocalize();
                w.TestShowCaptureSettings();
                await Shot("locale-" + loc);
                w.TestCloseOverlays();
            }
            LocaleManager.Apply(Locale.EnUS);
            w.TestRelocalize();

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
