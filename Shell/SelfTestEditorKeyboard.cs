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

    private static void Key(MainWindow window, IInputElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var source = PresentationSource.FromVisual(window)!;
        var keyboard = new TestKeyboard(modifiers);
        var down = new KeyEventArgs(keyboard, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(down);
        if (down.Handled) return;
        target.RaiseEvent(new KeyEventArgs(keyboard, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
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
            // Automation works in screen pixels: a resize and a move land where the reported bounds say.
            double perPixel = canvas.ScreenPixelsPerImagePixel;
            transform.Resize(150 * perPixel, 90 * perPixel);
            var resized = (ShapeAnnotation)canvas.Selected!;
            if (Math.Abs(resized.Bounds.Width - 150) > 1 || Math.Abs(resized.Bounds.Height - 90) > 1) throw new InvalidOperationException("Automation resize did not size the mark.");
            var target = marks[0].GetBoundingRectangle().TopLeft + new Vector(30, 20);
            transform.Move(target.X, target.Y);
            var landed = marks[0].GetBoundingRectangle().TopLeft;
            if (Math.Abs(landed.X - target.X) > 1.5 || Math.Abs(landed.Y - target.Y) > 1.5)
                throw new InvalidOperationException($"Automation move put the mark at {landed}, not {target}.");
            var sized = ((ShapeAnnotation)canvas.Selected!).Box;
            canvas.ResizeSelected(new Vector(10, -5));
            resized = (ShapeAnnotation)canvas.Selected!;
            if (Math.Abs(resized.Box.Width - sized.Width - 10) > 0.01 || Math.Abs(resized.Box.Height - sized.Height + 5) > 0.01) throw new InvalidOperationException("Keyboard resize did not size the mark.");
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
            App.Instance.Document?.MarkSaved();   // no discard prompt for the earlier picture
            if (!App.Instance.SetDocument(alike) || !ReferenceEquals(canvas.Document, alike))
                throw new InvalidOperationException("The second picture did not open.");
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

            // Crop through the window's actual key route. Pixel coordinates in this fixture
            // identify the chosen area independently of the selection implementation.
            var coordinates = new byte[40 * 30 * 4];
            for (int y = 0; y < 30; y++)
                for (int x = 0; x < 40; x++)
                {
                    int offset = (y * 40 + x) * 4;
                    coordinates[offset] = (byte)x;
                    coordinates[offset + 1] = (byte)y;
                    coordinates[offset + 2] = 200;
                    coordinates[offset + 3] = 255;
                }
            var coordinateImage = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, coordinates, 40 * 4);
            coordinateImage.Freeze();
            var crop = new EditorDocument(coordinateImage);
            App.Instance.Document?.MarkSaved();
            if (!App.Instance.SetDocument(crop)) throw new InvalidOperationException("The crop fixture did not open.");
            canvas.Zoom = 2;
            window.UpdateLayout();
            canvas.Focus();
            Keyboard.Focus(canvas);
            Key(window, canvas, System.Windows.Input.Key.C);
            Key(window, canvas, System.Windows.Input.Key.Enter);
            Key(window, canvas, System.Windows.Input.Key.Right);
            Key(window, canvas, System.Windows.Input.Key.Down);
            if (peer.GetName() != "Crop, 20 by 15 pixels at 11, 8" || peer.GetLiveSetting() != AutomationLiveSetting.Polite
                || !peer.GetHelpText().Contains("Ctrl+arrows"))
                throw new InvalidOperationException("Crop does not expose its rectangle and keyboard instructions through automation.");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var selectionView = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            selectionView.Render(window);
            ImageOutput.Save(selectionView, Path.Combine(folder, "keyboard-crop-selection.png"));
            Key(window, canvas, System.Windows.Input.Key.Enter);
            if (crop.PixelWidth != 20 || crop.PixelHeight != 15)
                throw new InvalidOperationException($"Keyboard crop left {crop.PixelWidth} by {crop.PixelHeight} pixels instead of the selected 20 by 15.");
            var cropOutput = new FormatConvertedBitmap(crop.Render(), PixelFormats.Bgra32, null, 0);
            cropOutput.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            if (pixel[0] != 11 || pixel[1] != 8) throw new InvalidOperationException("Keyboard crop chose the wrong top-left pixel.");
            cropOutput.CopyPixels(new Int32Rect(19, 14, 1, 1), pixel, 4, 0);
            if (pixel[0] != 30 || pixel[1] != 22) throw new InvalidOperationException("Keyboard crop chose the wrong bottom-right pixel.");
            report.Add("keyboard crop selects an interior rectangle at 200% zoom PASS");

            var regionDocument = new EditorDocument(coordinateImage);
            crop.MarkSaved();
            if (!App.Instance.SetDocument(regionDocument)) throw new InvalidOperationException("The region fixture did not open.");
            canvas.SetTool(ToolKind.Select);
            canvas.Zoom = 0.5;
            window.UpdateLayout();
            canvas.Focus();
            Keyboard.Focus(canvas);
            Int32Rect? picked = null;
            bool regionCompleted = false;
            canvas.BeginRegionPick(area => { picked = area; regionCompleted = true; });
            Key(window, canvas, System.Windows.Input.Key.Enter);
            Key(window, canvas, System.Windows.Input.Key.Right);
            Key(window, canvas, System.Windows.Input.Key.Down);
            Key(window, canvas, System.Windows.Input.Key.Right, ModifierKeys.Control);
            Key(window, canvas, System.Windows.Input.Key.Up, ModifierKeys.Control);
            if (peer.GetName() != "Read a region, 21 by 14 pixels at 11, 8" || !peer.GetHelpText().Contains("Enter reads"))
                throw new InvalidOperationException("OCR does not expose its current rectangle and acceptance instruction.");
            Key(window, canvas, System.Windows.Input.Key.Enter);
            if (!regionCompleted || picked != new Int32Rect(11, 8, 21, 14))
                throw new InvalidOperationException($"Keyboard OCR region did not complete with 11,8,21,14. Result: {picked}.");
            if (regionDocument.Dirty || regionDocument.PixelWidth != 40 || regionDocument.PixelHeight != 30)
                throw new InvalidOperationException("Choosing an OCR region changed the image.");
            report.Add("keyboard OCR rectangle moves, resizes and accepts at 50% zoom PASS");

            regionCompleted = false;
            canvas.BeginRegionPick(area => { picked = area; regionCompleted = true; });
            Key(window, canvas, System.Windows.Input.Key.Enter);
            Key(window, canvas, System.Windows.Input.Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
            Key(window, canvas, System.Windows.Input.Key.Escape);
            if (!regionCompleted || picked != null || regionDocument.Dirty || peer.GetName().StartsWith("Read a region,"))
                throw new InvalidOperationException("Escape did not cancel OCR selection without changing the image.");
            Key(window, canvas, System.Windows.Input.Key.C);
            Key(window, canvas, System.Windows.Input.Key.Enter);
            Key(window, canvas, System.Windows.Input.Key.Right);
            Key(window, canvas, System.Windows.Input.Key.Escape);
            if (regionDocument.Dirty || peer.GetName().StartsWith("Crop,"))
                throw new InvalidOperationException("Escape did not cancel the crop rectangle.");
            report.Add("Escape cancels crop and OCR with no image mutation or stale automation rectangle PASS");

            Key(window, canvas, System.Windows.Input.Key.Enter);
            for (int i = 0; i < 5; i++)
            {
                Key(window, canvas, System.Windows.Input.Key.Left, ModifierKeys.Shift);
                Key(window, canvas, System.Windows.Input.Key.Up, ModifierKeys.Shift);
                Key(window, canvas, System.Windows.Input.Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
                Key(window, canvas, System.Windows.Input.Key.Down, ModifierKeys.Control | ModifierKeys.Shift);
            }
            if (peer.GetName() != "Crop, 40 by 30 pixels at 0, 0")
                throw new InvalidOperationException("Keyboard rectangle exceeded the image boundary.");
            for (int i = 0; i < 5; i++)
            {
                Key(window, canvas, System.Windows.Input.Key.Left, ModifierKeys.Control | ModifierKeys.Shift);
                Key(window, canvas, System.Windows.Input.Key.Up, ModifierKeys.Control | ModifierKeys.Shift);
            }
            if (peer.GetName() != "Crop, 2 by 2 pixels at 0, 0")
                throw new InvalidOperationException("Keyboard crop did not stop at its minimum size.");
            Key(window, canvas, System.Windows.Input.Key.Enter);
            if (regionDocument.PixelWidth != 2 || regionDocument.PixelHeight != 2)
                throw new InvalidOperationException("Enter did not apply the resized crop through the window key route.");
            report.Add("Shift movement, Ctrl+Shift resize, image limits and resized crop acceptance PASS");

            regionDocument.MarkSaved();
            var replacement = new EditorDocument(coordinateImage);
            if (!App.Instance.SetDocument(replacement)) throw new InvalidOperationException("The replacement fixture did not open.");
            canvas.SetTool(ToolKind.Select);
            canvas.Focus();
            Keyboard.Focus(canvas);
            regionCompleted = false;
            canvas.BeginRegionPick(area => { picked = area; regionCompleted = true; });
            Key(window, canvas, System.Windows.Input.Key.Enter);
            if (!App.Instance.SetDocument(new EditorDocument(coordinateImage)) || !regionCompleted || picked != null)
                throw new InvalidOperationException("Replacing the document did not cancel its pending OCR region.");
            report.Add("document replacement cancels the pending region callback PASS");

            window.UpdateLayout();
            canvas.Focus();
            Keyboard.Focus(canvas);
            regionCompleted = false;
            canvas.BeginRegionPick(area => { picked = area; regionCompleted = true; });
            Key(window, canvas, System.Windows.Input.Key.Enter);
            canvas.Document!.Scale(4, 4);
            if (!regionCompleted || picked != null || peer.GetName().StartsWith("Read a region,"))
                throw new InvalidOperationException("Changing the source image retained a stale region rectangle.");
            report.Add("source image changes cancel the pending rectangle PASS");
            regionCompleted = false;
            canvas.BeginRegionPick(area => { picked = area; regionCompleted = true; });
            Key(window, canvas, System.Windows.Input.Key.Enter);
            Key(window, canvas, System.Windows.Input.Key.P);
            if (!regionCompleted || picked != null || canvas.Tool != ToolKind.Pen)
                throw new InvalidOperationException("Changing tools retained a pending OCR region.");
            report.Add("changing tools cancels the pending region PASS");

            // The review chord keeps its meaning even while Crop owns ordinary Enter.
            string reviewOutput = Path.Combine(folder, "review-output");
            Directory.CreateDirectory(reviewOutput);
            CaptureSettings.OutputFolder = reviewOutput;
            CaptureSettings.SaveToFolder = true;
            CaptureSettings.CopyToClipboard = false;
            CaptureSettings.PlaySound = false;
            CaptureSettings.Notify = false;
            using var tray = new TrayHost();
            var capture = App.Instance.TestUseCapture(new CaptureController(tray));
            foreach (bool pendingCrop in new[] { false, true })
            {
                App.Instance.Document?.MarkSaved();
                var reviewed = new EditorDocument(coordinateImage);
                if (!App.Instance.SetDocument(reviewed)) throw new InvalidOperationException("The review fixture did not open.");
                window.UpdateLayout();
                canvas.Focus();
                Keyboard.Focus(canvas);
                Key(window, canvas, System.Windows.Input.Key.C);
                if (pendingCrop) Key(window, canvas, System.Windows.Input.Key.Enter);
                var stateBeforeReview = reviewed.State;
                window.BeginReview(reviewed, DateTime.Now, KillerShot.Capture.CaptureMode.Region);
                Key(window, canvas, System.Windows.Input.Key.Enter, ModifierKeys.Control);
                if (window.InReview || !ReferenceEquals(reviewed.State, stateBeforeReview) || reviewed.Dirty
                    || reviewed.FilePath is not { } savedReview || !File.Exists(savedReview)
                    || capture.Last?.PixelWidth != 40 || capture.Last.PixelHeight != 30)
                    throw new InvalidOperationException("Ctrl+Enter did not accept review without cropping (pending crop: " + pendingCrop + ").");
                var savedReviewImage = ImageOutput.Load(savedReview);
                if (savedReviewImage.PixelWidth != 40 || savedReviewImage.PixelHeight != 30)
                    throw new InvalidOperationException("Review acceptance exported the pending crop instead of the current image.");
                report.Add("Ctrl+Enter accepts review with Crop active, pending crop " + pendingCrop + " PASS");
            }
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
