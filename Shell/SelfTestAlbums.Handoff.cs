using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestAlbums
{
    private static void CheckAlbumHandoff(string original, List<string> report)
    {
        long id = CaptureLibrary.Items().Single().Id;
        string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(original)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string request = "library:v2:" + token + ":" + id;
        string work = CaptureLibrary.CreateAlbum("Other");
        Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Other album must open.");
        long otherId = CaptureLibrary.AddNow(Picture(0x99), DateTime.Now, "region", "Other album", null);
        Check(otherId == id, "The handoff fixture needs matching IDs across albums.");
        App.Instance.OpenImage("library:" + id);
        Check(App.Instance.Document == null, "An ambiguous ID-only handoff opened the wrong album capture.");
        App.Instance.OpenImage(request);
        Check(App.Instance.Document == null, "An album-qualified handoff opened a different album's same-ID capture.");
        Check(CaptureLibrary.TrySwitchAlbum(original, null, out _), "Original album must reopen.");
        App.Instance.OpenImage(request);
        var document = App.Instance.Document;
        Check(document != null && document.PixelWidth == 2, "A matching album-qualified handoff must open its capture.");
        var pixels = new byte[16];
        document!.Image.CopyPixels(pixels, 8, 0);
        Check(pixels[0] == 0x66, "The handoff must open exact original pixels.");
        foreach (string invalid in new[] { "library:v1:any:1", "library:v2:!:1", "library:v2:" + token + ":0", "library:v2:" + token + ":-1" })
        {
            App.Instance.OpenImage(invalid);
            Check(ReferenceEquals(App.Instance.Document, document), "Malformed handoff replaced the current document.");
        }
        App.Instance.SetDocument(null);
        Check(CaptureLibrary.TryAddNowFromCli(Picture(0x77), DateTime.Now, "region", "CLI capture", null,
            out long committedId, out string? committedReference), "CLI capture must commit.");
        Check(committedReference != null && committedReference.StartsWith("library:v2:", StringComparison.Ordinal)
            && CaptureLibrary.OpenAuto() && CaptureLibrary.ResolveReference(committedReference)?.Id == committedId,
            "CLI commit must return a reference to the album used inside its selection lock.");
        string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original)!, "external.png");
        ImageOutput.Save(Picture(0x88), file);
        App.Instance.OpenImage(file);
        Check(App.Instance.Document?.FilePath == file, "Ordinary file requests must continue opening normally.");
        App.Instance.SetDocument(null);
        report.Add("Delayed handoffs reject legacy IDs, mismatched albums and malformed references; matching pixels preserved: PASS");
    }
}
