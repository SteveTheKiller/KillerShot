using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-editor-objects &lt;folder&gt;. Several marks can be
/// selected and moved, duplicated, reordered and deleted together, each as one undo step; placed
/// text and callouts can be edited again; automation can add and remove marks from the selection.</summary>
internal static class SelfTestEditorObjects
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-editor-objects") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

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
            var image = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 300 * 4], 400 * 4);
            image.Freeze();
            var canvas = new EditorCanvas();
            window = new Window { Content = canvas, Width = 500, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            var doc = new EditorDocument(image);
            canvas.Document = doc;
            window.UpdateLayout();
            void Place(ToolKind tool, string? text = null)
            {
                canvas.SetTool(tool);
                canvas.PlaceMark();
                if (text != null) { canvas.Children.OfType<TextBox>().Single().Text = text; canvas.CommitText(); }
            }
            Place(ToolKind.Rectangle);
            Place(ToolKind.Step);
            Place(ToolKind.Text, "Before");
            Place(ToolKind.Callout, "Look");
            if (doc.State.Items.Count != 4) throw new InvalidOperationException("Setup did not place four marks.");
            canvas.SetTool(ToolKind.Select);

            // Select all, nudge: every mark moves, and one undo puts them all back.
            var before = doc.State.Items.Select(a => a.Bounds).ToList();
            if (!canvas.SelectAll() || canvas.SelectedMarks.Count != 4) throw new InvalidOperationException("Ctrl+A did not select every mark.");
            canvas.Nudge(new Vector(5, 0));
            if (!doc.State.Items.Select(a => a.Bounds).Zip(before).All(p => Math.Abs(p.First.X - p.Second.X - 5) < 0.01))
                throw new InvalidOperationException("Nudging the selection did not move every mark.");
            if (canvas.SelectedMarks.Count != 4) throw new InvalidOperationException("Nudging lost the multiple selection.");
            doc.Undo();
            if (!doc.State.Items.Select(a => a.Bounds).SequenceEqual(before)) throw new InvalidOperationException("One undo did not restore the whole group.");
            report.Add("select all, group nudge, single undo PASS");

            // Duplicate: copies are added and selected; a copied step takes the next number.
            canvas.SelectAll();
            canvas.DuplicateSelected();
            if (doc.State.Items.Count != 8 || canvas.SelectedMarks.Count != 4) throw new InvalidOperationException("Duplicate did not copy and select four marks.");
            var steps = doc.State.Items.OfType<StepAnnotation>().Select(s => s.Number).ToList();
            if (!steps.SequenceEqual([1, 2])) throw new InvalidOperationException("The duplicated step repeated a number: " + string.Join(",", steps));
            doc.Undo();
            if (doc.State.Items.Count != 4) throw new InvalidOperationException("One undo did not remove every duplicate.");
            report.Add("duplicate with renumbered step, single undo PASS");

            // Reorder: the rectangle goes to the front, then back to the bottom.
            var rect = doc.State.Items[0];
            canvas.Select(rect);
            canvas.Reorder(+1, true);
            if (!ReferenceEquals(doc.State.Items[^1], canvas.Selected) || canvas.Selected is not ShapeAnnotation) throw new InvalidOperationException("Bring to front did not move the rectangle last.");
            canvas.Reorder(-1, false);
            if (doc.State.Items[^2] is not ShapeAnnotation) throw new InvalidOperationException("Send backward did not move it down one layer.");
            canvas.Reorder(-1, true);
            if (doc.State.Items[0] is not ShapeAnnotation) throw new InvalidOperationException("Send to back did not move it first.");
            report.Add("bring to front, send backward, send to back PASS");

            // Edit placed text again: same layer, one undo restores the old words.
            var text = doc.State.Items.OfType<TextAnnotation>().Single();
            int layer = doc.State.Items.IndexOf(text);
            canvas.Select(text);
            if (!canvas.EditSelectedText()) throw new InvalidOperationException("Placed text could not be edited.");
            var box = canvas.Children.OfType<TextBox>().Single();
            if (box.Text != "Before") throw new InvalidOperationException("The edit box did not start with the mark's text.");
            box.Text = "After";
            canvas.CommitText();
            if (doc.State.Items[layer] is not TextAnnotation { Text: "After" }) throw new InvalidOperationException("The edited text did not replace the mark in place.");
            doc.Undo();
            if (doc.State.Items[layer] is not TextAnnotation { Text: "Before" }) throw new InvalidOperationException("Undo did not restore the old text.");
            var callout = doc.State.Items.OfType<CalloutAnnotation>().Single();
            canvas.Select(callout);
            canvas.EditSelectedText();
            canvas.Children.OfType<TextBox>().Single().Text = "Look here";
            canvas.CommitText();
            if (!doc.State.Items.OfType<CalloutAnnotation>().Any(c => c.Text == "Look here" && c.Tip == callout.Tip)) throw new InvalidOperationException("The callout edit lost its text or point.");
            report.Add("text and callout re-edit in place with undo PASS");

            // Automation: add two marks to the selection, then delete both as one step.
            canvas.Select(null);
            var peer = UIElementAutomationPeer.CreatePeerForElement(canvas);
            var marks = peer.GetChildren()!.Where(c => c.GetPattern(PatternInterface.SelectionItem) != null).ToList();
            ((ISelectionItemProvider)marks[0].GetPattern(PatternInterface.SelectionItem)!).AddToSelection();
            ((ISelectionItemProvider)marks[1].GetPattern(PatternInterface.SelectionItem)!).AddToSelection();
            if (((ISelectionProvider)peer.GetPattern(PatternInterface.Selection)!).GetSelection().Length != 2) throw new InvalidOperationException("Automation could not build a two-mark selection.");
            canvas.DeleteSelected();
            if (doc.State.Items.Count != 2) throw new InvalidOperationException("Deleting the selection did not remove both marks.");
            doc.Undo();
            if (doc.State.Items.Count != 4) throw new InvalidOperationException("One undo did not bring both marks back.");
            report.Add("automation multi-select and group delete with single undo PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            File.WriteAllLines(Path.Combine(folder, "editor-objects-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
