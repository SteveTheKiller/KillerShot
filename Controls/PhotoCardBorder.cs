using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerShot.Controls;

/// <summary>A photo frame with an accent outer pixel and a darker inner pixel.</summary>
public class PhotoCardBorder : Border
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (BorderBrush is not SolidColorBrush brush || brush.Color.A == 0 || SystemParameters.HighContrast
            || ActualWidth <= 4 || ActualHeight <= 4) return;
        var pen = new Pen(new SolidColorBrush(Darker(brush.Color)), 1);
        double corner = Math.Max(0, CornerRadius.TopLeft - 1.5);
        drawingContext.DrawRoundedRectangle(null, pen, new Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3), corner, corner);
    }

    private static Color Darker(Color color) => Color.FromArgb(color.A,
        (byte)(color.R * .65), (byte)(color.G * .65), (byte)(color.B * .65));

    internal static void DrawFrame(DrawingGroup drawing, Rect bounds, double corner, Color accent, double thickness)
    {
        thickness = Math.Min(thickness, Math.Min(bounds.Width, bounds.Height) / 4);
        for (int ring = 0; ring < 2; ring++)
        {
            double inset = thickness * (.25 + ring * .5);
            var rect = bounds;
            rect.Inflate(-inset, -inset);
            var color = ring == 0 || SystemParameters.HighContrast ? accent : Darker(accent);
            drawing.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(color), thickness / 2),
                new RectangleGeometry(rect, Math.Max(0, corner - inset), Math.Max(0, corner - inset))));
        }
    }
}
