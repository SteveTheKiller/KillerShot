using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerFamily.Layout
{
    /// <summary>The four edges a pane can dock to.</summary>
    public enum DockSide { Left, Top, Right, Bottom }

    /// <summary>
    /// A body with one pane docked to any of its four sides. The pane is resized with a splitter
    /// and can be minimized to a thin bar along its edge, from which one click brings it back.
    ///
    /// Keyboard, the same in every Killer Tools app that has a movable pane:
    /// Alt+Shift+Left, Up, Right or Down docks the pane to that side, and the arrow of the side it
    /// is already on minimizes or restores it. Call <see cref="HandleKey"/> from the window's
    /// PreviewKeyDown.
    ///
    /// The host only arranges. The pane reads <see cref="Side"/> (or listens to
    /// <see cref="SideChanged"/>) to lay itself out across or along the edge, and the app saves
    /// <see cref="Side"/>, <see cref="IsMinimized"/>, <see cref="PaneWidth"/> and
    /// <see cref="PaneHeight"/> when <see cref="LayoutChanged"/> fires.
    /// </summary>
    public class DockHost : Grid
    {
        public static readonly DependencyProperty SideProperty = DependencyProperty.Register(
            nameof(Side), typeof(DockSide), typeof(DockHost), new PropertyMetadata(DockSide.Left, (d, _) => ((DockHost)d).OnSideChanged()));
        public static readonly DependencyProperty IsMinimizedProperty = DependencyProperty.Register(
            nameof(IsMinimized), typeof(bool), typeof(DockHost), new PropertyMetadata(false, (d, _) => ((DockHost)d).Relayout()));
        public static readonly DependencyProperty ShowPaneProperty = DependencyProperty.Register(
            nameof(ShowPane), typeof(bool), typeof(DockHost), new PropertyMetadata(true, (d, _) => ((DockHost)d).Relayout()));

        /// <summary>The side the pane is docked to.</summary>
        public DockSide Side { get => (DockSide)GetValue(SideProperty); set => SetValue(SideProperty, value); }

        /// <summary>The pane is folded to a thin bar along its edge.</summary>
        public bool IsMinimized { get => (bool)GetValue(IsMinimizedProperty); set => SetValue(IsMinimizedProperty, value); }

        /// <summary>False hides the pane, its bar and its splitter entirely, for states with
        /// nothing to show in it. Docking and minimizing are kept for when it comes back.</summary>
        public bool ShowPane { get => (bool)GetValue(ShowPaneProperty); set => SetValue(ShowPaneProperty, value); }

        /// <summary>The pane's width when docked left or right, in device-independent pixels.</summary>
        public double PaneWidth { get; set; } = 220;

        /// <summary>The pane's height when docked at the top or bottom.</summary>
        public double PaneHeight { get; set; } = 190;

        public double MinPaneSize { get; set; } = 110;

        /// <summary>The thickness of the bar a minimized pane folds to.</summary>
        public double MinimizedSize { get; set; } = 22;

        public double SplitterSize { get; set; } = 6;

        /// <summary>The docked content. Set once; the host moves it between sides.</summary>
        public UIElement? Pane { get => _pane; set { _pane = value; Relayout(); } }

        /// <summary>The main content, filling what the pane leaves.</summary>
        public UIElement? Body { get => _body; set { _body = value; Relayout(); } }

        /// <summary>Tooltip and screen reader name of the bar's restore button. Apps pass their
        /// localized string; the default is English.</summary>
        public string RestoreText { get; set; } = "Show the panel";

        /// <summary>Screen reader name of the splitter.</summary>
        public string ResizeText { get; set; } = "Resize the panel";

        /// <summary>The pane moved to another side.</summary>
        public event Action? SideChanged;

        /// <summary>The user docked, minimized, restored or resized the pane. Save the layout here.</summary>
        public event Action? LayoutChanged;

        public bool IsHorizontal => Side is DockSide.Top or DockSide.Bottom;

        private UIElement? _pane, _body;
        private readonly GridSplitter _splitter;
        private readonly Border _bar;
        private readonly Button _restore;

        public DockHost()
        {
            _splitter = new GridSplitter { Background = Brushes.Transparent, Focusable = true, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            _splitter.DragCompleted += (_, _) => { RememberSize(); LayoutChanged?.Invoke(); };
            _splitter.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { RememberSize(); LayoutChanged?.Invoke(); } };

            _restore = new Button
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
            };
            _restore.SetResourceReference(Control.ForegroundProperty, "MutedTextBrush");
            _restore.Template = FlatButton();
            _restore.Click += (_, _) => Restore();
            _bar = new Border { Child = _restore, CornerRadius = new CornerRadius(4) };
            _bar.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
        }

        /// <summary>A bare button: the glyph on the bar, the row hover color under the pointer and
        /// the accent when focused, so the bar looks the same in every theme.</summary>
        private static ControlTemplate FlatButton()
        {
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowHoverBrush"), "bd"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryBrush")));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryBrush")));
            template.Triggers.Add(focus);
            return template;
        }

        /// <summary>Alt+Shift+arrow: dock to that side, or minimize and restore on the side it is
        /// on. True when the key was one of these and the pane is showing.</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            if (!ShowPane || Keyboard.Modifiers != (ModifierKeys.Alt | ModifierKeys.Shift)) return false;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            DockSide? side = key switch
            {
                Key.Left => DockSide.Left, Key.Up => DockSide.Top,
                Key.Right => DockSide.Right, Key.Down => DockSide.Bottom,
                _ => null,
            };
            if (side is not DockSide target) return false;
            if (target == Side) { if (IsMinimized) Restore(); else Minimize(); }
            else DockTo(target);
            e.Handled = true;
            return true;
        }

        public void DockTo(DockSide side)
        {
            IsMinimized = false;
            Side = side;
            LayoutChanged?.Invoke();
        }

        public void Minimize()
        {
            if (IsMinimized) return;
            RememberSize();
            IsMinimized = true;
            LayoutChanged?.Invoke();
        }

        public void Restore()
        {
            if (!IsMinimized) return;
            IsMinimized = false;
            LayoutChanged?.Invoke();
            _pane?.Focus();
        }

        private void OnSideChanged()
        {
            Relayout();
            SideChanged?.Invoke();
        }

        private void RememberSize()
        {
            if (IsMinimized || _pane is not FrameworkElement pane || !pane.IsVisible) return;
            if (IsHorizontal) { if (pane.ActualHeight > 0) PaneHeight = pane.ActualHeight; }
            else if (pane.ActualWidth > 0) PaneWidth = pane.ActualWidth;
        }

        /// <summary>Lays out the tracks for the current side and state: pane, splitter and body
        /// in order from the docked edge, or the body alone.</summary>
        private void Relayout()
        {
            Children.Clear();
            RowDefinitions.Clear();
            ColumnDefinitions.Clear();
            if (_body == null) return;
            if (_pane == null || !ShowPane)
            {
                Children.Add(_body);
                return;
            }

            bool horizontal = IsHorizontal;
            bool atStart = Side is DockSide.Left or DockSide.Top;
            UIElement first = IsMinimized ? _bar : _pane;
            var paneTrack = IsMinimized
                ? new GridLength(MinimizedSize)
                : new GridLength(Math.Max(MinPaneSize, horizontal ? PaneHeight : PaneWidth));

            var tracks = new List<(GridLength Size, UIElement? Element, double Min)>
            {
                (paneTrack, first, IsMinimized ? MinimizedSize : MinPaneSize),
                (new GridLength(IsMinimized ? 4 : SplitterSize), IsMinimized ? null : _splitter, 0),
                (new GridLength(1, GridUnitType.Star), _body, MinPaneSize),
            };
            if (!atStart) tracks.Reverse();

            for (int i = 0; i < tracks.Count; i++)
            {
                var (size, element, min) = tracks[i];
                if (horizontal) RowDefinitions.Add(new RowDefinition { Height = size, MinHeight = min });
                else ColumnDefinitions.Add(new ColumnDefinition { Width = size, MinWidth = min });
                if (element == null) continue;
                if (horizontal) SetRow(element, i); else SetColumn(element, i);
                if (horizontal) SetColumn(element, 0); else SetRow(element, 0);
                Children.Add(element);
            }

            _splitter.ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns;
            _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _splitter.VerticalAlignment = VerticalAlignment.Stretch;
            _splitter.Cursor = horizontal ? Cursors.SizeNS : Cursors.SizeWE;
            AutomationProperties.SetName(_splitter, ResizeText);

            // The bar's chevron points into the window, the way the pane will come out.
            _restore.Content = Side switch
            {
                DockSide.Left => "", DockSide.Right => "",
                DockSide.Top => "", _ => "",
            };
            _restore.ToolTip = RestoreText;
            AutomationProperties.SetName(_restore, RestoreText);
        }
    }
}
