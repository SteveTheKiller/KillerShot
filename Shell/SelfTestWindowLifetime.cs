using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestWindowLifetime
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-window-lifetime") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>
        {
            "H1: Document event subscribers retain closed windows; detaching them permits collection with the document alive.",
            "H2: Application.MainWindow retains the first closed window; public MainWindow identity reveals that root.",
            "H3: Pending animations temporarily retain the window; dispatcher idle and completed fades release it.",
            "H4: Library callers apply a successful-navigation update after Cancel; the library becomes hidden while the document stays unchanged."
        };
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new window lifetime test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Synthetic album did not open.");
            var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 64).ToArray(), 16);
            image.Freeze();
            var document = new EditorDocument(image);
            App.Instance.SetDocument(document);
            App.Instance.ShowEditor();
            await Task.Delay(300);
            var retired = CloseEditor();
            Check(App.Instance.EditorWindow == null, "The closed editor is no longer the active window.");
            App.Instance.ShowEditor();
            await Task.Delay(500);
            report.Add("retired_is_application_main_window=" + IsMainWindow(retired));
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await App.Instance.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(40);
            }
            Check(!retired.IsAlive, "The closed MainWindow is still retained while App.Document remains alive.");
            Check(ReferenceEquals(App.Instance.Document, document), "Closing the editor must preserve its document.");
            var active = App.Instance.EditorWindow!;
            Check(ReferenceEquals(((EditorCanvas)active.FindName("Editor")).Document, document),
                "Reopening attaches the preserved document to the active editor.");
            document.Crop(new Int32Rect(0, 0, 2, 2));
            Check(((Button)active.FindName("UndoBtn")).IsEnabled && document.PixelWidth == 2,
                "Editing after reopening refreshes the active editor.");
            report.Add("closed_window_collected_with_live_document=True");
            report.Add("reopened_editor_tracks_document_edits=True");

            long targetId = CaptureLibrary.AddNow(image.Clone(), DateTime.Now, "region", "Cancellation target", null);
            active.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(active), 0, Key.F7)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Task.Delay(150);
            var library = (LibraryView)active.FindName("Library");
            Check(library.IsVisible, "The library shortcut must open the library before navigation.");
            var tileStyle = (Style)library.FindResource("LibTile");
            var tile = Visuals(library).OfType<Button>().Single(button => ReferenceEquals(button.Style, tileStyle));
            bool prompted = RespondToConfirmation(false, () => tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Check(prompted && ReferenceEquals(App.Instance.Document, document) && document.Dirty && document.PixelWidth == 2,
                "Cancel must preserve the original document and its edits.");
            Check(library.IsVisible, "Canceling library navigation unexpectedly hides the library view.");
            report.Add("cancel_library_navigation_preserves_document_and_view=True");

            prompted = RespondToConfirmation(true, () => tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Check(prompted && !ReferenceEquals(App.Instance.Document, document) && App.Instance.CurrentItem?.Id == targetId
                && !library.IsVisible, "Accepting replacement must open the selected capture and show the editor.");
            report.Add("accepted_library_navigation_shows_new_document=True");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            if (Directory.Exists(folder)) File.WriteAllLines(Path.Combine(folder, "window-lifetime-report.txt"), report);
            App.Instance.Document?.MarkSaved();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseEditor()
    {
        var window = App.Instance.EditorWindow!;
        var weak = new WeakReference(window);
        window.Opacity = 0;
        ((Button)window.FindName("CloseBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsMainWindow(WeakReference window) => ReferenceEquals(window.Target, App.Instance.MainWindow);

    private static bool RespondToConfirmation(bool accept, Action action)
    {
        bool seen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = App.Instance.Windows.OfType<ConfirmDialog>().FirstOrDefault(window => window.IsVisible);
            if (dialog == null) return;
            seen = true;
            timer.Stop();
            ((Button)dialog.FindName(accept ? "OkButton" : "CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try { action(); }
        finally { timer.Stop(); }
        return seen;
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
