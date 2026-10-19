using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using KillerShot.Services;

namespace KillerShot.Shell;

internal sealed record CollectionChoice(string Source, string Name, IReadOnlyList<CaptureItem> Preview, int Count, string Style)
{
    public string Detail => string.Format(CultureInfo.CurrentCulture,
        LocaleManager.Loc(Count == 1 ? "Str_Lib_CountOne" : "Str_Lib_Count"), Count);
    public string AccessibleName => Name + ", " + Detail;
}

internal sealed class CollectionBrowser : ListBox
{
    public CollectionBrowser()
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
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
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Padding" Value="4"/>
                <Setter Property="Margin" Value="0,2"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="AutomationProperties.Name" Value="{Binding AccessibleName}"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="row" Background="Transparent" BorderBrush="Transparent" BorderThickness="1"
                            CornerRadius="{DynamicResource SmallCornerRadius}" Padding="{TemplateBinding Padding}">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource SelectionBg}"/><Setter TargetName="row" Property="BorderBrush" Value="{DynamicResource SelectionAccent}"/><Setter Property="Foreground" Value="{DynamicResource SelectionFg}"/></Trigger>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="row" Property="BorderBrush" Value="{DynamicResource SelectionAccent}"/></Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="row" Property="BorderBrush" Value="{DynamicResource SelectionAccent}"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
    }
}

/// <summary>A bounded preview for a virtualized collection row.</summary>
public sealed class CollectionPreview : StackPanel
{
    private static readonly SemaphoreSlim LoadSlots = new(2);
    private readonly Grid _photos = new() { Height = 78, ClipToBounds = true };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _count = new() { FontSize = 11 };
    private int _generation;
    private readonly List<(Border Frame, CaptureItem Item, int Index)> _scattered = [];
    private static readonly (double X, double Y)[] ScatterSlots =
        [(.27, .28), (.72, .73), (.74, .23), (.25, .77), (.48, .47), (.51, .82), (.80, .47), (.47, .17)];

    public CollectionPreview()
    {
        Children.Add(_photos);
        Children.Add(_name);
        Children.Add(_count);
        _photos.SizeChanged += (_, _) => PlaceScatteredPhotos();
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { ThemeManager.ThemeChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { ThemeManager.ThemeChanged -= Refresh; _generation++; _photos.Children.Clear(); _scattered.Clear(); };
    }

    private async void Refresh()
    {
        int generation = ++_generation;
        _photos.Children.Clear();
        _scattered.Clear();
        _photos.RowDefinitions.Clear();
        _photos.ColumnDefinitions.Clear();
        if (DataContext is not CollectionChoice choice) return;
        _name.Text = choice.Name;
        _count.Text = choice.Detail;
        _photos.Visibility = choice.Preview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _photos.Height = choice.Style == "pile" ? 140 : 78;
        ToolTip = choice.AccessibleName;
        if (!IsLoaded) return;
        var preview = choice.Preview.Take(choice.Style == "pile" ? 8 : 4).Reverse().ToArray();
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
                Child = image, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center,
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
                image.Width = image.Height = double.NaN;
                frame.HorizontalAlignment = HorizontalAlignment.Left;
                frame.VerticalAlignment = VerticalAlignment.Top;
                _scattered.Add((frame, preview[i], i));
                PlaceScatteredPhotos();
                var shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 2 };
                frame.Effect = shadow;
                shadow.Opacity = TryFindResource("SelectedTileShadowOpacity") is double opacity ? opacity : 0;
            }
            _photos.Children.Add(frame);
            var item = preview[i];
            await LoadSlots.WaitAsync();
            try
            {
                if (generation != _generation || !CaptureLibrary.IsCurrent(item)) return;
                var bitmap = await Task.Run(() => CaptureLibrary.LoadThumb(item));
                if (generation != _generation || !CaptureLibrary.IsCurrent(item)) return;
                image.Source = bitmap;
                bitmaps[i] = bitmap;
            }
            catch (Exception) { /* The collection remains selectable if a preview is unavailable. */ }
            finally { LoadSlots.Release(); }
        }
        if (choice.Style == "mini" && preview.Length > 0 && generation == _generation)
        {
            _photos.Children.Clear();
            int current = 0;
            var left = new Image { Width = 52, Height = 42, Opacity = .55, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
            var right = new Image { Width = 52, Height = 42, Opacity = .55, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
            var center = new Image { Width = 108, Height = 66, IsHitTestVisible = false };
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
        foreach (var (frame, item, index) in _scattered)
        {
            double aspect = item.Width > 0 && item.Height > 0 ? (double)item.Width / item.Height : 1.5;
            double width = Math.Min(Math.Min(100, availableWidth * (.43 + index % 3 * .025)), 60 * aspect);
            double height = width / aspect;
            double angle = ((item.Id % 37 + 37) % 37) - 18;
            double radians = angle * Math.PI / 180;
            double halfWidth = (Math.Abs(Math.Cos(radians)) * width + Math.Abs(Math.Sin(radians)) * height) / 2;
            double halfHeight = (Math.Abs(Math.Sin(radians)) * width + Math.Abs(Math.Cos(radians)) * height) / 2;
            var slot = ScatterSlots[index % ScatterSlots.Length];
            double jitter = ((item.Id % 11 + 11) % 11 - 5) * .006;
            double x = Math.Clamp((slot.X + jitter) * availableWidth, halfWidth + 6, availableWidth - halfWidth - 6);
            double y = Math.Clamp((slot.Y - jitter) * availableHeight, halfHeight + 6, availableHeight - halfHeight - 6);
            frame.Width = width;
            frame.Height = height;
            frame.Margin = new Thickness(x - width / 2, y - height / 2, 0, 0);
            frame.RenderTransformOrigin = new Point(.5, .5);
            frame.RenderTransform = new RotateTransform(angle);
        }
    }
}
