using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Editor states a declined prompt must leave intact.</summary>
internal static class SelfTestEditorLifetime
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-editor-lifetime") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new editor lifetime test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CanceledDiscardKeepsReview();
            report.Add("Cancel on the discard prompt keeps the review, its bar and the picture PASS");
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            DockLayoutDoesNotReadLibrary();
            report.Add("docking, minimizing and restoring the collection strip does not read the album again PASS");
            ColorPickerIsLocalized();
            report.Add("the color picker's Hex label and OK button come from the app language PASS");
            VideoMarkAndAlbumSizesAreLocalized();
            report.Add("the video mark's name and album sizes follow the app language PASS");
            KeyboardMapShowsSharedKeys();
            report.Add("a key with an editor and an album action shows both on the keyboard map PASS");
            AlbumManagerFollowsThemeAndEscape();
            report.Add("the album manager repaints with the theme and Escape closes an open drop-down first PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            App.Instance.PresentDocumentReplacement = _ => true;
            try { App.Instance.SetDocument(null); } catch { }
            App.Instance.PresentDocumentReplacement = null;
            File.WriteAllLines(Path.Combine(folder, "editor-lifetime-report.txt"), report);
            Application.Current.Shutdown(code);
        }
        return true;
    }

    private static void KeyboardMapShowsSharedKeys()
    {
        var editor = App.Instance.EditorWindow ?? throw new InvalidOperationException("No editor window.");
        string L(string key) => (string)Application.Current.FindResource(key);
        bool Shown(IReadOnlyList<string> texts, string key) => texts.Any(t => t.Contains(L(key), StringComparison.Ordinal));
        var ctrl = editor.TestMapTexts(ctrl: true);
        var plain = editor.TestMapTexts(ctrl: false);
        var missing = new[] { ("Str_Img_Margin", ctrl), ("Str_Lib_MoveTo", ctrl), ("Str_KS_EditText", plain), ("Str_Lib_Note", plain),
            ("Str_KS_Delete", plain), ("Str_KS_DeleteCapture", plain) }.Where(p => !Shown(p.Item2, p.Item1)).Select(p => p.Item1).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("The keyboard map dropped: " + string.Join(", ", missing));
    }

    private static void AlbumManagerFollowsThemeAndEscape()
    {
        var editor = App.Instance.EditorWindow ?? throw new InvalidOperationException("No editor window.");
        editor.TestUseModelessAlbums();
        editor.TestShowAlbums();
        var dialog = editor.TestAlbumsDialog ?? throw new InvalidOperationException("The album manager did not open.");
        try
        {
            var resources = Application.Current.Resources;
            object? original = resources["BackgroundBrush"];
            var replaced = new SolidColorBrush(Color.FromRgb(1, 2, 3));
            resources["BackgroundBrush"] = replaced;
            try
            {
                var root = (System.Windows.Controls.Border)dialog.FindName("RootBorder");
                if (!ReferenceEquals(root.Background, replaced))
                    throw new InvalidOperationException("The album manager kept the old surface after the theme brushes changed.");
            }
            finally { resources["BackgroundBrush"] = original; }

            System.Windows.Controls.ComboBox? combo = null;
            void Find(DependencyObject node)
            {
                if (combo == null && node is System.Windows.Controls.ComboBox { IsVisible: true, IsEnabled: true } c) combo = c;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Find(VisualTreeHelper.GetChild(node, i));
            }
            dialog.UpdateLayout();
            Find(dialog);
            if (combo == null) throw new InvalidOperationException("The album manager has no drop-down to check.");
            combo.Focus();
            combo.IsDropDownOpen = true;
            var source = PresentationSource.FromVisual(combo) ?? throw new InvalidOperationException("The drop-down is not on screen.");
            System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            if (editor.TestAlbumsDialog == null || !dialog.IsVisible)
                throw new InvalidOperationException("Escape on an open drop-down closed the album manager.");
            if (combo.IsDropDownOpen) throw new InvalidOperationException("Escape did not close the drop-down.");
        }
        finally { editor.TestCloseAlbums(); }
    }

    private static void VideoMarkAndAlbumSizesAreLocalized()
    {
        try
        {
            LocaleManager.Apply(Locale.RuRU);
            var tile = KillerShot.Controls.VideoMark.Wrap(new System.Windows.Controls.Image(), isVideo: true);
            var host = new Window { Content = tile };
            FrameworkElement? mark = null;
            void Find(DependencyObject node)
            {
                if (node is FrameworkElement e && System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "VideoAlbumMark") mark = e;
                foreach (object child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) Find(d);
            }
            Find(tile);
            string? name = mark == null ? null : System.Windows.Automation.AutomationProperties.GetName(mark);
            host.Close();
            if (mark == null || name != (string)Application.Current.FindResource("Str_Capture_Video") || name == "Video")
                throw new InvalidOperationException($"The video mark is named '{name}' in Russian.");

            LocaleManager.Apply(Locale.Fr);
            var editor = App.Instance.EditorWindow ?? throw new InvalidOperationException("No editor window.");
            editor.TestRefreshAlbumManager();
            var rows = editor.TestAlbumManagerDetails();
            if (rows.Count == 0) throw new InvalidOperationException("The album manager listed no albums.");
            if (rows.Any(r => r.EndsWith(" KB", StringComparison.Ordinal)))
                throw new InvalidOperationException("Album sizes kept the English unit in French: " + rows[0]);
        }
        finally { LocaleManager.Apply(Locale.EnUS); }
    }

    private static void ColorPickerIsLocalized()
    {
        var resources = Application.Current.Resources;
        object? ok = resources["Str_Btn_OK"], hex = resources["Str_Color_Hex"];
        resources["Str_Btn_OK"] = "Übernehmen";
        resources["Str_Color_Hex"] = "Hexwert";
        try
        {
            var dialog = new KillerShot.Controls.ColorPickerDialog(null, Colors.SteelBlue);
            var texts = new List<string>();
            void Walk(object node)
            {
                if (node is System.Windows.Controls.TextBlock { Text: { } t }) texts.Add(t);
                if (node is System.Windows.Controls.ContentControl { Content: string s }) texts.Add(s);
                if (node is DependencyObject d)
                    foreach (object child in LogicalTreeHelper.GetChildren(d)) Walk(child);
            }
            Walk(dialog);
            dialog.Close();
            if (texts.Contains("OK") || texts.Contains("Hex"))
                throw new InvalidOperationException("The color picker kept English text: " + string.Join(" | ", texts.Where(t => t is "OK" or "Hex")));
        }
        finally { resources["Str_Btn_OK"] = ok; resources["Str_Color_Hex"] = hex; }
    }

    private static void DockLayoutDoesNotReadLibrary()
    {
        App.Instance.ShowEditor();
        var editor = App.Instance.EditorWindow ?? throw new InvalidOperationException("No editor window.");
        var strip = editor.TestStrip ?? throw new InvalidOperationException("No collection strip.");
        int reads = 0;
        var read = strip.ItemsReader;
        strip.ItemsReader = () => { reads++; return read(); };
        var dock = editor.TestStripDock;
        int changes = 0;
        dock.LayoutChanged += () => changes++;
        var original = dock.Side;
        foreach (var side in Enum.GetValues<KillerFamily.Layout.DockSide>().Append(original))
            dock.DockTo(side);
        if (changes == 0) throw new InvalidOperationException("Docking the strip raised no layout change.");
        if (reads != 0) throw new InvalidOperationException($"Docking the strip {changes} times read the album {reads} times.");
    }

    private static void CanceledDiscardKeepsReview()
    {
        var pixels = new byte[16 * 16 * 4];
        var picture = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        picture.Freeze();
        var doc = new EditorDocument(picture);
        App.Instance.ShowEditor();
        if (!App.Instance.SetDocument(doc)) throw new InvalidOperationException("The review fixture did not open.");
        var editor = App.Instance.EditorWindow ?? throw new InvalidOperationException("No editor window.");
        editor.BeginReview(doc, DateTime.Now, CaptureMode.Region);
        doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(1, 1), new Point(8, 8), Colors.Black, 1));
        App.Instance.PresentDocumentReplacement = _ => false;
        editor.TestDiscardReview();
        if (!editor.InReview || !ReferenceEquals(App.Instance.Document, doc))
            throw new InvalidOperationException($"Cancel on the discard prompt ended the review: inReview={editor.InReview}, kept={ReferenceEquals(App.Instance.Document, doc)}.");
        App.Instance.PresentDocumentReplacement = _ => true;
        editor.TestDiscardReview();
        if (editor.InReview || App.Instance.Document != null)
            throw new InvalidOperationException("A confirmed discard did not end the review and close the picture.");
    }
}
