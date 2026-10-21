using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-gesture-lifetime &lt;folder&gt;. A crop or
/// pen drag started on one picture is cancelled when another picture replaces it, so it can
/// never land on the new one.</summary>
internal static class SelfTestGestureLifetime
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-gesture-lifetime") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static BitmapSource Image()
    {
        var bmp = BitmapSource.Create(200, 150, 96, 96, PixelFormats.Bgra32, null, new byte[200 * 150 * 4], 200 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void Press(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });

    private static void Release(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        Window? window = null;
        try
        {
            var canvas = new EditorCanvas();
            window = new Window { Content = canvas, Width = 400, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            foreach (var tool in new[] { ToolKind.Crop, ToolKind.Pen, ToolKind.Redact })
            {
                canvas.SetTool(tool);
                var a = new EditorDocument(Image());
                var b = new EditorDocument(Image());
                canvas.Document = a;
                window.UpdateLayout();
                Press(canvas);
                canvas.Document = b;
                bool stillDragging = canvas.CancelGesture();
                Press(canvas);
                canvas.Document = b;
                Release(canvas);
                await Task.Delay(20);
                if (stillDragging) throw new InvalidOperationException($"{tool}: the old picture's drag was still active on the new one.");
                if (b.Dirty || b.PixelWidth != 200 || !b.State.Items.IsEmpty || a.Dirty)
                    throw new InvalidOperationException($"{tool}: a drag started on the old picture changed the new one.");
                report.Add($"{tool}: replacement mid-drag leaves both pictures unchanged PASS");
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            File.WriteAllLines(Path.Combine(folder, "gesture-lifetime-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
