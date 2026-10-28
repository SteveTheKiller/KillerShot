using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// Right-click menus that belong to where the click lands (Steve, 2026-10-27): the sidebar's menu
/// holds sidebar options, the carousel's holds carousel options, and the content area's holds the
/// views. Each attaches to its own area only, never to a dock host, because the hosts wrap the
/// content and their menu would answer clicks there too.
/// </summary>
public partial class MainWindow
{
    private void InitWorkspaceMenus()
    {
        void Attach(FrameworkElement element, Action<ContextMenu> fill)
        {
            var menu = new ContextMenu();
            element.ContextMenu = menu;
            menu.Opened += (_, _) => { fill(menu); MenuTidy.HideInactive(menu); };
        }
        RailStack.Background = Brushes.Transparent;
        Attach(RailStack, FillSidebarMenu);
        if (_navigationStrip != null) Attach(_navigationStrip, FillSidebarMenu);
        if (_strip != null) Attach(_strip, FillCarouselMenu);
        Attach(Library, FillContentMenu);
        Attach(FooterBar, FillContentMenu);
        NavigationDock.LayoutChanged += PlaceRailButtons;
        RailStack.LayoutUpdated += (_, _) => PlaceRailButtons();
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

    private MenuItem AddItem(ContextMenu menu, string key, Action action, bool? selected = null, string gesture = "")
    {
        var item = new MenuItem { Header = Loc(key), IsCheckable = selected.HasValue, IsChecked = selected == true, InputGestureText = gesture };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    /// <summary>The collections sidebar: which collection, how collections look, and which side.</summary>
    private void FillSidebarMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_navigationStrip is not { } strip) return;
        var source = new MenuItem { Header = Loc("Str_Strip_Collection") };
        var choices = strip.SourceMenu(CollectionPickerBtn);
        while (choices.Items.Count > 0) { object item = choices.Items[0]; choices.Items.RemoveAt(0); source.Items.Add(item); }
        menu.Items.Add(source);
        menu.Items.Add(strip.PreviewMenu());
        AddItem(menu, "Str_Sidebar_Compact", () => strip.Compact = !strip.Compact, strip.Compact);
        menu.Items.Add(new Separator());
        foreach (var (side, key, arrow) in StripSides.Where(s => s.Side is DockSide.Left or DockSide.Right))
        {
            var target = side;
            AddItem(menu, key, () => NavigationDock.DockTo(target), NavigationDock.Side == side, "Alt+Shift+" + arrow);
        }
    }

    /// <summary>The photo carousel: where it sits, and the cards under its pictures.</summary>
    private void FillCarouselMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var (side, key, arrow) in StripSides.Where(s => s.Side is DockSide.Top or DockSide.Bottom))
        {
            var target = side;
            AddItem(menu, key, () => StripDock.DockTo(target), StripDock.Side == side, "Alt+Shift+" + arrow);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(ViewInfoCards.Menu());
    }

    /// <summary>The content area: the album views, and info cards where the view shows them.</summary>
    private void FillContentMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var (view, key) in new[] { ("shelves", "Str_Lib_ViewShelves"), ("flow", "Str_Lib_ViewFlow"), ("grid", "Str_Lib_ViewGrid"), ("timeline", "Str_Lib_ViewTimeline") })
        {
            string target = view;
            AddItem(menu, key, () => ShowLibraryView(target), Library.IsVisible && Library.CurrentView == view,
                view switch { "shelves" => "F5", "flow" => "F6", "grid" => "F7", _ => "F8" });
        }
        if (Library.IsVisible && Library.CurrentView is "shelves" or "flow")
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ViewInfoCards.Menu());
        }
    }
}
