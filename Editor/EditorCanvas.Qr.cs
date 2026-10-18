using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using KillerTools.Qr;

namespace KillerShot.Editor;

public sealed partial class EditorCanvas
{
    private IReadOnlyList<QrResult> _qrResults = [];
    private readonly List<Button> _qrBadges = [];
    public event Action<QrResult, Button>? QrClicked;

    private static readonly DependencyProperty QrOutlineBrushProperty = DependencyProperty.Register(
        "QrOutlineBrush", typeof(Brush), typeof(EditorCanvas), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public void SetQrCodes(IReadOnlyList<QrResult> results)
    {
        foreach (var badge in _qrBadges) Children.Remove(badge);
        _qrBadges.Clear();
        _qrResults = results;
        SetResourceReference(QrOutlineBrushProperty, "PrimaryBrush");
        foreach (var result in results)
        {
            var badge = new Button { Content = "\uED14", Tag = "on", ToolTip = result.Text, Width = 20, Height = 20 };
            badge.SetResourceReference(StyleProperty, "RailButton");
            AutomationProperties.SetAutomationId(badge, "QrBadge-" + _qrBadges.Count);
            AutomationProperties.SetName(badge, "QR " + (_qrBadges.Count + 1));
            badge.Click += (_, e) => { e.Handled = true; QrClicked?.Invoke(result, badge); };
            _qrBadges.Add(badge);
            Children.Add(badge);
        }
        PositionQrBadges();
        InvalidateVisual();
    }

    private void PositionQrBadges()
    {
        for (int i = 0; i < _qrBadges.Count; i++)
        {
            if (_qrResults[i].Corners.Count == 0) continue;
            var point = _qrResults[i].Corners[0];
            SetLeft(_qrBadges[i], Math.Max(0, point.X * Scale));
            SetTop(_qrBadges[i], Math.Max(0, point.Y * Scale - 20));
        }
    }

    private void DrawQrOutlines(DrawingContext drawing)
    {
        var pen = new Pen((Brush)GetValue(QrOutlineBrushProperty), 1);
        foreach (var code in _qrResults)
        {
            if (code.Corners.Count != 4) continue;
            var outline = new StreamGeometry();
            using (var path = outline.Open())
            {
                path.BeginFigure(new Point(code.Corners[0].X * Scale, code.Corners[0].Y * Scale), false, true);
                foreach (var point in code.Corners.Skip(1)) path.LineTo(new Point(point.X * Scale, point.Y * Scale), true, false);
            }
            drawing.DrawGeometry(null, pen, outline);
        }
    }
}
