using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerShot.Controls;

internal static class QrMark
{
    internal static FrameworkElement Wrap(FrameworkElement image, bool hasQr)
    {
        if (!hasQr) return image;
        var grid = new Grid();
        grid.Children.Add(image);
        var glyph = new TextBlock { Text = "\uED14", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mark = new Border { Width = 22, Height = 22, Background = Brushes.Black, CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3),
            Child = glyph, IsHitTestVisible = false };
        AutomationProperties.SetAutomationId(mark, "QrAlbumMark");
        AutomationProperties.SetName(mark, "QR");
        grid.Children.Add(mark);
        return grid;
    }

    internal static DrawingGroup Drawing(Brush image, double width, double height, bool hasQr)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(image, null, new RectangleGeometry(new Rect(0, 0, width, height))));
        if (!hasQr) return drawing;
        using var dc = drawing.Append();
        double size = Math.Min(22, Math.Min(width, height) / 3);
        double x = width - size - 3;
        dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(x, 3, size, size), 3, 3);
        var text = new FormattedText("\uED14", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons"), size * 14 / 22, Brushes.White, 1);
        dc.DrawText(text, new Point(x + (size - text.Width) / 2, 3 + (size - text.Height) / 2));
        return drawing;
    }
}
