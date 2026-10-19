using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using KillerShot.Editor;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private void InitEditorContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => BuildEditorContextMenu(menu);
        EditorStage.ContextMenu = menu;
        BuildEditorContextMenu(menu);
    }

    private void BuildEditorContextMenu(ContextMenu menu)
    {
        Editor.CommitText();
        menu.Items.Clear();
        MenuItem ToolbarAction(string id)
        {
            var unit = _toolbarUnits.Single(u => u.Id == id);
            var item = new MenuItem { Tag = id, Header = Loc(unit.LabelKey), Icon = ToolbarUnitIcon(id),
                InputGestureText = ShortcutFor(unit.Main) ?? "", IsEnabled = unit.Main.IsEnabled };
            item.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!unit.Main.IsEnabled) return;
                if (id == "save") Save();
                else unit.Main.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, unit.Main));
            }));
            return item;
        }
        foreach (string id in new[] { "copy", "save", "saveas", "print" }) menu.Items.Add(ToolbarAction(id));
        menu.Items.Add(new Separator());
        menu.Items.Add(ToolbarAction("undo"));
        menu.Items.Add(ToolbarAction("redo"));
        var delete = new MenuItem { Tag = "delete", Header = Loc("Str_KS_Delete"), InputGestureText = "Del", IsEnabled = Editor.Selected != null };
        delete.Click += (_, _) => Editor.DeleteSelected();
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        var tools = new MenuItem { Header = Loc("Str_KS_GrpTools"), IsEnabled = _doc != null };
        foreach (ToolKind tool in Enum.GetValues<ToolKind>())
        {
            string id = _toolbarUnits.Single(u => u.Main == ToolButton(tool)).Id;
            var item = ToolbarAction(id);
            item.IsCheckable = true;
            item.IsChecked = Editor.Tool == tool;
            tools.Items.Add(item);
        }
        menu.Items.Add(tools);
        menu.Items.Add(ToolbarAction("color"));
        menu.Items.Add(ToolbarAction("width"));
        var zoom = new MenuItem { Header = Loc("Str_TT_ZoomLevel"), IsEnabled = _doc != null };
        zoom.Items.Add(ToolbarAction("fit"));
        zoom.Items.Add(ToolbarAction("actualsize"));
        zoom.Items.Add(new Separator());
        foreach (var (id, key, gesture, direction) in new[] { ("zoomin", "Str_KS_ZoomIn", "Ctrl+=", 1), ("zoomout", "Str_KS_ZoomOut", "Ctrl+-", -1) })
        {
            int step = direction;
            var item = new MenuItem { Tag = id, Header = Loc(key), InputGestureText = gesture };
            item.Click += (_, _) => ZoomStep(step);
            zoom.Items.Add(item);
        }
        menu.Items.Add(zoom);
    }
}
