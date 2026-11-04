using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KillerShot.Controls;

/// <summary>The play mark over a recording's poster frame, so a clip reads as a clip among screenshots.</summary>
internal static class VideoMark
{
    internal static FrameworkElement Wrap(FrameworkElement image, bool isVideo)
    {
        if (!isVideo) return image;
        var grid = new Grid();
        grid.Children.Add(image);
        var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0) };
        var mark = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = glyph, IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 } };
        AutomationProperties.SetAutomationId(mark, "VideoAlbumMark");
        mark.SetResourceReference(AutomationProperties.NameProperty, "Str_Capture_Video");
        grid.Children.Add(mark);
        return grid;
    }

    /// <summary>The same mark painted into a card face that is width by height in its own units,
    /// for the carousel, which draws its cards rather than laying out elements.</summary>
    internal static DrawingGroup Draw(DrawingGroup face, double width, double height, bool isVideo)
    {
        if (!isVideo) return face;
        using var dc = face.Append();
        double radius = Math.Min(width, height) * .14;
        var center = new Point(width / 2, height / 2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), radius * .06), center, radius, radius);
        var glyph = new FormattedText("", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons"), radius * .82, Brushes.White, 1);
        dc.DrawText(glyph, new Point(center.X - glyph.Width / 2 + radius * .06, center.Y - glyph.Height / 2));
        return face;
    }
}
