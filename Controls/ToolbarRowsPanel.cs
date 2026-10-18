using System.Windows;
using System.Windows.Controls;

namespace KillerShot.Controls;

/// <summary>One ordered toolbar group, balanced across one or two rows.</summary>
public sealed class ToolbarRowsPanel : Panel
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(int),
        typeof(ToolbarRowsPanel), new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public int Rows { get => (int)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    private int _split;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        foreach (var child in children) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _rowHeight = children.Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        double total = children.Sum(c => c.DesiredSize.Width), before = 0, best = total;
        _split = children.Length;
        if (Rows == 2)
            for (int i = 1; i < children.Length; i++)
            {
                before += children[i - 1].DesiredSize.Width;
                double width = Math.Max(before, total - before);
                if (width < best) { best = width; _split = i; }
            }
        return new Size(best, _rowHeight * (Rows == 2 ? 2 : 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int index = 0;
        double x = 0, y = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (index++ == _split) { x = 0; y = _rowHeight; }
            child.Arrange(new Rect(x, y, child.DesiredSize.Width, _rowHeight));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }
}
