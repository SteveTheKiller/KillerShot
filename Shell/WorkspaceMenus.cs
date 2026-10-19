using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Layout;
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
        if (_strip != null) Attach(_strip);
        Attach(Library);
        Attach(FooterBar);
        StripDock.LayoutChanged += ApplyRailPreferences;
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
        if (StripDock.IsHorizontal)
        {
            Grid.SetColumn(RailStack, Settings.GetBool("RailAtRight", true) ? 2 : 1);
            RailStack.HorizontalAlignment = Settings.GetBool("RailAtRight", true) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    private void FillWorkspaceMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        void Add(string key, Action action, bool? selected = null)
        {
            var item = new MenuItem { Header = Loc(key), IsCheckable = selected.HasValue, IsChecked = selected == true };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Str_Btn_Library", ShowLibrarySettings);
        if (_strip != null && _doc != null)
        {
            var source = new MenuItem { Header = Loc("Str_Strip_Collection") };
            var choices = _strip.SourceMenu(CollectionPickerBtn);
            while (choices.Items.Count > 0) { object item = choices.Items[0]; choices.Items.RemoveAt(0); source.Items.Add(item); }
            menu.Items.Add(source);
            foreach (var (side, key, _) in StripSides)
            {
                var target = side;
                Add(key, () => StripDock.DockTo(target), StripDock.Side == side);
            }
            Add("Str_Dock_Minimize", () => { if (StripDock.IsMinimized) StripDock.Restore(); else StripDock.Minimize(); }, StripDock.IsMinimized);
        }
        menu.Items.Add(new Separator());
        Add("Str_Rail_Flip", () =>
        {
            if (Library.IsVisible)
            {
                Settings.SetBool("LibraryRailAtRight", !Settings.GetBool("LibraryRailAtRight", false));
                SyncStripPlacement(library: true);
            }
            else if (StripDock.IsHorizontal) Settings.SetBool("RailAtRight", !Settings.GetBool("RailAtRight", true));
            else StripDock.DockTo(StripDock.Side == DockSide.Left ? DockSide.Right : DockSide.Left);
            ApplyRailPreferences();
        });
        Add("Str_Rail_Reverse", () => { Settings.SetBool("RailReversed", !Settings.GetBool("RailReversed", false)); ApplyRailPreferences(); }, Settings.GetBool("RailReversed", false));
        menu.Items.Add(new Separator());
        foreach (var (view, key) in new[] { ("shelves", "Str_Lib_ViewShelves"), ("flow", "Str_Lib_ViewFlow"), ("grid", "Str_Lib_ViewGrid"), ("timeline", "Str_Lib_ViewTimeline") })
        {
            string target = view;
            Add(key, () => ShowLibraryView(target), Library.IsVisible && Library.CurrentView == view);
        }
    }
}
