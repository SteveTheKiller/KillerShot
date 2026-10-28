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
        // Built before it opens, so a menu with nothing usable in it never opens as an empty card.
        EditorStage.ContextMenuOpening += (_, e) =>
        {
            BuildEditorContextMenu(menu);
            if (!menu.Items.OfType<MenuItem>().Any(item => item.Visibility == Visibility.Visible)) e.Handled = true;
        };
        menu.Opened += (_, _) => { if (menu.Items.Count == 0) BuildEditorContextMenu(menu); };
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
        // The picture itself, when it came from the album or a folder collection: Delete asks
        // first, as it does in the album (a folder file goes to the Recycle Bin).
        if (App.Instance.CurrentItem is { } current)
        {
            var delete = new MenuItem { Tag = "deletecapture", Header = Loc("Str_Lib_Delete") };
            delete.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Library.Delete(current)));
            menu.Items.Add(delete);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(ToolbarAction("undo"));
        menu.Items.Add(ToolbarAction("redo"));
        bool any = Editor.Selected != null;
        MenuItem Mark(string tag, string key, string gesture, bool enabled, Action action)
        {
            var item = new MenuItem { Tag = tag, Header = Loc(key), InputGestureText = gesture, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Mark("delete", "Str_KS_Delete", "Del", any, () => Editor.DeleteSelected());
        Mark("duplicate", "Str_KS_Duplicate", "Ctrl+D", any, () => Editor.DuplicateSelected());
        Mark("edittext", "Str_KS_EditText", "F2", Editor.Selected is TextAnnotation or CalloutAnnotation, () => Editor.EditSelectedText());
        Mark("selectall", "Str_KS_SelectAllMarks", "Ctrl+A", _doc?.State.Items.IsEmpty == false, () => Editor.SelectAll());
        var layer = new MenuItem { Header = Loc("Str_KS_LayerMenu"), IsEnabled = any };
        foreach (var (tag, key, gesture, direction, all) in new[]
        {
            ("front", "Str_KS_BringToFront", "Ctrl+Shift+]", 1, true), ("forward", "Str_KS_BringForward", "Ctrl+]", 1, false),
            ("backward", "Str_KS_SendBackward", "Ctrl+[", -1, false), ("back", "Str_KS_SendToBack", "Ctrl+Shift+[", -1, true),
        })
        {
            var item = new MenuItem { Tag = tag, Header = Loc(key), InputGestureText = gesture };
            item.Click += (_, _) => Editor.Reorder(direction, all);
            layer.Items.Add(item);
        }
        menu.Items.Add(layer);
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
        var image = new MenuItem { Header = Loc("Str_Btn_Image"), Icon = ToolbarUnitIcon("image"), IsEnabled = _doc != null };
        foreach (var item in ImageMenuItems()) image.Items.Add(item);
        menu.Items.Add(image);
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
        // Only what can be done right now: nothing greyed out.
        Controls.MenuTidy.HideInactive(menu);
    }
}
