using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-auto-redact &lt;new folder&gt;: clicks Auto redact in a real editor
/// window, works the Redact dialog through its own controls, presses Find, and checks the boxes
/// that land: what they cover, their color and feather in the rendered picture, one undo step,
/// and the saved marks. Then a capture arrives with Scan every capture on. Opens windows, so it
/// runs on a lab machine.
/// </summary>
internal static class SelfTestAutoRedact
{
    private static readonly Color Fill = Color.FromRgb(0x20, 0x40, 0x80);
    private const int Feather = 4;

    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-auto-redact", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    internal static BitmapSource Picture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1800, 220));
            var face = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText("Contact jane.doe@example.com today", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 44, Brushes.Black, 1), new Point(40, 30));
            dc.DrawText(new FormattedText("Phone (555) 123-4567 Card 4539 1488 0343 6467", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 44, Brushes.Black, 1), new Point(40, 120));
        }
        var rtb = new RenderTargetBitmap(1800, 220, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("The report folder must be new.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            if (TextRecognition.CreateRecognizer() == null)
            {
                report.Add("no recognition language installed: SKIPPED");
                code = 0;
                return;
            }
            RedactSettings.Fill = Fill;
            RedactSettings.Feather = Feather;
            // A toolbar layout saved by 1.1.1, before Auto redact existed: the button must still
            // reach the bar, beside Redact, and not only exist as an object.
            Settings.Set("ToolbarLayout", "capture,record,open,save,copy,copytext,print;undo,redo,select,pen,highlighter,arrow,line,rectangle,ellipse,fill,text,step,callout,redact,crop,image,color,width;");
            var doc = new EditorDocument(Picture());
            App.Instance.SetDocument(doc);
            window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000, Width = 1400, Height = 760, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(300);
            window.UpdateLayout();
            var bar = VisualTreeHelper.GetParent(window.ToolRedactBtn) as Panel;
            int redactAt = bar?.Children.IndexOf(window.ToolRedactBtn) ?? -1;
            if (!window.AutoRedactBtn.IsVisible || bar == null || redactAt < 0 || bar.Children.IndexOf(window.AutoRedactBtn) != redactAt + 1)
                throw new InvalidOperationException($"Auto redact is not on the toolbar beside Redact (visible={window.AutoRedactBtn.IsVisible}).");
            report.Add("toolbar: Auto redact shows beside Redact with a 1.1.1 saved layout PASS");

            // The toolbar button opens the dialog modally; work it from a queued callback.
            var done = new TaskCompletionSource<string>();
            _ = window.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    RedactDialog? dialog = null;
                    for (int i = 0; i < 200 && (dialog = Application.Current.Windows.OfType<RedactDialog>().FirstOrDefault(w => w.IsLoaded)) == null; i++) await Task.Delay(10);
                    if (dialog == null) throw new InvalidOperationException("Auto redact did not open the Redact dialog.");
                    foreach (var box in new[] { dialog.EmailCheck, dialog.CardCheck, dialog.PhoneCheck, dialog.SsnCheck, dialog.IpCheck, dialog.SecretCheck })
                        box.IsChecked = false;
                    if (dialog.FindButton.IsEnabled) throw new InvalidOperationException("Find stayed enabled with nothing checked.");
                    dialog.EmailCheck.IsChecked = true;
                    dialog.PhoneCheck.IsChecked = true;
                    if (RedactSettings.Kinds != (RedactKinds.Email | RedactKinds.Phone)) throw new InvalidOperationException($"The checkboxes saved {RedactSettings.Kinds}.");
                    dialog.FindButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    for (int i = 0; i < 1000 && dialog.IsLoaded; i++) await Task.Delay(10);
                    done.TrySetResult(dialog.IsLoaded ? "the dialog stayed open: " + dialog.StatusText.Text : "");
                    if (dialog.IsLoaded) dialog.Close();
                }
                catch (Exception error) { done.TrySetException(error); }
            });
            window.AutoRedactBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string problem = await done.Task;
            if (problem.Length > 0) throw new InvalidOperationException(problem);
            report.Add("dialog: checkboxes save, Find needs a kind, Find closes after adding boxes PASS");

            var boxes = doc.State.Items.OfType<ShapeAnnotation>().Where(s => s.Kind == ShapeKind.Redact).ToList();
            report.Add($"boxes: {string.Join("; ", boxes.Select(b => b.PixelCells.ToString(CultureInfo.InvariantCulture)))}");
            report.Add($"status: {window.StatusText.Text}");
            if (await TextRecognition.ReadWordsAsync(Picture()) is { } read)
                report.Add("ocr: " + string.Join(" / ", read.Select(l => string.Join(" | ", l.Select(w => w.Text + "@" + w.Box.ToString(CultureInfo.InvariantCulture))))));
            if (boxes.Count < 2) throw new InvalidOperationException("Expected boxes over the email and the phone number.");
            if (!boxes.All(b => b.RedactFill == Fill && b.Feather == Feather)) throw new InvalidOperationException("The boxes do not carry the chosen color and feather.");
            // Both labels, Contact and Phone, start at x = 40 and end before x = 150.
            if (boxes.Any(b => b.PixelCells.Left < 150))
                throw new InvalidOperationException("A box covers a label.");
            if (!boxes.Any(b => b.PixelCells.Top < 100) || !boxes.Any(b => b.PixelCells.Top > 100))
                throw new InvalidOperationException("The email line and the phone line were not both boxed.");
            if (boxes.Any(b => b.PixelCells.Top > 100 && b.PixelCells.Right > 700))
                throw new InvalidOperationException("The card number was boxed although cards were unchecked.");
            report.Add("boxes cover the email and phone, not the label or the unchecked card PASS");

            var picture = doc.Render();
            var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            var px = new byte[width * height * 4];
            converted.CopyPixels(px, width * 4, 0);
            Color At(int x, int y) { int i = (y * width + x) * 4; return Color.FromArgb(px[i + 3], px[i + 2], px[i + 1], px[i]); }
            foreach (var b in boxes)
            {
                var r = b.PixelCells;
                for (int y = (int)r.Top; y < (int)r.Bottom; y++)
                    for (int x = (int)r.Left; x < (int)r.Right; x++)
                        if (At(x, y) != Fill) throw new InvalidOperationException($"Pixel {x},{y} inside a box is {At(x, y)}, not the box color.");
                int ox = (int)r.Left + (int)(r.Width / 2), oy = (int)r.Top - 1;
                var ring = At(ox, oy);
                if (ring == Fill || ring == Colors.White) throw new InvalidOperationException($"The feather ring at {ox},{oy} is {ring}, not a blend.");
                var outside = At(ox, (int)r.Top - Feather - 1);
                if (outside != Colors.White && outside.R + outside.G + outside.B < 600) throw new InvalidOperationException($"The feather reaches past {Feather} pixels: {outside}.");
            }
            report.Add("rendered boxes are solid inside and fade only outward PASS");

            var restored = MarksFormat.Read(MarksFormat.Write(doc.State.Items)) ?? throw new InvalidOperationException("The marks did not read back.");
            if (!restored.OfType<ShapeAnnotation>().Where(s => s.Kind == ShapeKind.Redact).All(s => s.RedactFill == Fill && s.Feather == Feather))
                throw new InvalidOperationException("Saved marks lost the box color or feather.");
            report.Add("saved marks keep color and feather PASS");

            doc.Undo();
            if (doc.State.Items.OfType<ShapeAnnotation>().Any(s => s.Kind == ShapeKind.Redact)) throw new InvalidOperationException("One undo did not remove every found box.");
            report.Add("one undo removes them all PASS");

            // Scan every capture: a capture arrives through the app's own capture handler.
            RedactSettings.ScanEveryCapture = true;
            // The edited picture is unsaved, so replacing it asks first; discard it.
            App.Instance.PresentDocumentReplacement = _ => true;
            var onCaptured = typeof(App).GetMethod("OnCaptured", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("App.OnCaptured was not found.");
            onCaptured.Invoke(App.Instance, [Picture(), DateTime.Now, null, false]);
            var captured = App.Instance.Document ?? throw new InvalidOperationException("The capture did not open.");
            for (int i = 0; i < 500 && !captured.State.Items.OfType<ShapeAnnotation>().Any(); i++) await Task.Delay(10);
            int found = captured.State.Items.OfType<ShapeAnnotation>().Count(s => s.Kind == ShapeKind.Redact);
            if (found < 2) throw new InvalidOperationException($"Scan every capture added {found} boxes.");
            report.Add($"scan every capture boxed {found} areas on arrival PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            report.Add(code == 0 ? "result=ok" : "result=error");
            File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
            window?.Close();
            CaptureLibrary.Close();
            App.Instance.Shutdown(code);
        }
    }
}
