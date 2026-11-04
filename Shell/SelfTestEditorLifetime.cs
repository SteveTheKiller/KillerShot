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
