using System.Windows;
using System.Windows.Controls;

namespace KillerShot.Controls;

/// <summary>Marks the collection target under a capture drag. Sidebar rows show it through their
/// template, the same wash as hover; headers, shelves and picker entries get that brush as their
/// background, and lose it again when the drag moves on.</summary>
public static class DropHighlight
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(DropHighlight), new PropertyMetadata(false, OnIsActiveChanged));

    private static readonly DependencyProperty SavedBackgroundProperty = DependencyProperty.RegisterAttached(
        "SavedBackground", typeof(object), typeof(DropHighlight), new PropertyMetadata(null));

    // The element had no local background of its own.
    private static readonly object NoLocalValue = new();

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    private static void OnIsActiveChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is ListBoxItem) return;
        DependencyProperty? background = element switch
        {
            Panel => Panel.BackgroundProperty,
            Border => Border.BackgroundProperty,
            Control => Control.BackgroundProperty,
            _ => null,
        };
        if (background == null) return;
        if ((bool)e.NewValue)
        {
            object local = element.ReadLocalValue(background);
            element.SetValue(SavedBackgroundProperty, local == DependencyProperty.UnsetValue ? NoLocalValue : local);
            ((FrameworkElement)element).SetResourceReference(background, "RowHoverBrush");
        }
        else
        {
            object saved = element.GetValue(SavedBackgroundProperty);
            element.ClearValue(SavedBackgroundProperty);
            if (saved == null) return;
            if (saved == NoLocalValue) element.ClearValue(background);
            else element.SetValue(background, saved);
        }
    }
}
