using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using KillerFamily.Controls;
using KillerShot.Editor;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private void InitEditorContextMenu()
    {
        var menu = ShotCards.Menu();
        // Built before it opens, so a card with nothing usable on it never opens.
        EditorStage.ContextMenuOpening += (_, e) =>
        {
            BuildEditorContextMenu(menu);
            if (!ShotCards.AnyEnabled(menu)) e.Handled = true;
        };
        menu.Opened += (_, _) => { if (menu.Items.Count == 0) BuildEditorContextMenu(menu); };
        EditorStage.ContextMenu = menu;
        BuildEditorContextMenu(menu);
    }

    private void BuildEditorContextMenu(ContextMenu menu)
    {
        Editor.CommitText();
        // The picture card: the picture's name and size, with Delete for a picture from the album or a
        // folder collection at the right end of the header (it asks first, as it does in the album; a
        // folder file goes to the Recycle Bin). Then five full rows, most used first: output, undo and
        // the selected marks, the lists (arrange, tools, image) as flyout tiles, and the look. Print
        // starts in the card's Hidden drawer.
        ShotCards.Prepare(menu);
        var current = App.Instance.CurrentItem;
        string title = current is { } shown ? LibraryView.Title(shown) : Loc("Str_Lib_Capture");
        string? size = _doc == null ? null
            : _doc.PixelWidth.ToString(System.Globalization.CultureInfo.CurrentCulture) + " x " + _doc.PixelHeight.ToString(System.Globalization.CultureInfo.CurrentCulture);
        menu.Items.Add(current != null
            ? ShotCards.Header(menu, title, size, Loc("Str_Lib_Delete"), null,
                () => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Library.Delete(current))))
            : ShotCards.Header(menu, title, size));
        CardTile ToolbarAction(string id, bool? isChecked = null)
        {
            var unit = _toolbarUnits.Single(u => u.Id == id);
            string glyph = CardGlyph(unit);
            return new CardTile(glyph, Loc(unit.LabelKey), ShortcutFor(unit.Main), () => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!unit.Main.IsEnabled) return;
                if (id == "save") Save();
                else unit.Main.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, unit.Main));
            })), false, unit.Main.IsEnabled) { Checked = isChecked, Id = id };
        }
        // A tool row in the Tools flyout, ticked on the tool in use.
        MenuItem ToolRow(string id, bool isChecked)
        {
            var unit = _toolbarUnits.Single(u => u.Id == id);
            var item = new MenuItem { Tag = id, Header = Loc(unit.LabelKey), Icon = ToolbarUnitIcon(id),
                InputGestureText = ShortcutFor(unit.Main) ?? "", IsEnabled = unit.Main.IsEnabled, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (unit.Main.IsEnabled) unit.Main.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, unit.Main));
            }));
            return item;
        }
        var output = new[] { "copy", "save", "saveas", "print" }.Select(id => ToolbarAction(id));
        var history = new[] { ToolbarAction("undo"), ToolbarAction("redo") };
        bool any = Editor.Selected != null;
        // A tile shows the short caption; the full one stays its name and tooltip.
        CardTile Mark(string id, string glyph, string key, string shown, string keys, bool enabled, Action action)
            => new(glyph, Loc(key), keys, action, false, enabled) { Shown = Loc(shown), Id = id };
        // The marks: Delete for the selected marks is the destructive tile, so it ends the row. It erases
        // marks, so it is the eraser, not the header's bin, which deletes the picture.
        var marks = new[]
        {
            Mark("edittext", "\uE8D2", "Str_KS_EditText", "Str_Card_EditTextShort", "F2", Editor.Selected is TextAnnotation or CalloutAnnotation, () => Editor.EditSelectedText()),
            Mark("duplicate", "\uF413", "Str_KS_Duplicate", "Str_Card_DuplicateShort", "Ctrl+D", any, () => Editor.DuplicateSelected()),
            Mark("selectall", "\uE8B3", "Str_KS_SelectAllMarks", "Str_Ctx_SelectAll", "Ctrl+A", _doc?.State.Items.IsEmpty == false, () => Editor.SelectAll()),
            Mark("delete", "\uED61", "Str_KS_Delete", "Str_Lib_Delete", "Del", any, () => Editor.DeleteSelected()),
        };
        var layer = new List<object>();
        foreach (var (tag, key, gesture, direction, all) in new[]
        {
            ("front", "Str_KS_BringToFront", "Ctrl+Shift+]", 1, true), ("forward", "Str_KS_BringForward", "Ctrl+]", 1, false),
            ("backward", "Str_KS_SendBackward", "Ctrl+[", -1, false), ("back", "Str_KS_SendToBack", "Ctrl+Shift+[", -1, true),
        })
        {
            var item = new MenuItem { Tag = tag, Header = Loc(key), InputGestureText = gesture };
            item.Click += (_, _) => Editor.Reorder(direction, all);
            layer.Add(item);
        }
        var tools = new List<object>();
        foreach (ToolKind tool in Enum.GetValues<ToolKind>())
            tools.Add(ToolRow(_toolbarUnits.Single(u => u.Main == ToolButton(tool)).Id, Editor.Tool == tool));
        var zoom = new List<object>();
        foreach (string id in new[] { "fit", "actualsize" })
        {
            var unit = _toolbarUnits.Single(u => u.Id == id);
            var item = new MenuItem { Tag = id, Header = Loc(unit.LabelKey), Icon = ToolbarUnitIcon(id), InputGestureText = ShortcutFor(unit.Main) ?? "", IsEnabled = unit.Main.IsEnabled };
            item.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (unit.Main.IsEnabled) unit.Main.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, unit.Main));
            }));
            zoom.Add(item);
        }
        zoom.Add(new Separator());
        foreach (var (id, key, gesture, direction) in new[] { ("zoomin", "Str_KS_ZoomIn", "Ctrl+=", 1), ("zoomout", "Str_KS_ZoomOut", "Ctrl+-", -1) })
        {
            int step = direction;
            var item = new MenuItem { Tag = id, Header = Loc(key), InputGestureText = gesture };
            item.Click += (_, _) => ZoomStep(step);
            zoom.Add(item);
        }
        bool open = _doc != null;
        var lists = new[]
        {
            ShotCards.Flyout("\uE81E", Loc("Str_KS_LayerMenu"), null, layer, any) with { Id = "layer" },
            ShotCards.Flyout("\uEC87", Loc("Str_Card_Tools"), null, tools, open) with { Id = "tools" },
            ShotCards.Flyout(((char)_toolbarUnits.Single(u => u.Id == "image").Glyph).ToString(), Loc("Str_Btn_Image"), null, ImageMenuItems(), open) with { Id = "image" },
        };
        var look = new[]
        {
            ToolbarAction("color"),
            ToolbarAction("width"),
            ShotCards.Flyout("\uE71E", Loc("Str_TT_ZoomLevel"), null, zoom, open) with { Id = "zoom" },
        };
        // Each tile's Id is its command name: the drawer keeps it, and the self-tests read it as the tile's Uid.
        menu.Items.Add(ShotCards.Editable(menu, "Picture", ["copy", "save"], ["print"],
            output, history, marks, lists, look));
    }
}
