using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Editor;

namespace KillerShot.Shell;

/// <summary>Review first: the capture waits in the editor with nothing copied, saved or handed on.
/// Copy and save (Ctrl+Enter) sends the edited picture to the chosen places; Discard closes it.
/// Replacing the picture any other way sends nothing.</summary>
public partial class MainWindow
{
    private Border? _reviewBar;
    private (EditorDocument Doc, DateTime Taken, CaptureMode Mode)? _review;

    internal bool InReview => _review is { } r && ReferenceEquals(r.Doc, _doc);

    /// <summary>Holds the open capture for review.</summary>
    internal void BeginReview(EditorDocument doc, DateTime taken, CaptureMode mode)
    {
        _review = (doc, taken, mode);
        ShowReviewBar();
    }

    /// <summary>Called after every document change: a replaced picture leaves review unsent.</summary>
    private void SyncReview()
    {
        if (_review is { } r && !ReferenceEquals(r.Doc, _doc)) _review = null;
        if (_reviewBar != null) _reviewBar.Visibility = InReview ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Copy and save: the edited, flattened picture goes out now.</summary>
    internal bool AcceptReview()
    {
        if (!InReview || App.Instance.Capture is not { } capture) return false;
        var (doc, taken, mode) = _review!.Value;
        Editor.CommitText();
        BitmapSource output = doc.Render();
        _review = null;
        SyncReview();
        string? saved = capture.Publish(output, taken, mode, openEditor: false);
        if (saved != null && doc.FilePath == null) doc.FilePath = saved;
        StatusText.Text = Loc("Str_Review_Sent");
        return true;
    }

    internal void TestDiscardReview() => DiscardReview();

    private void DiscardReview()
    {
        if (!InReview) return;
        _review = null;
        SyncReview();
        App.Instance.SetDocument(null);
        StatusText.Text = Loc("Str_Review_Discarded");
    }

    private void ShowReviewBar()
    {
        if (_reviewBar == null)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 360 };
            text.SetResourceReference(TextBlock.TextProperty, "Str_Review_Hint");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var accept = new Button { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
            accept.SetResourceReference(ContentProperty, "Str_Review_Accept");
            accept.SetResourceReference(StyleProperty, "PrimaryButton");
            accept.ToolTip = "Ctrl+Enter";
            accept.Click += (_, _) => AcceptReview();
            var discard = new Button { Padding = new Thickness(12, 4, 12, 4) };
            discard.SetResourceReference(ContentProperty, "Str_Review_Discard");
            discard.SetResourceReference(StyleProperty, "OutlineButton");
            discard.Click += (_, _) => DiscardReview();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(text);
            row.Children.Add(accept);
            row.Children.Add(discard);
            _reviewBar = new Border
            {
                Child = row, Padding = new Thickness(14, 10, 14, 10), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 16),
            };
            _reviewBar.SetResourceReference(StyleProperty, "FlyoutCard");
            AutomationProperties.SetLiveSetting(_reviewBar, AutomationLiveSetting.Assertive);
            _reviewBar.SetResourceReference(AutomationProperties.NameProperty, "Str_Review_Hint");
            Panel.SetZIndex(_reviewBar, 50);
            ((Panel)ContentPane.Child).Children.Add(_reviewBar);
        }
        SyncReview();
    }
}
