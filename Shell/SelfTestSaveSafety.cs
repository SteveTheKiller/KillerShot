using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestSaveSafety
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-save-safety") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            string original = Path.Combine(folder, "animated.png");
            // Independently decoded two-frame red/blue APNG from the adversarial review.
            byte[] animation = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAABAAAAAQAAAAAAAAAAAAEACgAAWn8w0AAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAaZmNUTAAAAAEAAAABAAAAAQAAAAAAAAAAAAEACgAAwQzaBAAAABFmZEFUAAAAAnicY2Bg+P8fAAMCAf/1e6XXAAAAAElFTkSuQmCC");
            File.WriteAllBytes(original, animation);
            var document = new EditorDocument(ImageOutput.Load(original), original);
            App.Instance.SetDocument(document);
            App.Instance.ShowEditor();
            await Task.Delay(150);
            var window = App.Instance.EditorWindow!;
            string? suggested = null;
            string exported = Path.Combine(folder, "animated-static.png");
            bool opened = WithSaveDialog(window, "SaveBtn", dialog =>
            {
                suggested = dialog.FileName;
                dialog.FileName = exported;
                dialog.DialogResult = true;
            });
            Check(File.ReadAllBytes(original).SequenceEqual(animation), "Ordinary Save replaced the animated original.");
            Check(opened && suggested == "animated-static.png", "Ordinary Save must offer a distinct static PNG filename.");
            Check(File.Exists(exported) && ImageOutput.CanSaveInPlace(exported), "The separate static export was not written.");
            Check(document.FilePath == exported && !document.Dirty, "Successful export did not update saved state.");
            report.Add("Actual Save dialog preserves the two-frame original and writes a distinct static PNG: PASS");
            document = new EditorDocument(ImageOutput.Load(original), original);
            App.Instance.SetDocument(document);
            WithSaveDialog(window, "SaveBtn", dialog => dialog.DialogResult = false);
            Check(File.ReadAllBytes(original).SequenceEqual(animation) && document.FilePath == original,
                "Canceling export changed the original or its document path.");
            WithSaveDialog(window, "SaveBtn", dialog =>
            {
                dialog.FileName = original;
                dialog.DialogResult = true;
            });
            Check(File.ReadAllBytes(original).SequenceEqual(animation) && document.FilePath == original,
                "Choosing the original APNG as the export destination destroyed its animation.");
            Check(((TextBlock)window.FindName("StatusText")).Text.Contains("different filename"),
                "Rejected animation conversion did not explain how to preserve the original.");
            report.Add("Cancel and attempted same-path static conversion preserve the animated original: PASS");
            await CheckReplacement(window, folder, report);
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "save-safety-report.txt"), report);
            App.Instance.Document?.MarkSaved();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }

    private static Task CheckReplacement(MainWindow window, string folder, List<string> report)
    {
        App.Instance.Document?.MarkSaved();
        string destination = Path.Combine(folder, "A.png");
        var aPixels = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
            PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8);
        var bPixels = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96,
            PixelFormats.Bgra32, null, new byte[64], 16);
        ImageOutput.Save(aPixels, destination);
        byte[] original = File.ReadAllBytes(destination);
        var a = new EditorDocument(aPixels, destination);
        var b = new EditorDocument(bPixels);
        App.Instance.SetDocument(a);
        Check(WithSaveDialog(window, "SaveAsBtn", dialog =>
        {
            Check(App.Instance.SetDocument(b), "The replacement entry did not accept clean A.");
            b.Crop(new Int32Rect(0, 0, 3, 3));
            dialog.FileName = destination;
            dialog.DialogResult = true;
        }), "Save As never displayed its real modal dialog.");
        Check(File.ReadAllBytes(destination).SequenceEqual(original), "Replacement B overwrote A's destination.");
        Check(ReferenceEquals(App.Instance.Document, b) && b.Dirty && b.FilePath == null,
            "Canceled save transaction changed replacement B's saved state.");
        report.Add("Document replacement inside the real Save As modal preserves A and B's dirty state: PASS");
        b.MarkSaved();
        App.Instance.SetDocument(a);
        string bPath = Path.Combine(folder, "B.png");
        ImageOutput.Save(bPixels, bPath);
        WithSaveDialog(window, "SaveAsBtn", dialog =>
        {
            App.Instance.OpenImage(bPath);
            dialog.FileName = destination;
            dialog.DialogResult = true;
        });
        Check(File.ReadAllBytes(destination).SequenceEqual(original)
            && App.Instance.Document?.FilePath == bPath && App.Instance.Document.PixelWidth == 4,
            "External open during Save As changed A or saved B to the old destination.");
        App.Instance.SetDocument(a);
        a.Crop(new Int32Rect(0, 0, 1, 1));
        WithSaveDialog(window, "SaveAsBtn", dialog => dialog.DialogResult = false);
        Check(a.Dirty && File.ReadAllBytes(destination).SequenceEqual(original), "Canceled Save As cleared dirty state.");
        WithSaveDialog(window, "SaveAsBtn", dialog =>
        {
            dialog.FileName = Path.Combine(folder, "unsupported.gif");
            dialog.DialogResult = true;
        });
        Check(a.Dirty && a.FilePath == destination && !File.Exists(Path.Combine(folder, "unsupported.gif")),
            "Failed output cleared dirty state or changed the document path.");
        WithSaveDialog(window, "SaveAsBtn", dialog =>
        {
            dialog.FileName = Path.Combine(folder, "saved-A.png");
            dialog.DialogResult = true;
        });
        Check(!a.Dirty && ImageOutput.Load(a.FilePath!).PixelWidth == 1, "Successful Save As failed to save intended edits.");
        report.Add("External open, cancel, output failure and successful dirty-document Save As: PASS");
        return Task.CompletedTask;
    }

    private static bool WithSaveDialog(MainWindow window, string button, Action<FileDialog> respond)
    {
        bool seen = false;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<FileDialog>().FirstOrDefault(w => w.IsVisible);
            if (dialog == null && DateTime.UtcNow < deadline) return;
            timer.Stop();
            if (dialog == null) return;
            seen = true;
            try { respond(dialog); }
            catch (Exception ex) { failure = ex; dialog.DialogResult = false; }
        };
        timer.Start();
        try
        {
            ((Button)window.FindName(button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (button == "SaveBtn")
            {
                var menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Where(s => s.RootVisual != null).SelectMany(s => Visuals(s.RootVisual))
                    .OfType<ContextMenu>().Single(m => m.IsOpen);
                ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
            }
        }
        finally { timer.Stop(); }
        if (failure != null) throw failure;
        return seen;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
