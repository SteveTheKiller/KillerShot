using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Accessibility;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>Supplies command icons without changing the shared menu template or its check gutter.</summary>
internal static class MenuIcons
{
    private static bool _installed;
    private static readonly (string Key, int Glyph)[] Commands =
    [
        ("Str_Btn_Capture", 0xE722), ("Str_Mode_Region", 0xE7A8), ("Str_Mode_Window", 0xE737),
        ("Str_Mode_Monitor", 0xE7F4), ("Str_Mode_AllMonitors", 0xE975), ("Str_Mode_Qr", 0xED14),
        ("Str_Btn_Record", 0xE714), ("Str_Tray_Record", 0xE714), ("Str_Tray_StopRecording", 0xE71A),
        ("Str_Btn_Library", 0xE8F1), ("Str_Tray_Open", 0xE8A5),
        ("Str_Lib_Open", 0xE8E5), ("Str_Lib_Copy", 0xE8C8), ("Str_Btn_Print", 0xE749),
        ("Str_Lib_SaveAs", 0xE792), ("Str_Lib_ShowInExplorer", 0xE838), ("Str_Lib_AddToLibrary", 0xE710),
        ("Str_Lib_MoveTo", 0xE8DE), ("Str_Lib_Unfiled", 0xE8B7), ("Str_Lib_NewCollectionDots", 0xE8F4),
        ("Str_Lib_Note", 0xE70B), ("Str_Lib_CardColor", 0xE790), ("Str_Lib_CardColorDefault", 0xE777),
        ("Str_Lib_Delete", 0xE74D), ("Str_KS_Delete", 0xE74D), ("Str_KS_GrpTools", 0xE70F),
        ("Str_TT_ZoomLevel", 0xE71E), ("Str_KS_ZoomIn", 0xE8A3), ("Str_KS_ZoomOut", 0xE71F),
        ("Str_Strip_Collection", 0xE8B7), ("Str_Dock_Minimize", 0xE921), ("Str_Dock_Restore", 0xE923),
        ("Str_Rail_Reverse", 0xE8CB),
        ("Str_Lib_ViewShelves", 0xE8A9), ("Str_Lib_ViewFlow", 0xE7C5),
        ("Str_Lib_ViewGrid", 0xE80A), ("Str_Lib_ViewTimeline", 0xE823),
        ("Str_Lib_ByDate", 0xE787), ("Str_Lib_ByCollection", 0xE8B7),
        ("Str_Dock_Left", 0xE76B), ("Str_Dock_Right", 0xE76C), ("Str_Dock_Top", 0xE70E), ("Str_Dock_Bottom", 0xE70D),
        ("Str_Lib_RenameCollection", 0xE8AC), ("Str_Lib_CollectionColor", 0xE790),
        ("Str_Lib_DeleteCollection", 0xE74D), ("Str_Ocr_Links", 0xE71B),
        ("Str_Toolbar_Customize", 0xE713), ("Str_Toolbar_Hide", 0xE890),
        ("Str_CollectionPreview", 0xE890), ("Str_Preview_stack", 0xE81E), ("Str_Preview_grid", 0xE80A),
        ("Str_Preview_mini", 0xE7C5), ("Str_Preview_pile", 0xE8B9),
        ("Str_KS_EditText", 0xE70F), ("Str_KS_SelectAllMarks", 0xE8B3), ("Str_KS_LayerMenu", 0xE81E),
        ("Str_Btn_Image", 0xE8B9), ("Str_Img_Paste", 0xE77F), ("Str_Img_InsertFile", 0xE8B9),
        ("Str_Img_RotateRight", 0xE7AD), ("Str_Img_RotateLeft", 0xE7A6),
        ("Str_Img_FlipH", 0xE8AB), ("Str_Img_FlipV", 0xE8CB),
        ("Str_Img_Resize", 0xE740), ("Str_Img_Margin", 0xE739), ("Str_Img_AutoCrop", 0xE7A8),
        ("Str_Img_ResizeCustom", 0xE70F), ("Str_KS_Duplicate", 0xE8C8),
        ("Str_KS_BringToFront", 0xE74A), ("Str_KS_BringForward", 0xE70E),
        ("Str_KS_SendBackward", 0xE70D), ("Str_KS_SendToBack", 0xE74B)
    ];

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => Queue((ItemsControl)sender)));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler((sender, _) => Queue((ItemsControl)sender)));
    }

    private static void Queue(ItemsControl menu) => menu.Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => Fill(menu)));

    private static void Fill(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            // A command without a known glyph gets no icon; a stand-in gear made every unmapped item look like Settings.
            int glyph = item.Icon == null && item.Header is string header
                ? Commands.FirstOrDefault(c => LocaleManager.Loc(c.Key) == header).Glyph : 0;
            // The resize presets are plain percentages, not localized commands.
            if (glyph == 0 && item.Icon == null && item.Header is string percent && percent.EndsWith('%')) glyph = 0xE740;
            if (glyph != 0)
            {
                item.Icon = new Glyph { Text = char.ConvertFromUtf32(glyph),
                    FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Width = 16, Height = 16,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            }
            Fill(item);
        }
    }
}
