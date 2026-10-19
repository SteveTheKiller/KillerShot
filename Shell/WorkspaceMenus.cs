using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Layout;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private FrameworkElement[] _railOrder = [];

    private void InitWorkspaceMenus()
    {
        _railOrder = RailStack.Children.OfType<FrameworkElement>().ToArray();
        void Attach(FrameworkElement element)
        {
            var menu = new ContextMenu();
            element.ContextMenu = menu;
            menu.Opened += (_, _) => FillWorkspaceMenu(menu);
        }
        RailStack.Background = Brushes.Transparent;
        Attach(RailStack);
        Attach(StripDock);
        Attach(NavigationDock);
        if (_strip != null) Attach(_strip);
        if (_navigationStrip != null) Attach(_navigationStrip);
        Attach(Library);
        Attach(FooterBar);
        NavigationDock.LayoutChanged += ApplyRailPreferences;
        RailStack.LayoutUpdated += (_, _) => PlaceRailButtons();
        ApplyRailPreferences();
    }

    private void ApplyRailPreferences()
    {
        bool reversed = Settings.GetBool("RailReversed", false);
        var order = reversed ? _railOrder.Reverse() : _railOrder.AsEnumerable();
        RailStack.Children.Clear();
        foreach (var button in order) RailStack.Children.Add(button);
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

    private void FillWorkspaceMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        void Add(string key, Action action, bool? selected = null, string gesture = "")
        {
            var item = new MenuItem { Header = Loc(key), IsCheckable = selected.HasValue, IsChecked = selected == true, InputGestureText = gesture };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Str_Btn_Library", ShowLibrarySettings);
        if (_strip != null)
        {
            var source = new MenuItem { Header = Loc("Str_Strip_Collection") };
            var choices = (_navigationStrip ?? _strip).SourceMenu(CollectionPickerBtn);
            while (choices.Items.Count > 0) { object item = choices.Items[0]; choices.Items.RemoveAt(0); source.Items.Add(item); }
            menu.Items.Add(source);
            menu.Items.Add((_navigationStrip ?? _strip).PreviewMenu());
            foreach (var (side, key, _) in StripSides)
            {
                if (Library.IsVisible && side is DockSide.Top or DockSide.Bottom) continue;
                var target = side;
                var host = side is DockSide.Left or DockSide.Right ? NavigationDock : StripDock;
                Add(key, () => host.DockTo(target), host.Side == side);
            }
            var activeHost = menu.PlacementTarget == _strip || menu.PlacementTarget == StripDock ? StripDock : NavigationDock;
            Add("Str_Dock_Minimize", () => { if (activeHost.IsMinimized) activeHost.Restore(); else activeHost.Minimize(); }, activeHost.IsMinimized);
        }
        menu.Items.Add(new Separator());
        Add("Str_Rail_Reverse", () => { Settings.SetBool("RailReversed", !Settings.GetBool("RailReversed", false)); ApplyRailPreferences(); }, Settings.GetBool("RailReversed", false));
        menu.Items.Add(new Separator());
        foreach (var (view, key) in new[] { ("shelves", "Str_Lib_ViewShelves"), ("flow", "Str_Lib_ViewFlow"), ("grid", "Str_Lib_ViewGrid"), ("timeline", "Str_Lib_ViewTimeline") })
        {
            string target = view;
            Add(key, () => ShowLibraryView(target), Library.IsVisible && Library.CurrentView == view,
                view switch { "shelves" => "F5", "flow" => "F6", "grid" => "F7", _ => "F8" });
        }
        bool stripTarget = menu.PlacementTarget == _strip || menu.PlacementTarget == StripDock || !Library.IsVisible;
        bool? rolodex = stripTarget ? StripDock.IsHorizontal ? false : _strip?.SideView == "rolodex" ? true : null
            : Library.CurrentView is "shelves" or "flow" ? false : null;
        if (rolodex is bool sideView)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ViewInfoCards.Menu(sideView));
        }
    }
}
