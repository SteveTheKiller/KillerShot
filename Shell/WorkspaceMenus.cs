using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Controls;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// Right-click menus that belong to where the click lands: the sidebar's menu
/// holds sidebar options, the carousel's holds carousel options, and the content area's holds the
/// views. Each attaches to its own area only, never to a dock host, because the hosts wrap the
/// content and their menu would answer clicks there too.
/// </summary>
public partial class MainWindow
{
    private void InitWorkspaceMenus()
    {
        // The menu fills before it opens, not after: an empty menu opened and filled late can
        // measure as nothing and never show.
        void Attach(FrameworkElement element, Action<ContextMenu> fill, bool card = false)
        {
            var menu = card ? ShotCards.Menu() : new ContextMenu();
            element.ContextMenu = menu;
            // A right-click or the menu key fills it in ContextMenuOpening; Opened fills only a
            // menu opened some other way, so a filled menu is never rebuilt while it shows.
            bool filled = false;
            element.ContextMenuOpening += (_, e) =>
            {
                fill(menu);
                filled = menu.Items.OfType<MenuItem>().Any(item => item.Visibility == Visibility.Visible);
                if (!filled) e.Handled = true;
            };
            menu.Opened += (_, _) =>
            {
                if (!filled) fill(menu);
                filled = false;
            };
        }
        RailStack.Background = Brushes.Transparent;
        Attach(RailStack, FillSidebarMenu, card: true);
        if (_navigationStrip != null) Attach(_navigationStrip, FillSidebarMenu, card: true);
        if (_strip != null) Attach(_strip, FillCarouselMenu, card: true);
        Attach(Library, FillContentMenu, card: true);
        Attach(FooterBar, FillContentMenu, card: true);
        NavigationDock.LayoutChanged += PlaceRailButtons;
        PlaceRailButtons();
    }

    private void PlaceRailButtons()
    {
        if (NavigationDock.IsHorizontal)
        {
            Grid.SetColumn(RailStack, 2);
            RailStack.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    /// <summary>The glyph of a dock side's tile: an arrow toward that edge.</summary>
    internal static string SideGlyph(DockSide side) => side switch
    {
        DockSide.Left => "\uE72B", DockSide.Right => "\uE72A", DockSide.Top => "\uE74A", _ => "\uE74B",
    };

    /// <summary>A dock side's tile, filled while the dock is on that side.</summary>
    private CardTile SideTile(DockSide side, string key, string arrow, DockHost host)
        => new(SideGlyph(side), Loc(key), "Alt+Shift+" + arrow, () => host.DockTo(side)) { Checked = host.Side == side };

    /// <summary>The collections sidebar's card: which side and how compact, then which collection and how it looks.</summary>
    private void FillSidebarMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_navigationStrip is not { } strip) return;
        // A collection's own card names it; the sidebar's card is about the sidebar.
        menu.Items.Add(ShotCards.Header(menu, Loc("Str_Sidebar_Title")));
        var look = new List<CardTile>
        {
            ShotCards.Flyout("\uE8B7", Loc("Str_Strip_Collection"), null, ShotCards.RowsOf(strip.SourceMenu(CollectionPickerBtn))),
            ShotCards.Flyout("\uE8A9", Loc("Str_CollectionPreview"), null, ShotCards.RowsOf(strip.PreviewMenu())),
            new("\uE8FD", Loc("Str_Sidebar_Compact"), null, () => strip.Compact = !strip.Compact) { Checked = strip.Compact, KeepsOpen = true },
        };
        var sides = StripSides.Where(s => s.Side is DockSide.Left or DockSide.Right)
            .Select(s => SideTile(s.Side, s.Key, s.Arrow, NavigationDock));
        menu.Items.Add(ShotCards.Grid(menu, sides.Append(look[2]), look.Take(2)));
    }

    /// <summary>The photo carousel's card: where it sits, and the cards under its pictures.</summary>
    private void FillCarouselMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(ShotCards.Header(menu, Loc("Str_Carousel_Title"), _strip?.SourceCaption));
        var sides = StripSides.Where(s => s.Side is DockSide.Top or DockSide.Bottom)
            .Select(s => SideTile(s.Side, s.Key, s.Arrow, StripDock));
        menu.Items.Add(ShotCards.Grid(menu, sides, [ViewInfoCards.Tile()]));
    }

    /// <summary>The content area's card: the album views, and info cards, dimmed where the view
    /// does not show them.</summary>
    private void FillContentMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var views = new[]
        {
            ("shelves", "\uE8F1", "Str_Lib_ViewShelves", "F5"), ("flow", "\uE7C5", "Str_Lib_ViewFlow", "F6"),
            ("grid", "\uE80A", "Str_Lib_ViewGrid", "F7"), ("timeline", "\uE823", "Str_Lib_ViewTimeline", "F8"),
            ("files", "\uE8B7", "Str_Lib_ViewFiles", "F9"),
        };
        string? shown = Library.IsVisible ? views.FirstOrDefault(v => v.Item1 == Library.CurrentView).Item3 : null;
        menu.Items.Add(ShotCards.Header(menu, Loc("Str_Btn_Library"), shown == null ? null : Loc(shown)));
        var tiles = views.Select(v => new CardTile(v.Item2, Loc(v.Item3), v.Item4, () => ShowLibraryView(v.Item1))
            { Checked = Library.IsVisible && Library.CurrentView == v.Item1 });
        bool cards = Library.IsVisible && Library.CurrentView is "shelves" or "flow";
        menu.Items.Add(ShotCards.Grid(menu, tiles, [ViewInfoCards.Tile(cards)]));
    }
}
