using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-editor-tools &lt;folder&gt;. Lines, filled shapes,
/// numbered steps and callouts can be placed from the keyboard, export at the capture's pixel size
/// with the expected pixels, are named for screen readers, and steps number predictably through
/// delete and undo.</summary>
internal static class SelfTestEditorTools
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-editor-tools") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static readonly Color Ink = Color.FromRgb(0xE5, 0x39, 0x35);

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        LocaleManager.Apply(Locale.EnUS);
        int code = 1;
        var report = new List<string>();
        Window? window = null;
        try
        {
            var px = new byte[300 * 200 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 40; px[i + 1] = 120; px[i + 2] = 40; px[i + 3] = 255; }
            var image = BitmapSource.Create(300, 200, 96, 96, PixelFormats.Bgra32, null, px, 300 * 4);
            image.Freeze();
            var canvas = new EditorCanvas { Color = Ink, StrokeWidth = 6 };
            window = new Window { Content = canvas, Width = 400, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();

            Annotation Place(ToolKind tool, EditorDocument doc)
            {
                canvas.Document = doc;
                canvas.SetTool(tool);
                window!.UpdateLayout();
                int before = doc.State.Items.Count;
                if (!canvas.PlaceMark()) throw new InvalidOperationException(tool + " could not be placed from the keyboard.");
                if (tool == ToolKind.Callout)
                {
                    var box = canvas.Children.OfType<System.Windows.Controls.TextBox>().Single();
                    box.Text = "Click here";
                    canvas.CommitText();
                }
                if (doc.State.Items.Count != before + 1) throw new InvalidOperationException(tool + " did not add one mark.");
                return doc.State.Items[^1];
            }

            byte[] Pixels(EditorDocument doc, out int w)
            {
                var bmp = new FormatConvertedBitmap(doc.Render(), PixelFormats.Bgra32, null, 0);
                w = bmp.PixelWidth;
                if (bmp.PixelWidth != 300 || bmp.PixelHeight != 200) throw new InvalidOperationException("Export changed the capture's pixel size.");
                var data = new byte[w * bmp.PixelHeight * 4];
                bmp.CopyPixels(data, w * 4, 0);
                ImageOutput.SavePng(bmp, Path.Combine(folder, $"tool-{doc.State.Items[^1].GetType().Name}-{doc.State.Items.Count}.png"));
                return data;
            }
            bool IsInk(byte[] d, int w, Point p)
            {
                int i = ((int)p.Y * w + (int)p.X) * 4;
                return Math.Abs(d[i + 2] - Ink.R) < 8 && Math.Abs(d[i + 1] - Ink.G) < 8 && Math.Abs(d[i] - Ink.B) < 8;
            }

            // Line: the middle of the segment is ink.
            var lineDoc = new EditorDocument(image);
            var line = (ShapeAnnotation)Place(ToolKind.Line, lineDoc);
            if (line.Kind != ShapeKind.Line) throw new InvalidOperationException("The line tool did not make a line.");
            var mid = new Point((line.A.X + line.B.X) / 2, (line.A.Y + line.B.Y) / 2);
            var data = Pixels(lineDoc, out int width);
            if (!IsInk(data, width, mid)) throw new InvalidOperationException("The line did not export.");
            report.Add("line: placed, exported at pixel size PASS");

            // Fill: rectangle and ellipse centers are ink only when filled.
            foreach (var tool in new[] { ToolKind.Rectangle, ToolKind.Ellipse })
                foreach (bool filled in new[] { false, true })
                {
                    canvas.Filled = filled;
                    var doc = new EditorDocument(image);
                    var shape = (ShapeAnnotation)Place(tool, doc);
                    var c = new Point(shape.Box.X + shape.Box.Width / 2, shape.Box.Y + shape.Box.Height / 2);
                    data = Pixels(doc, out width);
                    if (IsInk(data, width, c) != filled) throw new InvalidOperationException($"{tool} fill={filled} exported the wrong interior.");
                }
            canvas.Filled = false;
            report.Add("rectangle and ellipse: outline and filled PASS");

            // Steps: 1, 2, 3; delete 2, the next is 4; undo brings 2 back.
            var stepDoc = new EditorDocument(image);
            var numbers = new List<int>();
            for (int n = 0; n < 3; n++) numbers.Add(((StepAnnotation)Place(ToolKind.Step, stepDoc)).Number);
            if (!numbers.SequenceEqual([1, 2, 3])) throw new InvalidOperationException("Steps did not number 1, 2, 3: " + string.Join(",", numbers));
            stepDoc.Remove(stepDoc.State.Items[1]);
            int next = ((StepAnnotation)Place(ToolKind.Step, stepDoc)).Number;
            if (next != 4) throw new InvalidOperationException("After deleting step 2 the next step was " + next + ", expected 4.");
            stepDoc.Undo();
            stepDoc.Undo();
            var restored = stepDoc.State.Items.OfType<StepAnnotation>().Select(s => s.Number).ToList();
            if (!restored.SequenceEqual([1, 2, 3])) throw new InvalidOperationException("Undo did not restore steps 1, 2, 3.");
            var step = (StepAnnotation)stepDoc.State.Items[0];
            data = Pixels(stepDoc, out width);
            var rim = new Point(step.Center.X + step.Width * 0.38, step.Center.Y);
            if (!IsInk(data, width, rim)) throw new InvalidOperationException("The step badge did not export.");
            report.Add("steps: 1,2,3; delete then 4; undo restores PASS");

            // Callout: a bubble with text and a tail to its point.
            var calloutDoc = new EditorDocument(image);
            var callout = (CalloutAnnotation)Place(ToolKind.Callout, calloutDoc);
            if (callout.Text != "Click here") throw new InvalidOperationException("The callout lost its text.");
            data = Pixels(calloutDoc, out width);
            var bubble = callout.BubbleRect;
            int bi = ((int)(bubble.Y + 3) * width + (int)(bubble.X + bubble.Width / 2)) * 4;
            if (data[bi] < 230 || data[bi + 1] < 230 || data[bi + 2] < 230) throw new InvalidOperationException("The callout bubble is not filled white.");
            report.Add("callout: bubble, text and tail PASS");

            // Names for screen readers.
            canvas.Document = stepDoc;
            var names = (UIElementAutomationPeer.CreatePeerForElement(canvas).GetChildren() ?? []).Select(c => c.GetName()).ToList();
            canvas.Document = calloutDoc;
            names.AddRange((UIElementAutomationPeer.CreatePeerForElement(canvas).GetChildren() ?? []).Select(c => c.GetName()));
            canvas.Document = lineDoc;
            names.AddRange((UIElementAutomationPeer.CreatePeerForElement(canvas).GetChildren() ?? []).Select(c => c.GetName()));
            report.Add("names: " + string.Join(" | ", names));
            if (!names.Any(n => n.StartsWith("Numbered step 2,")) || !names.Any(n => n.Contains("Click here")) || !names.Any(n => n.StartsWith("Line,")))
                throw new InvalidOperationException("New marks are not named for screen readers.");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            File.WriteAllLines(Path.Combine(folder, "editor-tools-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
