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
internal static class SelfTestMakePdf
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-make-pdf", var target]) return false;
        string folder = Path.GetFullPath(target);
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

            string empty = Path.Combine(folder, "empty");
            Directory.CreateDirectory(empty);
            Check(KillerPdfHandoff.MakePdf(empty) == KillerPdfHandoff.Result.NoImages, "empty folder says so");

            string crowd = Path.Combine(folder, "crowd");
            Directory.CreateDirectory(crowd);
            for (int i = 0; i < 1200; i++) File.WriteAllBytes(Path.Combine(crowd, $"capture-{i:D5}-long-name-padding.png"), [0]);
            started = null;
            Check(KillerPdfHandoff.MakePdf(crowd) == KillerPdfHandoff.Result.TooMany && started == null, "oversized folder refuses before starting");

            foreach (var r in Enum.GetValues<KillerPdfHandoff.Result>())
                if (KillerPdfHandoff.MessageKey(r) is { } key)
                    Check(Application.Current.TryFindResource(key) is string { Length: > 0 }, "string " + key);
        }
        catch (Exception error) { failures++; report.Add(error.ToString()); }
        finally
        {
            KillerPdfHandoff.FindOverride = null;
            KillerPdfHandoff.StartOverride = null;
        }
        report.Add(failures == 0 ? "result=ok" : $"result=error failures={failures}");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        App.Instance.Shutdown(failures == 0 ? 0 : 1);
        return true;
    }
}
