using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShot.Controls;
using KillerShot.Services;
using KillerTools.Qr;

namespace KillerShot.Shell;

public partial class MainWindow
{
    private ToolTip? _qrResultCard;

    private void ShowQrCard(QrResult result, Button anchor)
    {
        if (_qrResultCard != null) _qrResultCard.IsOpen = false;
        var content = new StackPanel { Width = 340 };
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);
        var heading = new TextBlock { Text = "QR", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) };
        content.Children.Add(heading);
        var text = new TextBox
        {
            Text = result.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8)
        };
        text.SetResourceReference(ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(text, "QR");
        content.Children.Add(text);
        var link = QrLink.Check(result.Text);
        foreach (string warning in link.Warnings)
        {
            var label = new TextBlock { Text = LocaleManager.Loc("Str_QrWarn_" + warning), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            content.Children.Add(label);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        Button Action(string key, string id, Action click)
        {
            var button = new Button { Content = LocaleManager.Loc(key), Margin = new Thickness(0, 0, 6, 0) };
            button.SetResourceReference(StyleProperty, "OutlineButton");
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => click();
            actions.Children.Add(button);
            return button;
        }
        var copy = Action("Str_Btn_Copy", "QrCopy", () =>
        {
            try { Clipboard.SetText(result.Text); }
            catch (Exception error) { StatusText.Text = error.Message; }
        });
        if (QrPayload.WifiPassword(result.Text) is { Length: > 0 } password)
            Action("Str_Qr_CopyPassword", "QrCopyPassword", () =>
            {
                try { Clipboard.SetText(password); }
                catch (Exception error) { StatusText.Text = error.Message; }
            });
        if (link.Disposition is QrLinkDisposition.Allowed or QrLinkDisposition.Warning)
            Action("Str_Btn_Open", "QrOpen", () =>
            {
                if (_qrResultCard != null) _qrResultCard.IsOpen = false;
                var checkedLink = QrLink.Check(result.Text);
                if (checkedLink.Url == null || checkedLink.Disposition is not (QrLinkDisposition.Allowed or QrLinkDisposition.Warning)) return;
                if (checkedLink.Disposition == QrLinkDisposition.Warning)
                {
                    string warnings = string.Join('\n', checkedLink.Warnings.Select(w => LocaleManager.Loc("Str_QrWarn_" + w)));
                    var confirm = new ConfirmDialog(LocaleManager.Loc("Str_Btn_Open"), warnings + "\n\n" + checkedLink.Url.AbsoluteUri,
                        LocaleManager.Loc("Str_Btn_Open"), LocaleManager.Loc("Str_Btn_Cancel")) { Owner = this };
                    confirm.ShowDialog();
                    if (!confirm.Confirmed) return;
                }
                WebLink.Open(checkedLink.Url.AbsoluteUri);
            });
        content.Children.Add(actions);
        var tooltip = new ToolTip
        {
            Content = content, PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = true, Focusable = true, Style = (Style)FindResource(typeof(ToolTip))
        };
        _qrResultCard = tooltip;
        if (FindName("QrResultCard") != null) UnregisterName("QrResultCard");
        RegisterName("QrResultCard", tooltip);
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            tooltip.IsOpen = false;
            if (IsActive) anchor.Focus();
        };
        // An inactive window can prepare its card without displaying a popup over another app.
        if (IsActive)
        {
            tooltip.IsOpen = true;
            Dispatcher.BeginInvoke(() => copy.Focus());
        }
    }
}
