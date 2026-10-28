using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>
/// The album manager, the KillerShot counterpart of the KillerNotes and Killendar database
/// dialogs: a modal card with the wordmark and "Albums" in its caption, a close button, and a
/// draggable title bar. The album and library controls themselves stay in MainWindow, where their
/// handlers live; the window lends them to this card while it is open and takes them back when it
/// closes.
/// </summary>
internal sealed class AlbumsDialog : Window
{
    private readonly ContentControl _host = new();

    private static string L(string key) => LocaleManager.Loc(key);

    public AlbumsDialog(Window owner, FrameworkElement body)
    {
        Owner = owner;
        Title = "KillerShot: " + L("Str_Album_Title");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TaskbarIdentity.Track(this);
        _host.Content = body;
        Content = BuildCard();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } };
        Loaded += (_, _) => KillerFamily.Windowing.WorkAreaFit.Fit(this, keepMargin: true);
    }

    /// <summary>Takes the body out again so MainWindow can put it back where it lives.</summary>
    public void Release() => _host.Content = null;

    private FrameworkElement BuildCard()
    {
        var caption = new Grid { Background = Brushes.Transparent };
        caption.SetResourceReference(HeightProperty, "DialogTitleBarHeight");
        caption.SetResourceReference(MarginProperty, "DialogTitleMargin");
        caption.Children.Add(DialogChrome.Wordmark(L("Str_Album_Title")));
        var close = DialogChrome.CloseGlyph(L("Str_Sys_Close"), Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        caption.Children.Add(close);
        caption.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        var titleBand = new Border { Child = caption };
        Panel.SetZIndex(titleBand, 1);
        titleBand.SetResourceReference(Border.BackgroundProperty, "DialogTitleBarBrush");
        titleBand.SetResourceReference(Border.CornerRadiusProperty, "DialogTitleCornerRadius");

        var rows = new DockPanel();
        DockPanel.SetDock(titleBand, Dock.Top);
        rows.Children.Add(titleBand);
        rows.Children.Add(_host);

        var layers = new Grid();
        var grain = new Border { IsHitTestVisible = false };
        grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
        grain.SetResourceReference(OpacityProperty, "GrainOpacity");
        grain.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
        layers.Children.Add(grain);
        // The raised dialog bevel, as on the confirm dialog: drawn only by 98SE.
        foreach (var (brush, thickness, inner) in new[]
        {
            ("FrameOuterLightBrush", "FrameOuterLightThickness", false), ("FrameOuterDarkBrush", "FrameOuterDarkThickness", false),
            ("FrameInnerLightBrush", "FrameInnerLightThickness", true), ("FrameInnerDarkBrush", "FrameInnerDarkThickness", true),
        })
        {
            var bevel = new Border { IsHitTestVisible = false };
            Panel.SetZIndex(bevel, 6);
            bevel.SetResourceReference(Border.BorderBrushProperty, brush);
            bevel.SetResourceReference(Border.BorderThicknessProperty, thickness);
            if (inner) bevel.SetResourceReference(MarginProperty, "FrameInnerMargin");
            layers.Children.Add(bevel);
        }
        var content = new Grid { Children = { rows } };
        content.SetResourceReference(MarginProperty, "DialogContentMargin");
        layers.Children.Add(content);

        var card = new PickerBorder { Child = layers, Opacity = 0 };
        card.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        card.SetResourceReference(Border.BorderThicknessProperty, "DialogOuterBorderThickness");
        card.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
        card.SetResourceReference(MarginProperty, "DialogHaloMargin");
        // Scaled by the theme: 0 on a flat theme, so a 98SE dialog sits flat like a Win98 one.
        card.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270,
            Opacity = TryFindResource("FlyoutShadowOpacity") is double opacity ? opacity : .5 };
        Loaded += (_, _) => Anim.FadeIn(card);
        return card;
    }
}
