using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestCarouselPerspective
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-carousel-perspective") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.Enabled = false;
        App.InitializeKit();
        int code = 1;
        var report = new List<string>();
        try
        {
            CheckFlow(folder, report, false);
            CheckFlow(folder, report, true);
            var strip = CreateFlow(false);
            strip.PerspectiveZoomEnabled = false;
            int selected = strip.SelectedIndex;
            strip.HandleWheel(120, ModifierKeys.Control);
            Check(strip.SelectedIndex == selected - 1, "The collection strip's existing wheel behavior changed.");
            report.Add("Collection-strip wheel behavior unchanged PASS");
            CheckAlbumHosts(report);
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        CaptureLibrary.Close();
        File.WriteAllLines(Path.Combine(folder, "carousel-perspective-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    private static CoverFlow CreateFlow(bool shelf)
    {
        var items = Enumerable.Range(0, 9).Select(i => new CaptureItem(i, DateTime.Today, 180, 120,
            "region", "Perspective " + i, null, null, null)).ToArray();
        var flow = new CoverFlow
        {
            PerspectiveZoomEnabled = true,
            ViewHeight = shelf ? 1.52 : 1.60,
            ViewCenterY = shelf ? .255 : .50,
            ThumbnailSource = item => Thumbnail((int)item.Id),
            Items = items,
            SelectedIndex = 4,
        };
        flow.Items = items;
        flow.Measure(new Size(1000, shelf ? 200 : 420));
        flow.Arrange(new Rect(0, 0, 1000, shelf ? 200 : 420));
        flow.UpdateLayout();
        return flow;
    }

    private static void CheckFlow(string folder, List<string> report, bool shelf)
    {
        string name = shelf ? "shelf" : "album";
        var flow = CreateFlow(shelf);
        int selected = flow.SelectedIndex;
        var before = Bounds(flow);
        Render(flow, Path.Combine(folder, name + "-before.png"));
        flow.UpdateHover(new Point(before.Center.X + before.Center.Width / 2, before.Center.Y + before.Center.Height / 2));
        Check(flow.Cursor == Cursors.Hand, "Hovering a clickable center image does not show a hand cursor.");
        flow.UpdateHover(new Point(2, 2));
        Check(flow.Cursor == Cursors.Arrow, "Hovering an empty gap does not restore the arrow cursor.");
        flow.HandleWheel(0, ModifierKeys.Control);
        Check(Bounds(flow) == before && flow.SelectedIndex == selected, "A zero wheel delta changed geometry or selection.");
        for (int i = 0; i < 10; i++) Check(flow.HandleWheel(120, ModifierKeys.Control), "Ctrl+wheel was not consumed.");
        flow.UpdateLayout();
        Check(flow.SelectedIndex == selected, "Ctrl+wheel navigated away from the selected image.");
        var closer = Bounds(flow);
        Render(flow, Path.Combine(folder, name + "-closer.png"));
        flow.UpdateHover(new Point(closer.Center.X + closer.Center.Width / 2, closer.Center.Y + closer.Center.Height / 2));
        Check(flow.Cursor == Cursors.Hand, "The zoomed image lost its clickable hand cursor.");
        flow.UpdateHover(new Point(closer.Side.X + closer.Side.Width / 2, closer.Side.Y + closer.Side.Height / 2));
        Check(flow.Cursor == Cursors.Hand, "A side image does not show a hand cursor.");
        Check(closer.Center.Height > before.Center.Height * 1.15, "Ctrl+wheel did not enlarge the selected image.");
        Check(closer.Side.Height < before.Side.Height * .90, "Ctrl+wheel did not shrink the side image.");
        Check(closer.Center.Top >= -1 && closer.Center.Bottom <= flow.ActualHeight + 1,
            "The enlarged selected image is clipped vertically.");
        report.Add($"{name}: center {before.Center.Width:F1}x{before.Center.Height:F1} -> {closer.Center.Width:F1}x{closer.Center.Height:F1}; side height {before.Side.Height:F1} -> {closer.Side.Height:F1} PASS");
        for (int i = 0; i < 10; i++) flow.HandleWheel(-120, ModifierKeys.Control);
        flow.UpdateLayout();
        var restored = Bounds(flow);
        Check(Math.Abs(restored.Center.Height - before.Center.Height) < .001
            && Math.Abs(restored.Side.Height - before.Side.Height) < .001, "Reverse wheel did not restore the default geometry.");
        for (int i = 0; i < 100; i++) flow.HandleWheel(120, ModifierKeys.Control);
        flow.UpdateLayout();
        var maximum = Bounds(flow);
        flow.HandleWheel(120, ModifierKeys.Control);
        flow.UpdateLayout();
        Check(Bounds(flow) == maximum, "Perspective zoom has no upper bound.");
        for (int i = 0; i < 100; i++) flow.HandleWheel(-120, ModifierKeys.Control);
        flow.UpdateLayout();
        var minimum = Bounds(flow);
        flow.HandleWheel(-120, ModifierKeys.Control);
        flow.UpdateLayout();
        Check(Bounds(flow) == minimum && minimum.Center.Height < before.Center.Height
            && minimum.Side.Height > before.Side.Height, "Negative zoom is not bounded or does not reverse the perspective.");
        Check(flow.SelectedIndex == selected, "Perspective zoom changed selection at a bound.");
        flow.HandleWheel(120, ModifierKeys.None);
        Check(flow.SelectedIndex == selected - 1, "Ordinary wheel navigation changed.");
        using (var source = new HwndSource(new HwndSourceParameters("Carousel key fixture") { WindowStyle = 0, Width = 1, Height = 1 }))
        {
            flow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(flow.SelectedIndex == selected, "Right-arrow navigation changed.");
            long? activated = null;
            flow.ItemActivated += item => activated = item.Id;
            flow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(activated == flow.SelectedItem?.Id, "Enter no longer opens the selected image.");
        }
        report.Add(name + ": reversible default geometry, both bounds, stable selection and ordinary wheel PASS");
        report.Add(name + ": image/gap hover cursors and keyboard navigation/activation PASS");
    }

    private static void CheckAlbumHosts(List<string> report)
    {
        Check(CaptureLibrary.OpenAuto(), "The isolated album did not open.");
        for (int i = 0; i < 9; i++) CaptureLibrary.AddNow(Thumbnail(i), DateTime.Today, "region", "Perspective " + i, null);
        var library = new LibraryView();
        var big = (CoverFlow)library.FindName("BigFlow");
        Check(big.PerspectiveZoomEnabled, "The album's full carousel did not enable perspective zoom.");
        library.TestSet("shelves", "date", "");
        library.Measure(new Size(1000, 600));
        library.Arrange(new Rect(0, 0, 1000, 600));
        library.UpdateLayout();
        var shelves = Descendants(library).OfType<CoverFlow>().Where(flow => !ReferenceEquals(flow, big)).ToArray();
        Check(shelves.Length > 0 && shelves.All(flow => flow.PerspectiveZoomEnabled),
            "The album's shelf carousels did not enable perspective zoom.");
        report.Add("Actual album full carousel and shelves enable perspective zoom PASS");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static (Rect Center, Rect Side) Bounds(CoverFlow flow)
    {
        var viewport = (Viewport3D)VisualTreeHelper.GetChild(flow, 0);
        var camera = (PerspectiveCamera)viewport.Camera;
        var scene = (Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
        var cards = scene.Children.OfType<Model3DGroup>().OrderBy(card => Math.Abs(card.Transform.Value.OffsetX)).ToArray();
        Rect Project(Model3DGroup card)
        {
            var face = (GeometryModel3D)card.Children[0];
            var points = ((MeshGeometry3D)face.Geometry).Positions.Select(card.Transform.Transform);
            double focal = flow.ActualWidth / (2 * Math.Tan(camera.FieldOfView * Math.PI / 360));
            var bounds = Rect.Empty;
            foreach (var point in points)
            {
                double depth = camera.Position.Z - point.Z;
                Check(depth > 0, "A card crossed the camera plane.");
                bounds.Union(new Point(flow.ActualWidth / 2 + (point.X - camera.Position.X) * focal / depth,
                    flow.ActualHeight / 2 - (point.Y - camera.Position.Y) * focal / depth));
            }
            return bounds;
        }
        return (Project(cards[0]), Project(cards[1]));
    }

    private static BitmapSource Thumbnail(int index)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(index == 4 ? Brushes.DarkOrange : Brushes.SteelBlue, new Pen(Brushes.White, 4), new Rect(2, 2, 176, 116));
            drawing.DrawText(new FormattedText((index + 1).ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 42, Brushes.White, 1), new Point(70, 30));
        }
        var image = new RenderTargetBitmap(180, 120, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static void Render(CoverFlow flow, string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, flow.ActualWidth, flow.ActualHeight);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, bounds);
        }
        var image = new RenderTargetBitmap((int)flow.ActualWidth, (int)flow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Render(flow);
        ImageOutput.SavePng(image, path);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
