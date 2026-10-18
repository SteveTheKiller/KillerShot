using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>One edited screenshot, previewed with the same page layout sent to the printer.</summary>
internal sealed class PrintPreviewWindow : Window
{
    private readonly BitmapSource _picture;
    private readonly string _jobName;
    private LocalPrintServer? _server;
    private readonly List<PrintQueue> _queues = [];
    private readonly ComboBox _printers = new(), _orientation = new(), _scale = new(), _paper = new(), _color = new();
    private readonly TextBox _copies = new() { Text = "1" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _preview = new();
    private readonly Button _print = new();
    private readonly List<PageMediaSize> _paperSizes = [];
    private bool _loading, _printing, _initialPreview = true;
    private double _pageWidth = 816, _pageHeight = 1056, _originX = 24, _originY = 24, _areaWidth = 768, _areaHeight = 1008;
    public bool Printed { get; private set; }

    private static string L(string key) => LocaleManager.Loc(key);

    public PrintPreviewWindow(Window? owner, BitmapSource picture, string jobName)
    {
        _picture = picture;
        _jobName = jobName;
        Owner = owner;
        Title = "KillerShot: " + L("Str_Btn_Print");
        Width = 940; Height = 720; MinWidth = 700; MinHeight = 480;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "TextBrush");
        UseLayoutRounding = true;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        { CaptionHeight = 0, ResizeBorderThickness = new Thickness(8), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        BuildUi();
        _loading = true;
        _orientation.Items.Add(L("Str_Print_Portrait")); _orientation.Items.Add(L("Str_Print_Landscape"));
        _orientation.SelectedIndex = CaptureSettings.PrintRotate && picture.PixelWidth > picture.PixelHeight ? 1 : 0;
        _scale.Items.Add(L("Str_Print_Fit")); _scale.Items.Add(L("Str_Print_Actual"));
        _scale.SelectedIndex = CaptureSettings.PrintShrink ? 0 : 1;
        _color.Items.Add(L("Str_Print_Color")); _color.Items.Add(L("Str_Print_BW")); _color.SelectedIndex = 0;
        LoadPrinters();
        _loading = false;
        RefreshPage();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_printing) Close(); };
        Closing += (_, e) => { if (_printing) e.Cancel = true; };
        Closed += (_, _) => _server?.Dispose();
    }

    private void BuildUi()
    {
        DialogChrome.AppName = "KillerShot";
        DialogChrome.IconUri = new Uri("pack://application:,,,/Resources/kst-icon.ico");
        var caption = new Grid { Height = 40, Background = Brushes.Transparent, Margin = new Thickness(8, 0, 4, 0) };
        caption.Children.Add(DialogChrome.Wordmark(L("Str_Btn_Print")));
        var close = DialogChrome.CloseGlyph(L("Str_Sys_Close"), () => Close());
        if (TryFindResource("AboutCloseGlyph") == null && close is Button closeButton)
        {
            closeButton.Content = "\u2715";
            closeButton.Width = 28;
            closeButton.Height = 26;
        }
        close.HorizontalAlignment = HorizontalAlignment.Right;
        caption.Children.Add(close);
        caption.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        var settings = new StackPanel { Margin = new Thickness(16, 6, 16, 12) };
        void Field(string key, Control control)
        {
            var label = new TextBlock { Text = L(key), Margin = new Thickness(0, 10, 0, 4), FontSize = 12 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            settings.Children.Add(label);
            control.MinHeight = 28;
            AutomationProperties.SetName(control, L(key));
            settings.Children.Add(control);
        }
        Field("Str_Print_Printer", _printers);
        Field("Str_Print_Paper", _paper);
        Field("Str_Print_Orientation", _orientation);
        Field("Str_Print_Scale", _scale);
        Field("Str_Print_Color", _color);
        Field("Str_Print_Copies", _copies);
        _status.Margin = new Thickness(0, 14, 0, 0);
        settings.Children.Add(_status);
        foreach (var combo in new[] { _orientation, _scale, _paper, _color }) combo.SelectionChanged += (_, _) => { if (!_loading) RefreshPage(); };
        _printers.SelectionChanged += (_, _) => { if (!_loading) { LoadPaper(); RefreshPage(); } };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var scroller = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(scroller);
        var previewBackground = new Border { Margin = new Thickness(0, 8, 16, 12), Padding = new Thickness(18) };
        previewBackground.SetResourceReference(Border.BackgroundProperty, "ContentPaneBrush");
        previewBackground.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
        previewBackground.Child = new Viewbox { Stretch = Stretch.Uniform, Child = _preview };
        Grid.SetColumn(previewBackground, 1); body.Children.Add(previewBackground);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        var cancel = new Button { Content = L("Str_Btn_Cancel"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "SurfaceButton"); cancel.Click += (_, _) => Close();
        _print.Content = L("Str_Btn_Print"); _print.MinWidth = 80; _print.Padding = new Thickness(12, 5, 12, 5);
        _print.SetResourceReference(StyleProperty, "OutlineButton"); _print.Click += (_, _) => Print();
        footer.Children.Add(cancel); footer.Children.Add(_print);
        var contents = new DockPanel();
        DockPanel.SetDock(caption, Dock.Top); contents.Children.Add(caption);
        DockPanel.SetDock(footer, Dock.Bottom); contents.Children.Add(footer); contents.Children.Add(body);
        var layers = new Grid();
        var grain = new Border { IsHitTestVisible = false };
        grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush"); grain.SetResourceReference(OpacityProperty, "GrainOpacity");
        grain.SetResourceReference(VisibilityProperty, "FilmGrainVisibility"); layers.Children.Add(grain);
        layers.Children.Add(contents);
        var card = new PickerBorder { Margin = new Thickness(12), BorderThickness = new Thickness(1), Child = layers };
        card.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        card.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
        Content = card;
    }

    private PrintQueue? Queue => _printers.SelectedIndex >= 0 && _printers.SelectedIndex < _queues.Count ? _queues[_printers.SelectedIndex] : null;

    private void LoadPrinters()
    {
        try
        {
            _server = new LocalPrintServer();
            foreach (var queue in _server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            { _queues.Add(queue); _printers.Items.Add(queue.FullName); }
            string? defaultName = _server.DefaultPrintQueue?.FullName;
            _printers.SelectedIndex = Math.Max(0, _queues.FindIndex(q => q.FullName == defaultName));
            LoadPaper();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void LoadPaper()
    {
        bool loading = _loading; _loading = true;
        try
        {
            _paperSizes.Clear(); _paper.Items.Clear();
            if (Queue is not { } queue) return;
            foreach (var size in queue.GetPrintCapabilities().PageMediaSizeCapability.Where(s => s.Width > 0 && s.Height > 0))
            { _paperSizes.Add(size); _paper.Items.Add(size.PageMediaSizeName?.ToString() ?? $"{size.Width:0} x {size.Height:0}"); }
            var current = queue.DefaultPrintTicket?.PageMediaSize;
            _paper.SelectedIndex = Math.Max(0, _paperSizes.FindIndex(s => s.PageMediaSizeName == current?.PageMediaSizeName));
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _loading = loading; }
    }

    private PrintTicket Ticket()
    {
        var ticket = Queue?.DefaultPrintTicket?.Clone() ?? new PrintTicket();
        ticket.PageOrientation = _orientation.SelectedIndex == 1 ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.OutputColor = _color.SelectedIndex == 1 ? OutputColor.Grayscale : OutputColor.Color;
        ticket.CopyCount = int.TryParse(_copies.Text, out int n) ? Math.Clamp(n, 1, 999) : 1;
        if (_paper.SelectedIndex >= 0 && _paper.SelectedIndex < _paperSizes.Count) ticket.PageMediaSize = _paperSizes[_paper.SelectedIndex];
        return ticket;
    }

    private void RefreshPage()
    {
        try
        {
            var ticket = Ticket();
            double w = ticket.PageMediaSize?.Width ?? 816, h = ticket.PageMediaSize?.Height ?? 1056;
            bool landscape = _orientation.SelectedIndex == 1;
            _pageWidth = landscape ? Math.Max(w, h) : Math.Min(w, h);
            _pageHeight = landscape ? Math.Min(w, h) : Math.Max(w, h);
            var area = Queue?.GetPrintCapabilities(ticket).PageImageableArea;
            _originX = area?.OriginWidth ?? 24; _originY = area?.OriginHeight ?? 24;
            _areaWidth = area?.ExtentWidth ?? _pageWidth - 48; _areaHeight = area?.ExtentHeight ?? _pageHeight - 48;
            if (_initialPreview)
            {
                _initialPreview = false;
                double fit = Math.Min(_areaWidth / _picture.PixelWidth, _areaHeight / _picture.PixelHeight);
                _loading = true;
                _scale.SelectedIndex = (fit < 1 ? CaptureSettings.PrintShrink : CaptureSettings.PrintEnlarge) ? 0 : 1;
                _loading = false;
            }
            _preview.Width = _pageWidth; _preview.Height = _pageHeight;
            _preview.Children.Clear(); _preview.Children.Add(CreateSheet());
            _print.IsEnabled = Queue != null;
        }
        catch (Exception ex) { _status.Text = ex.Message; _print.IsEnabled = false; }
    }

    internal Grid CreateSheet()
    {
        BitmapSource bitmap = _picture;
        if (_color.SelectedIndex == 1)
        {
            bitmap = new FormatConvertedBitmap(_picture, PixelFormats.Gray8, null, 0);
            bitmap.Freeze();
        }
        double fit = Math.Min(_areaWidth / bitmap.PixelWidth, _areaHeight / bitmap.PixelHeight);
        double scale = _scale.SelectedIndex == 1 ? 1 : fit;
        var sheet = new Grid { Width = _pageWidth, Height = _pageHeight, Background = Brushes.White, ClipToBounds = true };
        var image = new Image { Source = bitmap, Width = bitmap.PixelWidth * scale, Height = bitmap.PixelHeight * scale, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(image, _jobName);
        var printable = new Canvas { Width = _areaWidth, Height = _areaHeight, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(_originX, _originY, 0, 0) };
        Canvas.SetLeft(image, (_areaWidth - image.Width) / 2); Canvas.SetTop(image, (_areaHeight - image.Height) / 2);
        printable.Children.Add(image); sheet.Children.Add(printable);
        return sheet;
    }

    private void Print()
    {
        if (_printing || Queue is not { } queue) return;
        if (!int.TryParse(_copies.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int copies) || copies is < 1 or > 999)
        { _copies.Focus(); _copies.SelectAll(); return; }
        _printing = true; _print.IsEnabled = false;
        try
        {
            var ticket = Ticket();
            ticket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
            var dialog = new PrintDialog { PrintQueue = queue, PrintTicket = ticket };
            var sheet = CreateSheet();
            sheet.Measure(new Size(_pageWidth, _pageHeight)); sheet.Arrange(new Rect(0, 0, _pageWidth, _pageHeight));
            // PrintVisual begins at the driver's printable origin; compensate so the preview and job agree.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(sheet), null, new Rect(-_originX, -_originY, _pageWidth, _pageHeight));
            dialog.PrintVisual(visual, _jobName);
            Printed = true;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _printing = false; _print.IsEnabled = Queue != null; }
        if (Printed) Close();
    }
}
