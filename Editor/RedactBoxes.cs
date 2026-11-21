using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Editor;

/// <summary>Redaction boxes in the look chosen in the Redact settings, drawn by hand or found by
/// auto redact.</summary>
internal static class RedactBoxes
{
    public static ShapeAnnotation Box(Point a, Point b, Color penColor, double penWidth)
    {
        var fill = RedactSettings.Fill;
        return new ShapeAnnotation(ShapeKind.Redact, a, b, penColor, penWidth, false,
            fill == Colors.Black ? null : fill, RedactSettings.Feather);
    }

    /// <summary>Adds a box over each area, clipped to the picture, as one undo step. Areas already
    /// inside an existing redaction are skipped. Returns how many boxes were added.</summary>
    public static int AddFound(EditorDocument doc, IEnumerable<Rect> areas)
    {
        var picture = new Rect(0, 0, doc.PixelWidth, doc.PixelHeight);
        var existing = doc.State.Items.OfType<ShapeAnnotation>().Where(s => s.Kind == ShapeKind.Redact).Select(s => s.PixelCells).ToList();
        var items = doc.State.Items.ToBuilder();
        int added = 0;
        foreach (var area in areas)
        {
            var r = Rect.Intersect(area, picture);
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
            if (existing.Any(e => e.Contains(r))) continue;
            items.Add(Box(r.TopLeft, r.BottomRight, Colors.Black, 1));
            existing.Add(r);
            added++;
        }
        if (added > 0) doc.Apply(doc.State with { Items = items.ToImmutable() });
        return added;
    }

    /// <summary>Scan every capture for a new capture, shared by the app and the command line.
    /// Boxes what the scan finds and returns the number of boxes: 0 when it finds nothing or
    /// Windows cannot read text, and -1 when the scan fails. The document is fresh and has no
    /// marks yet, so recognition reads the captured picture itself.</summary>
    public static async Task<int> ScanNewCaptureAsync(EditorDocument doc, BitmapSource picture,
        Func<BitmapSource, Task<IReadOnlyList<IReadOnlyList<OcrWord>>?>> readWords)
    {
        var kinds = RedactSettings.Kinds;
        if (kinds == RedactKinds.None) return 0;
        try
        {
            var lines = await readWords(picture);
            return lines == null ? 0 : AddFound(doc, RedactionFinder.Find(lines, kinds));
        }
        // A scan that fails for any reason lets the original out as before; it never loses the capture.
        catch (Exception error) when (error is not OutOfMemoryException) { return -1; }
    }
}
