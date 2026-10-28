using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerShot.Controls;

/// <summary>
/// Right-click menus open right where the pointer is: the visible card's corner touches the
/// click, below and to the right when it fits, otherwise above or to the left, never stranded.
///
/// WPF's own MousePoint placement flips a tall menu above the pointer when it does not fit below,
/// and the ContextMenu style's halo correction (HorizontalOffset -17, VerticalOffset -13) is only
/// right for the downward case, so a flipped menu's visible bottom sat about 30px above the click
/// with the card running far up the screen (Steve, 2026-10-27). This takes over every menu that
/// opens at the pointer and places its card by its real halo in each direction.
///
/// Rail and toolbar flyouts are not touched: they set their own Custom placement through
/// FlyoutPlacement, and only menus still on MousePoint when they first open are taken over.
/// </summary>
internal static class PointerMenus
{
    private sealed class Click { public Point? Cursor; }

    private static readonly ConditionalWeakTable<ContextMenu, Click> Taken = new();

    /// <summary>Once at startup, after the kit's resources are in place.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnClosed));
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || !ReferenceEquals(e.OriginalSource, menu)) return;
        // Every open that arrives on WPF's pointer placement, the first and every later one: WPF
        // puts a reused menu back on MousePoint, with the style's offsets, for its next open. A
        // menu opened from the keyboard comes as Center and keeps it (audit 2026-10-27).
        if (menu.Placement != PlacementMode.MousePoint) return;
        // Only a menu that belongs to an element on screen: the tray icon's menu has no target, and
        // a Custom placement without one is measured from the screen's top-left corner, which put
        // it there (2026-10-27). Those keep WPF's own placement at the pointer.
        if (!Takes(menu)) return;
        var click = Taken.GetOrCreateValue(menu);
        menu.HorizontalOffset = menu.VerticalOffset = 0;
        menu.CustomPopupPlacementCallback = (popupSize, _, _) => Place(menu, click, popupSize);
        // Already open at WPF's spot; changing the mode places it again before its fade-in shows.
        menu.Placement = PlacementMode.Custom;
    }

    /// <summary>Whether a menu can be placed against the pointer: it needs an on-screen element
    /// to measure from.</summary>
    internal static bool Takes(ContextMenu menu) =>
        menu.PlacementTarget is UIElement target && PresentationSource.FromVisual(target) != null;

    private static void OnClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && ReferenceEquals(e.OriginalSource, menu) && Taken.TryGetValue(menu, out var click))
            click.Cursor = null;
    }

    private static CustomPopupPlacement[] Place(ContextMenu menu, Click click, Size popupSize)
    {
        // The pointer as it was when the menu opened, so a later re-layout never chases the mouse.
        if (click.Cursor == null && GetCursorPos(out var cursor)) click.Cursor = new Point(cursor.X, cursor.Y);
        if (click.Cursor is not Point at || menu.PlacementTarget is not UIElement target
            || PresentationSource.FromVisual(target)?.CompositionTarget is not { } device)
            return [new CustomPopupPlacement(new Point(), PopupPrimaryAxis.None)];

        Vector pointer = at - target.PointToScreen(new Point());
        // The card's transparent shadow halo, read from the template so a theme change follows it.
        Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement root ? root.Margin : new Thickness(22, 18, 22, 26);
        return Candidates(pointer, popupSize, halo, device.TransformToDevice.M11, device.TransformToDevice.M22)
            .Select(point => new CustomPopupPlacement(point, PopupPrimaryAxis.None)).ToArray();
    }

    /// <summary>Where the popup can go, best first, in device pixels from the target's corner: its
    /// visible card's corner two pixels from the pointer, below right, above right, below left,
    /// above left. WPF takes the first that fits the screen, and otherwise the one that shows most.</summary>
    internal static Point[] Candidates(Vector pointer, Size popupSize, Thickness halo, double scaleX, double scaleY)
    {
        const double gap = 2;
        double rightX = pointer.X - (halo.Left - gap) * scaleX, leftX = pointer.X - popupSize.Width + (halo.Right - gap) * scaleX;
        double downY = pointer.Y - (halo.Top - gap) * scaleY, upY = pointer.Y - popupSize.Height + (halo.Bottom - gap) * scaleY;
        return [new(rightX, downY), new(rightX, upY), new(leftX, downY), new(leftX, upY)];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
