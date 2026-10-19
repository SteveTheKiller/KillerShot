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
            Point origin = _pane.TranslatePoint(new Point(), element);
            FrameworkElement? face = card();
            Thickness halo = face?.Margin ?? new Thickness();
            double faceWidth = face is { ActualWidth: > 0 } ? face.ActualWidth : Math.Max(0, popupSize.Width - halo.Left - halo.Right);
            double x = alignRight ? Math.Max(8, _pane.ActualWidth - faceWidth - 8) - halo.Left : 8 - halo.Left;
            return [new CustomPopupPlacement(new Point(origin.X + x, origin.Y + 8 - halo.Top), PopupPrimaryAxis.None)];
        };
}
