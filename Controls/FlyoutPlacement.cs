using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerShot.Controls;

/// <summary>Places rail menus beside the button that opens them, toward the content.</summary>
internal static class FlyoutPlacement
{
    private static FrameworkElement? _pane;
    internal static void UsePane(FrameworkElement pane) => _pane = pane;

    private static PlacementMode Side(UIElement button)
    {
        if (_pane == null || button is not FrameworkElement element) return PlacementMode.Bottom;
        var point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), _pane);
        if (point.X <= 0) return PlacementMode.Right;
        if (point.X >= _pane.ActualWidth) return PlacementMode.Left;
        return point.Y <= 0 ? PlacementMode.Bottom : PlacementMode.Top;
    }

    internal static void Attach(ContextMenu menu, UIElement button)
    {
        menu.PlacementTarget = button;
        menu.Placement = Side(button);
        menu.CustomPopupPlacementCallback = null;
        menu.HorizontalOffset = menu.Placement == PlacementMode.Left ? -4 : menu.Placement == PlacementMode.Right ? 4 : 0;
        menu.VerticalOffset = menu.Placement == PlacementMode.Top ? -4 : menu.Placement == PlacementMode.Bottom ? 4 : 0;
    }

    internal static void AttachInsidePane(ContextMenu menu, UIElement button, bool alignRight = false)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Custom;
        menu.HorizontalOffset = menu.VerticalOffset = 0;
        menu.CustomPopupPlacementCallback = PanePlacement(button, alignRight,
            () => menu.Template?.FindName("MenuRoot", menu) as FrameworkElement);
    }

    internal static void AttachInsidePane(Popup popup, UIElement button, bool alignRight = false)
    {
        popup.PlacementTarget = button;
        popup.Placement = PlacementMode.Custom;
        popup.HorizontalOffset = popup.VerticalOffset = 0;
        popup.CustomPopupPlacementCallback = PanePlacement(button, alignRight,
            () => popup.Child as FrameworkElement);
    }

    private static CustomPopupPlacementCallback PanePlacement(UIElement button, bool alignRight, Func<FrameworkElement?> card)
        => (popupSize, targetSize, offset) =>
        {
            if (_pane == null || button is not FrameworkElement element)
                return [new CustomPopupPlacement(new Point(0, targetSize.Height + 8), PopupPrimaryAxis.None)];
            FrameworkElement? face = card();
            Vector faceOffset = new();
            double faceWidth = popupSize.Width;
            if (face != null && PresentationSource.FromVisual(face) is { RootVisual: System.Windows.Media.Visual root, CompositionTarget: { } target })
            {
                var transform = face.TransformToAncestor(root);
                Point left = target.TransformToDevice.Transform(transform.Transform(new Point()));
                Point right = target.TransformToDevice.Transform(transform.Transform(new Point(face.ActualWidth, 0)));
                faceOffset = (Vector)left;
                faceWidth = right.X - left.X;
            }
            Point desired = _pane.PointToScreen(new Point(alignRight ? _pane.ActualWidth - 8 : 8, 8));
            Vector position = desired - element.PointToScreen(new Point()) - faceOffset;
            if (alignRight) position.X -= faceWidth;
            return [new CustomPopupPlacement((Point)position, PopupPrimaryAxis.None)];
        };
}
