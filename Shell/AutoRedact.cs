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

    /// <summary>How many times a scan reads the picture again when it keeps changing underneath.</summary>
    private const int RedactionScanAttempts = 3;

    /// <summary>How a scan ended: boxes added, the picture read and holding nothing sensitive, the
    /// picture or document changing so often that the scan gave up, or Windows unable to read text.</summary>
    internal readonly record struct RedactionScan(RedactionOutcome Outcome, int Added = 0);

    internal enum RedactionOutcome { Found, NothingFound, Changed, Unavailable }

    /// <summary>What the scan did. A document that was replaced while the picture was being read
    /// gets nothing and is reported as changed. Words belong to the exact picture they were read
    /// from: when the picture is cropped, turned, resized, undone or marked while Windows reads
    /// it, the scan reads the current picture again instead of placing old positions on the new
    /// one, and gives up with nothing added, reported as changed, if it never holds still.</summary>
    internal async Task<RedactionScan> ScanForRedactionAsync(EditorDocument doc)
    {
        var kinds = RedactSettings.Kinds;
        if (kinds == RedactKinds.None) return new(RedactionOutcome.NothingFound);
        for (int attempt = 0; attempt < RedactionScanAttempts; attempt++)
        {
            var state = doc.State;
            // The picture as it will be saved: inserted pictures and text marks are read, and text
            // already under a redaction is not read twice.
            var lines = await ReadWordsAsync(EditorDocument.Render(state));
            if (lines == null) return new(RedactionOutcome.Unavailable);
            if (!ReferenceEquals(doc, _doc)) return new(RedactionOutcome.Changed);
            if (!ReferenceEquals(state, doc.State)) continue;
            int added = RedactBoxes.AddFound(doc, RedactionFinder.Find(lines, kinds));
            if (added > 0) Editor.InvalidateVisual();
            return added > 0 ? new(RedactionOutcome.Found, added) : new(RedactionOutcome.NothingFound);
        }
        return new(RedactionOutcome.Changed);
    }

    /// <summary>Scan every capture: runs once on a new capture as it opens.</summary>
    private async void ScanNewCapture(EditorDocument doc)
    {
        RedactionScan scan;
        try { scan = await ScanForRedactionAsync(doc); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { return; }
        if (scan.Added > 0 && ReferenceEquals(doc, _doc)) ShowRedactionResult(scan.Added);
    }

    private void ShowRedactionResult(int added) =>
        StatusText.Text = added == 1 ? Loc("Str_Redact_FoundOne") : string.Format(Loc("Str_Redact_Found"), added);
}
