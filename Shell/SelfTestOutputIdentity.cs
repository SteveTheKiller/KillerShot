using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-output-identity &lt;folder&gt;. With image A open
/// and the album shown over it, resolve capture B for album output and reject hidden-editor
/// output and edits through buttons, overflow and shortcuts. Clipboard and printing stay unused.</summary>
internal static class SelfTestOutputIdentity
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-output-identity") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static BitmapSource Solid(byte r, byte g, byte b)
    {
        var px = new byte[64 * 48 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new output-identity test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            var a = Solid(220, 20, 20);
            CaptureLibrary.AddNow(Solid(20, 20, 220), DateTime.Now, "window", "Capture B", null);
            var doc = new EditorDocument(a);
            doc.Crop(new Int32Rect(0, 0, 32, 24));
            doc.Crop(new Int32Rect(0, 0, 16, 12));
            doc.Undo();
            App.Instance.SetDocument(doc);
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(200);

            foreach (var view in new[] { "grid", "timeline", "shelves", "flow" })
            {
                window.TestShowLibraryView(view);
                await Task.Delay(300);
                window.UpdateLayout();
                if (!window.Library.IsVisible) throw new InvalidOperationException(view + ": the album is not over the image.");
                bool carousel = view is "flow" or "shelves";
                if (window.StripDock.ShowPane == carousel)
                    throw new InvalidOperationException(view + ": the photo strip " + (carousel ? "doubles the album carousel." : "is missing."));
                var shot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                shot.Render((Visual)window.Content);
                ImageOutput.SavePng(shot, Path.Combine(folder, "album-" + view + ".png"));
                report.Add(view + ": photo strip " + (carousel ? "hidden" : "shown") + " PASS");
                AssertEditorCommandsDisabled(window);
                if (view == "shelves") continue;
                var target = Visuals(window.Library).OfType<FrameworkElement>()
                    .FirstOrDefault(e => e.IsVisible && (e is Button { Tag: CaptureItem } || e is CoverFlow { MainView: true }))
                    ?? throw new InvalidOperationException(view + ": no capture control is showing.");
                target.Focus();
                Keyboard.Focus(target);
                var item = window.Library.FocusedCapture() ?? throw new InvalidOperationException(view + ": the focused capture was not found.");
                var px = new byte[4];
                new FormatConvertedBitmap(CaptureLibrary.LoadImage(item), PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1, 1, 1, 1), px, 4, 0);
                if (px[0] != 220 || px[2] != 20) throw new InvalidOperationException(view + ": Copy and Print would use the editor image instead of capture B.");
                report.Add(view + ": focused capture is B PASS");
                if (view == "grid")
                {
                    string destination = Path.Combine(folder, "hidden-editor-output.png");
                    AcceptSaveDialog(destination, () => Click(window, "SaveAsBtn"));
                    if (File.Exists(destination))
                    {
                        var actual = new byte[4];
                        new FormatConvertedBitmap(ImageOutput.Load(destination), PixelFormats.Bgra32, null, 0)
                            .CopyPixels(new Int32Rect(1, 1, 1, 1), actual, 4, 0);
                        report.Add($"toolbar output with blue B focused: R={actual[2]} G={actual[1]} B={actual[0]}");
                        throw new InvalidOperationException("Editor Save As remained active over the album and exported hidden image A.");
                    }
                    var state = doc.State;
                    Click(window, "UndoBtn");
                    Click(window, "RedoBtn");
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
                    Key(window, System.Windows.Input.Key.Y, ModifierKeys.Control);
                    Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
                    AcceptSaveDialog(destination, () => Key(window, System.Windows.Input.Key.S, ModifierKeys.Control));
                    if (!ReferenceEquals(doc.State, state) || File.Exists(destination))
                        throw new InvalidOperationException("An album shortcut changed or saved hidden image A.");
                    // Ctrl+Shift+S in the album is Save as for the focused capture, so it writes B, never A.
                    AcceptSaveDialog(destination, () => Key(window, System.Windows.Input.Key.S, ModifierKeys.Control | ModifierKeys.Shift));
                    if (File.Exists(destination))
                    {
                        var saved = new byte[4];
                        new FormatConvertedBitmap(ImageOutput.Load(destination), PixelFormats.Bgra32, null, 0)
                            .CopyPixels(new Int32Rect(1, 1, 1, 1), saved, 4, 0);
                        File.Delete(destination);
                        if (saved[0] != 220 || saved[2] != 20)
                            throw new InvalidOperationException("Album Save as wrote hidden image A instead of the focused capture B.");
                    }
                    report.Add("Album commands cannot export or edit hidden image A PASS");
                }
            }

            var search = (TextBox)window.Library.FindName("SearchBox");
            window.Activate();
            window.Library.FocusSearch();
            await Task.Delay(200);
            // Keyboard focus needs the app in the foreground, which a headless run cannot promise;
            // the popup's own focus scope still has to hold the search box.
            if (!search.IsKeyboardFocusWithin && !ReferenceEquals(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(search)), search)) throw new InvalidOperationException("The search box did not take focus.");
            if (window.Library.FocusedCapture() != null) throw new InvalidOperationException("Search focus still resolves a capture.");
            report.Add("search focus: no capture, editor image not used PASS");

            // With no capture focused, the actual output shortcuts must remain harmless.
            Key(window, System.Windows.Input.Key.Escape);
            window.Library.Focus();
            Keyboard.Focus(window.Library);
            if (window.Library.FocusedCapture() != null) throw new InvalidOperationException("The library background still resolves a capture.");
            Key(window, System.Windows.Input.Key.C, ModifierKeys.Control);
            Key(window, System.Windows.Input.Key.P, ModifierKeys.Control);
            Key(window, System.Windows.Input.Key.C, ModifierKeys.Control | ModifierKeys.Shift);
            AssertEditorCommandsDisabled(window);
            report.Add("No-selection output shortcuts leave clipboard and printer unused PASS");

            Key(window, System.Windows.Input.Key.L, ModifierKeys.Control);
            if (window.Library.IsVisible || !((Button)window.FindName("SaveBtn")).IsEnabled
                || !((Button)window.FindName("UndoBtn")).IsEnabled || !((Button)window.FindName("RedoBtn")).IsEnabled)
                throw new InvalidOperationException("Returning to the editor did not restore its commands.");
            window.Editor.Focus();
            Keyboard.Focus(window.Editor);
            Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
            if (doc.PixelWidth != 64) throw new InvalidOperationException("Visible-editor Undo did not restore the full image.");
            Key(window, System.Windows.Input.Key.Y, ModifierKeys.Control);
            if (doc.PixelWidth != 32) throw new InvalidOperationException("Visible-editor Redo did not restore the crop.");
            string visibleOutput = Path.Combine(folder, "visible-editor-output.png");
            AcceptSaveDialog(visibleOutput, () => Key(window, System.Windows.Input.Key.S, ModifierKeys.Control | ModifierKeys.Shift));
            var visiblePixel = new byte[4];
            new FormatConvertedBitmap(ImageOutput.Load(visibleOutput), PixelFormats.Bgra32, null, 0)
                .CopyPixels(new Int32Rect(1, 1, 1, 1), visiblePixel, 4, 0);
            if (visiblePixel[2] != 220 || visiblePixel[0] != 20 || doc.Dirty)
                throw new InvalidOperationException("Visible-editor Save As did not export A and mark it saved.");
            report.Add("Visible-editor Undo, Redo and real Save As export PASS");

            window.SetToolbarLayout(new MainWindow.ToolbarLayout([], [], ["undo", "redo", "save", "copy", "copytext", "print"]));
            Click(window, "MoreBtn");
            var more = (ContextMenu)window.FindName("MoreMenu");
            var queuedUndo = more.Items.OfType<MenuItem>().Single(m => m.InputGestureText == "Ctrl+Z");
            int replayed = 0;
            var undo = (Button)window.FindName("UndoBtn");
            RoutedEventHandler trackReplay = (_, _) => replayed++;
            undo.AddHandler(Button.ClickEvent, trackReplay, true);
            var beforeQueue = doc.State;
            queuedUndo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            more.IsOpen = false;
            window.TestShowLibraryView("grid");
            await Task.Delay(100);
            undo.RemoveHandler(Button.ClickEvent, trackReplay);
            if (replayed != 0 || !ReferenceEquals(doc.State, beforeQueue))
                throw new InvalidOperationException("A queued overflow action replayed after the album covered the editor.");
            Click(window, "MoreBtn");
            foreach (var item in more.Items.OfType<MenuItem>())
                if (item.InputGestureText is "Ctrl+Z" or "Ctrl+Y" or "Ctrl+S" or "Ctrl+C" or "Ctrl+Shift+C" or "Ctrl+P")
                    if (item.IsEnabled) throw new InvalidOperationException("An editor output or edit action remained enabled in overflow.");
            more.IsOpen = false;
            report.Add("Overflow disables editor actions and drops a queued action after workspace change PASS");

            doc.MarkSaved();
            if (!App.Instance.SetDocument(null)) throw new InvalidOperationException("The test document did not close.");
            AssertEditorCommandsDisabled(window);
            window.Library.Focus();
            Keyboard.Focus(window.Library);
            string emptyOutput = Path.Combine(folder, "no-document-output.png");
            AcceptSaveDialog(emptyOutput, () => Click(window, "SaveAsBtn"));
            AcceptSaveDialog(emptyOutput, () => Key(window, System.Windows.Input.Key.S, ModifierKeys.Control));
            AcceptSaveDialog(emptyOutput, () => Key(window, System.Windows.Input.Key.S, ModifierKeys.Control | ModifierKeys.Shift));
            Key(window, System.Windows.Input.Key.Z, ModifierKeys.Control);
            Key(window, System.Windows.Input.Key.Y, ModifierKeys.Control);
            if (File.Exists(emptyOutput)) throw new InvalidOperationException("A command exported an image without an open document.");
            report.Add("No-document commands remain disabled and harmless PASS");
            CheckEditDialogs(window, folder, report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "output-identity-report.txt"), report);
            window?.Close();
            App.Instance.Shutdown(code);
        }
    }

    private static void CheckEditDialogs(MainWindow window, string folder, List<string> report)
    {
        string inserted = Path.Combine(folder, "inserted.png");
        string replacement = Path.Combine(folder, "replacement.png");
        ImageOutput.SavePng(Solid(20, 220, 20), inserted);
        ImageOutput.SavePng(Solid(20, 20, 220), replacement);
        var failures = new List<string>();
        foreach (string operation in new[] { "insert", "resize", "margin" })
        foreach (string scenario in new[] { "replace", "state", "workspace", "cancel", "success" })
        {
            App.Instance.Document?.MarkSaved();
            var original = new EditorDocument(Solid(220, 20, 20));
            if (!App.Instance.SetDocument(original)) throw new InvalidOperationException("The edit fixture could not open A.");
            var originalState = original.State;
            EditorDocument expectedDocument = original;
            EditorState expectedState = original.State;
            WithImageDialog(window, operation, dialog =>
            {
                if (scenario == "replace")
                {
                    App.Instance.OpenImage(replacement);
                    expectedDocument = App.Instance.Document ?? throw new InvalidOperationException("External open lost the document.");
                    if (ReferenceEquals(expectedDocument, original) || expectedDocument.FilePath != replacement)
                        throw new InvalidOperationException("External open did not replace clean A while its dialog was open.");
                }
                else if (scenario == "state") original.Crop(new Int32Rect(0, 0, 40, 30));
                else if (scenario == "workspace") window.TestShowLibraryView("grid");
                expectedState = expectedDocument.State;
                if (dialog is FileDialog file) file.FileName = inserted;
                else ((TextBox)dialog.FindName("InputText")).Text = operation == "resize" ? "50%" : "4";
                if (scenario == "cancel") dialog.DialogResult = false;
                else if (dialog is FileDialog picker) SelfTestDialogs.Accept(picker);
                else dialog.DialogResult = true;
            });

            bool accepted = scenario == "success";
            bool preserved = ReferenceEquals(expectedDocument.State, expectedState);
            bool rightSuccess = operation switch
            {
                "insert" => original.State.Items.Count == 1 && original.PixelWidth == 64 && original.PixelHeight == 48,
                "resize" => original.PixelWidth == 32 && original.PixelHeight == 24,
                _ => original.PixelWidth == 72 && original.PixelHeight == 56,
            };
            bool good = ReferenceEquals(App.Instance.Document, expectedDocument)
                && (accepted ? rightSuccess && original.Dirty : preserved)
                && (scenario != "replace" || ReferenceEquals(original.State, originalState));
            string outcome = operation + " dialog " + scenario + ": " + (good ? "PASS" : "FAIL");
            report.Add(outcome);
            if (!good) failures.Add(outcome);
            App.Instance.Document?.MarkSaved();
        }
        if (failures.Count > 0) throw new InvalidOperationException("A stale image dialog changed its target: " + string.Join("; ", failures));
    }

    private static void WithImageDialog(MainWindow window, string operation, Action<Window> respond)
    {
        Click(window, "ImageBtn");
        var menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Where(s => s.RootVisual != null).SelectMany(s => Visuals(s.RootVisual))
            .OfType<ContextMenu>().Single(m => m.IsOpen);
        string label = LocaleManager.Loc(operation switch
        {
            "insert" => "Str_Img_InsertFile",
            "resize" => "Str_Img_Resize",
            _ => "Str_Img_Margin",
        });
        var action = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, label));
        if (operation == "resize") action = action.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocaleManager.Loc("Str_Img_ResizeCustom")));
        menu.IsOpen = false;
        bool seen = false;
        Exception? failure = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is FileDialog or PromptDialog);
            if (dialog == null) return;
            timer.Stop();
            seen = true;
            try { respond(dialog); }
            catch (Exception error) { failure = error; dialog.DialogResult = false; }
        };
        timer.Start();
        try { action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        finally { timer.Stop(); }
        if (failure != null) throw failure;
        if (!seen) throw new InvalidOperationException(operation + " did not open its real modal dialog.");
    }

    private static void AssertEditorCommandsDisabled(MainWindow window)
    {
        foreach (string name in new[] { "SaveBtn", "SaveAsBtn", "CopyBtn", "CopyTextBtn", "PrintBtn", "UndoBtn", "RedoBtn", "ImageBtn" })
            if (((Button)window.FindName(name)).IsEnabled)
                throw new InvalidOperationException(name + " remained enabled over the album.");
    }

    private static void Click(MainWindow window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AcceptSaveDialog(string destination, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<FileDialog>().FirstOrDefault(w => w.IsVisible);
            if (dialog == null) return;
            timer.Stop();
            dialog.FileName = destination;
            SelfTestDialogs.Accept(dialog);
        };
        timer.Start();
        try { action(); }
        finally { timer.Stop(); }
    }

    private sealed class TestKeyboard(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(System.Windows.Input.Key key) => key switch
        {
            System.Windows.Input.Key.LeftCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            System.Windows.Input.Key.LeftShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            System.Windows.Input.Key.LeftAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            _ => KeyStates.None,
        };
    }

    private static void Key(MainWindow window, System.Windows.Input.Key key, ModifierKeys modifiers = ModifierKeys.None)
        => window.RaiseEvent(new KeyEventArgs(new TestKeyboard(modifiers), PresentationSource.FromVisual(window)!, 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
