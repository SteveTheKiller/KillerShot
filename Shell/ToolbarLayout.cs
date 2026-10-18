using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

// ============================================================
// Toolbar layout: which buttons sit on the toolbar, in what order, on which side, and which
// live in the More menu (the chevron at the end of the bar) instead. The same model as
// KillerPDF's Shell/ToolbarLayout.cs.
//
// The layout is a list of button ids for each side, with "-" for a separator, plus the ids the
// user moved to the More menu. When the window is too narrow for the bar, buttons also move to
// the More menu, a group at a time in ToolbarShedOrder; a button the user put there stays
// there at any width. The active tool always stays on the bar so its highlight stays visible.
// ============================================================
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        internal sealed record ToolbarUnit(string Id, Button Main, int Glyph, string LabelKey);

        /// <summary>A saved toolbar arrangement. Separator tokens are <see cref="ToolbarSeparatorToken"/>.</summary>
        internal sealed record ToolbarLayout(IReadOnlyList<string> Left, IReadOnlyList<string> Right,
            IReadOnlyList<string> More);

        internal const string ToolbarSeparatorToken = "-";
        private const string ToolbarLayoutSetting = "ToolbarLayout";

        // Everything on the left, in the groups the bar always had.
        internal static readonly ToolbarLayout DefaultToolbarLayout = new(
            ["capture", "record", "recent", "-", "open", "save", "saveas", "copy", "print", "-", "undo", "redo",
             "-", "select", "pen", "highlighter", "arrow", "rectangle", "ellipse", "text", "redact", "crop",
             "-", "color", "width", "-", "fit", "actualsize"],
            [],
            []);

        // Buttons leave the bar for the More menu in this order as the window narrows, a group at a
        // time: the most occasional first, the drawing tools late. Capture and Select never leave.
        private static readonly string[][] ToolbarShedOrder =
        [
            ["print"],
            ["saveas"],
            ["recent"],
            ["open"],
            ["fit", "actualsize"],
            ["undo", "redo"],
            ["ellipse"],
            ["highlighter"],
            ["arrow"],
            ["redact"],
            ["rectangle"],
            ["text"],
            ["crop"],
            ["width"],
            ["color"],
            ["pen"],
            ["copy"],
            ["save"],
            ["record"],
        ];

        private readonly List<ToolbarUnit> _toolbarUnits = [];
        private ToolbarLayout _toolbarLayout = DefaultToolbarLayout;
        private readonly HashSet<string> _toolbarShed = [];
        private bool _toolbarReflowQueued;

        /// <summary>Registers every toolbar button once and arranges the bar from the saved
        /// layout. Call before InitToolbarStyle.</summary>
        private void InitToolbarLayout()
        {
            _toolbarUnits.Clear();
            foreach (var (id, btn, glyph, key) in ToolbarItems())
                _toolbarUnits.Add(new ToolbarUnit(id, btn, glyph, key));
            _toolbarLayout = NormalizeToolbarLayout(ParseToolbarLayout(Settings.Get(ToolbarLayoutSetting)));
            ApplyToolbarLayout();
            // The bar's own size stops changing once it is clipped, so the window's is watched too.
            ToolbarGrid.SizeChanged += (_, _) => QueueToolbarReflow();
            SizeChanged += (_, _) => QueueToolbarReflow();
        }

        /// <summary>Reads the saved layout: "left;right;more", each a comma list of ids.</summary>
        internal static ToolbarLayout ParseToolbarLayout(string? saved)
        {
            if (string.IsNullOrWhiteSpace(saved)) return DefaultToolbarLayout;
            string[] parts = saved.Split(';');
            if (parts.Length != 3) return DefaultToolbarLayout;
            static List<string> Ids(string part) =>
                [.. part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            return new ToolbarLayout(Ids(parts[0]), Ids(parts[1]), Ids(parts[2]));
        }

        internal static string FormatToolbarLayout(ToolbarLayout layout) =>
            string.Join(",", layout.Left) + ";" + string.Join(",", layout.Right) + ";" + string.Join(",", layout.More);

        /// <summary>
        /// Drops unknown and repeated ids and puts back any button the saved layout does not
        /// mention (one a later version added) where the default layout has it. Separators never
        /// start or end a side or sit side by side.
        /// </summary>
        internal static ToolbarLayout NormalizeToolbarLayout(ToolbarLayout layout)
        {
            var known = DefaultToolbarLayout.Left.Concat(DefaultToolbarLayout.Right)
                .Where(id => id != ToolbarSeparatorToken).ToHashSet();
            var seen = new HashSet<string>();
            List<string> Clean(IEnumerable<string> ids, bool separators)
            {
                var result = new List<string>();
                foreach (string id in ids)
                {
                    if (id == ToolbarSeparatorToken)
                    {
                        if (separators && result.Count > 0 && result[^1] != ToolbarSeparatorToken) result.Add(id);
                    }
                    else if (known.Contains(id) && seen.Add(id)) result.Add(id);
                }
                while (result.Count > 0 && result[^1] == ToolbarSeparatorToken) result.RemoveAt(result.Count - 1);
                return result;
            }
            List<string> left = Clean(layout.Left, true), right = Clean(layout.Right, true), more = Clean(layout.More, false);
            foreach (string id in DefaultToolbarLayout.Left.Where(id => id != ToolbarSeparatorToken && !seen.Contains(id)))
                left.Add(id);
            foreach (string id in DefaultToolbarLayout.Right.Where(id => id != ToolbarSeparatorToken && !seen.Contains(id)))
                right.Add(id);
            return new ToolbarLayout(left, right, more);
        }

        /// <summary>Saves and applies a layout from Customize toolbar.</summary>
        internal void SetToolbarLayout(ToolbarLayout layout)
        {
            _toolbarLayout = NormalizeToolbarLayout(layout);
            Settings.Set(ToolbarLayoutSetting, FormatToolbarLayout(_toolbarLayout));
            ApplyToolbarLayout();
            QueueToolbarReflow();
        }

        /// <summary>Puts every button on its side in layout order, with separators between groups.
        /// Buttons in the More menu stay in the tree, collapsed, so their captions, shortcuts and
        /// the active-tool highlight keep working.</summary>
        private void ApplyToolbarLayout()
        {
            var byId = _toolbarUnits.ToDictionary(unit => unit.Id);
            foreach (ToolbarUnit unit in _toolbarUnits)
                (unit.Main.Parent as Panel)?.Children.Remove(unit.Main);
            LeftBar.Children.Clear();
            RightBar.Children.Clear();

            void Fill(Panel bar, IEnumerable<string> ids)
            {
                foreach (string id in ids)
                {
                    if (id == ToolbarSeparatorToken) bar.Children.Add(MakeToolbarSeparator());
                    else if (byId.TryGetValue(id, out ToolbarUnit? unit)) bar.Children.Add(unit.Main);
                }
            }
            Fill(LeftBar, _toolbarLayout.Left);
            Fill(RightBar, _toolbarLayout.Right);
            foreach (string id in _toolbarLayout.More)
                if (byId.TryGetValue(id, out ToolbarUnit? unit))
                    (DefaultToolbarLayout.Left.Contains(id) ? LeftBar : RightBar).Children.Add(unit.Main);
            ReflowToolbar();
        }

        private static Rectangle MakeToolbarSeparator()
        {
            var separator = new Rectangle { Width = 1, Margin = new Thickness(4, 6, 4, 6), Tag = ToolbarSeparatorToken };
            separator.SetResourceReference(Shape.FillProperty, "CardBorderBrush");
            return separator;
        }

        /// <summary>Hides a separator with no visible button on one side of it, so buttons in the
        /// More menu never leave a stray or doubled divider.</summary>
        private void SyncToolbarSeparators()
        {
            foreach (Panel bar in new Panel[] { LeftBar, RightBar })
            {
                Rectangle? pending = null;
                bool seenButton = false;
                foreach (UIElement child in bar.Children)
                {
                    if (child is Rectangle { Tag: ToolbarSeparatorToken } separator)
                    {
                        separator.Visibility = Visibility.Collapsed;
                        if (seenButton) pending ??= separator;
                        continue;
                    }
                    if (child.Visibility != Visibility.Visible) continue;
                    if (pending is not null) pending.Visibility = Visibility.Visible;
                    pending = null;
                    seenButton = true;
                }
            }
        }

        /// <summary>Coalesces reflow requests into one per layout pass.</summary>
        private void QueueToolbarReflow()
        {
            if (_toolbarReflowQueued) return;
            _toolbarReflowQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _toolbarReflowQueued = false;
                ReflowToolbar();
            }));
        }

        /// <summary>Shows every button that fits, starting fully expanded and moving groups to the
        /// More menu in ToolbarShedOrder until the bar fits its width.</summary>
        private void ReflowToolbar()
        {
            if (_toolbarUnits.Count == 0) return;
            var byId = _toolbarUnits.ToDictionary(unit => unit.Id);
            var chosen = _toolbarLayout.More.ToHashSet();
            _toolbarShed.Clear();
            foreach (ToolbarUnit unit in _toolbarUnits)
                unit.Main.Visibility = chosen.Contains(unit.Id) ? Visibility.Collapsed : Visibility.Visible;

            Button? active = _doc != null ? ToolButton(Editor.Tool) : null;
            double avail = AvailableToolbarWidth();
            double Needed()
            {
                SyncToolbarSeparators();
                bool more = chosen.Count > 0 || _toolbarShed.Count > 0;
                MoreBtn.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
                var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
                LeftBar.Measure(infinite);
                RightBar.Measure(infinite);
                MoreBtn.Measure(infinite);
                return LeftBar.DesiredSize.Width + RightBar.DesiredSize.Width + (more ? MoreBtn.DesiredSize.Width : 0);
            }

            if (avail > 0)
            {
                foreach (string[] group in ToolbarShedOrder)
                {
                    if (Needed() <= avail) break;
                    foreach (string id in group)
                    {
                        if (chosen.Contains(id) || !byId.TryGetValue(id, out ToolbarUnit? unit)) continue;
                        if (ReferenceEquals(unit.Main, active)) continue;
                        unit.Main.Visibility = Visibility.Collapsed;
                        _toolbarShed.Add(id);
                    }
                }
            }
            Needed();
        }

        /// <summary>
        /// The width the bar really has. A bar wider than its column is laid out at its own width
        /// and clipped, which makes its ActualWidth (and every parent's) report the overflowing
        /// width, so the room is measured from the window's own edge instead, in the bar's units
        /// so the app size is accounted for.
        /// </summary>
        private double AvailableToolbarWidth()
        {
            if (PresentationSource.FromVisual(ToolbarGrid) == null) return ToolbarGrid.ActualWidth;
            double edge = TranslatePoint(new Point(ActualWidth - RootGrid.Margin.Right, 0), ToolbarGrid).X;
            return Math.Min(ToolbarGrid.ActualWidth, edge - ToolbarGrid.Margin.Right);
        }

        // ---- the More menu ----

        private void MoreBtn_Click(object sender, RoutedEventArgs e)
        {
            BuildMoreMenu();
            OpenFlyout(MoreMenu, MoreBtn);
        }

        /// <summary>A right-click on the chevron opens the same menu.</summary>
        private void MoreBtn_ContextMenuOpening(object sender, ContextMenuEventArgs e) => BuildMoreMenu();

        private void BuildMoreMenu()
        {
            MoreMenu.Items.Clear();
            var byId = _toolbarUnits.ToDictionary(unit => unit.Id);
            // The user's choices first, then whatever the window was too narrow for, in bar order.
            var ids = _toolbarLayout.More
                .Concat(_toolbarLayout.Left.Concat(_toolbarLayout.Right).Where(_toolbarShed.Contains));
            foreach (string id in ids)
            {
                if (!byId.TryGetValue(id, out ToolbarUnit? unit)) continue;
                var item = new MenuItem
                {
                    Header = Loc(unit.LabelKey),
                    Icon = ToolbarUnitIcon(unit.Id),
                    InputGestureText = ShortcutFor(unit.Main) ?? "",
                    IsEnabled = unit.Main.IsEnabled,
                };
                Button target = unit.Main;
                // After the menu closes, so a button that opens its own flyout opens it cleanly.
                item.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, target))));
                MoreMenu.Items.Add(item);
            }
        }

        /// <summary>Customize toolbar, from the toolbar's right-click menu.</summary>
        private void OpenCustomizeToolbar()
        {
            var dialog = new Controls.CustomizeToolbarDialog(_toolbarUnits, _toolbarLayout, DefaultToolbarLayout,
                id => ToolbarUnitIcon(id), Loc) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } layout) SetToolbarLayout(layout);
        }

        /// <summary>A unit's icon for a list row: its glyph, or the drawn color and width icons.</summary>
        private FrameworkElement ToolbarUnitIcon(string id)
        {
            ToolbarUnit unit = _toolbarUnits.First(u => u.Id == id);
            FrameworkElement icon = DrawnIcon(unit.Main) is { } drawn ? drawn(14) : new TextBlock
            {
                Text = ((char)unit.Glyph).ToString(),
                FontFamily = MdlFont,
                FontSize = 14,
            };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return icon;
        }

        // ---- hide the toolbar ----

        private bool _toolbarHidden;

        private void ToggleToolbarHidden() => SetToolbarHidden(!_toolbarHidden, announce: true);

        private void SetToolbarHidden(bool hidden, bool announce)
        {
            _toolbarHidden = hidden;
            ToolbarGrid.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            ToolbarRowDef.MinHeight = hidden ? 0 : 42;
            Settings.SetBool("ToolbarHidden", hidden);
            SyncToolbarMenuChecks();
            if (announce) StatusText.Text = Loc(hidden ? "Str_St_ToolbarHidden" : "Str_St_ToolbarShown");
            if (!hidden) QueueToolbarReflow();
        }
    }
}
