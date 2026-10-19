using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Accessibility;

// ============================================================
// Toolbar appearance: two independent settings, icon size and where the text goes, with
// KillerPDF's button sizes. Right-click the toolbar to change them, to customize which buttons
// sit where (ToolbarLayout.cs) or to hide the toolbar.
//
// The active tool is highlighted by setting the BUTTON's Foreground (EditorHost.cs), so the
// content built below must not set Foreground on its own TextBlocks.
// ============================================================
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private enum IconSize { Small, Large }
        private enum LabelMode { None, Beside, Under, Only }

        // Large icons with no text by default: an editor toolbar has many buttons, and every
        // one keeps its name and shortcut in the tooltip.
        private IconSize _iconSize = IconSize.Large;
        private LabelMode _labelMode = LabelMode.None;

        /// <summary>
        /// Every toolbar button with the id its place is saved under. This table is the only place
        /// a toolbar codepoint appears. A glyph of 0 marks a button whose icon is drawn (the color
        /// swatch and the stroke width). Where each button sits is ToolbarLayout.cs.
        /// </summary>
        private IEnumerable<(string Id, Button Btn, int Glyph, string LabelKey)> ToolbarItems()
        {
            yield return ("capture",    CaptureBtn,         0xE722, "Str_Btn_Capture");     // Camera
            yield return ("record",     RecordBtn,          0xE7C8, "Str_Btn_Record");      // Record
            yield return ("recent",     RecentBtn,          0xE81C, "Str_Btn_Recent");      // History
            yield return ("open",       OpenBtn,            0xE8E5, "Str_Btn_Open");        // OpenFile
            yield return ("save",       SaveBtn,            0xE74E, "Str_Btn_Save");        // Save
            yield return ("saveas",     SaveAsBtn,          0xE792, "Str_Btn_SaveAs");      // SaveAs
            yield return ("copy",       CopyBtn,            0xE8C8, "Str_Btn_Copy");        // Copy
            yield return ("print",      PrintBtn,           0xE749, "Str_Btn_Print");       // Print
            yield return ("undo",       UndoBtn,            0xE7A7, "Str_Btn_Undo");        // Undo
            yield return ("redo",       RedoBtn,            0xE7A6, "Str_Btn_Redo");        // Redo
            yield return ("select",     ToolSelectBtn,      0xE7C9, "Str_Tool_Select");     // TouchPointer
            yield return ("pen",        ToolPenBtn,         0xE70F, "Str_Tool_Pen");        // Edit (pencil)
            yield return ("highlighter", ToolHighlighterBtn, 0xE7E6, "Str_Tool_Highlighter");// Highlight
            yield return ("arrow",      ToolArrowBtn,       0xE72A, "Str_Tool_Arrow");      // Forward
            yield return ("rectangle",  ToolRectangleBtn,   0xE739, "Str_Tool_Rectangle");  // Checkbox (square)
            yield return ("ellipse",    ToolEllipseBtn,     0xEA3A, "Str_Tool_Ellipse");    // StatusCircleRing
            yield return ("text",       ToolTextBtn,        0xE8D2, "Str_Tool_Text");       // Font
            yield return ("redact",     ToolRedactBtn,      0xE73B, "Str_Tool_Redact");     // CheckboxFill
            yield return ("crop",       ToolCropBtn,        0xE7A8, "Str_Tool_Crop");       // Crop
            yield return ("color",      ColorBtn,           0,      "Str_Btn_Color");
            yield return ("width",      WidthBtn,           0,      "Str_Btn_Width");
            yield return ("fit",        FitBtn,             0xE9A6, "Str_Btn_Fit");         // FitPage
            yield return ("actualsize", ActualSizeBtn,      0xE71E, "Str_Btn_ActualSize");  // Zoom
        }

        // Ctrl+Shift+1 to 6 pick these, in this order, as in KillerPDF (Shortcuts.cs).
        private static readonly (IconSize Size, string LabelKey, string Gesture)[] IconSizes =
        [
            (IconSize.Small, "Str_Toolbar_SmallIcons", "Ctrl+Shift+1"),
            (IconSize.Large, "Str_Toolbar_LargeIcons", "Ctrl+Shift+2"),
        ];

        private static readonly (LabelMode Mode, string LabelKey, string Gesture)[] LabelModes =
        [
            (LabelMode.None,   "Str_Toolbar_TextNone",   "Ctrl+Shift+3"),
            (LabelMode.Beside, "Str_Toolbar_TextBeside", "Ctrl+Shift+4"),
            (LabelMode.Under,  "Str_Toolbar_TextUnder",  "Ctrl+Shift+5"),
            (LabelMode.Only,   "Str_Toolbar_TextOnly",   "Ctrl+Shift+6"),
        ];

        private static readonly FontFamily MdlFont = new("Segoe MDL2 Assets");

        // ---- apply ----

        /// <summary>
        /// Restores the saved settings. Call once at startup, after the locale is in place - the
        /// captions are read through Loc() and would otherwise be built in the wrong language.
        /// </summary>
        private void InitToolbarStyle()
        {
            if (Settings.Get("ToolbarIconSize") is string sz && Enum.TryParse<IconSize>(sz, out var s))
                _iconSize = s;
            if (Settings.Get("ToolbarLabels") is string lb && Enum.TryParse<LabelMode>(lb, out var l))
                _labelMode = l;
            if (Settings.GetBool("ToolbarHidden", false)) SetToolbarHidden(true, announce: false);

            BuildToolbarMenu();
            ApplyToolbarStyle();
        }

        private void SetIconSize(IconSize size)  { _iconSize = size;  Settings.Set("ToolbarIconSize", size.ToString()); ApplyToolbarStyle(); }
        private void SetLabelMode(LabelMode mode) { _labelMode = mode; Settings.Set("ToolbarLabels", mode.ToString()); ApplyToolbarStyle(); }

        private void ApplyToolbarStyle()
        {
            foreach (var (_, btn, glyph, key) in ToolbarItems())
            {
                var label = Loc(key);
                btn.Content = BuildToolbarContent(_iconSize, _labelMode, glyph, label, DrawnIcon(btn));
                SizeToolbarButton(btn, _iconSize, _labelMode);
                // The name always stays reachable, which is what makes an icon-only bar usable.
                // The shortcut rides along in the tooltip, the family rule for every action surface.
                btn.ToolTip = ShortcutFor(btn) is string sc ? label + "  (" + sc + ")" : label;
            }

            SyncToolbarMenuChecks();
            HighlightActiveTool();
            // The buttons changed size, so what fits on the bar has to be worked out again.
            QueueToolbarReflow();
        }

        private void SyncToolbarMenuChecks()
        {
            foreach (var item in ToolbarMenu.Items)
            {
                if (item is not MenuItem mi) continue;
                if (mi.Tag is IconSize szTag)
                {
                    mi.IsChecked = szTag == _iconSize;
                    // Text-only has no icon to size. Gray the choice rather than hiding it, so the
                    // setting stays visible and comes back when text moves off Only.
                    mi.IsEnabled = _labelMode != LabelMode.Only;
                }
                else if (mi.Tag is LabelMode lbTag) mi.IsChecked = lbTag == _labelMode;
                else if (mi.Tag is "hide") mi.IsChecked = _toolbarHidden;
            }
        }

        // ---- KillerPDF's numbers, from SettingsPanel.cs SetToolbarButton. Do not retune these.
        //
        // These are the values settled on in KillerPDF, and the bar must match KillerPDF's exactly.
        //
        //   glyph    small 14   large 20   beside 16   under 20
        //   caption  beside 12  under 10
        //   button   icon-only  small 36x32, large 46x42, beside 40x34, under 46x52
        //   padding  icon-only 10,6   beside/under 8,5   text-only 8,5 with auto width
        //
        // The one deviation is deliberate: KillerPDF derives the GLYPH SIZE from the text mode
        // (under forces 20, beside forces 16), which is the very coupling that had to be broken.
        // Here the size axis wins and the text mode only decides layout - so "small icons with
        // text under" is a real combination rather than being silently promoted to large.

        private const double GlyphSmall = 14;
        private const double GlyphLarge = 20;

        /// <summary>Applies KillerPDF's button box for the current combination.</summary>
        private static void SizeToolbarButton(Button btn, IconSize size, LabelMode mode)
        {
            bool large = size == IconSize.Large;
            switch (mode)
            {
                case LabelMode.Only:
                    btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34;
                    btn.Padding = new Thickness(8, 5, 8, 5);
                    break;
                case LabelMode.Beside:
                    btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34;
                    btn.Padding = new Thickness(8, 5, 8, 5);
                    break;
                case LabelMode.Under:
                    btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = large ? 50 : 46;
                    btn.Padding = new Thickness(6, 4, 6, 4);
                    break;
                default:   // None - icon only, the fixed KillerPDF box
                    btn.Width = large ? 46 : 36;
                    btn.MinWidth = 0;
                    btn.Height = large ? 42 : 32;
                    btn.Padding = new Thickness(10, 6, 10, 6);
                    break;
            }
        }

        /// <summary>
        /// The button's content. Nothing here sets Foreground - see the class comment: the
        /// active-view highlight works by setting Foreground on the button and relying on
        /// inheritance to reach these.
        ///
        /// Size and text are independent, so an icon is sized by <paramref name="size"/> alone.
        /// The old code derived the icon size from the TEXT mode, which is exactly why the two
        /// could not be chosen separately.
        /// </summary>
        private static object BuildToolbarContent(IconSize size, LabelMode mode, int glyph, string label, Func<double, FrameworkElement>? drawn = null)
        {
            double glyphSize = size == IconSize.Large ? GlyphLarge : GlyphSmall;

            if (mode == LabelMode.Only)
                return new TextBlock
                {
                    Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                };

            // A drawn icon sits in a glyph-sized box, so a thin stroke bar keeps its label on the
            // same line as the glyph buttons' labels.
            FrameworkElement? art = drawn?.Invoke(glyphSize);
            if (art != null) art.VerticalAlignment = VerticalAlignment.Center;
            FrameworkElement icon = art != null ? new Grid { Height = glyphSize, Children = { art } } : new Glyph
            {
                Text = ((char)glyph).ToString(),
                FontFamily = MdlFont,
                FontSize = glyphSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;

            if (mode == LabelMode.None) return icon;

            var stack = new StackPanel
            {
                Orientation = mode == LabelMode.Beside ? Orientation.Horizontal : Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                Text = label,
                // KillerPDF: 12 beside, 10 under.
                FontSize = mode == LabelMode.Beside ? 12 : 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            if (mode == LabelMode.Beside) icon.Margin = new Thickness(0, 0, 7, 0);
            else                          text.Margin = new Thickness(0, 2, 0, 0);

            stack.Children.Add(icon);
            stack.Children.Add(text);
            return stack;
        }

        // ---- the right-click menu ----

        /// <summary>
        /// TWO radio groups, separated: icon size, then where the text goes. One flat list of five
        /// could not express "large icons WITH text", which is the whole reason this was split.
        /// Items stay open on click so the styles can be compared without reopening the menu.
        /// Customize toolbar and Hide toolbar follow, as in KillerPDF.
        /// </summary>
        private void BuildToolbarMenu()
        {
            ToolbarMenu.Items.Clear();
            ToolbarMenu.Items.Add(new MenuItem { Header = Loc("Str_Toolbar_Header"), IsEnabled = false });
            ToolbarMenu.Items.Add(new Separator());

            foreach (var (size, key, gesture) in IconSizes)
            {
                var mi = new MenuItem { Header = Loc(key), Tag = size, IsCheckable = true, IsChecked = size == _iconSize,
                                        StaysOpenOnClick = true, InputGestureText = gesture };
                // Checking one unchecks the rest through ApplyToolbarStyle, so this behaves as a
                // radio group without needing RadioButton plumbing inside a menu.
                var s = size;
                mi.Click += (_, __) => SetIconSize(s);
                ToolbarMenu.Items.Add(mi);
            }

            ToolbarMenu.Items.Add(new Separator());

            foreach (var (mode, key, gesture) in LabelModes)
            {
                var mi = new MenuItem { Header = Loc(key), Tag = mode, IsCheckable = true, IsChecked = mode == _labelMode,
                                        StaysOpenOnClick = true, InputGestureText = gesture };
                var m = mode;
                mi.Click += (_, __) => SetLabelMode(m);
                ToolbarMenu.Items.Add(mi);
            }

            ToolbarMenu.Items.Add(new Separator());
            var customize = new MenuItem { Header = Loc("Str_Toolbar_Customize") };
            customize.Click += (_, __) => OpenCustomizeToolbar();
            ToolbarMenu.Items.Add(customize);
            var hide = new MenuItem { Header = Loc("Str_Toolbar_Hide"), Tag = "hide", IsCheckable = true,
                                      IsChecked = _toolbarHidden, InputGestureText = "Alt+M" };
            hide.Click += (_, __) => ToggleToolbarHidden();
            ToolbarMenu.Items.Add(hide);
        }

        /// <summary>Ctrl+Shift+1 to 6: the two icon sizes, then the four text modes.</summary>
        private void PickToolbarStyle(int number)
        {
            if (number is >= 1 and <= 2) SetIconSize(IconSizes[number - 1].Size);
            else if (number is >= 3 and <= 6) SetLabelMode(LabelModes[number - 3].Mode);
            else return;
            StatusText.Text = Loc(number <= 2 ? IconSizes[number - 1].LabelKey : LabelModes[number - 3].LabelKey);
        }

        /// <summary>Rebuild captions and the menu after a live language change.</summary>
        private void RelocalizeToolbar()
        {
            BuildToolbarMenu();
            ApplyToolbarStyle();
            Library.Relocalize();
            _strip?.Relocalize();
        }
    }
}
