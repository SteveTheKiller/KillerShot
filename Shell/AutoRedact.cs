using System.Windows;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Auto redact: the Redact settings dialog from the toolbar, and the scan that reads the
/// picture with Windows text recognition and boxes what it finds as ordinary redaction boxes in
/// one undo step, so each can be checked and removed before saving.</summary>
public partial class MainWindow
{
    internal Func<BitmapSource, Task<IReadOnlyList<IReadOnlyList<OcrWord>>?>> ReadWordsAsync { get; set; } = TextRecognition.ReadWordsAsync;

    private void AutoRedactBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!EditorWorkspaceActive) return;
        var doc = _doc;
        Editor.CommitText();
        var dialog = new RedactDialog(this, () => ScanForRedactionAsync(doc));
        dialog.ShowDialog();
        if (dialog.Added > 0) ShowRedactionResult(dialog.Added);
    }

    /// <summary>Boxes added to the document, or null when Windows cannot read text here. A document
    /// that was replaced while the picture was being read gets nothing.</summary>
    internal async Task<int?> ScanForRedactionAsync(EditorDocument doc)
    {
        var kinds = RedactSettings.Kinds;
        if (kinds == RedactKinds.None) return 0;
        // The picture as it stands, so text under marks already drawn is not read twice.
        var lines = await ReadWordsAsync(doc.State.Image);
        if (lines == null) return null;
        if (!ReferenceEquals(doc, _doc)) return 0;
        int added = RedactBoxes.AddFound(doc, RedactionFinder.Find(lines, kinds));
        if (added > 0) Editor.InvalidateVisual();
        return added;
    }

    /// <summary>Scan every capture: runs once on a new capture as it opens.</summary>
    private async void ScanNewCapture(EditorDocument doc)
    {
        int? added;
        try { added = await ScanForRedactionAsync(doc); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { return; }
        if (added is > 0 && ReferenceEquals(doc, _doc)) ShowRedactionResult(added.Value);
    }

    private void ShowRedactionResult(int added) =>
        StatusText.Text = added == 1 ? Loc("Str_Redact_FoundOne") : string.Format(Loc("Str_Redact_Found"), added);
}
