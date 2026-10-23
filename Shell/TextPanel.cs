using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Copy text: reads the text in the picture on this computer, copies it, and shows it in a
/// card to select from, with a region reader and the recognition language.</summary>
public partial class MainWindow
{
    internal Func<BitmapSource, Task<string?>> RecognizeTextAsync { get; set; } = TextRecognition.ReadAsync;
    internal Action<string> CopyTextToClipboard { get; set; } = Clipboard.SetText;
    private Popup? _textPopup;
    private TextBox? _textBox;
    private TextBlock? _textStatus;
    private ComboBox? _textLanguage;
    private int _textRead;   // ignores a slow read that a newer one replaced
    private RecognitionSnapshot? _textSnapshot;

    private void CopyTextBtn_Click(object sender, RoutedEventArgs e) => _ = ReadPictureTextAsync(null, copy: true);

    /// <summary>Reads the whole picture, or one area of it, and optionally copies the result.</summary>
    internal async Task ReadPictureTextAsync(Int32Rect? area, bool copy)
    {
        if (_doc == null || _recognitionClosed) return;
        Editor.CommitText();
        SyncRecognitionState();
        var snapshot = CaptureRecognition();
        ShowTextPanel();
        BitmapSource image = _doc.Render();
        if (area is { } a)
        {
            var cropped = new CroppedBitmap(image, ClipToImage(a, image));
            cropped.Freeze();
            image = cropped;
        }
        int read = ++_textRead;
        _textSnapshot = null;
        _textBox!.Text = "";
        _textStatus!.Text = Loc("Str_Ocr_Reading");
        string? text;
        try { text = await RecognizeTextAsync(image); }
        catch (Exception error) { if (read == _textRead && IsCurrentRecognition(snapshot)) _textStatus.Text = error.Message; return; }
        if (read != _textRead || !IsCurrentRecognition(snapshot)) return;
        _textSnapshot = snapshot;
        if (text == null) { _textBox!.Text = ""; _textStatus.Text = Loc("Str_Ocr_Unavailable"); return; }
        _textBox!.Text = text;
        if (text.Trim().Length == 0) { _textStatus.Text = Loc("Str_Ocr_None"); return; }
        _textStatus.Text = "";
        if (copy) CopyRecognizedText();
    }

    private static Int32Rect ClipToImage(Int32Rect a, BitmapSource image)
    {
        int x = Math.Clamp(a.X, 0, image.PixelWidth - 1), y = Math.Clamp(a.Y, 0, image.PixelHeight - 1);
        return new Int32Rect(x, y, Math.Clamp(a.Width, 1, image.PixelWidth - x), Math.Clamp(a.Height, 1, image.PixelHeight - y));
    }

    private void CopyRecognizedText()
    {
        if (_textBox == null || _textBox.Text.Length == 0 || !IsCurrentRecognition(_textSnapshot)) return;
        string text = _textBox.SelectionLength > 0 ? _textBox.SelectedText : _textBox.Text;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!IsCurrentRecognition(_textSnapshot)) return;
            try
            {
                CopyTextToClipboard(text);
                string done = string.Format(CultureInfo.CurrentCulture, Loc("Str_Ocr_Copied"), text.Length);
                _textStatus!.Text = done;
                StatusText.Text = done;
                return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
        }
        _textStatus!.Text = string.Format(Loc("Str_Tray_ClipboardBusy"), "");
    }

    private void ShowTextPanel()
    {
        if (_textPopup == null) BuildTextPanel();
        if (!IsVisible) return;
        RefreshTextLanguages();
        FlyoutPlacement.AttachInsidePane(_textPopup!, CopyTextBtn.IsVisible ? CopyTextBtn : MoreBtn, alignRight: true);
        _textPopup!.IsOpen = true;
    }

    private void BuildTextPanel()
    {
        var content = new StackPanel();
        content.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        content.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        var heading = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) };
        heading.SetResourceReference(TextBlock.TextProperty, "Str_Ocr_Title");
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
        KillerFamily.Theming.HeadingAccent.SetEnabled(heading, true);
        content.Children.Add(heading);

        _textBox = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8),
            ContextMenu = Application.Current.TryFindResource("TextInputContextMenu") as ContextMenu,
        };
        _textBox.SetResourceReference(StyleProperty, "DarkTextBox");
        _textBox.SetResourceReference(AutomationProperties.NameProperty, "Str_Ocr_Title");
        content.Children.Add(_textBox);

        _textStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        _textStatus.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
        AutomationProperties.SetLiveSetting(_textStatus, AutomationLiveSetting.Polite);
        content.Children.Add(_textStatus);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button Action(string key, RoutedEventHandler click)
        {
            var button = new Button { Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 4) };
            button.SetResourceReference(ContentProperty, key);
            button.SetResourceReference(StyleProperty, "OutlineButton");
            button.Click += click;
            buttons.Children.Add(button);
            return button;
        }
        Action("Str_Ocr_CopyAll", (_, _) => CopyRecognizedText());
        Action("Str_Ocr_Region", (_, _) =>
        {
            _textPopup!.IsOpen = false;
            StatusText.Text = Loc("Str_Ocr_RegionHint");
            Editor.BeginRegionPick(area => { if (area is { } a) _ = ReadPictureTextAsync(a, copy: true); });
            Editor.Focus();
        });
        content.Children.Add(buttons);

        var language = new DockPanel();
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        label.SetResourceReference(TextBlock.TextProperty, "Str_Ocr_Language");
        DockPanel.SetDock(label, Dock.Left);
        language.Children.Add(label);
        _textLanguage = new ComboBox { MinWidth = 180 };
        _textLanguage.SetResourceReference(AutomationProperties.NameProperty, "Str_Ocr_Language");
        AutomationProperties.SetLabeledBy(_textLanguage, label);
        _textLanguage.SelectionChanged += (_, _) =>
        {
            if (_syncingTextLanguage || _textLanguage.SelectedItem is not ComboBoxItem { Tag: var tag }) return;
            TextRecognition.LanguageTag = tag as string;
            _ = ReadPictureTextAsync(null, copy: false);
        };
        language.Children.Add(_textLanguage);
        content.Children.Add(language);

        var card = new Border { Width = 420, Child = new ScrollViewer { Content = content, Padding = new Thickness(14), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        card.SetResourceReference(StyleProperty, "FlyoutCard");
        _textPopup = new Popup { Child = card, AllowsTransparency = true, StaysOpen = false };
        card.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { _textPopup.IsOpen = false; e.Handled = true; } };
        _textPopup.Opened += (_, _) => _textBox.Focus();
    }

    internal string TestRecognizedText => _textBox?.Text ?? "";
    internal string TestTextStatus => _textStatus?.Text ?? "";

    private bool _syncingTextLanguage;

    private void RefreshTextLanguages()
    {
        if (_textLanguage == null) return;
        _syncingTextLanguage = true;
        try
        {
            _textLanguage.Items.Clear();
            var windows = new ComboBoxItem { Content = Loc("Str_Ocr_Default"), Tag = null };
            _textLanguage.Items.Add(windows);
            ComboBoxItem chosen = windows;
            foreach (var (tag, name) in TextRecognition.Languages)
            {
                var item = new ComboBoxItem { Content = name, Tag = tag };
                _textLanguage.Items.Add(item);
                if (string.Equals(tag, TextRecognition.LanguageTag, StringComparison.OrdinalIgnoreCase)) chosen = item;
            }
            _textLanguage.SelectedItem = chosen;
        }
        finally { _syncingTextLanguage = false; }
    }
}
