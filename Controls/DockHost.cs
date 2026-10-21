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
    /// and can be minimized; the rail's chevron folds it away and brings it back.
    ///
    /// The rail (the app's icon rail, set as <see cref="Rail"/>) sits on the pane's INNER edge,
    /// the one facing the body, as KillerUI's SidebarSide lays out a sidebar: a column beside a
    /// left or right pane, a row under a top pane or over a bottom one. It stays where it is when
    /// the pane minimizes or hides, so its buttons never move under the pointer. The collapse
    /// chevron is at the rail's start and the rail's own buttons at its far end.
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

        /// <summary>The rail's thickness: its width beside a left or right pane, its height beside
        /// a top or bottom one.</summary>
        public double RailSize { get; set; } = 24;

        public double SplitterSize { get; set; } = 6;

        /// <summary>The docked content. Set once; the host moves it between sides.</summary>
        public UIElement? Pane { get => _pane; set { _pane = value; Relayout(); } }

        /// <summary>The main content, filling what the pane leaves.</summary>
        public UIElement? Body { get => _body; set { _body = value; Relayout(); } }

        /// <summary>The app's icon rail. A StackPanel turns to run along the rail, and each child's
        /// margin turns with it, so a gap below a button becomes a gap to its right.</summary>
        public UIElement? Rail { get => _rail; set { _rail = value; Relayout(); } }

        /// <summary>Tooltip and screen reader name of the chevron while the pane is minimized. Apps
        /// pass their localized string; the default is English.</summary>
        public string RestoreText { get => _restoreText; set { _restoreText = value; UpdateToggle(); } }

        /// <summary>Tooltip and screen reader name of the chevron while the pane is open.</summary>
        public string CollapseText { get => _collapseText; set { _collapseText = value; UpdateToggle(); } }

        /// <summary>Screen reader name of the splitter.</summary>
        public string ResizeText { get; set; } = "Resize the panel";

        /// <summary>The pane moved to another side.</summary>
        public event Action? SideChanged;

        /// <summary>The user docked, minimized, restored or resized the pane. Save the layout here.</summary>
        public event Action? LayoutChanged;

        public bool IsHorizontal => Side is DockSide.Top or DockSide.Bottom;

        /// <summary>The sides this pane may dock to; others are refused. Null allows every side.</summary>
        public IReadOnlyCollection<DockSide>? AllowedSides { get; set; }

        private bool Allows(DockSide side) => AllowedSides == null || AllowedSides.Contains(side);

        private UIElement? _pane, _body, _rail;
        private readonly GridSplitter _splitter;
        private readonly Grid _inner = new();
        private readonly Grid _railGrid = new() { Background = Brushes.Transparent };
        private readonly Button _toggle;
        private bool _railAcross;   // the rail's children are laid out across (a top or bottom rail)
        private string _restoreText = "Show the panel", _collapseText = "Minimize the panel";

        public DockHost()
        {
            _splitter = new GridSplitter { Background = Brushes.Transparent, Focusable = true, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            SetZIndex(_splitter, 2);
            _splitter.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="GridSplitter">
                    <Border Background="Transparent">
                        <Rectangle x:Name="line" Width="1" HorizontalAlignment="Right" Fill="{DynamicResource PaneBorderBrush}" Opacity="0"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="ResizeDirection" Value="Rows">
                            <Setter TargetName="line" Property="Width" Value="Auto"/>
                            <Setter TargetName="line" Property="Height" Value="1"/>
                            <Setter TargetName="line" Property="HorizontalAlignment" Value="Stretch"/>
                            <Setter TargetName="line" Property="VerticalAlignment" Value="Bottom"/>
                        </Trigger>
                        <Trigger Property="Tag" Value="Right">
                            <Setter TargetName="line" Property="HorizontalAlignment" Value="Left"/>
                        </Trigger>
                        <Trigger Property="Tag" Value="Bottom">
                            <Setter TargetName="line" Property="VerticalAlignment" Value="Top"/>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="line" Property="Opacity" Value="1"/></Trigger>
                        <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="line" Property="Opacity" Value="1"/>
                            <Setter TargetName="line" Property="Fill" Value="{DynamicResource SelectionAccent}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
                """);
            _splitter.DragCompleted += (_, _) => { RememberSize(); LayoutChanged?.Invoke(); };
            _splitter.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { RememberSize(); LayoutChanged?.Invoke(); } };

            // The collapse chevron, styled like the rail's other buttons, at the rail's start.
            _toggle = new Button { FontSize = 16 };
            _toggle.SetResourceReference(StyleProperty, "RailButton");
            _toggle.Click += (_, _) => { if (IsMinimized) Restore(); else Minimize(); };
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
            if (side is not DockSide target || !Allows(target)) return false;
            if (target == Side) { if (IsMinimized) Restore(); else Minimize(); }
            else DockTo(target);
            e.Handled = true;
            return true;
        }

        public void DockTo(DockSide side)
        {
            if (!Allows(side)) return;
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

        public void RefreshLayout() => Relayout();

        private void RememberSize()
        {
            if (IsMinimized || _pane is not FrameworkElement pane || !pane.IsVisible) return;
            if (IsHorizontal) { if (pane.ActualHeight > 0) PaneHeight = pane.ActualHeight; }
            else if (pane.ActualWidth > 0) PaneWidth = pane.ActualWidth;
        }

        /// <summary>Lays out the tracks for the current side and state: pane, splitter, then the
        /// rail and body in order from the docked edge. A minimized or hidden pane leaves the rail
        /// and body.</summary>
        private void Relayout()
        {
            // Keep the body attached while folding or moving a pane. Nested hosts otherwise
            // unload the editor and its popups during a layout-only change.
            foreach (var child in Children.Cast<UIElement>().Where(child => child != _inner).ToArray()) Children.Remove(child);
            RowDefinitions.Clear();
            ColumnDefinitions.Clear();
            foreach (var child in _inner.Children.Cast<UIElement>().Where(child => child != _body && child != _railGrid).ToArray())
                _inner.Children.Remove(child);
            _inner.RowDefinitions.Clear();
            _inner.ColumnDefinitions.Clear();
            _railGrid.Children.Clear();
            if (_body == null) return;

            bool horizontal = IsHorizontal;
            bool atStart = Side is DockSide.Left or DockSide.Top;
            bool paneOpen = _pane != null && ShowPane && !IsMinimized;

            LayoutRail(horizontal);
            // The rail and body share one track, so the splitter resizes the pane against both.
            var inner = new List<(GridLength Size, UIElement Element)>
            {
                (new GridLength(RailSize), _railGrid),
                (new GridLength(1, GridUnitType.Star), _body),
            };
            if (!atStart) inner.Reverse();
            for (int i = 0; i < inner.Count; i++)
            {
                if (horizontal) _inner.RowDefinitions.Add(new RowDefinition { Height = inner[i].Size });
                else _inner.ColumnDefinitions.Add(new ColumnDefinition { Width = inner[i].Size });
                if (horizontal) { SetRow(inner[i].Element, i); SetColumn(inner[i].Element, 0); }
                else { SetColumn(inner[i].Element, i); SetRow(inner[i].Element, 0); }
                if (!_inner.Children.Contains(inner[i].Element)) _inner.Children.Add(inner[i].Element);
            }

            if (!paneOpen)
            {
                if (!Children.Contains(_inner)) Children.Add(_inner);
                return;
            }

            var tracks = new List<(GridLength Size, UIElement Element, double Min)>
            {
                (new GridLength(Math.Max(MinPaneSize, horizontal ? PaneHeight : PaneWidth)), _pane!, MinPaneSize),
                (new GridLength(0), _splitter, 0),
                (new GridLength(1, GridUnitType.Star), _inner, MinPaneSize),
            };
            if (!atStart) tracks.Reverse();

            for (int i = 0; i < tracks.Count; i++)
            {
                var (size, element, min) = tracks[i];
                if (horizontal) RowDefinitions.Add(new RowDefinition { Height = size, MinHeight = min });
                else ColumnDefinitions.Add(new ColumnDefinition { Width = size, MinWidth = min });
                if (horizontal) { SetRow(element, i); SetColumn(element, 0); }
                else { SetColumn(element, i); SetRow(element, 0); }
                if (!Children.Contains(element)) Children.Add(element);
            }
            SetZIndex(_inner, 1);

            _splitter.ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns;
            // The resize target occupies the pane's last pixels, leaving the rail unobstructed.
            _splitter.Tag = Side.ToString();
            _splitter.Width = horizontal ? double.NaN : SplitterSize;
            _splitter.Height = horizontal ? SplitterSize : double.NaN;
            _splitter.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            _splitter.VerticalAlignment = horizontal ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            _splitter.Margin = horizontal
                ? new Thickness(0, atStart ? -SplitterSize : 0, 0, atStart ? 0 : -SplitterSize)
                : new Thickness(atStart ? -SplitterSize : 0, 0, atStart ? 0 : -SplitterSize, 0);
            _splitter.Cursor = horizontal ? Cursors.SizeNS : Cursors.SizeWE;
            AutomationProperties.SetName(_splitter, ResizeText);
        }

        /// <summary>Open, the chevron points at the edge the pane folds into; minimized, back out
        /// of it. Its tooltip says which.</summary>
        private void UpdateToggle()
        {
            if (_toggle == null) return;
            bool open = !IsMinimized;
            _toggle.Content = (Side, open) switch
            {
                (DockSide.Left, true) or (DockSide.Right, false) => "\uE76B",
                (DockSide.Right, true) or (DockSide.Left, false) => "\uE76C",
                (DockSide.Top, true) or (DockSide.Bottom, false) => "\uE70E",
                _ => "\uE70D",
            };
            string text = open ? _collapseText : _restoreText;
            _toggle.ToolTip = text;
            AutomationProperties.SetName(_toggle, text);
        }

        /// <summary>The rail for the current side: the chevron at its start, the app's buttons at its
        /// far end, running down a column or across a row.</summary>
        private void LayoutRail(bool across)
        {
            _railGrid.RowDefinitions.Clear();
            _railGrid.ColumnDefinitions.Clear();
            _railGrid.Margin = across ? new Thickness(6, 2, 6, 2) : new Thickness(2, 6, 2, 6);

            _toggle.Visibility = _pane != null && ShowPane ? Visibility.Visible : Visibility.Collapsed;
            UpdateToggle();

            if (across)
            {
                _railGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _railGrid.ColumnDefinitions.Add(new ColumnDefinition());
                _railGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            else
            {
                _railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _railGrid.RowDefinitions.Add(new RowDefinition());
                _railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            _toggle.HorizontalAlignment = HorizontalAlignment.Center;
            _toggle.VerticalAlignment = VerticalAlignment.Center;
            SetRow(_toggle, 0);
            SetColumn(_toggle, 0);
            _railGrid.Children.Add(_toggle);

            if (_rail == null) return;
            if (_rail is StackPanel stack)
            {
                stack.Orientation = across ? Orientation.Horizontal : Orientation.Vertical;
                if (across != _railAcross)
                    foreach (var child in stack.Children.OfType<FrameworkElement>())
                        child.Margin = new Thickness(child.Margin.Top, child.Margin.Left, child.Margin.Bottom, child.Margin.Right);
            }
            _railAcross = across;
            if (_rail is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            if (across) { SetColumn(_rail, 2); SetRow(_rail, 0); }
            else { SetRow(_rail, 2); SetColumn(_rail, 0); }
            _railGrid.Children.Add(_rail);
        }
    }
}
