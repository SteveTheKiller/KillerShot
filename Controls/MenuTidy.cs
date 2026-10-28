using System.Windows;
using System.Windows.Controls;

namespace KillerShot.Controls;

/// <summary>
/// A menu shows only what can be done right now: an item that would be greyed out is left out,
/// a submenu with nothing left in it goes too, and the separators those leave at either end or
/// side by side are dropped (Steve, 2026-10-27).
/// </summary>
internal static class MenuTidy
{
    /// <summary>Hides what cannot be used. Run it after the menu is built, each time it opens.</summary>
    public static void HideInactive(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Items.Count > 0) HideInactive(item);
            bool usable = item.IsEnabled && (item.Items.Count == 0 || item.Items.OfType<MenuItem>().Any(child => child.Visibility == Visibility.Visible));
            item.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        }
        bool previousWasItem = false;
        Separator? pending = null;
        foreach (var element in menu.Items.OfType<FrameworkElement>())
        {
            if (element is Separator separator)
            {
                separator.Visibility = Visibility.Collapsed;
                if (previousWasItem) pending = separator;
                previousWasItem = false;
            }
            else if (element.Visibility == Visibility.Visible)
            {
                if (pending != null) { pending.Visibility = Visibility.Visible; pending = null; }
                previousWasItem = true;
            }
        }
    }
}
