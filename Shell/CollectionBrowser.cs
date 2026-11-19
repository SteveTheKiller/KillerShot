using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using KillerShot.Services;

namespace KillerShot.Shell;

internal sealed record CollectionChoice(string Source, string StoredName, IReadOnlyList<CaptureItem> Preview, int Count, string Style,
    string? StatusKey = null, string? Path = null)
{
    public string Name => Source == "all" ? LocaleManager.Loc("Str_Strip_All")
        : Source == "c0" ? LocaleManager.Loc("Str_Lib_Unfiled") : StoredName;
    public string? Status => StatusKey == null ? null : LocaleManager.Loc(StatusKey);
    public string Detail => Status ?? string.Format(CultureInfo.CurrentCulture,
        LocaleManager.Loc(Count == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), Count);
    public string AccessibleName => Name + ", " + Detail;
}

internal sealed class CollectionBrowser : ListBox
{
    /// <summary>Fills a row's own card. Returns false for rows that have no card of their own
    /// (All captures, Unfiled), which keep the sidebar's menu.</summary>
    internal Func<CollectionChoice, ContextMenu, bool>? RowMenu { get; set; }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not ListBoxItem row) return;
        row.ContextMenuOpening -= Row_ContextMenuOpening;
        row.ContextMenu = null;
        if (item is not CollectionChoice choice || RowMenu is not { } fill) return;
        var probe = new ContextMenu();
        if (!fill(choice, probe)) return;
        row.ContextMenu = probe;
        row.ContextMenuOpening += Row_ContextMenuOpening;
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListBoxItem row) { row.ContextMenuOpening -= Row_ContextMenuOpening; row.ContextMenu = null; }
        base.ClearContainerForItemOverride(element, item);
    }

    // The card is rebuilt each time it opens, so names, counts and KillerPDF are current.
    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: CollectionChoice choice, ContextMenu: { } menu } row || RowMenu is not { } fill) return;
        if (_pendingCard != null || menu.IsOpen) { e.Handled = true; return; }
        fill(choice, menu);
        HoldToolTips(row, menu);
    }

    // A right-click on another collection selects it on button down, and the album reloads for it. The reload
    // replaces the rows, so the card the click was about to open belonged to a row that no longer existed and the
    // first right-click showed nothing. The click is noted here and the card opens on the collection's new row
    // once the reload has settled, at the pointer, the way a right-click card does.
    private (string Source, string StoredName)? _pendingCard;

    protected override void OnPreviewMouseRightButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);
        if (ContainerFromElement(this, e.OriginalSource as DependencyObject) is not ListBoxItem { IsSelected: false, DataContext: CollectionChoice choice, ContextMenu: not null }) return;
        _pendingCard = (choice.Source, choice.StoredName);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, OpenPendingCard);
    }

    private void OpenPendingCard()
    {
        if (_pendingCard is not { } key) return;
        _pendingCard = null;
        if (RowMenu is not { } fill) return;
        var item = Items.OfType<CollectionChoice>().FirstOrDefault(c => c.Source == key.Source && c.StoredName == key.StoredName);
        if (item == null || ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem { ContextMenu: { } card } row || card.IsOpen) return;
        fill(item, card);
        HoldToolTips(row, card);
        card.PlacementTarget = row;
        card.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        card.IsOpen = true;
    }

    /// <summary>The pointer stays on the tile while its card is open, so the tile's tooltip would
    /// open again on top of the card's header. Tooltips under the row stay off until the card closes.</summary>
    private static void HoldToolTips(ListBoxItem row, ContextMenu menu)
    {
        var held = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement { ToolTip: not null } tipped && ToolTipService.GetIsEnabled(tipped))
            {
                ToolTipService.SetIsEnabled(tipped, false);
                held.Add(tipped);
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(row);
        if (held.Count == 0) return;
        // A tooltip that opened while the pointer rested on the tile stays up behind a card opened in code, since
        // only a real right-click tells the tooltip service to close it.
        foreach (var source in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().ToList())
        {
            var root = source.RootVisual;
            var tip = root as ToolTip ?? (root != null && VisualTreeHelper.GetChildrenCount(root) > 0 ? VisualTreeHelper.GetChild(root, 0) as ToolTip : null);
            if (tip is { IsOpen: true, PlacementTarget: { } target } && held.Contains(target)) tip.IsOpen = false;
        }
        // A tooltip whose hover delay was already running when the card opened in code still shows on top of
        // it, since turning the tooltip off does not stop a pending one. Its opening is refused instead.
        static void Refuse(object sender, ToolTipEventArgs e) => e.Handled = true;
        foreach (var element in held.OfType<FrameworkElement>()) element.ToolTipOpening += Refuse;
        void Release(object? sender, RoutedEventArgs e)
        {
            menu.Closed -= Release;
            foreach (var element in held)
            {
                element.ClearValue(ToolTipService.IsEnabledProperty);
                if (element is FrameworkElement framework) framework.ToolTipOpening -= Refuse;
            }
        }
        menu.Closed += Release;
    }

    public CollectionBrowser()
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Controls.SidebarEdgeFade.Attach(this);
        Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBox">
                <Border Background="{TemplateBinding Background}" ClipToBounds="False">
                    <ScrollViewer x:Name="PART_ScrollViewer" Focusable="False" ClipToBounds="False" CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                        <ScrollViewer.Template><ControlTemplate TargetType="ScrollViewer">
                            <Grid ClipToBounds="False">
                                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Grid.Column="0"
                                    Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}"/>
                                <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Panel.ZIndex="10" Orientation="Vertical" Cursor="Arrow"
                                    Minimum="0" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}"
                                    Value="{Binding VerticalOffset, Mode=OneWay, RelativeSource={RelativeSource TemplatedParent}}"
                                    Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
                            </Grid>
                        </ControlTemplate></ScrollViewer.Template>
                        <!-- The gutters sit inside the viewport, which clips: pile photos reach a few
                             pixels past their card and stay whole there, at the top as at the sides. -->
                        <ItemsPresenter Margin="8,8,8,0"/>
                    </ScrollViewer>
                </Border>
            </ControlTemplate>
            """);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        AutomationProperties.SetName(this, LocaleManager.Loc("Str_Strip_Collection"));
        ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:s="clr-namespace:KillerShot.Shell;assembly=KillerShot">
                <s:CollectionPreview/>
            </DataTemplate>
            """);
        ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   xmlns:c="clr-namespace:KillerShot.Controls;assembly=KillerShot" TargetType="ListBoxItem">
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Padding" Value="5"/>
                <Setter Property="Margin" Value="0,2"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="AutomationProperties.Name" Value="{Binding AccessibleName}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="row" Background="Transparent"
                            CornerRadius="{DynamicResource ControlCornerRadius}" Padding="{TemplateBinding Padding}">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource RowHoverBrush}"/></Trigger>
                        <Trigger Property="c:DropHighlight.IsActive" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource RowHoverBrush}"/></Trigger>
                        <Trigger Property="IsSelected" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource SelectionBg}"/><Setter Property="Foreground" Value="{DynamicResource SelectionFg}"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
    }
}

/// <summary>A small photo preview for a virtualized collection row.</summary>
public sealed class CollectionPreview : StackPanel
{
    private readonly Grid _photos = new() { Height = 78, ClipToBounds = true };
    private readonly Canvas _pile = new() { ClipToBounds = false };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _count = new() { FontSize = 11 };
    private readonly Border _label = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _captions = new();
    private int _generation;
    private readonly List<(Border Frame, CaptureItem Item, int Index)> _scattered = [];
    private uint _scatterSeed;
    private static readonly (double X, double Y)[][] ScatterLayouts =
    [
        [(.27,.28),(.72,.73),(.74,.23),(.25,.77),(.48,.47),(.51,.82),(.80,.47),(.47,.17)],
        [(.24,.20),(.37,.35),(.52,.49),(.69,.66),(.78,.82),(.22,.63),(.70,.24),(.43,.80)],
        [(.50,.17),(.28,.29),(.22,.51),(.31,.74),(.53,.83),(.74,.70),(.79,.44),(.67,.26)],
        [(.21,.27),(.46,.21),(.74,.30),(.66,.49),(.37,.52),(.22,.73),(.50,.80),(.78,.73)],
        [(.20,.20),(.75,.20),(.28,.44),(.69,.44),(.39,.65),(.59,.66),(.26,.83),(.77,.83)],
        [(.50,.18),(.29,.36),(.73,.36),(.50,.49),(.21,.66),(.78,.66),(.48,.81),(.61,.30)],
        [(.23,.24),(.48,.29),(.76,.22),(.26,.50),(.70,.49),(.23,.78),(.49,.72),(.78,.80)],
        [(.75,.18),(.59,.34),(.42,.51),(.26,.69),(.20,.84),(.24,.25),(.77,.67),(.54,.82)],
        [(.29,.18),(.68,.29),(.35,.40),(.73,.51),(.28,.63),(.64,.75),(.43,.84),(.48,.28)],
        [(.21,.36),(.39,.22),(.65,.20),(.80,.38),(.65,.56),(.42,.52),(.27,.76),(.70,.81)],
        [(.24,.19),(.49,.35),(.77,.21),(.22,.51),(.74,.51),(.23,.80),(.51,.66),(.77,.81)],
        [(.39,.18),(.67,.25),(.23,.43),(.54,.45),(.79,.56),(.32,.70),(.60,.81),(.72,.73)],
    ];

    public CollectionPreview()
    {
        Children.Add(_photos);
        _captions.Children.Add(_name);
        _captions.Children.Add(_count);
        var labelContent = new Grid();
        var selectionWash = new Border();
        selectionWash.Style = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
                <Style.Triggers>
                    <MultiDataTrigger><MultiDataTrigger.Conditions>
                        <Condition Binding="{Binding Style}" Value="pile"/>
                        <Condition Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True"/>
                    </MultiDataTrigger.Conditions><Setter Property="Background" Value="{DynamicResource SelectionBg}"/></MultiDataTrigger>
                </Style.Triggers>
            </Style>
            """);
        labelContent.Children.Add(selectionWash);
        labelContent.Children.Add(_captions);
        _label.Child = labelContent;
        _label.Style = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
                <Style.Triggers>
                    <DataTrigger Binding="{Binding Style}" Value="pile">
                        <Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/>
                        <!-- A 1px ring of the background around the selection wash; the text keeps its
                             room inside the wash (Refresh pads the captions). -->
                        <Setter Property="Padding" Value="1"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
            """);
        Children.Add(_label);
        _photos.SizeChanged += (_, _) => PlaceScatteredPhotos();
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { ThemeManager.ThemeChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { ThemeManager.ThemeChanged -= Refresh; _generation++; _photos.Children.Clear(); _pile.Children.Clear(); _scattered.Clear(); };
    }

    /// <summary>The family's bar shadow weight: present on the modern themes, zero on flat ones like 98SE.</summary>
    private double PileShadowOpacity() => TryFindResource("BarShadowOpacity") is double opacity ? opacity : 0;

    internal void Relocalize()
    {
        if (DataContext is not CollectionChoice choice) return;
        _name.Text = choice.Name;
        _count.Text = choice.Detail;
        ToolTip = choice.Path is { } path ? path + "\n" + choice.Detail : choice.AccessibleName;
        DependencyObject? parent = this;
        while (parent != null && parent is not ListBoxItem) parent = VisualTreeHelper.GetParent(parent);
        if (parent is ListBoxItem row) row.GetBindingExpression(AutomationProperties.NameProperty)?.UpdateTarget();
    }

    private async void Refresh()
    {
        int generation = ++_generation;
        _photos.Children.Clear();
        _pile.Children.Clear();
        _scattered.Clear();
        _photos.RowDefinitions.Clear();
        _photos.ColumnDefinitions.Clear();
        if (DataContext is not CollectionChoice choice) return;
        _scatterSeed = 2166136261;
        foreach (char character in choice.Source) _scatterSeed = unchecked((_scatterSeed ^ character) * 16777619);
        _name.Text = choice.Name;
        _count.Text = choice.Detail;
        _photos.Visibility = choice.Preview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Minimized collections: one line each, the name with its count after it, and no photos.
        bool row = choice.Style == "rows";
        _captions.Orientation = row ? Orientation.Horizontal : Orientation.Vertical;
        _name.Margin = row ? new Thickness(0, 3, 8, 3) : new Thickness(0, 4, 0, 0);
        _count.VerticalAlignment = row ? VerticalAlignment.Center : VerticalAlignment.Top;
        _count.Opacity = row ? .7 : 1;
        _photos.Height = choice.Style == "pile" ? 112 : 78;
        _captions.Margin = choice.Style == "pile" ? new Thickness(2, 0, 2, 0) : default;
        _photos.ClipToBounds = choice.Style != "pile";
        if (choice.Style == "pile") _photos.Children.Add(_pile);
        // The pile's name card sits on the photos; a theme-weighted shadow lifts it off them.
        _label.Effect = choice.Style == "pile"
            ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 2, Opacity = PileShadowOpacity() }
            : null;
        ToolTip = choice.Path is { } path ? path + "\n" + choice.Detail : choice.AccessibleName;
        if (!IsLoaded) return;
        var preview = choice.Preview.Take(choice.Style == "pile" ? 12 : 4).Reverse().ToArray();
        var bitmaps = new BitmapSource?[preview.Length];
        if (choice.Style == "grid")
        {
            for (int i = 0; i < 2; i++)
            {
                _photos.RowDefinitions.Add(new RowDefinition());
                _photos.ColumnDefinitions.Add(new ColumnDefinition());
            }
        }
        for (int i = 0; i < preview.Length; i++)
        {
            var image = new Image { Width = 112, Height = 62, Stretch = Stretch.Uniform, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var frame = new Border
            {
                Child = image, Padding = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform(i * 5 - 8, -i * 3),
            };
            frame.SetResourceReference(BackgroundProperty, "ContentPaneBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            frame.BorderThickness = new Thickness(1);
            if (choice.Style == "grid")
            {
                frame.RenderTransform = Transform.Identity;
                frame.HorizontalAlignment = HorizontalAlignment.Stretch;
                frame.VerticalAlignment = VerticalAlignment.Stretch;
                image.Width = double.NaN;
                image.Height = double.NaN;
                Grid.SetRow(frame, i / 2);
                Grid.SetColumn(frame, i % 2);
            }
            else if (choice.Style == "pile")
            {
                frame.Padding = new Thickness(0);
                image.Width = image.Height = double.NaN;
                frame.HorizontalAlignment = HorizontalAlignment.Left;
                frame.VerticalAlignment = VerticalAlignment.Top;
                _scattered.Add((frame, preview[i], i));
                PlaceScatteredPhotos();
                frame.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Opacity = PileShadowOpacity() };
            }
            if (choice.Style == "pile") _pile.Children.Add(frame);
            else _photos.Children.Add(frame);
            var item = preview[i];
            frame.Cursor = Controls.DragCursors.Open;
            ImageDrag.AttachCapture(frame, _ => item);
            try
            {
                if (generation != _generation || !CaptureLibrary.IsCurrent(item)) return;
                var bitmap = await CaptureLibrary.LoadThumbAsync(item);
                if (generation != _generation || !CaptureLibrary.IsCurrent(item)) return;
                image.Source = bitmap;
                bitmaps[i] = bitmap;
            }
            catch (Exception) { /* The collection remains selectable if a preview is unavailable. */ }
        }
        if (choice.Style == "mini" && preview.Length > 0 && generation == _generation)
        {
            _photos.Children.Clear();
            int current = 0;
            var left = new Image { Width = 52, Height = 42, Opacity = .55, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
            var right = new Image { Width = 52, Height = 42, Opacity = .55, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
            var center = new Image { Width = 108, Height = 66, Cursor = Controls.DragCursors.Open };
            ImageDrag.AttachCapture(center, _ => preview[current]);
            void Show()
            {
                center.Source = bitmaps[current];
                left.Source = current > 0 ? bitmaps[current - 1] : null;
                right.Source = current + 1 < bitmaps.Length ? bitmaps[current + 1] : null;
            }
            _photos.Children.Add(left);
            _photos.Children.Add(right);
            _photos.Children.Add(center);
            void Arrow(bool next)
            {
                var button = new Button
                {
                    Content = next ? "\uE76C" : "\uE76B", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    Width = 22, Height = 24, Padding = new Thickness(0),
                    HorizontalAlignment = next ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = LocaleManager.Loc(next ? "Str_PreviewNext" : "Str_PreviewPrevious"),
                };
                AutomationProperties.SetName(button, button.ToolTip.ToString());
                button.Click += (_, e) => { current = (current + (next ? 1 : bitmaps.Length - 1)) % bitmaps.Length; Show(); e.Handled = true; };
                _photos.Children.Add(button);
            }
            Show();
            if (preview.Length > 1) { Arrow(false); Arrow(true); }
        }
    }

    private void PlaceScatteredPhotos()
    {
        double availableWidth = _photos.ActualWidth, availableHeight = _photos.Height;
        if (availableWidth <= 12 || availableHeight <= 12) return;
        _label.MaxWidth = availableWidth;
        foreach (var (frame, item, index) in _scattered)
        {
            uint variation = unchecked((uint)item.Id * 2654435761U ^ _scatterSeed);
            variation ^= variation >> 16;
            double aspect = item.Width > 0 && item.Height > 0 ? (double)item.Width / item.Height : 1.5;
            double photoWidth = Math.Min(Math.Min(140, availableWidth * (.52 + variation % 12 * .012)), 82 * aspect);
            double width = photoWidth + 2;
            double height = photoWidth / aspect + 2;
            double angle = (int)(variation % 43) - 21;
            double radians = angle * Math.PI / 180;
            double halfWidth = (Math.Abs(Math.Cos(radians)) * width + Math.Abs(Math.Sin(radians)) * height) / 2;
            double halfHeight = (Math.Abs(Math.Sin(radians)) * width + Math.Abs(Math.Cos(radians)) * height) / 2;
            var slots = ScatterLayouts[_scatterSeed % (uint)ScatterLayouts.Length];
            var slot = slots[index % slots.Length];
            if (_scattered.Count == 2) slot = index == 0 ? (.24, .35) : (.76, .80);
            double jitter = ((int)(variation % 13) - 6) * .006;
            double y = Math.Clamp((slot.Y - jitter) * availableHeight * 1.35 - 18, halfHeight - 12, availableHeight - halfHeight + 40);
            if (_scattered.Count == 2 && index == 0) y = Math.Max(y, halfHeight + 2);
            double x = (slot.X + jitter) * availableWidth;
            x = Math.Clamp(x, halfWidth - 6, availableWidth - halfWidth + 6);   // the same reach on both sides
            frame.Width = width;
            frame.Height = height;
            Canvas.SetLeft(frame, x - width / 2);
            Canvas.SetTop(frame, y - height / 2);
            frame.RenderTransformOrigin = new Point(.5, .5);
            frame.RenderTransform = new RotateTransform(angle);
        }
    }
}
