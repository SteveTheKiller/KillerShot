using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using KillerFamily.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot's family tile cards (the KillerPDF 1.9 card rules, KillerUI CardBuilder.TileGroups):
/// 1. Three tiles across, every tile an MDL2 glyph over a short caption that wraps inside the tile.
/// 2. Related tiles are one group on a row of their own; a group longer than a row goes on to the next,
///    and an unfilled row stays blank on the right.
/// 3. Most used first, destructive last; Delete on a card for one thing sits in the header.
/// 4. A tile that only sometimes applies stays in its place, dimmed, so the rows never shift.
/// 5. No text rows: everything is a tile, or a flyout tile for a list such as Move to.
/// Grid lays the tiles out with the kit's CardBuilder.TileGroups.
/// </summary>
internal static class ShotCards
{
    /// <summary>The card width for three tiles across (the menu adds its 22px halo on each side).</summary>
    public const double Width = 276;

    /// <summary>Tiles across a card row.</summary>
    public const int Columns = 3;

    /// <summary>The tag on a card header's Delete button, for the self-tests.</summary>
    public const string HeaderDeleteTag = "carddelete";

    /// <summary>The rows a flyout tile opens, kept on its button so the self-tests can reach them.</summary>
    public static readonly DependencyProperty FlyoutRowsProperty =
        DependencyProperty.RegisterAttached("FlyoutRows", typeof(IList<object>), typeof(ShotCards));

    public static IList<object>? GetFlyoutRows(DependencyObject d) => (IList<object>?)d.GetValue(FlyoutRowsProperty);

    private static readonly DependencyProperty DeleteButtonProperty =
        DependencyProperty.RegisterAttached("DeleteButton", typeof(Button), typeof(ShotCards));

    private static readonly DependencyProperty WatchedProperty =
        DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(ShotCards), new PropertyMetadata(false));

    private static string Loc(string key) => LocaleManager.Loc(key);

    // Flyout tiles whose rows are only built when the tile is clicked (Recent loads thumbnails).
    private static readonly ConditionalWeakTable<CardTile, Action<ItemsControl>> LazyFills = new();

    /// <summary>A new card at the three-across width.</summary>
    public static ContextMenu Menu() => CardBuilder.Menu(Width);

    /// <summary>Gives a menu made elsewhere (XAML, or a control's own ContextMenu) the card look and width.</summary>
    public static ContextMenu Prepare(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
        menu.MinWidth = menu.MaxWidth = Width + 44;
        return menu;
    }

    /// <summary>
    /// The card's header: its title, an optional dim line under it, and, on a card for one thing, that
    /// thing's Delete as an icon button at the right end (the KillerPDF mark card's WithHeaderDelete).
    /// </summary>
    public static MenuItem Header(ContextMenu card, string title, string? meta = null,
        string? deleteName = null, string? deleteKeys = null, Action? delete = null, bool deleteEnabled = true)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        text.Children.Add(head);
        if (!string.IsNullOrEmpty(meta))
        {
            var sub = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            text.Children.Add(sub);
        }
        var dock = new DockPanel { LastChildFill = true };
        if (delete != null && deleteName != null)
        {
            var button = new Button
            {
                Content = "\uE74D", Tag = HeaderDeleteTag, IsEnabled = deleteEnabled,
                ToolTip = CardBuilder.Tip(deleteName, deleteKeys),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                // KillerShot's toolbar style carries no icon font, so the glyph names the font the tiles draw with.
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarButton");
            AutomationProperties.SetName(button, deleteName);
            if (!string.IsNullOrEmpty(deleteKeys)) AutomationProperties.SetAcceleratorKey(button, deleteKeys);
            ToolTipService.SetShowOnDisabled(button, true);
            var run = delete;
            button.Click += (_, _) => { card.IsOpen = false; run(); };
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
            card.SetValue(DeleteButtonProperty, button);
            if (!(bool)card.GetValue(WatchedProperty))
            {
                // A card opened from the keyboard starts on its first tile, never on Delete: Enter there would delete.
                card.SetValue(WatchedProperty, true);
                card.Opened += (_, _) => card.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (ReferenceEquals(Keyboard.FocusedElement, card.GetValue(DeleteButtonProperty))) FirstTile(card)?.Focus();
                }));
                // A card opened by the Apps key can keep focus on the card itself, and the first arrow would then
                // step onto Delete, the first control in it. That first step goes to the first tile instead.
                card.PreviewKeyDown += (_, e) =>
                {
                    if (!ReferenceEquals(Keyboard.FocusedElement, card)) return;
                    if (e.Key is not (Key.Down or Key.Up or Key.Left or Key.Right or Key.Tab or Key.Home)) return;
                    if (FirstTile(card) is { } first) { first.Focus(); e.Handled = true; }
                };
            }
        }
        dock.Children.Add(text);
        var slot = CardBuilder.Slot(dock, new Thickness(4, 2, 4, 6));
        AutomationProperties.SetName(slot, title);
        return slot;
    }

    /// <summary>
    /// The tile grid: each group on rows of its own, three across. Empty groups are skipped. Put it on the
    /// card after the header; it is the card's only other item.
    /// </summary>
    public static MenuItem Grid(ContextMenu card, params IEnumerable<CardTile>[] groups)
    {
        // The kit lays out the grid (CardBuilder.TileGroups); blank cells end a group's row. Cells line up with
        // the tiles in order, blanks skipped, so each flyout tile gets its rows and lazy fill wired here.
        var lists = groups.Select(g => g.ToList()).Where(g => g.Count > 0).ToList();
        var strip = CardBuilder.TileGroups(card, Loc("Str_Card_Quick"), lists, Columns);
        var tiles = lists.SelectMany(g => g).ToList();
        if (strip.Child is Panel cells)
        {
            int next = 0;
            foreach (UIElement cell in cells.Children)
            {
                if (cell is Border { Child: null }) continue;
                if (next >= tiles.Count) break;
                var tile = tiles[next++];
                var button = cell as Button ?? (cell as System.Windows.Controls.Grid)?.Children.OfType<Button>().FirstOrDefault();
                if (tile.Flyout is not { } rows || button == null) continue;
                button.SetValue(FlyoutRowsProperty, rows);
                if (!LazyFills.TryGetValue(tile, out var fill)) continue;
                // The kit opens the flyout with the placeholder row first; this fills it in place.
                ItemsControl? host = null;
                button.Click += (_, _) =>
                {
                    host ??= (rows.FirstOrDefault() as FrameworkElement)?.Parent as ItemsControl;
                    if (host == null) return;
                    fill(host);
                    button.SetValue(FlyoutRowsProperty, host.Items.Cast<object>().ToList());
                    // The kit's flyout picked its focus row when it opened, before the fill replaced the rows, so
                    // a keyboard open would have left focus on the card. The first real row takes it here.
                    if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
                        host.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                            host.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEnabled)?.Focus());
                };
            }
        }
        var slot = CardBuilder.Slot(strip, new Thickness(0));
        AutomationProperties.SetName(slot, Loc("Str_Card_Quick"));
        return slot;
    }

    /// <summary>A tile that opens a list beside itself. The rows are taken out of whatever held them.</summary>
    public static CardTile Flyout(string glyph, string caption, string? keys, IEnumerable<object> rows, bool enabled = true)
    {
        var list = rows.ToList();
        return new CardTile(glyph, caption, keys, null, false, enabled && list.Count > 0) { Flyout = list };
    }

    /// <summary>A flyout tile whose rows are built when it is clicked, not when the card opens; until then
    /// it holds one dimmed placeholder row.</summary>
    public static CardTile LazyFlyout(string glyph, string caption, string placeholder, Action<ItemsControl> fill, bool enabled = true)
    {
        var tile = new CardTile(glyph, caption, null, null, false, enabled)
            { Flyout = new List<object> { new MenuItem { Header = placeholder, IsEnabled = false } } };
        LazyFills.Add(tile, fill);
        return tile;
    }

    /// <summary>The rows of a MenuItem built as a submenu, ready for a flyout tile.</summary>
    public static IEnumerable<object> RowsOf(ItemsControl source)
    {
        var rows = source.Items.Cast<object>().ToList();
        source.Items.Clear();
        return rows;
    }

    /// <summary>Every tile and header button on a card, in order.</summary>
    public static IEnumerable<Button> Buttons(ItemsControl card) => card.Items.OfType<MenuItem>().SelectMany(ButtonsIn);

    /// <summary>The tiles or header buttons of one card item, in order.</summary>
    public static IEnumerable<Button> ButtonsIn(MenuItem item) => Descendants(item.Header as DependencyObject).OfType<Button>();

    /// <summary>The first tile that can be used.</summary>
    public static Button? FirstTile(ItemsControl card) =>
        Buttons(card).FirstOrDefault(b => !Equals(b.Tag, HeaderDeleteTag) && b.IsEnabled);

    /// <summary>Whether any tile or header button on the card can be used.</summary>
    public static bool AnyEnabled(ItemsControl card) => Buttons(card).Any(b => b.IsEnabled);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }
}
