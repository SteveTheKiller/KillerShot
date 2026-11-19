using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerShot.Shell;

/// <summary>
/// What the self-tests read on a family tile card (ShotCards): tiles are found by their automation
/// name, the way a screen reader finds them; a filled tile is on; Delete for the one thing a card
/// is about sits in the header. NoTextRows is the card rule that everything is a tile.
/// </summary>
internal static class SelfTestCards
{
    /// <summary>The card's tiles in order, without the header's Delete.</summary>
    public static IEnumerable<Button> Tiles(ItemsControl card) =>
        ShotCards.Buttons(card).Where(button => !Equals(button.Tag, ShotCards.HeaderDeleteTag));

    public static string Name(Button tile) => AutomationProperties.GetName(tile);

    public static string[] Names(ItemsControl card) => Tiles(card).Select(Name).ToArray();

    // A tile drops a trailing "..." from its caption, so a menu string still finds its tile.
    private static bool Named(Button tile, string name) => Trim(Name(tile)) == Trim(name);
    private static string Trim(string text) => text.TrimEnd('.', '\u2026').TrimEnd();

    public static Button? Find(ItemsControl card, string name) => Tiles(card).FirstOrDefault(tile => Named(tile, name));

    public static Button Tile(ItemsControl card, string name) =>
        Find(card, name) ?? throw new InvalidOperationException($"The card has no '{name}' tile. It has: {string.Join(" | ", Names(card))}");

    /// <summary>A tile, or the header's Delete, by name: what a click on the card can run.</summary>
    public static Button Command(ItemsControl card, string name) =>
        ShotCards.Buttons(card).FirstOrDefault(button => Named(button, name))
        ?? throw new InvalidOperationException($"The card has no '{name}' command. It has: {string.Join(" | ", ShotCards.Buttons(card).Select(Name))}");

    /// <summary>Whether an on/off tile is on (the kit fills it while on).</summary>
    public static bool IsOn(Button tile) => tile.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue;

    public static void Click(Button tile) => tile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tile));

    /// <summary>The header's Delete, or null when the card has none.</summary>
    public static Button? HeaderDelete(ItemsControl card) =>
        ShotCards.Buttons(card).FirstOrDefault(button => Equals(button.Tag, ShotCards.HeaderDeleteTag));

    /// <summary>The rows a flyout tile opens.</summary>
    public static IList<object> FlyoutRows(ItemsControl card, string name) =>
        ShotCards.GetFlyoutRows(Tile(card, name)) ?? throw new InvalidOperationException($"The '{name}' tile opens no list.");

    /// <summary>A flyout row by its header text.</summary>
    public static MenuItem Row(ItemsControl card, string tile, string header) =>
        FlyoutRows(card, tile).OfType<MenuItem>().SingleOrDefault(row => Equals(row.Header, header) || Convert.ToString(row.Header) == header)
        ?? throw new InvalidOperationException($"The '{tile}' tile has no '{header}' row.");

    /// <summary>The card's title (the header slot's automation name).</summary>
    public static string? Title(ItemsControl card) =>
        card.Items.OfType<MenuItem>().FirstOrDefault() is { } first ? AutomationProperties.GetName(first) : null;

    /// <summary>Null when the card is only a header and tile grids; otherwise what breaks the rule.</summary>
    public static string? TextRows(ItemsControl card)
    {
        int index = 0;
        foreach (var item in card.Items)
        {
            if (item is Separator) return "a separator";
            if (item is not MenuItem row) return "a " + item.GetType().Name;
            string name = AutomationProperties.GetName(row) is { Length: > 0 } named ? named : Convert.ToString(row.Header) ?? "";
            if (row.Header is not FrameworkElement) return "the text row '" + row.Header + "'";
            if (row.Items.Count > 0) return "the submenu '" + name + "'";
            if (row.Icon != null || !string.IsNullOrEmpty(row.InputGestureText) || row.IsCheckable) return "the menu row '" + name + "'";
            // After the header, every slot is a row of tiles.
            if (index++ > 0 && !ShotCards.ButtonsIn(row).Any()) return "the text row '" + name + "'";
        }
        return null;
    }

    /// <summary>Throws when the card holds a text row, or when it has no tiles.</summary>
    public static void CheckCard(ItemsControl card, string what)
    {
        if (TextRows(card) is { } problem) throw new InvalidOperationException($"The {what} card holds {problem}; a card has tiles only.");
        if (!Tiles(card).Any()) throw new InvalidOperationException($"The {what} card has no tiles.");
    }

    /// <summary>Throws unless the card's header carries Delete under this name.</summary>
    public static Button CheckHeaderDelete(ItemsControl card, string name, string what)
    {
        var delete = HeaderDelete(card) ?? throw new InvalidOperationException($"The {what} card has no Delete in its header.");
        if (!Named(delete, name)) throw new InvalidOperationException($"The {what} card's header Delete is named '{Name(delete)}', not '{name}'.");
        if (Tiles(card).Any(tile => Named(tile, name))) throw new InvalidOperationException($"The {what} card has Delete as a tile as well as in its header.");
        return delete;
    }
}
