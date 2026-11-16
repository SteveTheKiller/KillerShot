using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-redaction-finder &lt;new folder&gt;: checks what auto redact boxes. Each
/// case is one OCR line; the words marked with * must be covered and no other word may be. A
/// second part draws text into a picture and runs it through Windows OCR and the finder, the path
/// the editor takes. Opens no window.
/// </summary>
internal static class SelfTestRedactionFinder
{
    private static readonly (string Line, RedactKinds Kinds)[] Cases =
    [
        ("Contact *bob.smith@example.co.uk for access", RedactKinds.Email),
        ("Card *4111 *1111 *1111 *1111 exp 12/29", RedactKinds.Card),
        ("Card 4111 1111 1111 1112 fails the checksum", RedactKinds.Card),
        ("Order 1234567890123 is not a card", RedactKinds.Card),
        ("Card *4111 *1111 *1111 *1111 *123 with the code after it", RedactKinds.Card),
        ("Row *12 *4111 *1111 *1111 *1111 with a column before it", RedactKinds.Card),
        ("Card *5500-0000-0000-0004-77 with digits run on", RedactKinds.Card),
        ("SSN: *123-45-6789", RedactKinds.Ssn),
        ("Invalid 000-12-3456 and 666-12-3456 and 900-12-3456", RedactKinds.Ssn),
        ("Call *(555) *123-4567 today", RedactKinds.Phone),
        ("Call *+1 *555-123-4567 or *555.987.6543", RedactKinds.Phone),
        ("Office *+44 *20 *7946 *0958 London", RedactKinds.Phone),
        ("Version 1.2.0 shipped on 2026-11-15", RedactKinds.Phone | RedactKinds.Ip | RedactKinds.Card | RedactKinds.Ssn),
        ("Server *192.168.1.20 and gateway *10.0.0.1", RedactKinds.Ip),
        ("Not an address 999.168.1.20", RedactKinds.Ip),
        ("IPv6 *2001:db8:85a3::8a2e:370:7334 is listed", RedactKinds.Ip),
        ("Meeting at 12:30:45 today", RedactKinds.Ip),
        ("password: *hunter2", RedactKinds.Secret),
        ("*API_KEY=AKIAIOSFODNN7EXAMPLE", RedactKinds.Secret),
        ("Key *AKIAIOSFODNN7EXAMPLE alone", RedactKinds.Secret),
        ("token *ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789 leaked", RedactKinds.Secret),
        ("Bearer *eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJlLXZhbHVl", RedactKinds.Secret),
        ("The password field is required", RedactKinds.Secret),
        ("Mail *ann@example.com but card off 4111 1111 1111 1111", RedactKinds.Email),
        ("Phone *(555) *123-4567 Card 4111 1111 1111 1111", RedactKinds.Phone),
    ];

    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-redaction-finder", var folder]) return false;
        _ = RunAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new List<string>();
        int failures = 0;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("The report folder must be new.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));

            foreach (var (line, kinds) in Cases)
            {
                string[] tokens = line.Split(' ');
                var words = new List<OcrWord>();
                var expected = new HashSet<int>();
                for (int i = 0; i < tokens.Length; i++)
                {
                    string text = tokens[i];
                    if (text.StartsWith('*')) { expected.Add(i); text = text[1..]; }
                    words.Add(new OcrWord(text, new Rect(i * 100, 0, 90, 20)));
                }
                var boxes = RedactionFinder.Find([words], kinds);
                var covered = new HashSet<int>();
                for (int i = 0; i < words.Count; i++)
                    if (boxes.Any(b => b.Contains(new Point(words[i].Box.X + 45, 10)))) covered.Add(i);
                bool pass = covered.SetEquals(expected);
                if (!pass) failures++;
                report.Add($"{(pass ? "PASS" : "FAIL")} [{kinds}] {line} -> covered {string.Join(",", covered.Order())}, expected {string.Join(",", expected.Order())}");
            }

            // The real path: rendered text, Windows OCR word boxes, then the finder.
            var image = Render("Email jane.doe@example.com  Phone (555) 123-4567", "IP 172.16.4.2 on the second line");
            var lines = await TextRecognition.ReadWordsAsync(image);
            if (lines == null) report.Add("OCR: no recognition language installed, real-path check skipped");
            else
            {
                var found = RedactionFinder.Find(lines, RedactKinds.Email | RedactKinds.Phone | RedactKinds.Ip);
                var all = lines.SelectMany(l => l).ToList();
                string[] secrets = ["jane.doe@example.com", "(555)", "123-4567", "172.16.4.2"];
                string[] plain = ["Email", "Phone", "IP"];
                bool Covered(OcrWord w) => found.Any(b => b.Contains(new Point(w.Box.X + w.Box.Width / 2, w.Box.Y + w.Box.Height / 2)));
                var missed = all.Where(w => secrets.Contains(w.Text) && !Covered(w)).Select(w => w.Text).ToList();
                var extra = all.Where(w => plain.Contains(w.Text) && Covered(w)).Select(w => w.Text).ToList();
                bool seen = secrets.All(s => all.Any(w => w.Text == s));
                bool pass = seen && missed.Count == 0 && extra.Count == 0;
                if (!pass) failures++;
                report.Add($"{(pass ? "PASS" : "FAIL")} OCR real path: words [{string.Join(" | ", all.Select(w => w.Text))}], {found.Count} boxes, missed [{string.Join(",", missed)}], extra [{string.Join(",", extra)}]");

                // The window test's picture: both lines must come back from OCR. Windows OCR drops
                // a whole Segoe UI line that contains "1111", so the test card avoids that group.
                var window = await TextRecognition.ReadWordsAsync(SelfTestAutoRedact.Picture()) ?? [];
                bool both = window.Count >= 2;
                if (!both) failures++;
                report.Add($"{(both ? "PASS" : "FAIL")} OCR window picture: {string.Join(" / ", window.Select(l => string.Join(" ", l.Select(w => w.Text))))}");
            }
        }
        catch (Exception error) { failures++; report.Add(error.ToString()); }
        finally
        {
            report.Add(failures == 0 ? "result=ok" : $"result=error failures={failures}");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
            Application.Current.Shutdown(failures == 0 ? 0 : 1);
        }
    }

    private static BitmapSource Render(params string[] lines)
    {
        var formatted = lines.Select(text => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 28, Brushes.Black, 1.0)).ToList();
        int w = (int)formatted.Max(f => f.Width) + 40, h = (int)formatted.Sum(f => f.Height * 2) + 40;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            double y = 20;
            foreach (var f in formatted) { dc.DrawText(f, new Point(20, y)); y += f.Height * 2; }
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
