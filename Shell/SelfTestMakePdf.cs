using System.Diagnostics;
using System.IO;
using System.Windows;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-make-pdf &lt;new folder&gt;: checks the Make PDF handoff without
/// starting KillerPDF. A fake exe stands in for the install, and the start is captured: the
/// folder's own pictures go over as bare names with the folder as the working directory, other
/// files and subfolders stay out, and empty or oversized folders get their message. Opens no window.
/// </summary>
internal static partial class SelfTestMakePdf
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-make-pdf", var target]) return false;
        _ = RunAsync(Path.GetFullPath(target));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int failures = 0;
        void Check(bool pass, string name) { if (!pass) failures++; report.Add((pass ? "PASS " : "FAIL ") + name); }
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("The report folder must be new.");
            string pictures = Path.Combine(folder, "pictures with spaces");
            Directory.CreateDirectory(Path.Combine(pictures, "sub"));
            foreach (var name in new[] { "b 2.png", "a 10.JPG", "c.tiff", "notes.txt", "sub\\deep.png" })
                File.WriteAllBytes(Path.Combine(pictures, name), [0]);
            string exe = Path.Combine(folder, "KillerPDF.exe");
            File.WriteAllBytes(exe, [0]);

            KillerPdfHandoff.FindOverride = () => null;
            Check(!KillerPdfHandoff.IsInstalled && KillerPdfHandoff.MakePdf(pictures) == KillerPdfHandoff.Result.NotInstalled, "no install, no row and a message");

            KillerPdfHandoff.FindOverride = () => exe;
            ProcessStartInfo? started = null;
            KillerPdfHandoff.StartOverride = info => started = info;
            var result = KillerPdfHandoff.MakePdf(pictures);
            var names = started?.ArgumentList.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            report.Add("args: " + string.Join(" | ", names));
            Check(result == KillerPdfHandoff.Result.Started && started?.FileName == exe, "starts the installed KillerPDF");
            Check(names.SequenceEqual(["a 10.JPG", "b 2.png", "c.tiff"]), "passes only the folder's own pictures, as bare names");
            Check(started?.WorkingDirectory == pictures, "working directory is the folder");

            // Picked pictures from the Files view go over in the order the list shows them, not
            // name order, and only the picked ones.
            started = null;
            var picked = KillerPdfHandoff.MakePdf([Path.Combine(pictures, "c.tiff"), Path.Combine(pictures, "a 10.JPG")]);
            var pickedNames = started?.ArgumentList.ToArray() ?? [];
            report.Add("picked: " + string.Join(" | ", pickedNames));
            Check(picked == KillerPdfHandoff.Result.Started && pickedNames.SequenceEqual(["c.tiff", "a 10.JPG"]) && started?.WorkingDirectory == pictures,
                "picked pictures go over in list order, as bare names, from their folder");
            started = null;
            Check(KillerPdfHandoff.MakePdf([Path.Combine(pictures, "notes.txt")]) == KillerPdfHandoff.Result.NoImages && started == null,
                "a pick with no pictures says so");
            Check(KillerPdfHandoff.AllImages([Path.Combine(pictures, "b 2.png"), Path.Combine(pictures, "c.tiff")])
                && !KillerPdfHandoff.AllImages([Path.Combine(pictures, "b 2.png"), Path.Combine(pictures, "notes.txt")])
                && !KillerPdfHandoff.AllImages([]), "the card offers Make PDF only when every picked item is a picture");

            // A card builds on the UI thread, so a network folder is never listed to decide the
            // row: a dead share would hold the menu for the whole SMB timeout. The click checks.
            var probe = Stopwatch.StartNew();
            bool remote = KillerPdfHandoff.OffersMakePdf(@"\\killershot-selftest.invalid\share\pictures");
            probe.Stop();
            report.Add($"remote probe: {remote} {probe.ElapsedMilliseconds} ms");
            Check(remote && probe.ElapsedMilliseconds < 250, "a network folder offers Make PDF without listing it");
            Check(KillerPdfHandoff.OffersMakePdf(pictures) && !KillerPdfHandoff.OffersMakePdf(Path.Combine(folder, "sub-missing")),
                "a local folder offers Make PDF only when it has pictures");

            string empty = Path.Combine(folder, "empty");
            Directory.CreateDirectory(empty);
            Check(KillerPdfHandoff.MakePdf(empty) == KillerPdfHandoff.Result.NoImages, "empty folder says so");

            string pages = Path.Combine(folder, "pages");
            Directory.CreateDirectory(pages);
            foreach (var n in new[] { "page 10.png", "page 2.png", "Page 1.png" }) File.WriteAllBytes(Path.Combine(pages, n), [0]);
            var ordered = KillerPdfHandoff.ImagesIn(pages).Select(Path.GetFileName).ToList();
            Check(ordered.SequenceEqual(["Page 1.png", "page 2.png", "page 10.png"]), "pages follow Explorer's name order, numbers counted as numbers: " + string.Join(", ", ordered));

            string crowd = Path.Combine(folder, "crowd");
            Directory.CreateDirectory(crowd);
            for (int i = 0; i < 1200; i++) File.WriteAllBytes(Path.Combine(crowd, $"capture-{i:D5}-long-name-padding.png"), [0]);
            started = null;
            Check(KillerPdfHandoff.MakePdf(crowd) == KillerPdfHandoff.Result.TooMany && started == null, "oversized folder refuses before starting");

            foreach (var r in Enum.GetValues<KillerPdfHandoff.Result>())
                if (KillerPdfHandoff.MessageKey(r) is { } key)
                    Check(Application.Current.TryFindResource(key) is string { Length: > 0 }, "string " + key);

            // A library collection: the encrypted pictures are decrypted into a numbered temp
            // folder in capture order, and closing the album deletes it.
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not open.");
            var shot = SelfTestAutoRedact.Picture();
            long newer = CaptureLibrary.AddNow(shot, new DateTime(2026, 11, 2), "region", "newer", null);
            long older = CaptureLibrary.AddNow(shot, new DateTime(2026, 11, 1), "region", "older", null);
            CaptureLibrary.AddNow(shot, new DateTime(2026, 11, 3), "region", "outside", null);
            long session = CaptureLibrary.Session;
            long collection = CaptureLibrary.CreateCollection("Pictures", [newer, older], session);
            started = null;
            var made = await KillerPdfHandoff.MakePdfFromCollectionAsync(collection, session);
            string copies = started?.WorkingDirectory ?? "";
            var copied = started?.ArgumentList.ToArray() ?? [];
            report.Add($"collection: {made} {copies} {string.Join(" | ", copied)}");
            Check(made == KillerPdfHandoff.Result.Started && copied.SequenceEqual(["0001.png", "0002.png"]), "collection hands over its two pictures, numbered");
            Check(copies.StartsWith(Path.Combine(folder, "playback"), StringComparison.OrdinalIgnoreCase)
                && File.ReadAllBytes(Path.Combine(copies, "0001.png")).SequenceEqual(CaptureLibrary.LoadOutputPng(older)), "oldest capture comes first, decrypted");
            long emptyCollection = CaptureLibrary.CreateCollection("Empty", null, session);
            Check(await KillerPdfHandoff.MakePdfFromCollectionAsync(emptyCollection, session) == KillerPdfHandoff.Result.NoImages, "empty collection says so");
            Check(await KillerPdfHandoff.MakePdfFromCollectionAsync(collection, session + 1) == KillerPdfHandoff.Result.AlbumChanged, "a stale album session is refused");
            CaptureLibrary.Close();
            for (int i = 0; i < 100 && Directory.Exists(copies); i++) await Task.Delay(20);
            Check(copies.Length > 0 && !Directory.Exists(copies), "closing the album deletes the decrypted copies");
            Directory.CreateDirectory(Path.Combine(folder, "playback", "PDF leftover"));
            VideoCaptures.WipePlayback();
            Check(!Directory.Exists(Path.Combine(folder, "playback", "PDF leftover")), "exit wipe deletes leftover copies");
            await RetirementAsync(folder, Check, report);
            await SameIdRaceAsync(folder, Check, report, hold: false);
            await SameIdRaceAsync(folder, Check, report, hold: true);
        }
        catch (Exception error) { failures++; report.Add(error.ToString()); }
        finally
        {
            KillerPdfHandoff.FindOverride = null;
            KillerPdfHandoff.StartOverride = null;
            KillerPdfHandoff.ExportHook = null;
        }
        report.Add(failures == 0 ? "result=ok" : $"result=error failures={failures}");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        App.Instance.Shutdown(failures == 0 ? 0 : 1);
    }
}
