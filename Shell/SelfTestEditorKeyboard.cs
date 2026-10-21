using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-editor-keyboard &lt;folder&gt;. With the pointer
/// unused, place a redaction and a text mark, move and resize them, move between them, undo,
/// delete and export, all through the real window's keys and UI Automation.</summary>
internal static class SelfTestEditorKeyboard
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-editor-keyboard") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static void Key(MainWindow window, IInputElement target, Key key)
    {
        var source = PresentationSource.FromVisual(window)!;
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(down);
        if (down.Handled) return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new editor-keyboard test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            var px = new byte[400 * 300 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 180; px[i + 2] = 40; px[i + 3] = 255; }
            var image = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, px, 400 * 4);
            image.Freeze();
            var doc = new EditorDocument(image);
            App.Instance.SetDocument(doc);
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(300);
            var canvas = window.Editor;
            canvas.Focus();
            Keyboard.Focus(canvas);
            if (!canvas.IsKeyboardFocused) throw new InvalidOperationException("The picture cannot take keyboard focus.");

            // Redaction: X picks the tool, Enter places a box in the visible picture.
            Key(window, canvas, System.Windows.Input.Key.X);
            Key(window, canvas, System.Windows.Input.Key.Enter);
            if (doc.State.Items.SingleOrDefault() is not ShapeAnnotation { Kind: ShapeKind.Redact } placed || canvas.Selected != placed)
                throw new InvalidOperationException("Enter did not place and select a redaction box.");
            var before = placed.Box;
            Key(window, canvas, System.Windows.Input.Key.Right);
            Key(window, canvas, System.Windows.Input.Key.Down);
            var moved = (ShapeAnnotation)canvas.Selected!;
            if (moved.Box.TopLeft != before.TopLeft + new Vector(1, 1)) throw new InvalidOperationException("Arrow keys did not move the redaction.");
            report.Add($"redaction placed at {before} and moved by keyboard PASS");

            // Text: T, Enter opens the text box in the picture, Enter commits it.
            Key(window, canvas, System.Windows.Input.Key.T);
            Key(window, canvas, System.Windows.Input.Key.Enter);
            var box = canvas.Children.OfType<TextBox>().SingleOrDefault() ?? throw new InvalidOperationException("Enter did not open a text box.");
            if (!box.IsKeyboardFocused) throw new InvalidOperationException("The text box did not take focus.");
            box.Text = "Ticket 4417";
            Key(window, box, System.Windows.Input.Key.Enter);
            if (doc.State.Items.Count != 2 || doc.State.Items[1] is not TextAnnotation { Text: "Ticket 4417" } text || canvas.Selected != text)
                throw new InvalidOperationException("Enter did not commit and select the text mark.");
            if (!canvas.IsKeyboardFocused) throw new InvalidOperationException("Focus did not return to the picture after text.");
            report.Add("text placed, typed and committed by keyboard PASS");

            // Tab moves between marks and past the last one leaves the picture, so focus is never
            // trapped. Escape clears the selection; Tab then starts at the first mark again.
            Key(window, canvas, System.Windows.Input.Key.Tab);
            if (canvas.Selected != null) throw new InvalidOperationException("Tab past the last mark kept a selection.");
            canvas.Focus();
            Keyboard.Focus(canvas);
            Key(window, canvas, System.Windows.Input.Key.Escape);
            Key(window, canvas, System.Windows.Input.Key.Tab);
            if (canvas.Selected is not ShapeAnnotation { Kind: ShapeKind.Redact }) throw new InvalidOperationException("Tab did not start at the first mark.");
            if (!canvas.IsKeyboardFocused) throw new InvalidOperationException("Tab between marks moved focus out of the picture.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(canvas);
            var marks = peer.GetChildren()?.Where(c => c.GetPattern(PatternInterface.SelectionItem) != null).ToList() ?? [];
            var names = marks.Select(m => m.GetName()).ToList();
            report.Add("automation marks: " + string.Join(" | ", names));
            if (marks.Count != 2 || !names[0].StartsWith("Redact") || !names[1].Contains("Ticket 4417"))
                throw new InvalidOperationException("The automation tree does not name both marks.");
            if (peer.GetPattern(PatternInterface.Selection) is not ISelectionProvider selection || selection.GetSelection().Length != 1)
                throw new InvalidOperationException("The picture does not report its selected mark.");
            var transform = (ITransformProvider)marks[0].GetPattern(PatternInterface.Transform)!;
            if (!transform.CanResize || !transform.CanMove) throw new InvalidOperationException("Marks cannot be moved or resized through automation.");
            transform.Resize(150, 90);
            var resized = (ShapeAnnotation)canvas.Selected!;
            if (Math.Abs(resized.Box.Width - 150) > 0.01 || Math.Abs(resized.Box.Height - 90) > 0.01) throw new InvalidOperationException("Automation resize did not size the mark.");
            canvas.ResizeSelected(new Vector(10, -5));
            resized = (ShapeAnnotation)canvas.Selected!;
            if (Math.Abs(resized.Box.Width - 160) > 0.01 || Math.Abs(resized.Box.Height - 85) > 0.01) throw new InvalidOperationException("Keyboard resize did not size the mark.");
            ((ISelectionItemProvider)marks[1].GetPattern(PatternInterface.SelectionItem)!).Select();
            if (canvas.Selected is not TextAnnotation) throw new InvalidOperationException("Automation could not select the text mark.");
            report.Add("tab, automation names, selection, move and resize PASS");

            // Delete, undo through the toolbar's automation, then export.
            Key(window, canvas, System.Windows.Input.Key.Delete);
            if (doc.State.Items.Count != 1) throw new InvalidOperationException("Delete did not remove the selected mark.");
            var undo = (Button)window.FindName("UndoBtn");
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(undo).GetPattern(PatternInterface.Invoke)!).Invoke();
            // An automation click is queued; give it a moment.
            for (int wait = 0; wait < 40 && doc.State.Items.Count != 2; wait++) await Task.Delay(25);
            if (doc.State.Items.Count != 2) throw new InvalidOperationException("Undo did not restore the deleted mark.");
            var output = new FormatConvertedBitmap(doc.Render(), PixelFormats.Bgra32, null, 0);
            var r = resized.PixelCells;
            var pixel = new byte[4];
            output.CopyPixels(new Int32Rect((int)r.X + 2, (int)r.Y + 2, 1, 1), pixel, 4, 0);
            if (pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0) throw new InvalidOperationException("The exported redaction is not black.");
            report.Add("delete, undo and export without a pointer PASS");
            // Two marks placed alike are equal records: Tab must still step past the second one.
            var alike = new EditorDocument(image);
            App.Instance.SetDocument(alike);
            await Task.Delay(100);
            var first = new ShapeAnnotation(ShapeKind.Redact, new Point(10, 10), new Point(60, 40), Colors.Black, 1);
            alike.Add(first);
            alike.Add(first with { });
            canvas.Select(null);
            bool steps = canvas.SelectNext(false) && canvas.SelectNext(false) && ReferenceEquals(canvas.Selected, alike.State.Items[1]);
            if (!steps || canvas.SelectNext(false)) throw new InvalidOperationException("Tab stuck on two marks placed alike.");
            report.Add("Tab passes two marks placed alike PASS");

            // A step badge shrinks from the keyboard, and a resize that changes nothing adds no undo step.
            var badge = new StepAnnotation(new Point(100, 100), 1, Colors.Red, 30);
            alike.Add(badge);
            canvas.Select(badge);
            int undoBefore = alike.UndoCount;
            canvas.ResizeSelected(new Vector(-1, 0));
            if (canvas.Selected is not StepAnnotation { Width: 29 }) throw new InvalidOperationException("Ctrl+Left did not shrink the step badge.");
            canvas.Select(alike.State.Items[0]);
            for (int i = 0; i < 100; i++) canvas.ResizeSelected(new Vector(-1, -1));
            int afterSmallest = alike.UndoCount;
            canvas.ResizeSelected(new Vector(-1, -1));
            if (alike.UndoCount != afterSmallest) throw new InvalidOperationException("A resize that changed nothing added an undo step.");
            report.Add($"step badge shrinks, no-op resize adds no undo ({undoBefore} -> {afterSmallest}) PASS");

            // A damaged marks row opens as no marks instead of failing the capture.
            if (MarksFormat.Read("""{"v":1,"marks":[{"t":"shape"}]}""") is { Count: > 0 })
                throw new InvalidOperationException("A mark missing its fields was read.");
            report.Add("damaged marks row is skipped PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "editor-keyboard-report.txt"), report);
            window?.Close();
            App.Instance.Shutdown(code);
        }
    }
}
