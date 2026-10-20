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
