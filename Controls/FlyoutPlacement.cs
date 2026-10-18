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
}
